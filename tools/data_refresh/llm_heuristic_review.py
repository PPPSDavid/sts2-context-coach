"""LLM-assisted heuristic review from local run telemetry + current scoring rules."""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import requests

from config import LlmConfig
from models import utc_now_iso


@dataclass
class HeuristicAnalysisResult:
    proposals_path: Path
    report_path: Path
    review_script_path: Path
    proposal_count: int
    llm_used: bool


def run_heuristic_analysis(
    *,
    llm_cfg: LlmConfig,
    project_root: Path,
    output_dir: Path,
    logs_dir: Path,
    runs_limit: int,
) -> HeuristicAnalysisResult:
    output_dir.mkdir(parents=True, exist_ok=True)

    input_bundle = _build_input_bundle(
        project_root=project_root,
        output_dir=output_dir,
        logs_dir=logs_dir,
        runs_limit=runs_limit,
    )

    llm_used = _llm_enabled(llm_cfg)
    proposals: list[dict[str, Any]] = []
    llm_error: str | None = None
    if llm_used:
        try:
            proposals = _request_llm_proposals(llm_cfg, input_bundle)
        except Exception as ex:  # pragma: no cover - defensive
            llm_error = str(ex)
            proposals = []

    doc = {
        "generated_at": utc_now_iso(),
        "llm_used": llm_used and llm_error is None,
        "llm_error": llm_error,
        "source": {
            "logs_dir": str(logs_dir),
            "runs_sampled": input_bundle["telemetry_summary"]["runs_count"],
            "events_sampled": input_bundle["telemetry_summary"]["events_count"],
        },
        "telemetry_summary": input_bundle["telemetry_summary"],
        "proposals": [_with_review_defaults(p, idx) for idx, p in enumerate(proposals, start=1)],
    }

    proposals_path = output_dir / "heuristic_proposals.json"
    report_path = output_dir / "heuristic_proposals.md"
    review_script_path = project_root / "tools" / "data_refresh" / "review_heuristic_proposals.ps1"

    proposals_path.write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
    report_path.write_text(_render_report(doc), encoding="utf-8")
    review_script_path.write_text(_render_review_script(), encoding="utf-8")

    return HeuristicAnalysisResult(
        proposals_path=proposals_path,
        report_path=report_path,
        review_script_path=review_script_path,
        proposal_count=len(doc["proposals"]),
        llm_used=bool(doc["llm_used"]),
    )


def list_proposals(proposals_path: Path) -> list[dict[str, Any]]:
    doc = _read_json_file(proposals_path)
    return list(doc.get("proposals") or [])


def set_proposal_status(
    proposals_path: Path,
    proposal_id: str,
    status: str,
    note: str | None = None,
) -> bool:
    status = status.strip().lower()
    if status not in {"needs_review", "approved", "rejected"}:
        raise ValueError(f"Unsupported status: {status}")
    doc = _read_json_file(proposals_path)
    changed = False
    for p in doc.get("proposals") or []:
        if str(p.get("id")) != proposal_id:
            continue
        p["review_status"] = status
        p["reviewed_at"] = utc_now_iso()
        if note:
            p["review_note"] = note
        changed = True
        break
    if changed:
        proposals_path.write_text(json.dumps(doc, ensure_ascii=False, indent=2), encoding="utf-8")
    return changed


def _build_input_bundle(
    *,
    project_root: Path,
    output_dir: Path,
    logs_dir: Path,
    runs_limit: int,
) -> dict[str, Any]:
    rules = {
        "card_heuristics_cs": _safe_read(project_root / "Scoring" / "CardHeuristics.cs", 24000),
        "recommendation_engine_cs": _safe_read(project_root / "Scoring" / "RecommendationEngine.cs", 50000),
    }
    world = _read_json_file(output_dir / "world.generated.json")
    meta_summary = _read_json_file(output_dir / "metadata_summary.json")
    telemetry_summary = _summarize_runs(logs_dir=logs_dir, runs_limit=runs_limit)

    return {
        "rules": rules,
        "world_metadata_summary": meta_summary,
        "world_snapshot_counts": {
            "acts": len(world.get("acts") or []),
            "events": len(world.get("events") or []),
            "encounters": len(world.get("encounters") or []),
            "monsters": len(world.get("monsters") or []),
        },
        "telemetry_summary": telemetry_summary,
    }


def _summarize_runs(*, logs_dir: Path, runs_limit: int) -> dict[str, Any]:
    run_dirs = [p for p in logs_dir.glob("*/") if p.is_dir()]
    run_dirs.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    run_dirs = run_dirs[: max(0, runs_limit)]

    runs: list[dict[str, Any]] = []
    outcome_counts: dict[str, int] = {}
    decision_type_counts: dict[str, int] = {}
    reason_key_counts: dict[str, int] = {}
    accepted = 0
    accepted_known = 0
    events_total = 0

    for run_dir in run_dirs:
        summary = _read_json_file(run_dir / "summary.json")
        metadata = _read_json_file(run_dir / "metadata.json")
        events = _read_jsonl(run_dir / "events.jsonl", max_lines=2500)
        events_total += len(events)

        outcome = str(summary.get("run_outcome") or "").strip() or "unknown"
        outcome_counts[outcome] = outcome_counts.get(outcome, 0) + 1

        sampled_decisions = 0
        for ev in events:
            ev_type = str(ev.get("event_type") or ev.get("type") or "").strip()
            if "decision" in ev_type:
                sampled_decisions += 1
                decision_type_counts[ev_type] = decision_type_counts.get(ev_type, 0) + 1
            if isinstance(ev.get("accepted_recommendation"), bool):
                accepted_known += 1
                if bool(ev.get("accepted_recommendation")):
                    accepted += 1
            for b in ev.get("score_breakdown") or []:
                if not isinstance(b, dict):
                    continue
                key = str(b.get("key") or "").strip()
                if key:
                    reason_key_counts[key] = reason_key_counts.get(key, 0) + 1

        runs.append(
            {
                "run_id": run_dir.name,
                "character": metadata.get("character") or summary.get("character"),
                "ascension": metadata.get("ascension") or summary.get("ascension"),
                "run_outcome": outcome,
                "decisions_sampled": sampled_decisions,
            }
        )

    top_reasons = sorted(reason_key_counts.items(), key=lambda kv: kv[1], reverse=True)[:25]
    return {
        "runs_count": len(runs),
        "events_count": events_total,
        "runs": runs[:20],
        "run_outcomes": outcome_counts,
        "decision_types": decision_type_counts,
        "top_reason_keys": [{"key": k, "count": v} for k, v in top_reasons],
        "accepted_recommendation_rate": (accepted / accepted_known) if accepted_known else None,
        "accepted_recommendation_observations": accepted_known,
    }


def _request_llm_proposals(llm_cfg: LlmConfig, bundle: dict[str, Any]) -> list[dict[str, Any]]:
    system = (
        "You are reviewing a Slay the Spire 2 scoring engine for safe heuristic tuning. "
        "Return JSON only. Favor small, testable changes. Do not suggest rewrites or speculative mechanics."
    )
    user = json.dumps(
        {
            "task": (
                "Analyze current scoring heuristics + run telemetry. Propose concrete heuristic changes. "
                "Each proposal must include rationale and exact target symbol/file."
            ),
            "constraints": [
                "Prefer tuning constants / thresholds and narrow rule adjustments.",
                "No backend/upload/network changes.",
                "Preserve determinism and low runtime overhead.",
                "Avoid proposing changes without telemetry signal.",
            ],
            "required_output_schema": {
                "proposals": [
                    {
                        "id": "string short id",
                        "title": "string",
                        "target_file": "path",
                        "target_symbol": "symbol or constant name",
                        "change_type": "weight_tuning|threshold_tuning|rule_adjustment|telemetry_instrumentation",
                        "current_behavior": "string",
                        "proposed_change": "string",
                        "expected_effect": "string",
                        "risk": "low|medium|high",
                        "confidence": "0..1 number",
                        "evidence": ["1-3 concise bullets from telemetry"],
                    }
                ]
            },
            "input": bundle,
        },
        ensure_ascii=False,
    )
    data = _chat_json(llm_cfg, system=system, user=user)
    rows = data.get("proposals") if isinstance(data, dict) else None
    if not isinstance(rows, list):
        return []
    out: list[dict[str, Any]] = []
    for row in rows[:20]:
        if isinstance(row, dict):
            out.append(row)
    return out


def _chat_json(llm_cfg: LlmConfig, *, system: str, user: str) -> dict[str, Any]:
    key = os.environ.get(llm_cfg.api_key_env, "").strip()
    if not key:
        raise RuntimeError(f"Missing API key env var: {llm_cfg.api_key_env}")
    base = (llm_cfg.base_url or "https://api.openai.com/v1").rstrip("/")
    headers: dict[str, str] = {"Authorization": f"Bearer {key}", "Content-Type": "application/json"}
    for hk, hv in (llm_cfg.extra_headers or {}).items():
        if hv:
            headers[hk] = hv
    payload = {
        "model": llm_cfg.model,
        "messages": [{"role": "system", "content": system}, {"role": "user", "content": user}],
        "temperature": 0.2,
    }
    resp = requests.post(f"{base}/chat/completions", headers=headers, json=payload, timeout=60)
    resp.raise_for_status()
    text = ((resp.json().get("choices") or [{}])[0].get("message") or {}).get("content") or "{}"
    return _extract_json_payload(str(text))


def _extract_json_payload(text: str) -> dict[str, Any]:
    t = text.strip()
    if t.startswith("```"):
        lines = t.splitlines()
        if len(lines) >= 3 and lines[-1].strip() == "```":
            t = "\n".join(lines[1:-1]).strip()
    parsed = json.loads(t)
    if not isinstance(parsed, dict):
        raise ValueError("Expected JSON object payload")
    return parsed


def _with_review_defaults(proposal: dict[str, Any], index: int) -> dict[str, Any]:
    pid = str(proposal.get("id") or "").strip()
    if not pid:
        pid = f"proposal_{index:03d}"
    return {
        "id": pid,
        "title": str(proposal.get("title") or "").strip(),
        "target_file": str(proposal.get("target_file") or "").strip(),
        "target_symbol": str(proposal.get("target_symbol") or "").strip(),
        "change_type": str(proposal.get("change_type") or "").strip(),
        "current_behavior": str(proposal.get("current_behavior") or "").strip(),
        "proposed_change": str(proposal.get("proposed_change") or "").strip(),
        "expected_effect": str(proposal.get("expected_effect") or "").strip(),
        "risk": str(proposal.get("risk") or "medium").strip().lower(),
        "confidence": _safe_float(proposal.get("confidence"), default=0.5),
        "evidence": [str(x) for x in (proposal.get("evidence") or [])[:5]],
        "review_status": "needs_review",
        "review_note": "",
        "reviewed_at": None,
    }


def _safe_float(value: Any, *, default: float) -> float:
    try:
        v = float(value)
    except (TypeError, ValueError):
        return default
    return max(0.0, min(1.0, v))


def _llm_enabled(cfg: LlmConfig) -> bool:
    return bool(cfg.enabled and os.environ.get(cfg.api_key_env, "").strip())


def _read_json_file(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {}
    try:
        parsed = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    return parsed if isinstance(parsed, dict) else {}


def _read_jsonl(path: Path, max_lines: int) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    out: list[dict[str, Any]] = []
    try:
        with path.open("r", encoding="utf-8", errors="replace") as f:
            for i, line in enumerate(f):
                if i >= max_lines:
                    break
                s = line.strip()
                if not s:
                    continue
                try:
                    row = json.loads(s)
                    if isinstance(row, dict):
                        out.append(row)
                except json.JSONDecodeError:
                    continue
    except OSError:
        return []
    return out


def _safe_read(path: Path, max_chars: int) -> str:
    if not path.exists():
        return ""
    text = path.read_text(encoding="utf-8", errors="replace")
    text = re.sub(r"\r\n?", "\n", text)
    return text[:max_chars]


def _render_report(doc: dict[str, Any]) -> str:
    lines = [
        "# Heuristic Proposals",
        "",
        f"- Generated: {doc.get('generated_at')}",
        f"- LLM used: {doc.get('llm_used')}",
        f"- Runs sampled: {(doc.get('source') or {}).get('runs_sampled', 0)}",
        f"- Events sampled: {(doc.get('source') or {}).get('events_sampled', 0)}",
        "",
        "## Proposals",
        "",
    ]
    proposals = doc.get("proposals") or []
    if not proposals:
        lines.append("No proposals generated.")
        return "\n".join(lines) + "\n"
    for p in proposals:
        lines.extend(
            [
                f"### {p.get('id')} - {p.get('title')}",
                f"- Status: {p.get('review_status')}",
                f"- Target: `{p.get('target_file')}` :: `{p.get('target_symbol')}`",
                f"- Type: `{p.get('change_type')}` | Risk: `{p.get('risk')}` | Confidence: `{p.get('confidence')}`",
                f"- Proposed change: {p.get('proposed_change')}",
                f"- Expected effect: {p.get('expected_effect')}",
                "",
            ]
        )
    return "\n".join(lines) + "\n"


def _render_review_script() -> str:
    return """param(
  [ValidateSet("list","approve","reject")] [string]$Action = "list",
  [string]$Id = "",
  [string]$Note = "",
  [string]$ConfigPath = ""
)

$root = Resolve-Path (Join-Path $PSScriptRoot "..\\..")
$main = Join-Path $root "tools\\data_refresh\\main.py"
$configArgs = @()
if ($ConfigPath) {
  $configArgs = @("--config", $ConfigPath)
}

if ($Action -eq "list") {
  python $main heuristics-list @configArgs
  exit $LASTEXITCODE
}

if (-not $Id) {
  Write-Error "Provide -Id when using approve/reject."
  exit 1
}

$status = if ($Action -eq "approve") { "approved" } else { "rejected" }
python $main heuristics-set --id $Id --status $status --note $Note @configArgs
exit $LASTEXITCODE
"""

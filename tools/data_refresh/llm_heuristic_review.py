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

    # Card/tag enrichment still respects ``llm.enabled``; heuristic review only needs a key.
    llm_used = _heuristic_llm_available(llm_cfg)
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
        "recommendation_engine_cs": _safe_read(
            project_root / "Scoring" / "RecommendationEngine.cs", 50000
        ),
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


def _last_run_finished_status(events: list[dict[str, Any]]) -> str | None:
    """Last ``run_finished`` ``status`` in file order (within the sampled lines)."""
    last: str | None = None
    for ev in events:
        if str(ev.get("event_type") or "").strip() != "run_finished":
            continue
        st = str(ev.get("status") or "").strip()
        if st:
            last = st
    return last


def _resolve_run_outcome(*, summary: dict[str, Any], events: list[dict[str, Any]]) -> str:
    terminal = _last_run_finished_status(events)
    if terminal:
        return terminal
    s = str(summary.get("run_outcome") or "").strip()
    return s if s else "unknown"


def _accumulate_engine_score_reasons(
    events: list[dict[str, Any]], reason_key_counts: dict[str, int]
) -> None:
    """``engine_scores`` rows use ``score_breakdown[].reason`` (legacy flat ``score_breakdown[].key`` supported)."""
    for ev in events:
        if str(ev.get("event_type") or "").strip() != "decision":
            continue
        scores = ev.get("engine_scores")
        if isinstance(scores, dict):
            for _opt, payload in scores.items():
                if not isinstance(payload, dict):
                    continue
                for b in payload.get("score_breakdown") or []:
                    if not isinstance(b, dict):
                        continue
                    key = str(b.get("reason") or b.get("key") or "").strip()
                    if key:
                        reason_key_counts[key] = reason_key_counts.get(key, 0) + 1
            continue
        for b in ev.get("score_breakdown") or []:
            if not isinstance(b, dict):
                continue
            key = str(b.get("reason") or b.get("key") or "").strip()
            if key:
                reason_key_counts[key] = reason_key_counts.get(key, 0) + 1


def _infer_acceptance_from_choices(events: list[dict[str, Any]]) -> tuple[int, int]:
    """
    Join ``decision`` + ``decision_choice`` on ``decision_id`` (reward picks with
    ``deck_diff_inferred``). Returns ``(accepted_count, total_with_recommendation)``.
    """
    decisions: dict[str, dict[str, Any]] = {}
    for ev in events:
        if ev.get("event_type") != "decision":
            continue
        did = ev.get("decision_id")
        if isinstance(did, str):
            decisions[did] = ev

    accepted = 0
    total = 0
    for ev in events:
        if ev.get("event_type") != "decision_choice":
            continue
        did = ev.get("decision_id")
        pick = ev.get("player_choice")
        if not isinstance(did, str) or not isinstance(pick, str) or not pick.strip():
            continue
        parent = decisions.get(did)
        if parent is None:
            continue
        rec = parent.get("recommended_choice")
        if not isinstance(rec, str) or not rec.strip():
            continue
        total += 1
        if pick.strip() == rec.strip():
            accepted += 1
    return accepted, total


def _inline_decision_acceptance(events: list[dict[str, Any]]) -> tuple[int, int]:
    """Counts ``decision`` rows where ``player_choice`` was known at log time."""
    accepted = 0
    known = 0
    for ev in events:
        if ev.get("event_type") != "decision":
            continue
        pc = ev.get("player_choice")
        if not isinstance(pc, str) or not pc.strip():
            continue
        if not isinstance(ev.get("accepted_recommendation"), bool):
            continue
        known += 1
        if bool(ev.get("accepted_recommendation")):
            accepted += 1
    return accepted, known


def _summarize_runs(*, logs_dir: Path, runs_limit: int) -> dict[str, Any]:
    run_dirs = [p for p in logs_dir.glob("*/") if p.is_dir() and (p / "events.jsonl").is_file()]
    run_dirs.sort(key=lambda p: p.stat().st_mtime, reverse=True)
    run_dirs = run_dirs[: max(0, runs_limit)]

    runs: list[dict[str, Any]] = []
    outcome_counts: dict[str, int] = {}
    event_type_counts: dict[str, int] = {}
    reason_key_counts: dict[str, int] = {}
    choice_acc, choice_tot = 0, 0
    inline_acc, inline_tot = 0, 0
    events_total = 0

    for run_dir in run_dirs:
        summary = _read_json_file(run_dir / "summary.json")
        metadata = _read_json_file(run_dir / "metadata.json")
        events = _read_jsonl(run_dir / "events.jsonl", max_lines=2500)
        events_total += len(events)

        outcome = _resolve_run_outcome(summary=summary, events=events)
        outcome_counts[outcome] = outcome_counts.get(outcome, 0) + 1

        sampled_decisions = 0
        for ev in events:
            ev_type = str(ev.get("event_type") or ev.get("type") or "").strip()
            if ev_type:
                event_type_counts[ev_type] = event_type_counts.get(ev_type, 0) + 1
            if ev_type == "decision":
                sampled_decisions += 1

        _accumulate_engine_score_reasons(events, reason_key_counts)
        ca, ct = _infer_acceptance_from_choices(events)
        choice_acc += ca
        choice_tot += ct
        ia, it = _inline_decision_acceptance(events)
        inline_acc += ia
        inline_tot += it

        final_state = summary.get("final_state")
        fs = final_state if isinstance(final_state, dict) else {}
        character = metadata.get("character") or fs.get("character") or summary.get("character")
        ascension = metadata.get("ascension")
        if ascension is None:
            ascension = (
                fs.get("ascension")
                if isinstance(fs.get("ascension"), (int, float))
                else summary.get("ascension")
            )

        runs.append(
            {
                "run_id": run_dir.name,
                "character": character,
                "ascension": ascension,
                "run_outcome": outcome,
                "summary_run_outcome": str(summary.get("run_outcome") or "").strip() or None,
                "decisions_sampled": sampled_decisions,
            }
        )

    if choice_tot > 0:
        acc_rate_obs = choice_tot
        acc_rate = choice_acc / choice_tot
        acc_src = "decision_choice_vs_recommended"
    elif inline_tot > 0:
        acc_rate_obs = inline_tot
        acc_rate = inline_acc / inline_tot
        acc_src = "inline_player_choice"
    else:
        legacy_known = 0
        legacy_acc = 0
        for run_dir in run_dirs:
            events = _read_jsonl(run_dir / "events.jsonl", max_lines=2500)
            for ev in events:
                if ev.get("event_type") != "decision":
                    continue
                if not isinstance(ev.get("accepted_recommendation"), bool):
                    continue
                legacy_known += 1
                if bool(ev.get("accepted_recommendation")):
                    legacy_acc += 1
        acc_rate_obs = legacy_known
        acc_rate = (legacy_acc / legacy_known) if legacy_known else None
        acc_src = "legacy_decision_bool"

    decision_types = {k: v for k, v in event_type_counts.items() if k.startswith("decision")}
    top_reasons = sorted(reason_key_counts.items(), key=lambda kv: kv[1], reverse=True)[:25]
    return {
        "runs_count": len(runs),
        "events_count": events_total,
        "runs": runs[:20],
        "run_outcomes": outcome_counts,
        "event_type_counts": event_type_counts,
        "decision_types": decision_types,
        "top_reason_keys": [{"key": k, "count": v} for k, v in top_reasons],
        "accepted_recommendation_rate": acc_rate,
        "accepted_recommendation_observations": acc_rate_obs,
        "accepted_recommendation_source": acc_src,
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


def _heuristic_llm_available(cfg: LlmConfig) -> bool:
    """True when Chat Completions can be called (API key in env). Independent of ``llm.enabled``."""
    return bool(os.environ.get(cfg.api_key_env, "").strip())


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

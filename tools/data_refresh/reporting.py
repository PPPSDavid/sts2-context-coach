"""refresh_report.md and DeepDiff-based diff JSON."""

from __future__ import annotations

from datetime import UTC, datetime
from pathlib import Path
from typing import Any

from config import GENERATOR_VERSION
from deepdiff import DeepDiff
from io_utils import write_json


def write_diff_json(
    path: Path,
    production: dict[str, Any],
    generated: dict[str, Any],
) -> dict[str, Any]:
    diff = DeepDiff(production, generated, ignore_order=True, verbose_level=2)
    try:
        serial = diff.to_dict()  # type: ignore[union-attr]
    except Exception:
        serial = {"repr": str(diff)}
    out = {
        "schema_version": 1,
        "generated_at": _utc(),
        "generator_version": GENERATOR_VERSION,
        "deepdiff": serial,
        "summary": _summarize_diff(diff),
    }
    write_json(path, out)
    return out


def _summarize_diff(diff: Any) -> dict[str, Any]:
    return {
        "type_changes": len(getattr(diff, "type_changes", {}) or {}),
        "values_changed": len(getattr(diff, "values_changed", {}) or {}),
        "dictionary_item_added": len(getattr(diff, "dictionary_item_added", {}) or {}),
        "dictionary_item_removed": len(getattr(diff, "dictionary_item_removed", {}) or {}),
        "iterable_item_added": len(getattr(diff, "iterable_item_added", {}) or {}),
        "iterable_item_removed": len(getattr(diff, "iterable_item_removed", {}) or {}),
    }


def _utc() -> str:
    return datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ")


def write_refresh_report(
    path: Path,
    fetch_summary: list[dict[str, Any]],
    validation_messages: list[str],
    card_issues: int,
    relic_issues: int,
    review_queue_size: int,
    keywords_glossary: dict[str, Any] | None = None,
) -> None:
    lines = [
        "# Data refresh report",
        "",
        f"- Generated: `{_utc()}`",
        f"- Generator: `{GENERATOR_VERSION}`",
        "",
        "## Fetch",
        "",
    ]
    for f in fetch_summary:
        status = f.get("status", "?")
        url = f.get("url", "")
        cached = f.get("from_cache", False)
        err = f.get("error")
        lines.append(f"- `{status}` {'(cache)' if cached else ''} {url}")
        if err:
            lines.append(f"  - error: {err}")
    lines.extend(
        [
            "",
            "## Validation",
            "",
            f"- Card issues: {card_issues}",
            f"- Relic issues: {relic_issues}",
            "",
        ]
    )
    for m in validation_messages:
        lines.append(f"- {m}")
    lines.extend(
        [
            "",
            "## Review",
            "",
            f"- Items in review queue: **{review_queue_size}**",
            "",
            "See `review_queue.json` and `*.diff.json` under `output/`.",
            "",
        ]
    )
    if keywords_glossary:
        lines.extend(
            [
                "## Keywords glossary (buffs / debuffs / mechanics)",
                "",
                "Mechanical wiki.gg extraction — **no LLM**.",
                "",
                f"- Terms: **{keywords_glossary.get('term_count', 0)}**",
                f"- Detail pages fetched: {keywords_glossary.get('detail_page_urls', 0)}",
                f"- Table fallback rows: {keywords_glossary.get('table_fallback_rows_added', 0)}",
                f"- Detail pages supplemented from index tables: {keywords_glossary.get('detail_page_supplements_applied', 0)}",
                f"- Skipped empty detail pages: {keywords_glossary.get('detail_pages_skipped_empty', 0)}",
                f"- Generated: `{keywords_glossary.get('generated_at', '')}`",
                "",
                "Written to `Data/keywords.json` and `output/keywords.generated.json`.",
                "",
            ]
        )
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")

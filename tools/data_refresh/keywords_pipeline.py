"""Mechanical (non-LLM) wiki.gg → keywords.json for the mod LLM glossary."""

from __future__ import annotations

from typing import Any

from config import AppConfig
from io_utils import write_json
from models import utc_now_iso
from parsers.keywords_parser import (
    discover_keyword_page_urls,
    merge_index_tables_into_keywords,
    parse_keyword_page,
)
from sources.base import CachedFetcher


def run_keywords_refresh(
    cfg: AppConfig,
    fetcher: CachedFetcher,
    *,
    force: bool,
    write_production: bool,
) -> dict[str, Any]:
    """
    Discover/fetch STS2 Debuffs/Buffs pages + optional extra URLs; merge detail pages with
    table Description fallbacks. Writes output/keywords.generated.json; optionally Data/keywords.json.
    """
    paths = cfg.paths
    ordered_urls: list[str] = []
    seen: set[str] = set()
    index_html: list[tuple[str, str]] = []

    for idx_url in cfg.sources.wiki_keyword_index_pages:
        content = fetcher.get_text(idx_url, force=force)
        index_html.append((idx_url, content.text))
        for u in discover_keyword_page_urls(content.text, idx_url):
            if u not in seen:
                seen.add(u)
                ordered_urls.append(u)

    for u in cfg.sources.wiki_keyword_pages:
        p = str(u).strip()
        if not p:
            continue
        if p not in seen:
            seen.add(p)
            ordered_urls.append(p)

    by_term: dict[str, dict[str, str]] = {}
    skipped: list[str] = []
    for u in ordered_urls:
        content = fetcher.get_text(u, force=force)
        row = parse_keyword_page(content.text, u, content.fetched_at)
        if not row:
            skipped.append(u)
            continue
        key = row["term"].strip().lower()
        by_term[key] = {"term": row["term"].strip(), "definition": row["definition"].strip()}

    fb, sup = merge_index_tables_into_keywords(index_html, by_term)

    keywords = sorted(by_term.values(), key=lambda x: x["term"].lower())
    generated_at = utc_now_iso()
    doc = {"schema_version": 1, "generated_at": generated_at, "keywords": keywords}

    paths.output_dir.mkdir(parents=True, exist_ok=True)
    write_json(paths.output_dir / "keywords.generated.json", doc)
    if write_production:
        paths.data_dir.mkdir(parents=True, exist_ok=True)
        write_json(paths.data_dir / "keywords.json", {"schema_version": 1, "keywords": keywords})

    return {
        "generated_at": generated_at,
        "term_count": len(keywords),
        "detail_page_urls": len(ordered_urls),
        "detail_pages_skipped_empty": len(skipped),
        "table_fallback_rows_added": fb,
        "detail_page_supplements_applied": sup,
        "skipped_urls": skipped,
    }

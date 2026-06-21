#!/usr/bin/env python3
"""CLI for offline STS2 metadata refresh (separate from the game mod runtime)."""

from __future__ import annotations

import re
import sys
from pathlib import Path

import typer

# Ensure imports resolve when executed as `python main.py`
_ROOT = Path(__file__).resolve().parent
if str(_ROOT) not in sys.path:
    sys.path.insert(0, str(_ROOT))

from config import load_config  # noqa: E402
from io_utils import create_backup, ensure_dirs, list_backups, read_json, write_json  # noqa: E402
from llm_enrichment import build_enricher  # noqa: E402
from llm_heuristic_review import (  # noqa: E402
    list_proposals as list_heuristic_proposals,
    run_heuristic_analysis,
    set_proposal_status as set_heuristic_proposal_status,
)
from merge import build_patch_maps, merge_cards, merge_relics  # noqa: E402
from models import (  # noqa: E402
    PatchNoteEntry,
    RawCardRecord,
    RawEncounterRecord,
    RawEventRecord,
    RawMonsterRecord,
    RawRelicRecord,
    ReviewQueueFile,
    utc_now_iso,
)
from parsers.cards_parser import enrich_cards_with_detail_pages, parse_cards_from_wiki_html  # noqa: E402
from parsers.patch_parser import extract_patch_entities_heuristic, parse_steam_rss  # noqa: E402
from parsers.relics_parser import parse_relics_from_wiki_html  # noqa: E402
from keywords_pipeline import run_keywords_refresh  # noqa: E402
from parsers.world_parser import (  # noqa: E402
    build_wiki_parse_api_url,
    enrich_encounters_with_detail_pages,
    enrich_events_with_detail_pages,
    enrich_monsters_with_detail_pages,
    extract_wikitext_from_parse_api_response,
    extract_act_urls_from_acts_wikitext,
    extract_act_urls_from_acts_page,
    parse_act_page,
    parse_act_wikitext,
)
from reporting import write_diff_json, write_refresh_report  # noqa: E402
from review import apply_approved, approve, list_pending, note, reject  # noqa: E402
from sources.base import CachedFetcher  # noqa: E402
from sources.community_guides import CommunityGuidesSource  # noqa: E402
from sources.steam_patch_notes import SteamPatchSource  # noqa: E402
from sources.wiki_gg import WikiGgSource  # noqa: E402
from validation import validate_cards_document, validate_relics_document  # noqa: E402

app = typer.Typer(add_completion=False, help="Slay the Spire 2 metadata refresh (offline tool)")


def _dedupe_records_by_internal_name(records: list[object], kind: str) -> list[object]:
    """Keep first occurrence for each internal_name and report dropped duplicates."""
    out: list[object] = []
    seen: set[str] = set()
    dropped = 0
    for rec in records:
        iid = str(getattr(rec, "internal_name", "") or "").strip()
        if not iid:
            out.append(rec)
            continue
        if iid in seen:
            dropped += 1
            continue
        seen.add(iid)
        out.append(rec)
    if dropped:
        typer.echo(f"Deduped {dropped} duplicate {kind} record(s) by internal_name.")
    return out


def _dedupe_dict_entries(entries: list[dict], kind: str) -> list[dict]:
    """Keep first entry for each internal_name; helps sanitize production/generation artifacts."""
    out: list[dict] = []
    seen: set[str] = set()
    dropped = 0
    for entry in entries:
        iid = str(entry.get("internal_name") or "").strip()
        if not iid:
            out.append(entry)
            continue
        if iid in seen:
            dropped += 1
            continue
        seen.add(iid)
        out.append(entry)
    if dropped:
        typer.echo(f"Deduped {dropped} duplicate {kind} JSON row(s) by internal_name.")
    return out


def _cfg(config: Path | None) -> object:
    return load_config(config)


def _keywords_glossary_summary(kw_stats: dict) -> dict:
    """Small JSON-safe summary for parsed_raw / metadata_summary / refresh_report."""
    return {
        "source": "wiki_gg_mechanical",
        "generated_at": kw_stats.get("generated_at", ""),
        "term_count": kw_stats.get("term_count", 0),
        "detail_page_urls": kw_stats.get("detail_page_urls", 0),
        "detail_pages_skipped_empty": kw_stats.get("detail_pages_skipped_empty", 0),
        "table_fallback_rows_added": kw_stats.get("table_fallback_rows_added", 0),
        "detail_page_supplements_applied": kw_stats.get("detail_page_supplements_applied", 0),
    }


def _build_metadata_summary(
    cards: list[RawCardRecord],
    relics: list[RawRelicRecord],
    acts: list[dict],
    events: list[dict],
    encounters: list[dict],
    monsters: list[dict],
) -> dict:
    by_act_events: dict[str, int] = {}
    by_act_encounters: dict[str, int] = {}
    by_act_monsters: dict[str, int] = {}
    for e in events:
        act = str(e.get("act_internal_name") or "Unknown")
        by_act_events[act] = by_act_events.get(act, 0) + 1
    for e in encounters:
        act = str(e.get("act_internal_name") or "Unknown")
        by_act_encounters[act] = by_act_encounters.get(act, 0) + 1
    elite_count = sum(1 for e in encounters if str(e.get("encounter_type") or "normal") == "elite")
    boss_count = sum(1 for e in encounters if str(e.get("encounter_type") or "normal") == "boss")
    normal_encounter_count = sum(1 for e in encounters if str(e.get("encounter_type") or "normal") == "normal")
    monsters_with_skills = 0
    total_monster_skills = 0
    events_with_description = sum(1 for e in events if str(e.get("raw_description") or "").strip())
    encounters_with_description = sum(1 for e in encounters if str(e.get("raw_description") or "").strip())
    monsters_with_description = sum(1 for m in monsters if str(m.get("raw_description") or "").strip())
    for m in monsters:
        skills = m.get("skills") or []
        if skills:
            monsters_with_skills += 1
            total_monster_skills += len(skills)
        act = str(m.get("act_internal_name") or "Unknown")
        by_act_monsters[act] = by_act_monsters.get(act, 0) + 1

    return {
        "generated_at": utc_now_iso(),
        "counts": {
            "cards": len(cards),
            "relics": len(relics),
            "acts": len(acts),
            "events": len(events),
            "encounters": len(encounters),
            "normal_encounters": normal_encounter_count,
            "elites": elite_count,
            "bosses": boss_count,
            "monsters": len(monsters),
            "events_with_description": events_with_description,
            "encounters_with_description": encounters_with_description,
            "monsters_with_description": monsters_with_description,
            "monsters_with_skills": monsters_with_skills,
            "total_monster_skills": total_monster_skills,
        },
        "coverage_by_act": {
            "events": by_act_events,
            "encounters": by_act_encounters,
            "monsters": by_act_monsters,
        },
        "act_names": [str(a.get("name") or "") for a in acts if a.get("name")],
    }


def _name_from_wiki_url(url: str) -> str:
    if "Slay_the_Spire_2:" in url:
        slug = url.split("Slay_the_Spire_2:", 1)[-1]
    else:
        slug = url
    slug = slug.split("#", 1)[0].split("?", 1)[0]
    slug = slug.replace("_", " ").strip()
    return slug or url


def _annotate_shared_across_acts(acts: list[object], events: list[object], encounters: list[object], monsters: list[object]) -> None:
    # Build inverse index from per-act name sets.
    event_index: dict[str, set[str]] = {}
    encounter_index: dict[str, set[str]] = {}
    monster_index: dict[str, set[str]] = {}
    for act in acts:
        act_name = str(getattr(act, "internal_name", None) or getattr(act, "name", "Unknown"))
        for n in getattr(act, "event_names", []) or []:
            event_index.setdefault(str(n).strip().lower(), set()).add(act_name)
        for n in getattr(act, "encounter_names", []) or []:
            encounter_index.setdefault(str(n).strip().lower(), set()).add(act_name)
        for n in getattr(act, "elite_names", []) or []:
            encounter_index.setdefault(str(n).strip().lower(), set()).add(act_name)
        for n in getattr(act, "boss_names", []) or []:
            encounter_index.setdefault(str(n).strip().lower(), set()).add(act_name)
        for n in getattr(act, "monster_names", []) or []:
            monster_index.setdefault(str(n).strip().lower(), set()).add(act_name)

    for e in events:
        key = str(getattr(e, "name", "")).strip().lower()
        e.shared_across_acts = sorted(event_index.get(key, set()))
    for e in encounters:
        key = str(getattr(e, "name", "")).strip().lower()
        e.shared_across_acts = sorted(encounter_index.get(key, set()))
    for m in monsters:
        key = str(getattr(m, "name", "")).strip().lower()
        m.shared_across_acts = sorted(monster_index.get(key, set()))


@app.command()
def fetch(
    config: Path | None = typer.Option(None, "--config", help="Path to config.yaml"),
    force: bool = typer.Option(False, "--force", help="Bypass cache TTL"),
) -> None:
    """Download remote sources into cache."""

    cfg = _cfg(config)
    paths = cfg.paths
    ensure_dirs(paths.output_dir, paths.cache_dir, paths.backups_dir)
    fetcher = CachedFetcher(paths.cache_dir, cfg.fetch)
    wiki = WikiGgSource(fetcher, cfg.sources)
    steam = SteamPatchSource(fetcher, cfg.sources)
    comm = CommunityGuidesSource(fetcher, guide_urls=[])

    manifest: list[dict[str, str | bool | None]] = []
    for url, cc in {**wiki.fetch_all(force=force), **steam.fetch_all(force=force), **comm.fetch_all(force=force)}.items():
        manifest.append(
            {
                "url": url,
                "fetched_at": cc.fetched_at,
                "from_cache": cc.from_cache,
                "status": cc.status,
                "error": cc.error,
            }
        )
    write_json(paths.output_dir / "fetch_manifest.json", {"generated_at": utc_now_iso(), "entries": manifest})
    typer.echo(f"Fetch complete. {len(manifest)} URLs. Manifest: {paths.output_dir / 'fetch_manifest.json'}")


@app.command()
def parse(
    config: Path | None = typer.Option(None, "--config"),
    force: bool = typer.Option(False, "--force"),
) -> None:
    """Fetch (cache) + parse into intermediate JSON (cards/relics/world + mechanical keywords glossary)."""

    cfg = _cfg(config)
    paths = cfg.paths
    ensure_dirs(paths.output_dir, paths.cache_dir)
    fetcher = CachedFetcher(paths.cache_dir, cfg.fetch)
    wiki = WikiGgSource(fetcher, cfg.sources)

    cards_html = fetcher.get_text(cfg.sources.wiki_cards_list, force=force)
    relics_html = fetcher.get_text(cfg.sources.wiki_relics_list, force=force)

    raw_cards = parse_cards_from_wiki_html(cards_html.text, cards_html.url, cards_html.fetched_at)
    raw_cards = enrich_cards_with_detail_pages(raw_cards, fetcher, max_fetch=300, force=force)
    raw_relics = parse_relics_from_wiki_html(relics_html.text, relics_html.url, relics_html.fetched_at)
    raw_cards = _dedupe_records_by_internal_name(raw_cards, "card")
    raw_relics = _dedupe_records_by_internal_name(raw_relics, "relic")

    acts_html = fetcher.get_text(cfg.sources.wiki_acts_list, force=force)
    acts_wiki = fetcher.get_text(build_wiki_parse_api_url("Slay_the_Spire_2:Acts"), force=force)
    act_urls = list(
        dict.fromkeys(
            cfg.sources.wiki_act_pages
            + extract_act_urls_from_acts_page(acts_html.text)
            + extract_act_urls_from_acts_wikitext(extract_wikitext_from_parse_api_response(acts_wiki.text))
        )
    )
    acts = []
    events = []
    encounters = []
    monsters = []
    for u in act_urls[:12]:
        page = fetcher.get_text(u, force=force)
        page_title = u.split("/wiki/", 1)[-1]
        wiki_json = fetcher.get_text(build_wiki_parse_api_url(page_title), force=force)
        wikitext = extract_wikitext_from_parse_api_response(wiki_json.text)
        act, evs, encs, mons = parse_act_wikitext(wikitext, page.url, page.fetched_at)
        if not evs and not encs and not mons:
            act, evs, encs, mons = parse_act_page(page.text, page.url, page.fetched_at)
        acts.append(act)
        events.extend(evs)
        encounters.extend(encs)
        monsters.extend(mons)

    for u in cfg.sources.wiki_monster_pages:
        page = fetcher.get_text(u, force=force)
        name = _name_from_wiki_url(u)
        monsters.append(
            RawMonsterRecord(
                name=name,
                internal_name=re.sub(r"[^a-zA-Z0-9]+", "", name) or "UnknownMonster",
                source_url=page.url,
                source_fetched_at=page.fetched_at,
            )
        )
    for u in cfg.sources.wiki_event_pages:
        page = fetcher.get_text(u, force=force)
        name = _name_from_wiki_url(u)
        events.append(
            RawEventRecord(
                name=name,
                internal_name=re.sub(r"[^a-zA-Z0-9]+", "", name) or "UnknownEvent",
                source_url=page.url,
                source_fetched_at=page.fetched_at,
            )
        )
    for u in cfg.sources.wiki_encounter_pages:
        page = fetcher.get_text(u, force=force)
        name = _name_from_wiki_url(u)
        encounters.append(
            RawEncounterRecord(
                name=name,
                internal_name=re.sub(r"[^a-zA-Z0-9]+", "", name) or "UnknownEncounter",
                source_url=page.url,
                source_fetched_at=page.fetched_at,
            )
        )
    acts = _dedupe_records_by_internal_name(acts, "act")
    events = _dedupe_records_by_internal_name(events, "event")
    encounters = _dedupe_records_by_internal_name(encounters, "encounter")
    monsters = _dedupe_records_by_internal_name(monsters, "monster")
    _annotate_shared_across_acts(acts, events, encounters, monsters)
    events = enrich_events_with_detail_pages(events, fetcher, force=force)
    encounters = enrich_encounters_with_detail_pages(encounters, fetcher, force=force)
    monsters = enrich_monsters_with_detail_pages(monsters, fetcher, force=force)

    kw_stats = run_keywords_refresh(cfg, fetcher, force=force, write_production=True)
    kw_sum = _keywords_glossary_summary(kw_stats)

    write_json(
        paths.output_dir / "parsed_raw.json",
        {
            "generated_at": utc_now_iso(),
            "cards": [c.model_dump() for c in raw_cards],
            "relics": [r.model_dump() for r in raw_relics],
            "acts": [a.model_dump() for a in acts],
            "events": [e.model_dump() for e in events],
            "encounters": [e.model_dump() for e in encounters],
            "monsters": [m.model_dump() for m in monsters],
            "keywords_glossary": kw_sum,
        },
    )
    summary = _build_metadata_summary(
        cards=raw_cards,
        relics=raw_relics,
        acts=[a.model_dump() for a in acts],
        events=[e.model_dump() for e in events],
        encounters=[e.model_dump() for e in encounters],
        monsters=[m.model_dump() for m in monsters],
    )
    summary["keywords_glossary"] = kw_sum
    write_json(paths.output_dir / "metadata_summary.json", summary)
    typer.echo(
        "Parsed "
        f"{len(raw_cards)} cards, {len(raw_relics)} relics, "
        f"{len(acts)} acts, {len(events)} events, {len(encounters)} encounters, {len(monsters)} monsters "
        f"-> output/parsed_raw.json | keywords glossary: {kw_sum['term_count']} terms (mechanical) -> Data/keywords.json"
    )


@app.command()
def enrich(
    config: Path | None = typer.Option(None, "--config"),
) -> None:
    """Run LLM enrichment (no-op if API key missing)."""

    cfg = _cfg(config)
    paths = cfg.paths
    parsed = read_json(paths.output_dir / "parsed_raw.json")
    if not parsed:
        typer.echo("No parsed_raw.json — run parse first.", err=True)
        raise typer.Exit(1)
    cards = [RawCardRecord.model_validate(x) for x in parsed.get("cards", [])]
    relics = [RawRelicRecord.model_validate(x) for x in parsed.get("relics", [])]
    enricher = build_enricher(cfg.llm)
    ce = enricher.enrich_cards(cards)
    re = enricher.enrich_relics(relics)
    write_json(
        paths.output_dir / "llm_proposals.json",
        {
            "generated_at": utc_now_iso(),
            "cards": {k: v.model_dump() for k, v in ce.items()},
            "relics": {k: v.model_dump() for k, v in re.items()},
        },
    )
    typer.echo(f"LLM proposals written ({len(ce)} cards, {len(relics)} relic slots checked).")


@app.command("heuristics-analyze")
def heuristics_analyze(
    config: Path | None = typer.Option(None, "--config"),
    logs_dir: Path = typer.Option(Path("logs"), "--logs-dir", help="Directory containing run log folders"),
    runs_limit: int = typer.Option(40, "--runs-limit", min=1, max=200),
) -> None:
    """Use LLM to propose scoring heuristic changes from run telemetry."""

    cfg = _cfg(config)
    project_root = Path(cfg.paths.project_root)
    resolved_logs = logs_dir if logs_dir.is_absolute() else project_root / logs_dir
    result = run_heuristic_analysis(
        llm_cfg=cfg.llm,
        project_root=project_root,
        output_dir=cfg.paths.output_dir,
        logs_dir=resolved_logs,
        runs_limit=runs_limit,
    )
    typer.echo(
        "Heuristic analysis complete. "
        f"proposals={result.proposal_count} llm_used={result.llm_used} "
        f"json={result.proposals_path} report={result.report_path} "
        f"review_script={result.review_script_path}"
    )


@app.command("heuristics-list")
def heuristics_list(config: Path | None = typer.Option(None, "--config")) -> None:
    """List LLM heuristic proposals and review status."""

    cfg = _cfg(config)
    proposals_path = cfg.paths.output_dir / "heuristic_proposals.json"
    rows = list_heuristic_proposals(proposals_path)
    if not rows:
        typer.echo("No heuristic proposals found. Run `python main.py heuristics-analyze` first.")
        return
    for p in rows:
        typer.echo(
            f"{p.get('id')} [{p.get('review_status')}] "
            f"{p.get('title')} | risk={p.get('risk')} conf={p.get('confidence')}"
        )


@app.command("keywords-refresh")
def keywords_refresh_cmd(
    config: Path | None = typer.Option(None, "--config"),
    force: bool = typer.Option(False, "--force", "-f", help="Bypass HTTP cache TTL"),
) -> None:
    """Fetch STS2 wiki keyword/status pages (mechanical only) and write Data/keywords.json + output/."""
    cfg = _cfg(config)
    ensure_dirs(cfg.paths.output_dir, cfg.paths.cache_dir)
    fetcher = CachedFetcher(cfg.paths.cache_dir, cfg.fetch)
    for idx_url in cfg.sources.wiki_keyword_index_pages:
        typer.echo(f"Discovering links from {idx_url}")
    stats = run_keywords_refresh(cfg, fetcher, force=force, write_production=True)
    typer.echo(
        f"Wrote {stats['term_count']} keyword row(s) -> {cfg.paths.data_dir / 'keywords.json'} "
        f"and {cfg.paths.output_dir / 'keywords.generated.json'}"
    )
    if stats.get("table_fallback_rows_added"):
        typer.echo(f"Table fallback rows added: {stats['table_fallback_rows_added']}")
    if stats.get("detail_page_supplements_applied"):
        typer.echo(f"Detail pages supplemented from table: {stats['detail_page_supplements_applied']}")
    if stats.get("detail_pages_skipped_empty"):
        typer.echo(f"Skipped {stats['detail_pages_skipped_empty']} detail URL(s) with empty lead section.")


@app.command("heuristics-set")
def heuristics_set(
    id: str = typer.Option(..., "--id", help="Proposal id"),
    status: str = typer.Option(..., "--status", help="needs_review|approved|rejected"),
    note: str = typer.Option("", "--note"),
    config: Path | None = typer.Option(None, "--config"),
) -> None:
    """Update review status for one heuristic proposal."""

    cfg = _cfg(config)
    proposals_path = cfg.paths.output_dir / "heuristic_proposals.json"
    ok = set_heuristic_proposal_status(
        proposals_path=proposals_path,
        proposal_id=id,
        status=status,
        note=note or None,
    )
    if not ok:
        typer.echo(f"Proposal id not found: {id}", err=True)
        raise typer.Exit(1)
    typer.echo("Updated.")


@app.command()
def diff(
    config: Path | None = typer.Option(None, "--config"),
) -> None:
    """Write diff JSON comparing production vs generated (if generated exists)."""

    cfg = _cfg(config)
    paths = cfg.paths
    prod_c = read_json(paths.cards_production) or {}
    prod_r = read_json(paths.relics_production) or {}
    gen_c = read_json(paths.output_dir / "cards.generated.json")
    gen_r = read_json(paths.output_dir / "relics.generated.json")
    gen_kw = read_json(paths.output_dir / "keywords.generated.json")
    prod_kw = read_json(paths.data_dir / "keywords.json") or {"schema_version": 1, "keywords": []}
    if gen_c:
        write_diff_json(paths.output_dir / "cards.diff.json", prod_c, gen_c)
    if gen_r:
        write_diff_json(paths.output_dir / "relics.diff.json", prod_r, gen_r)
    if gen_kw:
        write_diff_json(paths.output_dir / "keywords.diff.json", prod_kw, gen_kw)
    typer.echo("Diff files updated (if generated JSON exists).")


@app.command()
def validate(
    config: Path | None = typer.Option(None, "--config"),
) -> None:
    """Validate generated JSON files."""

    cfg = _cfg(config)
    paths = cfg.paths
    gen_c = read_json(paths.output_dir / "cards.generated.json")
    gen_r = read_json(paths.output_dir / "relics.generated.json")
    if not gen_c and not gen_r:
        typer.echo("No generated JSON in output/ — run refresh first.", err=True)
        raise typer.Exit(1)
    issues_c = validate_cards_document(gen_c) if gen_c else []
    issues_r = validate_relics_document(gen_r) if gen_r else []
    for i in issues_c + issues_r:
        typer.echo(f"[{i.level}] {i.message}" + (f" — {i.detail}" if i.detail else ""))
    err = sum(1 for i in issues_c + issues_r if i.level == "error")
    raise typer.Exit(1 if err else 0)


@app.command("refresh")
def refresh_cmd(
    config: Path | None = typer.Option(None, "--config"),
    safe: bool = typer.Option(False, "--safe/--no-safe", help="Use safe review-gated merge mode"),
    force: bool = typer.Option(False, "--force"),
) -> None:
    """Run full pipeline: fetch, parse, merge, optional LLM for cards/relics only, mechanical keywords, report."""

    cfg = _cfg(config)
    if safe:
        cfg.merge_mode = "safe"
    else:
        cfg.merge_mode = "overwrite"
    paths = cfg.paths
    ensure_dirs(paths.output_dir, paths.cache_dir, paths.backups_dir)

    fetcher = CachedFetcher(paths.cache_dir, cfg.fetch)
    wiki = WikiGgSource(fetcher, cfg.sources)
    steam = SteamPatchSource(fetcher, cfg.sources)

    wiki_pages = wiki.fetch_all(force=force)
    steam_pages = steam.fetch_all(force=force)

    fetch_summary = [
        {"url": u, "status": cc.status, "from_cache": cc.from_cache, "error": cc.error}
        for u, cc in {**wiki_pages, **steam_pages}.items()
    ]

    cards_html = fetcher.get_text(cfg.sources.wiki_cards_list, force=force)
    relics_html = fetcher.get_text(cfg.sources.wiki_relics_list, force=force)

    raw_cards = parse_cards_from_wiki_html(cards_html.text, cards_html.url, cards_html.fetched_at)
    raw_cards = enrich_cards_with_detail_pages(raw_cards, fetcher, max_fetch=300, force=force)
    raw_relics = parse_relics_from_wiki_html(relics_html.text, relics_html.url, relics_html.fetched_at)
    raw_cards = _dedupe_records_by_internal_name(raw_cards, "card")
    raw_relics = _dedupe_records_by_internal_name(raw_relics, "relic")

    acts_html = fetcher.get_text(cfg.sources.wiki_acts_list, force=force)
    acts_wiki = fetcher.get_text(build_wiki_parse_api_url("Slay_the_Spire_2:Acts"), force=force)
    act_urls = list(
        dict.fromkeys(
            cfg.sources.wiki_act_pages
            + extract_act_urls_from_acts_page(acts_html.text)
            + extract_act_urls_from_acts_wikitext(extract_wikitext_from_parse_api_response(acts_wiki.text))
        )
    )
    acts = []
    events = []
    encounters = []
    monsters = []
    for u in act_urls[:12]:
        page = fetcher.get_text(u, force=force)
        page_title = u.split("/wiki/", 1)[-1]
        wiki_json = fetcher.get_text(build_wiki_parse_api_url(page_title), force=force)
        wikitext = extract_wikitext_from_parse_api_response(wiki_json.text)
        act, evs, encs, mons = parse_act_wikitext(wikitext, page.url, page.fetched_at)
        if not evs and not encs and not mons:
            act, evs, encs, mons = parse_act_page(page.text, page.url, page.fetched_at)
        acts.append(act)
        events.extend(evs)
        encounters.extend(encs)
        monsters.extend(mons)

    for u in cfg.sources.wiki_monster_pages:
        page = fetcher.get_text(u, force=force)
        name = _name_from_wiki_url(u)
        monsters.append(
            RawMonsterRecord(
                name=name,
                internal_name=re.sub(r"[^a-zA-Z0-9]+", "", name) or "UnknownMonster",
                source_url=page.url,
                source_fetched_at=page.fetched_at,
            )
        )
    for u in cfg.sources.wiki_event_pages:
        page = fetcher.get_text(u, force=force)
        name = _name_from_wiki_url(u)
        events.append(
            RawEventRecord(
                name=name,
                internal_name=re.sub(r"[^a-zA-Z0-9]+", "", name) or "UnknownEvent",
                source_url=page.url,
                source_fetched_at=page.fetched_at,
            )
        )
    for u in cfg.sources.wiki_encounter_pages:
        page = fetcher.get_text(u, force=force)
        name = _name_from_wiki_url(u)
        encounters.append(
            RawEncounterRecord(
                name=name,
                internal_name=re.sub(r"[^a-zA-Z0-9]+", "", name) or "UnknownEncounter",
                source_url=page.url,
                source_fetched_at=page.fetched_at,
            )
        )
    acts = _dedupe_records_by_internal_name(acts, "act")
    events = _dedupe_records_by_internal_name(events, "event")
    encounters = _dedupe_records_by_internal_name(encounters, "encounter")
    monsters = _dedupe_records_by_internal_name(monsters, "monster")
    _annotate_shared_across_acts(acts, events, encounters, monsters)
    events = enrich_events_with_detail_pages(events, fetcher, force=force)
    encounters = enrich_encounters_with_detail_pages(encounters, fetcher, force=force)
    monsters = enrich_monsters_with_detail_pages(monsters, fetcher, force=force)

    prod_kw_snapshot = read_json(paths.data_dir / "keywords.json") or {"schema_version": 1, "keywords": []}
    kw_stats = run_keywords_refresh(cfg, fetcher, force=force, write_production=True)
    kw_sum = _keywords_glossary_summary(kw_stats)
    gen_kw_doc = read_json(paths.output_dir / "keywords.generated.json") or {}
    if gen_kw_doc:
        write_diff_json(paths.output_dir / "keywords.diff.json", prod_kw_snapshot, gen_kw_doc)

    write_json(
        paths.output_dir / "parsed_raw.json",
        {
            "generated_at": utc_now_iso(),
            "cards": [c.model_dump() for c in raw_cards],
            "relics": [r.model_dump() for r in raw_relics],
            "acts": [a.model_dump() for a in acts],
            "events": [e.model_dump() for e in events],
            "encounters": [e.model_dump() for e in encounters],
            "monsters": [m.model_dump() for m in monsters],
            "keywords_glossary": kw_sum,
        },
    )
    write_json(
        paths.output_dir / "world.generated.json",
        {
            "schema_version": 1,
            "generated_at": utc_now_iso(),
            "acts": [a.model_dump() for a in acts],
            "events": [e.model_dump() for e in events],
            "encounters": [e.model_dump() for e in encounters],
            "monsters": [m.model_dump() for m in monsters],
        },
    )
    summary = _build_metadata_summary(
        cards=raw_cards,
        relics=raw_relics,
        acts=[a.model_dump() for a in acts],
        events=[e.model_dump() for e in events],
        encounters=[e.model_dump() for e in encounters],
        monsters=[m.model_dump() for m in monsters],
    )
    summary["keywords_glossary"] = kw_sum
    write_json(paths.output_dir / "metadata_summary.json", summary)

    prod_cards_doc = read_json(paths.cards_production) or {"schema_version": 1, "cards": []}
    prod_relics_doc = read_json(paths.relics_production) or {"schema_version": 1, "relics": []}
    prod_cards = list(prod_cards_doc.get("cards") or [])
    prod_relics = list(prod_relics_doc.get("relics") or [])
    prod_cards = _dedupe_dict_entries(prod_cards, "card")
    prod_relics = _dedupe_dict_entries(prod_relics, "relic")

    known_names_cards = {str(c.get("internal_name")) for c in prod_cards if c.get("internal_name")}
    known_names_cards |= {c.internal_name for c in raw_cards if c.internal_name}
    known_names_relics = {str(r.get("internal_name")) for r in prod_relics if r.get("internal_name")}
    known_names_relics |= {r.internal_name for r in raw_relics if r.internal_name}

    rss = fetcher.get_text(cfg.sources.steam_news_rss, force=force)
    patch_records = parse_steam_rss(rss.text, rss.url, rss.fetched_at)
    patches_out: list[dict[str, object]] = []
    affected: list[tuple[str, str, str]] = []
    for pr in patch_records:
        ents = extract_patch_entities_heuristic(pr, known_names_cards, known_names_relics)
        entry = PatchNoteEntry(
            patch_id=pr.patch_id,
            date=pr.date,
            source_url=pr.source_url,
            affected_entities=ents,
        )
        patches_out.append(entry.model_dump())
        for e in ents:
            affected.append((e.type, e.internal_name, pr.patch_id))

    patch_cards, patch_relics = build_patch_maps(affected)

    enricher = build_enricher(cfg.llm)
    llm_cards = {}
    llm_relics = {}
    if enricher.is_enabled():
        # Full parsed coverage for refresh output (not just currently shipped production entities).
        llm_card_targets = [c for c in raw_cards if c.internal_name]
        llm_relic_targets = [r for r in raw_relics if r.internal_name]
        typer.echo(f"LLM cards: class-batched enrichment across {len(llm_card_targets)} target cards")
        llm_cards.update(enricher.enrich_cards(llm_card_targets))
        typer.echo(f"LLM relics: enriching up to {cfg.llm.max_items_per_run} target relics")
        llm_relics.update(enricher.enrich_relics(llm_relic_targets))

    merged_cards, q_cards = merge_cards(
        prod_cards,
        raw_cards,
        llm_cards,
        cfg.merge_mode,
        patch_cards,
    )
    merged_relics, q_relics = merge_relics(
        prod_relics,
        raw_relics,
        llm_relics,
        cfg.merge_mode,
        patch_relics,
    )

    cards_gen = {"schema_version": prod_cards_doc.get("schema_version", 1), "cards": merged_cards}
    relics_gen = {"schema_version": prod_relics_doc.get("schema_version", 1), "relics": merged_relics}

    write_json(paths.output_dir / "cards.generated.json", cards_gen)
    write_json(paths.output_dir / "relics.generated.json", relics_gen)
    write_json(paths.output_dir / "patch_notes.generated.json", {"schema_version": 1, "patches": patches_out})

    write_diff_json(paths.output_dir / "cards.diff.json", prod_cards_doc, cards_gen)
    write_diff_json(paths.output_dir / "relics.diff.json", prod_relics_doc, relics_gen)

    issues_c = validate_cards_document(cards_gen)
    issues_r = validate_relics_document(relics_gen)
    messages = [f"{i.level}: {i.message}" for i in issues_c + issues_r]

    rq = ReviewQueueFile(
        generated_at=utc_now_iso(),
        items=q_cards + q_relics,
    )
    write_json(paths.output_dir / "review_queue.json", rq.model_dump())

    write_refresh_report(
        paths.output_dir / "refresh_report.md",
        fetch_summary,
        messages,
        len([i for i in issues_c if i.level == "error"]),
        len([i for i in issues_r if i.level == "error"]),
        len(rq.items),
        keywords_glossary=kw_sum,
    )

    write_json(paths.output_dir / "fetch_manifest.json", {"generated_at": utc_now_iso(), "entries": fetch_summary})

    typer.echo(
        f"Refresh complete. Review queue: {len(rq.items)} items. "
        f"Keywords glossary: {kw_sum['term_count']} terms (mechanical, no LLM). "
        f"Report: {paths.output_dir / 'refresh_report.md'}"
    )


review_app = typer.Typer(help="Review queue commands")
app.add_typer(review_app, name="review")


@review_app.command("list")
def review_list(config: Path | None = typer.Option(None, "--config")) -> None:
    cfg = _cfg(config)
    pending = list_pending(cfg.paths.output_dir / "review_queue.json")
    for p in pending:
        typer.echo(f"{p.entity_type} {p.internal_name}: {p.reason}")


@review_app.command("approve")
def review_approve(
    type: str = typer.Option(..., "--type", help="card or relic"),
    id: str = typer.Option(..., "--id", help="internal_name"),
    config: Path | None = typer.Option(None, "--config"),
) -> None:
    cfg = _cfg(config)
    approve(cfg.paths.output_dir / "review_queue.json", type, id)
    typer.echo("Approved.")


@review_app.command("reject")
def review_reject(
    type: str = typer.Option(..., "--type"),
    id: str = typer.Option(..., "--id"),
    config: Path | None = typer.Option(None, "--config"),
) -> None:
    cfg = _cfg(config)
    reject(cfg.paths.output_dir / "review_queue.json", type, id)
    typer.echo("Rejected.")


@review_app.command("note")
def review_note(
    type: str = typer.Option(..., "--type"),
    id: str = typer.Option(..., "--id"),
    message: str = typer.Option(..., "--message"),
    config: Path | None = typer.Option(None, "--config"),
) -> None:
    cfg = _cfg(config)
    note(cfg.paths.output_dir / "review_queue.json", type, id, message)
    typer.echo("Note added.")


@app.command("apply-approved")
def apply_approved_cmd(config: Path | None = typer.Option(None, "--config")) -> None:
    """Merge approved review items into production JSON (creates backup first)."""

    cfg = _cfg(config)
    paths = cfg.paths
    backup_dir = create_backup(paths.cards_production, paths.relics_production, paths.backups_dir)
    typer.echo(f"Backup: {backup_dir}")
    apply_approved(
        paths.output_dir / "review_queue.json",
        paths.cards_production,
        paths.relics_production,
        paths.output_dir / "cards.generated.json",
        paths.output_dir / "relics.generated.json",
    )
    typer.echo("Applied approved changes to production JSON.")


@app.command("backup")
def backup_cmd(config: Path | None = typer.Option(None, "--config")) -> None:
    cfg = _cfg(config)
    paths = cfg.paths
    d = create_backup(paths.cards_production, paths.relics_production, paths.backups_dir)
    typer.echo(str(d))


@app.command("rollback")
def rollback_cmd(
    to: str = typer.Option(..., "--to", help="Backup folder name (timestamp)"),
    config: Path | None = typer.Option(None, "--config"),
) -> None:
    cfg = _cfg(config)
    paths = cfg.paths
    src = paths.backups_dir / to
    if not src.is_dir():
        typer.echo(f"Backup not found: {src}", err=True)
        raise typer.Exit(1)
    import shutil

    if (src / "cards.json").exists():
        shutil.copy2(src / "cards.json", paths.cards_production)
    if (src / "relics.json").exists():
        shutil.copy2(src / "relics.json", paths.relics_production)
    typer.echo(f"Restored from {src}")


@app.command("backups-list")
def backups_list(config: Path | None = typer.Option(None, "--config")) -> None:
    cfg = _cfg(config)
    for name, p in list_backups(cfg.paths.backups_dir):
        typer.echo(f"{name}  {p}")


def main() -> None:
    app()


if __name__ == "__main__":
    main()

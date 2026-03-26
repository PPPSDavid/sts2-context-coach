#!/usr/bin/env python3
"""CLI for offline STS2 metadata refresh (separate from the game mod runtime)."""

from __future__ import annotations

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
from merge import build_patch_maps, merge_cards, merge_relics  # noqa: E402
from models import (  # noqa: E402
    PatchNoteEntry,
    RawCardRecord,
    RawRelicRecord,
    ReviewQueueFile,
    utc_now_iso,
)
from parsers.cards_parser import enrich_cards_with_detail_pages, parse_cards_from_wiki_html  # noqa: E402
from parsers.patch_parser import extract_patch_entities_heuristic, parse_steam_rss  # noqa: E402
from parsers.relics_parser import parse_relics_from_wiki_html  # noqa: E402
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
    """Fetch (cache) + parse into intermediate JSON."""

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

    write_json(
        paths.output_dir / "parsed_raw.json",
        {
            "generated_at": utc_now_iso(),
            "cards": [c.model_dump() for c in raw_cards],
            "relics": [r.model_dump() for r in raw_relics],
        },
    )
    typer.echo(f"Parsed {len(raw_cards)} cards, {len(raw_relics)} relics -> output/parsed_raw.json")


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
    if gen_c:
        write_diff_json(paths.output_dir / "cards.diff.json", prod_c, gen_c)
    if gen_r:
        write_diff_json(paths.output_dir / "relics.diff.json", prod_r, gen_r)
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
    """Run full pipeline: fetch, parse, merge, optional LLM, outputs + report + review queue."""

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

    write_json(
        paths.output_dir / "parsed_raw.json",
        {
            "generated_at": utc_now_iso(),
            "cards": [c.model_dump() for c in raw_cards],
            "relics": [r.model_dump() for r in raw_relics],
        },
    )

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
    )

    write_json(paths.output_dir / "fetch_manifest.json", {"generated_at": utc_now_iso(), "entries": fetch_summary})

    typer.echo(f"Refresh complete. Review queue: {len(rq.items)} items. Report: {paths.output_dir / 'refresh_report.md'}")


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

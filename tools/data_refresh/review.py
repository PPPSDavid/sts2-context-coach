"""Lightweight review queue persistence and apply-approved."""

from __future__ import annotations

import copy
from pathlib import Path
from typing import Any

from io_utils import read_json, write_json
from models import ReviewQueueFile, ReviewQueueItem, utc_now_iso


def load_queue(path: Path) -> ReviewQueueFile:
    raw = read_json(path)
    if not raw:
        return ReviewQueueFile(generated_at=utc_now_iso())
    return ReviewQueueFile.model_validate(raw)


def save_queue(path: Path, q: ReviewQueueFile) -> None:
    q.generated_at = utc_now_iso()
    write_json(path, q.model_dump())


def find_item(q: ReviewQueueFile, entity_type: str, internal_name: str) -> ReviewQueueItem | None:
    for it in q.items:
        if it.entity_type == entity_type and it.internal_name == internal_name:
            return it
    return None


def approve(path: Path, entity_type: str, internal_name: str, reviewer: str = "local") -> None:
    q = load_queue(path)
    it = find_item(q, entity_type, internal_name)
    if it is None:
        raise ValueError(f"No queue item for {entity_type}/{internal_name}")
    it.review_status = "approved"
    it.provenance["reviewed_by"] = reviewer
    it.provenance["reviewed_at"] = utc_now_iso()
    save_queue(path, q)


def reject(path: Path, entity_type: str, internal_name: str, reviewer: str = "local") -> None:
    q = load_queue(path)
    it = find_item(q, entity_type, internal_name)
    if it is None:
        raise ValueError(f"No queue item for {entity_type}/{internal_name}")
    it.review_status = "rejected"
    it.provenance["reviewed_by"] = reviewer
    it.provenance["reviewed_at"] = utc_now_iso()
    save_queue(path, q)


def note(path: Path, entity_type: str, internal_name: str, message: str) -> None:
    q = load_queue(path)
    it = find_item(q, entity_type, internal_name)
    if it is None:
        raise ValueError(f"No queue item for {entity_type}/{internal_name}")
    it.reason = f"{it.reason}\nNote: {message}".strip()
    if it.provenance is None:
        it.provenance = {}
    it.provenance["notes"] = message
    save_queue(path, q)


def list_pending(path: Path) -> list[ReviewQueueItem]:
    q = load_queue(path)
    return [i for i in q.items if i.review_status == "needs_review"]


def apply_approved(
    queue_path: Path,
    cards_production: Path,
    relics_production: Path,
    generated_cards_path: Path,
    generated_relics_path: Path,
) -> None:
    """Apply approved queue items to production JSON, using proposed values."""

    q = load_queue(queue_path)
    gen_cards = read_json(generated_cards_path) or {}
    gen_relics = read_json(generated_relics_path) or {}

    prod_cards_doc = read_json(cards_production) or {"schema_version": 1, "cards": []}
    prod_relics_doc = read_json(relics_production) or {"schema_version": 1, "relics": []}

    cards_list = list(prod_cards_doc.get("cards") or [])
    relics_list = list(prod_relics_doc.get("relics") or [])

    by_card = {c["internal_name"]: i for i, c in enumerate(cards_list) if c.get("internal_name")}
    by_relic = {r["internal_name"]: i for i, r in enumerate(relics_list) if r.get("internal_name")}

    gen_card_by = {c["internal_name"]: c for c in (gen_cards.get("cards") or []) if c.get("internal_name")}
    gen_relic_by = {r["internal_name"]: r for r in (gen_relics.get("relics") or []) if r.get("internal_name")}

    for it in q.items:
        if it.review_status != "approved":
            continue
        if it.entity_type == "card":
            idx = by_card.get(it.internal_name)
            if idx is None:
                # new card from generated
                base = copy.deepcopy(gen_card_by.get(it.internal_name, {}))
                if base:
                    _strip_meta_review(base)
                    cards_list.append(base)
                    by_card[it.internal_name] = len(cards_list) - 1
            else:
                ent = cards_list[idx]
                _apply_proposed(ent, it.proposed, gen_card_by.get(it.internal_name, {}))
                _strip_meta_review(ent)
        elif it.entity_type == "relic":
            idx = by_relic.get(it.internal_name)
            if idx is None:
                base = copy.deepcopy(gen_relic_by.get(it.internal_name, {}))
                if base:
                    _strip_meta_review(base)
                    relics_list.append(base)
                    by_relic[it.internal_name] = len(relics_list) - 1
            else:
                ent = relics_list[idx]
                _apply_proposed(ent, it.proposed, gen_relic_by.get(it.internal_name, {}))
                _strip_meta_review(ent)

    prod_cards_doc["cards"] = cards_list
    prod_relics_doc["relics"] = relics_list
    write_json(cards_production, prod_cards_doc)
    write_json(relics_production, prod_relics_doc)


def _apply_proposed(ent: dict[str, Any], proposed: dict[str, Any], generated_full: dict[str, Any]) -> None:
    meta = ent.setdefault("_meta", {})
    for k, v in proposed.items():
        if k == "patch_context":
            meta["patch_context"] = copy.deepcopy(v)
        elif not str(k).startswith("_meta"):
            ent[k] = copy.deepcopy(v)
    if generated_full.get("_meta"):
        ent["_meta"] = copy.deepcopy(generated_full["_meta"])
        ent["_meta"]["review_status"] = "approved"
        ent["_meta"]["last_reviewed_at"] = utc_now_iso()


def _strip_meta_review(ent: dict[str, Any]) -> None:
    meta = ent.get("_meta")
    if isinstance(meta, dict):
        meta["review_status"] = "approved"
        meta["last_reviewed_at"] = utc_now_iso()

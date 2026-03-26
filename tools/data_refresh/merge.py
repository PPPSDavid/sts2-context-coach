"""Conservative merge: production + parsed sources + optional LLM (safe by default)."""

from __future__ import annotations

import copy
from typing import Any

from config import GENERATOR_VERSION
from models import (
    LlmCardEnrichment,
    LlmRelicEnrichment,
    PatchContext,
    RawCardRecord,
    RawRelicRecord,
    ReviewQueueItem,
    utc_now_iso,
)


def _display_text(raw: str | dict[str, Any] | None) -> str:
    if raw is None:
        return ""
    if isinstance(raw, dict):
        en = raw.get("en")
        if isinstance(en, str) and en.strip():
            return en
        for value in raw.values():
            if isinstance(value, str) and value.strip():
                return value
        return ""
    return str(raw)


def _display_en_only(d: dict[str, Any]) -> str:
    dn = d.get("display_name")
    if isinstance(dn, dict):
        return str(dn.get("en") or "")
    return str(dn or "")


def normalize_card_entry(c: dict[str, Any]) -> dict[str, Any]:
    out = copy.deepcopy(c)
    out["display_name"] = _display_text(out.get("display_name"))
    return out


def normalize_relic_entry(r: dict[str, Any]) -> dict[str, Any]:
    out = copy.deepcopy(r)
    out["display_name"] = _display_text(out.get("display_name"))
    return out


def _ensure_meta(
    base: dict[str, Any],
    source_urls: list[str],
    review_status: str,
    patch_ctx: PatchContext | None = None,
) -> dict[str, Any]:
    meta = base.get("_meta") or {}
    if not isinstance(meta, dict):
        meta = {}
    pc = patch_ctx or PatchContext()
    merged_meta: dict[str, Any] = {
        "source_urls": list(dict.fromkeys(meta.get("source_urls", []) + source_urls)),
        "source_last_seen": utc_now_iso(),
        "generated_at": utc_now_iso(),
        "generator_version": GENERATOR_VERSION,
        "review_status": review_status,
        "last_reviewed_at": meta.get("last_reviewed_at"),
        "review_notes": meta.get("review_notes"),
        "reviewed_by": meta.get("reviewed_by"),
        "patch_context": pc.model_dump(),
        "manual_override_fields": list(meta.get("manual_override_fields") or []),
        "field_provenance": dict(meta.get("field_provenance") or {}),
    }
    base["_meta"] = merged_meta
    return base


def merge_cards(
    production_cards: list[dict[str, Any]],
    wiki_cards: list[RawCardRecord],
    llm_by_internal: dict[str, LlmCardEnrichment],
    merge_mode: str,
    patch_by_internal: dict[str, PatchContext],
) -> tuple[list[dict[str, Any]], list[ReviewQueueItem]]:
    """Return merged card dicts (with _meta) and review queue items."""

    by_id: dict[str, dict[str, Any]] = {}
    for c in production_cards:
        iid = c.get("internal_name")
        if isinstance(iid, str):
            by_id[iid] = normalize_card_entry(c)

    wiki_by_id = {w.internal_name or "": w for w in wiki_cards if w.internal_name}

    queue: list[ReviewQueueItem] = []
    merged_out: list[dict[str, Any]] = []

    all_ids = sorted(set(by_id.keys()) | set(wiki_by_id.keys()))

    for iid in all_ids:
        prod = by_id.get(iid)
        wiki = wiki_by_id.get(iid)
        base = copy.deepcopy(prod) if prod else {}
        if not base.get("internal_name"):
            base["internal_name"] = iid

        manual = set(base.get("_meta", {}).get("manual_override_fields") or [])

        prev_snapshot = {k: copy.deepcopy(base.get(k)) for k in _card_compare_keys(base)}
        prev_meta_pc = copy.deepcopy((base.get("_meta") or {}).get("patch_context"))

        local_q: list[ReviewQueueItem] = []

        source_urls: list[str] = []
        if wiki and wiki.source_url:
            source_urls.append(wiki.source_url)

        if wiki:
            _apply_wiki_card(base, wiki, merge_mode, manual, local_q, iid)

        llm = llm_by_internal.get(iid)
        if llm:
            _apply_llm_card(base, llm, merge_mode, manual, local_q, iid)

        pctx = patch_by_internal.get(iid) or PatchContext()
        if pctx.needs_rereview_due_to_patch or pctx.recently_changed:
            local_q.append(
                ReviewQueueItem(
                    entity_type="card",
                    internal_name=iid,
                    changed_fields=["_meta.patch_context"],
                    previous={"patch_context": prev_meta_pc},
                    proposed={"patch_context": pctx.model_dump()},
                    provenance={"source": "steam", "derived_from": "patch_notes"},
                    confidence=0.6,
                    reason="Entity referenced in recent patch notes — verify gameplay tags.",
                )
            )

        queue.extend(local_q)

        review_status = "needs_review"
        if prod and not local_q and _card_unchanged(prev_snapshot, base):
            review_status = str((prod.get("_meta") or {}).get("review_status") or "approved")
        if not prod:
            review_status = "needs_review"

        _ensure_meta(base, source_urls, review_status, pctx)
        merged_out.append(base)

    return merged_out, queue


def _card_compare_keys(card: dict[str, Any]) -> list[str]:
    return [
        "internal_name",
        "display_name",
        "character",
        "cost",
        "rarity",
        "type",
        "description",
        "tags",
        "synergy_tags",
        "role_tags",
        "impact_level",
        "notes",
        "upgraded_description",
        "upgrade_summary",
        "upgrade_cost_delta",
        "upgrade_block_delta",
        "upgrade_draw_delta",
        "upgrade_damage_delta",
        "upgrade_removes_exhaust",
        "upgrade_major",
        "upgrade_tier",
        "enchantment_potential_tier",
        "enchantment_tier_by_kind",
    ]


def _card_unchanged(prev: dict[str, Any], cur: dict[str, Any]) -> bool:
    for k in _card_compare_keys(cur):
        if prev.get(k) != cur.get(k):
            return False
    return True


def _apply_wiki_card(
    base: dict[str, Any],
    wiki: RawCardRecord,
    merge_mode: str,
    manual: set[str],
    queue: list[ReviewQueueItem],
    iid: str,
) -> None:
    field_map = [
        ("display_name", _display_text(wiki.name)),
        ("character", wiki.character),
        ("cost", wiki.cost),
        ("rarity", wiki.rarity),
        ("type", wiki.type),
        ("description", wiki.raw_description or None),
        ("upgraded_description", wiki.upgraded_description or None),
    ]
    for field, new_val in field_map:
        if new_val is None:
            continue
        if field in manual:
            continue
        if field == "display_name":
            old = base.get("display_name")
            old_en = _display_en_only(base)
            new_en = _display_text(new_val)
            if not old_en.strip() and new_en.strip():
                base["display_name"] = new_val
                _prov(base, field, "wiki", 1.0, "parsed_source")
                continue
            if old_en and old_en != new_en:
                queue.append(
                    ReviewQueueItem(
                        entity_type="card",
                        internal_name=iid,
                        changed_fields=[field],
                        previous={"display_name": old},
                        proposed={"display_name": new_val},
                        provenance={"source": "wiki", "confidence": 1.0, "derived_from": "parsed_source"},
                        confidence=1.0,
                        reason="Wiki display name differs from production",
                    )
                )
                if merge_mode in ("safe", "suggest"):
                    continue
            base["display_name"] = new_val
            _prov(base, field, "wiki", 1.0, "parsed_source")
            continue

        old = base.get(field)
        if old is not None and old != new_val:
            queue.append(
                ReviewQueueItem(
                    entity_type="card",
                    internal_name=iid,
                    changed_fields=[field],
                    previous={field: old},
                    proposed={field: new_val},
                    provenance={"source": "wiki", "confidence": 1.0, "derived_from": "parsed_source"},
                    confidence=1.0,
                    reason=f"Wiki suggests different {field}",
                )
            )
            if merge_mode in ("safe", "suggest"):
                continue
        base[field] = new_val
        _prov(base, field, "wiki", 1.0, "parsed_source")

    _apply_upgrade_mechanics_from_wiki(base, wiki)


def _apply_upgrade_mechanics_from_wiki(base: dict[str, Any], wiki: RawCardRecord) -> None:
    base_desc = (wiki.raw_description or "").strip()
    upg_desc = (wiki.upgraded_description or "").strip()
    if not base_desc or not upg_desc:
        return

    if wiki.cost is not None and wiki.upgrade_cost is not None:
        base["upgrade_cost_delta"] = int(wiki.upgrade_cost) - int(wiki.cost)
        _prov(base, "upgrade_cost_delta", "wiki", 0.95, "parsed_source")

    base_block = _extract_first_int_after(base_desc, "Block")
    upg_block = _extract_first_int_after(upg_desc, "Block")
    if base_block is not None and upg_block is not None:
        base["upgrade_block_delta"] = upg_block - base_block
        _prov(base, "upgrade_block_delta", "wiki", 0.9, "parsed_source")

    base_draw = _extract_first_int_after(base_desc, "Draw")
    upg_draw = _extract_first_int_after(upg_desc, "Draw")
    if base_draw is not None and upg_draw is not None:
        base["upgrade_draw_delta"] = upg_draw - base_draw
        _prov(base, "upgrade_draw_delta", "wiki", 0.9, "parsed_source")

    base_dmg = _extract_first_int_after(base_desc, "Deal")
    upg_dmg = _extract_first_int_after(upg_desc, "Deal")
    if base_dmg is not None and upg_dmg is not None:
        base["upgrade_damage_delta"] = upg_dmg - base_dmg
        _prov(base, "upgrade_damage_delta", "wiki", 0.88, "parsed_source")

    base_exh = "exhaust" in base_desc.lower()
    upg_exh = "exhaust" in upg_desc.lower()
    if base_exh and not upg_exh:
        base["upgrade_removes_exhaust"] = True
        _prov(base, "upgrade_removes_exhaust", "wiki", 0.92, "parsed_source")


def _extract_first_int_after(text: str, token: str) -> int | None:
    import re

    m = re.search(rf"(\\d+)\\s*{re.escape(token)}", text, flags=re.IGNORECASE)
    if m:
        try:
            return int(m.group(1))
        except ValueError:
            return None
    # Fallback for phrasings like "Draw 2 cards"
    m2 = re.search(rf"{re.escape(token)}\\s*(\\d+)", text, flags=re.IGNORECASE)
    if m2:
        try:
            return int(m2.group(1))
        except ValueError:
            return None
    return None


def _apply_llm_card(
    base: dict[str, Any],
    llm: LlmCardEnrichment,
    merge_mode: str,
    manual: set[str],
    queue: list[ReviewQueueItem],
    iid: str,
) -> None:
    for field in [
        "tags",
        "synergy_tags",
        "role_tags",
        "impact_level",
        "notes",
        "upgrade_summary",
        "upgrade_tier",
        "enchantment_potential_tier",
        "enchantment_tier_by_kind",
    ]:
        if field in manual:
            continue
        new_val = getattr(llm, field, None)
        if field in ("tags", "synergy_tags", "role_tags"):
            if not new_val:
                continue
        elif field in ("notes", "impact_level", "upgrade_summary"):
            if new_val is None:
                continue
        elif field in ("upgrade_tier", "enchantment_potential_tier"):
            if new_val is None:
                continue
        elif field in ("enchantment_tier_by_kind",):
            if not new_val:
                continue
        else:
            continue
        old = base.get(field)
        if merge_mode in ("safe", "suggest"):
            queue.append(
                ReviewQueueItem(
                    entity_type="card",
                    internal_name=iid,
                    changed_fields=[field],
                    previous={field: old},
                    proposed={field: new_val},
                    provenance={"source": "llm", "confidence": llm.confidence, "derived_from": "inferred"},
                    confidence=llm.confidence,
                    reason="LLM proposed value — requires approval in safe/suggest mode",
                )
            )
            continue
        base[field] = new_val
        _prov(base, field, "llm", llm.confidence, "llm_inference")


def _prov(base: dict[str, Any], field: str, source: str, conf: float, derived: str) -> None:
    meta = base.setdefault("_meta", {})
    fp = meta.setdefault("field_provenance", {})
    fp[field] = {"source": source, "confidence": conf, "derived_from": derived}


def merge_relics(
    production_relics: list[dict[str, Any]],
    wiki_relics: list[RawRelicRecord],
    llm_by_internal: dict[str, LlmRelicEnrichment],
    merge_mode: str,
    patch_by_internal: dict[str, PatchContext],
) -> tuple[list[dict[str, Any]], list[ReviewQueueItem]]:
    by_id: dict[str, dict[str, Any]] = {}
    for r in production_relics:
        iid = r.get("internal_name")
        if isinstance(iid, str):
            by_id[iid] = normalize_relic_entry(r)

    wiki_by_id = {w.internal_name or "": w for w in wiki_relics if w.internal_name}
    queue: list[ReviewQueueItem] = []
    out: list[dict[str, Any]] = []

    for iid in sorted(set(by_id.keys()) | set(wiki_by_id.keys())):
        prod = by_id.get(iid)
        wiki = wiki_by_id.get(iid)
        base = copy.deepcopy(prod) if prod else {}
        if not base.get("internal_name"):
            base["internal_name"] = iid
        manual = set(base.get("_meta", {}).get("manual_override_fields") or [])
        local_q: list[ReviewQueueItem] = []
        source_urls: list[str] = []
        if wiki and wiki.source_url:
            source_urls.append(wiki.source_url)

        if wiki:
            new_dn = _display_text(wiki.name)
            old_en = _display_en_only(base)
            if not old_en.strip() and new_dn:
                base["display_name"] = new_dn
                _prov(base, "display_name", "wiki", 1.0, "parsed_source")
            elif old_en and new_dn and old_en != new_dn:
                local_q.append(
                    ReviewQueueItem(
                        entity_type="relic",
                        internal_name=iid,
                        changed_fields=["display_name"],
                        previous={"display_name": base.get("display_name")},
                        proposed={"display_name": new_dn},
                        provenance={"source": "wiki", "confidence": 1.0, "derived_from": "parsed_source"},
                        confidence=1.0,
                        reason="Wiki display name differs",
                    )
                )
                if merge_mode not in ("safe", "suggest"):
                    base["display_name"] = new_dn
                    _prov(base, "display_name", "wiki", 1.0, "parsed_source")
            if wiki.raw_description:
                if not base.get("description"):
                    base["description"] = wiki.raw_description
                    _prov(base, "description", "wiki", 1.0, "parsed_source")

        llm = llm_by_internal.get(iid)
        if llm:
            for field in ["tags", "synergy_tags", "notes"]:
                if field in manual:
                    continue
                new_val = getattr(llm, field, None)
                if field != "notes" and not new_val:
                    continue
                if field == "notes" and not new_val:
                    continue
                old = base.get(field)
                if merge_mode in ("safe", "suggest"):
                    local_q.append(
                        ReviewQueueItem(
                            entity_type="relic",
                            internal_name=iid,
                            changed_fields=[field],
                            previous={field: old},
                            proposed={field: new_val},
                            provenance={"source": "llm", "confidence": llm.confidence, "derived_from": "inferred"},
                            confidence=llm.confidence,
                            reason="LLM proposed relic field",
                        )
                    )
                    continue
                base[field] = new_val
                _prov(base, field, "llm", llm.confidence, "llm_inference")

        pctx = patch_by_internal.get(iid) or PatchContext()
        if pctx.needs_rereview_due_to_patch or pctx.recently_changed:
            local_q.append(
                ReviewQueueItem(
                    entity_type="relic",
                    internal_name=iid,
                    changed_fields=["_meta.patch_context"],
                    previous={"patch_context": (base.get("_meta") or {}).get("patch_context")},
                    proposed={"patch_context": pctx.model_dump()},
                    provenance={"source": "steam", "derived_from": "patch_notes"},
                    confidence=0.6,
                    reason="Relic mentioned in patch notes — verify notes/tags.",
                )
            )

        queue.extend(local_q)

        review_status = "needs_review"
        if prod and not local_q:
            review_status = str((prod.get("_meta") or {}).get("review_status") or "approved")
        if not prod:
            review_status = "needs_review"

        _ensure_meta(base, source_urls, review_status, pctx)
        out.append(base)

    return out, queue


def build_patch_maps(
    affected: list[tuple[str, str, str]],
) -> tuple[dict[str, PatchContext], dict[str, PatchContext]]:
    """affected: (entity_type, internal_name, patch_id) — last occurrence wins for last_seen_patch."""

    cards: dict[str, PatchContext] = {}
    relics: dict[str, PatchContext] = {}
    for typ, iid, pid in affected:
        bucket = cards if typ == "card" else relics
        if iid in bucket:
            prev = bucket[iid]
            if pid not in prev.related_patch_ids:
                prev.related_patch_ids.append(pid)
            prev.last_seen_patch = pid
            prev.recently_changed = True
            prev.needs_rereview_due_to_patch = True
        else:
            bucket[iid] = PatchContext(
                last_seen_patch=pid,
                recently_changed=True,
                related_patch_ids=[pid],
                needs_rereview_due_to_patch=True,
            )
    return cards, relics

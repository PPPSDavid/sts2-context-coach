"""Pluggable LLM enrichment — disabled when no API key."""

from __future__ import annotations

import json
import os
import time
from typing import Any

import requests

from config import LlmConfig
from models import LlmCardEnrichment, LlmRelicEnrichment, RawCardRecord, RawRelicRecord
from tag_vocabulary import (
    SUPPORTED_CARD_ROLE_TAGS,
    SUPPORTED_CARD_SYNERGY_TAGS,
    SUPPORTED_CARD_TAGS,
    SUPPORTED_IMPACT_LEVELS,
    SUPPORTED_RELIC_SYNERGY_TAGS,
    SUPPORTED_RELIC_TAGS,
    normalize_tag_list,
)


class LlmEnricher:
    """OpenAI-compatible chat completions by default; swap provider in subclasses."""

    def __init__(self, cfg: LlmConfig) -> None:
        self.cfg = cfg

    def is_enabled(self) -> bool:
        if not self.cfg.enabled:
            return False
        key = os.environ.get(self.cfg.api_key_env, "")
        return bool(key.strip())

    def enrich_cards(self, cards: list[RawCardRecord]) -> dict[str, LlmCardEnrichment]:
        out: dict[str, LlmCardEnrichment] = {}
        if not self.is_enabled():
            return out

        # Batch by character/class for better in-class consistency and fewer API calls.
        target_cards = [c for c in cards if c.internal_name][: self.cfg.max_items_per_run]
        by_class: dict[str, list[RawCardRecord]] = {}
        for c in target_cards:
            k = (c.character or "Unknown").strip() or "Unknown"
            by_class.setdefault(k, []).append(c)

        colorless = by_class.get("Colorless", [])
        colorless_refs = [
            {
                "internal_name": c.internal_name,
                "type": c.type,
                "description": c.raw_description,
            }
            for c in colorless
            if c.internal_name
        ][:80]

        # Prioritize real classes first; colorless can be done last.
        for cls in sorted(k for k in by_class.keys() if k != "Colorless"):
            for batch in _chunk(by_class[cls], 40):
                try:
                    batch_out = self._card_batch_for_class(cls, batch, colorless_refs)
                    out.update(batch_out)
                except Exception:
                    for c in batch:
                        if c.internal_name:
                            out[c.internal_name] = LlmCardEnrichment(confidence=0.0, inferred=True)

        if colorless:
            for batch in _chunk(colorless, 40):
                try:
                    batch_out = self._card_batch_for_class("Colorless", batch, [])
                    out.update(batch_out)
                except Exception:
                    for c in batch:
                        if c.internal_name:
                            out[c.internal_name] = LlmCardEnrichment(confidence=0.0, inferred=True)
        return out

    def enrich_relics(self, relics: list[RawRelicRecord]) -> dict[str, LlmRelicEnrichment]:
        out: dict[str, LlmRelicEnrichment] = {}
        if not self.is_enabled():
            return out
        for r in relics[: self.cfg.max_items_per_run]:
            if not r.internal_name:
                continue
            try:
                out[r.internal_name] = self._one_relic(r)
            except Exception:
                out[r.internal_name] = LlmRelicEnrichment(confidence=0.0, inferred=True)
        return out

    def _one_card(self, c: RawCardRecord) -> LlmCardEnrichment:
        system = (
            "You are a strict Slay the Spire 2 metadata classifier. "
            "Output ONLY one JSON object and nothing else. "
            "Use only provided allowed vocab items; do not invent tags. "
            "Prefer precision over recall: if uncertain, omit tags instead of guessing. "
            "Do not assign draw-related labels unless the description explicitly draws cards."
        )
        user = json.dumps(
            {
                "name": c.name,
                "description": c.raw_description,
                "character": c.character,
                "type": c.type,
                "allowed_tags": SUPPORTED_CARD_TAGS,
                "allowed_synergy_tags": SUPPORTED_CARD_SYNERGY_TAGS,
                "allowed_role_tags": SUPPORTED_CARD_ROLE_TAGS,
                "allowed_impact_levels": SUPPORTED_IMPACT_LEVELS,
            },
            ensure_ascii=False,
        )
        prompt = (
            "Return JSON with keys exactly: tags, synergy_tags, role_tags, impact_level, notes, confidence.\n"
            "Rules:\n"
            "- tags/synergy_tags/role_tags must be arrays of strings from allowed vocab only.\n"
            "- impact_level must be one of allowed_impact_levels or null.\n"
            "- confidence must be a number 0..1.\n"
            "- notes must be short and factual.\n"
            "- If description does not clearly support a label, leave it out.\n"
            "Few-shot examples:\n"
            "INPUT: {\"name\":\"Hemokinesis\",\"description\":\"Lose 2 HP. Deal 14 damage.\"}\n"
            "OUTPUT: {\"tags\":[\"attack\",\"frontload\"],\"synergy_tags\":[\"attack\"],\"role_tags\":[\"frontload\"],\"impact_level\":\"medium\",\"notes\":\"Direct frontloaded damage with HP cost.\",\"confidence\":0.78}\n"
            "INPUT: {\"name\":\"Shrug It Off\",\"description\":\"Gain 8 Block. Draw 1 card.\"}\n"
            "OUTPUT: {\"tags\":[\"block\",\"draw\"],\"synergy_tags\":[\"block\",\"draw\"],\"role_tags\":[\"sustain\",\"consistency\"],\"impact_level\":\"medium\",\"notes\":\"Efficient defense plus cantrip draw.\",\"confidence\":0.9}\n"
            f"Classify this card:\n{user}"
        )
        data = self._chat(system, prompt)
        parsed = LlmCardEnrichment.model_validate(data)
        parsed.tags = normalize_tag_list(parsed.tags, SUPPORTED_CARD_TAGS)
        parsed.synergy_tags = normalize_tag_list(parsed.synergy_tags, SUPPORTED_CARD_SYNERGY_TAGS)
        parsed.role_tags = normalize_tag_list(parsed.role_tags, SUPPORTED_CARD_ROLE_TAGS)
        if parsed.impact_level and parsed.impact_level.lower() in SUPPORTED_IMPACT_LEVELS:
            parsed.impact_level = parsed.impact_level.lower()
        else:
            parsed.impact_level = None
        parsed.confidence = max(0.0, min(1.0, float(parsed.confidence)))
        return parsed

    def _one_relic(self, r: RawRelicRecord) -> LlmRelicEnrichment:
        system = (
            "You are a strict Slay the Spire 2 relic metadata classifier. "
            "Output ONLY one JSON object and nothing else. "
            "Use only provided allowed vocab; prefer precision over recall."
        )
        user = json.dumps(
            {
                "name": r.name,
                "description": r.raw_description,
                "allowed_tags": SUPPORTED_RELIC_TAGS,
                "allowed_synergy_tags": SUPPORTED_RELIC_SYNERGY_TAGS,
            },
            ensure_ascii=False,
        )
        prompt = (
            "Return JSON with keys exactly: tags, synergy_tags, notes, confidence.\n"
            "Rules:\n"
            "- tags/synergy_tags must be arrays from allowed vocab only.\n"
            "- Do not emit broad catch-all tag sets; include only directly supported tags.\n"
            "- confidence must be 0..1.\n"
            "Few-shot examples:\n"
            "INPUT: {\"name\":\"Bag of Preparation\",\"description\":\"At the start of each combat, draw 2 additional cards.\"}\n"
            "OUTPUT: {\"tags\":[\"draw\"],\"synergy_tags\":[\"draw\"],\"notes\":\"Improves opening hand consistency.\",\"confidence\":0.93}\n"
            "INPUT: {\"name\":\"Burning Blood\",\"description\":\"At the end of combat, heal 6 HP.\"}\n"
            "OUTPUT: {\"tags\":[\"block\"],\"synergy_tags\":[],\"notes\":\"Post-combat sustain, not direct combat synergy.\",\"confidence\":0.62}\n"
            f"Classify this relic:\n{user}"
        )
        data = self._chat(system, prompt)
        parsed = LlmRelicEnrichment.model_validate(data)
        parsed.tags = normalize_tag_list(parsed.tags, SUPPORTED_RELIC_TAGS)
        parsed.synergy_tags = normalize_tag_list(parsed.synergy_tags, SUPPORTED_RELIC_SYNERGY_TAGS)
        parsed.confidence = max(0.0, min(1.0, float(parsed.confidence)))
        return parsed

    def _card_batch_for_class(
        self,
        cls: str,
        cards: list[RawCardRecord],
        colorless_refs: list[dict[str, Any]],
    ) -> dict[str, LlmCardEnrichment]:
        system = (
            "You are a strict Slay the Spire 2 metadata classifier. "
            "Output ONLY one JSON object and nothing else. "
            "Use only provided allowed vocab items; do not invent tags. "
            "Prefer precision over recall: if uncertain, omit tags instead of guessing. "
            "Classify cards using card text first, then class-level context as a weak secondary signal.\n\n"
            "Gameplay context:\n"
            "- Typical turn starts with 3 energy.\n"
            "- Cards are played from hand and usually go to discard after being played.\n"
            "- You draw from draw pile each turn; typical hand limit is 10 cards.\n"
            "- Exhaust means a card is removed for the rest of combat.\n"
            "- Ethereal cards exhaust if unplayed at end of turn.\n"
            "- Colorless cards are neutral and can appear for all classes.\n"
            "- Base vs upgraded text comes from wiki fields (description, upgraded_description). "
            "Do not invent mechanics that are not implied by those strings.\n"
            "- For upgrades: numeric deltas (cost/block/draw/damage) and upgrade_removes_exhaust are "
            "recomputed from wiki text by the pipeline—your job is mainly upgrade_summary (1–2 sentences, "
            "strategic “why it matters”) and upgrade_tier (D–S). You may leave mechanical upgrade_* fields null.\n\n"
            "Upgrade tier rubric (compare base vs upgraded text; think long-term fight impact):\n"
            "- S: Run-defining or archetype-defining. Examples: removing Exhaust on a card whose effect scales "
            "with times played (e.g. each replay compounds value); upgrades that add a new “every shuffle” loop; "
            "or turning a core enabler into 0-cost while adding major velocity. Small number changes alone are not S.\n"
            "- A: Large spike—removes Exhaust on strong cards, −1 energy on cards you spam, big jumps on multi-hit "
            "or scaling payoffs, or upgrades that unlock a new primary plan.\n"
            "- B: Solid value—meaningful numbers or reliability, same role.\n"
            "- C: Minor but real bump.\n"
            "- D: Barely matters (e.g. tiny numbers on basic Strikes/Defends)—still worth upgrading early with excess gold."
        )

        payload = {
            "class": cls,
            "allowed_tags": SUPPORTED_CARD_TAGS,
            "allowed_synergy_tags": SUPPORTED_CARD_SYNERGY_TAGS,
            "allowed_role_tags": SUPPORTED_CARD_ROLE_TAGS,
            "allowed_impact_levels": SUPPORTED_IMPACT_LEVELS,
            "allowed_enchantment_kinds": ["attack", "block", "draw", "energy", "remove_exhaust"],
            "colorless_context": colorless_refs if cls != "Colorless" else [],
            "cards": [
                {
                    "internal_name": c.internal_name,
                    "name": c.name,
                    "description": c.raw_description,
                    "upgraded_description": c.upgraded_description,
                    "character": c.character,
                    "type": c.type,
                }
                for c in cards
                if c.internal_name
            ],
        }
        prompt = (
            "Return JSON with shape:\n"
            '{ "cards": [ {'
            '"internal_name": str, "tags": [], "synergy_tags": [], "role_tags": [], '
            '"impact_level": str|null, "notes": str, "confidence": number, '
            '"upgraded_description": str|null, "upgrade_summary": str|null, '
            '"upgrade_cost_delta": int|null, "upgrade_block_delta": int|null, '
            '"upgrade_draw_delta": int|null, "upgrade_damage_delta": int|null, '
            '"upgrade_removes_exhaust": bool|null, "upgrade_major": bool|null, "upgrade_tier": str|null, '
            '"enchantment_potential_tier": str|null, "enchantment_tier_by_kind": object|null '
            "} ] }\n"
            "Rules:\n"
            "- Include one output entry per input card.\n"
            "- tags/synergy_tags/role_tags must use allowed vocab only.\n"
            "- Prefer description-grounded tags. Do NOT add draw tags unless card text explicitly draws/adds cards to hand.\n"
            "- Use class context only to break ties, not to invent unsupported labels.\n"
            "- Keep notes short and factual.\n"
            "- upgrade_summary: short strategic rationale comparing upgraded_description to description (not just +N).\n"
            "- upgrade_tier: D/C/B/A/S from rubric in system message. "
            "Removing Exhaust on a scaling/combo engine is often S (see Voltaic pattern). "
            "Doubling both draw + discard on a cheap Silent enabler is often S for discard / Sly-style payoffs (see Prepared).\n"
            "- enchantment_potential_tier: expected value tier D/C/B/A/S assuming uncertain future enchantment availability. "
            "Probability-discount this; do not assume guaranteed best enchantment.\n"
            '- enchantment_tier_by_kind: object with optional keys from {"attack","block","draw","energy","remove_exhaust"}, '
            "each value D/C/B/A/S for realized value if that enchantment kind is currently on this card. "
            "If uncertain, omit key.\n"
            "- Mechanical upgrade_* number/boolean fields: prefer null (pipeline fills from wiki when possible).\n"
            "Few-shot:\n"
            "INPUT CARD: Hemokinesis | 'Lose 2 HP. Deal 14 damage.'\n"
            "OUTPUT: {\"internal_name\":\"Hemokinesis\",\"tags\":[\"attack\",\"frontload\"],\"synergy_tags\":[\"attack\"],"
            '"role_tags":["frontload"],"impact_level":"medium","notes":"Direct damage with HP cost.",'
            '"confidence":0.78,"upgraded_description":null,"upgrade_summary":"More damage; same downside.",'
            '"upgrade_cost_delta":null,"upgrade_block_delta":null,"upgrade_draw_delta":null,"upgrade_damage_delta":null,'
            '"upgrade_removes_exhaust":null,"upgrade_major":false,"upgrade_tier":"B",'
            '"enchantment_potential_tier":"B","enchantment_tier_by_kind":{"attack":"B","remove_exhaust":"A"}}\n'
            "INPUT CARD: ShrugItOff | 'Gain 8 Block. Draw 1 card.' | upgraded: 'Gain 11 Block. Draw 1 card.'\n"
            "OUTPUT: {\"internal_name\":\"ShrugItOff\",\"tags\":[\"block\",\"draw\"],\"synergy_tags\":[\"block\",\"draw\"],"
            '"role_tags":["sustain","consistency"],"impact_level":"medium","notes":"Defense plus cantrip draw.",'
            '"confidence":0.88,"upgraded_description":null,"upgrade_summary":"Extra block improves/defends a common pick.",'
            '"upgrade_cost_delta":null,"upgrade_block_delta":null,"upgrade_draw_delta":null,"upgrade_damage_delta":null,'
            '"upgrade_removes_exhaust":null,"upgrade_major":false,"upgrade_tier":"B",'
            '"enchantment_potential_tier":"B","enchantment_tier_by_kind":{"block":"B","draw":"B"}}\n'
            "INPUT CARD: Prepared (Silent) | 'Draw 1 card. Discard 1 card.' | upgraded: 'Draw 2 cards. Discard 2 cards.'\n"
            "OUTPUT: {\"internal_name\":\"Prepared\",\"tags\":[\"skill\",\"draw\",\"discard\"],\"synergy_tags\":[\"draw\",\"discard\"],"
            '"role_tags":["consistency","setup"],"impact_level":"medium","notes":"Cycles hand and fuels discard synergies.",'
            '"confidence":0.86,"upgraded_description":null,'
            '"upgrade_summary":"Both draw and discard climb 1→2: more chips to strip and better odds to hit discard payoffs (e.g. Sly) while this common skill usually costs 0 so tempo stays free.",'
            '"upgrade_cost_delta":null,"upgrade_block_delta":null,"upgrade_draw_delta":null,"upgrade_damage_delta":null,'
            '"upgrade_removes_exhaust":null,"upgrade_major":true,"upgrade_tier":"S",'
            '"enchantment_potential_tier":"S","enchantment_tier_by_kind":{"draw":"S","energy":"A","remove_exhaust":"A"}}\n'
            "INPUT CARD: Voltaic (Defect) | base has Exhaust | 'Channel Lightning equal to the Lightning already Channeled this combat. Exhaust .' "
            "| upgraded: 'Channel Lightning equal to the Lightning already Channeled this combat.'\n"
            "OUTPUT: {\"internal_name\":\"Voltaic\",\"tags\":[\"skill\",\"scaling\",\"exhaust\"],\"synergy_tags\":[\"scaling\",\"exhaust\"],"
            '"role_tags":["setup","scaling"],"impact_level":"high","notes":"Channels Lightning based on prior channels; exhaust gates repeats.",'
            '"confidence":0.9,"upgraded_description":null,'
            '"upgrade_summary":"Removing Exhaust lets you replay it every cycle; each play channels more Lightning than the last, so orbs snowball exponentially through the fight.",'
            '"upgrade_cost_delta":null,"upgrade_block_delta":null,"upgrade_draw_delta":null,"upgrade_damage_delta":null,'
            '"upgrade_removes_exhaust":null,"upgrade_major":true,"upgrade_tier":"S",'
            '"enchantment_potential_tier":"S","enchantment_tier_by_kind":{"remove_exhaust":"S","energy":"B"}}\n'
            f"Classify this batch:\n{json.dumps(payload, ensure_ascii=False)}"
        )
        data = self._chat(system, prompt)
        rows = data.get("cards") if isinstance(data, dict) else None
        if not isinstance(rows, list):
            raise ValueError("LLM batch response missing cards[]")

        out: dict[str, LlmCardEnrichment] = {}
        expected_ids = {c.internal_name for c in cards if c.internal_name}
        for row in rows:
            if not isinstance(row, dict):
                continue
            iid = str(row.get("internal_name") or "").strip()
            if not iid or iid not in expected_ids:
                continue
            parsed = LlmCardEnrichment.model_validate(row)
            parsed.tags = normalize_tag_list(parsed.tags, SUPPORTED_CARD_TAGS)
            parsed.synergy_tags = normalize_tag_list(parsed.synergy_tags, SUPPORTED_CARD_SYNERGY_TAGS)
            parsed.role_tags = normalize_tag_list(parsed.role_tags, SUPPORTED_CARD_ROLE_TAGS)
            if parsed.impact_level and parsed.impact_level.lower() in SUPPORTED_IMPACT_LEVELS:
                parsed.impact_level = parsed.impact_level.lower()
            else:
                parsed.impact_level = None
            parsed.confidence = max(0.0, min(1.0, float(parsed.confidence)))
            if parsed.upgrade_tier:
                t = parsed.upgrade_tier.strip().upper()
                parsed.upgrade_tier = t if t in {"D", "C", "B", "A", "S"} else None
            if parsed.enchantment_potential_tier:
                et = parsed.enchantment_potential_tier.strip().upper()
                parsed.enchantment_potential_tier = et if et in {"D", "C", "B", "A", "S"} else None
            clean_kind: dict[str, str] = {}
            for k, v in (parsed.enchantment_tier_by_kind or {}).items():
                nk = str(k).strip().lower()
                tv = str(v).strip().upper()
                if nk in {"attack", "block", "draw", "energy", "remove_exhaust"} and tv in {"D", "C", "B", "A", "S"}:
                    clean_kind[nk] = tv
            parsed.enchantment_tier_by_kind = clean_kind
            out[iid] = parsed

        # Ensure full coverage of requested batch.
        for iid in expected_ids:
            out.setdefault(iid, LlmCardEnrichment(confidence=0.0, inferred=True))
        return out

    def _chat(self, system: str, user: str) -> dict[str, Any]:
        key = os.environ.get(self.cfg.api_key_env, "")
        base = (self.cfg.base_url or "https://api.openai.com/v1").rstrip("/")
        url = f"{base}/chat/completions"
        headers: dict[str, str] = {
            "Authorization": f"Bearer {key}",
            "Content-Type": "application/json",
        }
        for hk, hv in (self.cfg.extra_headers or {}).items():
            if hv:
                headers[hk] = hv
        body = {
            "model": self.cfg.model,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
            "temperature": 0.2,
        }
        # Avoid long hangs on large runs: retry briefly on transient failures.
        last_err: Exception | None = None
        for attempt in range(3):
            try:
                r = requests.post(url, headers=headers, json=body, timeout=45)
                if r.status_code in (429, 500, 502, 503, 504):
                    last_err = RuntimeError(f"Transient LLM status {r.status_code}")
                    if attempt < 2:
                        time.sleep(1.5 * (attempt + 1))
                        continue
                r.raise_for_status()
                js = r.json()
                text = js["choices"][0]["message"]["content"]
                parsed = _extract_json_payload(text)
                if not isinstance(parsed, dict):
                    raise ValueError("LLM response must be a JSON object")
                return parsed
            except (requests.Timeout, requests.ConnectionError, ValueError, KeyError, RuntimeError) as e:
                last_err = e
                if attempt < 2:
                    time.sleep(1.5 * (attempt + 1))
                    continue
                raise
        if last_err:
            raise last_err
        raise RuntimeError("LLM request failed without explicit error")


def _extract_json_payload(text: str) -> Any:
    text = text.strip()
    if text.startswith("```"):
        lines = text.split("\n")
        text = "\n".join(lines[1:-1] if lines[-1].strip() == "```" else lines[1:])
    return json.loads(text)


def _chunk(items: list[RawCardRecord], size: int) -> list[list[RawCardRecord]]:
    if size <= 0:
        return [items]
    return [items[i : i + size] for i in range(0, len(items), size)]


class AnthropicEnricher(LlmEnricher):
    """Optional alternate provider — stub hooks for future Messages API."""

    def _chat(self, system: str, user: str) -> dict[str, Any]:
        raise NotImplementedError("Set provider to openai_compatible or implement AnthropicEnricher")


def build_enricher(cfg: LlmConfig) -> LlmEnricher:
    if cfg.provider == "anthropic":
        return AnthropicEnricher(cfg)
    # openrouter uses the same Chat Completions schema as OpenAI
    return LlmEnricher(cfg)

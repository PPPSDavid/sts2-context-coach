"""Supported tag vocabulary aligned with the coach evaluation engine."""

from __future__ import annotations

from typing import Iterable

# Core tags used by DeckAnalyzer / RecommendationEngine behavior.
ENGINE_CRITICAL_CARD_TAGS = {
    "attack",
    "block",
    "draw",
    "frontload",
    "scaling",
    "non_redundant_attack",
}

ENGINE_CRITICAL_ROLE_TAGS = {
    "block",
    "draw",
    "frontload",
    "scaling",
}

ENGINE_CRITICAL_SYNERGY_TAGS = {
    "strength",
    "exhaust",
}

# Broader metadata vocabulary supported by existing production JSON + coach usage.
SUPPORTED_CARD_TAGS = sorted(
    ENGINE_CRITICAL_CARD_TAGS
    | {
        "skill",
        "power",
        "debuff",
        "strength",
        "exhaust",
        "block_synergy",
        "discard",
        "vulnerable",
        "multi_hit",
        "efficient_block",
        "low_impact_attack",
    }
)

SUPPORTED_CARD_SYNERGY_TAGS = sorted(
    ENGINE_CRITICAL_SYNERGY_TAGS
    | {
        "attack",
        "block",
        "draw",
        "frontload",
        "scaling",
        "discard",
    }
)

SUPPORTED_CARD_ROLE_TAGS = sorted(
    ENGINE_CRITICAL_ROLE_TAGS
    | {
        "setup",
        "consistency",
        "sustain",
        "exhaust",
    }
)

# Relic tags are matched against card tags/roles/synergy tags in RecommendationEngine.
SUPPORTED_RELIC_TAGS = sorted(
    {
        "attack",
        "block",
        "draw",
        "frontload",
        "scaling",
        "strength",
        "exhaust",
        "discard",
    }
)

SUPPORTED_RELIC_SYNERGY_TAGS = SUPPORTED_RELIC_TAGS

SUPPORTED_IMPACT_LEVELS = ["low", "medium", "high"]


def normalize_tag_list(values: Iterable[str], allowed: set[str] | list[str]) -> list[str]:
    allowed_set = set(allowed)
    out: list[str] = []
    seen: set[str] = set()
    for raw in values:
        t = str(raw).strip().lower()
        if not t or t not in allowed_set or t in seen:
            continue
        seen.add(t)
        out.append(t)
    return out


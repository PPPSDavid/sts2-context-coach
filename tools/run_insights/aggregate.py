from __future__ import annotations

import json
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class CardRunStats:
    times_offered: int = 0
    times_picked: int = 0
    times_heuristic_top1: int = 0
    times_picked_when_heuristic_top1: int = 0
    example_decision_ids: list[str] = field(default_factory=list)


def load_card_names(cards_json: Path) -> set[str]:
    data = json.loads(cards_json.read_text(encoding="utf-8"))
    names: set[str] = set()
    for c in data.get("cards") or []:
        n = (c.get("internal_name") or "").strip()
        if n:
            names.add(n)
    return names


def aggregate_pick_stats(
    events: list[dict[str, Any]],
    *,
    known_cards: set[str] | None = None,
    max_examples: int = 5,
) -> dict[str, Any]:
    """
    Join decision + decision_choice (deck_diff_inferred) for card_reward.
    Shop and path decisions are counted only as 'offered' heuristics where applicable.
    """
    decisions: dict[str, dict[str, Any]] = {}
    for ev in events:
        if ev.get("event_type") != "decision":
            continue
        did = ev.get("decision_id")
        if not isinstance(did, str):
            continue
        decisions[did] = ev

    by_card: dict[str, CardRunStats] = defaultdict(CardRunStats)

    for ev in events:
        if ev.get("event_type") != "decision":
            continue
        dtype = (ev.get("decision_type") or "").strip().lower()
        if dtype not in ("card_reward", "shop"):
            continue
        opts = ev.get("candidate_options") or []
        if not isinstance(opts, list):
            continue
        for name in opts:
            if not isinstance(name, str) or not name.strip():
                continue
            nm = name.strip()
            if known_cards is not None and nm not in known_cards:
                continue
            by_card[nm].times_offered += 1
        rec = ev.get("recommended_choice")
        if isinstance(rec, str) and rec.strip():
            r = rec.strip()
            if known_cards is None or r in known_cards:
                by_card[r].times_heuristic_top1 += 1

    for ev in events:
        if ev.get("event_type") != "decision_choice":
            continue
        did = ev.get("decision_id")
        pick = ev.get("player_choice")
        if not isinstance(did, str) or not isinstance(pick, str) or not pick.strip():
            continue
        pick = pick.strip()
        if known_cards is not None and pick not in known_cards:
            continue
        parent = decisions.get(did)
        if parent is None:
            continue
        dtype = (parent.get("decision_type") or "").strip().lower()
        if dtype != "card_reward":
            continue
        st = by_card[pick]
        st.times_picked += 1
        rec = parent.get("recommended_choice")
        if isinstance(rec, str) and rec.strip() == pick:
            st.times_picked_when_heuristic_top1 += 1
        if len(st.example_decision_ids) < max_examples:
            st.example_decision_ids.append(did)

    cards_out: dict[str, dict[str, Any]] = {}
    for name, st in sorted(by_card.items(), key=lambda x: (-x[1].times_picked, x[0])):
        cards_out[name] = {
            "times_offered": st.times_offered,
            "times_picked": st.times_picked,
            "times_heuristic_top1": st.times_heuristic_top1,
            "times_picked_when_heuristic_top1": st.times_picked_when_heuristic_top1,
            "example_decision_ids": list(st.example_decision_ids),
        }

    return {
        "schema_version": 1,
        "summary": {
            "decision_events": sum(1 for e in events if e.get("event_type") == "decision"),
            "decision_choice_events": sum(1 for e in events if e.get("event_type") == "decision_choice"),
            "llm_coach_batch_events": sum(1 for e in events if e.get("event_type") == "llm_coach_batch"),
            "llm_deck_summary_events": sum(1 for e in events if e.get("event_type") == "llm_deck_summary"),
            "cards_tracked": len(cards_out),
        },
        "cards": cards_out,
        "notes": [
            "Discards are not emitted in telemetry today; this tool only infers reward picks via decision_choice.",
            "Shop purchases are not linked via decision_choice; shop rows contribute to times_offered / heuristic top only.",
        ],
    }


def write_insights(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")

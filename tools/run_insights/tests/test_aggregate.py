from __future__ import annotations

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.run_insights.aggregate import aggregate_pick_stats, load_card_names
from tools.run_insights.ingest import load_events


class TestAggregate(unittest.TestCase):
    def test_join_decision_and_choice(self) -> None:
        events = [
            {
                "event_type": "decision",
                "decision_id": "run-d0001",
                "decision_type": "card_reward",
                "candidate_options": ["CardA", "CardB"],
                "recommended_choice": "CardB",
            },
            {
                "event_type": "decision_choice",
                "decision_id": "run-d0001",
                "player_choice": "CardA",
                "resolution": "deck_diff_inferred",
            },
        ]
        r = aggregate_pick_stats(events, known_cards={"CardA", "CardB"})
        self.assertEqual(r["cards"]["CardA"]["times_picked"], 1)
        self.assertEqual(r["cards"]["CardB"]["times_heuristic_top1"], 1)
        self.assertEqual(r["cards"]["CardA"]["times_picked_when_heuristic_top1"], 0)

    def test_load_events_zip(self) -> None:
        lines = [
            json.dumps(
                {
                    "event_type": "decision",
                    "decision_id": "x-d0001",
                    "decision_type": "card_reward",
                    "candidate_options": ["OnlyOne"],
                    "recommended_choice": "OnlyOne",
                }
            ),
            json.dumps(
                {
                    "event_type": "decision_choice",
                    "decision_id": "x-d0001",
                    "player_choice": "OnlyOne",
                    "resolution": "deck_diff_inferred",
                }
            ),
        ]
        with tempfile.TemporaryDirectory() as td:
            zpath = Path(td) / "bundle.zip"
            with zipfile.ZipFile(zpath, "w") as z:
                z.writestr("run123/events.jsonl", "\n".join(lines) + "\n")
            ev = load_events(zpath)
            self.assertGreaterEqual(len(ev), 2)

    def test_load_card_names(self) -> None:
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False, encoding="utf-8") as f:
            json.dump({"cards": [{"internal_name": "ZTestCard"}]}, f)
            f.flush()
            p = Path(f.name)
        try:
            names = load_card_names(p)
            self.assertIn("ZTestCard", names)
        finally:
            p.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()

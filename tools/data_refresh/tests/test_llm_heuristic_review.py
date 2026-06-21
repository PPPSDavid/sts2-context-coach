from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

_ROOT = Path(__file__).resolve().parents[1]
if str(_ROOT) not in sys.path:
    sys.path.insert(0, str(_ROOT))

from llm_heuristic_review import (  # noqa: E402
    _accumulate_engine_score_reasons,
    _infer_acceptance_from_choices,
    _inline_decision_acceptance,
    _last_run_finished_status,
    _resolve_run_outcome,
    _summarize_runs,
)


class TestTelemetrySummaries(unittest.TestCase):
    def test_last_run_finished_status(self) -> None:
        ev = [
            {"event_type": "run_finished", "status": "defeat"},
            {"event_type": "ping"},
            {"event_type": "run_finished", "status": "victory"},
        ]
        self.assertEqual(_last_run_finished_status(ev), "victory")

    def test_resolve_run_outcome_prefers_events(self) -> None:
        summary = {"run_outcome": "active"}
        events = [{"event_type": "run_finished", "status": "victory"}]
        self.assertEqual(_resolve_run_outcome(summary=summary, events=events), "victory")

    def test_accumulate_engine_score_reasons(self) -> None:
        counts: dict[str, int] = {}
        events = [
            {
                "event_type": "decision",
                "engine_scores": {
                    "Bash": {
                        "score_breakdown": [
                            {"reason": "reason.strength_synergy", "weight": 5},
                            {"reason": "reason.expensive_low_energy", "weight": -5},
                        ]
                    }
                },
            }
        ]
        _accumulate_engine_score_reasons(events, counts)
        self.assertEqual(counts.get("reason.strength_synergy"), 1)
        self.assertEqual(counts.get("reason.expensive_low_energy"), 1)

    def test_infer_acceptance_from_choices(self) -> None:
        events = [
            {
                "event_type": "decision",
                "decision_id": "r-d0001",
                "recommended_choice": "Bash",
            },
            {
                "event_type": "decision_choice",
                "decision_id": "r-d0001",
                "player_choice": "Bash",
            },
            {
                "event_type": "decision",
                "decision_id": "r-d0002",
                "recommended_choice": "Strike",
            },
            {
                "event_type": "decision_choice",
                "decision_id": "r-d0002",
                "player_choice": "Defend",
            },
        ]
        acc, tot = _infer_acceptance_from_choices(events)
        self.assertEqual(tot, 2)
        self.assertEqual(acc, 1)

    def test_inline_decision_acceptance(self) -> None:
        events = [
            {
                "event_type": "decision",
                "player_choice": "Strike",
                "accepted_recommendation": True,
            },
            {"event_type": "decision", "accepted_recommendation": False},
        ]
        acc, tot = _inline_decision_acceptance(events)
        self.assertEqual(tot, 1)
        self.assertEqual(acc, 1)

    def test_summarize_runs_integration(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            run = Path(td) / "run-test-1"
            run.mkdir()
            events = [
                {"event_type": "decision", "decision_id": "d1", "recommended_choice": "A"},
                {"event_type": "decision_choice", "decision_id": "d1", "player_choice": "A"},
                {
                    "event_type": "decision",
                    "decision_id": "d2",
                    "engine_scores": {
                        "Z": {"score_breakdown": [{"reason": "reason.foo", "weight": 1}]}
                    },
                },
                {"event_type": "run_finished", "status": "defeat"},
            ]
            (run / "events.jsonl").write_text(
                "\n".join(json.dumps(e) for e in events) + "\n", encoding="utf-8"
            )
            (run / "summary.json").write_text(
                json.dumps(
                    {"run_outcome": "active", "final_state": {"character": "X", "ascension": 2}}
                ),
                encoding="utf-8",
            )
            (run / "metadata.json").write_text(
                json.dumps({"character": "IRONCLAD"}), encoding="utf-8"
            )
            out = _summarize_runs(logs_dir=Path(td), runs_limit=10)
            self.assertEqual(out["runs_count"], 1)
            self.assertEqual(out["runs"][0]["run_outcome"], "defeat")
            self.assertEqual(out["runs"][0]["character"], "IRONCLAD")
            self.assertEqual(
                out["accepted_recommendation_source"], "decision_choice_vs_recommended"
            )
            self.assertEqual(out["accepted_recommendation_rate"], 1.0)
            keys = {x["key"] for x in out["top_reason_keys"]}
            self.assertIn("reason.foo", keys)


if __name__ == "__main__":
    unittest.main()

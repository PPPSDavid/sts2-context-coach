from __future__ import annotations

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from tools.run_insights.aggregate import aggregate_pick_stats, load_card_names
from tools.run_insights.ingest import (
    build_run_context_from_filesystem,
    collect_run_sidecars_from_zip,
    load_events,
    resolve_run_directory_for_sidecars,
)


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

    def test_resolve_run_directory_for_sidecars(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            root = Path(td)
            run_dir = root / "run-abc"
            run_dir.mkdir()
            (run_dir / "events.jsonl").write_text("{}\n", encoding="utf-8")
            self.assertEqual(resolve_run_directory_for_sidecars(run_dir), run_dir.resolve())
            self.assertEqual(
                resolve_run_directory_for_sidecars(run_dir / "events.jsonl"), run_dir.resolve()
            )
            self.assertIsNone(resolve_run_directory_for_sidecars(root / "nope.jsonl"))

    def test_build_run_context_from_filesystem(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            run_dir = Path(td) / "run-xyz"
            run_dir.mkdir(parents=True)
            (run_dir / "events.jsonl").write_text(
                json.dumps({"event_type": "ping"}) + "\n", encoding="utf-8"
            )
            (run_dir / "metadata.json").write_text(
                json.dumps({"run_id": "run-xyz", "character": "Silent", "ascension": 2}),
                encoding="utf-8",
            )
            (run_dir / "summary.json").write_text(
                json.dumps({"run_id": "run-xyz", "run_outcome": "victory"}),
                encoding="utf-8",
            )
            ctx = build_run_context_from_filesystem(run_dir / "events.jsonl")
            assert ctx is not None
            self.assertEqual(ctx["run_id"], "run-xyz")
            self.assertEqual(ctx["metadata"].get("character"), "Silent")
            self.assertEqual(ctx["summary"].get("run_outcome"), "victory")

    def test_collect_run_sidecars_from_zip(self) -> None:
        lines = [
            json.dumps(
                {"event_type": "decision", "decision_id": "d1", "decision_type": "card_reward"}
            ),
        ]
        with tempfile.TemporaryDirectory() as td:
            zpath = Path(td) / "export.zip"
            with zipfile.ZipFile(zpath, "w") as z:
                z.writestr("runA/events.jsonl", "\n".join(lines) + "\n")
                z.writestr(
                    "runA/metadata.json",
                    json.dumps({"run_id": "runA", "character": "Defect"}),
                )
                z.writestr("runA/summary.json", json.dumps({"run_outcome": "death"}))
            rows = collect_run_sidecars_from_zip(zpath)
            self.assertEqual(len(rows), 1)
            self.assertEqual(rows[0]["run_id"], "runA")
            self.assertEqual(rows[0]["metadata"].get("character"), "Defect")
            self.assertEqual(rows[0]["summary"].get("run_outcome"), "death")


if __name__ == "__main__":
    unittest.main()

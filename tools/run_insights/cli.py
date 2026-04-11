from __future__ import annotations

import argparse
from pathlib import Path

from tools.run_insights.aggregate import aggregate_pick_stats, load_card_names, write_insights
from tools.run_insights.ingest import load_events


def default_cards_path() -> Path:
    return Path(__file__).resolve().parents[2] / "Data" / "cards.json"


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(
        description="Read Context Coach events.jsonl (or export ZIP) and write staging pick-stats JSON."
    )
    p.add_argument(
        "--input",
        "-i",
        required=True,
        help="Path to events.jsonl, a run directory containing events.jsonl, or an export .zip",
    )
    p.add_argument(
        "--cards",
        "-c",
        type=Path,
        default=None,
        help="cards.json for filtering unknown internal names (default: repo Data/cards.json)",
    )
    p.add_argument(
        "--out",
        "-o",
        type=Path,
        default=Path("tools/run_insights/out/insights.json"),
        help="Output JSON path",
    )
    p.add_argument(
        "--no-card-filter",
        action="store_true",
        help="Do not filter against cards.json (include all candidate strings)",
    )
    args = p.parse_args(argv)

    events = load_events(Path(args.input))
    cards_path = args.cards or default_cards_path()
    known = None if args.no_card_filter else load_card_names(cards_path)
    payload = aggregate_pick_stats(events, known_cards=known)
    payload["source"] = {"input": str(Path(args.input).resolve())}
    if known is not None:
        payload["source"]["cards_json"] = str(cards_path.resolve())
    write_insights(Path(args.out), payload)
    print(f"Wrote {args.out} ({payload['summary']['cards_tracked']} cards)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

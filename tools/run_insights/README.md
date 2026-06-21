# run_insights

Standalone analysis for exported Context Coach telemetry (`events.jsonl` or an **Export & Share** ZIP).

## Usage

From the repo root (so `Data/cards.json` resolves):

```bash
set PYTHONPATH=.
python -m tools.run_insights --input path/to/events.jsonl --out tools/run_insights/out/my-insights.json
```

- **ZIP**: `--input` may point to `sts2-context-coach-*.zip`; every `*/events.jsonl` inside is merged.
- **Filtering**: by default, candidate names are kept only if they appear in `Data/cards.json`. Use `--no-card-filter` to include unknown strings.
- **LLM rows**: the summary counts `llm_coach_batch` / `llm_deck_summary` lines (see `contextcoach.config` and in-game logging).

## Tests

```bash
set PYTHONPATH=.
python -m unittest discover -s tools/run_insights/tests -p "test_*.py" -v
```

## Limits

Reward picks are inferred from `decision_choice` with `resolution=deck_diff_inferred`. Shop purchases and discards are not fully modeled in telemetry yet; see `notes` in the output JSON.

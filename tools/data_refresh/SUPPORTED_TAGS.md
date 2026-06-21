# Supported Tag Vocabulary (Coach Engine)

This document defines the allowed metadata tags for refresh + LLM enrichment, aligned to the current evaluation logic in:

- `Scoring/DeckAnalyzer.cs`
- `Scoring/RecommendationEngine.cs`
- `Scoring/CardHeuristics.cs`

## Engine-Critical Card Tags

These are read directly by the scoring engine and should be treated as canonical:

- `attack`
- `block`
- `draw`
- `frontload`
- `scaling`
- `non_redundant_attack`

## Engine-Critical Card Role Tags

- `block`
- `draw`
- `frontload`
- `scaling`

## Engine-Critical Card Synergy Tags

- `strength`
- `exhaust`

## Extended Card Tags (Supported in metadata)

This tool allows these additional card tags for richer annotation:

- `attack`
- `skill`
- `power`
- `debuff`
- `draw`
- `block`
- `scaling`
- `strength`
- `exhaust`
- `block_synergy`
- `discard`
- `vulnerable`
- `multi_hit`
- `efficient_block`
- `low_impact_attack`
- `frontload`
- `non_redundant_attack`

## Supported Card Synergy Tags

- `strength`
- `exhaust`
- `attack`
- `block`
- `draw`
- `frontload`
- `scaling`
- `discard`

## Supported Card Role Tags

- `block`
- `draw`
- `frontload`
- `scaling`
- `setup`
- `consistency`
- `sustain`
- `exhaust`

## Supported Impact Levels

- `low`
- `medium`
- `high`

## Supported Relic Tags

Relic tags/synergy tags should overlap card-side tags because relic metadata is matched against candidate card tags in `RecommendationEngine.ScoreRelicMetadataSynergy`.

- `attack`
- `block`
- `draw`
- `frontload`
- `scaling`
- `strength`
- `exhaust`
- `discard`

## Notes

- LLM output is filtered to this vocabulary by `tools/data_refresh/tag_vocabulary.py`.
- Tags outside this set are discarded during enrichment normalization.

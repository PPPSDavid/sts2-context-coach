# Heuristic Proposals

- Generated: 2026-04-12T01:14:32Z
- LLM used: True
- Runs sampled: 5
- Events sampled: 6141

## Proposals

### tune_high_cost_penalty - Adjust High Cost Penalty Weight
- Status: needs_review
- Target: `RecommendationEngine.cs` :: `WeightHighCostPen`
- Type: `weight_tuning` | Risk: `low` | Confidence: `0.8`
- Proposed change: Reduce the penalty weight from 6f to 5f.
- Expected effect: Lower the negative impact of high-cost cards, potentially increasing their selection rate.

### tune_strength_synergy_weight - Adjust Strength Synergy Weight
- Status: needs_review
- Target: `RecommendationEngine.cs` :: `WeightStrengthSyn`
- Type: `weight_tuning` | Risk: `low` | Confidence: `0.75`
- Proposed change: Increase the weight from 5f to 6f.
- Expected effect: Encourage players to select strength synergy cards more frequently, enhancing deck synergy.

### tune_draw_weight - Adjust Draw Weight
- Status: needs_review
- Target: `RecommendationEngine.cs` :: `WeightNeedsDraw`
- Type: `weight_tuning` | Risk: `medium` | Confidence: `0.65`
- Proposed change: Decrease the weight from 7f to 6f.
- Expected effect: Reduce the emphasis on draw cards, allowing for more diverse deck strategies.


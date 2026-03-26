using Sts2ContextCoach.Data;
using Sts2ContextCoach.Diagnostics;
using Sts2ContextCoach.State;

namespace Sts2ContextCoach.Scoring;


public static class RecommendationEngine
{
    private const int HighCostThreshold = 2;
    private const int LowEnergyThreshold = 3;

    private const float WeightNeedsBlock = 8f;
    private const float WeightNeedsFrontload = 7f;
    private const float WeightNeedsDraw = 7f;
    private const float WeightNeedsScaling = 7f;
    private const float WeightGoodFrontload = 4f;
    private const float WeightGoodScaling = 4f;
    private const float WeightStrengthSyn = 5f;
    private const float WeightExhaustSyn = 5f;
    private const float WeightHighCostPen = 6f;
    private const float WeightRedundantAttack = 7f;
    private static readonly Dictionary<string, float> UpgradeTierWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D"] = 0.8f,
        ["C"] = 1.8f,
        ["B"] = 3.2f,
        ["A"] = 5.0f,
        ["S"] = 7.2f
    };
    private static readonly Dictionary<string, float> EnchantmentExpectedTierWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D"] = 0.2f,
        ["C"] = 0.5f,
        ["B"] = 0.9f,
        ["A"] = 1.4f,
        ["S"] = 2.0f
    };
    private static readonly Dictionary<string, float> EnchantmentRealizedTierWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D"] = 0.8f,
        ["C"] = 1.6f,
        ["B"] = 2.8f,
        ["A"] = 4.2f,
        ["S"] = 6.0f
    };

    public static ScoreResult ScoreCard(
        string internalName,
        bool upgraded,
        int? cardCost,
        GameState state,
        ShopEconomyContext? shopEconomy = null,
        float augmentScoreBonus = 0f,
        string? augmentReasonKey = null)
    {
        var hasRow = CardDatabase.TryGetBaseScore(internalName, out var baseScore);
        if (!hasRow)
            baseScore = 35f;

        var ctx = baseScore;
        var reasons = new List<(string key, float weight)>();

        MetadataRepository.TryGetCard(internalName, out var cardMeta);
        ApplyUpgradeScoring(upgraded, cardMeta, ref ctx, reasons);

        var deck = state.Deck;
        var deckSize = deck?.Count ?? 0;

        if (deckSize > 10)
        {
            var pen = -MathF.Min(12f, (deckSize - 10) * 0.6f);
            ctx += pen;
            reasons.Add(("reason.deck_pressure", pen));
        }

        var analysis = DeckAnalyzer.Analyze(state);
        var cand = DeckAnalyzer.EvaluateCandidate(internalName);
        var ironclad = IsIronclad(state);

        if (deck != null && deck.Count > 0)
        {
            if (analysis.BlockNeed > 0.05f && cand.ProvidesBlock)
            {
                var w = analysis.BlockNeed * WeightNeedsBlock;
                ctx += w;
                reasons.Add(("reason.needs_block", w));
            }

            if (analysis.FrontloadNeed > 0.05f && cand.ProvidesFrontload)
            {
                var w = analysis.FrontloadNeed * WeightNeedsFrontload;
                ctx += w;
                reasons.Add(("reason.needs_frontload", w));
            }

            if (analysis.DrawNeed > 0.05f && cand.ProvidesDraw)
            {
                var w = analysis.DrawNeed * WeightNeedsDraw;
                ctx += w;
                reasons.Add(("reason.needs_draw", w));
            }

            if (analysis.ScalingNeed > 0.05f && cand.ProvidesScaling)
            {
                var w = analysis.ScalingNeed * WeightNeedsScaling;
                ctx += w;
                reasons.Add(("reason.needs_scaling", w));
            }

            if (cand.HighImpactFrontload && analysis.FrontloadNeed > 0.15f)
            {
                var w = WeightGoodFrontload * analysis.FrontloadNeed;
                ctx += w;
                reasons.Add(("reason.good_frontload", w));
            }

            if (cand.HighImpactScaling && analysis.ScalingNeed > 0.15f)
            {
                var w = WeightGoodScaling * analysis.ScalingNeed;
                ctx += w;
                reasons.Add(("reason.good_scaling", w));
            }

            var strMul = ironclad ? 1f : 0.4f;
            if (analysis.HasStrengthSynergy && cand.StrengthSynergy)
            {
                var w = WeightStrengthSyn * strMul;
                ctx += w;
                reasons.Add(("reason.strength_synergy", w));
            }

            var exhMul = ironclad ? 1f : 0.4f;
            if (analysis.HasExhaustSynergy && cand.ExhaustSynergy)
            {
                var w = WeightExhaustSyn * exhMul;
                ctx += w;
                reasons.Add(("reason.exhaust_synergy", w));
            }

            if (analysis.HighCostPressure > 0.05f && cand.IsHighCost)
            {
                var w = -analysis.HighCostPressure * WeightHighCostPen;
                ctx += w;
                reasons.Add(("reason.too_many_high_cost_cards", w));
            }

            if (analysis.AttackSpamPressure > 0.05f && cand.IsRedundantAttack)
            {
                var w = -analysis.AttackSpamPressure * WeightRedundantAttack;
                ctx += w;
                reasons.Add(("reason.redundant_attack", w));
            }

            var hasDiscardSupport = deck.Any(c => CardHeuristics.LooksLikeDiscardSynergy(c.Name));
            if (hasDiscardSupport && CardHeuristics.LooksLikeDiscardSynergy(internalName))
            {
                var bonus = 5f;
                ctx += bonus;
                reasons.Add(("reason.discard_synergy", bonus));
            }
        }

        ctx += ScoreRelicMetadataSynergy(state, internalName, reasons);

        var relics = state.Relics;
        if (relics != null)
        {
            var shurikenLike = relics.Any(r => r.Contains("Shuriken", StringComparison.OrdinalIgnoreCase));
            if (shurikenLike && CardHeuristics.LooksLikeAttack(internalName))
            {
                var bonus = 4f;
                ctx += bonus;
                reasons.Add(("reason.relic_attack_synergy", bonus));
            }

            var vajraLike = relics.Any(r => r.Contains("Vajra", StringComparison.OrdinalIgnoreCase));
            if (vajraLike && CardHeuristics.LooksLikeAttack(internalName))
            {
                var bonus = 3f;
                ctx += bonus;
                reasons.Add(("reason.relic_strength_attack", bonus));
            }
        }

        var maxEnergy = state.MaxEnergy ?? 3;
        var cost = cardCost ?? cand.EffectiveCost;
        if (cost >= HighCostThreshold && maxEnergy <= LowEnergyThreshold)
        {
            var pen = -5f;
            ctx += pen;
            reasons.Add(("reason.expensive_low_energy", pen));
        }

        var ctxBeforeEconomy = ctx;
        ApplyShopEconomy(internalName, state, analysis, baseScore, ctxBeforeEconomy, ref ctx, reasons, shopEconomy);

        ApplyEnchantmentScoring(cardMeta, augmentScoreBonus, augmentReasonKey, ref ctx, reasons);

        if (!hasRow)
            reasons.Add(("reason.base_fallback", 0.1f));

        if (reasons.Count == 0)
            reasons.Add(("reason.default", 0f));

        var topReasons = reasons
            .GroupBy(r => r.key, StringComparer.Ordinal)
            .Select(g => (key: g.Key, weight: g.Sum(x => x.weight)))
            .OrderByDescending(r => MathF.Abs(r.weight))
            .Take(3)
            .ToList();

        return new ScoreResult
        {
            BaseScore = baseScore,
            ContextScore = ctx,
            ReasonKeys = topReasons.Select(r => r.key).ToList(),
            ReasonWeights = topReasons.Select(r => r.weight).ToList()
        };
    }

    private static float ScoreRelicMetadataSynergy(GameState state, string candidateName, List<(string key, float weight)> reasons)
    {
        var relics = state.Relics;
        if (relics == null || relics.Count == 0) return 0f;

        MetadataRepository.TryGetCard(candidateName, out var cardMeta);
        var candTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cardMeta != null)
        {
            // Use explicit synergy tags only; role/tags are intentionally broad and often noisy.
            foreach (var t in cardMeta.SynergyTags) candTags.Add(t);
        }

        if (candTags.Count == 0) return 0f;

        var total = 0f;
        foreach (var r in relics)
        {
            if (!MetadataRepository.TryGetRelic(r, out var rel) || rel is null || rel.SynergyTags.Count == 0)
                continue;
            // Data refresh may emit generic catch-all tags for unknown relics (e.g., starter/placeholder rows).
            // Ignore very broad relic tag sets to avoid noisy false-positive "relic synergy" reasons.
            if (rel.SynergyTags.Count > 4)
                continue;

            foreach (var tag in rel.SynergyTags)
            {
                if (!candTags.Contains(tag))
                    continue;

                total += 2.5f;
            }
        }

        // Require at least two tag overlaps before surfacing this as a top reason.
        // This avoids noisy "matches relic tags" labels from weak single-tag matches.
        if (total >= 5f)
            reasons.Add(("reason.relic_synergy", total));

        return total >= 5f ? total : 0f;
    }

    private static void ApplyShopEconomy(
        string internalName,
        GameState state,
        DeckAnalysis analysis,
        float baseScore,
        float ctxBeforeEconomy,
        ref float ctx,
        List<(string key, float weight)> reasons,
        ShopEconomyContext? shop)
    {
        if (ContextCoachLogging.Verbose)
        {
            if (shop is null)
                ContextCoachLogging.VerboseInfo(
                    $"shop-econ card={internalName} skipped: shop context null (overlay did not pass shop probe)");
            else if (!shop.Value.HasCardPrice)
                ContextCoachLogging.VerboseInfo(
                    $"shop-econ card={internalName} skipped: no parsed price " +
                    $"(cardPrice={(shop.Value.CardPrice?.ToString() ?? "null")}, discounted={shop.Value.IsDiscounted}, " +
                    $"removal={(shop.Value.RemovalServicePrice?.ToString() ?? "null")}, gold={state.Gold?.ToString() ?? "?"})");
        }

        if (shop is null || !shop.Value.HasCardPrice)
            return;

        var eco = shop.Value;
        var price = eco.CardPrice!.Value;
        var gold = state.Gold;
        var deckSize = state.Deck?.Count ?? 0;
        var canPay = !gold.HasValue || gold.Value >= price;
        var ctxAtStart = ctx;

        if (gold.HasValue && gold.Value < price)
        {
            const float w = 22f;
            ctx -= w;
            reasons.Add(("reason.cannot_afford", -w));
        }

        if (gold.HasValue && gold.Value >= price)
        {
            var after = gold.Value - price;
            if (after is >= 0 and < 18)
            {
                const float w = 4.5f;
                ctx -= w;
                reasons.Add(("reason.tight_gold", -w));
            }
        }

        if (eco.IsDiscounted && canPay)
        {
            const float w = 5.5f;
            ctx += w;
            reasons.Add(("reason.shop_discount", w));
        }

        if (canPay && gold.HasValue && gold.Value > 0)
        {
            var ratio = price / (float)gold.Value;
            if (ratio <= 0.14f)
            {
                var w = 4f + (1f - ratio) * 8f;
                ctx += w;
                reasons.Add(("reason.good_shop_value", w));
            }

            if (gold.Value >= 300 && gold.Value >= price * 2.1f)
            {
                const float w = 2.5f;
                ctx += w;
                reasons.Add(("reason.plenty_of_gold", w));
            }
        }

        var removal = eco.RemovalServicePrice;
        if (removal.HasValue && deckSize >= 13 && gold.HasValue &&
            gold.Value >= price && gold.Value >= removal.Value)
        {
            var bloated = deckSize >= 15 || analysis.HighCostPressure > 0.2f;
            var marginal = baseScore < 39f || ctxBeforeEconomy < 43f;
            if (bloated && marginal && price > removal.Value * 1.05f)
            {
                const float w = 6f;
                ctx -= w;
                reasons.Add(("reason.consider_removal", -w));
            }
        }

        if (ContextCoachLogging.Verbose)
        {
            var delta = ctx - ctxAtStart;
            var ratioStr = gold is int gv && gv > 0 ? $"{price / (float)gv:F3}" : "?";
            var skipGoodValue = !canPay || !gold.HasValue || gold.Value <= 0 || price / (float)gold.Value > 0.14f;
            var skipPlenty = !canPay || !gold.HasValue || gold.Value < 300 || gold.Value < price * 2.1f;
            var considerRemovalDetail =
                removal is { } rm
                    ? $"removal={rm} deck={deckSize} bloated={(deckSize >= 15 || analysis.HighCostPressure > 0.2f)} " +
                      $"marginal={(baseScore < 39f || ctxBeforeEconomy < 43f)} price>rem*1.05={price > rm * 1.05f}"
                    : "removal=null";
            ContextCoachLogging.VerboseInfo(
                $"shop-econ card={internalName} price={price} sale={eco.IsDiscounted} gold={gold?.ToString() ?? "?"} " +
                $"canPay={canPay} ratio={ratioStr} skipGoodShopValue={skipGoodValue} skipPlentyGold={skipPlenty} " +
                $"{considerRemovalDetail} ctxDelta={delta:F2}");
        }
    }

    private static void ApplyUpgradeScoring(
        bool upgraded,
        CardMetadataDto? cardMeta,
        ref float ctx,
        List<(string key, float weight)> reasons)
    {
        if (!upgraded || cardMeta == null) return;

        var total = 0f;
        if (!string.IsNullOrWhiteSpace(cardMeta.UpgradeTier) &&
            UpgradeTierWeights.TryGetValue(cardMeta.UpgradeTier.Trim(), out var tierW))
        {
            total += tierW;
            reasons.Add(("reason.upgrade_tier", tierW));
        }

        if (cardMeta.UpgradeCostDelta is int costDelta && costDelta < 0)
            total += Math.Abs(costDelta) * 2.0f;
        if (cardMeta.UpgradeBlockDelta is int blkDelta && blkDelta > 0)
            total += MathF.Min(6f, blkDelta * 0.7f);
        if (cardMeta.UpgradeDrawDelta is int drawDelta && drawDelta > 0)
            total += MathF.Min(7f, drawDelta * 2.4f);
        if (cardMeta.UpgradeDamageDelta is int dmgDelta && dmgDelta > 0)
            total += MathF.Min(6f, dmgDelta * 0.45f);
        if (cardMeta.UpgradeRemovesExhaust is true)
            total += 8f;
        if (cardMeta.UpgradeMajor is true)
            total += 3.5f;

        if (total > 0.25f)
        {
            ctx += total;
            reasons.Add(("reason.upgrade_bonus", total));
        }
    }

    private static void ApplyEnchantmentScoring(
        CardMetadataDto? cardMeta,
        float augmentScoreBonus,
        string? augmentReasonKey,
        ref float ctx,
        List<(string key, float weight)> reasons)
    {
        // Channel 1: probability-discounted future upside (always modest).
        if (cardMeta?.EnchantmentPotentialTier is { } expectedTier &&
            EnchantmentExpectedTierWeights.TryGetValue(expectedTier.Trim(), out var ew))
        {
            ctx += ew;
            reasons.Add(("reason.enchantment_expected", ew));
        }

        if (string.IsNullOrWhiteSpace(augmentReasonKey))
            return;

        // Channel 2: realized value when an enchantment is actually present right now.
        var kind = AugmentKindFromReasonKey(augmentReasonKey);
        var realized = 0f;
        if (kind != null &&
            cardMeta?.EnchantmentTierByKind != null &&
            cardMeta.EnchantmentTierByKind.TryGetValue(kind, out var tier) &&
            EnchantmentRealizedTierWeights.TryGetValue((tier ?? "").Trim(), out var rw))
        {
            realized = rw;
        }
        else if (augmentScoreBonus > 0.01f)
        {
            // Fallback to runtime heuristics when no per-card LLM tier exists yet.
            realized = MathF.Min(3.6f, augmentScoreBonus);
        }

        if (realized > 0.01f)
        {
            ctx += realized;
            reasons.Add((augmentReasonKey, realized));
        }
    }

    private static string? AugmentKindFromReasonKey(string reasonKey)
    {
        if (reasonKey.EndsWith("augment_remove_exhaust", StringComparison.OrdinalIgnoreCase)) return "remove_exhaust";
        if (reasonKey.EndsWith("augment_attack", StringComparison.OrdinalIgnoreCase)) return "attack";
        if (reasonKey.EndsWith("augment_draw", StringComparison.OrdinalIgnoreCase)) return "draw";
        if (reasonKey.EndsWith("augment_energy", StringComparison.OrdinalIgnoreCase)) return "energy";
        if (reasonKey.EndsWith("augment_block", StringComparison.OrdinalIgnoreCase)) return "block";
        return null;
    }

    private static bool IsIronclad(GameState state)
    {
        var c = state.Character;
        if (string.IsNullOrEmpty(c)) return true;

        return c.Contains("Ironclad", StringComparison.OrdinalIgnoreCase)
               || c.Contains("Iron", StringComparison.OrdinalIgnoreCase);
    }
}

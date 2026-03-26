using Sts2ContextCoach.Data;
using Sts2ContextCoach.State;

namespace Sts2ContextCoach.Scoring;

/// <summary>Builds <see cref="DeckAnalysis"/> from the current deck using JSON metadata when present, else string heuristics.</summary>
public static class DeckAnalyzer
{
    public static CardCandidateTraits EvaluateCandidate(string internalName)
    {
        MetadataRepository.TryGetCard(internalName, out var meta);
        var cost = ResolveCost(internalName, meta);
        var highImpact = string.Equals(meta?.ImpactLevel, "high", StringComparison.OrdinalIgnoreCase);

        return new CardCandidateTraits
        {
            EffectiveCost = cost,
            ProvidesBlock = IsBlock(internalName, meta),
            ProvidesDraw = IsDraw(internalName, meta),
            ProvidesFrontload = IsFrontload(internalName, meta),
            ProvidesScaling = IsScaling(internalName, meta),
            StrengthSynergy = CardHeuristics.LooksLikeStrengthSynergy(internalName) ||
                              HasSyn(meta, "strength"),
            ExhaustSynergy = CardHeuristics.LooksLikeExhaustSynergy(internalName) ||
                             HasSyn(meta, "exhaust"),
            IsHighCost = cost >= 2,
            IsRedundantAttack = IsRedundantAttack(internalName, meta),
            HighImpactFrontload = highImpact && IsFrontload(internalName, meta),
            HighImpactScaling = highImpact && IsScaling(internalName, meta)
        };
    }

    private static bool HasSyn(CardMetadataDto? meta, string token)
    {
        return meta != null && meta.SynergyTags.Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    private const int BlockSatisfiedAt = 3;
    private const int DrawSatisfiedAt = 3;
    private const int FrontloadSatisfiedAt = 3;
    private const int ScalingSatisfiedAt = 2;
    private const int HighCostPainAt = 5;
    private const int AttackSpamPainAt = 9;

    public static DeckAnalysis Analyze(GameState state)
    {
        var deck = state.Deck;
        var size = deck?.Count ?? 0;
        if (size == 0)
            return Empty();

        var block = 0;
        var draw = 0;
        var frontload = 0;
        var scaling = 0;
        var highCost = 0;
        var attacks = 0;
        var redundantAttacks = 0;
        var strSyn = false;
        var exhSyn = false;

        foreach (var c in deck!)
        {
            var name = c.Name;
            MetadataRepository.TryGetCard(name, out var meta);

            if (IsBlock(name, meta)) block++;
            if (IsDraw(name, meta)) draw++;
            if (IsFrontload(name, meta)) frontload++;
            if (IsScaling(name, meta)) scaling++;
            if (IsHighCost(name, meta)) highCost++;

            if (IsAttack(name, meta))
            {
                attacks++;
                if (IsRedundantAttack(name, meta))
                    redundantAttacks++;
            }

            if (HasStrengthSynergy(name, meta)) strSyn = true;
            if (HasExhaustSynergy(name, meta)) exhSyn = true;
        }

        var blockNeed = NeedGap(block, BlockSatisfiedAt);
        var frontNeed = NeedGap(frontload, FrontloadSatisfiedAt);
        var drawNeed = NeedGap(draw, DrawSatisfiedAt);
        var scalingNeed = NeedGap(scaling, ScalingSatisfiedAt);

        var hcPressure = PressureOver(highCost, HighCostPainAt);
        var atkSpam = PressureOver(redundantAttacks, AttackSpamPainAt);

        return new DeckAnalysis
        {
            DeckSize = size,
            BlockCardCount = block,
            DrawCardCount = draw,
            FrontloadCardCount = frontload,
            ScalingCardCount = scaling,
            HighCostCardCount = highCost,
            AttackCardCount = attacks,
            RedundantAttackCount = redundantAttacks,
            HasStrengthSynergy = strSyn,
            HasExhaustSynergy = exhSyn,
            BlockNeed = blockNeed,
            FrontloadNeed = frontNeed,
            DrawNeed = drawNeed,
            ScalingNeed = scalingNeed,
            HighCostPressure = hcPressure,
            AttackSpamPressure = atkSpam
        };
    }

    private static DeckAnalysis Empty() => new()
    {
        BlockNeed = 0.5f,
        FrontloadNeed = 0.4f,
        DrawNeed = 0.4f,
        ScalingNeed = 0.4f
    };

    private static float NeedGap(int have, int want)
    {
        if (have >= want) return 0f;
        return MathF.Min(1f, (want - have) / (float)want);
    }

    private static float PressureOver(int amount, int threshold, float scale = 0.25f)
    {
        if (amount < threshold) return 0f;
        return MathF.Min(1f, (amount - threshold + 1) * scale);
    }

    private static bool IsBlock(string name, CardMetadataDto? meta)
    {
        if (meta != null && (HasRole(meta, "block") || HasTag(meta, "block")))
            return true;

        return CardHeuristics.LooksLikeBlockCard(name);
    }

    private static bool IsDraw(string name, CardMetadataDto? meta)
    {
        // Prefer precision over recall: broad metadata "draw" labels can be noisy.
        // Treat metadata-only draw as valid only for explicit draw-specific tags.
        if (meta != null)
        {
            if (HasTag(meta, "card_draw") || HasRole(meta, "card_draw"))
                return true;
        }

        return CardHeuristics.LooksLikeDrawCard(name);
    }

    private static bool IsFrontload(string name, CardMetadataDto? meta)
    {
        if (meta != null && (HasRole(meta, "frontload") || HasTag(meta, "frontload")))
            return true;

        return CardHeuristics.LooksLikeFrontloadCard(name);
    }

    private static bool IsScaling(string name, CardMetadataDto? meta)
    {
        if (meta != null && (HasRole(meta, "scaling") || HasTag(meta, "scaling")))
            return true;

        return CardHeuristics.LooksLikeScalingCard(name);
    }

    private static bool IsHighCost(string name, CardMetadataDto? meta)
    {
        var cost = ResolveCost(name, meta);
        return cost >= 2;
    }

    private static bool IsAttack(string name, CardMetadataDto? meta)
    {
        if (meta != null && HasTag(meta, "attack"))
            return true;

        return CardHeuristics.LooksLikeAttack(name);
    }

    private static bool IsRedundantAttack(string name, CardMetadataDto? meta)
    {
        if (meta != null && meta.Tags.Any(t => t.Equals("non_redundant_attack", StringComparison.OrdinalIgnoreCase)))
            return false;

        return CardHeuristics.LooksLikeGenericAttack(name);
    }

    private static bool HasStrengthSynergy(string name, CardMetadataDto? meta)
    {
        if (meta != null && HasSyn(meta, "strength"))
            return true;

        return CardHeuristics.LooksLikeStrengthSynergy(name);
    }

    private static bool HasExhaustSynergy(string name, CardMetadataDto? meta)
    {
        if (meta != null && HasSyn(meta, "exhaust"))
            return true;

        return CardHeuristics.LooksLikeExhaustSynergy(name);
    }

    private static int ResolveCost(string name, CardMetadataDto? meta)
    {
        if (meta?.Cost is int c) return c;
        return CardHeuristics.HeuristicCost(name);
    }

    private static bool HasTag(CardMetadataDto meta, string token)
    {
        return meta.Tags.Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasRole(CardMetadataDto meta, string token)
    {
        return meta.RoleTags.Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase));
    }
}

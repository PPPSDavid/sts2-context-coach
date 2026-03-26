namespace Sts2ContextCoach.Scoring;

/// <summary>Per-card signals for scoring a reward pick (metadata + heuristics).</summary>
public readonly struct CardCandidateTraits
{
    public int EffectiveCost { get; init; }
    public bool ProvidesBlock { get; init; }
    public bool ProvidesDraw { get; init; }
    public bool ProvidesFrontload { get; init; }
    public bool ProvidesScaling { get; init; }
    public bool StrengthSynergy { get; init; }
    public bool ExhaustSynergy { get; init; }
    public bool IsHighCost { get; init; }
    public bool IsRedundantAttack { get; init; }
    public bool HighImpactFrontload { get; init; }
    public bool HighImpactScaling { get; init; }
}

/// <summary>Derived deck statistics for contextual scoring (Ironclad-focused weights in <see cref="DeckAnalyzer"/>).</summary>
public sealed class DeckAnalysis
{
    public int DeckSize { get; init; }

    public int BlockCardCount { get; init; }
    public int DrawCardCount { get; init; }
    public int FrontloadCardCount { get; init; }
    public int ScalingCardCount { get; init; }
    public int HighCostCardCount { get; init; }
    public int AttackCardCount { get; init; }
    public int RedundantAttackCount { get; init; }

    public bool HasStrengthSynergy { get; init; }
    public bool HasExhaustSynergy { get; init; }

    /// <summary>0 = saturated, 1 = urgent gap.</summary>
    public float BlockNeed { get; init; }
    public float FrontloadNeed { get; init; }
    public float DrawNeed { get; init; }
    public float ScalingNeed { get; init; }

    public float HighCostPressure { get; init; }
    public float AttackSpamPressure { get; init; }
}

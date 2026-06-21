namespace Sts2ContextCoach.Scoring;

internal static class CardHeuristics
{
    private static readonly string[] BlockTokens =
    [
        "Defend", "Barricade", "Impervious", "Ghost", "FlameBarrier", "Blur", "CalculatedGamble",
        "Deflect", "CripplingCloud", "LegSweep", "Cleave", "Shrink", "Hologram", "Equilibrium",
        "ShrugItOff", "TrueGrit", "Armaments", "GhostlyArmor", "Entrench", "Dexterity", "Flex"
    ];

    private static readonly string[] DiscardTokens =
    [
        "Prepared", "Acrobatics", "Tactician", "ToolsOfTheTrade", "Concentrate", "Reflex",
        "StormOfSteel", "Violence", "Dismiss", "Backflip", "Adrenaline"
    ];

    private static readonly string[] DrawTokens =
    [
        "BattleTrance", "Offering", "PommelStrike", "ShrugItOff", "MasterOfStrategy",
        "Backflip", "Acrobatics", "Skim", "CompileDriver", "Coolheaded", "FTL",
        "Evolve", "DarkEmbrace"
    ];

    private static readonly string[] FrontloadTokens =
    [
        "Bash", "Uppercut", "Clash", "IronWave", "TwinStrike", "Anger", "Whirlwind", "Thunderclap",
        "Cleave", "SwordBoomerang", "Feed", "Reaper", "Dropkick", "Rampage", "Pummel", "Headbutt"
    ];

    private static readonly string[] ScalingTokens =
    [
        "DemonForm", "Barricade", "Inflame", "Combust", "LimitBreak", "FeelNoPain", "DarkEmbrace",
        "Juggernaut", "Brutality", "Evolve", "Rupture", "Demon", "SpotWeakness", "WraithForm"
    ];

    private static readonly string[] StrengthTokens =
    [
        "Inflame", "Flex", "LimitBreak", "SpotWeakness", "HeavyBlade", "SwordBoomerang", "Whirlwind",
        "TwinStrike", "Reaper", "Anger", "Bash", "Uppercut", "Rampage", "Brutality", "Pummel"
    ];

    private static readonly string[] ExhaustTokens =
    [
        "Corruption", "DarkEmbrace", "FeelNoPain", "SecondWind", "Exhume", "BurningBlood", "Evolve",
        "Sentinel", "TrueGrit", "Offering", "Hemokinesis", "InfernalBlade", "Impervious"
    ];

    private static readonly string[] HighCostTokens =
    [
        "DemonForm", "Barricade", "Offering", "Reaper", "Impervious", "Feed", "Whirlwind", "Bludgeon",
        "Immolate", "Carnage", "FiendFire", "LimitBreak", "SpotWeakness"
    ];

    private static readonly string[] AttackTokens =
    [
        "Strike", "Bash", "Cleave", "Anger", "Clash", "TwinStrike", "HeavyBlade", "Whirlwind",
        "Pommel", "Thunderclap", "Sword", "Boomerang", "Reaper", "Feed", "Dropkick", "Uppercut"
    ];

    /// <summary>Plain damage attacks that stack poorly — used for redundancy pressure.</summary>
    private static readonly string[] GenericAttackTokens =
    [
        "Strike", "Anger", "TwinStrike", "Clash", "Pummel", "Headbutt", "WildStrike", "IronWave"
    ];

    public static bool LooksLikeBlockCard(string internalName)
    {
        foreach (var t in BlockTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return internalName.Contains("Block", StringComparison.OrdinalIgnoreCase)
               || internalName.Contains("Shield", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeDiscardSynergy(string internalName)
    {
        foreach (var t in DiscardTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return internalName.Contains("Discard", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeDrawCard(string internalName)
    {
        foreach (var t in DrawTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return internalName.Contains("Draw", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeFrontloadCard(string internalName)
    {
        foreach (var t in FrontloadTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return LooksLikeAttack(internalName) && !LooksLikeScalingCard(internalName);
    }

    public static bool LooksLikeScalingCard(string internalName)
    {
        foreach (var t in ScalingTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return internalName.Contains("Strength", StringComparison.OrdinalIgnoreCase)
               || internalName.Contains("Dexterity", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeStrengthSynergy(string internalName)
    {
        foreach (var t in StrengthTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return internalName.Contains("Strength", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeExhaustSynergy(string internalName)
    {
        foreach (var t in ExhaustTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return internalName.Contains("Exhaust", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeAttack(string internalName)
    {
        foreach (var t in AttackTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return internalName.EndsWith("Attack", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeGenericAttack(string internalName)
    {
        foreach (var t in GenericAttackTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return true;
        }

        return internalName.Contains("Strike", StringComparison.OrdinalIgnoreCase)
               && !internalName.Contains("Perfected", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Best-effort cost when JSON metadata is absent (defaults to 1).</summary>
    public static int HeuristicCost(string internalName)
    {
        foreach (var t in HighCostTokens)
        {
            if (internalName.Contains(t, StringComparison.Ordinal)) return 2;
        }

        if (internalName.Contains("X", StringComparison.Ordinal)) return 2;
        return 1;
    }
}

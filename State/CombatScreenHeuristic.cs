using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace Sts2ContextCoach.State;

/// <summary>
/// Single definition of "probably in-fight UI" for overlay + RunLogger so victory/defeat transitions
/// do not briefly read as non-combat and trigger heavy overlay work.
/// </summary>
public static class CombatScreenHeuristic
{
    public static string BuildAncestorPath(NCard card)
    {
        var parts = new List<string>();
        Node? n = card.GetParent();
        while (n != null && parts.Count < 12)
        {
            parts.Add(n.Name.ToString());
            n = n.GetParent();
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    public static bool PathLooksLikeCombat(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var s = path;
        return s.Contains("battle", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("combat", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("encounter", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("FightScene", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("CombatView", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("FightUI", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("BattleUI", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Enemy", StringComparison.OrdinalIgnoreCase) ||
               s.Contains("Monster", StringComparison.OrdinalIgnoreCase) ||
               (s.Contains("hand", StringComparison.OrdinalIgnoreCase) &&
                (s.Contains("combat", StringComparison.OrdinalIgnoreCase) ||
                 s.Contains("battle", StringComparison.OrdinalIgnoreCase) ||
                 s.Contains("fight", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Card reward / pick screens often live under a combat scene graph or nodes named *Select*;
    /// this distinguishes them from in-fight hand and from deck-browser grids.
    /// </summary>
    public static bool PathIndicatesCardRewardPick(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var s = path;
        if (s.Contains("reward", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Contains("cardchoice", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("card_choice", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Contains("choosecard", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("choose_card", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Contains("pickcard", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("pick_card", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Contains("cardpick", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("card_pick", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Contains("draft", StringComparison.OrdinalIgnoreCase) &&
            s.Contains("card", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Contains("choice", StringComparison.OrdinalIgnoreCase) &&
            s.Contains("card", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Contains("option", StringComparison.OrdinalIgnoreCase) &&
            s.Contains("card", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.Contains("bounty", StringComparison.OrdinalIgnoreCase))
            return true;

        return s.Contains("choose", StringComparison.OrdinalIgnoreCase) &&
               s.Contains("card", StringComparison.OrdinalIgnoreCase);
    }
}

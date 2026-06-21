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

    /// <summary>
    /// Builds a slash path from the Godot scene tree so run telemetry can detect
    /// victory/defeat screens even when no <c>NCard</c> overlay timers are running (e.g. end-of-run UI).
    /// </summary>
    public static string BuildGlobalUiPath()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
            return "";

        Node? best = null;
        var bestDepth = -1;
        var walkBudget = 1200;

        void Visit(Node n, int depth, ref int budget)
        {
            if (budget-- <= 0 || depth > 28)
                return;

            var nm = n.Name.ToString();
            if (UiPathLooksTerminalRelevant(nm))
            {
                if (depth >= bestDepth)
                {
                    bestDepth = depth;
                    best = n;
                }
            }

            foreach (var child in n.GetChildren())
            {
                if (child is Node cn)
                    Visit(cn, depth + 1, ref budget);
            }
        }

        Visit(tree.Root, 0, ref walkBudget);
        if (best != null)
            return PathFromNodeToRoot(best);

        var leaf = tree.CurrentScene as Node ?? FirstChildNode(tree.Root);
        return leaf != null ? PathFromNodeToRoot(leaf) : "";
    }

    private static Node? FirstChildNode(Node root)
    {
        foreach (var c in root.GetChildren())
        {
            if (c is Node n)
                return n;
        }

        return null;
    }

    private static string PathFromNodeToRoot(Node end)
    {
        var parts = new List<string>();
        for (var n = end; n != null && parts.Count < 28; n = n.GetParent())
            parts.Add(n.Name.ToString());
        parts.Reverse();
        return string.Join("/", parts);
    }

    /// <summary>Node-name hints for DFS; keep loosely aligned with <see cref="Telemetry.RunOutcomeClassifier"/>.</summary>
    private static bool UiPathLooksTerminalRelevant(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        return name.Contains("Victory", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("NeoVict", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("RunComplete", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("run_complete", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Credit", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Defeat", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Death", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("GameOver", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("YouDied", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("RunLost", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("EndOfRun", StringComparison.OrdinalIgnoreCase);
    }
}

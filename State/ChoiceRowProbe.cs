using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace Sts2ContextCoach.State;

/// <summary>
/// Card reward rows often use one <see cref="NCard"/> per slot (no shared sibling parent).
/// Walks up the scene and BFSes shallow subtrees to collect every choosable card for one batched LLM call.
/// </summary>
public static class ChoiceRowProbe
{
    private const int MaxAncestors = 16;
    private const int BfsBudget = 420;
    private const int DefaultMaxDepth = 8;

    public static List<NCard> CollectChoiceRowCardNodes(NCard anchor, int maxCards = 12, int maxDepth = DefaultMaxDepth)
    {
        // Card hover tooltips use a different NCard under HoverTip*; the real reward row lives under
        // NCardRewardSelectionScreen. Without this, BFS rejects (anchor not in subtree) → 1-card "row" → new LLM batch.
        if (IsHoverPreviewTree(anchor))
        {
            var hoverRewardRoot = FindCardRewardScreenSubtreeRoot(anchor);
            if (hoverRewardRoot != null)
            {
                var hoverRow = BfsCoachableCards(hoverRewardRoot, null, maxDepth, maxCards, requireAnchorInResult: false);
                if (hoverRow.Count >= 2)
                    return hoverRow;
            }
        }

        // Real reward UI lives under *NCardReward* / *CardRewardSelection*. BFS from higher combat ancestors
        // pulls hand / other NCards → huge batches, unstable keys, or LLM JSON that matches no candidate.
        var pickRewardRoot = FindCardRewardScreenSubtreeRoot(anchor);
        if (pickRewardRoot != null)
        {
            var rewardRow = BfsCoachableCards(pickRewardRoot, anchor, maxDepth, maxCards, requireAnchorInResult: true);
            if (rewardRow.Count >= 2)
                return rewardRow;
        }

        // Multi-row rewards without a named reward root, or odd layouts: sibling row + ancestor BFS; prefer
        // the largest set that still contains this anchor (still capped by maxCards / depth).
        var candidates = new List<List<NCard>>();

        var siblings = CollectSiblingCoachable(anchor);
        if (siblings.Count >= 2)
            candidates.Add(siblings);

        Node? cur = anchor.GetParent();
        for (var hop = 0; hop < MaxAncestors && cur != null; hop++, cur = cur.GetParent())
        {
            var found = BfsCoachableCards(cur, anchor, maxDepth, maxCards, requireAnchorInResult: true);
            if (found.Count >= 2)
                candidates.Add(found);
        }

        List<NCard>? best = null;
        foreach (var list in candidates)
        {
            if (best == null || list.Count > best.Count)
                best = list;
        }

        if (best != null)
            return best;

        return siblings.Count > 0 ? siblings : SingleOrEmpty(anchor);
    }

    /// <summary>
    /// Floating hover preview cards live under these nodes — not e.g. <c>NoHoverTips</c> (substring "HoverTip" is a false positive).
    /// </summary>
    public static bool IsHoverPreviewTree(NCard anchor)
    {
        for (var n = (Node?)anchor; n != null; n = n.GetParent())
        {
            var nm = n.Name.ToString();
            if (nm.Contains("HoverTipsContainer", StringComparison.OrdinalIgnoreCase))
                return true;
            if (nm.Contains("CardHoverTip", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static Node? FindCardRewardScreenSubtreeRoot(NCard anchor)
    {
        for (var n = (Node?)anchor; n != null; n = n.GetParent())
        {
            var nm = n.Name.ToString();
            if (nm.Contains("NCardReward", StringComparison.OrdinalIgnoreCase) ||
                nm.Contains("CardRewardSelection", StringComparison.OrdinalIgnoreCase))
                return n;
        }

        return null;
    }

    private static List<NCard> SingleOrEmpty(NCard anchor)
    {
        return IsCoachable(anchor) ? [anchor] : [];
    }

    private static List<NCard> CollectSiblingCoachable(NCard anchor)
    {
        var parent = anchor.GetParent();
        if (parent == null)
            return [];
        var list = new List<NCard>();
        foreach (var ch in parent.GetChildren())
        {
            if (ch is NCard nc && IsCoachable(nc))
                list.Add(nc);
        }

        return list;
    }

    private static List<NCard> BfsCoachableCards(
        Node root,
        NCard? anchor,
        int maxDepth,
        int maxCards,
        bool requireAnchorInResult)
    {
        var result = new List<NCard>();
        var visited = new HashSet<ulong>();
        var q = new Queue<(Node n, int d)>();
        q.Enqueue((root, 0));
        var budget = 0;

        while (q.Count > 0 && result.Count < maxCards && budget++ < BfsBudget)
        {
            var (n, d) = q.Dequeue();
            if (d > maxDepth)
                continue;
            if (!visited.Add(n.GetInstanceId()))
                continue;

            if (n is NCard nc && IsCoachable(nc))
                result.Add(nc);

            foreach (var c in n.GetChildren())
                q.Enqueue((c, d + 1));
        }

        if (result.Count < 2)
            return [];

        if (requireAnchorInResult && (anchor == null || !result.Any(n => ReferenceEquals(n, anchor))))
            return [];

        return result;
    }

    private static bool IsCoachable(NCard nc)
    {
        var m = CardModelReflection.GetModel(nc);
        if (m == null)
            return false;
        var name = m.GetType().Name;
        return !name.StartsWith("Strike", StringComparison.Ordinal) &&
               !name.StartsWith("Defend", StringComparison.Ordinal);
    }
}

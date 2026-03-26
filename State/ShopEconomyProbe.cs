using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using Sts2ContextCoach.Diagnostics;

namespace Sts2ContextCoach.State;

/// <summary>
/// Reads merchant prices from the scene tree (sibling labels, sale colors, removal service row).
/// Best-effort only; if parsing fails, <see cref="ShopEconomyContext.CardPrice"/> stays null.
/// </summary>
public static class ShopEconomyProbe
{
    private const int MinGoldPrice = 10;
    private const int MaxGoldPrice = 999;

    private static ulong _cachedShopRootId;
    private static int? _cachedRemovalPrice;

    /// <summary>Clears cached removal price (e.g. when leaving shop or invalidating game snapshot).</summary>
    public static void ClearCache()
    {
        _cachedShopRootId = 0;
        _cachedRemovalPrice = null;
    }

    public static ShopEconomyContext Probe(NCard card)
    {
        var parent = card.GetParent();
        if (parent == null)
            return default;

        var candidates = new List<int>(4);
        CollectSlotPrices(parent, card, candidates);

        var (price, discounted) = PickPriceAndSale(parent, card, candidates);

        var shopRoot = FindShopRoot(card);
        var removal = shopRoot != null ? GetRemovalPrice(shopRoot) : null;

        if (ContextCoachLogging.Verbose)
        {
            var filtered = candidates
                .Where(n => n is >= MinGoldPrice and <= MaxGoldPrice)
                .Distinct()
                .OrderBy(n => n)
                .ToList();
            var dualSale = filtered.Count >= 2 && filtered[^1] >= filtered[0] * 2 - 2;
            var msg =
                $"shop-probe parent={parent.Name} rawCount={candidates.Count} " +
                $"raw=[{string.Join(",", candidates.Take(8))}] filtered=[{string.Join(",", filtered)}] " +
                $"pick={(price?.ToString() ?? "null")} discounted={discounted} dualPriceImpliesSale={dualSale} " +
                $"shopRoot={(shopRoot?.Name.ToString() ?? "null")} removal={(removal?.ToString() ?? "null")}";
            ContextCoachLogging.VerboseInfo(msg);
            if (price is null && (candidates.Count == 0 || filtered.Count == 0))
                LogNearbyPriceLabels(parent, card);
        }

        return new ShopEconomyContext
        {
            CardPrice = price,
            IsDiscounted = discounted,
            RemovalServicePrice = removal
        };
    }

    private static void LogNearbyPriceLabels(Node parent, NCard card)
    {
        var lines = new List<string>(16);
        void Walk(Node n, int depth, int maxDepth)
        {
            if (depth > maxDepth || lines.Count >= 14) return;
            switch (n)
            {
                case Label l:
                {
                    var t = l.Text.ToString();
                    if (t.Length > 48) t = t.Substring(0, 45) + "...";
                    lines.Add($"{n.Name}:{t}");
                    break;
                }
                case RichTextLabel rtl:
                {
                    var t = rtl.GetParsedText();
                    if (t.Length > 48) t = t.Substring(0, 45) + "...";
                    lines.Add($"{n.Name}:{t}");
                    break;
                }
            }

            foreach (var c in n.GetChildren())
            {
                if (ReferenceEquals(c, card)) continue;
                if (c.Name.ToString().StartsWith("ContextCoach", StringComparison.Ordinal)) continue;
                Walk(c, depth + 1, maxDepth);
            }
        }

        foreach (var child in parent.GetChildren())
        {
            if (ReferenceEquals(child, card)) continue;
            Walk(child, 0, 4);
        }

        if (lines.Count == 0)
            ContextCoachLogging.VerboseInfo("shop-probe no Label/RichTextLabel siblings found under slot parent (depth<=4)");
        else
            ContextCoachLogging.VerboseInfo("shop-probe nearby labels: " + string.Join(" | ", lines));
    }

    private static void CollectSlotPrices(Node parent, NCard card, List<int> candidates)
    {
        foreach (var child in parent.GetChildren())
        {
            if (ReferenceEquals(child, card))
                continue;

            CollectNumericLabels(child, candidates, depth: 0, maxDepth: 5);
        }

        foreach (var child in card.GetChildren())
        {
            var name = child.Name.ToString();
            if (name.StartsWith("ContextCoach", StringComparison.Ordinal))
                continue;

            CollectNumericLabels(child, candidates, 0, 3);
        }
    }

    private static void CollectNumericLabels(Node node, List<int> candidates, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        TryAddLabelNumber(node, candidates);

        foreach (var child in node.GetChildren())
            CollectNumericLabels(child, candidates, depth + 1, maxDepth);
    }

    private static void TryAddLabelNumber(Node node, List<int> candidates)
    {
        switch (node)
        {
            case Label label:
                if (TryParseGoldNumber(label.Text, out var v))
                    candidates.Add(v);
                break;
            case RichTextLabel rtl:
                if (TryParseGoldNumber(rtl.GetParsedText(), out var v2))
                    candidates.Add(v2);
                break;
        }
    }

    private static (int? price, bool discounted) PickPriceAndSale(Node parent, NCard card, List<int> candidates)
    {
        if (candidates.Count == 0)
            return (null, false);

        var distinct = candidates
            .Where(n => n is >= MinGoldPrice and <= MaxGoldPrice)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (distinct.Count == 0)
            return (null, false);

        if (distinct.Count == 1)
            return (distinct[0], IsSaleStyled(parent, card));

        var low = distinct[0];
        var high = distinct[^1];
        if (high >= low * 2 - 2)
            return (low, true);

        return (low, IsSaleStyled(parent, card));
    }

    private static bool IsSaleStyled(Node parent, NCard card)
    {
        foreach (var child in parent.GetChildren())
        {
            if (ReferenceEquals(child, card)) continue;
            if (IsGreenDealLabel(child))
                return true;
            if (child.Name.ToString().Contains("Sale", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsGreenDealLabel(Node node)
    {
        if (node is Label label)
        {
            var m = label.Modulate;
            if (m.G > 0.72f && m.R < 0.55f && m.B < 0.55f)
                return true;
        }

        foreach (var child in node.GetChildren())
        {
            if (IsGreenDealLabel(child))
                return true;
        }

        return false;
    }

    private static bool TryParseGoldNumber(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return false;
        if (!int.TryParse(digits, out var v)) return false;
        if (v is < MinGoldPrice or > MaxGoldPrice) return false;
        value = v;
        return true;
    }

    private static Node? FindShopRoot(NCard card)
    {
        Node? n = card.GetParent();
        for (var i = 0; i < 12 && n != null; i++)
        {
            var name = n.Name.ToString();
            if (name.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Merchant", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Store", StringComparison.OrdinalIgnoreCase))
                return n;

            n = n.GetParent();
        }

        return null;
    }

    private static int? GetRemovalPrice(Node shopRoot)
    {
        var id = shopRoot.GetInstanceId();
        if (id == _cachedShopRootId && _cachedRemovalPrice.HasValue)
            return _cachedRemovalPrice;

        _cachedShopRootId = id;
        _cachedRemovalPrice = ScanRemovalPrice(shopRoot);
        return _cachedRemovalPrice;
    }

    private static int? ScanRemovalPrice(Node root)
    {
        var q = new Queue<Node>();
        q.Enqueue(root);
        var budget = 0;
        int? best = null;

        while (q.Count > 0 && budget++ < 220)
        {
            var n = q.Dequeue();

            if (n is Label or RichTextLabel)
            {
                if (PathFromNodeToRootContains(n, root, "Remove") ||
                    PathFromNodeToRootContains(n, root, "Removal") ||
                    PathFromNodeToRootContains(n, root, "Purge"))
                {
                    if (n is Label l && TryParseGoldNumber(l.Text, out var v))
                        best = PickSmaller(best, v);
                    else if (n is RichTextLabel rtl && TryParseGoldNumber(rtl.GetParsedText(), out var v2))
                        best = PickSmaller(best, v2);
                }
            }

            foreach (var child in n.GetChildren())
                q.Enqueue(child);
        }

        return best;
    }

    private static int? PickSmaller(int? a, int b)
    {
        if (!a.HasValue || b < a.Value) return b;
        return a;
    }

    private static bool PathFromNodeToRootContains(Node node, Node root, string needle)
    {
        for (var cur = node; cur != null; cur = cur.GetParent())
        {
            if (cur.Name.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
            if (ReferenceEquals(cur, root))
                break;
        }

        return false;
    }
}

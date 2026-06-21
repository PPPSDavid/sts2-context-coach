using System.Linq;
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

    private static ulong _rowStatsShopRootId;
    private static long _rowStatsCachedMs;
    private static int? _rowStatsMinPrice;
    private static int _rowStatsPricedCount;

    /// <summary>Clears cached removal price (e.g. when leaving shop or invalidating game snapshot).</summary>
    public static void ClearCache()
    {
        _cachedShopRootId = 0;
        _cachedRemovalPrice = null;
        _rowStatsShopRootId = 0;
        _rowStatsCachedMs = 0;
        _rowStatsMinPrice = null;
        _rowStatsPricedCount = 0;
    }

    /// <summary>
    /// Min listed card price under the same shop root (cached ~450ms). Used for “cheapest in row” heuristics.
    /// </summary>
    public static (int? MinPrice, int PricedSlotCount) GetShopRowCardPriceStats(NCard anchor, int? playerGold)
    {
        var root = FindShopRoot(anchor);
        if (root == null)
            return (null, 0);

        var rid = root.GetInstanceId();
        var now = System.Environment.TickCount64;
        if (rid == _rowStatsShopRootId && now - _rowStatsCachedMs < 450)
            return (_rowStatsMinPrice, _rowStatsPricedCount);

        var cards = CollectShopCardNodes(anchor, maxCards: 28);
        int? min = null;
        var priced = 0;
        foreach (var nc in cards)
        {
            var eco = Probe(nc, playerGold);
            if (!eco.HasCardPrice) continue;
            priced++;
            var p = eco.CardPrice!.Value;
            min = min is null || p < min ? p : min;
        }

        _rowStatsShopRootId = rid;
        _rowStatsCachedMs = now;
        _rowStatsMinPrice = min;
        _rowStatsPricedCount = priced;
        return (min, priced);
    }

    /// <param name="playerGold">When set, values equal to current gold are dropped from price candidates if a larger candidate exists (HUD gold leak).</param>
    public static ShopEconomyContext Probe(NCard card, int? playerGold = null)
    {
        var parent = card.GetParent();
        if (parent == null)
            return default;

        var candidates = new List<int>(4);
        CollectSlotPrices(parent, card, candidates);

        var (price, discounted) = PickPriceAndSale(parent, card, candidates, playerGold);

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
            if (child is NCard)
                continue;

            // Shallow only — deep walks pulled in other slots’ prices (e.g. 71g next to 52g card).
            CollectNumericLabels(child, candidates, depth: 0, maxDepth: 2, preferPriceNamedNodes: false);
        }

        foreach (var child in card.GetChildren())
        {
            var name = child.Name.ToString();
            if (name.StartsWith("ContextCoach", StringComparison.Ordinal))
                continue;

            // Some merchant card holders place the sticker deeper under per-card UI wrappers.
            // Go deeper for this card subtree only; at deep levels, prefer labels under price/gold/cost-like node names
            // to avoid matching card description numbers (damage, block, etc.).
            CollectNumericLabels(child, candidates, depth: 0, maxDepth: 6, preferPriceNamedNodes: true);
        }

        if (candidates.Count == 0)
        {
            var slot = FindMerchantSlotContainer(card);
            if (slot != null)
                CollectScopedSlotPrices(slot, card, candidates, depth: 0, maxDepth: 9);
        }
    }

    private static void CollectNumericLabels(
        Node node,
        List<int> candidates,
        int depth,
        int maxDepth,
        bool preferPriceNamedNodes)
    {
        if (depth > maxDepth) return;

        TryAddLabelNumber(node, candidates, depth, preferPriceNamedNodes);

        foreach (var child in node.GetChildren())
            CollectNumericLabels(child, candidates, depth + 1, maxDepth, preferPriceNamedNodes);
    }

    private static void TryAddLabelNumber(Node node, List<int> candidates, int depth, bool preferPriceNamedNodes)
    {
        if (preferPriceNamedNodes && depth >= 3 && !LooksLikePriceNodeName(node.Name.ToString()))
            return;

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

    private static bool LooksLikePriceNodeName(string name)
    {
        return name.Contains("Price", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Gold", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Cost", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Coin", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Sale", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Discount", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Merchant", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Shop", StringComparison.OrdinalIgnoreCase);
    }

    private static Node? FindMerchantSlotContainer(NCard card)
    {
        for (var n = card.GetParent(); n != null; n = n.GetParent())
        {
            var nm = n.Name.ToString();
            if (IsMerchantPerCardSlotContainer(nm))
                return n;
        }

        return null;
    }

    private static void CollectScopedSlotPrices(Node node, NCard card, List<int> candidates, int depth, int maxDepth)
    {
        if (depth > maxDepth)
            return;
        if (ReferenceEquals(node, card) || node is not NCard)
            TryAddLabelNumber(node, candidates, depth, preferPriceNamedNodes: true);

        foreach (var child in node.GetChildren())
        {
            if (ReferenceEquals(child, card))
                continue;
            if (child is NCard)
                continue;
            if (child.Name.ToString().StartsWith("ContextCoach", StringComparison.Ordinal))
                continue;
            CollectScopedSlotPrices(child, card, candidates, depth + 1, maxDepth);
        }
    }

    private static List<int> FilterHudGoldFromPrices(List<int> distinctAscending, int? playerGold)
    {
        if (distinctAscending.Count <= 1 || playerGold is not int pg)
            return distinctAscending;
        if (!distinctAscending.Contains(pg))
            return distinctAscending;
        var high = distinctAscending[^1];
        if (high > pg)
            return distinctAscending.Where(x => x != pg).ToList();
        return distinctAscending;
    }

    private static (int? price, bool discounted) PickPriceAndSale(
        Node parent,
        NCard card,
        List<int> candidates,
        int? playerGold)
    {
        if (candidates.Count == 0)
            return (null, false);

        var distinct = candidates
            .Where(n => n is >= MinGoldPrice and <= MaxGoldPrice)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        distinct = FilterHudGoldFromPrices(distinct, playerGold);

        if (distinct.Count == 0)
            return (null, false);

        if (distinct.Count == 1)
            return (distinct[0], IsSaleGreenStyled(parent, card));

        var low = distinct[0];
        var high = distinct[^1];
        var saleGreen = IsSaleGreenStyled(parent, card);
        // Two-price sale: high ~2× low. Never pick the low on dual math alone when it is junk vs high
        // (e.g. 14 vs 83) — that used to fire “small share of gold” via a fake low price.
        var dualStickerSale = high >= low * 2 - 2;
        var lowLooksLikeDiscount = low >= high * 0.22f;
        if (dualStickerSale && lowLooksLikeDiscount)
            return (low, true);

        // Green “deal” styling only (do not use *Name contains "Sale"* — too many false positives on shop slots).
        if (saleGreen && low < high && (low >= high * 0.2f || dualStickerSale))
            return (low, true);

        // Two plausible stickers close together: prefer LOWER — the higher number is often bleed from the next shop slot.
        if (distinct.Count == 2 && high - low <= 52)
            return (low, false);

        // Exactly two values far apart: the larger is often bleed from relic/service/other rows (e.g. 48 vs 421).
        if (distinct.Count == 2)
        {
            var gap = high - low;
            // Player can afford the lower but not the higher → lower is almost always this card's sticker.
            if (playerGold is int g && low <= g && high > g && gap > 35)
                return (low, IsSaleGreenStyled(parent, card));
            // Strong outlier ratio even when gold is missing or both prices exceed gold.
            if (gap > 90 && high >= low * 2.4f)
                return (low, IsSaleGreenStyled(parent, card));
        }

        // 3+ leaked values: never take max (that was neighbor relic/potion prices); smallest in-range is usually this card.
        if (distinct.Count >= 3)
            return (distinct[0], IsSaleGreenStyled(parent, card));

        // Wide gap, no sale: keep higher as sticker (e.g. 114 vs 176 after HUD filter).
        return (high, false);
    }

    private static bool IsSaleGreenStyled(Node parent, NCard card)
    {
        foreach (var child in parent.GetChildren())
        {
            if (ReferenceEquals(child, card)) continue;
            if (IsGreenDealLabel(child))
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

    /// <summary>Matches <see cref="FindShopRoot"/> naming heuristics (for UI classification).</summary>
    public static bool IsShopLikeNodeName(string name) =>
        LooksLikeShopContainerName(name);

    private static bool LooksLikeShopContainerName(string name)
    {
        return name.Contains("Shop", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Merchant", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Store", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Vendor", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Kiosk", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Bazaar", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Peddler", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Wares", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("CardShop", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("BuyCards", StringComparison.OrdinalIgnoreCase) ||
               (name.Contains("Purchase", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("Card", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Single-card slots (e.g. MerchantCardHolder4) contain "Merchant" but must NOT be the BFS root —
    /// logs showed shopRootBfs=1 and alternating 5- vs 2-card LLM batches.
    /// </summary>
    private static bool IsMerchantPerCardSlotContainer(string name) =>
        name.Contains("MerchantCardHolder", StringComparison.OrdinalIgnoreCase);

    private static Node? FindShopRoot(NCard card)
    {
        Node? n = card.GetParent();
        for (var i = 0; i < 24 && n != null; i++, n = n.GetParent())
        {
            var nm = n.Name.ToString();
            if (IsMerchantPerCardSlotContainer(nm))
                continue;
            if (LooksLikeShopContainerName(nm))
                return n;
        }

        return null;
    }

    /// <summary>When save-backed <see cref="GameState.Gold"/> is null, scan HUD labels named *Gold* above the card.</summary>
    public static int? TryResolveHudPlayerGold(NCard anchor)
    {
        // Shop: walk from shared merchant root up first so every slot resolves the same HUD gold when possible.
        var shopRoot = FindShopRoot(anchor);
        if (shopRoot != null)
        {
            for (var n = shopRoot; n != null; n = n.GetParent())
            {
                var v = ScanSubtreeForGoldNamedLabel(n, maxDepth: 6, maxNodes: 140);
                if (v.HasValue)
                    return v;
            }
        }

        for (var n = anchor.GetParent(); n != null; n = n.GetParent())
        {
            var v = ScanSubtreeForGoldNamedLabel(n, maxDepth: 6, maxNodes: 120);
            if (v.HasValue)
                return v;
        }

        return null;
    }

    private static int? ScanSubtreeForGoldNamedLabel(Node root, int maxDepth, int maxNodes)
    {
        var q = new Queue<(Node node, int depth)>();
        q.Enqueue((root, 0));
        var budget = 0;
        while (q.Count > 0 && budget++ < maxNodes)
        {
            var (node, d) = q.Dequeue();
            if (d > maxDepth)
                continue;
            var nm = node.Name.ToString();
            if (nm.Contains("Gold", StringComparison.OrdinalIgnoreCase))
            {
                switch (node)
                {
                    case Label l:
                        if (TryParsePlayerHudGold(l.Text, out var a))
                            return a;
                        break;
                    case RichTextLabel rtl:
                        if (TryParsePlayerHudGold(rtl.GetParsedText(), out var b))
                            return b;
                        break;
                }
            }

            foreach (var c in node.GetChildren())
                q.Enqueue((c, d + 1));
        }

        return null;
    }

    private static bool TryParsePlayerHudGold(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length is 0 or > 5) return false;
        if (!int.TryParse(digits, out var v)) return false;
        if (v is < 0 or > 99999) return false;
        value = v;
        return true;
    }

    /// <summary>
    /// All purchasable <see cref="NCard"/> nodes under the shop root (for batched LLM — avoids per-slot batch keys).
    /// </summary>
    public static List<NCard> CollectShopCardNodes(NCard anchor, int maxCards = 28)
    {
        var root = FindShopRoot(anchor);
        if (root == null)
            return [];

        var seen = new HashSet<ulong>();
        var result = new List<NCard>();
        var q = new Queue<Node>();
        q.Enqueue(root);
        var budget = 0;

        while (q.Count > 0 && result.Count < maxCards && budget++ < 520)
        {
            var n = q.Dequeue();
            if (n is NCard nc && seen.Add(nc.GetInstanceId()))
            {
                var m = CardModelReflection.GetModel(nc);
                if (m != null)
                {
                    var nm = m.GetType().Name;
                    if (!nm.StartsWith("Strike", StringComparison.Ordinal) &&
                        !nm.StartsWith("Defend", StringComparison.Ordinal))
                        result.Add(nc);
                }
            }

            foreach (var c in n.GetChildren())
                q.Enqueue(c);
        }

        return result;
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

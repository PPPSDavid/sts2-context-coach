using System.Reflection;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace Sts2ContextCoach.State;

/// <summary>
/// Rule-based context bonus for card augments/enchantments (runtime). Tries model reflection then node names/paths.
/// </summary>
public static class CardAugmentProbe
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;

    public static (float Weight, string? ReasonKey) GetScoreDelta(NCard? card, object? model)
    {
        var blob = new StringBuilder(384);
        AppendModelBlob(model, blob, depth: 0, maxDepth: 5);
        if (card != null)
            AppendNodeBlob(card, blob, depth: 0, maxDepth: 6);

        var s = blob.ToString();
        if (s.Length < 3)
            return (0f, null);

        return ClassifyHints(s);
    }

    private static (float Weight, string? ReasonKey) ClassifyHints(string raw)
    {
        var sl = raw.ToLowerInvariant();
        var hasAugmentContext =
            sl.Contains("enchant", StringComparison.Ordinal) ||
            sl.Contains("augment", StringComparison.Ordinal) ||
            sl.Contains("modifier", StringComparison.Ordinal) ||
            sl.Contains("mutat", StringComparison.Ordinal) ||
            sl.Contains("res://images/enchantments/", StringComparison.Ordinal);
        if (!hasAugmentContext)
            return (0f, null);

        float total = 0f;
        string? bestKey = null;
        float best = 0f;

        void Add(string key, float w)
        {
            total += w;
            if (w > best)
            {
                best = w;
                bestKey = key;
            }
        }

        // Remove / negate exhaust on card (high impact)
        if (sl.Contains("removeexhaust") || sl.Contains("no_exhaust") || sl.Contains("noexhaust") ||
            sl.Contains("stripexhaust") || (sl.Contains("cleanse") && sl.Contains("exhaust")))
            Add("reason.augment_remove_exhaust", 8f);

        // Common STS2 enchantment art names (see res://images/enchantments/*.png in logs)
        if (sl.Contains("sharp") || sl.Contains("serrated"))
            Add("reason.augment_attack", 2.2f);
        if (sl.Contains("nimble") || sl.Contains("quickdraw"))
            Add("reason.augment_draw", 2.4f);
        // Avoid generic "energy" token: it appears in many non-augment contexts.
        if (sl.Contains("swift") || sl.Contains("haste") || sl.Contains("instinct"))
            Add("reason.augment_energy", 2.0f);
        if (sl.Contains("stalwart") || sl.Contains("bulwark") || sl.Contains("plated") ||
            (sl.Contains("enchantments") && sl.Contains("block")))
            Add("reason.augment_block", 2.2f);

        if (total < 0.5f)
            return (0f, null);

        // Cap so augments never dominate deck/relic context
        const float cap = 9f;
        if (total > cap)
            total = cap;

        return (total, bestKey);
    }

    private static void AppendModelBlob(object? obj, StringBuilder sb, int depth, int maxDepth)
    {
        if (obj == null || depth > maxDepth) return;

        var t = obj.GetType();
        sb.Append(t.Name);
        sb.Append(' ');

        if (obj is string str)
        {
            if (str.Length <= 120) sb.Append(str).Append(' ');
            return;
        }

        if (obj is System.Collections.IEnumerable enumerable and not string)
        {
            var n = 0;
            foreach (var item in enumerable)
            {
                AppendModelBlob(item, sb, depth + 1, maxDepth);
                if (++n >= 6) break;
            }

            return;
        }

        if (t.IsPrimitive) return;

        foreach (var p in t.GetProperties(Flags))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            var pn = p.Name;
            if (!pn.Contains("Enchant", StringComparison.Ordinal) &&
                !pn.Contains("Augment", StringComparison.Ordinal) &&
                !pn.Contains("Modifier", StringComparison.Ordinal) &&
                !pn.Contains("Mutat", StringComparison.Ordinal))
                continue;

            object? v;
            try { v = p.GetValue(obj); }
            catch { continue; }

            AppendModelBlob(v, sb, depth + 1, maxDepth);
        }
    }

    private static void AppendNodeBlob(Node node, StringBuilder sb, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        sb.Append(node.Name.ToString());
        sb.Append(' ');

        try
        {
            var path = node.SceneFilePath;
            if (!string.IsNullOrEmpty(path))
            {
                sb.Append(path);
                sb.Append(' ');
            }
        }
        catch
        {
            // ignored
        }

        foreach (var child in node.GetChildren())
            AppendNodeBlob(child, sb, depth + 1, maxDepth);
    }
}

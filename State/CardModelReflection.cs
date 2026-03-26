using System.Reflection;
using MegaCrit.Sts2.Core.Nodes.Cards;

namespace Sts2ContextCoach.State;

public static class CardModelReflection
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;

    public static object? GetModel(NCard card)
    {
        var t = card.GetType();
        var field = t.GetField("_model", Flags);
        if (field != null)
        {
            try { return field.GetValue(card); } catch { /* ignored */ }
        }

        var prop = t.GetProperty("Model", Flags);
        if (prop != null)
        {
            try { return prop.GetValue(card); } catch { /* ignored */ }
        }

        return null;
    }

    public static string? GetInternalName(NCard card) => GetInternalName(GetModel(card));

    public static string? GetInternalName(object? model) => model?.GetType().Name;

    public static bool IsUpgraded(NCard card) => IsUpgraded(GetModel(card));

    public static bool IsUpgraded(object? model)
    {
        if (model == null) return false;
        var t = model.GetType();
        foreach (var name in new[] { "IsUpgraded", "Upgraded", "WasUpgraded" })
        {
            var p = t.GetProperty(name, Flags);
            if (p?.PropertyType == typeof(bool) && p.CanRead)
            {
                try
                {
                    return p.GetValue(model) is true;
                }
                catch
                {
                    // ignored
                }
            }
        }

        return false;
    }

    public static int? GetCost(object? model)
    {
        if (model == null) return null;
        var t = model.GetType();
        foreach (var name in new[] { "Cost", "EnergyCost", "ManaCost" })
        {
            var p = t.GetProperty(name, Flags);
            if (p == null || !p.CanRead) continue;
            try
            {
                var v = p.GetValue(model);
                switch (v)
                {
                    case int i: return i;
                    case short s: return s;
                    case long l: return (int)l;
                }
            }
            catch
            {
                // ignored
            }
        }

        return null;
    }
}

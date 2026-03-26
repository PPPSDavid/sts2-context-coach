using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using Sts2ContextCoach.Data;
using Sts2ContextCoach.Localization;
using Sts2ContextCoach.Scoring;
using Sts2ContextCoach.State;

namespace Sts2ContextCoach.UI;

[HarmonyPatch]
public static class CardOverlayPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var type = typeof(NCard);
        var m = type.GetMethod("_Ready", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        if (m != null) yield return m;
    }

    [HarmonyPostfix]
    public static void Postfix(NCard __instance)
    {
        try
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var uiName = "ContextCoachUI_" + id;

            var container = new Node2D { Name = uiName };

            const float boxWidth = 220f;
            const float boxHeight = 118f;

            container.Position = new Vector2(-boxWidth / 2f, 160f);
            container.Visible = false;

            var border = new ColorRect();
            border.SetSize(new Vector2(boxWidth + 4f, boxHeight + 4f));
            border.SetPosition(new Vector2(-2f, -2f));
            container.AddChild(border);

            var bg = new ColorRect
            {
                Color = new Color(0.05f, 0.05f, 0.05f, 0.95f)
            };
            bg.SetSize(new Vector2(boxWidth, boxHeight));
            container.AddChild(bg);

            var label = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };
            label.SetSize(new Vector2(boxWidth, boxHeight));
            label.AddThemeFontSizeOverride("font_size", 14);
            label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 1));
            label.AddThemeConstantOverride("shadow_offset_x", 1);
            label.AddThemeConstantOverride("shadow_offset_y", 1);
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            container.AddChild(label);

            __instance.AddChild(container);

            SetupTracker(__instance, container, border, bg, label, uiName);
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] Overlay patch error: {ex.Message}");
        }
    }

    private static void SetupTracker(NCard cardNode, Node2D container, ColorRect border, ColorRect bg, Label label, string myUiName)
    {
        var timer = new Godot.Timer
        {
            WaitTime = 0.35,
            Autostart = true
        };
        container.AddChild(timer);

        const float defaultY = 160f;
        const float shopY = -300f;
        const float boxWidth = 220f;

        timer.Timeout += () =>
        {
            if (!GodotObject.IsInstanceValid(container) || !GodotObject.IsInstanceValid(cardNode)) return;

            foreach (var child in cardNode.GetChildren())
            {
                var childName = child.Name.ToString();
                if (childName.StartsWith("ContextCoachUI", StringComparison.Ordinal) && childName != myUiName)
                {
                    child.Name = "Killed_" + Guid.NewGuid();
                    child.QueueFree();
                }
            }

            var isCombat = false;
            var isShop = false;
            var isGridOrDeck = false;
            Node? current = cardNode.GetParent();
            while (current != null)
            {
                var n = current.Name.ToString().ToLowerInvariant();

                if (n.Contains("battle", StringComparison.Ordinal) ||
                    n.Contains("combat", StringComparison.Ordinal) ||
                    n.Contains("hand", StringComparison.Ordinal))
                {
                    isCombat = true;
                }

                if (n.Contains("shop", StringComparison.Ordinal) ||
                    n.Contains("merchant", StringComparison.Ordinal) ||
                    n.Contains("store", StringComparison.Ordinal))
                {
                    isShop = true;
                }

                if (n.Contains("grid", StringComparison.Ordinal) ||
                    n.Contains("deck", StringComparison.Ordinal) ||
                    n.Contains("pile", StringComparison.Ordinal) ||
                    n.Contains("select", StringComparison.Ordinal) ||
                    n.Contains("remove", StringComparison.Ordinal))
                {
                    isGridOrDeck = true;
                }

                current = current.GetParent();
            }

            if (isCombat)
            {
                container.Visible = false;
                foreach (var child in cardNode.GetChildren())
                {
                    if (child.Name.ToString().StartsWith("ContextCoachUI", StringComparison.Ordinal))
                        (child as CanvasItem)!.Visible = false;
                }

                return;
            }

            if (isShop && !isGridOrDeck)
                container.Position = new Vector2(-boxWidth / 2f, shopY);
            else
                container.Position = new Vector2(-boxWidth / 2f, defaultY);

            var model = CardModelReflection.GetModel(cardNode);
            if (model == null)
            {
                container.Visible = false;
                return;
            }

            var internalName = model.GetType().Name;
            if (internalName.StartsWith("Strike", StringComparison.Ordinal) ||
                internalName.StartsWith("Defend", StringComparison.Ordinal))
            {
                container.Visible = false;
                foreach (var child in cardNode.GetChildren())
                {
                    if (child.Name.ToString().StartsWith("ContextCoachUI", StringComparison.Ordinal))
                        (child as CanvasItem)!.Visible = false;
                }

                return;
            }

            container.Visible = true;

            var state = GameStateExtractor.ExtractForCard(cardNode);

            ShopEconomyContext? shopEco = null;
            if (isShop && !isGridOrDeck)
                shopEco = ShopEconomyProbe.Probe(cardNode);

            var upgraded = CardModelReflection.IsUpgraded(model);
            var cost = CardModelReflection.GetCost(model);
            var (augmentBonus, augmentReason) = CardAugmentProbe.GetScoreDelta(cardNode, model);
            var score = RecommendationEngine.ScoreCard(
                internalName,
                upgraded,
                cost,
                state,
                shopEco,
                augmentBonus,
                augmentReason);

            var baseLabel = LocalizationManager.T("ui.base");
            var ctxLabel = LocalizationManager.T("ui.ctx");
            var header = string.Format(LocalizationManager.T("ui.score_header"),
                baseLabel, Math.Round(score.BaseScore),
                ctxLabel, Math.Round(score.ContextScore));

            var body = header;
            var reasonCount = Math.Min(score.ReasonKeys.Count, score.ReasonWeights.Count);
            for (var i = 0; i < reasonCount; i++)
            {
                var key = score.ReasonKeys[i];
                var weight = score.ReasonWeights[i];
                var sign = weight >= 0f ? "+" : "-";
                var reason = string.Format(
                    LocalizationManager.T("ui.reason_line"),
                    $"{sign} {LocalizationManager.T(key)} ({weight:+0.#;-0.#;0})");
                body += "\n" + reason;
            }

            if (!CardDatabase.Rows.ContainsKey(internalName))
            {
                body += "\n" + LocalizationManager.T("ui.missing_card");
            }

            label.Text = body;

            ApplyContextScoreChrome(border, bg, label, score.ContextScore);
        };
    }

    /// <summary>Chromatic accent from final context score (Ctx). Uses a wide hue spread so typical shop ranges (~25–55) read as clearly different tiers.</summary>
    private static void ApplyContextScoreChrome(ColorRect border, ColorRect bg, Label label, float ctx)
    {
        var accent = ContextScoreAccent(ctx);

        var bgTint = new Color(
            0.035f + accent.R * 0.16f,
            0.035f + accent.G * 0.16f,
            0.032f + accent.B * 0.14f,
            0.94f);

        bg.Color = bgTint;
        border.Color = accent;
        label.Modulate = accent;
    }

    /// <summary>Smooth ramp: cool rose (low) → orange → gold → cyan → mint → emerald (high).</summary>
    private static Color ContextScoreAccent(float ctx)
    {
        ReadOnlySpan<(float x, Color c)> stops =
        [
            (16f, new Color(0.72f, 0.38f, 0.48f)),
            (24f, new Color(0.95f, 0.38f, 0.28f)),
            (32f, new Color(0.98f, 0.62f, 0.22f)),
            (38f, new Color(0.98f, 0.88f, 0.35f)),
            (44f, new Color(0.28f, 0.82f, 0.98f)),
            (50f, new Color(0.32f, 0.94f, 0.58f)),
            (58f, new Color(0.18f, 0.98f, 0.45f)),
        ];

        if (ctx <= stops[0].x)
            return stops[0].c;
        if (ctx >= stops[^1].x)
            return stops[^1].c;

        for (var i = 0; i < stops.Length - 1; i++)
        {
            var a = stops[i];
            var b = stops[i + 1];
            if (ctx > b.x)
                continue;

            var u = (ctx - a.x) / (b.x - a.x);
            return a.c.Lerp(b.c, u);
        }

        return stops[^1].c;
    }
}

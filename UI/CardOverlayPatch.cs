using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using Sts2ContextCoach.Data;
using Sts2ContextCoach.Llm;
using Sts2ContextCoach.Localization;
using Sts2ContextCoach.Scoring;
using Sts2ContextCoach.State;
using Sts2ContextCoach.Telemetry;

namespace Sts2ContextCoach.UI;

[HarmonyPatch]
public static class CardOverlayPatch
{
    private static readonly object ExportUiLock = new();

    private static string? _lastLoggedScoringMode;

    /// <summary>After path briefly drops combat tokens (e.g. kill animation), keep light overlay mode for a few seconds.</summary>
    private static DateTimeOffset _combatUiHoldUntil;

    private static long _lastHoldPhaseObserveMs;

    private static long _lastShopLlmDiagMs;
    private static string _lastShopLlmDiagMsg = "";

    /// <summary>
    /// All purchasable shop <see cref="NCard"/>s seen this visit (staggered overlay timers + per-row BFS otherwise
    /// yield different sets → different LLM batch keys → requests supersede forever).
    /// </summary>
    private static readonly Dictionary<ulong, NCard> MerchantShopCoachUnion = new();

    private const string ExportLayerName = "ContextCoachExportLayer";
    private const float ExportButtonDefaultX = -430f;
    private const float ExportButtonDefaultY = 16f;

    /// <summary>Wider but shorter than reward overlay; shop uses a strongly negative Y so the box sits above the card, not on the art/title.</summary>
    private const float OverlayBoxWidth = 352f;
    private const float OverlayBoxHeight = 132f;

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
            // Hover previews are separate NCard instances; coaching them duplicates UI and breaks LLM batching.
            // Match tooltip layer nodes only — broad "HoverTip" matches e.g. NoHoverTips and skips all reward overlays.
            if (ChoiceRowProbe.IsHoverPreviewTree(__instance))
                return;

            var id = Guid.NewGuid().ToString("N")[..8];
            var uiName = "ContextCoachUI_" + id;

            var container = new Node2D { Name = uiName };

            var boxWidth = OverlayBoxWidth;
            var boxHeight = OverlayBoxHeight;

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

            var label = new RichTextLabel
            {
                BbcodeEnabled = true,
                FitContent = false,
                ScrollActive = false,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            label.SetSize(new Vector2(boxWidth, boxHeight));
            label.AddThemeFontSizeOverride("normal_font_size", 14);
            label.AddThemeColorOverride("default_color", new Color(0.94f, 0.94f, 0.94f, 1f));
            container.AddChild(label);

            __instance.AddChild(container);

            SetupTracker(__instance, container, border, bg, label, uiName);
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] Overlay patch error: {ex.Message}");
        }
    }

    /// <summary>RichTextLabel treats [ ] as BBCode; neutralize user/model text.</summary>
    private static string RtfSafe(string s) =>
        s.Replace("[", "\uFF3B", StringComparison.Ordinal).Replace("]", "\uFF3D", StringComparison.Ordinal);

    private static void AppendHeuristicReasons(System.Text.StringBuilder body, ScoreResult score)
    {
        var reasonCount = Math.Min(score.ReasonKeys.Count, score.ReasonWeights.Count);
        for (var i = 0; i < reasonCount; i++)
        {
            var key = score.ReasonKeys[i];
            var weight = score.ReasonWeights[i];
            var sign = weight >= 0f ? "+" : "-";
            var line = string.Format(
                LocalizationManager.T("ui.reason_line"),
                $"{sign} {LocalizationManager.T(key)} ({weight:+0.#;-0.#;0})");
            body.AppendLine(RtfSafe(line));
        }
    }

    private static void AppendMissingCardLine(System.Text.StringBuilder body, string internalName)
    {
        if (!CardDatabase.Rows.ContainsKey(internalName))
            body.AppendLine(RtfSafe(LocalizationManager.T("ui.missing_card")));
    }

    private static void SetupTracker(NCard cardNode, Node2D container, ColorRect border, ColorRect bg, RichTextLabel label, string myUiName)
    {
        var timer = new Godot.Timer
        {
            // Stagger ticks so many NCard._Ready overlays do not all fire in one frame.
            WaitTime = 0.35 + (float)Random.Shared.NextDouble() * 0.28,
            Autostart = true
        };
        container.AddChild(timer);

        const float defaultY = 160f;
        // NCard origin ~ card center; negative Y is toward top of screen. Keep bottom of box (shopY + height) above typical card top (~-200) so names/art stay visible.
        const float shopY = -360f;
        var boxWidth = OverlayBoxWidth;

        timer.Timeout += () =>
        {
            try
            {
                if (!GodotObject.IsInstanceValid(container) || !GodotObject.IsInstanceValid(cardNode)) return;

                ClassifyCardUiContext(cardNode, out var rawCombat, out var isCombat, out var isShop, out var isGridOrDeck);

                if (isCombat)
                {
                    MerchantShopCoachUnion.Clear();
                    container.Visible = false;
                    foreach (var child in cardNode.GetChildren())
                    {
                        if (!child.Name.ToString().StartsWith("ContextCoachUI", StringComparison.Ordinal))
                            continue;
                        if (child is CanvasItem item)
                            item.Visible = false;
                    }

                    // Raw combat: no reflection / disk (all hand cards tick together).
                    // Hold-only phase: path lost combat tokens briefly after lethal; cheap throttled observe for combat-end + logs.
                    if (!rawCombat)
                    {
                        var nowH = System.Environment.TickCount64;
                        if (_lastHoldPhaseObserveMs == 0 || nowH - _lastHoldPhaseObserveMs >= 400)
                        {
                            _lastHoldPhaseObserveMs = nowH;
                            var holdState = GameStateCache.GetStateForCard(cardNode);
                            RunLogger.ObserveRunState(holdState);
                        }
                    }

                    return;
                }

                EnsureExportUi(cardNode);

                var state = GameStateCache.GetStateForCard(cardNode);
                if (isShop && !isGridOrDeck && state.Gold == null)
                {
                    var hudGold = ShopEconomyProbe.TryResolveHudPlayerGold(cardNode);
                    if (hudGold.HasValue)
                        state.Gold = hudGold.Value;
                }

                RunLogger.ObserveRunState(state);

                foreach (var child in cardNode.GetChildren())
                {
                    var childName = child.Name.ToString();
                    if (childName.StartsWith("ContextCoachUI", StringComparison.Ordinal) && childName != myUiName)
                    {
                        child.Name = "Killed_" + Guid.NewGuid();
                        child.QueueFree();
                    }
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
                        if (!child.Name.ToString().StartsWith("ContextCoachUI", StringComparison.Ordinal))
                            continue;
                        if (child is CanvasItem item)
                            item.Visible = false;
                    }

                    return;
                }

                container.Visible = true;

                ShopEconomyContext? shopEco = null;
                if (isShop && !isGridOrDeck)
                {
                    var (rowMin, rowSlots) = ShopEconomyProbe.GetShopRowCardPriceStats(cardNode, state.Gold);
                    var raw = ShopEconomyProbe.Probe(cardNode, state.Gold);
                    shopEco = new ShopEconomyContext
                    {
                        CardPrice = raw.CardPrice,
                        IsDiscounted = raw.IsDiscounted,
                        RemovalServicePrice = raw.RemovalServicePrice,
                        RowMinListedCardPrice = rowSlots >= 2 ? rowMin : null,
                        RowListedPriceSlotCount = rowSlots
                    };
                }

                var upgraded = CardModelReflection.IsUpgraded(model);
                var cost = CardModelReflection.GetCost(model);
                var (augmentBonus, augmentReason) = CardAugmentProbe.GetScoreDelta(cardNode, model);

                var llmSurface = !isGridOrDeck;
                var shopRow = isShop && !isGridOrDeck;
                List<LlmCoachCandidate>? coachRow = null;
                // Build row whenever LLM mode is on (even without API key) so overlay can explain no-key vs empty row.
                if (llmSurface && ContextCoachConfig.IsLlmScoringEnabled)
                {
                    if (shopRow)
                    {
                        AccumulateMerchantShopCoachUnion(cardNode);
                        var union = SnapshotMerchantShopCoachUnion();
                        if (union.Count > 0)
                        {
                            coachRow = BuildCoachRowFromNodes(union, state, shopRow: true);
                            if (coachRow.Count == 0)
                                coachRow = BuildLlmCoachRow(cardNode, state, true);
                        }
                        else
                            coachRow = BuildLlmCoachRow(cardNode, state, true);
                    }
                    else
                        coachRow = BuildLlmCoachRow(cardNode, state, false);
                }

                var rowMatch = coachRow is { Count: > 0 }
                    ? FindRowCandidate(coachRow, internalName, upgraded, shopEco, shopRow)
                    : null;
                var score = rowMatch?.Heuristic ?? RecommendationEngine.ScoreCard(
                    internalName,
                    upgraded,
                    cost,
                    state,
                    shopEco,
                    augmentBonus,
                    augmentReason);

                TryLogDecision(
                    isShop && !isGridOrDeck ? "shop" : "card_reward",
                    internalName,
                    score,
                    state,
                    shopEco,
                    coachRow);

                var sm = (ContextCoachConfig.Current.ScoringMode ?? "heuristic").Trim();
                if (!string.Equals(_lastLoggedScoringMode, sm, StringComparison.OrdinalIgnoreCase))
                {
                    _lastLoggedScoringMode = sm;
                    Log.Info($"[ContextCoach] scoring_mode={sm} llm_api_key={(ContextCoachConfig.TryGetLlmApiKey() != null ? "set" : "missing")}");
                }

                var baseLabel = LocalizationManager.T("ui.base");
                var ctxLabel = LocalizationManager.T("ui.ctx");
                var headerPlain = string.Format(LocalizationManager.T("ui.score_header"),
                    baseLabel, Math.Round(score.BaseScore),
                    ctxLabel, Math.Round(score.ContextScore));

                var body = new System.Text.StringBuilder();
                string? llmBanner = null;
                string? llmNote = null;

                if (llmSurface && ContextCoachConfig.IsLlmScoringEnabled)
                {
                    if (coachRow == null || coachRow.Count == 0)
                    {
                        body.AppendLine(RtfSafe(headerPlain));
                        AppendHeuristicReasons(body, score);
                        AppendMissingCardLine(body, internalName);
                        var probeN = ChoiceRowProbe.CollectChoiceRowCardNodes(cardNode).Count;
                        var coachN = coachRow?.Count ?? 0;
                        body.AppendLine(RtfSafe(string.Format(
                            LocalizationManager.T("ui.llm_row_empty_detail"),
                            probeN,
                            coachN)));
                        Log.Info($"[ContextCoach][LLM] coach row empty probe_ncards={probeN} coach_candidates={coachN} card={internalName} screen={state.CurrentScreen ?? "?"}");
                    }
                    else if (ContextCoachConfig.TryGetLlmApiKey() == null)
                    {
                        body.AppendLine(RtfSafe(headerPlain));
                        AppendHeuristicReasons(body, score);
                        AppendMissingCardLine(body, internalName);
                        body.AppendLine(RtfSafe(LocalizationManager.T("ui.llm_no_key")));
                    }
                    else
                    {
                        var decisionType = shopRow ? "shop" : "card_reward";
                        var llmTimeoutSec = ContextCoachConfig.EffectiveLlmTimeoutSeconds;
                        var analyzingLine = string.Format(LocalizationManager.T("ui.llm_analyzing"), llmTimeoutSec);
                        try
                        {
                            LlmBatchCoordinator.ScheduleBatch(decisionType, state, coachRow);
                            var batchKey = LlmBatchCoordinator.ComputeBatchKey(decisionType, state, coachRow);
                            var adviceKey = $"{internalName}|u{(upgraded ? 1 : 0)}";
                            var llmStatus = LlmBatchCoordinator.TryGetAdvice(
                                adviceKey,
                                batchKey,
                                out var llmAdvice,
                                out var llmErr);
                            switch (llmStatus)
                            {
                                case LlmOverlayBatchStatus.Pending:
                                    body.AppendLine(RtfSafe(headerPlain));
                                    AppendHeuristicReasons(body, score);
                                    AppendMissingCardLine(body, internalName);
                                    body.AppendLine(RtfSafe(analyzingLine));
                                    break;
                                case LlmOverlayBatchStatus.Failed:
                                    body.AppendLine(RtfSafe(headerPlain));
                                    AppendHeuristicReasons(body, score);
                                    AppendMissingCardLine(body, internalName);
                                    body.AppendLine(RtfSafe(string.Format(LocalizationManager.T("ui.llm_failed"), llmErr ?? "?")));
                                    Log.Warn($"[ContextCoach][LLM] overlay shows failure: {llmErr ?? "?"}");
                                    break;
                                case LlmOverlayBatchStatus.Ready:
                                    if (llmAdvice?.CoachScore is { } cs)
                                    {
                                        llmBanner =
                                            $"[center][font_size=16][b][color=#7ee8ff]{RtfSafe(string.Format(LocalizationManager.T("ui.llm_score_line"), cs))}[/color][/b][/font_size][/center]";
                                    }

                                    if (llmAdvice != null && !string.IsNullOrWhiteSpace(llmAdvice.CoachNote))
                                        llmNote = llmAdvice.CoachNote;

                                    if (llmBanner != null)
                                        body.AppendLine(llmBanner);
                                    body.AppendLine(RtfSafe(headerPlain));
                                    AppendHeuristicReasons(body, score);
                                    AppendMissingCardLine(body, internalName);
                                    if (!string.IsNullOrWhiteSpace(llmNote))
                                        body.AppendLine(RtfSafe(llmNote));
                                    break;
                                default:
                                    // Idle = batch key drift or state reset between ScheduleBatch and TryGetAdvice; avoid empty overlay.
                                    body.AppendLine(RtfSafe(headerPlain));
                                    AppendHeuristicReasons(body, score);
                                    AppendMissingCardLine(body, internalName);
                                    body.AppendLine(RtfSafe(analyzingLine));
                                    break;
                            }
                        }
                        catch (Exception llmEx)
                        {
                            Log.Warn($"[ContextCoach][LLM] overlay LLM branch failed: {llmEx.Message}");
                            body.AppendLine(RtfSafe(headerPlain));
                            AppendHeuristicReasons(body, score);
                            AppendMissingCardLine(body, internalName);
                            body.AppendLine(RtfSafe(string.Format(LocalizationManager.T("ui.llm_failed"), llmEx.Message)));
                        }
                    }
                }
                else if (llmSurface)
                {
                    body.AppendLine(RtfSafe(headerPlain));
                    AppendHeuristicReasons(body, score);
                    AppendMissingCardLine(body, internalName);
                    body.AppendLine(RtfSafe(LocalizationManager.T("ui.llm_skipped_heuristic")));
                }
                else
                {
                    body.AppendLine(RtfSafe(headerPlain));
                    AppendHeuristicReasons(body, score);
                    AppendMissingCardLine(body, internalName);
                    body.AppendLine(RtfSafe(LocalizationManager.T("ui.llm_skipped_surface")));
                }

                label.Text = body.ToString().TrimEnd();

                ApplyContextScoreChrome(border, bg, label, score.ContextScore);
            }
            catch (Exception ex)
            {
                Log.Warn($"[ContextCoach] overlay tick failed: {ex.Message}");
            }
        };
    }

    /// <summary>Shop: whole merchant. Card rewards: sibling row or BFS (isolated slots). Fallback: siblings.</summary>
    private static List<LlmCoachCandidate> BuildLlmCoachRow(NCard anchor, GameState state, bool shopRow)
    {
        if (shopRow)
        {
            var shopNodes = ShopEconomyProbe.CollectShopCardNodes(anchor);
            List<NCard>? useNodes = null;
            var mode = "sibling";

            if (shopNodes.Count >= 2)
            {
                useNodes = shopNodes;
                mode = "shop_root_bfs";
            }
            else
            {
                var rowFb = ChoiceRowProbe.CollectChoiceRowCardNodes(anchor);
                if (rowFb.Count >= 2)
                {
                    useNodes = rowFb;
                    mode = "shop_row_probe_fallback";
                }
                else if (shopNodes.Count == 1)
                {
                    useNodes = shopNodes;
                    mode = "shop_root_bfs_single";
                }
                else if (rowFb.Count == 1)
                {
                    useNodes = rowFb;
                    mode = "shop_row_probe_single";
                }
            }

            if (useNodes is { Count: > 0 })
            {
                LogShopLlmDiag(
                    $"shop LLM row mode={mode} final={useNodes.Count} shopRootBfs={shopNodes.Count} screen={state.CurrentScreen ?? "?"}");
                return BuildCoachRowFromNodes(useNodes, state, shopRow: true);
            }

            LogShopLlmDiag(
                $"shop LLM row FALLBACK siblings only — shopRootBfs={shopNodes.Count} screen={state.CurrentScreen ?? "?"}");
        }
        else
        {
            var choiceNodes = ChoiceRowProbe.CollectChoiceRowCardNodes(anchor);
            if (choiceNodes.Count > 0)
                return BuildCoachRowFromNodes(choiceNodes, state, shopRow: false);
        }

        return BuildCoachRow(anchor, state, shopRow);
    }

    private static void LogShopLlmDiag(string message)
    {
        var now = System.Environment.TickCount64;
        if (message == _lastShopLlmDiagMsg && now - _lastShopLlmDiagMs < 5000)
            return;
        _lastShopLlmDiagMs = now;
        _lastShopLlmDiagMsg = message;
        Log.Info($"[ContextCoach][LLM] {message}");
    }

    private static void AccumulateMerchantShopCoachUnion(NCard anchor)
    {
        foreach (var nc in ShopEconomyProbe.CollectShopCardNodes(anchor))
        {
            if (GodotObject.IsInstanceValid(nc))
                MerchantShopCoachUnion[nc.GetInstanceId()] = nc;
        }

        if (MerchantShopCoachUnion.Count < 2)
        {
            foreach (var nc in ChoiceRowProbe.CollectChoiceRowCardNodes(anchor))
            {
                if (GodotObject.IsInstanceValid(nc))
                    MerchantShopCoachUnion[nc.GetInstanceId()] = nc;
            }
        }
    }

    private static List<NCard> SnapshotMerchantShopCoachUnion()
    {
        List<ulong>? dead = null;
        foreach (var kv in MerchantShopCoachUnion)
        {
            if (!GodotObject.IsInstanceValid(kv.Value))
                (dead ??= new List<ulong>()).Add(kv.Key);
        }

        if (dead != null)
        {
            foreach (var k in dead)
                MerchantShopCoachUnion.Remove(k);
        }

        return MerchantShopCoachUnion.Values
            .Where(GodotObject.IsInstanceValid)
            .OrderBy(nc => CardModelReflection.GetInternalName(nc) ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(nc => nc.GetInstanceId())
            .ToList();
    }

    private static List<LlmCoachCandidate> BuildCoachRow(NCard anchor, GameState state, bool shopRow)
    {
        var list = new List<LlmCoachCandidate>();
        try
        {
            var parent = anchor.GetParent();
            if (parent == null)
                return list;

            foreach (var child in parent.GetChildren())
            {
                if (child is not NCard nc) continue;
                TryAddCoachCandidate(nc, state, shopRow, list);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] BuildCoachRow failed: {ex.Message}");
        }

        return list;
    }

    private static List<LlmCoachCandidate> BuildCoachRowFromNodes(IEnumerable<NCard> nodes, GameState state, bool shopRow)
    {
        var list = new List<LlmCoachCandidate>();
        try
        {
            foreach (var nc in nodes)
                TryAddCoachCandidate(nc, state, shopRow, list);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] BuildCoachRowFromNodes failed: {ex.Message}");
        }

        return list;
    }

    private static void TryAddCoachCandidate(NCard nc, GameState state, bool shopRow, List<LlmCoachCandidate> list)
    {
        var m = CardModelReflection.GetModel(nc);
        if (m == null) return;
        var name = m.GetType().Name;
        if (name.StartsWith("Strike", StringComparison.Ordinal) ||
            name.StartsWith("Defend", StringComparison.Ordinal))
            return;

        ShopEconomyContext? se = null;
        if (shopRow)
        {
            var (rowMin, rowSlots) = ShopEconomyProbe.GetShopRowCardPriceStats(nc, state.Gold);
            var raw = ShopEconomyProbe.Probe(nc, state.Gold);
            se = new ShopEconomyContext
            {
                CardPrice = raw.CardPrice,
                IsDiscounted = raw.IsDiscounted,
                RemovalServicePrice = raw.RemovalServicePrice,
                RowMinListedCardPrice = rowSlots >= 2 ? rowMin : null,
                RowListedPriceSlotCount = rowSlots
            };
        }

        var up = CardModelReflection.IsUpgraded(m);
        var cst = CardModelReflection.GetCost(m);
        if (!cst.HasValue && MetadataRepository.TryGetCard(name, out var meta) && meta != null)
        {
            if (up && meta.Cost is int bcUp)
            {
                var u = bcUp + (meta.UpgradeCostDelta ?? 0);
                if (u is >= 0 and <= 9)
                    cst = u;
            }
            else if (!up && meta.Cost is int bc && bc is >= 0 and <= 9)
                cst = bc;
        }

        var (ab, ar) = CardAugmentProbe.GetScoreDelta(nc, m);
        var scr = RecommendationEngine.ScoreCard(name, up, cst, state, se, ab, ar);
        list.Add(LlmCoachCandidate.FromRuntime(name, up, cst, se, ab, ar, scr));
    }

    private static LlmCoachCandidate? FindRowCandidate(
        IReadOnlyList<LlmCoachCandidate> row,
        string internalName,
        bool upgraded,
        ShopEconomyContext? shopEco,
        bool shopRow)
    {
        if (shopRow && shopEco is { CardPrice: not null })
        {
            foreach (var c in row)
            {
                if (!string.Equals(c.InternalName, internalName, StringComparison.Ordinal) || c.Upgraded != upgraded)
                    continue;
                if (c.ShopPrice == shopEco.Value.CardPrice)
                    return c;
            }
        }

        return row.FirstOrDefault(c =>
            string.Equals(c.InternalName, internalName, StringComparison.Ordinal) && c.Upgraded == upgraded);
    }

    private static void ClassifyCardUiContext(NCard cardNode, out bool rawCombat, out bool isCombat, out bool isShop, out bool isGridOrDeck)
    {
        rawCombat = false;
        isShop = false;
        isGridOrDeck = false;
        Node? current = cardNode.GetParent();
        while (current != null)
        {
            var n = current.Name.ToString().ToLowerInvariant();

            if (ShopEconomyProbe.IsShopLikeNodeName(current.Name.ToString()))
                isShop = true;

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

        var path = CombatScreenHeuristic.BuildAncestorPath(cardNode);
        var rewardPick = CombatScreenHeuristic.PathIndicatesCardRewardPick(path);

        // Some pick UIs omit "reward"/"choose" in node names but sit under combat with a small sibling row.
        if (!rewardPick)
        {
            var pl = path.ToLowerInvariant();
            if (!pl.Contains("hand", StringComparison.Ordinal) &&
                !pl.Contains("shop", StringComparison.Ordinal) &&
                !pl.Contains("merchant", StringComparison.Ordinal) &&
                !pl.Contains("store", StringComparison.Ordinal) &&
                !pl.Contains("vendor", StringComparison.Ordinal) &&
                !pl.Contains("bazaar", StringComparison.Ordinal) &&
                !pl.Contains("kiosk", StringComparison.Ordinal))
            {
                var sib = CountCoachableSiblingNcards(cardNode);
                if (sib is >= 2 and <= 8 &&
                    (pl.Contains("combat", StringComparison.Ordinal) ||
                     pl.Contains("battle", StringComparison.Ordinal) ||
                     pl.Contains("fight", StringComparison.Ordinal) ||
                     pl.Contains("encounter", StringComparison.Ordinal)))
                    rewardPick = true;
            }
        }

        // "Choose a card" UIs often include *Select* in a node name; do not treat as deck/grid browser.
        if (rewardPick)
            isGridOrDeck = false;

        // Rewards can sit under combat scene nodes; still show coach overlay (and LLM) for the pick row.
        rawCombat = CombatScreenHeuristic.PathLooksLikeCombat(path) && !rewardPick;
        if (rawCombat)
            _combatUiHoldUntil = DateTimeOffset.UtcNow.AddSeconds(3.5);

        var inCombatHold = DateTimeOffset.UtcNow < _combatUiHoldUntil;
        isCombat = rewardPick ? false : rawCombat || inCombatHold;
    }

    private static int CountCoachableSiblingNcards(NCard cardNode)
    {
        var parent = cardNode.GetParent();
        if (parent == null) return 0;
        var n = 0;
        foreach (var ch in parent.GetChildren())
        {
            if (ch is not NCard nc) continue;
            var m = CardModelReflection.GetModel(nc);
            if (m == null) continue;
            var name = m.GetType().Name;
            if (name.StartsWith("Strike", StringComparison.Ordinal) ||
                name.StartsWith("Defend", StringComparison.Ordinal))
                continue;
            n++;
        }

        return n;
    }

    private static void TryLogDecision(
        string decisionType,
        string cardInternalName,
        ScoreResult score,
        GameState state,
        ShopEconomyContext? shopEco,
        IReadOnlyList<LlmCoachCandidate>? coachRow)
    {
        try
        {
            var source = decisionType == "shop" && shopEco.HasValue
                ? $"shop(price={shopEco.Value.CardPrice?.ToString() ?? "?"},sale={shopEco.Value.IsDiscounted})"
                : "overlay";

            var map = new Dictionary<string, ScoreResult>(StringComparer.Ordinal);
            List<string> candidatesOrdered = [];

            if (coachRow is { Count: > 0 })
            {
                foreach (var c in coachRow)
                {
                    if (string.IsNullOrWhiteSpace(c.InternalName))
                        continue;
                    if (!map.TryGetValue(c.InternalName, out var existing) ||
                        c.Heuristic.ContextScore > existing.ContextScore)
                        map[c.InternalName] = c.Heuristic;
                }

                var seenRow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in coachRow)
                {
                    if (string.IsNullOrWhiteSpace(c.InternalName) || !seenRow.Add(c.InternalName))
                        continue;
                    candidatesOrdered.Add(c.InternalName);
                }
            }
            else if (string.Equals(decisionType, "card_reward", StringComparison.OrdinalIgnoreCase) &&
                     state.RewardCards is { Count: > 0 } reward)
            {
                foreach (var c in reward)
                {
                    if (string.IsNullOrWhiteSpace(c.Name))
                        continue;
                    var cost = ResolveCostForTelemetry(c.Name, c.Upgraded);
                    map[c.Name] = RecommendationEngine.ScoreCard(c.Name, c.Upgraded, cost, state, null, 0f, null);
                }

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in reward)
                {
                    if (string.IsNullOrWhiteSpace(c.Name) || !seen.Add(c.Name))
                        continue;
                    candidatesOrdered.Add(c.Name);
                }
            }
            else
            {
                candidatesOrdered.Add(cardInternalName);
                map[cardInternalName] = score;
            }

            if (candidatesOrdered.Count == 0)
                candidatesOrdered.Add(cardInternalName);

            RunLogger.LogDecision(
                decisionType,
                state,
                candidatesOrdered,
                map,
                playerChoice: null,
                source: source);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] decision log skipped: {ex.Message}");
        }
    }

    private static int? ResolveCostForTelemetry(string internalName, bool upgraded)
    {
        int? cst = null;
        if (!MetadataRepository.TryGetCard(internalName, out var meta) || meta == null)
            return null;
        if (upgraded && meta.Cost is int bcUp)
        {
            var u = bcUp + (meta.UpgradeCostDelta ?? 0);
            if (u is >= 0 and <= 9)
                cst = u;
        }
        else if (!upgraded && meta.Cost is int bc && bc is >= 0 and <= 9)
            cst = bc;

        return cst;
    }

    private static void EnsureExportUi(Node anyNodeInTree)
    {
        try
        {
            var root = anyNodeInTree.GetTree().Root;
            lock (ExportUiLock)
            {
                if (root.GetNodeOrNull<CanvasLayer>(ExportLayerName) != null)
                    return;

                var layer = new CanvasLayer { Name = ExportLayerName, Layer = 120 };
                var button = new Button
                {
                    Text = "Export & Share",
                    Flat = false,
                    TooltipText = "Export local gameplay logs (zip) for manual sharing."
                };
                button.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                button.Position = new Vector2(ExportButtonDefaultX, ExportButtonDefaultY);
                button.CustomMinimumSize = new Vector2(220f, 40f);
                button.Size = new Vector2(220f, 40f);
                button.MouseFilter = Control.MouseFilterEnum.Stop;
                button.FocusMode = Control.FocusModeEnum.All;
                button.Modulate = Colors.White;
                button.AddThemeFontSizeOverride("font_size", 15);
                button.AddThemeColorOverride("font_color", Colors.White);
                button.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 0.92f));
                button.AddThemeColorOverride("font_pressed_color", new Color(0.95f, 0.95f, 1f));
                var sbNormal = new StyleBoxFlat
                {
                    BgColor = new Color(0.16f, 0.38f, 0.58f, 0.98f),
                    BorderColor = new Color(0.55f, 0.78f, 1f),
                    BorderWidthLeft = 2,
                    BorderWidthTop = 2,
                    BorderWidthRight = 2,
                    BorderWidthBottom = 2,
                    CornerRadiusTopLeft = 5,
                    CornerRadiusTopRight = 5,
                    CornerRadiusBottomRight = 5,
                    CornerRadiusBottomLeft = 5,
                    ContentMarginLeft = 10,
                    ContentMarginRight = 10,
                    ContentMarginTop = 8,
                    ContentMarginBottom = 8
                };
                var sbHover = (StyleBoxFlat)sbNormal.Duplicate();
                sbHover.BgColor = new Color(0.2f, 0.48f, 0.72f, 1f);
                var sbPress = (StyleBoxFlat)sbNormal.Duplicate();
                sbPress.BgColor = new Color(0.1f, 0.28f, 0.45f, 1f);
                button.AddThemeStyleboxOverride("normal", sbNormal);
                button.AddThemeStyleboxOverride("hover", sbHover);
                button.AddThemeStyleboxOverride("pressed", sbPress);
                EnableDrag(button);
                layer.AddChild(button);

                var llmToggle = new CheckButton
                {
                    Name = "ContextCoachLlmToggle",
                    Text = LocalizationManager.T("ui.llm_toggle"),
                    ButtonPressed = ContextCoachConfig.IsLlmScoringEnabled
                };
                llmToggle.SetAnchorsPreset(Control.LayoutPreset.TopRight);
                llmToggle.Position = new Vector2(ExportButtonDefaultX, ExportButtonDefaultY + 48f);
                llmToggle.CustomMinimumSize = new Vector2(220f, 32f);
                llmToggle.Size = new Vector2(220f, 32f);
                llmToggle.AddThemeFontSizeOverride("font_size", 14);
                llmToggle.MouseFilter = Control.MouseFilterEnum.Stop;
                llmToggle.Toggled += pressed =>
                {
                    try
                    {
                        ContextCoachConfig.Current.ScoringMode = pressed ? "llm" : "heuristic";
                        ContextCoachConfig.Save();
                        if (!pressed)
                            LlmBatchCoordinator.ResetForHeuristicMode();
                        Log.Info($"[ContextCoach] scoring_mode={(pressed ? "llm" : "heuristic")} (UI toggle)");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[ContextCoach] LLM toggle save failed: {ex.Message}");
                    }
                };
                layer.AddChild(llmToggle);

                LlmSettingsPanel.Attach(layer, ExportButtonDefaultX, ExportButtonDefaultY + 88f);

                var dialog = new AcceptDialog
                {
                    Name = "ContextCoachExportDialog",
                    Title = "Context Coach Export",
                    DialogText =
                        "Exported a local ZIP with gameplay-only data (events, summary, metadata; plus run/llm/*.json when llm_mirror_transcripts_into_run_folder is true in contextcoach.config). No system identity or file paths are included. Please manually attach the ZIP in the issue form."
                };
                layer.AddChild(dialog);

                button.Pressed += () =>
                {
                    try
                    {
                        var zip = RunLogger.ExportLogs();
                        if (!string.IsNullOrWhiteSpace(zip))
                        {
                            var runCount = Math.Max(1, RunLogger.LastExportedRunCount);
                            var llmZipNote = ContextCoachConfig.Current.LlmMirrorTranscriptsIntoRunFolder
                                ? "Each run may include llm/coach-*.json (LLM prompts/responses) when LLM was used."
                                : "LLM correlation is in events.jsonl (llm_coach_batch / llm_deck_summary). Set llm_mirror_transcripts_into_run_folder to also bundle per-request transcript JSON.";
                            dialog.DialogText =
                                $"Export complete.\n{zip}\n\nIncludes unpublished run logs: {runCount} run(s)\nEach run contains events.jsonl, summary.json, metadata.json.\n{llmZipNote}\nGameplay data only. No auto-upload.\nPlease attach this ZIP manually in the issue form.";
                        }
                        else
                        {
                            dialog.DialogText = "No active run logs found to export yet.";
                        }

                        dialog.PopupCentered(new Vector2I(760, 220));

                        if (ContextCoachConfig.Current.PromptForUpload && !string.IsNullOrWhiteSpace(zip))
                        {
                            var url = BuildIssueUrl(RunLogger.CurrentRunId, RunLogger.LastExportedRunCount);
                            OS.ShellOpen(url);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[ContextCoach] Export & Share failed: {ex.Message}");
                    }
                };

                root.AddChild(layer);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] Unable to create Export UI: {ex.Message}");
        }
    }

    private static string BuildIssueUrl(string? runId, int exportedRunCount)
    {
        var count = exportedRunCount <= 0 ? 1 : exportedRunCount;
        var body =
            "Sharing Context Coach local telemetry export.%0A%0A" +
            $"- run_id: {Uri.EscapeDataString(runId ?? "unknown")}%0A" +
            $"- run_count_in_zip: {count}%0A" +
            "- attached: sts2-context-coach-<bundle_or_run_id>.zip%0A" +
            "- notes: (optional)%0A%0A" +
            "Please attach the generated ZIP manually using GitHub issue attachments (drag/drop).";
        return $"https://github.com/PPPSDavid/sts2-context-coach/issues/new?title=Telemetry%20Export%20{Uri.EscapeDataString(runId ?? "run")}&body={body}";
    }

    private static void EnableDrag(Button button)
    {
        var dragging = false;
        button.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                dragging = mb.Pressed;
            }
            else if (e is InputEventMouseMotion mm && dragging)
            {
                var vp = button.GetViewport();
                var visible = vp.GetVisibleRect().Size;
                var next = button.Position + mm.Relative;
                var minX = -visible.X + 20f;
                var maxX = -20f;
                var minY = 6f;
                var maxY = MathF.Max(6f, visible.Y - button.Size.Y - 6f);
                button.Position = new Vector2(
                    Math.Clamp(next.X, minX, maxX),
                    Math.Clamp(next.Y, minY, maxY));
            }
        };
    }

    /// <summary>Chromatic accent from final context score (Ctx). Uses a wide hue spread so typical shop ranges (~25–55) read as clearly different tiers.</summary>
    private static void ApplyContextScoreChrome(ColorRect border, ColorRect bg, RichTextLabel label, float ctx)
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

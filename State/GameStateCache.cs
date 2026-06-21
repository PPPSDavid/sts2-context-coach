using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using Sts2ContextCoach.Diagnostics;
using Sts2ContextCoach.Llm;
using Sts2ContextCoach.Scoring;

namespace Sts2ContextCoach.State;

/// <summary>
/// Heavy reflection runs at most once per interval and is shared by all card overlays.
/// </summary>
public static class GameStateCache
{
    private static readonly object Gate = new();
    private static long _lastRefreshMs;
    private static GameState? _global;

    private static string? _coachHistoryRunIdentity;
    private static int? _coachHistorySavePlayerCount;
    private static int? _coachHistoryLastFloor;

    /// <summary>How often to re-run reflection over the game assembly (ms).</summary>
    public const int RefreshIntervalMs = 1200;

    public static GameState GetStateForCard(NCard? anchor)
    {
        var shared = GetOrRefreshGlobal();
        return GameStateExtractor.MergePerCard(shared, anchor);
    }

    private static GameState GetOrRefreshGlobal()
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (_global != null && now - _lastRefreshMs < RefreshIntervalMs)
                return _global;

            _lastRefreshMs = now;
            _global = GameStateExtractor.BuildGlobalReflectionState(out var provenance);
            MaybeResetCoachHistoryForNewRun(_global);
            _global.CachedDeckAnalysis = DeckAnalyzer.Analyze(_global);
            if (ContextCoachLogging.Verbose)
                Log.Info($"[ContextCoach] {ContextCoachLogging.FormatSnapshot(_global, provenance)} (interval={RefreshIntervalMs}ms)");
            return _global;
        }
    }

    /// <summary>For tests or after loading a save; clears cached snapshot.</summary>
    public static void Invalidate()
    {
        ShopEconomyProbe.ClearCache();
        GameStateExtractor.ClearReflectionCaches();
        lock (Gate)
        {
            _global = null;
            _lastRefreshMs = 0;
            _coachHistoryRunIdentity = null;
            _coachHistorySavePlayerCount = null;
            _coachHistoryLastFloor = null;
        }
    }

    private static void MaybeResetCoachHistoryForNewRun(GameState state)
    {
        var cleared = false;
        var id = state.RunIdentity;
        if (!string.IsNullOrWhiteSpace(id) &&
            _coachHistoryRunIdentity != null &&
            !string.Equals(id, _coachHistoryRunIdentity, StringComparison.Ordinal))
        {
            CoachPickHistory.Clear();
            cleared = true;
        }

        var slots = state.SavePlayerCount;
        if (slots != null &&
            _coachHistorySavePlayerCount != null &&
            slots != _coachHistorySavePlayerCount)
        {
            if (!cleared)
                CoachPickHistory.Clear();
            cleared = true;
        }

        var floor = state.Floor;
        if (floor != null && _coachHistoryLastFloor != null && floor < _coachHistoryLastFloor - 2)
        {
            if (!cleared)
                CoachPickHistory.Clear();
            cleared = true;
        }

        if (cleared)
            Log.Info("[ContextCoach][LLM] coach_history cleared (new run: save id, player slots, or floor regression)");

        if (!string.IsNullOrWhiteSpace(id))
            _coachHistoryRunIdentity = id;
        else if (cleared)
            _coachHistoryRunIdentity = null;

        if (slots != null)
            _coachHistorySavePlayerCount = slots;
        if (floor != null)
            _coachHistoryLastFloor = floor;
    }
}

using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using Sts2ContextCoach.Diagnostics;
using Sts2ContextCoach.Llm;
using Sts2ContextCoach.Scoring;
using Sts2ContextCoach.Telemetry;

namespace Sts2ContextCoach.State;

/// <summary>
/// Heavy reflection runs at most once per interval and is shared by all card overlays.
/// </summary>
public static class GameStateCache
{
    private static readonly object Gate = new();
    /// <summary>
    /// Ensures only one thread runs reflection + deck analysis at a time (game objects are not thread-safe).
    /// </summary>
    private static readonly object BuildLock = new();
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
        // Victory/defeat screens often have no shop/reward NCard timers; sample the scene tree here.
        try
        {
            RunLogger.TryProbeTerminalFromSceneTree(shared);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] TryProbeTerminalFromSceneTree failed: {ex.Message}");
        }

        return GameStateExtractor.MergePerCard(shared, anchor);
    }

    /// <summary>
    /// Periodic tick when logging is on: refreshes shared state, probes the full UI tree for terminal
    /// screens, and runs <see cref="RunLogger.ObserveRunState"/> so runs can close even if no
    /// <see cref="NCard"/> overlay is active (empty hand / pure end-of-run UI).
    /// </summary>
    public static void TickRunTelemetryHeartbeat()
    {
        if (!RunLogger.IsEnabled)
            return;

        try
        {
            var shared = GetOrRefreshGlobal();
            RunLogger.TryProbeTerminalFromSceneTree(shared);
            RunLogger.ObserveRunState(shared);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] TickRunTelemetryHeartbeat failed: {ex.Message}");
        }
    }

    private static GameState GetOrRefreshGlobal()
    {
        var now = Environment.TickCount64;
        lock (Gate)
        {
            if (_global != null && now - _lastRefreshMs < RefreshIntervalMs)
                return _global;
        }

        // Reflection + deck analysis can take tens–hundreds of ms. Do not hold Gate during the work
        // (so readers are not queued behind a long critical section), but serialize builds: game
        // / Godot-backed reflection is not safe to run concurrently from multiple overlay threads.
        string provenance;
        GameState built;
        lock (BuildLock)
        {
            lock (Gate)
            {
                if (_global != null && Environment.TickCount64 - _lastRefreshMs < RefreshIntervalMs)
                    return _global;
            }

            var buildStarted = Environment.TickCount64;
            built = GameStateExtractor.BuildGlobalReflectionState(out provenance);
            built.CachedDeckAnalysis = DeckAnalyzer.Analyze(built);
            var buildMs = Environment.TickCount64 - buildStarted;
            if (buildMs > 1500)
                Log.Warn($"[ContextCoach] GameStateCache reflection+analyze slow: {buildMs}ms (threshold 1500ms)");
        }

        lock (Gate)
        {
            var now2 = Environment.TickCount64;
            if (_global != null && now2 - _lastRefreshMs < RefreshIntervalMs)
                return _global;

            MaybeResetCoachHistoryForNewRun(built);
            _global = built;
            _lastRefreshMs = Environment.TickCount64;
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

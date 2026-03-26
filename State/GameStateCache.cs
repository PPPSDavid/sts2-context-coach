using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Cards;
using Sts2ContextCoach.Diagnostics;

namespace Sts2ContextCoach.State;

/// <summary>
/// Heavy reflection runs at most once per interval and is shared by all card overlays.
/// </summary>
public static class GameStateCache
{
    private static readonly object Gate = new();
    private static long _lastRefreshMs;
    private static GameState? _global;

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
            Log.Info($"[ContextCoach] {ContextCoachLogging.FormatSnapshot(_global, provenance)} (interval={RefreshIntervalMs}ms; verbose=STS2_CONTEXT_COACH_VERBOSE=1)");
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
        }
    }
}

using Godot;
using MegaCrit.Sts2.Core.Logging;
using Sts2ContextCoach.State;

namespace Sts2ContextCoach.Telemetry;

/// <summary>
/// Attaches a low-frequency <see cref="Timer"/> to the scene root so <see cref="GameStateCache.TickRunTelemetryHeartbeat"/>
/// runs without relying on <see cref="MegaCrit.Sts2.Core.Nodes.Cards.NCard"/> overlay timers alone.
/// </summary>
public static class RunTelemetryHeartbeat
{
    private const double WaitSeconds = 0.5;
    private const int MaxTreeWaitAttempts = 600;
    private static bool _attachScheduled;
    private static int _treeWaitAttempts;

    public static void TryScheduleAttach()
    {
        if (_attachScheduled)
            return;
        _attachScheduled = true;
        Callable.From(AttachWhenTreeReady).CallDeferred();
    }

    private static void AttachWhenTreeReady()
    {
        if (Engine.GetMainLoop() is not SceneTree tree || tree.Root == null)
        {
            if (_treeWaitAttempts++ < MaxTreeWaitAttempts)
                Callable.From(AttachWhenTreeReady).CallDeferred();
            else
                Log.Warn("[ContextCoach] RunTelemetryHeartbeat: SceneTree not ready; terminal probe stays card-driven only.");
            return;
        }

        var root = tree.Root;
        foreach (var child in root.GetChildren())
        {
            if (child is Godot.Timer t && t.Name == "ContextCoachRunTelemetryHeartbeat")
                return;
        }

        var timer = new Godot.Timer
        {
            WaitTime = WaitSeconds,
            OneShot = false,
            Autostart = true,
            Name = "ContextCoachRunTelemetryHeartbeat"
        };
        timer.Timeout += OnTimeout;
        root.AddChild(timer);
        Log.Info($"[ContextCoach] RunTelemetryHeartbeat: timer attached ({WaitSeconds}s)");
    }

    private static void OnTimeout()
    {
        GameStateCache.TickRunTelemetryHeartbeat();
    }
}

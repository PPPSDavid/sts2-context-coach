using System.Diagnostics;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2ContextCoach.Diagnostics;

/// <summary>
/// Forwards Context Coach diagnostics to STS2 <see cref="Log"/> when running inside the game.
/// Under xUnit / Visual Studio Test, writes to <see cref="Trace"/> instead so the host never
/// touches <see cref="Log"/>'s static initializer (it pulls in Godot native interop and can
/// fault with access violations in a plain <c>dotnet test</c> process).
/// </summary>
internal static class CoachGameLog
{
    private static readonly Lazy<bool> PreferTrace = new(DetectTestLikeHost);

    private static bool DetectTestLikeHost()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = asm.GetName().Name ?? string.Empty;
                if (n.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (string.Equals(n, "xunit.runner.visualstudio.testadapter", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // If enumeration fails, default to game logging.
        }

        return false;
    }

    public static void Info(string message)
    {
        if (PreferTrace.Value)
        {
            Trace.WriteLine(message);
            return;
        }

        Log.Info(message);
    }

    public static void Warn(string message)
    {
        if (PreferTrace.Value)
        {
            Trace.WriteLine("[WARN] " + message);
            return;
        }

        Log.Warn(message);
    }

    public static void Error(string message)
    {
        if (PreferTrace.Value)
        {
            Trace.WriteLine("[ERROR] " + message);
            return;
        }

        Log.Error(message);
    }
}

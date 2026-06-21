namespace Sts2ContextCoach.Telemetry;

/// <summary>
/// Maps HP + UI path fragments to a terminal run outcome for <c>run_finished</c> / <c>summary.json</c>.
/// </summary>
public static class RunOutcomeClassifier
{
    /// <summary>Returns <c>victory</c>, <c>defeat</c>, or null when the run should stay active.</summary>
    public static string? Classify(int? hp, string? screen)
    {
        if (hp is <= 0)
            return "defeat";

        if (string.IsNullOrWhiteSpace(screen))
            return null;

        var s = screen;
        // Do not use bare "win" — it matches "Window" in many Godot UI paths and falsely ends the run.
        if (s.Contains("victory", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("credits", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("RunComplete", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("run_complete", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("NeoVictory", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("VictoryScreen", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("RunVictory", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("EndOfRun", StringComparison.OrdinalIgnoreCase))
            return "victory";

        if (s.Contains("defeat", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("death", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("gameover", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("GameOver", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("YouDied", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("RunLost", StringComparison.OrdinalIgnoreCase))
            return "defeat";

        return null;
    }
}

using MegaCrit.Sts2.Core.Logging;
using Sts2ContextCoach.State;
using System.Text.Json;

namespace Sts2ContextCoach.Diagnostics;

internal static class ContextCoachLogging
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static bool Verbose =>
        string.Equals(Environment.GetEnvironmentVariable("STS2_CONTEXT_COACH_VERBOSE"), "1", StringComparison.OrdinalIgnoreCase);

    public static bool DumpStateEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("STS2_CONTEXT_COACH_DUMP_STATE"), "1", StringComparison.OrdinalIgnoreCase);

    public static void VerboseInfo(string message)
    {
        if (!Verbose) return;
        Log.Info($"[ContextCoach] {message}");
    }

    public static string FormatSnapshot(GameState s, string provenance)
    {
        var deck = s.Deck?.Count ?? 0;
        var relics = s.Relics?.Count ?? 0;
        var hp = s.Hp.HasValue && s.MaxHp.HasValue ? $"{s.Hp}/{s.MaxHp}" : $"{s.Hp?.ToString() ?? "?"}";
        return
            $"snapshot provenance={provenance} deck={deck} relics={relics} gold={s.Gold?.ToString() ?? "?"} hp={hp} act={s.Act?.ToString() ?? "?"} floor={s.Floor?.ToString() ?? "?"} asc={s.Ascension?.ToString() ?? "?"} maxEn={s.MaxEnergy?.ToString() ?? "?"} char={s.Character ?? "?"}";
    }

    public static void DumpGameState(GameState state, string source)
    {
        if (!DumpStateEnabled) return;
        try
        {
            var payload = JsonSerializer.Serialize(state, JsonOptions);
            Log.Info($"[ContextCoach] state_dump source={source} {payload}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] state_dump failed: {ex.Message}");
        }
    }
}

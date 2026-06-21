using System.Text.Json;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2ContextCoach.State;

public static class GameStateDebug
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(GameState state) => JsonSerializer.Serialize(state, Options);

    public static void LogJson(GameState state, string tag = "[ContextCoach] GameState")
    {
        try
        {
            Log.Info($"{tag}: {ToJson(state)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] GameState JSON failed: {ex.Message}");
        }
    }
}

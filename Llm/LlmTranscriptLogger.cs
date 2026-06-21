using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;
using Sts2ContextCoach.Telemetry;

namespace Sts2ContextCoach.Llm;

/// <summary>Writes full LLM request/response JSON files under the mod folder for debugging (see ContextCoachConfig.LlmLogTranscripts).</summary>
public static class LlmTranscriptLogger
{
    /// <returns>Basename of the transcript file (e.g. <c>coach-20260411-120000-abc123.json</c>) when written; otherwise null.</returns>
    public static string? TryWrite(
        string corr,
        string model,
        string systemPrompt,
        string userPayloadJson,
        string rawApiResponse,
        string assistantContent)
    {
        if (!ContextCoachConfig.Current.LlmLogTranscripts)
            return null;

        try
        {
            var dir = Path.Combine(ContextCoachConfig.GetLogsRootPath(), "llm");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var basename = $"coach-{stamp}-{corr}.json";
            var path = Path.Combine(dir, basename);
            var doc = new
            {
                utc = DateTime.UtcNow.ToString("o"),
                corr,
                model,
                system = systemPrompt,
                user_json = userPayloadJson,
                raw_api_response = rawApiResponse,
                assistant_content = assistantContent
            };
            var text = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, text);
            Log.Info($"[ContextCoach][LLM] transcript {path}");
            RunLogger.TryMirrorLlmTranscript(path, basename);
            return basename;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] transcript write failed: {ex.Message}");
            return null;
        }
    }
}

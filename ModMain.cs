using System.IO;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using Sts2ContextCoach.Data;
using Sts2ContextCoach.Localization;
using Sts2ContextCoach.State;
using Sts2ContextCoach.Telemetry;

namespace Sts2ContextCoach;

[ModInitializer("Initialize")]
public static class ModMain
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
        Log.Info($"[ContextCoach] Initializing Context Coach (v{ver}). Logging: global snapshot each ~{GameStateCache.RefreshIntervalMs}ms when overlays refresh; set STS2_CONTEXT_COACH_VERBOSE=1 for SaveManager/reflection detail.");

        try
        {
            LocalizationManager.LoadFromAssemblyDirectory();
            ContextCoachConfig.LoadOrCreate();
            GameStateCache.Invalidate();

            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            var csv = Path.Combine(dir, "result_cleaned.csv");
            CardDatabase.Load(csv);

            if (EmbeddedShippedData.TryLoadAll(out var cj, out var rj, out var kj))
            {
                MetadataRepository.LoadFromJson(cj!, rj!);
                KeywordGlossary.LoadFromJson(kj!);
            }
            else
            {
                var dataDir = Path.Combine(dir, "data");
                MetadataRepository.Load(
                    Path.Combine(dataDir, "cards.json"),
                    Path.Combine(dataDir, "relics.json"));
                KeywordGlossary.Load(Path.Combine(dataDir, "keywords.json"));
            }

            _harmony = new Harmony("Sts2ContextCoach");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            RunTelemetryHeartbeat.TryScheduleAttach();

            var sm = (ContextCoachConfig.Current.ScoringMode ?? "heuristic").Trim();
            var keyOk = ContextCoachConfig.TryGetLlmApiKey() != null;
            Log.Info($"[ContextCoach] Harmony patches applied. logging_enabled={ContextCoachConfig.Current.LoggingEnabled}, telemetry_enabled={ContextCoachConfig.Current.TelemetryEnabled}, prompt_for_upload={ContextCoachConfig.Current.PromptForUpload}, auto_upload={ContextCoachConfig.Current.AutoUpload}, scoring_mode={sm}, llm_api_key={(keyOk ? "set" : "missing")}, llm_model={ContextCoachConfig.Current.LlmModel}");
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] Initialization failed: {ex.Message}");
        }
    }
}

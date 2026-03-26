using System.IO;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using Sts2ContextCoach.Data;
using Sts2ContextCoach.Localization;
using Sts2ContextCoach.State;

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
            GameStateCache.Invalidate();

            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
            var csv = Path.Combine(dir, "result_cleaned.csv");
            CardDatabase.Load(csv);

            var dataDir = Path.Combine(dir, "data");
            MetadataRepository.Load(
                Path.Combine(dataDir, "cards.json"),
                Path.Combine(dataDir, "relics.json"));

            _harmony = new Harmony("Sts2ContextCoach");
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            Log.Info("[ContextCoach] Harmony patches applied.");
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] Initialization failed: {ex.Message}");
        }
    }
}

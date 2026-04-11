using System.IO;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2ContextCoach.Telemetry;

public sealed class ContextCoachConfig
{
    public bool LoggingEnabled { get; set; } = true;
    public bool TelemetryEnabled { get; set; } = false;
    public bool PromptForUpload { get; set; } = true;
    public bool AutoUpload { get; set; } = false;

    /// <summary>heuristic | llm — LLM uses OpenRouter-compatible HTTP when API key is set.</summary>
    public string ScoringMode { get; set; } = "heuristic";

    public string LlmApiKeyEnv { get; set; } = "OPENROUTER_API_KEY";

    /// <summary>
    /// Optional API key stored in <c>contextcoach.config</c> (JSON text; not <c>.json</c> — the game scans those as mod manifests).
    /// When the environment variable named by <see cref="LlmApiKeyEnv"/> is set, it always wins.
    /// </summary>
    public string? LlmApiKey { get; set; }

    public string LlmBaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    /// <summary>OpenRouter model id. Default is a small/cheap model; set <c>openrouter/auto</c> to let OpenRouter route (often pricier).</summary>
    public string LlmModel { get; set; } = "openai/gpt-4o-mini";
    public int LlmTimeoutSeconds { get; set; } = 45;
    public int LlmMaxCoachHistoryLines { get; set; } = 14;
    public bool LlmEnableDeckProfileSummary { get; set; } = true;
    public int LlmDeckProfileRefreshFloorDelta { get; set; } = 3;
    public int LlmDeckProfileMaxLines { get; set; } = 8;

    /// <summary>Send OpenAI-style response_format=json_object (disable if your model returns HTTP 400).</summary>
    public bool LlmJsonObjectResponseFormat { get; set; } = true;

    /// <summary>Write prompt + raw API JSON under AppData (no API key stored).</summary>
    public bool LlmLogTranscripts { get; set; } = true;

    /// <summary>Optional OpenRouter attribution header (URL).</summary>
    public string? LlmHttpReferer { get; set; }

    public string LlmAppTitle { get; set; } = "STS2 Context Coach";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static ContextCoachConfig Current { get; private set; } = new();

    /// <summary>JSON settings next to the mod DLL (extension is not <c>.json</c> so STS2 does not treat it as a mod manifest).</summary>
    public static string ConfigPath =>
        Path.Combine(GetModRootPath(), "contextcoach.config");

    public static void LoadOrCreate()
    {
        try
        {
            var legacy = Path.Combine(GetModRootPath(), "contextcoach.config.json");
            if (!File.Exists(ConfigPath) && File.Exists(legacy))
            {
                try
                {
                    File.Copy(legacy, ConfigPath, overwrite: false);
                }
                catch
                {
                    // ignored
                }
            }

            if (!File.Exists(ConfigPath))
            {
                Current = new ContextCoachConfig();
                Save();
                return;
            }

            var raw = File.ReadAllText(ConfigPath);
            Current = JsonSerializer.Deserialize<ContextCoachConfig>(raw, JsonOptions) ?? new ContextCoachConfig();
            // Hard safety gate: no automatic upload in this mod.
            Current.AutoUpload = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] Config load failed, using defaults: {ex.Message}");
            Current = new ContextCoachConfig();
        }
    }

    public static void Save()
    {
        try
        {
            Current.AutoUpload = false;
            var raw = JsonSerializer.Serialize(Current, JsonOptions);
            File.WriteAllText(ConfigPath, raw + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] Config save failed: {ex.Message}");
        }
    }

    public static string GetModRootPath()
    {
        var asmPath = Assembly.GetExecutingAssembly().Location;
        return Path.GetDirectoryName(asmPath) ?? AppContext.BaseDirectory;
    }

    /// <summary>Writable data outside <c>mods/</c> so JSON logs are not scanned as mod manifests.</summary>
    public static string GetWritableRootPath()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SlayTheSpire2",
                "Sts2ContextCoach");
            Directory.CreateDirectory(root);
            return root;
        }
        catch
        {
            return GetModRootPath();
        }
    }

    public static string GetLogsRootPath() => Path.Combine(GetWritableRootPath(), "logs");

    public static string GetExportsRootPath() => Path.Combine(GetWritableRootPath(), "exports");

    public static bool IsLlmScoringEnabled =>
        string.Equals(Current.ScoringMode?.Trim(), "llm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Per-request HTTP cancel timeout (clamped 10–120s), used by the LLM client and overlay hint.</summary>
    public static int EffectiveLlmTimeoutSeconds =>
        Math.Clamp(Current.LlmTimeoutSeconds, 10, 120);

    public static int EffectiveLlmDeckProfileRefreshFloorDelta =>
        Math.Clamp(Current.LlmDeckProfileRefreshFloorDelta, 1, 8);

    public static int EffectiveLlmDeckProfileMaxLines =>
        Math.Clamp(Current.LlmDeckProfileMaxLines, 3, 14);

    /// <summary>Resolved API key: environment variable (if set) overrides <see cref="LlmApiKey"/> file value.</summary>
    public static string? TryGetLlmApiKey()
    {
        var name = Current.LlmApiKeyEnv?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                var env = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(env))
                    return env.Trim();
            }
            catch
            {
                // ignored
            }
        }

        var file = Current.LlmApiKey?.Trim();
        return string.IsNullOrWhiteSpace(file) ? null : file;
    }

    /// <summary>For UI hints only: <c>env</c>, <c>file</c>, or <c>none</c> (never exposes the secret).</summary>
    public static string DescribeLlmKeySource()
    {
        var name = Current.LlmApiKeyEnv?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            try
            {
                var env = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(env))
                    return "env";
            }
            catch
            {
                // ignored
            }
        }

        return string.IsNullOrWhiteSpace(Current.LlmApiKey) ? "none" : "file";
    }
}

using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2ContextCoach.Localization;

public static class LocalizationManager
{
    private const string ResEn = "Sts2ContextCoach.Localization.en.json";
    private const string ResZhCn = "Sts2ContextCoach.Localization.zh-CN.json";

    private static readonly Dictionary<string, string> Fallback = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _active = new(StringComparer.OrdinalIgnoreCase);
    public static string Language { get; private set; } = "en";

    public static void LoadFromAssemblyDirectory()
    {
        _active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var asm = Assembly.GetExecutingAssembly();

        if (!TryLoadFromEmbedded(asm, ResEn, seedFallback: true) && !TryLoadFromDiskFolder("en", seedFallback: true))
            Log.Warn("[ContextCoach] Failed to load embedded or disk localization for en.");

        var preferred = ResolvePreferredLanguage();
        if (!string.Equals(preferred, "en", StringComparison.OrdinalIgnoreCase))
        {
            var resName = string.Equals(preferred, "zh-CN", StringComparison.OrdinalIgnoreCase) ? ResZhCn : $"Sts2ContextCoach.Localization.{preferred}.json";
            if (!TryLoadFromEmbedded(asm, resName, seedFallback: false) && !TryLoadFromDiskFolder(preferred, seedFallback: false))
                Log.Warn($"[ContextCoach] Missing localization for {preferred} (embedded + disk).");
        }

        Language = preferred;
        Log.Info($"[ContextCoach] Localization active: {Language} (keys: {_active.Count}, source=embedded or disk)");
    }

    private static bool TryLoadFromEmbedded(Assembly asm, string resourceName, bool seedFallback)
    {
        try
        {
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return false;
            using var reader = new StreamReader(stream);
            return MergeJson(reader.ReadToEnd(), seedFallback);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] Embedded localization {resourceName}: {ex.Message}");
            return false;
        }
    }

    private static bool TryLoadFromDiskFolder(string lang, bool seedFallback)
    {
        var root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
        var path = Path.Combine(root, "Localization", lang + ".json");
        if (!File.Exists(path)) return false;
        try
        {
            return MergeJson(File.ReadAllText(path), seedFallback);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach] Disk localization {path}: {ex.Message}");
            return false;
        }
    }

    private static bool MergeJson(string json, bool seedFallback)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (dict == null) return false;
        foreach (var kv in dict)
        {
            if (seedFallback)
                Fallback[kv.Key] = kv.Value;
            _active[kv.Key] = kv.Value;
        }

        return true;
    }

    private static string ResolvePreferredLanguage()
    {
        var env = Environment.GetEnvironmentVariable("STS2_CONTEXT_COACH_LANG")?.Trim();
        if (IsChineseLangToken(env))
            return "zh-CN";
        if (!string.IsNullOrEmpty(env) && env.Equals("en", StringComparison.OrdinalIgnoreCase))
            return "en";

        // Prefer explicit game setting when available (STS2 uses tokens like "zhs").
        var configured = ReadGameConfiguredLanguageToken();
        if (IsChineseLangToken(configured))
            return "zh-CN";

        try
        {
            var cul = System.Globalization.CultureInfo.CurrentUICulture.Name;
            if (cul.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return "zh-CN";
        }
        catch
        {
            // ignored
        }

        return "en";
    }

    private static bool IsChineseLangToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim().ToLowerInvariant();
        return t is "zh-cn" or "zh_cn" or "zh" or "zhs" or "zh-hans" or "zh_hans" or "chs" or "chinese_simplified";
    }

    private static string? ReadGameConfiguredLanguageToken()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData)) return null;
            var root = Path.Combine(appData, "SlayTheSpire2", "steam");
            if (!Directory.Exists(root)) return null;

            foreach (var userDir in Directory.GetDirectories(root))
            {
                var p = Path.Combine(userDir, "settings.save");
                if (!File.Exists(p)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(p));
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("language", out var langEl) &&
                    langEl.ValueKind == JsonValueKind.String)
                    return langEl.GetString();
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    public static string T(string key)
    {
        if (_active.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v))
            return v;
        if (Fallback.TryGetValue(key, out v) && !string.IsNullOrEmpty(v))
            return v;
        return key;
    }
}

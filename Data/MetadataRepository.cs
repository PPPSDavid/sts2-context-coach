using System.Text.Json;
using Sts2ContextCoach.Diagnostics;

namespace Sts2ContextCoach.Data;

/// <summary>Loads optional local JSON metadata shipped with the mod. Never performs network I/O.</summary>
public static class MetadataRepository
{
    public static Dictionary<string, CardMetadataDto> Cards { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, RelicMetadataDto> Relics { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps internal names without wiki-style apostrophe encoding (e.g. <c>AscendersBane</c>) to canonical JSON keys
    /// (<c>Ascender27sBane</c>). Runtime save data often uses the collapsed form.
    /// </summary>
    private static Dictionary<string, string> CardApostropheAliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> RelicApostropheAliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    public static void Load(string? cardsPath, string? relicsPath)
    {
        Cards.Clear();
        Relics.Clear();

        CardApostropheAliases.Clear();
        RelicApostropheAliases.Clear();

        if (!string.IsNullOrEmpty(cardsPath))
            LoadCardsFromPath(cardsPath);

        if (!string.IsNullOrEmpty(relicsPath))
            LoadRelicsFromPath(relicsPath);
    }

    /// <summary>Load from embedded JSON strings (avoids shipping <c>data/*.json</c> next to the DLL — the game scans them as mod manifests).</summary>
    public static void LoadFromJson(string cardsJson, string relicsJson)
    {
        Cards.Clear();
        Relics.Clear();
        CardApostropheAliases.Clear();
        RelicApostropheAliases.Clear();
        try
        {
            IngestCardsJson(cardsJson);
        }
        catch (Exception ex)
        {
            CoachGameLog.Error($"[ContextCoach] embedded cards metadata: {ex.Message}");
        }

        try
        {
            IngestRelicsJson(relicsJson);
        }
        catch (Exception ex)
        {
            CoachGameLog.Error($"[ContextCoach] embedded relics metadata: {ex.Message}");
        }
    }

    private static void LoadCardsFromPath(string path)
    {
        if (!File.Exists(path))
        {
            CoachGameLog.Warn($"[ContextCoach] cards metadata not found: {path}");
            return;
        }

        try
        {
            IngestCardsJson(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            CoachGameLog.Error($"[ContextCoach] cards metadata load error: {ex.Message}");
        }
    }

    private static void IngestCardsJson(string json)
    {
        var dto = JsonSerializer.Deserialize<CardsFileDto>(json, JsonOptions);
        if (dto?.Cards == null) return;

        foreach (var c in dto.Cards)
        {
            if (string.IsNullOrWhiteSpace(c.InternalName)) continue;
            var key = NormalizeKey(c.InternalName);
            if (key.Length == 0) continue;
            Cards[key] = c;
        }

        BuildApostropheAliases(Cards.Keys, CardApostropheAliases);
        CoachGameLog.Info($"[ContextCoach] Loaded {Cards.Count} card metadata row(s) (schema v{dto.SchemaVersion}).");
    }

    private static void LoadRelicsFromPath(string path)
    {
        if (!File.Exists(path))
        {
            CoachGameLog.Warn($"[ContextCoach] relics metadata not found: {path}");
            return;
        }

        try
        {
            IngestRelicsJson(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            CoachGameLog.Error($"[ContextCoach] relics metadata load error: {ex.Message}");
        }
    }

    private static void IngestRelicsJson(string json)
    {
        var dto = JsonSerializer.Deserialize<RelicsFileDto>(json, JsonOptions);
        if (dto?.Relics == null) return;

        foreach (var r in dto.Relics)
        {
            if (string.IsNullOrWhiteSpace(r.InternalName)) continue;
            var key = NormalizeKey(r.InternalName);
            if (key.Length == 0) continue;
            Relics[key] = r;
        }

        BuildApostropheAliases(Relics.Keys, RelicApostropheAliases);
        CoachGameLog.Info($"[ContextCoach] Loaded {Relics.Count} relic metadata row(s) (schema v{dto.SchemaVersion}).");
    }

    public static bool TryGetCard(string internalName, out CardMetadataDto? meta)
    {
        meta = null;
        if (string.IsNullOrEmpty(internalName)) return false;

        var key = NormalizeKey(internalName);
        if (Cards.TryGetValue(key, out var row))
        {
            meta = row;
            return true;
        }

        var alt = CardIdNormalizer.FromModelIdEntry(internalName);
        if (alt.Length > 0 && !string.Equals(alt, key, StringComparison.Ordinal) &&
            Cards.TryGetValue(alt, out row))
        {
            meta = row;
            return true;
        }

        if (CardApostropheAliases.TryGetValue(key, out var canon) && Cards.TryGetValue(canon, out row))
        {
            meta = row;
            return true;
        }

        return false;
    }

    public static bool TryGetRelic(string internalName, out RelicMetadataDto? meta)
    {
        meta = null;
        if (string.IsNullOrEmpty(internalName)) return false;

        var key = NormalizeKey(internalName);
        if (Relics.TryGetValue(key, out var row))
        {
            meta = row;
            return true;
        }

        var alt = CardIdNormalizer.FromModelIdEntry(internalName);
        if (alt.Length > 0 && !string.Equals(alt, key, StringComparison.Ordinal) &&
            Relics.TryGetValue(alt, out row))
        {
            meta = row;
            return true;
        }

        if (RelicApostropheAliases.TryGetValue(key, out var canon) && Relics.TryGetValue(canon, out row))
        {
            meta = row;
            return true;
        }

        return false;
    }

    /// <summary>Wiki / data pipelines often encode <c>'</c> as <c>27</c> or <c>39</c> inside identifiers.</summary>
    private static string? CollapseWikiStyleApostropheEncoding(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var t = key.Replace("27", "", StringComparison.OrdinalIgnoreCase)
            .Replace("39", "", StringComparison.OrdinalIgnoreCase);
        return t.Length > 0 && !string.Equals(t, key, StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    private static void BuildApostropheAliases(IEnumerable<string> canonicalKeys, Dictionary<string, string> into)
    {
        foreach (var canon in canonicalKeys)
        {
            var collapsed = CollapseWikiStyleApostropheEncoding(canon);
            if (collapsed == null) continue;
            if (!into.ContainsKey(collapsed))
                into[collapsed] = canon;
        }
    }

    private static string NormalizeKey(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return string.Empty;
        return CardIdNormalizer.FromModelIdEntry(t);
    }
}

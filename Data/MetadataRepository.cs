using System.Text.Json;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2ContextCoach.Data;

/// <summary>Loads optional local JSON metadata shipped with the mod. Never performs network I/O.</summary>
public static class MetadataRepository
{
    public static Dictionary<string, CardMetadataDto> Cards { get; } = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<string, RelicMetadataDto> Relics { get; } = new(StringComparer.OrdinalIgnoreCase);

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

        if (!string.IsNullOrEmpty(cardsPath))
            LoadCards(cardsPath);

        if (!string.IsNullOrEmpty(relicsPath))
            LoadRelics(relicsPath);
    }

    private static void LoadCards(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"[ContextCoach] cards metadata not found: {path}");
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<CardsFileDto>(json, JsonOptions);
            if (dto?.Cards == null) return;

            foreach (var c in dto.Cards)
            {
                if (string.IsNullOrWhiteSpace(c.InternalName)) continue;
                var key = NormalizeKey(c.InternalName);
                if (key.Length == 0) continue;
                Cards[key] = c;
            }

            Log.Info($"[ContextCoach] Loaded {Cards.Count} card metadata row(s) (schema v{dto.SchemaVersion}).");
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] cards metadata load error: {ex.Message}");
        }
    }

    private static void LoadRelics(string path)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"[ContextCoach] relics metadata not found: {path}");
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<RelicsFileDto>(json, JsonOptions);
            if (dto?.Relics == null) return;

            foreach (var r in dto.Relics)
            {
                if (string.IsNullOrWhiteSpace(r.InternalName)) continue;
                var key = NormalizeKey(r.InternalName);
                if (key.Length == 0) continue;
                Relics[key] = r;
            }

            Log.Info($"[ContextCoach] Loaded {Relics.Count} relic metadata row(s) (schema v{dto.SchemaVersion}).");
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] relics metadata load error: {ex.Message}");
        }
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

        return false;
    }

    private static string NormalizeKey(string s)
    {
        var t = s.Trim();
        if (t.Length == 0) return string.Empty;
        return CardIdNormalizer.FromModelIdEntry(t);
    }
}

using System.Globalization;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2ContextCoach.Data;

public sealed class CardRow
{
    public string InternalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public float WinRate { get; set; }
    public float PickRate { get; set; }
    public float SkipRate { get; set; }

    /// <summary>Static priors from community CSV — used as base_score.</summary>
    public float BaseScore => WinRate * 0.6f + PickRate * 0.4f;
}

public static class CardDatabase
{
    public static Dictionary<string, CardRow> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static void Load(string filePath)
    {
        Rows.Clear();
        if (!File.Exists(filePath))
        {
            Log.Error($"[ContextCoach] CSV not found: {filePath}");
            return;
        }

        try
        {
            using var reader = new StreamReader(filePath);
            reader.ReadLine();
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length < 5) continue;

                var internalName = parts[0].Trim();
                if (string.IsNullOrEmpty(internalName) || internalName == "未知") continue;

                var row = new CardRow
                {
                    InternalName = internalName,
                    DisplayName = parts[1].Trim(),
                    WinRate = ParseFloat(parts[2]),
                    PickRate = ParseFloat(parts[3]),
                    SkipRate = ParseFloat(parts[4])
                };

                Rows.TryAdd(internalName, row);
            }

            Log.Info($"[ContextCoach] Loaded {Rows.Count} base score rows.");
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] CSV load error: {ex.Message}");
        }
    }

    public static bool TryGetBaseScore(string internalName, out float baseScore)
    {
        if (Rows.TryGetValue(internalName, out var row))
        {
            baseScore = row.BaseScore;
            return true;
        }

        var normalized = CardIdNormalizer.FromModelIdEntry(internalName);
        if (normalized.Length > 0 && !string.Equals(normalized, internalName, StringComparison.Ordinal) &&
            Rows.TryGetValue(normalized, out row))
        {
            baseScore = row.BaseScore;
            return true;
        }

        baseScore = 35f;
        return false;
    }

    private static float ParseFloat(string value)
    {
        if (float.TryParse(value.Replace("%", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var r))
            return r;
        return 0f;
    }
}

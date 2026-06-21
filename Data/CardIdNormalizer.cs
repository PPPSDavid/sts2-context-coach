namespace Sts2ContextCoach.Data;

/// <summary>Save data uses ModelId <c>Entry</c>, often snake_case; cards in UI use PascalCase type names.</summary>
public static class CardIdNormalizer
{
    public static string FromModelIdEntry(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var s = raw.Trim();
        if (!s.Contains('_')) return s;

        var parts = s.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 0) continue;
            parts[i] = char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant();
        }

        return string.Concat(parts);
    }
}

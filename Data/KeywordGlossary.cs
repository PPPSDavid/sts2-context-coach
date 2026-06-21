using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MegaCrit.Sts2.Core.Logging;

namespace Sts2ContextCoach.Data;

/// <summary>Loads optional keyword → definition rows; used to attach only relevant tooltip-style hints to LLM prompts.</summary>
public static class KeywordGlossary
{
    private static readonly List<KeywordEntry> Entries = [];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true
    };

    public static void Load(string? path)
    {
        Entries.Clear();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            if (!string.IsNullOrEmpty(path))
                Log.Warn($"[ContextCoach] keywords glossary not found: {path}");
            return;
        }

        try
        {
            IngestJson(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] keywords glossary load error: {ex.Message}");
        }
    }

    /// <summary>Embedded keywords JSON (see <see cref="MetadataRepository.LoadFromJson"/> rationale).</summary>
    public static void LoadFromJson(string json)
    {
        Entries.Clear();
        try
        {
            IngestJson(json);
        }
        catch (Exception ex)
        {
            Log.Error($"[ContextCoach] keywords glossary load error: {ex.Message}");
        }
    }

    private static void IngestJson(string json)
    {
        var dto = JsonSerializer.Deserialize<KeywordsFileDto>(json, JsonOptions);
        if (dto?.Keywords == null) return;
        foreach (var k in dto.Keywords)
        {
            if (string.IsNullOrWhiteSpace(k.Term) || string.IsNullOrWhiteSpace(k.Definition))
                continue;
            var term = k.Term.Trim();
            var def = k.Definition.Trim();
            if (term.Length == 0 || def.Length == 0) continue;
            Entries.Add(new KeywordEntry(term, def, k.Aliases));
        }

        Log.Info($"[ContextCoach] Loaded {Entries.Count} keyword glossary row(s) (schema v{dto.SchemaVersion}).");
    }

    /// <summary>Core card keywords not always present as rows in <c>keywords.json</c>; attach when deck/candidate text mentions them.</summary>
    private static readonly (string Term, string Definition)[] BuiltinMechanicHints =
    [
        ("Ethereal",
            "Card keyword: if still in hand at end of turn, the card is discarded (unless another effect retains it)."),
    ];

    /// <summary>Definitions for glossary terms that appear in the given text blobs (word-boundary match, case-insensitive).</summary>
    public static List<KeywordHintRow> CollectHints(IEnumerable<string?> textBlobs, int maxHints = 22)
    {
        if (maxHints <= 0)
            return [];

        var haystack = string.Join('\n', textBlobs.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
        if (haystack.Length == 0)
            return [];

        var picked = new List<KeywordHintRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Entries.Count > 0)
        {
            foreach (var e in Entries)
            {
                if (picked.Count >= maxHints) break;
                if (seen.Contains(e.Term)) continue;

                var hit = Matches(haystack, e.Term);
                if (!hit && e.Aliases != null)
                {
                    foreach (var alias in e.Aliases)
                    {
                        if (string.IsNullOrWhiteSpace(alias)) continue;
                        if (Matches(haystack, alias.Trim()))
                        {
                            hit = true;
                            break;
                        }
                    }
                }

                if (!hit) continue;
                picked.Add(new KeywordHintRow(e.Term, e.Definition));
                seen.Add(e.Term);
            }
        }

        foreach (var (term, def) in BuiltinMechanicHints)
        {
            if (picked.Count >= maxHints) break;
            if (seen.Contains(term)) continue;
            if (!Matches(haystack, term)) continue;
            picked.Add(new KeywordHintRow(term, def));
            seen.Add(term);
        }

        return picked
            .OrderBy(x => x.Term, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool Matches(string haystack, string term)
    {
        try
        {
            var inner = Regex.Escape(term).Replace(" ", @"\s+");
            if (inner.Length == 0) return false;
            return Regex.IsMatch(
                haystack,
                @"\b" + inner + @"\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch
        {
            return false;
        }
    }

    private sealed record KeywordEntry(string Term, string Definition, List<string>? Aliases);

    private sealed class KeywordsFileDto
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("keywords")]
        public List<KeywordFileRow> Keywords { get; set; } = [];
    }

    private sealed class KeywordFileRow
    {
        [JsonPropertyName("term")]
        public string Term { get; set; } = "";

        [JsonPropertyName("definition")]
        public string Definition { get; set; } = "";

        [JsonPropertyName("aliases")]
        public List<string>? Aliases { get; set; }
    }
}

public sealed record KeywordHintRow(string Term, string Definition);

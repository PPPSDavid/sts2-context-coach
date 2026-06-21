using System.Text;
using MegaCrit.Sts2.Core.Logging;
using Sts2ContextCoach.State;
using Sts2ContextCoach.Telemetry;

namespace Sts2ContextCoach.Llm;

/// <summary>Short rolling text lines for the next LLM prompt + inferred picks between batches.</summary>
public static class CoachPickHistory
{
    private static readonly object Gate = new();
    private static readonly List<string> Lines = new();

    public static void AppendVerdict(string decisionType, string batchSummary, IReadOnlyList<(string name, string note, int? score)> ranked)
    {
        try
        {
            var top = ranked.Count > 0 ? ranked[0].name : "?";
            var sb = new StringBuilder(256);
            sb.Append('[').Append(decisionType).Append("] LLM top=").Append(top);
            if (ranked.Count > 1)
            {
                sb.Append(" order=");
                for (var i = 0; i < Math.Min(ranked.Count, 5); i++)
                {
                    if (i > 0) sb.Append('>');
                    sb.Append(ranked[i].name);
                }
            }

            sb.Append(" | ").Append(batchSummary);
            EnqueueLine(sb.ToString());
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] history verdict append failed: {ex.Message}");
        }
    }

    public static void AppendInferredPick(string? picked, string? previousBatchSummary)
    {
        if (string.IsNullOrWhiteSpace(picked))
            return;
        try
        {
            EnqueueLine($"[pick] {picked} (after {previousBatchSummary ?? "?"})");
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] history pick append failed: {ex.Message}");
        }
    }

    public static IReadOnlyList<string> SnapshotLinesForPrompt()
    {
        lock (Gate)
        {
            var max = Math.Clamp(ContextCoachConfig.Current.LlmMaxCoachHistoryLines, 4, 40);
            if (Lines.Count <= max)
                return Lines.ToArray();
            return Lines.Skip(Lines.Count - max).ToArray();
        }
    }

    /// <summary>Drop rolling LLM lines when a new run is detected (same game session, different save).</summary>
    public static void Clear()
    {
        lock (Gate) Lines.Clear();
    }

    /// <summary>
    /// If the deck gained exactly one copy of a card from <paramref name="candidateNames"/>, record a pick line.
    /// </summary>
    public static void TryInferPick(
        IReadOnlyList<CardInstance>? deckNow,
        IReadOnlyDictionary<string, int>? deckBefore,
        IReadOnlyCollection<string> candidateNames,
        string? previousBatchSummary)
    {
        if (deckBefore == null || candidateNames.Count == 0)
            return;

        try
        {
            var now = CountDeck(deckNow);
            if (!TrySingleCandidateIncrement(deckBefore, now, candidateNames, out var picked))
                return;

            Log.Info($"[ContextCoach][LLM] inferred pick: {picked} (batch was: {previousBatchSummary ?? "?"})");
            AppendInferredPick(picked, previousBatchSummary);
        }
        catch (Exception ex)
        {
            Log.Warn($"[ContextCoach][LLM] pick inference failed: {ex.Message}");
        }
    }

    private static void EnqueueLine(string line)
    {
        var max = Math.Clamp(ContextCoachConfig.Current.LlmMaxCoachHistoryLines, 4, 40);
        lock (Gate)
        {
            Lines.Add(line.Trim());
            while (Lines.Count > max)
                Lines.RemoveAt(0);
        }
    }

    private static Dictionary<string, int> CountDeck(IReadOnlyList<CardInstance>? deck)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (deck == null) return d;
        foreach (var c in deck)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) continue;
            d[c.Name] = d.TryGetValue(c.Name, out var n) ? n + 1 : 1;
        }

        return d;
    }

    private static bool TrySingleCandidateIncrement(
        IReadOnlyDictionary<string, int> before,
        IReadOnlyDictionary<string, int> after,
        IReadOnlyCollection<string> candidateNames,
        out string? picked)
    {
        picked = null;
        var beforeTotal = before.Values.Sum();
        var afterTotal = after.Values.Sum();
        if (afterTotal != beforeTotal + 1)
            return false;

        string? match = null;
        foreach (var name in candidateNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var b = before.TryGetValue(name, out var x) ? x : 0;
            var a = after.TryGetValue(name, out var y) ? y : 0;
            if (a != b + 1)
                continue;
            if (match != null)
                return false;
            match = name;
        }

        picked = match;
        return match != null;
    }
}

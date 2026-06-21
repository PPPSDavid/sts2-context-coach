using Sts2ContextCoach.Llm;
using Sts2ContextCoach.Scoring;
using Xunit;

namespace Sts2ContextCoach.Tests.Llm;

public sealed class CoachRankingsParseTests
{
    private static LlmCoachCandidate C(string name, bool up = false) =>
        LlmCoachCandidate.FromRuntime(name, up, 1, null, 0f, null,
            new ScoreResult { BaseScore = 1f, ContextScore = 2f });

    [Fact]
    public void ParseRankings_StripsProsePrefixBeforeJson()
    {
        var candidates = new[] { C("Alpha"), C("Beta") };
        const string content = """
Here is the JSON you asked for:
{"rankings":[{"internal_name":"Beta","upgraded":false,"coach_note":"pick B","coach_score":80},{"internal_name":"Alpha","upgraded":false,"coach_note":"pick A","coach_score":70}]}
""";
        var map = LlmBatchCoordinator.ParseRankings(content, candidates);
        Assert.Equal(2, map.Count);
        Assert.True(map.TryGetValue("Beta|u0", out var b) && b.CoachScore == 80);
        Assert.True(map.TryGetValue("Alpha|u0", out var a) && a.CoachScore == 70);
    }

    [Fact]
    public void ParseRankings_StripsMarkdownFence()
    {
        var candidates = new[] { C("StrikeDummy") };
        var content = """
```json
{"rankings":[{"internal_name":"StrikeDummy","upgraded":false,"coach_note":"ok","coach_score":55}]}
```
""";
        var map = LlmBatchCoordinator.ParseRankings(content, candidates);
        Assert.Single(map);
        Assert.Equal(55, map["StrikeDummy|u0"].CoachScore);
    }

    [Fact]
    public void ParseRankings_ThrowsWhenNoRankingsMatch()
    {
        var candidates = new[] { C("OnlyThis") };
        const string content = """{"rankings":[{"internal_name":"Other","upgraded":false,"coach_note":"x","coach_score":99}]}""";
        Assert.Throws<InvalidOperationException>(() => LlmBatchCoordinator.ParseRankings(content, candidates));
    }
}

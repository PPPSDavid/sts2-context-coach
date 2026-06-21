namespace Sts2ContextCoach.Scoring;

public sealed class ScoreResult
{
    public float BaseScore { get; init; }
    public float ContextScore { get; init; }
    public IReadOnlyList<string> ReasonKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<float> ReasonWeights { get; init; } = Array.Empty<float>();
    public IReadOnlyList<ScoreBreakdownItem> Breakdown { get; init; } = Array.Empty<ScoreBreakdownItem>();
}

public sealed class ScoreBreakdownItem
{
    public required string Key { get; init; }
    public float Weight { get; init; }
}

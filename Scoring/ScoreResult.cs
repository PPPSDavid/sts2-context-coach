namespace Sts2ContextCoach.Scoring;

public sealed class ScoreResult
{
    public float BaseScore { get; init; }
    public float ContextScore { get; init; }
    public IReadOnlyList<string> ReasonKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<float> ReasonWeights { get; init; } = Array.Empty<float>();
}

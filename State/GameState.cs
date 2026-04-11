using System.Text.Json.Serialization;
using Sts2ContextCoach.Scoring;

namespace Sts2ContextCoach.State;

public sealed class GameState
{
    public string? Character { get; set; }
    public int? Hp { get; set; }
    public int? MaxHp { get; set; }
    public int? Gold { get; set; }
    public List<CardInstance>? Deck { get; set; }
    public List<string>? Relics { get; set; }
    public int? Act { get; set; }
    public int? Floor { get; set; }
    public int? Ascension { get; set; }
    public int? MaxEnergy { get; set; }
    public string? CurrentScreen { get; set; }
    public List<CardInstance>? RewardCards { get; set; }

    /// <summary>Optional stable id from <c>SerializableRun</c> when the game exposes it; used to reset LLM <c>coach_history</c> between runs.</summary>
    public string? RunIdentity { get; set; }

    /// <summary>Number of entries in save <c>Players</c> (1 solo, 2+ co-op); history resets when this changes mid-session.</summary>
    public int? SavePlayerCount { get; set; }

    /// <summary>Filled on the shared global snapshot once per <see cref="GameStateCache"/> refresh; copied in <see cref="GameStateExtractor.MergePerCard"/>.</summary>
    [JsonIgnore]
    public DeckAnalysis? CachedDeckAnalysis { get; set; }
}

using System.Text.Json.Serialization;

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
}

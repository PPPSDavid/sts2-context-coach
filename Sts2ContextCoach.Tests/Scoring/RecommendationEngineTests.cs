using Sts2ContextCoach.Scoring;
using Sts2ContextCoach.State;
using Xunit;

namespace Sts2ContextCoach.Tests.Scoring;

public sealed class RecommendationEngineTests
{
    [Fact]
    public void ScoreCard_UsesFallbackBaseScore_WhenCardMissingFromDatabase()
    {
        var state = new GameState
        {
            Character = "Ironclad",
            Deck = []
        };

        var score = RecommendationEngine.ScoreCard("UnknownCardForTest", upgraded: false, cardCost: 1, state);

        Assert.Equal(35f, score.BaseScore);
        Assert.Contains(score.ReasonKeys, k => k == "reason.base_fallback");
    }

    [Fact]
    public void ScoreCard_AddsExpensiveLowEnergyPenalty()
    {
        var state = new GameState
        {
            Character = "Ironclad",
            MaxEnergy = 3,
            Deck = []
        };

        var score = RecommendationEngine.ScoreCard("UnknownCardForPenalty", upgraded: false, cardCost: 2, state);

        Assert.Contains(score.Breakdown, b => b.Key == "reason.expensive_low_energy" && b.Weight < 0f);
    }

    [Fact]
    public void ScoreCard_AddsDeckPressurePenalty_ForLargeDeck()
    {
        var state = new GameState
        {
            Character = "Ironclad",
            Deck = Enumerable.Range(0, 11)
                .Select(i => new CardInstance { Name = $"TestCard{i}" })
                .ToList()
        };

        var score = RecommendationEngine.ScoreCard("UnknownCardForDeckPressure", upgraded: false, cardCost: 1, state);

        Assert.Contains(score.Breakdown, b => b.Key == "reason.deck_pressure" && b.Weight < 0f);
    }
}

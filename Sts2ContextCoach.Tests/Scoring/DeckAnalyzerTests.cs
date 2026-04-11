using Sts2ContextCoach.Scoring;
using Sts2ContextCoach.State;
using Xunit;

namespace Sts2ContextCoach.Tests.Scoring;

public sealed class DeckAnalyzerTests
{
    [Fact]
    public void Analyze_EmptyDeck_UsesDefaultNeeds()
    {
        var state = new GameState
        {
            Deck = []
        };

        var analysis = DeckAnalyzer.Analyze(state);

        Assert.Equal(0.5f, analysis.BlockNeed);
        Assert.Equal(0.4f, analysis.FrontloadNeed);
        Assert.Equal(0.4f, analysis.DrawNeed);
        Assert.Equal(0.4f, analysis.ScalingNeed);
        Assert.Equal(0f, analysis.HighCostPressure);
        Assert.Equal(0f, analysis.AttackSpamPressure);
    }

    [Fact]
    public void Analyze_HighCostPressure_StartsAtThreshold()
    {
        var state = new GameState
        {
            Deck = Enumerable.Range(0, 5)
                .Select(i => new CardInstance { Name = $"DemonForm{i}" })
                .ToList()
        };

        var analysis = DeckAnalyzer.Analyze(state);

        Assert.Equal(5, analysis.HighCostCardCount);
        Assert.Equal(0.25f, analysis.HighCostPressure);
    }

    [Fact]
    public void Analyze_HighCostPressure_IsZeroBelowThreshold()
    {
        var state = new GameState
        {
            Deck = Enumerable.Range(0, 4)
                .Select(i => new CardInstance { Name = $"DemonForm{i}" })
                .ToList()
        };

        var analysis = DeckAnalyzer.Analyze(state);

        Assert.Equal(4, analysis.HighCostCardCount);
        Assert.Equal(0f, analysis.HighCostPressure);
    }

    [Fact]
    public void Analyze_AttackSpamPressure_StartsAtThreshold()
    {
        var state = new GameState
        {
            Deck = Enumerable.Range(0, 9)
                .Select(i => new CardInstance { Name = $"Strike{i}" })
                .ToList()
        };

        var analysis = DeckAnalyzer.Analyze(state);

        Assert.Equal(9, analysis.RedundantAttackCount);
        Assert.Equal(0.25f, analysis.AttackSpamPressure);
    }
}

using Sts2ContextCoach.Telemetry;
using Xunit;

namespace Sts2ContextCoach.Tests.Telemetry;

public class RunOutcomeClassifierTests
{
    [Fact]
    public void HpZero_IsDefeat_EvenOnNeutralScreen()
    {
        Assert.Equal("defeat", RunOutcomeClassifier.Classify(0, "root/Game/Map"));
    }

    [Theory]
    [InlineData("root/UI/NeoVictoryPanel", "victory")]
    [InlineData("root/run_complete_summary", "victory")]
    [InlineData("root/RunComplete", "victory")]
    [InlineData("root/CreditsRoll", "victory")]
    [InlineData("root/EndOfRunStats", "victory")]
    [InlineData("root/GameOverScreen", "defeat")]
    [InlineData("root/YouDied", "defeat")]
    [InlineData("root/RunLost", "defeat")]
    public void ScreenFragments_Classify(string path, string expected)
    {
        Assert.Equal(expected, RunOutcomeClassifier.Classify(10, path));
    }

    [Fact]
    public void WindowPath_DoesNotFalseVictory()
    {
        Assert.Null(RunOutcomeClassifier.Classify(10, "root/Game/WindowContainer/WinSizeDrag"));
    }

    [Fact]
    public void EmptyScreen_NullUnlessHpZero()
    {
        Assert.Null(RunOutcomeClassifier.Classify(5, ""));
        Assert.Null(RunOutcomeClassifier.Classify(5, null));
    }
}

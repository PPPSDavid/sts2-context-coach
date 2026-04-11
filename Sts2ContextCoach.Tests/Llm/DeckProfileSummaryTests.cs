using Sts2ContextCoach.Llm;
using Sts2ContextCoach.State;
using Xunit;

namespace Sts2ContextCoach.Tests.Llm;

public sealed class DeckProfileSummaryTests
{
    [Fact]
    public void ComputeDeckProfileSignature_ChangesWhenDeckChanges()
    {
        var a = new GameState
        {
            Character = "IRONCLAD",
            Deck = [new CardInstance { Name = "BASH", Upgraded = false }]
        };
        var b = new GameState
        {
            Character = "IRONCLAD",
            Deck =
            [
                new CardInstance { Name = "BASH", Upgraded = false },
                new CardInstance { Name = "PommelStrike", Upgraded = false }
            ]
        };

        var sa = LlmBatchCoordinator.ComputeDeckProfileSignature(a);
        var sb = LlmBatchCoordinator.ComputeDeckProfileSignature(b);

        Assert.NotEqual(sa, sb);
    }

    [Fact]
    public void ComputeDeckProfileRefreshDecision_ColdStartSchedules()
    {
        var state = new GameState
        {
            Floor = 5,
            Deck = [new CardInstance { Name = "BASH", Upgraded = false }]
        };

        var decision = LlmBatchCoordinator.ComputeDeckProfileRefreshDecision(state, currentProfile: null, pendingSignature: null);

        Assert.True(decision.ShouldSchedule);
        Assert.Equal("cold_start", decision.Reason);
    }

    [Fact]
    public void ComputeDeckProfileRefreshDecision_PendingSameSignatureDoesNotSchedule()
    {
        var state = new GameState
        {
            Deck = [new CardInstance { Name = "BASH", Upgraded = false }]
        };
        var sig = LlmBatchCoordinator.ComputeDeckProfileSignature(state);

        var decision = LlmBatchCoordinator.ComputeDeckProfileRefreshDecision(state, currentProfile: null, pendingSignature: sig);

        Assert.False(decision.ShouldSchedule);
        Assert.Equal("pending_same_signature", decision.Reason);
    }

    [Fact]
    public void ComputeDeckProfileRefreshDecision_SignatureChangeSchedules()
    {
        var state = new GameState
        {
            Floor = 6,
            Deck =
            [
                new CardInstance { Name = "BASH", Upgraded = false },
                new CardInstance { Name = "PommelStrike", Upgraded = false }
            ]
        };

        var oldProfile = new LlmDeckProfile
        {
            Signature = "OLD_SIG",
            GeneratedUtc = DateTimeOffset.UtcNow,
            GeneratedFloor = 5,
            CorePlan = "old"
        };

        var decision = LlmBatchCoordinator.ComputeDeckProfileRefreshDecision(state, oldProfile, pendingSignature: null);

        Assert.True(decision.ShouldSchedule);
        Assert.Equal("signature_changed", decision.Reason);
    }

    [Fact]
    public void ComputeDeckProfileRefreshDecision_FloorAdvanceSchedulesWithSameSignature()
    {
        var state = new GameState
        {
            Floor = 10,
            Deck = [new CardInstance { Name = "BASH", Upgraded = false }]
        };
        var sig = LlmBatchCoordinator.ComputeDeckProfileSignature(state);
        var oldProfile = new LlmDeckProfile
        {
            Signature = sig,
            GeneratedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            GeneratedFloor = 5,
            CorePlan = "same deck"
        };

        var decision = LlmBatchCoordinator.ComputeDeckProfileRefreshDecision(state, oldProfile, pendingSignature: null);

        Assert.True(decision.ShouldSchedule);
        Assert.Equal("floor_advanced", decision.Reason);
    }
}

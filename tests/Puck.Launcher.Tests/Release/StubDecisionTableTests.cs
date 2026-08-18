using Puck.Launcher.Stub;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over the stub's entire selection policy — a pure function, exercised here without any process or
/// file system.</summary>
public sealed class StubDecisionTableTests {
    [Fact]
    public void Decide_LaunchesCurrent_WhileAttemptsAreBelowTheCeiling() {
        var decision = StubDecisionTable.Decide(attempts: 2, currentGeneration: 9, hasLastGood: true, lastGoodGeneration: 1, maxAttempts: 3);

        Assert.Equal(actual: decision, expected: StubAction.LaunchCurrent);
    }
    [Fact]
    public void Decide_Reverts_AtTheExactBoundary_WhenGenerationAllowsIt() {
        var decision = StubDecisionTable.Decide(attempts: 3, currentGeneration: 1, hasLastGood: true, lastGoodGeneration: 1, maxAttempts: 3);

        Assert.Equal(actual: decision, expected: StubAction.RevertToLastGood);
    }
    [Fact]
    public void Decide_LaunchesAnyway_WhenCeilingReached_ButCurrentGenerationExceedsLastGood() {
        var decision = StubDecisionTable.Decide(attempts: 3, currentGeneration: 2, hasLastGood: true, lastGoodGeneration: 1, maxAttempts: 3);

        Assert.Equal(actual: decision, expected: StubAction.LaunchCurrentAnyway);
    }
    [Fact]
    public void Decide_LaunchesAnyway_WhenCeilingReached_AndThereIsNoLastGoodAtAll() {
        var decision = StubDecisionTable.Decide(attempts: 3, currentGeneration: 1, hasLastGood: false, lastGoodGeneration: 0, maxAttempts: 3);

        Assert.Equal(actual: decision, expected: StubAction.LaunchCurrentAnyway);
    }
    [Fact]
    public void Decide_LaunchesCurrent_OneAttemptBelowTheCeiling() {
        var decision = StubDecisionTable.Decide(attempts: 2, currentGeneration: 1, hasLastGood: true, lastGoodGeneration: 1, maxAttempts: 3);

        Assert.Equal(actual: decision, expected: StubAction.LaunchCurrent);
    }
    [Fact]
    public void AttemptsFor_IsZero_ForARecordNamingADifferentVersion() {
        var record = new StubHealthRecord(Attempts: 5, Version: "1.0.0");

        Assert.Equal(expected: 0, actual: StubHealth.AttemptsFor(record: record, version: "1.0.1"));
        Assert.Equal(expected: 5, actual: StubHealth.AttemptsFor(record: record, version: "1.0.0"));
    }
}

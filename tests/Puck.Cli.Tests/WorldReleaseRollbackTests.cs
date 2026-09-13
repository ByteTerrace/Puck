using System.Text.Json;
using Puck.Cli.Azure;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseRollbackTests {
    private static WorldReleaseGroupRecord Committed => new() {
        Schema = WorldReleaseGroupStore.Schema, DeploymentGroup = "primary", Owner = Guid.NewGuid(),
        ActiveRelease = "release-b", PreviousRelease = "release-a", PendingOperationId = Guid.NewGuid(),
        PendingSourceRelease = "release-a", PendingTargetRelease = "release-b",
        PendingPhase = WorldReleaseOperationPhase.Commit, PendingCommitted = true,
        Admission = WorldReleaseAdmissionState.Open, RollbackEligible = true,
    };

    [Fact]
    public void AdmittedCommitCanRollbackWithoutFinalizingItsWindow() {
        // A completed deployment deliberately retains the pending slot and recovery roots.
        var record = WorldReleaseGroupStore.DeserializeValidated(JsonSerializer.SerializeToUtf8Bytes(Committed));
        Assert.NotNull(record.PendingOperationId);
        Assert.Equal(("release-b", "release-a"), AzureCommand.GetWorldReleaseRollbackPair(record));
        Assert.False(record.HasUnfinishedOperation);
    }

    [Theory]
    [InlineData(WorldReleaseOperationPhase.Prepare)]
    [InlineData(WorldReleaseOperationPhase.Drain)]
    [InlineData(WorldReleaseOperationPhase.Activate)]
    [InlineData(WorldReleaseOperationPhase.Verify)]
    [InlineData(WorldReleaseOperationPhase.Recover)]
    [InlineData(WorldReleaseOperationPhase.RecoverActivate)]
    [InlineData(WorldReleaseOperationPhase.Commit)]
    public void UnfinishedOperationRequiresResumeIncludingCommitBeforeAdmission(WorldReleaseOperationPhase phase) {
        var record = Committed with {
            PendingPhase = phase, PendingCommitted = phase == WorldReleaseOperationPhase.Commit,
            Admission = phase == WorldReleaseOperationPhase.Prepare ? WorldReleaseAdmissionState.Open : WorldReleaseAdmissionState.Closed,
        };
        var error = Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(record));
        Assert.Contains(record.PendingOperationId!.Value.ToString("D"), error.Message);
        Assert.Contains("puck world release resume", error.Message);
    }

    [Fact]
    public void ReusingAnOperationCannotToggleBackOrSelectItsOldRecoveryRoots() {
        var record = Committed;
        var operation = record.PendingOperationId!.Value;
        Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(record, operation));
        record = record with {
            PendingOperationId = null, PendingSourceRelease = null, PendingTargetRelease = null, PendingCommitted = false, PendingPhase = null,
            History = [new() { OperationId = operation, SourceRelease = "release-a", TargetRelease = "release-b", Result = "committed" }],
        };
        Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(record, operation));
        Assert.Equal(("release-b", "release-a"), AzureCommand.GetWorldReleaseRollbackPair(record, Guid.NewGuid()));
    }

    [Fact]
    public void FailedRollbackRecoveryRemainsEligibleButFinalizationCannotBeUndone() {
        var record = Committed with {
            PendingOperationId = null, PendingPhase = null, PendingCommitted = false,
            PendingSourceRelease = null, PendingTargetRelease = null,
        };
        Assert.Equal(("release-b", "release-a"), AzureCommand.GetWorldReleaseRollbackPair(record));
        var error = Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(record with { RollbackEligible = false }));
        Assert.Contains("finalized window", error.Message);
        error = Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(record with { Admission = WorldReleaseAdmissionState.Closed }));
        Assert.Contains("resume", error.Message);
    }
}

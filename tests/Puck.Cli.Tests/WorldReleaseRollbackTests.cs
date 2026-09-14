using System.Diagnostics;
using System.Text.Json;
using Puck.Cli.Azure;
using Puck.World.Server;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class WorldReleaseRollbackTests {
    [Fact]
    public void AdmittedCommitCanRollbackWithoutFinalizingItsWindow() {
        // A completed deployment deliberately retains the pending slot and recovery roots.
        var record = WorldReleaseGroupStore.DeserializeValidated(bytes: JsonSerializer.SerializeToUtf8Bytes(Committed));

        Assert.NotNull(value: record.PendingOperationId);
        Assert.Equal(
            ("release-b", "release-a"),
            AzureCommand.GetWorldReleaseRollbackPair(record)
        );
        Assert.False(condition: record.HasUnfinishedOperation);
    }
    [Fact]
    public void FailedRollbackRecoveryRemainsEligibleButFinalizationCannotBeUndone() {
        var record = Committed with {
            PendingOperationId = null,
            PendingPhase = null,
            PendingCommitted = false,
            PendingSourceRelease = null,
            PendingTargetRelease = null,
        };

        Assert.Equal(
            ("release-b", "release-a"),
            AzureCommand.GetWorldReleaseRollbackPair(record)
        );
        var error = Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(record with { RollbackEligible = false }));

        Assert.Contains(
            "finalized window",
            error.Message
        );
        error = Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(record with { Admission = WorldReleaseAdmissionState.Closed }));
        Assert.Contains(
            "resume",
            error.Message
        );
    }
    [InlineData("status", "missing")]
    [InlineData("exercise", "Host configuration directory does not exist")]
    [Theory]
    public async Task OperatorRefusalsReportTheReasonWithoutAnUnhandledException(string verb, string reason) {
        var temporary = Directory.CreateTempSubdirectory(prefix: "puck-release-refusal-");

        try {
            var start = new ProcessStartInfo(fileName: "dotnet") {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = temporary.FullName,
            };

            foreach (var argument in new[] { typeof(PuckRootCommand).Assembly.Location, "world", "release", verb, Path.Combine(
                path1: temporary.FullName,
                path2: "missing"
            ) }) {
                start.ArgumentList.Add(item: argument);
            }
            using var process = Process.Start(startInfo: start)!;
            var token = TestContext.Current.CancellationToken;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken: token);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken: token);

            await process.WaitForExitAsync(cancellationToken: token);
            Assert.Equal(
                1,
                process.ExitCode
            );
            Assert.Empty(value: await output);
            var diagnostic = await errors;

            Assert.StartsWith(
                actualString: diagnostic,
                expectedStartString: "world release: "
            );
            Assert.Contains(
                actualString: diagnostic,
                expectedSubstring: reason
            );
            Assert.DoesNotContain(
                actualString: diagnostic,
                expectedSubstring: "Unhandled exception"
            );
            Assert.DoesNotContain(
                actualString: diagnostic,
                expectedSubstring: "   at "
            );
        } finally { temporary.Delete(recursive: true); }
    }
    [Fact]
    public void ReusingAnOperationCannotToggleBackOrSelectItsOldRecoveryRoots() {
        var record = Committed;
        var operation = record.PendingOperationId!.Value;

        Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(
            record,
            operation
        ));
        record = record with {
            PendingOperationId = null,
            PendingSourceRelease = null,
            PendingTargetRelease = null,
            PendingCommitted = false,
            PendingPhase = null,
            History = [new() { OperationId = operation, Result = "committed", SourceRelease = "release-a", TargetRelease = "release-b" }],
        };
        Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(
            record,
            operation
        ));
        Assert.Equal(
            ("release-b", "release-a"),
            AzureCommand.GetWorldReleaseRollbackPair(
                record,
                Guid.NewGuid()
            )
        );
    }
    [InlineData(WorldReleaseOperationPhase.Prepare)]
    [InlineData(WorldReleaseOperationPhase.Drain)]
    [InlineData(WorldReleaseOperationPhase.Activate)]
    [InlineData(WorldReleaseOperationPhase.Verify)]
    [InlineData(WorldReleaseOperationPhase.Recover)]
    [InlineData(WorldReleaseOperationPhase.RecoverActivate)]
    [InlineData(WorldReleaseOperationPhase.Commit)]
    [Theory]
    public void UnfinishedOperationRequiresResumeIncludingCommitBeforeAdmission(WorldReleaseOperationPhase phase) {
        var record = Committed with {
            PendingPhase = phase,
            PendingCommitted = (phase == WorldReleaseOperationPhase.Commit),
            Admission = ((phase == WorldReleaseOperationPhase.Prepare)
            ? WorldReleaseAdmissionState.Open
            : WorldReleaseAdmissionState.Closed),
        };
        var error = Assert.Throws<InvalidOperationException>(() => AzureCommand.GetWorldReleaseRollbackPair(record));

        Assert.Contains(
            record.PendingOperationId!.Value.ToString(format: "D"),
            error.Message
        );
        Assert.Contains(
            "puck world release resume",
            error.Message
        );
    }

    private static WorldReleaseGroupRecord Committed => new() {
        Schema = WorldReleaseGroupStore.Schema,
        DeploymentGroup = "primary",
        Owner = Guid.NewGuid(),
        ActiveRelease = "release-b",
        PreviousRelease = "release-a",
        PendingOperationId = Guid.NewGuid(),
        PendingSourceRelease = "release-a",
        PendingTargetRelease = "release-b",
        PendingPhase = WorldReleaseOperationPhase.Commit,
        PendingCommitted = true,
        Admission = WorldReleaseAdmissionState.Open,
        RollbackEligible = true,
    };
}

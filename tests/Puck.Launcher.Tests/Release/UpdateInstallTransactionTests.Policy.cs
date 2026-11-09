using Puck.Confinement;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>The release record's failure schedules (<c>docs/plans/updater-policy.md</c>), with real signatures, replay
/// marks, staging and stub selection over one install root.</summary>
public sealed partial class UpdateInstallTransactionTests {
    private static readonly byte[] Rebuilt = [7, 7, 7];

    // Schedule 1: a signed release fails startup; the stub rejects its payload and rolls back; the same payload under a
    // wider rollout, a higher sequence and a new version label is refused; a rebuilt payload installs.
    [Fact]
    public async Task RejectedPayloadStaysRejectedAcrossRolloutProgressionWhileAChangedPayloadInstalls() {
        _ = InstallSeed.Install(installRoot: Install, version: "1.0.0");
        Publish(manifest: Manifest(version: "1.1.0"));
        Assert.True(condition: (await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        for (var attempt = 0; (attempt < 3); attempt++) {
            Assert.Equal(expected: "1.1.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 3).Version);
        }
        var rollback = InstallSeed.Launch(installRoot: Install, maxAttempts: 3);

        Assert.Equal(expected: "1.0.0", actual: rollback.Version);
        Assert.Equal(expected: "1.1.0", actual: rollback.RolledBackFrom);
        Assert.Equal(expected: "1.1.0", actual: Assert.Single(collection: InstallSeed.Policy(installRoot: Install).RejectedPayloads).Version);

        foreach (var (version, sequence, rollout) in new[] { ("1.1.0", 2UL, 50), ("1.1.1", 3UL, 100) }) {
            Publish(manifest: Manifest(notes: $"re-signed {version}", rollout: rollout, sequence: sequence, version: version));
            var check = await Service().CheckAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
            var apply = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(expected: UpdateCheckOutcome.Refused, actual: check.Outcome);
            Assert.Contains(expectedSubstring: "rejected after failing startup", actualString: check.Detail);
            Assert.False(condition: apply.Applied);
            Assert.Equal(expected: "1.0.0", actual: Current);
        }
        Publish(manifest: Manifest(payload: Rebuilt, sequence: 4, version: "1.1.2"), content: Rebuilt);
        var changed = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: changed.Applied, userMessage: changed.Detail);
        Assert.Equal(expected: "1.1.2", actual: Current);
    }
    // Schedule 2A: a release revokes the last proven install and then fails startup; rollback skips the revoked install
    // for an earlier proven one. 2B: when every proven install is revoked or below the floor, launch is refused by name.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task RollbackNeverSelectsARevokedOrIncompatibleProvenInstall(bool noCompatibleFallback) {
        _ = InstallSeed.Install(installRoot: Install, stateGeneration: 1, version: "1.0.0");
        _ = InstallSeed.Install(installRoot: Install, stateGeneration: (noCompatibleFallback ? 2 : 1), version: "1.1.0");
        Publish(manifest: Manifest(revoked: ["1.1.0"], stateGeneration: (noCompatibleFallback ? 2 : 1), version: "1.2.0"));
        var applied = await Service(installedVersion: "1.1.0").ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: applied.Applied, userMessage: applied.Detail);
        Assert.Contains(expected: "1.1.0", collection: InstallSeed.Policy(installRoot: Install).RevokedVersions);
        Assert.Equal(expected: (noCompatibleFallback ? null : "1.0.0"), actual: InstallSeed.RollbackTarget(installRoot: Install));
        Assert.Equal(expected: "1.2.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        var decision = InstallSeed.Launch(installRoot: Install, maxAttempts: 1);

        if (!noCompatibleFallback) {
            Assert.Equal(expected: "1.0.0", actual: decision.Version);
            return;
        }
        Assert.Null(@object: decision.Version);
        Assert.Contains(expectedSubstring: "'1.1.0' is revoked", actualString: decision.Refusal);
        Assert.Contains(expectedSubstring: "version '1.0.0' declares state generation 1, below this install's floor 2", actualString: decision.Refusal);
        Assert.Equal(expected: "1.2.0", actual: Current);
        Assert.Equal(expected: "1.2.0", actual: Assert.Single(collection: InstallSeed.Policy(installRoot: Install).RejectedPayloads).Version);
        // The refusal is stable: the next stub run refuses the same way and launches nothing.
        Assert.Null(@object: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
    }
    // An authentic manifest's revocations are durable after a read-only check, even when its own version is refused, and
    // without consuming its sequence; a name that cannot be a version names no install and is dropped before any write.
    [Fact]
    public async Task CheckPersistsAuthenticRevocationsEvenWhenItRefusesTheManifest() {
        _ = InstallSeed.Install(installRoot: Install, version: "1.0.0");
        var refused = Manifest(revoked: ["1.5.0", "../outside", "1.0.0"], version: "1.0.0");

        Publish(manifest: refused);
        var check = await Service().CheckAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: UpdateCheckOutcome.Refused, actual: check.Outcome);
        Assert.Equal(expected: ["1.5.0", "1.0.0"], actual: InstallSeed.Policy(installRoot: Install).RevokedVersions);
        // The check consumed nothing: an admissible manifest at the same sequence still installs.
        Publish(manifest: Manifest(revoked: ["1.5.0"], version: "1.1.0"));
        Assert.True(condition: (await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        // The revoked version is never a rollback target again.
        Assert.Null(@object: InstallSeed.RollbackTarget(installRoot: Install));
    }
    // U01: a check that starts while the stub holds the transaction waits for it, and merges its revocation into the record
    // that holds the stub's rejection rather than replacing it with an earlier snapshot.
    [Fact]
    public async Task CheckWaitsForTheStubsTransactionAndKeepsItsRejection() {
        _ = InstallSeed.Install(installRoot: Install, version: "1.0.0");
        _ = InstallSeed.Install(installRoot: Install, proven: false, version: "1.1.0");
        Publish(manifest: Manifest(revoked: ["1.5.0"], version: "1.6.0"));
        Task<UpdateCheckResult> check;

        using (var stub = InstallTransaction.TryBegin(installRoot: Install)!) {
            check = Task.Run(function: () => Service().CheckAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken), cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(expected: "1.1.0", actual: stub.SelectLaunch(maxAttempts: 1).Version);
            Assert.Equal(expected: "1.0.0", actual: stub.SelectLaunch(maxAttempts: 1).Version);
            Assert.False(condition: check.IsCompleted, userMessage: "a check verified while another holder had the install transaction");
        }
        Assert.Equal(expected: UpdateCheckOutcome.Available, actual: (await check).Outcome);
        var policy = InstallSeed.Policy(installRoot: Install);

        Assert.Equal(expected: "1.1.0", actual: Assert.Single(collection: policy.RejectedPayloads).Version);
        Assert.Equal(expected: ["1.5.0"], actual: policy.RevokedVersions);
        Assert.Equal(expected: "1.0.0", actual: policy.Current);
    }
    // U02: once a launch reached generation 2, a correctly signed, higher version declaring generation 1 is refused at
    // admission, before it can become current.
    [Fact]
    public async Task ALowerGenerationThanALaunchReachedIsRefusedBeforeItBecomesCurrent() {
        _ = InstallSeed.Install(installRoot: Install, proven: false, stateGeneration: 2, version: "2.0.0");
        Assert.Equal(expected: "2.0.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 3).Version);
        Assert.Equal(expected: 2, actual: InstallSeed.Policy(installRoot: Install).StateGenerationFloor);
        Publish(manifest: Manifest(payload: Rebuilt, stateGeneration: 1, version: "2.1.0"), content: Rebuilt);
        var check = await Service(installedVersion: "2.0.0").CheckAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        var apply = await Service(installedVersion: "2.0.0").ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: UpdateCheckOutcome.Refused, actual: check.Outcome);
        Assert.Contains(expectedSubstring: "below this install's floor 2", actualString: check.Detail);
        Assert.False(condition: apply.Applied);
        Assert.Equal(expected: "2.0.0", actual: Current);
    }
    // The lead's U03 ruling: the floor follows the final launch choice, and is durable in the record when selection
    // returns, before the stub starts the process; a later lower-generation install is then refused.
    [Fact]
    public async Task TheFloorFollowsTheLaunchChoiceBeforeTheProcessStarts() {
        _ = InstallSeed.Install(installRoot: Install, stateGeneration: 1, version: "1.0.0");
        _ = InstallSeed.Install(installRoot: Install, proven: false, stateGeneration: 3, version: "1.1.0");
        Assert.Equal(expected: 1, actual: InstallSeed.Policy(installRoot: Install).StateGenerationFloor);
        Assert.Equal(expected: "1.1.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        Assert.Equal(expected: 3, actual: InstallSeed.Policy(installRoot: Install).StateGenerationFloor);
        // 1.1.0 failed its launch; 1.0.0 is below the floor its launch raised, so nothing may run.
        var decision = InstallSeed.Launch(installRoot: Install, maxAttempts: 1);

        Assert.Null(@object: decision.Version);
        Assert.Contains(expectedSubstring: "below this install's floor 3", actualString: decision.Refusal);
        Publish(manifest: Manifest(payload: Rebuilt, stateGeneration: 2, version: "1.2.0"), content: Rebuilt);
        Assert.False(condition: (await Service(installedVersion: "1.1.0").ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
    }
    // U06: B's payload exhausts its attempts but the fourth stub run has not happened yet when an older session applies
    // B.1, the same payload under a new version and sequence. The count belongs to the payload, so B.1 inherits it and
    // the next stub run rejects it instead of launching it again.
    [Fact]
    public async Task AnUpdateCarryingTheSamePayloadInheritsItsExhaustedAttempts() {
        _ = InstallSeed.Install(installRoot: Install, version: "1.0.0");
        Publish(manifest: Manifest(version: "1.1.0"));
        Assert.True(condition: (await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        for (var attempt = 0; (attempt < 3); attempt++) {
            Assert.Equal(expected: "1.1.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 3).Version);
        }
        Publish(manifest: Manifest(sequence: 2, version: "1.1.1"));
        Assert.True(condition: (await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        var decision = InstallSeed.Launch(installRoot: Install, maxAttempts: 3);

        Assert.Equal(expected: "1.0.0", actual: decision.Version);
        Assert.Equal(expected: "1.1.1", actual: decision.RolledBackFrom);
    }
    // U07: a payload that succeeds on its last allowed attempt is proven and its attempts cleared in one commit, so the
    // next launch counts afresh instead of rejecting it.
    [Fact]
    public void ProofOnTheLastAllowedAttemptClearsItsAttemptsInTheSameCommit() {
        var record = InstallSeed.Install(installRoot: Install, proven: false, version: "1.0.0");

        for (var attempt = 0; (attempt < 3); attempt++) {
            Assert.Equal(expected: "1.0.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 3).Version);
        }
        Assert.True(condition: InstallSeed.Healthy(installRoot: Install, version: "1.0.0"));
        var policy = InstallSeed.Policy(installRoot: Install);

        Assert.Equal(expected: 0, actual: policy.AttemptsFor(payloadPin: record.PayloadPin));
        Assert.Equal(expected: record.Sequence, actual: Assert.Single(collection: policy.ProvenInstalls).Sequence);
        Assert.Equal(expected: "1.0.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 3).Version);
        Assert.Equal(expected: 1, actual: InstallSeed.Policy(installRoot: Install).AttemptsFor(payloadPin: record.PayloadPin));
    }
    // U10: a proof of health names its exact install. Once a proven version's directory is gone, a reinstall under the same
    // name is a new install with a new sequence, and inherits nothing: it is never a rollback target until it proves itself.
    [Fact]
    public void AReinstalledVersionDoesNotInheritAnOldProof() {
        _ = InstallSeed.Install(installRoot: Install, version: "1.0.0");
        var original = InstallSeed.Install(installRoot: Install, version: "1.1.0");

        _ = InstallSeed.Install(installRoot: Install, proven: false, version: "1.2.0");
        Assert.True(condition: InstallPruner.RemoveVersion(installRoot: Install, version: "1.1.0"));
        Assert.Equal(expected: "1.2.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        Assert.Equal(expected: "1.0.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        var reinstalled = InstallSeed.Install(installRoot: Install, payloadPin: original.PayloadPin, proven: false, version: "1.1.0");

        Assert.NotEqual(expected: original.Sequence, actual: reinstalled.Sequence);
        Assert.DoesNotContain(collection: InstallSeed.Policy(installRoot: Install).ProvenInstalls, filter: proof => (proof.Version == "1.1.0"));
        _ = InstallSeed.Install(installRoot: Install, proven: false, version: "1.3.0");
        Assert.Equal(expected: "1.0.0", actual: InstallSeed.RollbackTarget(installRoot: Install));
        Assert.Equal(expected: "1.3.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        Assert.Equal(expected: "1.0.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
    }
    // A record that would not read back is never published (U09), and a record that does not read is never treated as
    // empty: the stub refuses by name rather than launch on a guess.
    [Fact]
    public void AnInvalidReleaseRecordFailsClosed() {
        _ = InstallSeed.Install(installRoot: Install, version: "1.0.0");
        using (var transaction = InstallTransaction.TryBegin(installRoot: Install)!) {
            Assert.Null(@object: transaction.MergeRevocations(precedence: ReleaseVersion.Compare, revoked: ["../escape", "a/b", ""]));
            Assert.Empty(collection: transaction.Policy.RevokedVersions);
        }
        File.WriteAllText(path: Path.Combine(Install, InstallLayout.StateDirectoryName, InstallLayout.ReleasePolicyFileName), contents: "{\"schema\":\"puck.release.policy.v0\"}");
        using var stub = InstallTransaction.TryBegin(installRoot: Install)!;

        Assert.Throws<InvalidDataException>(testCode: () => stub.SelectLaunch(maxAttempts: 3));
    }
}

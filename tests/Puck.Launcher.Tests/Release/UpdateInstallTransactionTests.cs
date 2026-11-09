using Puck.Assets;
using Puck.Confinement;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Exercises persisted restart boundaries with real signatures, replay files, payload hashes and the release
/// record.</summary>
public sealed partial class UpdateInstallTransactionTests : IDisposable {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task RecoveryRetainsIntentAcrossSequenceContentionAndBackwardsClock(bool backwardsClock) {
        Publish(manifest: Manifest());
        Assert.False(condition: (await Service(applier: new InterruptedApplier(boundary: "before-pointer"))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        var original = File.ReadAllBytes(path: Intent);
        using (var lease = backwardsClock ? null : Puck.Abstractions.FileLock.TryAcquire(path: Path.Combine(Install, "sequence.lock"))) {
            if (!backwardsClock) { Assert.NotNull(@object: lease); }
            var refused = await Service().ApplyAsync(now: backwardsClock ? Now.AddSeconds(seconds: -1) : Now,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(condition: refused.Applied);
            Assert.Contains(expectedSubstring: backwardsClock ? "clock" : "busy", actualString: refused.Detail);
            Assert.Equal(expected: original, actual: File.ReadAllBytes(path: Intent));
        }
        var resumed = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: resumed.Applied, userMessage: resumed.Detail);
        Assert.False(condition: File.Exists(path: Intent));
    }

    [Fact]
    public async Task TerminalApplierRefusalRetiresIntentAndAllowsTheNextRelease() {
        Publish(manifest: Manifest());
        Assert.False(condition: (await Service(applier: new InterruptedApplier(boundary: "after-pointer"))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        var record = Path.Combine(Install, "versions", "1.0.1", InstallRecord.FileName);

        File.WriteAllText(path: record, contents: File.ReadAllText(path: record).Replace(oldValue: "\"stateGeneration\": 1", newValue: "\"stateGeneration\": 2", comparisonType: StringComparison.Ordinal));
        var refused = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: refused.Applied);
        Assert.Contains(expectedSubstring: "state generation", actualString: refused.Detail);
        Assert.False(condition: File.Exists(path: Intent));
        Publish(manifest: Manifest(version: "1.0.2", sequence: 2));
        var next = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: next.Applied, userMessage: next.Detail);
    }

    private readonly ReleaseChainFixture m_fixture = new();
    private readonly TempStagingRoot m_root = new();

    private static readonly byte[] Payload = [1, 2, 3, 4];

    private static DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch);
    private string Feed => Path.Combine(path1: m_root.RootPath, path2: "feed");
    private string Install => Path.Combine(path1: m_root.RootPath, path2: "install");
    private string Intent => Path.Combine(path1: Install, path2: "install-intent.json");
    private string? Current => InstallSeed.Current(installRoot: Install);
    private string PayloadPath => Path.Combine(path1: Install, path2: "versions", path3: "1.0.1", path4: "a.dll");

    private AttestationReleaseVerifier Verifier(ReleaseChainFixture? fixture = null) {
        fixture ??= m_fixture;
        return new AttestationReleaseVerifier(codec: fixture.Codec,
            sequenceStore: new FileReleaseSequenceStore(filePath: Path.Combine(path1: Install, path2: "sequence")),
            trustList: fixture.BuildTrustList(replayHorizon: TimeSpan.FromDays(days: 30)));
    }
    // One verification under its own transaction on the install root, as a check runs it.
    private ReleaseVerifyOutcome Verify(ReleaseManifest manifest, bool advanceSequence = false, string installedVersion = "1.0.0") {
        using var transaction = InstallTransaction.TryBegin(installRoot: Install)!;

        return Verifier().Verify(advanceSequence: advanceSequence, installedVersion: installedVersion, manifest: manifest, now: Now, transaction: transaction);
    }
    private ReleaseManifest Manifest(string version = "1.0.1", ulong sequence = 1, int rollout = 100, byte[]? payload = null, int stateGeneration = 1,
        IReadOnlyList<string>? revoked = null, string? notes = null, string? minimumSupported = null) {
        var bytes = (payload ?? Payload);

        return m_fixture.Sign(
            document: new ReleaseManifest(App: "puck.world", Channel: "stable", MinimumSupported: minimumSupported, Notes: notes,
                Payloads: [new ReleasePayload(Rid: UpdateService.CurrentRid(), Files: [new ReleasePayloadFile(Path: "a.dll", Hash: ContentPin.Compute(content: bytes).ToString(), Size: bytes.Length)])],
                Revoked: revoked, Rollout: new ReleaseRollout(Percent: rollout), Schema: ReleaseManifest.CurrentSchema,
                Signature: null, StateGeneration: stateGeneration, Version: version),
            notBefore: ReleaseChainFixture.Epoch, notAfter: (ReleaseChainFixture.Epoch + 3600), sequence: sequence);
    }
    private void Publish(ReleaseManifest manifest, bool payload = true, byte[]? content = null) {
        var directory = Path.Combine(path1: Feed, path2: "stable");

        Directory.CreateDirectory(path: directory);
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: "manifest.json"), bytes: ReleaseChainFixture.ToWireBytes(manifest: manifest));
        if (payload) { new ContentAddressedStore(root: Feed).Put(content: (content ?? Payload)); }
    }
    private UpdateService Service(IReleaseVerifier? verifier = null, IUpdateApplier? applier = null, IUpdateStager? stager = null, string installedVersion = "1.0.0") => new(
        source: new DirectoryReleaseSource(root: Feed), verifier: (verifier ?? Verifier()), applier: (applier ?? new FileUpdateApplier()),
        stager: (stager ?? new ContentAddressedUpdateStager(source: new DirectoryReleaseSource(root: Feed), cache: new ContentAddressedStore(root: Path.Combine(path1: Install, path2: "cache")), cacheRoot: Install)),
        options: new UpdateOptions(App: "puck.world", CacheRoot: Install, Channel: "stable", InstalledVersion: installedVersion, TrustAnchor: m_fixture.TrustAnchor, InstallId: new string(c: '0', count: 32)));

    [Fact]
    public async Task OutsideRollout_DoesNotConsumeSequenceOrCreateIntent() {
        var manifest = Manifest(rollout: 0);

        Publish(manifest: manifest);
        var outcome = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: outcome.Applied);
        Assert.Contains(expectedSubstring: "OutsideRollout", actualString: outcome.Detail);
        Assert.True(condition: Verify(manifest: manifest).Accepted);
        Assert.False(condition: File.Exists(path: Intent));
        Assert.Null(@object: Current);
    }
    [Fact]
    public async Task FailedDownload_CanRetryTheSameSequenceAfterRestart() {
        var manifest = Manifest();

        Publish(manifest: manifest, payload: false);
        Assert.False(condition: (await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        Assert.False(condition: File.Exists(path: Intent));
        Assert.True(condition: Verify(manifest: manifest).Accepted);

        Publish(manifest: manifest);
        var outcome = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: outcome.Applied, userMessage: outcome.Detail);
        Assert.Equal(expected: "1.0.1", actual: Current);
        Assert.False(condition: File.Exists(path: Intent));
        Assert.False(condition: Verify(manifest: manifest).Accepted);
    }
    [InlineData("before-pointer")]
    [InlineData("after-record")]
    [InlineData("after-pointer")]
    [Theory]
    public async Task InterruptedInstall_ResumesPinnedIntentAcrossRestartAndExpiry(string boundary) {
        var manifest = Manifest();

        Publish(manifest: manifest);
        var interrupted = await Service(verifier: new InterruptedVerifier(inner: Verifier(), boundary: boundary),
            applier: new InterruptedApplier(boundary: boundary)).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: interrupted.Applied);
        Assert.True(condition: File.Exists(path: Intent));
        // An install record without its commit selects nothing: the version is neither current nor proven.
        Assert.Equal(expected: ((boundary == "after-pointer") ? "1.0.1" : null), actual: Current);

        // A newer network response cannot replace work already authorized and pinned in the local journal.
        Publish(manifest: Manifest(version: "1.0.2", sequence: 2));
        var recovered = await Service(installedVersion: ((boundary == "after-pointer") ? "1.0.1" : "1.0.0"),
            stager: ((boundary == "after-pointer") ? new RefusingStager() : null))
            .ApplyAsync(now: Now.AddHours(hours: 2), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: recovered.Applied, userMessage: recovered.Detail);
        Assert.Equal(expected: "1.0.1", actual: Current);
        Assert.Equal(expected: Payload, actual: File.ReadAllBytes(path: PayloadPath));
        Assert.False(condition: File.Exists(path: Intent));
        Assert.False(condition: Verify(manifest: manifest).Accepted);
    }
    [InlineData("before-reservation")]
    [InlineData("after-reservation")]
    [InlineData("refused-reservation")]
    [Theory]
    public async Task InterruptedReservationCannotCreateResumableAuthorization(string boundary) {
        var manifest = Manifest();

        Publish(manifest: manifest);
        var interrupted = await Service(verifier: new InterruptedVerifier(inner: Verifier(), boundary: boundary))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: interrupted.Applied);
        Assert.False(condition: File.Exists(path: Intent));
        Assert.Null(@object: Current);
        Assert.Equal(expected: (boundary != "after-reservation"), actual: Verify(manifest: manifest).Accepted);
        // No intent exists to revive an expired authorization; a fresh signed release is required.
        Assert.False(condition: (await Service().ApplyAsync(now: Now.AddHours(hours: 2), cancellationToken: TestContext.Current.CancellationToken)).Applied);
        Publish(manifest: Manifest(version: "1.0.2", sequence: 2));
        Assert.True(condition: (await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        Assert.Equal(expected: "1.0.2", actual: Current);
    }
    [Fact]
    public async Task InterruptedStage_ReplacementManifestCannotInheritFilesItDoesNotDeclare() {
        var first = Manifest();

        first = m_fixture.Sign(document: first with {
            Signature = null,
            Payloads = [first.Payloads[0] with { Files = [first.Payloads[0].Files[0], first.Payloads[0].Files[0] with { Path = "obsolete.dll" }] }],
        }, notBefore: ReleaseChainFixture.Epoch, notAfter: (ReleaseChainFixture.Epoch + 3600), sequence: 1);
        Publish(manifest: first);
        var stager = new ContentAddressedUpdateStager(source: new DirectoryReleaseSource(root: Feed),
            cache: new ContentAddressedStore(root: Path.Combine(path1: Install, path2: "cache")), cacheRoot: Install);
        var interrupted = await Service(stager: new InterruptedStager(inner: stager))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: interrupted.Applied);
        Assert.False(condition: File.Exists(path: Intent));
        var obsolete = Path.Combine(path1: Install, path2: "versions", path3: "1.0.1", path4: "obsolete.dll");

        Assert.True(condition: File.Exists(path: obsolete));

        // Both releases are genuinely signed for this channel. No local attacker writes the install tree:
        // interruption precedes the intent, then a new signed sequence replaces the same unpublished version.
        Publish(manifest: Manifest(sequence: 2));
        var replacement = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: replacement.Applied, userMessage: replacement.Detail);
        Assert.Equal(expected: "1.0.1", actual: Current);
        Assert.False(condition: File.Exists(path: obsolete));
        Assert.Equal(expected: Payload, actual: File.ReadAllBytes(path: PayloadPath));
    }
    [Fact]
    public async Task Recovery_RefusesCorruptActiveContentWithoutRestagingIt() {
        Publish(manifest: Manifest());
        Assert.False(condition: (await Service(applier: new InterruptedApplier(boundary: "after-pointer"))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        File.WriteAllBytes(path: PayloadPath, bytes: [9]);
        var recovered = await Service(stager: new RefusingStager()).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: recovered.Applied);
        Assert.Contains(expectedSubstring: "hash", actualString: recovered.Detail);
        Assert.Equal(expected: new byte[] { 9 }, actual: File.ReadAllBytes(path: PayloadPath));
        Assert.False(condition: File.Exists(path: Intent));
    }
    [InlineData("newer-mark")]
    [InlineData("newer-current")]
    [InlineData("changed-trust")]
    [Theory]
    public async Task Recovery_RefusesSupersededStateAndChangedTrust(string changed) {
        Publish(manifest: Manifest());
        Assert.False(condition: (await Service(applier: new InterruptedApplier(boundary: "before-pointer"))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        if (changed == "newer-mark") {
            Assert.True(condition: Verify(manifest: Manifest(version: "1.0.2", sequence: 2), advanceSequence: true).Accepted);
        } else if (changed == "newer-current") {
            _ = InstallSeed.Install(installRoot: Install, proven: false, version: "1.0.2");
        }
        var recovered = await Service(stager: new RefusingStager(),
            verifier: ((changed == "changed-trust") ? Verifier(fixture: new ReleaseChainFixture()) : null))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: recovered.Applied);
        Assert.False(condition: File.Exists(path: Intent));
        Assert.Equal(expected: ((changed == "newer-current") ? "1.0.2" : null), actual: Current);
        Publish(manifest: Manifest(version: "1.0.3", sequence: 3));
        var fresh = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: fresh.Applied, userMessage: fresh.Detail);
    }
    [Fact]
    public async Task SeparateServices_CannotWriteOneInstallRootConcurrently() {
        Publish(manifest: Manifest());
        using var blocked = new BlockingStager();
        var first = Service(stager: blocked).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        await blocked.Entered.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        try {
            Assert.Null(@object: InstallTransaction.TryBegin(installRoot: Install));
            var second = await Service(stager: new RefusingStager()).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: second.Applied);
            Assert.Contains(expectedSubstring: "busy", actualString: second.Detail);
        } finally {
            blocked.Release.TrySetResult();
        }
        Assert.False(condition: (await first).Applied);
        Assert.False(condition: File.Exists(path: Intent));
    }
    [Fact]
    public async Task StubTransaction_BlocksUpdateUntilTheChildCanLaunch() {
        Publish(manifest: Manifest());
        using (InstallTransaction.TryBegin(installRoot: Install)) {
            var blocked = await Service(stager: new RefusingStager()).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(condition: blocked.Applied);
            Assert.Contains(expectedSubstring: "busy", actualString: blocked.Detail);
        }
        var result = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: result.Applied, userMessage: result.Detail);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task StubRejection_RetiresOnlyTheRejectedVersionsIntent(bool newerPending) {
        _ = InstallSeed.Install(installRoot: Install, version: "1.0.0");
        Publish(manifest: Manifest());
        var first = await Service(applier: (newerPending ? null : new InterruptedApplier(boundary: "after-pointer")))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: newerPending, actual: first.Applied);
        if (newerPending) {
            Publish(manifest: Manifest(version: "1.0.2", sequence: 2, payload: [5, 6]), content: [5, 6]);
            Assert.False(condition: (await Service(applier: new InterruptedApplier(boundary: "before-pointer"))
                .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        }
        Assert.True(condition: File.Exists(path: Intent));
        // 1.0.1 fails its one allowed launch; the next stub launch rejects its payload and rolls back to proven 1.0.0.
        Assert.Equal(expected: "1.0.1", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        Assert.Equal(expected: "1.0.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        Assert.Equal(expected: "1.0.0", actual: Current);
        var resumed = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: newerPending, actual: resumed.Applied);
        if (!newerPending) { Assert.Contains(expectedSubstring: "rejected", actualString: resumed.Detail); }
        Assert.False(condition: File.Exists(path: Intent));
        Assert.Equal(expected: (newerPending ? "1.0.2" : "1.0.0"), actual: Current);
    }
    [Fact]
    public async Task FreshApply_DoesNotStageOverAnAlreadyActiveVersion() {
        Publish(manifest: Manifest());
        Assert.True(condition: (await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        // Even a valid new sequence is insufficient to authorize overwriting an active version directory.
        Publish(manifest: Manifest(sequence: 2));
        var result = await Service(stager: new RefusingStager()).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: result.Applied);
        Assert.Contains(expectedSubstring: "current install", actualString: result.Detail);
        Assert.Equal(expected: Payload, actual: File.ReadAllBytes(path: PayloadPath));
    }
    [Fact]
    public async Task Recovery_RefusesWhenRollbackDropsCurrentBelowPendingIntentsMinimumSupported() {
        _ = InstallSeed.Install(installRoot: Install, version: "0.8.0");
        _ = InstallSeed.Install(installRoot: Install, proven: false, version: "1.0.1");
        Publish(manifest: Manifest(version: "1.0.2", minimumSupported: "1.0.0"));
        Assert.False(condition: (await Service(installedVersion: "1.0.1", applier: new InterruptedApplier(boundary: "before-pointer"))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        Assert.True(condition: File.Exists(path: Intent));

        // 1.0.1 fails its one allowed launch and the stub rolls back to proven 0.8.0, keeping the newer pending intent.
        Assert.Equal(expected: "1.0.1", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        Assert.Equal(expected: "0.8.0", actual: InstallSeed.Launch(installRoot: Install, maxAttempts: 1).Version);
        var recovered = await Service(installedVersion: "0.8.0", stager: new RefusingStager())
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: recovered.Applied);
        Assert.Contains(expectedSubstring: "minimumSupported", actualString: recovered.Detail);
        Assert.Equal(expected: "0.8.0", actual: Current);
        Assert.False(condition: File.Exists(path: Intent));
        Publish(manifest: Manifest(version: "1.0.3", sequence: 2));
        var bridge = await Service(installedVersion: "0.8.0").ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: bridge.Applied, userMessage: bridge.Detail);
    }
    [Fact]
    public async Task Apply_RefusesCaseInsensitiveVersionCollisionWithActiveInstall() {
        _ = InstallSeed.Install(installRoot: Install, version: "1.0.1-Alpha");
        Publish(manifest: Manifest(version: "1.0.1-alpha", sequence: 1));
        var outcome = await Service(installedVersion: "1.0.0", stager: new RefusingStager())
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(condition: outcome.Applied);
        Assert.Contains(expectedSubstring: "collides", actualString: outcome.Detail, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InterruptedVerifier(IReleaseVerifier inner, string boundary) : IReleaseVerifier {
        public ReleaseVerifyOutcome Verify(ReleaseManifest manifest, DateTimeOffset now, string installedVersion, bool advanceSequence, InstallTransaction transaction) =>
            inner.Verify(advanceSequence: advanceSequence, installedVersion: installedVersion, manifest: manifest, now: now, transaction: transaction);
        public ReleaseVerifyOutcome ReserveInstall(ReleaseManifest manifest, DateTimeOffset verifiedAt, string installedVersion, InstallTransaction transaction) {
            if (boundary == "before-reservation") { throw new IOException(message: "injected interruption before reservation"); }
            if (boundary == "refused-reservation") { return ReleaseVerifyOutcome.Refuse(reason: "injected reservation refusal"); }
            var result = inner.ReserveInstall(installedVersion: installedVersion, manifest: manifest, verifiedAt: verifiedAt, transaction: transaction);

            if (boundary == "after-reservation") { throw new IOException(message: "injected interruption after reservation"); }
            return result;
        }
    }
    // "after-record" leaves what a process killed between the install record's durable write and the release record's
    // commit leaves: a record for the next sequence that no commit selects.
    private sealed class InterruptedApplier(string boundary) : IUpdateApplier {
        public UpdateApplyResult Apply(ReleaseManifest manifest, string rid, InstallTransaction transaction, bool recoverInterruptedApply = false) {
            if (boundary == "before-pointer") { throw new IOException(message: "injected interruption before the commit"); }
            if (boundary == "after-record") {
                File.WriteAllText(path: Path.Combine(transaction.RootPath, "versions", manifest.Version, InstallRecord.FileName),
                    contents: $"{{\"sequence\":{transaction.Policy.LastSequence + 1},\"rid\":\"{rid}\",\"stateGeneration\":{manifest.StateGeneration},\"payloadPin\":\"{ReleasePayloadIdentity.Of(payload: manifest.Payloads[0], stateGeneration: manifest.StateGeneration)}\",\"files\":[\"{manifest.Payloads[0].Files[0].Hash}\"]}}");
                throw new IOException(message: "injected interruption after the install record");
            }
            var result = new FileUpdateApplier().Apply(manifest: manifest, recoverInterruptedApply: recoverInterruptedApply, rid: rid, transaction: transaction);

            if (boundary == "after-pointer") { throw new IOException(message: "injected interruption after the commit"); }
            return result;
        }
    }
    private sealed class RefusingStager : IUpdateStager {
        public Task<UpdateStageResult> StageAsync(ReleaseManifest manifest, string rid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message: "staging must not run on this path");
    }
    private sealed class InterruptedStager(IUpdateStager inner) : IUpdateStager {
        public async Task<UpdateStageResult> StageAsync(ReleaseManifest manifest, string rid, CancellationToken cancellationToken) {
            var result = await inner.StageAsync(cancellationToken: cancellationToken, manifest: manifest, rid: rid);

            Assert.True(condition: result.Staged, userMessage: result.RefusalReason);
            throw new IOException(message: "injected interruption after staging and before install intent");
        }
    }
    private sealed class BlockingStager : IUpdateStager, IDisposable {
        public TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpdateStageResult> StageAsync(ReleaseManifest manifest, string rid, CancellationToken cancellationToken) {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken: cancellationToken);
            return UpdateStageResult.Refuse(reason: "injected staging failure");
        }
        public void Dispose() => Release.TrySetResult();
    }

    public void Dispose() => m_root.Dispose();
}

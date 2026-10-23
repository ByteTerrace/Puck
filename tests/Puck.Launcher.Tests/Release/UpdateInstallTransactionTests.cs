using Puck.Assets;
using Puck.Launcher.Release;
using Puck.Launcher.Stub;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Exercises persisted restart boundaries with real signatures, replay files, payload hashes and pointers.</summary>
public sealed partial class UpdateInstallTransactionTests : IDisposable {
    private readonly ReleaseChainFixture m_fixture = new();
    private readonly TempStagingRoot m_root = new();
    private static readonly byte[] Payload = [1, 2, 3, 4];
    private static DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch);
    private string Feed => Path.Combine(path1: m_root.RootPath, path2: "feed");
    private string Install => Path.Combine(path1: m_root.RootPath, path2: "install");
    private string Intent => Path.Combine(path1: Install, path2: "install-intent.json");
    private string Current => Path.Combine(path1: Install, path2: "current");
    private string PayloadPath => Path.Combine(Install, "versions", "1.0.1", "a.dll");

    private AttestationReleaseVerifier Verifier(ReleaseChainFixture? fixture = null) {
        fixture ??= m_fixture;
        return new AttestationReleaseVerifier(codec: fixture.Codec,
            sequenceStore: new FileReleaseSequenceStore(filePath: Path.Combine(path1: Install, path2: "sequence")),
            trustList: fixture.BuildTrustList(replayHorizon: TimeSpan.FromDays(days: 30)));
    }

    private ReleaseManifest Manifest(string version = "1.0.1", ulong sequence = 1, int rollout = 100) => m_fixture.Sign(
        document: new ReleaseManifest(App: "puck.world", Channel: "stable", MinimumSupported: null, Notes: null,
            Payloads: [new ReleasePayload(Rid: UpdateService.CurrentRid(), Files: [new ReleasePayloadFile(Path: "a.dll", Hash: ContentPin.Compute(content: Payload).ToString(), Size: Payload.Length)])],
            Revoked: null, Rollout: new ReleaseRollout(Percent: rollout), Schema: ReleaseManifest.CurrentSchema,
            Signature: null, StateGeneration: 1, Version: version),
        notBefore: ReleaseChainFixture.Epoch, notAfter: ReleaseChainFixture.Epoch + 3600, sequence: sequence);

    private void Publish(ReleaseManifest manifest, bool payload = true) {
        var directory = Path.Combine(path1: Feed, path2: "stable");
        Directory.CreateDirectory(path: directory);
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: "manifest.json"), bytes: ReleaseChainFixture.ToWireBytes(manifest: manifest));
        if (payload) { new ContentAddressedStore(root: Feed).Put(content: Payload); }
    }

    private UpdateService Service(IReleaseVerifier? verifier = null, IUpdateApplier? applier = null, IUpdateStager? stager = null, string installedVersion = "1.0.0") => new(
        source: new DirectoryReleaseSource(root: Feed), verifier: verifier ?? Verifier(), applier: applier ?? new FileUpdateApplier(),
        stager: stager ?? new ContentAddressedUpdateStager(source: new DirectoryReleaseSource(root: Feed), cache: new ContentAddressedStore(root: Path.Combine(path1: Install, path2: "cache")), cacheRoot: Install),
        options: new UpdateOptions(App: "puck.world", CacheRoot: Install, Channel: "stable", InstalledVersion: installedVersion, TrustAnchor: m_fixture.TrustAnchor, InstallId: new string(c: '0', count: 32)));

    [Fact]
    public async Task OutsideRollout_DoesNotConsumeSequenceOrCreateIntent() {
        var manifest = Manifest(rollout: 0);
        Publish(manifest: manifest);
        var outcome = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: outcome.Applied);
        Assert.Contains(expectedSubstring: "OutsideRollout", actualString: outcome.Detail);
        Assert.True(condition: Verifier().Verify(manifest: manifest, now: Now, installedVersion: "1.0.0", advanceSequence: false).Accepted);
        Assert.False(condition: File.Exists(path: Intent));
        Assert.False(condition: File.Exists(path: Current));
    }

    [Fact]
    public async Task FailedDownload_CanRetryTheSameSequenceAfterRestart() {
        var manifest = Manifest();
        Publish(manifest: manifest, payload: false);
        Assert.False(condition: (await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        Assert.False(condition: File.Exists(path: Intent));
        Assert.True(condition: Verifier().Verify(manifest: manifest, now: Now, installedVersion: "1.0.0", advanceSequence: false).Accepted);

        Publish(manifest: manifest);
        var outcome = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: outcome.Applied, userMessage: outcome.Detail);
        Assert.Equal(expected: "1.0.1", actual: File.ReadAllText(path: Current));
        Assert.False(condition: File.Exists(path: Intent));
        Assert.False(condition: Verifier().Verify(manifest: manifest, now: Now, installedVersion: "1.0.0", advanceSequence: false).Accepted);
    }

    [Theory]
    [InlineData("before-reservation")]
    [InlineData("after-reservation")]
    [InlineData("before-pointer")]
    [InlineData("after-pointer")]
    public async Task InterruptedInstall_ResumesPinnedIntentAcrossRestartAndExpiry(string boundary) {
        var manifest = Manifest();
        Publish(manifest: manifest);
        var interrupted = await Service(verifier: new InterruptedVerifier(inner: Verifier(), boundary: boundary),
            applier: new InterruptedApplier(boundary: boundary)).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: interrupted.Applied);
        Assert.True(condition: File.Exists(path: Intent));

        // A newer network response cannot replace work already authorized and pinned in the local journal.
        Publish(manifest: Manifest(version: "1.0.2", sequence: 2));
        var recovered = await Service(installedVersion: boundary == "after-pointer" ? "1.0.1" : "1.0.0",
            stager: boundary == "after-pointer" ? new RefusingStager() : null)
            .ApplyAsync(now: Now.AddHours(hours: 2), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: recovered.Applied, userMessage: recovered.Detail);
        Assert.Equal(expected: "1.0.1", actual: File.ReadAllText(path: Current));
        Assert.Equal(expected: Payload, actual: File.ReadAllBytes(path: PayloadPath));
        Assert.False(condition: File.Exists(path: Intent));
        Assert.False(condition: Verifier().Verify(manifest: manifest, now: Now, installedVersion: "1.0.0", advanceSequence: false).Accepted);
    }

    [Fact]
    public async Task InterruptedStage_ReplacementManifestCannotInheritFilesItDoesNotDeclare() {
        var first = Manifest();
        first = m_fixture.Sign(document: first with {
            Signature = null,
            Payloads = [first.Payloads[0] with { Files = [first.Payloads[0].Files[0], first.Payloads[0].Files[0] with { Path = "obsolete.dll" }] }],
        }, notBefore: ReleaseChainFixture.Epoch, notAfter: ReleaseChainFixture.Epoch + 3600, sequence: 1);
        Publish(manifest: first);
        var stager = new ContentAddressedUpdateStager(source: new DirectoryReleaseSource(root: Feed),
            cache: new ContentAddressedStore(root: Path.Combine(path1: Install, path2: "cache")), cacheRoot: Install);
        var interrupted = await Service(stager: new InterruptedStager(inner: stager))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: interrupted.Applied);
        Assert.False(condition: File.Exists(path: Intent));
        var obsolete = Path.Combine(Install, "versions", "1.0.1", "obsolete.dll");
        Assert.True(condition: File.Exists(path: obsolete));

        // Both releases are genuinely signed for this channel. No local attacker writes the install tree:
        // interruption precedes the intent, then a new signed sequence replaces the same unpublished version.
        Publish(manifest: Manifest(sequence: 2));
        var replacement = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: replacement.Applied, userMessage: replacement.Detail);
        Assert.Equal(expected: "1.0.1", actual: File.ReadAllText(path: Current));
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
        Assert.True(condition: File.Exists(path: Intent));
    }

    [Theory]
    [InlineData("newer-mark")]
    [InlineData("newer-pointer")]
    [InlineData("changed-trust")]
    public async Task Recovery_RefusesSupersededStateAndChangedTrust(string changed) {
        Publish(manifest: Manifest());
        Assert.False(condition: (await Service(applier: new InterruptedApplier(boundary: "before-pointer"))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        if (changed == "newer-mark") {
            Assert.True(condition: Verifier().Verify(manifest: Manifest(version: "1.0.2", sequence: 2), now: Now,
                installedVersion: "1.0.0", advanceSequence: true).Accepted);
        } else if (changed == "newer-pointer") {
            File.WriteAllText(path: Current, contents: "1.0.2");
        }
        var recovered = await Service(stager: new RefusingStager(),
            verifier: changed == "changed-trust" ? Verifier(fixture: new ReleaseChainFixture()) : null)
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: recovered.Applied);
        Assert.True(condition: File.Exists(path: Intent));
        Assert.Equal(expected: changed == "newer-pointer" ? "1.0.2" : null, actual: File.Exists(path: Current) ? File.ReadAllText(path: Current) : null);
    }

    [Fact]
    public async Task SeparateServices_CannotWriteOneInstallRootConcurrently() {
        Publish(manifest: Manifest());
        using var blocked = new BlockingStager();
        var first = Service(stager: blocked).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        await blocked.Entered.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        try {
            Assert.Throws<IOException>(testCode: () => StubInstall.AcquireUpdateLease(installRoot: Install));
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
    public async Task StubDecisionLease_BlocksUpdateUntilTheChildCanLaunch() {
        Publish(manifest: Manifest());
        using (StubInstall.AcquireUpdateLease(installRoot: Install)) {
            var blocked = await Service(stager: new RefusingStager()).ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(condition: blocked.Applied);
            Assert.Contains(expectedSubstring: "busy", actualString: blocked.Detail);
        }
        var result = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(condition: result.Applied, userMessage: result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StubRollback_CancelsOnlyTheRejectedVersionsIntent(bool newerPending) {
        Directory.CreateDirectory(path: Install);
        File.WriteAllText(path: Current, contents: "1.0.0");
        Publish(manifest: Manifest());
        var first = await Service(applier: newerPending ? null : new InterruptedApplier(boundary: "after-pointer"))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected: newerPending, actual: first.Applied);
        if (newerPending) {
            Publish(manifest: Manifest(version: "1.0.2", sequence: 2));
            Assert.False(condition: (await Service(applier: new InterruptedApplier(boundary: "before-pointer"))
                .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        }
        Assert.True(condition: File.Exists(path: Intent));
        using (StubInstall.AcquireUpdateLease(installRoot: Install)) {
            StubInstall.RevertToLastGood(installRoot: Install, failedVersion: "1.0.1", lastGoodVersion: "1.0.0");
        }
        Assert.Equal(expected: newerPending, actual: File.Exists(path: Intent));
        Assert.Equal(expected: "1.0.0", actual: File.ReadAllText(path: Current));
        var resumed = await Service().ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(expected: newerPending, actual: resumed.Applied);
        Assert.Equal(expected: newerPending ? "1.0.2" : "1.0.0", actual: File.ReadAllText(path: Current));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("{\"manifest\":null}")]
    [InlineData("{\"manifest\":{\"version\":1}}")]
    public void StubRollback_RefusesMalformedIntentBeforeChangingCurrent(string content) {
        Directory.CreateDirectory(path: Install);
        File.WriteAllText(path: Current, contents: "1.0.1");
        File.WriteAllText(path: Intent, contents: content);
        using (StubInstall.AcquireUpdateLease(installRoot: Install)) {
            Assert.ThrowsAny<System.Text.Json.JsonException>(testCode: () =>
                StubInstall.RevertToLastGood(installRoot: Install, failedVersion: "1.0.1", lastGoodVersion: "1.0.0"));
        }
        Assert.Equal(expected: "1.0.1", actual: File.ReadAllText(path: Current));
        Assert.Equal(expected: content, actual: File.ReadAllText(path: Intent));
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
        Directory.CreateDirectory(path: Install);
        File.WriteAllText(path: Current, contents: "1.0.1");
        var manifest = m_fixture.Sign(
            document: new ReleaseManifest(App: "puck.world", Channel: "stable", MinimumSupported: "1.0.0", Notes: null,
                Payloads: [new ReleasePayload(Rid: UpdateService.CurrentRid(), Files: [new ReleasePayloadFile(Path: "a.dll", Hash: ContentPin.Compute(content: Payload).ToString(), Size: Payload.Length)])],
                Revoked: null, Rollout: new ReleaseRollout(Percent: 100), Schema: ReleaseManifest.CurrentSchema,
                Signature: null, StateGeneration: 1, Version: "1.0.2"),
            notBefore: ReleaseChainFixture.Epoch, notAfter: ReleaseChainFixture.Epoch + 3600, sequence: 1);
        Publish(manifest: manifest);
        Assert.False(condition: (await Service(installedVersion: "1.0.1", applier: new InterruptedApplier(boundary: "before-pointer"))
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken)).Applied);
        Assert.True(condition: File.Exists(path: Intent));

        // Simulate stub rollback of 1.0.1 back to last-good 0.8.0 while preserving newer pending intent 1.0.2.
        using (StubInstall.AcquireUpdateLease(installRoot: Install)) {
            StubInstall.RevertToLastGood(installRoot: Install, failedVersion: "1.0.1", lastGoodVersion: "0.8.0");
        }

        var recovered = await Service(installedVersion: "0.8.0", stager: new RefusingStager())
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: recovered.Applied);
        Assert.Contains(expectedSubstring: "minimumSupported", actualString: recovered.Detail);
        Assert.Equal(expected: "0.8.0", actual: File.ReadAllText(path: Current));
    }

    [Fact]
    public async Task Apply_RefusesCaseInsensitiveVersionCollisionWithActiveInstall() {
        Directory.CreateDirectory(path: Install);
        File.WriteAllText(path: Current, contents: "1.0.1-Alpha");
        Publish(manifest: Manifest(version: "1.0.1-alpha", sequence: 1));
        var outcome = await Service(installedVersion: "1.0.0", stager: new RefusingStager())
            .ApplyAsync(now: Now, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(condition: outcome.Applied);
        Assert.Contains(expectedSubstring: "collides", actualString: outcome.Detail, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InterruptedVerifier(IReleaseVerifier inner, string boundary) : IReleaseVerifier {
        public ReleaseVerifyOutcome Verify(ReleaseManifest manifest, DateTimeOffset now, string installedVersion, bool advanceSequence) =>
            inner.Verify(manifest: manifest, now: now, installedVersion: installedVersion, advanceSequence: advanceSequence);
        public ReleaseVerifyOutcome ReserveInstall(ReleaseManifest manifest, DateTimeOffset verifiedAt, string installedVersion) {
            if (boundary == "before-reservation") { throw new IOException(message: "injected interruption before reservation"); }
            var result = inner.ReserveInstall(manifest: manifest, verifiedAt: verifiedAt, installedVersion: installedVersion);
            if (boundary == "after-reservation") { throw new IOException(message: "injected interruption after reservation"); }
            return result;
        }
    }

    private sealed class InterruptedApplier(string boundary) : IUpdateApplier {
        public UpdateApplyResult Apply(ReleaseManifest manifest, string rid, string cacheRoot, bool recoverInterruptedApply = false) {
            if (boundary == "before-pointer") { throw new IOException(message: "injected interruption before pointer swap"); }
            var result = new FileUpdateApplier().Apply(manifest: manifest, rid: rid, cacheRoot: cacheRoot, recoverInterruptedApply: recoverInterruptedApply);
            if (boundary == "after-pointer") { throw new IOException(message: "injected interruption after pointer swap"); }
            return result;
        }
    }

    private sealed class RefusingStager : IUpdateStager {
        public Task<UpdateStageResult> StageAsync(ReleaseManifest manifest, string rid, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message: "staging must not run on this path");
    }

    private sealed class InterruptedStager(IUpdateStager inner) : IUpdateStager {
        public async Task<UpdateStageResult> StageAsync(ReleaseManifest manifest, string rid, CancellationToken cancellationToken) {
            var result = await inner.StageAsync(manifest: manifest, rid: rid, cancellationToken: cancellationToken);
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

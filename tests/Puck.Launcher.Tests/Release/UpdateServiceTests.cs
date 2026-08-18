using Puck.Assets;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>End-to-end laws over <see cref="UpdateService.CheckAsync"/> against a <see cref="DirectoryReleaseSource"/>
/// — the exact transport a <c>self-update</c> canary leg would point at.</summary>
public sealed class UpdateServiceTests : IDisposable {
    private readonly string m_root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-launcher-update-service-tests-{Guid.NewGuid():n}");

    public void Dispose() {
        if (Directory.Exists(path: m_root)) {
            Directory.Delete(path: m_root, recursive: true);
        }
    }

    private void PublishManifest(ReleaseManifest signedManifest, string channel) {
        var directory = Path.Combine(path1: m_root, path2: channel);

        _ = Directory.CreateDirectory(path: directory);
        File.WriteAllBytes(path: Path.Combine(path1: directory, path2: "manifest.json"), bytes: ReleaseChainFixture.ToWireBytes(manifest: signedManifest));
    }
    private static ReleaseManifest UnsignedDocument(string version, int rolloutPercent) => new(
        App: "puck.world",
        Channel: "stable",
        MinimumSupported: null,
        Notes: null,
        Payloads: [new ReleasePayload(Rid: "win-x64", Files: [new ReleasePayloadFile(Path: "a.dll", Hash: $"sha256/{new string(c: '0', count: 64)}", Size: 1)])],
        Revoked: null,
        Rollout: new ReleaseRollout(Percent: rolloutPercent),
        Schema: ReleaseManifest.CurrentSchema,
        Signature: null,
        StateGeneration: 1,
        Version: version
    );

    [Fact]
    public async Task CheckAsync_ReportsAvailable_ForAVerifiedNewerManifestInsideRollout() {
        var fixture = new ReleaseChainFixture();
        var signed = fixture.Sign(document: UnsignedDocument(rolloutPercent: 100, version: "1.0.1"), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1);

        PublishManifest(channel: "stable", signedManifest: signed);

        var options = new UpdateOptions(
            App: "puck.world",
            CacheRoot: m_root,
            Channel: "stable",
            InstalledVersion: "1.0.0",
            TrustAnchor: fixture.TrustAnchor
        );
        var verifier = new AttestationReleaseVerifier(codec: fixture.Codec, sequenceStore: new InMemoryReleaseSequenceStore(), trustList: fixture.BuildTrustList(replayHorizon: TimeSpan.FromDays(days: 30)));
        var service = new UpdateService(
            applier: new FileUpdateApplier(),
            options: options,
            source: new DirectoryReleaseSource(root: m_root),
            stager: new ContentAddressedUpdateStager(cache: new ContentAddressedStore(root: Path.Combine(path1: m_root, path2: "objects")), cacheRoot: m_root, source: new DirectoryReleaseSource(root: m_root)),
            verifier: verifier
        );

        var result = await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.Equal(expected: UpdateCheckOutcome.Available, actual: result.Outcome);
        Assert.NotNull(@object: result.Manifest);
        Assert.Equal(expected: "1.0.1", actual: result.Manifest!.Version);
    }
    [Fact]
    public async Task CheckAsync_ReportsOutsideRollout_WhenBucketExcludesThisInstall() {
        var fixture = new ReleaseChainFixture();
        var signed = fixture.Sign(document: UnsignedDocument(rolloutPercent: 0, version: "1.0.1"), notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1);

        PublishManifest(channel: "stable", signedManifest: signed);

        var options = new UpdateOptions(
            App: "puck.world",
            CacheRoot: m_root,
            Channel: "stable",
            InstalledVersion: "1.0.0",
            TrustAnchor: fixture.TrustAnchor
        );
        var verifier = new AttestationReleaseVerifier(codec: fixture.Codec, sequenceStore: new InMemoryReleaseSequenceStore(), trustList: fixture.BuildTrustList(replayHorizon: TimeSpan.FromDays(days: 30)));
        var service = new UpdateService(
            applier: new FileUpdateApplier(),
            options: options,
            source: new DirectoryReleaseSource(root: m_root),
            stager: new ContentAddressedUpdateStager(cache: new ContentAddressedStore(root: Path.Combine(path1: m_root, path2: "objects")), cacheRoot: m_root, source: new DirectoryReleaseSource(root: m_root)),
            verifier: verifier
        );

        var result = await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.Equal(expected: UpdateCheckOutcome.OutsideRollout, actual: result.Outcome);
    }
    [Fact]
    public async Task CheckAsync_ReportsUpToDate_WhenNoManifestPublished() {
        var fixture = new ReleaseChainFixture();
        var options = new UpdateOptions(
            App: "puck.world",
            CacheRoot: m_root,
            Channel: "stable",
            InstalledVersion: "1.0.0",
            TrustAnchor: fixture.TrustAnchor
        );
        var verifier = new AttestationReleaseVerifier(codec: fixture.Codec, sequenceStore: new InMemoryReleaseSequenceStore(), trustList: fixture.BuildTrustList(replayHorizon: TimeSpan.FromDays(days: 30)));
        var service = new UpdateService(
            applier: new FileUpdateApplier(),
            options: options,
            source: new DirectoryReleaseSource(root: m_root),
            stager: new ContentAddressedUpdateStager(cache: new ContentAddressedStore(root: Path.Combine(path1: m_root, path2: "objects")), cacheRoot: m_root, source: new DirectoryReleaseSource(root: m_root)),
            verifier: verifier
        );

        var result = await service.CheckAsync(cancellationToken: TestContext.Current.CancellationToken, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.Equal(expected: UpdateCheckOutcome.UpToDate, actual: result.Outcome);
    }
}

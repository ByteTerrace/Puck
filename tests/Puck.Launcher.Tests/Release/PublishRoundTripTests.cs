using Puck.Assets;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>An unsigned <c>puck publish</c>-shaped release-source tree, signed by a throwaway chain the same way
/// the <c>self-update</c> canary mints one, round-trips through <see cref="DirectoryReleaseSource"/> and
/// <see cref="AttestationReleaseVerifier"/> — the dry-run verb itself never signs (see
/// <c>Puck.Cli.Publish.PublishCommand</c>).</summary>
public sealed class PublishRoundTripTests : IDisposable {
    private readonly string m_root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-launcher-publish-tests-{Guid.NewGuid():n}");

    public void Dispose() {
        if (Directory.Exists(path: m_root)) {
            Directory.Delete(path: m_root, recursive: true);
        }
    }
    [Fact]
    public async Task UnsignedDryRunTree_VerifiesOnceSigned_ByAThrowawayChain() {
        // The exact walk PublishCommand performs: hash each payload file into the same objects/ tree
        // DirectoryReleaseSource reads.
        var objectsStore = new ContentAddressedStore(root: m_root);
        var fileBytes = "puck.world payload byte content"u8.ToArray();
        var hash = objectsStore.Put(content: fileBytes);
        var unsigned = new ReleaseManifest(
            App: "puck.world",
            Channel: "stable",
            MinimumSupported: null,
            Notes: null,
            Payloads: [new ReleasePayload(Files: [new ReleasePayloadFile(Hash: hash, Path: "Puck.World.exe", Size: fileBytes.Length)], Rid: "win-x64")],
            Revoked: null,
            Rollout: new ReleaseRollout(Percent: 100),
            Schema: ReleaseManifest.CurrentSchema,
            Signature: null,
            StateGeneration: 1,
            Version: "1.0.1"
        );
        var fixture = new ReleaseChainFixture();
        var signed = fixture.Sign(document: unsigned, notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1);
        var channelDirectory = Path.Combine(path1: m_root, path2: "stable");

        Directory.CreateDirectory(path: channelDirectory);
        File.WriteAllBytes(path: Path.Combine(path1: channelDirectory, path2: "manifest.json"), bytes: ReleaseChainFixture.ToWireBytes(manifest: signed));

        var source = new DirectoryReleaseSource(root: m_root);
        var fetch = await source.TryGetLatestManifestAsync(cancellationToken: TestContext.Current.CancellationToken, channel: "stable");

        Assert.True(condition: fetch.Found, userMessage: fetch.RefusalReason);

        var parsed = System.Text.Json.JsonSerializer.Deserialize<ReleaseManifest>(utf8Json: fetch.ManifestBytes, options: Puck.Assets.Documents.DocumentJsonOptions.Shared)!;
        var verifier = new AttestationReleaseVerifier(codec: fixture.Codec, sequenceStore: new InMemoryReleaseSequenceStore(), trustList: fixture.BuildTrustList(replayHorizon: TimeSpan.FromDays(days: 30)));
        var verified = verifier.Verify(advanceSequence: true, installedVersion: "1.0.0", manifest: parsed, now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch));

        Assert.True(condition: verified.Accepted, userMessage: verified.RefusalReason);

        using var destination = new MemoryStream();
        var found = await source.TryGetFileAsync(cancellationToken: TestContext.Current.CancellationToken, destination: destination, hash: hash);

        Assert.True(condition: found);
        Assert.Equal(expected: fileBytes, actual: destination.ToArray());
    }
}

using Puck.Assets;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>An unsigned <c>puck publish</c>-shaped release-source tree, signed by a throwaway chain the same way
/// the <c>self-update</c> canary mints one, round-trips through <see cref="DirectoryReleaseSource"/> and
/// <see cref="AttestationReleaseVerifier"/> — the dry-run verb itself never signs (see
/// <c>Puck.Cli.Publish.PublishCommand</c>).</summary>
public sealed class PublishRoundTripTests : IDisposable {
    private readonly TempStagingRoot m_root = new();

    public void Dispose() => m_root.Dispose();
    // A published tree's executable files keep their mode through the signed manifest and the stager: the flag is read
    // from the tree, the signature covers it, a manifest without it does not parse, and a staged file takes its mode.
    [Fact]
    public async Task PublishedExecutable_IsSigned_AndStagedExecutable() {
        var input = Path.Combine(path1: m_root.RootPath, path2: "built");
        var tool = Path.Combine(path1: input, path2: "tls/caddy");
        var library = Path.Combine(path1: input, path2: "app.dll");

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: tool)!);
        File.WriteAllBytes(bytes: [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1], path: tool);
        File.WriteAllBytes(bytes: "library"u8.ToArray(), path: library);
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, path: tool);
            File.SetUnixFileMode(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite, path: library);
        }
        var fixture = new ReleaseChainFixture();
        var sourceRoot = Path.Combine(path1: m_root.RootPath, path2: "channel");
        var source = new DirectoryReleaseSource(root: sourceRoot);

        _ = source.Publish(input, UpdateService.CurrentRid(), new ReleaseManifest(App: "puck.world", Channel: "stable", MinimumSupported: null, Notes: null, Payloads: [],
            Revoked: null, Rollout: new ReleaseRollout(Percent: 100), Schema: ReleaseManifest.CurrentSchema, Signature: null, StateGeneration: 1, Version: "1.0.1"),
            sign: manifest => fixture.Sign(document: manifest, notAfter: (ReleaseChainFixture.Epoch + 3600), notBefore: ReleaseChainFixture.Epoch, sequence: 1));
        var bytes = (await source.TryGetLatestManifestAsync(cancellationToken: TestContext.Current.CancellationToken, channel: "stable")).ManifestBytes;
        var parsed = System.Text.Json.JsonSerializer.Deserialize<ReleaseManifest>(options: Puck.Assets.Documents.DocumentJsonOptions.Shared, utf8Json: bytes)!;
        var files = parsed.Payloads[0].Files.ToDictionary(keySelector: static file => file.Path);

        Assert.True(condition: files["tls/caddy"].Executable);
        Assert.False(condition: files["app.dll"].Executable);

        AttestationReleaseVerifier Verifier() => new(codec: fixture.Codec, sequenceStore: new InMemoryReleaseSequenceStore(),
            trustList: fixture.BuildTrustList(replayHorizon: TimeSpan.FromDays(days: 30)));
        var now = DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch);
        var tampered = parsed with { Payloads = [parsed.Payloads[0] with { Files = [.. parsed.Payloads[0].Files.Select(selector: static file => file with { Executable = true })] }] };
        var refused = Verifier().Verify(advanceSequence: false, installedVersion: "1.0.0", manifest: tampered, now: now);

        Assert.False(condition: refused.Accepted);
        Assert.True(condition: Verifier().Verify(advanceSequence: false, installedVersion: "1.0.0", manifest: parsed, now: now).Accepted);

        var json = System.Text.Json.Nodes.JsonNode.Parse(utf8Json: bytes)!;

        _ = json["payloads"]![0]!["files"]![0]!.AsObject().Remove(propertyName: "executable");
        _ = Assert.Throws<System.Text.Json.JsonException>(testCode: () => System.Text.Json.JsonSerializer.Deserialize<ReleaseManifest>(
            json: json.ToJsonString(), options: Puck.Assets.Documents.DocumentJsonOptions.Shared));

        var installRoot = Path.Combine(path1: m_root.RootPath, path2: "install");
        var stager = new ContentAddressedUpdateStager(cache: new ContentAddressedStore(root: Path.Combine(path1: installRoot, path2: "objects")), cacheRoot: installRoot, source: source);
        var staged = await stager.StageAsync(cancellationToken: TestContext.Current.CancellationToken, manifest: parsed, rid: UpdateService.CurrentRid());

        Assert.True(condition: staged.Staged, userMessage: staged.RefusalReason);
        foreach (var file in parsed.Payloads[0].Files) {
            Assert.True(condition: ReleaseFileMode.Matches(file: file, path: Path.Combine(path1: staged.StagedPath!, path2: file.Path)), userMessage: file.Path);
        }
    }
    [Fact]
    public async Task UnsignedDryRunTree_VerifiesOnceSigned_ByAThrowawayChain() {
        // The exact walk PublishCommand performs: hash each payload file into the same objects/ tree
        // DirectoryReleaseSource reads.
        var objectsStore = new ContentAddressedStore(root: m_root.RootPath);
        var fileBytes = "puck.world payload byte content"u8.ToArray();
        var hash = objectsStore.Put(content: fileBytes);
        var unsigned = new ReleaseManifest(
            App: "puck.world",
            Channel: "stable",
            MinimumSupported: null,
            Notes: null,
            Payloads: [new ReleasePayload(
                    Files: [new ReleasePayloadFile(
                            Hash: hash.ToString(),
                            Path: "Puck.World.exe",
                            Size: fileBytes.Length, Executable: false
                        )],
                    Rid: "win-x64"
                )],
            Revoked: null,
            Rollout: new ReleaseRollout(Percent: 100),
            Schema: ReleaseManifest.CurrentSchema,
            Signature: null,
            StateGeneration: 1,
            Version: "1.0.1"
        );
        var fixture = new ReleaseChainFixture();
        var signed = fixture.Sign(
            document: unsigned,
            notAfter: (ReleaseChainFixture.Epoch + 3600),
            notBefore: ReleaseChainFixture.Epoch,
            sequence: 1
        );
        var channelDirectory = Path.Combine(
            path1: m_root.RootPath,
            path2: "stable"
        );

        Directory.CreateDirectory(path: channelDirectory);
        File.WriteAllBytes(
            path: Path.Combine(
                path1: channelDirectory,
                path2: "manifest.json"
            ),
            bytes: ReleaseChainFixture.ToWireBytes(manifest: signed)
        );

        var source = new DirectoryReleaseSource(root: m_root.RootPath);
        var fetch = await source.TryGetLatestManifestAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            channel: "stable"
        );

        Assert.True(
            condition: fetch.Found,
            userMessage: fetch.RefusalReason
        );

        var parsed = System.Text.Json.JsonSerializer.Deserialize<ReleaseManifest>(
            utf8Json: fetch.ManifestBytes,
            options: Puck.Assets.Documents.DocumentJsonOptions.Shared
        )!;
        var verifier = new AttestationReleaseVerifier(
            codec: fixture.Codec,
            sequenceStore: new InMemoryReleaseSequenceStore(),
            trustList: fixture.BuildTrustList(replayHorizon: TimeSpan.FromDays(days: 30))
        );
        var verified = verifier.Verify(
            advanceSequence: true,
            installedVersion: "1.0.0",
            manifest: parsed,
            now: DateTimeOffset.FromUnixTimeSeconds(seconds: ReleaseChainFixture.Epoch)
        );

        Assert.True(
            condition: verified.Accepted,
            userMessage: verified.RefusalReason
        );

        using var destination = new MemoryStream();
        var found = await source.TryGetFileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            destination: destination,
            length: fileBytes.Length,
            pin: hash
        );

        Assert.True(condition: found);
        Assert.Equal(
            expected: fileBytes,
            actual: destination.ToArray()
        );
    }
}

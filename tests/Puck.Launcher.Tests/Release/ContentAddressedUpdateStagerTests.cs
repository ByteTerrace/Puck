using Puck.Assets;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over <see cref="ContentAddressedUpdateStager"/>: hash re-verification refuses a bad file, and a
/// file already in the content-addressed cache is never re-fetched.</summary>
public sealed class ContentAddressedUpdateStagerTests : IDisposable {
    private readonly TempStagingRoot m_root = new();

    private static ReleaseManifest ManifestFor(string version, ReleasePayloadFile file) => new(
        App: "puck.world",
        Channel: "stable",
        MinimumSupported: null,
        Notes: null,
        Payloads: [new ReleasePayload(
                Rid: "win-x64",
                Files: [file]
            )],
        Revoked: null,
        Rollout: new ReleaseRollout(Percent: 100),
        Schema: ReleaseManifest.CurrentSchema,
        Signature: null,
        StateGeneration: 1,
        Version: version
    );

    public void Dispose() => m_root.Dispose();
    [Fact]
    public async Task StageAsync_NeverRefetches_AnAlreadyCachedFile() {
        var content = "shared bytes"u8.ToArray();
        var hash = ContentPin.Compute(content: content).ToString();
        var source = new RecordingReleaseSource(files: new Dictionary<string, byte[]> { [hash] = content });
        var cache = new ContentAddressedStore(root: Path.Combine(
            path1: m_root.RootPath,
            path2: "objects"
        ));
        var stager = new ContentAddressedUpdateStager(
            cache: cache,
            cacheRoot: m_root.RootPath,
            source: source
        );
        var file = new ReleasePayloadFile(
            Path: "shared.dll",
            Hash: hash,
            Size: content.Length
        );

        var first = await stager.StageAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            manifest: ManifestFor(
                file: file,
                version: "1.0.0"
            ),
            rid: "win-x64"
        );

        Assert.True(
            condition: first.Staged,
            userMessage: first.RefusalReason
        );
        Assert.Equal(
            expected: 1,
            actual: first.FilesDownloaded
        );
        Assert.Single(collection: source.Requested);

        var second = await stager.StageAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            manifest: ManifestFor(
                file: file,
                version: "1.0.1"
            ),
            rid: "win-x64"
        );

        Assert.True(
            condition: second.Staged,
            userMessage: second.RefusalReason
        );
        Assert.Equal(
            expected: 0,
            actual: second.FilesDownloaded
        );
        Assert.Equal(
            expected: 1,
            actual: second.FilesReused
        );
        // Still exactly one request total — the second version's shared-hash file was never re-fetched.
        Assert.Single(collection: source.Requested);
        Assert.Equal(
            expected: content,
            actual: await File.ReadAllBytesAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                path: Path.Combine(
                    path1: second.StagedPath!,
                    path2: "shared.dll"
                )
            )
        );
    }
    [Fact]
    public async Task StageAsync_Refuses_BadFileHash() {
        var content = "a"u8.ToArray();
        var wrongHash = $"sha256/{new string(
            c: '0',
            count: 64
        )}";
        var source = new RecordingReleaseSource(files: new Dictionary<string, byte[]> { [wrongHash] = content });
        var cache = new ContentAddressedStore(root: Path.Combine(
            path1: m_root.RootPath,
            path2: "objects"
        ));
        var stager = new ContentAddressedUpdateStager(
            cache: cache,
            cacheRoot: m_root.RootPath,
            source: source
        );
        var manifest = ManifestFor(
            version: "1.0.0",
            file: new ReleasePayloadFile(
                Path: "a.dll",
                Hash: wrongHash,
                Size: content.Length
            )
        );

        var result = await stager.StageAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            manifest: manifest,
            rid: "win-x64"
        );

        Assert.False(condition: result.Staged);
        Assert.Contains(
            expectedSubstring: "hash",
            actualString: result.RefusalReason!,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task StageAsync_RefusesEscapingPathsBeforeFetchingOrWriting() {
        var content = "untrusted bytes"u8.ToArray();
        var hash = ContentPin.Compute(content: content).ToString();
        var source = new RecordingReleaseSource(files: new Dictionary<string, byte[]> { [hash] = content });
        var stager = new ContentAddressedUpdateStager(
            source: source,
            cache: new ContentAddressedStore(root: Path.Combine(path1: m_root.RootPath, path2: "objects")),
            cacheRoot: m_root.RootPath
        );
        var escapedFile = Path.Combine(path1: m_root.RootPath, path2: "escaped.dll");
        var file = new ReleasePayloadFile(Path: "a.dll", Hash: hash, Size: content.Length);
        var candidates = new[] {
            ManifestFor(version: "2.0.0+/../../escaped", file: file),
            ManifestFor(version: "2.0.0", file: file with { Path = escapedFile }),
        };

        foreach (var manifest in candidates) {
            var result = await stager.StageAsync(manifest: manifest, rid: "win-x64", cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(condition: result.Staged);
        }

        Assert.Empty(collection: source.Requested);
        Assert.False(condition: File.Exists(path: escapedFile));
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: m_root.RootPath, path2: "escaped")));
    }

    private sealed class RecordingReleaseSource(IReadOnlyDictionary<string, byte[]> files) : IReleaseSource {
        public readonly List<string> Requested = [];

        public Task<bool> TryGetFileAsync(ContentPin pin, Stream destination, long length, CancellationToken cancellationToken) {
            Requested.Add(item: pin.ToString());

            if (!files.TryGetValue(
                key: pin.ToString(),
                value: out var bytes
            )) {
                return Task.FromResult(result: false);
            }

            destination.Write(buffer: bytes);

            return Task.FromResult(result: true);
        }
        public Task<ReleaseSourceResult> TryGetLatestManifestAsync(string channel, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

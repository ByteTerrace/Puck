using Puck.Assets;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over <see cref="FileUpdateApplier"/>: hash re-verification, the atomic <c>current</c> pointer, and
/// <c>last-good</c> retention.</summary>
public sealed class FileUpdateApplierTests : IDisposable {
    private readonly string m_root = Path.Combine(path1: Path.GetTempPath(), path2: $"puck-launcher-applier-tests-{Guid.NewGuid():n}");

    public void Dispose() {
        if (Directory.Exists(path: m_root)) {
            Directory.Delete(path: m_root, recursive: true);
        }
    }

    private static ReleaseManifest ManifestFor(string version, int stateGeneration, ReleasePayloadFile file) => new(
        App: "puck.world",
        Channel: "stable",
        MinimumSupported: null,
        Notes: null,
        Payloads: [new ReleasePayload(Rid: "win-x64", Files: [file])],
        Revoked: null,
        Rollout: new ReleaseRollout(Percent: 100),
        Schema: ReleaseManifest.CurrentSchema,
        Signature: null,
        StateGeneration: stateGeneration,
        Version: version
    );
    private void StageFile(string version, string relativePath, byte[] content) {
        var stagedPath = Path.Combine(path1: m_root, path2: "versions", path3: version, path4: relativePath);

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: stagedPath)!);
        File.WriteAllBytes(bytes: content, path: stagedPath);
    }

    [Fact]
    public void Apply_Refuses_WhenTheVersionWasNeverStaged() {
        var applier = new FileUpdateApplier();
        var manifest = ManifestFor(version: "1.0.1", stateGeneration: 1, file: new ReleasePayloadFile(Path: "a.dll", Hash: $"sha256/{new string(c: '0', count: 64)}", Size: 1));

        var result = applier.Apply(cacheRoot: m_root, manifest: manifest, rid: "win-x64");

        Assert.False(condition: result.Applied);
        Assert.Contains(expectedSubstring: "not staged", actualString: result.RefusalReason!, comparisonType: StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Apply_Refuses_WhenAStagedFileWasTamperedAfterStaging() {
        var content = "original"u8.ToArray();
        var hash = $"sha256/{ContentAddressedStore.ComputeHash(content: content)}";

        StageFile(version: "1.0.1", relativePath: "a.dll", content: "tampered"u8.ToArray());

        var applier = new FileUpdateApplier();
        var manifest = ManifestFor(version: "1.0.1", stateGeneration: 1, file: new ReleasePayloadFile(Path: "a.dll", Hash: hash, Size: content.Length));

        var result = applier.Apply(cacheRoot: m_root, manifest: manifest, rid: "win-x64");

        Assert.False(condition: result.Applied);
        Assert.Contains(expectedSubstring: "hash", actualString: result.RefusalReason!, comparisonType: StringComparison.OrdinalIgnoreCase);
        Assert.False(condition: File.Exists(path: Path.Combine(path1: m_root, path2: "current")));
    }
    [Fact]
    public void Apply_WritesCurrentAndStateGeneration_OnAFirstInstall_WithNoLastGood() {
        var content = "bytes"u8.ToArray();
        var hash = $"sha256/{ContentAddressedStore.ComputeHash(content: content)}";

        StageFile(content: content, relativePath: "a.dll", version: "1.0.0");

        var applier = new FileUpdateApplier();
        var manifest = ManifestFor(version: "1.0.0", stateGeneration: 1, file: new ReleasePayloadFile(Path: "a.dll", Hash: hash, Size: content.Length));

        var result = applier.Apply(cacheRoot: m_root, manifest: manifest, rid: "win-x64");

        Assert.True(condition: result.Applied, userMessage: result.RefusalReason);
        Assert.Null(@object: result.PreviousVersion);
        Assert.Equal(expected: "1.0.0", actual: File.ReadAllText(path: Path.Combine(path1: m_root, path2: "current")).Trim());
        Assert.Equal(expected: "1", actual: File.ReadAllText(path: Path.Combine(path1: m_root, path2: "versions", path3: "1.0.0", path4: "state-generation")).Trim());
        Assert.False(condition: File.Exists(path: Path.Combine(path1: m_root, path2: "last-good")));
    }
    [Fact]
    public void Apply_RetainsThePreviousCurrentAsLastGood_OnASecondApply() {
        var firstContent = "v1"u8.ToArray();
        var firstHash = $"sha256/{ContentAddressedStore.ComputeHash(content: firstContent)}";
        var secondContent = "v2"u8.ToArray();
        var secondHash = $"sha256/{ContentAddressedStore.ComputeHash(content: secondContent)}";

        StageFile(content: firstContent, relativePath: "a.dll", version: "1.0.0");
        StageFile(content: secondContent, relativePath: "a.dll", version: "1.0.1");

        var applier = new FileUpdateApplier();

        var first = applier.Apply(cacheRoot: m_root, manifest: ManifestFor(version: "1.0.0", stateGeneration: 1, file: new ReleasePayloadFile(Path: "a.dll", Hash: firstHash, Size: firstContent.Length)), rid: "win-x64");

        Assert.True(condition: first.Applied, userMessage: first.RefusalReason);

        var second = applier.Apply(cacheRoot: m_root, manifest: ManifestFor(version: "1.0.1", stateGeneration: 2, file: new ReleasePayloadFile(Path: "a.dll", Hash: secondHash, Size: secondContent.Length)), rid: "win-x64");

        Assert.True(condition: second.Applied, userMessage: second.RefusalReason);
        Assert.Equal(expected: "1.0.0", actual: second.PreviousVersion);
        Assert.Equal(expected: "1.0.1", actual: File.ReadAllText(path: Path.Combine(path1: m_root, path2: "current")).Trim());
        Assert.Equal(expected: "1.0.0", actual: File.ReadAllText(path: Path.Combine(path1: m_root, path2: "last-good")).Trim());
    }
}

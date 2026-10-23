using Puck.Assets;
using Puck.Launcher.Release;
using Xunit;

namespace Puck.Launcher.Tests.Release;

/// <summary>Laws over <see cref="FileUpdateApplier"/>: hash re-verification, the atomic <c>current</c> pointer, and
/// <c>last-good</c> retention.</summary>
public sealed class FileUpdateApplierTests : IDisposable {
    private readonly TempStagingRoot m_root = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_RefusesCaseAliasOfAnActiveDirectory(bool lastGood) {
        var content = "verified bytes"u8.ToArray();
        var file = new ReleasePayloadFile(Path: "a.dll", Hash: ContentPin.Compute(content: content).ToString(), Size: content.Length);
        StageFile(version: "1.0.1-alpha", relativePath: file.Path, content: content);
        File.WriteAllText(path: Path.Combine(m_root.RootPath, "current"), contents: lastGood ? "1.0.0" : "1.0.1-Alpha");
        if (lastGood) {
            File.WriteAllText(path: Path.Combine(m_root.RootPath, "last-good"), contents: "1.0.1-Alpha");
        }
        var result = new FileUpdateApplier().Apply(manifest: ManifestFor(version: "1.0.1-alpha", stateGeneration: 1, file: file), rid: "win-x64", cacheRoot: m_root.RootPath);
        Assert.False(condition: result.Applied);
        Assert.Contains(expectedSubstring: "collides", actualString: result.RefusalReason!);
    }

    [Fact]
    public void Apply_RefusesDowngradeAndReapplyAgainstTheCurrentPointer() {
        var content = "verified bytes"u8.ToArray();
        var file = new ReleasePayloadFile(Path: "a.dll", Hash: ContentPin.Compute(content: content).ToString(), Size: content.Length);
        StageFile(version: "1.0.2", relativePath: file.Path, content: content);
        StageFile(version: "1.0.3", relativePath: file.Path, content: content);
        var applier = new FileUpdateApplier();

        Assert.True(condition: applier.Apply(manifest: ManifestFor(version: "1.0.3", stateGeneration: 1, file: file), rid: "win-x64", cacheRoot: m_root.RootPath).Applied);
        Assert.False(condition: applier.Apply(manifest: ManifestFor(version: "1.0.2", stateGeneration: 1, file: file), rid: "win-x64", cacheRoot: m_root.RootPath).Applied);
        Assert.False(condition: applier.Apply(manifest: ManifestFor(version: "1.0.3", stateGeneration: 1, file: file), rid: "win-x64", cacheRoot: m_root.RootPath).Applied);
        Assert.Equal(expected: "1.0.3", actual: File.ReadAllText(path: Path.Combine(path1: m_root.RootPath, path2: "current")));
        Assert.False(condition: File.Exists(path: Path.Combine(path1: m_root.RootPath, path2: "last-good")));
    }

    private static ReleaseManifest ManifestFor(string version, int stateGeneration, ReleasePayloadFile file) => new(
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
        StateGeneration: stateGeneration,
        Version: version
    );
    private void StageFile(string version, string relativePath, byte[] content) {
        var stagedPath = Path.Combine(
            path1: m_root.RootPath,
            path2: "versions",
            path3: version,
            path4: relativePath
        );

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: stagedPath)!);
        File.WriteAllBytes(
            bytes: content,
            path: stagedPath
        );
    }

    [Fact]
    public void Apply_Refuses_WhenAStagedFileWasTamperedAfterStaging() {
        var content = "original"u8.ToArray();
        var hash = ContentPin.Compute(content: content).ToString();

        StageFile(
            version: "1.0.1",
            relativePath: "a.dll",
            content: "tampered"u8.ToArray()
        );

        var applier = new FileUpdateApplier();
        var manifest = ManifestFor(
            version: "1.0.1",
            stateGeneration: 1,
            file: new ReleasePayloadFile(
                Path: "a.dll",
                Hash: hash,
                Size: content.Length
            )
        );

        var result = applier.Apply(
            cacheRoot: m_root.RootPath,
            manifest: manifest,
            rid: "win-x64"
        );

        Assert.False(condition: result.Applied);
        Assert.Contains(
            expectedSubstring: "hash",
            actualString: result.RefusalReason!,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
        Assert.False(condition: File.Exists(path: Path.Combine(
            path1: m_root.RootPath,
            path2: "current"
        )));
    }
    [Fact]
    public void Apply_Refuses_WhenTheVersionWasNeverStaged() {
        var applier = new FileUpdateApplier();
        var manifest = ManifestFor(
            version: "1.0.1",
            stateGeneration: 1,
            file: new ReleasePayloadFile(
                Path: "a.dll",
                Hash: $"sha256/{new string(
                    c: '0',
                    count: 64
                )}",
                Size: 1
            )
        );

        var result = applier.Apply(
            cacheRoot: m_root.RootPath,
            manifest: manifest,
            rid: "win-x64"
        );

        Assert.False(condition: result.Applied);
        Assert.Contains(
            expectedSubstring: "not staged",
            actualString: result.RefusalReason!,
            comparisonType: StringComparison.OrdinalIgnoreCase
        );
    }
    [Fact]
    public void Apply_RetainsThePreviousCurrentAsLastGood_OnASecondApply() {
        var firstContent = "v1"u8.ToArray();
        var firstHash = ContentPin.Compute(content: firstContent).ToString();
        var secondContent = "v2"u8.ToArray();
        var secondHash = ContentPin.Compute(content: secondContent).ToString();

        StageFile(
            content: firstContent,
            relativePath: "a.dll",
            version: "1.0.0"
        );
        StageFile(
            content: secondContent,
            relativePath: "a.dll",
            version: "1.0.1"
        );

        var applier = new FileUpdateApplier();

        var first = applier.Apply(
            cacheRoot: m_root.RootPath,
            manifest: ManifestFor(
                version: "1.0.0",
                stateGeneration: 1,
                file: new ReleasePayloadFile(
                    Path: "a.dll",
                    Hash: firstHash,
                    Size: firstContent.Length
                )
            ),
            rid: "win-x64"
        );

        Assert.True(
            condition: first.Applied,
            userMessage: first.RefusalReason
        );

        var second = applier.Apply(
            cacheRoot: m_root.RootPath,
            manifest: ManifestFor(
                version: "1.0.1",
                stateGeneration: 2,
                file: new ReleasePayloadFile(
                    Path: "a.dll",
                    Hash: secondHash,
                    Size: secondContent.Length
                )
            ),
            rid: "win-x64"
        );

        Assert.True(
            condition: second.Applied,
            userMessage: second.RefusalReason
        );
        Assert.Equal(
            expected: "1.0.0",
            actual: second.PreviousVersion
        );
        Assert.Equal(
            expected: "1.0.1",
            actual: File.ReadAllText(path: Path.Combine(
                path1: m_root.RootPath,
                path2: "current"
            )).Trim()
        );
        Assert.Equal(
            expected: "1.0.0",
            actual: File.ReadAllText(path: Path.Combine(
                path1: m_root.RootPath,
                path2: "last-good"
            )).Trim()
        );
    }
    [Fact]
    public void Apply_WritesCurrentAndStateGeneration_OnAFirstInstall_WithNoLastGood() {
        var content = "bytes"u8.ToArray();
        var hash = ContentPin.Compute(content: content).ToString();

        StageFile(
            content: content,
            relativePath: "a.dll",
            version: "1.0.0"
        );

        var applier = new FileUpdateApplier();
        var manifest = ManifestFor(
            version: "1.0.0",
            stateGeneration: 1,
            file: new ReleasePayloadFile(
                Path: "a.dll",
                Hash: hash,
                Size: content.Length
            )
        );

        var result = applier.Apply(
            cacheRoot: m_root.RootPath,
            manifest: manifest,
            rid: "win-x64"
        );

        Assert.True(
            condition: result.Applied,
            userMessage: result.RefusalReason
        );
        Assert.Null(@object: result.PreviousVersion);
        Assert.Equal(
            expected: "1.0.0",
            actual: File.ReadAllText(path: Path.Combine(
                path1: m_root.RootPath,
                path2: "current"
            )).Trim()
        );
        Assert.Equal(
            expected: "1",
            actual: File.ReadAllText(path: Path.Combine(
                path1: m_root.RootPath,
                path2: "versions",
                path3: "1.0.0",
                path4: "state-generation"
            )).Trim()
        );
        Assert.False(condition: File.Exists(path: Path.Combine(
            path1: m_root.RootPath,
            path2: "last-good"
        )));
    }
    public void Dispose() => m_root.Dispose();
}

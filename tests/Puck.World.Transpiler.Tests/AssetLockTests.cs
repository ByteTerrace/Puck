using System.Security.Cryptography;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Assets;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class AssetLockTests {
    [Fact]
    public void UpdateIsExplicitDeterministicAndResolvePinsExactBytes() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: "schema: \"puck.world.definition.v1\""u8, path: "world.puck");

        _ = directory.Write(bytes: "z"u8, path: "z.bin");
        _ = directory.Write(bytes: "alpha"u8, path: "nested/a.bin");

        var assetLock = AssetLock.Update(
            sourcePath: sourcePath,
            paths: ["z.bin", "nested/a.bin", "z.bin"]
        );
        var expectedAlpha = Hash(bytes: "alpha"u8);
        var expectedZ = Hash(bytes: "z"u8);

        Assert.Equal(
            expected: $$"""
                {
                  "format": 1,
                  "assets": {
                    "nested/a.bin": "{{expectedAlpha}}",
                    "z.bin": "{{expectedZ}}"
                  }
                }

                """,
            actual: assetLock.Serialize()
        );
        Assert.False(condition: File.Exists(path: AssetLock.DeriveLockPath(sourcePath: sourcePath)));
        Assert.True(condition: assetLock.Write(sourcePath: sourcePath));
        Assert.False(condition: assetLock.Write(sourcePath: sourcePath));

        var loaded = AssetLock.Load(sourcePath: sourcePath);
        var resolved = loaded.Resolve(sourcePath: sourcePath, paths: ["z.bin", "nested/a.bin"]);

        Assert.Equal(expected: ["nested/a.bin", "z.bin"], actual: resolved.Keys);
        Assert.Equal(expected: "alpha"u8.ToArray(), actual: resolved["nested/a.bin"].Content.ToArray());
        Assert.Equal(expected: expectedZ, actual: resolved["z.bin"].Hash);
    }
    [Fact]
    public void MissingAndChangedAssetsAreRefusedWithoutMutatingLock() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");
        var assetPath = directory.Write(bytes: "before"u8, path: "asset.bin");
        var assetLock = AssetLock.Update(sourcePath: sourcePath, paths: ["asset.bin"]);

        _ = assetLock.Write(sourcePath: sourcePath);
        var lockPath = AssetLock.DeriveLockPath(sourcePath: sourcePath);
        var lockedBytes = File.ReadAllBytes(path: lockPath);

        var missing = Assert.Throws<AssetLockException>(testCode: () => assetLock.Resolve(
            sourcePath: sourcePath,
            paths: ["other.bin"]
        ));

        Assert.Contains(expectedSubstring: "no pinned hash", actualString: missing.Message, comparisonType: StringComparison.Ordinal);

        File.WriteAllBytes(path: assetPath, bytes: "after"u8.ToArray());
        var stale = Assert.Throws<AssetLockException>(testCode: () => assetLock.Resolve(
            sourcePath: sourcePath,
            paths: ["asset.bin"]
        ));

        Assert.Contains(expectedSubstring: "does not match its lock", actualString: stale.Message, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: lockedBytes, actual: File.ReadAllBytes(path: lockPath));
    }
    [InlineData("")]
    [InlineData("../escape.bin")]
    [InlineData("./asset.bin")]
    [InlineData("nested//asset.bin")]
    [InlineData("nested\\asset.bin")]
    [InlineData("C:/asset.bin")]
    [Theory]
    public void NonPortableOrEscapingPathsAreRefused(string path) {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");

        _ = Assert.Throws<AssetLockException>(testCode: () => AssetLock.Update(
            sourcePath: sourcePath,
            paths: [path]
        ));
    }
    [Fact]
    public void StrictParserRefusesUnknownDuplicateAndNonCanonicalEntries() {
        Assert.Contains(
            expectedSubstring: "not recognized",
            actualString: Assert.Throws<AssetLockException>(testCode: () => AssetLock.Parse(json: """
                { "format": 1, "assets": {}, "extra": true }
                """)).Message,
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "duplicated",
            actualString: Assert.Throws<AssetLockException>(testCode: () => AssetLock.Parse(json: """
                { "format": 1, "format": 1, "assets": {} }
                """)).Message,
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "lowercase hexadecimal",
            actualString: Assert.Throws<AssetLockException>(testCode: () => AssetLock.Parse(json: $$"""
                { "format": 1, "assets": { "asset.bin": "sha256/{{new string(c: 'A', count: 64)}}" } }
                """)).Message,
            comparisonType: StringComparison.Ordinal
        );
        Assert.Contains(
            expectedSubstring: "duplicated",
            actualString: Assert.Throws<AssetLockException>(testCode: () => AssetLock.Parse(json: $$"""
                { "format": 1, "assets": {
                  "asset.bin": "sha256/{{new string(c: '0', count: 64)}}",
                  "asset.bin": "sha256/{{new string(c: '1', count: 64)}}"
                } }
                """)).Message,
            comparisonType: StringComparison.Ordinal
        );
    }
    [Fact]
    public void ExplicitUpdateDropsPinsNoLongerDiscovered() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");

        _ = directory.Write(bytes: "kept"u8, path: "kept.bin");
        _ = directory.Write(bytes: "removed"u8, path: "removed.bin");
        _ = AssetLock.Update(sourcePath: sourcePath, paths: ["kept.bin", "removed.bin"]).Write(sourcePath: sourcePath);

        var refreshed = AssetLock.Update(sourcePath: sourcePath, paths: ["kept.bin"]);

        Assert.Single(collection: refreshed.Assets);
        Assert.True(condition: refreshed.Assets.ContainsKey(key: "kept.bin"));
    }
    [Fact]
    public void ExplicitUpdateReplacesAnArbitrarilyLargeMalformedLock() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");

        _ = directory.Write(bytes: "asset"u8, path: "asset.bin");
        var lockPath = AssetLock.DeriveLockPath(sourcePath: sourcePath);

        File.WriteAllBytes(path: lockPath, bytes: new byte[((2 * 1024) * 1024)]);
        var refreshed = AssetLock.Update(sourcePath: sourcePath, paths: ["asset.bin"]);

        Assert.True(condition: refreshed.Write(sourcePath: sourcePath));
        Assert.Equal(expected: refreshed.Serialize(), actual: File.ReadAllText(path: lockPath));
    }
    [Fact]
    public void ReferenceCountIsBoundedBeforeFileReads() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");
        var paths = Enumerable.Range(start: 0, count: (AssetLock.MaximumAssetCount + 1))
            .Select(selector: static index => $"asset-{index}.bin");

        var refusal = Assert.Throws<AssetLockException>(testCode: () => AssetLock.Update(
            sourcePath: sourcePath,
            paths: paths
        ));

        Assert.Contains(expectedSubstring: "entry limit", actualString: refusal.Message, comparisonType: StringComparison.Ordinal);
    }
    [Fact]
    public void CompilationContextUsesDefiningModuleBaseAndWritesOnlyAfterSuccess() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "root/world.puck");

        _ = directory.Write(bytes: [], path: "modules/room.puck");
        _ = directory.Write(bytes: "room"u8, path: "modules/assets/room.bin");
        var moduleBase = Path.GetDirectoryName(path: directory.PathOf(path: "modules/room.puck"))!;
        var diagnostics = new DiagnosticBag();
        var context = new AssetCompilationContext(rootSourcePath: sourcePath, updateLock: true);

        Assert.True(condition: context.TryResolve(
            authoredPath: "assets/room.bin",
            definingBasePath: moduleBase,
            span: SourceSpan.None,
            diagnostics: diagnostics,
            normalizedPath: out var normalizedPath
        ));
        Assert.Equal(expected: "../modules/assets/room.bin", actual: normalizedPath);
        Assert.False(condition: File.Exists(path: AssetLock.DeriveLockPath(sourcePath: sourcePath)));
        Assert.True(condition: context.Validate(diagnostics: diagnostics));
        Assert.True(condition: context.SaveUpdatedLock(diagnostics: diagnostics));
        Assert.True(condition: File.Exists(path: AssetLock.DeriveLockPath(sourcePath: sourcePath)));

        var verifyDiagnostics = new DiagnosticBag();
        var verify = new AssetCompilationContext(rootSourcePath: sourcePath);

        Assert.True(condition: verify.TryResolve(
            authoredPath: "assets/room.bin",
            definingBasePath: moduleBase,
            span: SourceSpan.None,
            diagnostics: verifyDiagnostics,
            normalizedPath: out var verifiedPath
        ));
        Assert.Equal(expected: normalizedPath, actual: verifiedPath);
        Assert.False(condition: verifyDiagnostics.HasErrors);
    }
    [Fact]
    public void CompilationContextDoesNotRefreshAfterAnotherCompileError() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");

        _ = directory.Write(bytes: "asset"u8, path: "asset.bin");
        var diagnostics = new DiagnosticBag();

        diagnostics.ReportError(code: "TEST", message: "another stage refused", span: SourceSpan.None);
        var context = new AssetCompilationContext(rootSourcePath: sourcePath, updateLock: true);

        Assert.True(condition: context.TryResolve(
            authoredPath: "asset.bin",
            definingBasePath: Path.GetDirectoryName(path: sourcePath)!,
            span: SourceSpan.None,
            diagnostics: diagnostics,
            normalizedPath: out _
        ));
        Assert.False(condition: context.SaveUpdatedLock(diagnostics: diagnostics));
        Assert.False(condition: File.Exists(path: AssetLock.DeriveLockPath(sourcePath: sourcePath)));
    }
    [Fact]
    public void WorldCompilerExplicitRefreshPinsAndOrdinaryCompileRefusesChangedBytes() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");
        var assetPath = directory.Write(bytes: "first"u8, path: "content/game.bin");
        const string Source = """
            schema: "puck.world.definition.v1"
            machines [
              {
                name: "cabinet"
                engine: "gaming-brick"
                configuration {
                  schema: "puck.gaming-brick.configuration.v1"
                  content { path: asset "content/game.bin" }
                }
                running: false
              }
            ]
            """;

        var refresh = WorldCompiler.Compile(
            source: Source,
            sourcePath: sourcePath,
            updateAssets: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(condition: refresh.Diagnostics.HasErrors, userMessage: refresh.Diagnostics.FormatReport(sourceText: Source));
        Assert.NotNull(@object: refresh.Assets);
        Assert.True(condition: refresh.Assets!.Validate(diagnostics: refresh.Diagnostics));
        Assert.True(condition: refresh.Assets!.SaveUpdatedLock(diagnostics: refresh.Diagnostics));

        File.WriteAllBytes(path: assetPath, bytes: "second"u8.ToArray());
        var stale = WorldCompiler.Compile(
            source: Source,
            sourcePath: sourcePath,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Contains(collection: stale.Diagnostics, filter: static diagnostic =>
            ((diagnostic.Code == PuckDiagnosticCodes.InvalidValue) &&
            diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "does not match its lock")));
    }
    [Fact]
    public void ImportedModuleAssetResolvesFromDefinitionAndPinsRelativeToRoot() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "root/world.puck");

        _ = directory.Write(bytes: "module asset"u8, path: "modules/assets/game.bin");
        _ = directory.Write(bytes: """
            module cabinet() {
              machines [
                {
                  name: "machine"
                  engine: "gaming-brick"
                  configuration {
                    schema: "puck.gaming-brick.configuration.v1"
                    content { path: asset "assets/game.bin" }
                  }
                  running: false
                }
              ]
            }
            """u8, path: "modules/cabinet.puck");
        const string Source = """
            schema: "puck.world.definition.v1"
            import "../modules/cabinet.puck"
            use cabinet()
            """;

        var compilation = WorldCompiler.Compile(
            source: Source,
            sourcePath: sourcePath,
            updateAssets: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.False(condition: compilation.Diagnostics.HasErrors, userMessage: compilation.Diagnostics.FormatReport(sourceText: Source));
        Assert.True(condition: compilation.Assets!.Validate(diagnostics: compilation.Diagnostics));
        Assert.True(condition: compilation.Assets!.SaveUpdatedLock(diagnostics: compilation.Diagnostics));
        Assert.True(condition: AssetLock.Load(sourcePath: sourcePath).Assets.ContainsKey(key: "../modules/assets/game.bin"));
        Assert.Equal(
            expected: "../modules/assets/game.bin",
            actual: compilation.RequireJson()["machines"]![0]!["configuration"]!["content"]!["path"]!.GetValue<string>()
        );
    }
    [Fact]
    public void FailedWorldCompileNeverWritesRequestedAssetRefresh() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");

        _ = directory.Write(bytes: "asset"u8, path: "asset.bin");
        const string Source = """
            schema: "puck.world.definition.v1"
            machines [
              {
                name: "broken"
                engine: "gaming-brick"
                configuration {
                  schema: "puck.gaming-brick.configuration.v1"
                  content { path: asset "asset.bin" }
                }
                running: false
              }
            ]
            use missing()
            """;

        var compilation = WorldCompiler.Compile(
            source: Source,
            sourcePath: sourcePath,
            updateAssets: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Contains(collection: compilation.Diagnostics, filter: static diagnostic =>
            ((diagnostic.Code == PuckDiagnosticCodes.InvalidValue) &&
            diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "missing")));
        Assert.Single(collection: compilation.Assets!.PendingReferences);
        Assert.False(condition: File.Exists(path: AssetLock.DeriveLockPath(sourcePath: sourcePath)));
    }
    [Fact]
    public void AssetRequiresAQuotedPath() {
        using var directory = new TemporaryDirectory();
        var sourcePath = directory.Write(bytes: [], path: "world.puck");
        const string Source = """
            schema: "puck.world.definition.v1"
            let invalid = asset 42
            """;

        var compilation = WorldCompiler.Compile(
            source: Source,
            sourcePath: sourcePath,
            updateAssets: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Contains(collection: compilation.Diagnostics, filter: static diagnostic =>
            (diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.False(condition: File.Exists(path: AssetLock.DeriveLockPath(sourcePath: sourcePath)));
    }

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));

    private sealed class TemporaryDirectory : IDisposable {
        private readonly string m_path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-asset-lock-" + Guid.NewGuid().ToString(format: "n"))
        );

        public TemporaryDirectory() {
            _ = Directory.CreateDirectory(path: m_path);
        }

        public string Write(string path, ReadOnlySpan<byte> bytes) {
            var fullPath = PathOf(path: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: fullPath)!);
            File.WriteAllBytes(bytes: bytes, path: fullPath);
            return fullPath;
        }
        public string PathOf(string path) =>
            Path.Combine(path1: m_path, path2: path.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'));
        public void Dispose() {
            Directory.Delete(path: m_path, recursive: true);
        }
    }
}

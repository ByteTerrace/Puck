using System.Security.Cryptography;
using System.Text.Json;

using Puck.Cli.Branding;

using Xunit;

namespace Puck.Cli.Tests;

public sealed class BrandingCommandTests {
    private static void AssertRefusesWithoutChangingCopy(Fixture fixture) {
        var before = File.ReadAllBytes(path: fixture.DestinationPath);

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(
            repositoryRoot: fixture.Root,
            check: false
        ));

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "problem(s) found"
        );
        Assert.Equal(
            expected: before,
            actual: File.ReadAllBytes(path: fixture.DestinationPath)
        );
    }
    private static (int ExitCode, string Output) Capture(Func<int> run) {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var writer = new StringWriter();

        Console.SetOut(newOut: writer);
        Console.SetError(newError: writer);
        try {
            return (run(), writer.ToString());
        } finally {
            Console.SetOut(newOut: originalOut);
            Console.SetError(newError: originalError);
        }
    }

    [Fact]
    public void CheckIsReadOnlyAndReportsDeferredCopies() {
        using var fixture = Fixture.Create(
            deferred: true,
            destinationMatches: true
        );
        var before = File.ReadAllBytes(path: fixture.DestinationPath);
        var beforeWrite = File.GetLastWriteTimeUtc(path: fixture.DestinationPath);

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(
            repositoryRoot: fixture.Root,
            check: true
        ));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: output
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "deferred copies: 1"
        );
        Assert.Equal(
            expected: before,
            actual: File.ReadAllBytes(path: fixture.DestinationPath)
        );
        Assert.Equal(
            expected: beforeWrite,
            actual: File.GetLastWriteTimeUtc(path: fixture.DestinationPath)
        );
    }
    [Fact]
    public void CheckReportsDriftWithoutRepairingIt() {
        using var fixture = Fixture.Create();
        var before = File.ReadAllBytes(path: fixture.DestinationPath);

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(
            repositoryRoot: fixture.Root,
            check: true
        ));

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "distributed copy drift"
        );
        Assert.Equal(
            expected: before,
            actual: File.ReadAllBytes(path: fixture.DestinationPath)
        );
    }
    [Fact]
    public void DeferredConsumerMayBeAbsentWhileActiveCopiesAreChecked() {
        using var fixture = Fixture.Create(
            deferred: true,
            destinationMatches: true
        );

        File.WriteAllText(
            path: fixture.ManifestPath,
            contents: File.ReadAllText(path: fixture.ManifestPath)
            .Replace(
                comparisonType: StringComparison.Ordinal,
                newValue: "absent/deferred.bin",
                oldValue: "consumer/deferred.bin"
            )
        );

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(
            repositoryRoot: fixture.Root,
            check: true
        ));

        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "deferred copies: 1"
        );
        Assert.False(condition: Directory.Exists(path: Path.Combine(
            path1: fixture.Root,
            path2: "absent"
        )));
    }
    [Fact]
    public void DestinationOverlappingCanonicalSourceRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: "branding/source.bin");

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void DirectoryDestinationRefusesBeforeAnyCopyIsChanged() {
        using var fixture = Fixture.Create(secondCopyPath: "consumer");

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void DirectoryDestinationRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: "consumer");

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void DuplicateDestinationRefusesBeforeWriting() {
        using var fixture = Fixture.Create(secondCopyPath: "consumer/copy.bin");

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void EscapingDestinationRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: "../outside.bin");

        AssertRefusesWithoutChangingCopy(fixture: fixture);
        Assert.False(condition: File.Exists(path: Path.Combine(
            path1: Path.GetDirectoryName(path: fixture.Root)!,
            path2: "outside.bin"
        )));
    }
    [Fact]
    public void InvalidSourceHashRefusesBeforeWriting() {
        using var fixture = Fixture.Create(manifestHash: new string(
            c: '0',
            count: 64
        ));

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void MissingSourceRefusesBeforeWriting() {
        using var fixture = Fixture.Create(manifestSource: "branding/missing.bin");

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void NonObjectManifestRefusesSafelyBeforeWriting() {
        using var fixture = Fixture.Create();

        File.WriteAllText(
            fixture.ManifestPath,
            "[]"
        );
        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void ReparsePointPathRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: "link/copy.bin");
        var outside = Directory.CreateTempSubdirectory(prefix: "puck-branding-outside-");

        try {
            try {
                Directory.CreateSymbolicLink(
                    path: Path.Combine(
                        path1: fixture.Root,
                        path2: "link"
                    ),
                    pathToTarget: outside.FullName
                );
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) {
                Assert.Skip(reason: $"symbolic links are unavailable: {exception.Message}");
                return;
            }

            AssertRefusesWithoutChangingCopy(fixture: fixture);
        } finally {
            outside.Delete(recursive: true);
        }
    }
    [Fact]
    public void RootedDestinationRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: Path.GetFullPath(path: "branding-outside.bin"));

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void SyncLeavesAnAlreadyMatchingCopyUntouched() {
        using var fixture = Fixture.Create(destinationMatches: true);
        var beforeWrite = File.GetLastWriteTimeUtc(path: fixture.DestinationPath);

        var (exitCode, _) = Capture(run: () => BrandingCommand.Run(
            repositoryRoot: fixture.Root,
            check: false
        ));

        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
        Assert.Equal(
            expected: beforeWrite,
            actual: File.GetLastWriteTimeUtc(path: fixture.DestinationPath)
        );
    }
    [Fact]
    public void SyncRepairsAChangedCopy() {
        using var fixture = Fixture.Create();

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(
            repositoryRoot: fixture.Root,
            check: false
        ));

        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
        Assert.Contains(
            actualString: output,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "synchronized"
        );
        Assert.Equal(
            expected: fixture.SourceBytes,
            actual: File.ReadAllBytes(path: fixture.DestinationPath)
        );
    }
    [Fact]
    public void UnknownAssetRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyAsset: "missing-asset");

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }
    [Fact]
    public void WiringDriftRefusesBeforeWriting() {
        using var fixture = Fixture.Create(wiringText: "missing-brand-token");

        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }

    private sealed class Fixture : IDisposable {
        private Fixture(string root, byte[] sourceBytes, string destinationPath) {
            Root = root;
            SourceBytes = sourceBytes;
            DestinationPath = destinationPath;
        }

        public string DestinationPath { get; }
        public string ManifestPath => Path.Combine(
            path1: Root,
            path2: "branding",
            path3: "manifest.json"
        );
        public string Root { get; }
        public byte[] SourceBytes { get; }

        public static Fixture Create(
            bool deferred = false,
            bool destinationMatches = false,
            string? manifestHash = null,
            string manifestSource = "branding/source.bin",
            string copyAsset = "source",
            string copyPath = "consumer/copy.bin",
            string? secondCopyPath = null,
            string wiringText = "brand-token") {
            var root = Directory.CreateTempSubdirectory(prefix: "puck-branding-").FullName;
            var sourceDirectory = Directory.CreateDirectory(path: Path.Combine(
                path1: root,
                path2: "branding"
            ));
            var consumerDirectory = Directory.CreateDirectory(path: Path.Combine(
                path1: root,
                path2: "consumer"
            ));
            var sourceBytes = new byte[] { 1, 2, 3, 4, 5 };
            var sourcePath = Path.Combine(
                path1: sourceDirectory.FullName,
                path2: "source.bin"
            );
            var destinationPath = Path.Combine(
                path1: root,
                path2: "consumer",
                path3: "copy.bin"
            );

            File.WriteAllBytes(
                bytes: sourceBytes,
                path: sourcePath
            );
            File.WriteAllBytes(
                Path.Combine(
                    path1: consumerDirectory.FullName,
                    path2: "wiring.txt"
                ),
                System.Text.Encoding.UTF8.GetBytes(s: "brand-token")
            );
            File.WriteAllBytes(
                Path.Combine(
                    path1: root,
                    path2: "consumer",
                    path3: "copy.bin"
                ),
                (destinationMatches
                ? sourceBytes
                : new byte[] { 9, 9, 9 })
            );
            var hash = Convert.ToHexString(inArray: SHA256.HashData(source: sourceBytes)).ToLowerInvariant();
            var copies = new List<object> { new { asset = copyAsset, path = copyPath } };

            if (secondCopyPath is not null) {
                copies.Add(item: new { asset = "source", path = secondCopyPath });
            }
            if (deferred) {
                copies.Add(item: new { asset = "source", path = "consumer/deferred.bin", deferred = true });
            }

            var manifest = new {
                schema = "puck.branding.v1",
                assets = new[] { new { id = "source", source = manifestSource, sha256 = (manifestHash ?? hash) } },
                copies,
                wiring = new[] { new { path = "consumer/wiring.txt", contains = wiringText, description = "fixture wiring" } },
            };

            File.WriteAllText(
                Path.Combine(
                    path1: root,
                    path2: "manifest.json"
                ),
                JsonSerializer.Serialize(manifest)
            );
            // Run() uses branding/manifest.json; keep the fixture's source next to it and then move the manifest.
            File.Move(
                Path.Combine(
                    path1: root,
                    path2: "manifest.json"
                ),
                Path.Combine(
                    path1: root,
                    path2: "branding",
                    path3: "manifest.json"
                )
            );

            return new Fixture(
                destinationPath: destinationPath,
                root: root,
                sourceBytes: sourceBytes
            );
        }
        public void Dispose() {
            try { Directory.Delete(
                Root,
                recursive: true
            ); } catch { }
        }
    }
}

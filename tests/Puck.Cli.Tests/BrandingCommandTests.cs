using System.Security.Cryptography;
using System.Text.Json;

using Puck.Cli.Branding;

using Xunit;

namespace Puck.Cli.Tests;

public sealed class BrandingCommandTests {
    [Fact]
    public void CheckIsReadOnlyAndReportsDeferredCopies() {
        using var fixture = Fixture.Create(deferred: true, destinationMatches: true);
        var before = File.ReadAllBytes(fixture.DestinationPath);
        var beforeWrite = File.GetLastWriteTimeUtc(fixture.DestinationPath);

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(repositoryRoot: fixture.Root, check: true));

        Assert.True(condition: exitCode == 0, userMessage: output);
        Assert.Contains(expectedSubstring: "deferred copies: 1", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: before, actual: File.ReadAllBytes(fixture.DestinationPath));
        Assert.Equal(expected: beforeWrite, actual: File.GetLastWriteTimeUtc(fixture.DestinationPath));
    }

    [Fact]
    public void SyncRepairsAChangedCopy() {
        using var fixture = Fixture.Create();

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(repositoryRoot: fixture.Root, check: false));

        Assert.Equal(expected: 0, actual: exitCode);
        Assert.Contains(expectedSubstring: "synchronized", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: fixture.SourceBytes, actual: File.ReadAllBytes(fixture.DestinationPath));
    }

    [Fact]
    public void SyncLeavesAnAlreadyMatchingCopyUntouched() {
        using var fixture = Fixture.Create(destinationMatches: true);
        var beforeWrite = File.GetLastWriteTimeUtc(fixture.DestinationPath);

        var (exitCode, _) = Capture(run: () => BrandingCommand.Run(repositoryRoot: fixture.Root, check: false));

        Assert.Equal(expected: 0, actual: exitCode);
        Assert.Equal(expected: beforeWrite, actual: File.GetLastWriteTimeUtc(fixture.DestinationPath));
    }

    [Fact]
    public void CheckReportsDriftWithoutRepairingIt() {
        using var fixture = Fixture.Create();
        var before = File.ReadAllBytes(fixture.DestinationPath);

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(repositoryRoot: fixture.Root, check: true));

        Assert.Equal(expected: 1, actual: exitCode);
        Assert.Contains(expectedSubstring: "distributed copy drift", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: before, actual: File.ReadAllBytes(fixture.DestinationPath));
    }

    [Fact]
    public void DeferredConsumerMayBeAbsentWhileActiveCopiesAreChecked() {
        using var fixture = Fixture.Create(deferred: true, destinationMatches: true);
        File.WriteAllText(path: fixture.ManifestPath, contents: File.ReadAllText(path: fixture.ManifestPath)
            .Replace(oldValue: "consumer/deferred.bin", newValue: "absent/deferred.bin", comparisonType: StringComparison.Ordinal));

        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(repositoryRoot: fixture.Root, check: true));

        Assert.Equal(expected: 0, actual: exitCode);
        Assert.Contains(expectedSubstring: "deferred copies: 1", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.False(condition: Directory.Exists(path: Path.Combine(path1: fixture.Root, path2: "absent")));
    }

    [Fact]
    public void DirectoryDestinationRefusesBeforeAnyCopyIsChanged() {
        using var fixture = Fixture.Create(secondCopyPath: "consumer");
        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }

    [Fact]
    public void RootedDestinationRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: Path.GetFullPath(path: "branding-outside.bin"));
        AssertRefusesWithoutChangingCopy(fixture: fixture);
    }

    [Fact]
    public void InvalidSourceHashRefusesBeforeWriting() {
        using var fixture = Fixture.Create(manifestHash: new string('0', 64));
        AssertRefusesWithoutChangingCopy(fixture);
    }

    [Fact]
    public void MissingSourceRefusesBeforeWriting() {
        using var fixture = Fixture.Create(manifestSource: "branding/missing.bin");
        AssertRefusesWithoutChangingCopy(fixture);
    }

    [Fact]
    public void UnknownAssetRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyAsset: "missing-asset");
        AssertRefusesWithoutChangingCopy(fixture);
    }

    [Fact]
    public void EscapingDestinationRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: "../outside.bin");
        AssertRefusesWithoutChangingCopy(fixture);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside.bin")));
    }

    [Fact]
    public void DuplicateDestinationRefusesBeforeWriting() {
        using var fixture = Fixture.Create(secondCopyPath: "consumer/copy.bin");
        AssertRefusesWithoutChangingCopy(fixture);
    }

    [Fact]
    public void DirectoryDestinationRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: "consumer");
        AssertRefusesWithoutChangingCopy(fixture);
    }

    [Fact]
    public void DestinationOverlappingCanonicalSourceRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: "branding/source.bin");
        AssertRefusesWithoutChangingCopy(fixture);
    }

    [Fact]
    public void WiringDriftRefusesBeforeWriting() {
        using var fixture = Fixture.Create(wiringText: "missing-brand-token");
        AssertRefusesWithoutChangingCopy(fixture);
    }

    [Fact]
    public void NonObjectManifestRefusesSafelyBeforeWriting() {
        using var fixture = Fixture.Create();
        File.WriteAllText(fixture.ManifestPath, "[]");
        AssertRefusesWithoutChangingCopy(fixture);
    }

    [Fact]
    public void ReparsePointPathRefusesBeforeWriting() {
        using var fixture = Fixture.Create(copyPath: "link/copy.bin");
        var outside = Directory.CreateTempSubdirectory(prefix: "puck-branding-outside-");
        try {
            try {
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "link"), outside.FullName);
            } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) {
                Assert.Skip(reason: $"symbolic links are unavailable: {exception.Message}");
                return;
            }

            AssertRefusesWithoutChangingCopy(fixture);
        } finally {
            outside.Delete(recursive: true);
        }
    }

    private static void AssertRefusesWithoutChangingCopy(Fixture fixture) {
        var before = File.ReadAllBytes(fixture.DestinationPath);
        var (exitCode, output) = Capture(run: () => BrandingCommand.Run(repositoryRoot: fixture.Root, check: false));

        Assert.Equal(expected: 1, actual: exitCode);
        Assert.Contains(expectedSubstring: "problem(s) found", actualString: output, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: before, actual: File.ReadAllBytes(fixture.DestinationPath));
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

    private sealed class Fixture : IDisposable {
        private Fixture(string root, byte[] sourceBytes, string destinationPath) {
            Root = root;
            SourceBytes = sourceBytes;
            DestinationPath = destinationPath;
        }

        public string Root { get; }
        public string ManifestPath => Path.Combine(Root, "branding", "manifest.json");
        public byte[] SourceBytes { get; }
        public string DestinationPath { get; }

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
            var sourceDirectory = Directory.CreateDirectory(Path.Combine(root, "branding"));
            var consumerDirectory = Directory.CreateDirectory(Path.Combine(root, "consumer"));
            var sourceBytes = new byte[] { 1, 2, 3, 4, 5 };
            var sourcePath = Path.Combine(sourceDirectory.FullName, "source.bin");
            var destinationPath = Path.Combine(root, "consumer", "copy.bin");
            File.WriteAllBytes(sourcePath, sourceBytes);
            File.WriteAllBytes(Path.Combine(consumerDirectory.FullName, "wiring.txt"), System.Text.Encoding.UTF8.GetBytes("brand-token"));
            File.WriteAllBytes(Path.Combine(root, "consumer", "copy.bin"), destinationMatches ? sourceBytes : new byte[] { 9, 9, 9 });
            var hash = Convert.ToHexString(SHA256.HashData(sourceBytes)).ToLowerInvariant();
            var copies = new List<object> { new { asset = copyAsset, path = copyPath } };
            if (secondCopyPath is not null) {
                copies.Add(new { asset = "source", path = secondCopyPath });
            }
            if (deferred) {
                copies.Add(new { asset = "source", path = "consumer/deferred.bin", deferred = true });
            }

            var manifest = new {
                schema = "puck.branding.v1",
                assets = new[] { new { id = "source", source = manifestSource, sha256 = manifestHash ?? hash } },
                copies,
                wiring = new[] { new { path = "consumer/wiring.txt", contains = wiringText, description = "fixture wiring" } },
            };
            File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest));
            // Run() uses branding/manifest.json; keep the fixture's source next to it and then move the manifest.
            File.Move(Path.Combine(root, "manifest.json"), Path.Combine(root, "branding", "manifest.json"));

            return new Fixture(root, sourceBytes, destinationPath);
        }

        public void Dispose() {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }
}

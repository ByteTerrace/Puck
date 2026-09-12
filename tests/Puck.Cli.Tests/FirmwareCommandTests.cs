using Puck.Cli.Firmware;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class FirmwareCommandTests {
    [Fact]
    public void MissingOrUnknownOptionsAreUsageErrors() {
        Assert.Equal(expected: 2, actual: PuckRootCommand.Invoke(args: ["firmware", "hgb"]));
        Assert.Equal(expected: 2, actual: PuckRootCommand.Invoke(args: ["firmware", "hgb", "--output", "unused", "--unknown"]));
        Assert.Equal(expected: 2, actual: PuckRootCommand.Invoke(args: ["firmware", "agb", "--source", "unused"]));
    }

    [Fact]
    public void VerificationDoesNotCreateMissingArtifactsOrRepairDrift() {
        using var directory = new FirmwareTestDirectory();
        var missingDirectory = Path.Combine(path1: directory.Path, path2: "not created");
        var image = Path.Combine(path1: missingDirectory, path2: "test.bin");
        byte[] expected = [3, 1, 4, 1, 5];

        Assert.False(condition: FirmwareArtifact.WriteOrVerify(path: image, bytes: expected, verify: true, machine: "test"));
        Assert.False(condition: Directory.Exists(path: missingDirectory));
        Assert.True(condition: FirmwareArtifact.WriteOrVerify(path: image, bytes: expected, verify: false, machine: "test"));
        Assert.True(condition: FirmwareArtifact.WriteOrVerify(path: image, bytes: expected, verify: true, machine: "test"));

        byte[] different = [3, 1, 4, 1, 6];

        Assert.False(condition: FirmwareArtifact.WriteOrVerify(path: image, bytes: different, verify: true, machine: "test"));
        Assert.Equal(expected: expected, actual: File.ReadAllBytes(path: image));
        Assert.Empty(collection: Directory.GetFiles(path: missingDirectory, searchPattern: "*.tmp"));
    }

    [Fact]
    public void HgbGenerationAndVerificationCoverEveryRevision() {
        using var directory = new FirmwareTestDirectory();

        Assert.Equal(expected: 0, actual: PuckRootCommand.Invoke(args: ["firmware", "hgb", "--output", directory.Path]));
        var expectedNames = Enum.GetValues<ConsoleModel>().Select(selector: static model => model.ToString().ToLowerInvariant() + ".bin").Order(comparer: StringComparer.Ordinal).ToArray();
        var actualNames = Directory.GetFiles(path: directory.Path).Select(selector: static file => Path.GetFileName(path: file)).Order(comparer: StringComparer.Ordinal).ToArray();

        Assert.Equal(expected: expectedNames, actual: actualNames);

        foreach (var model in Enum.GetValues<ConsoleModel>()) {
            var path = Path.Combine(path1: directory.Path, path2: model.ToString().ToLowerInvariant() + ".bin");
            Assert.Equal(expected: model.SupportsColor() ? BootRomBuilder.ColorLength : BootRomBuilder.MonochromeLength, actual: new FileInfo(fileName: path).Length);
        }

        Assert.Equal(expected: 0, actual: PuckRootCommand.Invoke(args: ["firmware", "hgb", "--output", directory.Path, "--verify"]));
    }

    [Fact]
    public async Task AgbMissingSourceRefusesBeforeTouchingOutputAsync() {
        using var directory = new FirmwareTestDirectory();
        var image = Path.Combine(path1: directory.Path, path2: "existing.bin");
        byte[] previous = [9, 2, 6, 5];

        File.WriteAllBytes(path: image, bytes: previous);
        // Both tool paths exist, but must never launch: required source validation comes first.
        var executable = Environment.ProcessPath!;
        var result = await AgbFirmwareCommand.RunAsync(source: directory.Path, output: image, clang: executable, linker: executable, verify: false);

        Assert.Equal(expected: 1, actual: result);
        Assert.Equal(expected: previous, actual: File.ReadAllBytes(path: image));
        Assert.Single(collection: Directory.GetFiles(path: directory.Path));
    }

    private sealed class FirmwareTestDirectory : IDisposable {
        public string Path { get; } = Directory.CreateTempSubdirectory(prefix: "puck firmware tests ").FullName;

        public void Dispose() {
            var actual = System.IO.Path.GetFullPath(path: Path);
            var parent = System.IO.Path.TrimEndingDirectorySeparator(path: System.IO.Path.GetFullPath(path: System.IO.Path.GetTempPath()));

            Assert.Equal(expected: parent, actual: System.IO.Path.GetDirectoryName(path: actual), ignoreCase: OperatingSystem.IsWindows());
            Assert.StartsWith(expectedStartString: "puck firmware tests ", actualString: System.IO.Path.GetFileName(path: actual), comparisonType: StringComparison.Ordinal);
            Directory.Delete(path: actual, recursive: true);
        }
    }
}

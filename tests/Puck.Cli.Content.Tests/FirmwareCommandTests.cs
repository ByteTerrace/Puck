using Puck.Cli.Firmware;
using Puck.HumbleGamingBrick;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Content.Tests;

public sealed class FirmwareCommandTests {
    [Fact]
    public async Task AgbMissingSourceRefusesBeforeTouchingOutputAsync() {
        using var directory = new TemporaryDirectory(prefix: "puck-firmware tests ");
        var image = Path.Combine(
            path1: directory.RootPath,
            path2: "existing.bin"
        );
        byte[] previous = [9, 2, 6, 5];

        File.WriteAllBytes(
            bytes: previous,
            path: image
        );
        // Both tool paths exist, but must never launch: required source validation comes first.
        var executable = Environment.ProcessPath!;
        var result = await AgbFirmwareCommand.RunAsync(
            source: directory.RootPath,
            output: image,
            clang: executable,
            linker: executable
        );

        Assert.Equal(
            actual: result,
            expected: 1
        );
        Assert.Equal(
            expected: previous,
            actual: File.ReadAllBytes(path: image)
        );
        Assert.Single(collection: Directory.GetFiles(path: directory.RootPath));
    }
    [Fact]
    public void HgbExportCoversEveryRevision() {
        using var directory = new TemporaryDirectory(prefix: "puck-firmware tests ");

        Assert.Equal(
            expected: 0,
            actual: SuiteRoot.Invoke(args: ["firmware", "hgb", "--output", directory.RootPath])
        );
        var expectedNames = Enum.GetValues<ConsoleModel>().Select(selector: static model => (model.ToString().ToLowerInvariant() + ".bin")).Order(comparer: StringComparer.Ordinal).ToArray();
        var actualNames = Directory.GetFiles(path: directory.RootPath).Select(selector: static file => Path.GetFileName(path: file)).Order(comparer: StringComparer.Ordinal).ToArray();

        Assert.Equal(
            actual: actualNames,
            expected: expectedNames
        );

        foreach (var model in Enum.GetValues<ConsoleModel>()) {
            var path = Path.Combine(
                path1: directory.RootPath,
                path2: (model.ToString().ToLowerInvariant() + ".bin")
            );

            Assert.Equal(
                expected: (model.SupportsColor()
                ? BootRomBuilder.ColorLength
                : BootRomBuilder.MonochromeLength),
                actual: new FileInfo(fileName: path).Length
            );
        }


    }
    [Fact]
    public void MissingOrUnknownOptionsAreUsageErrors() {
        Assert.Equal(
            expected: 2,
            actual: SuiteRoot.Invoke(args: ["firmware", "hgb"])
        );
        Assert.Equal(
            expected: 2,
            actual: SuiteRoot.Invoke(args: ["firmware", "hgb", "--output", "unused", "--unknown"])
        );
        Assert.Equal(
            expected: 2,
            actual: SuiteRoot.Invoke(args: ["firmware", "agb", "--source", "unused"])
        );
    }
}

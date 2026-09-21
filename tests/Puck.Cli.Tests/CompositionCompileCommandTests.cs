using System.Text.Json;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class CompositionCompileCommandTests {
    private const string TwoWorlds = """
        module room(value) { state { world { slot score = value } } }
        world north = room(1)
        world south = room(2)
        """;

    [Fact]
    public async Task CompositionWritesEveryDeclaredFilenameAndDocumentIdentityAsync() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(path: "map.puck", text: TwoWorlds);

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source]));

        using var north = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: directory.PathOf(path: "north.world.json")));
        using var south = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: directory.PathOf(path: "south.world.json")));

        Assert.Equal(expected: "north", actual: north.RootElement.GetProperty(propertyName: "documentId").GetString());
        Assert.Equal(expected: "south", actual: south.RootElement.GetProperty(propertyName: "documentId").GetString());
    }
    [Fact]
    public async Task OneInvalidWorldPreventsEveryCompositionOutputAsync() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(path: "map.puck", text: """
            module room() { state { world { slot score = 0 } } }
            world north = room()
            world south = missing()
            """);

        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source]));
        Assert.False(condition: File.Exists(path: directory.PathOf(path: "north.world.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(path: "south.world.json")));
    }
    [Fact]
    public async Task CompositionOutputOptionNamesDestinationDirectoryAsync() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(path: "map.puck", text: TwoWorlds);
        var destination = directory.PathOf(path: "generated");

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--output", destination]));

        Assert.True(condition: File.Exists(path: Path.Combine(path1: destination, path2: "north.world.json")));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: destination, path2: "south.world.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(path: "north.world.json")));
    }
    [Fact]
    public async Task AssetUpdateRequiresSemanticValidationAndDefaultCompileRefusesStaleBytesAsync() {
        using var directory = new TemporaryDirectory();
        var asset = directory.WriteBytes(path: "game.gb", bytes: "first"u8);
        var invalid = directory.WriteText(path: "invalid.puck", text: MachineSource(model: "not-a-model"));

        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", invalid, "--update-assets"]));
        Assert.False(condition: File.Exists(path: directory.PathOf(path: "invalid.assets.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(path: "invalid.world.json")));

        var source = directory.WriteText(path: "cabinet.puck", text: MachineSource(model: "cgb"));

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--update-assets"]));
        Assert.True(condition: File.Exists(path: directory.PathOf(path: "cabinet.assets.json")));
        var output = directory.PathOf(path: "cabinet.world.json");
        var originalOutput = File.ReadAllBytes(path: output);

        File.WriteAllBytes(path: asset, bytes: "changed"u8.ToArray());
        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source]));
        Assert.Equal(expected: originalOutput, actual: File.ReadAllBytes(path: output));
    }
    [Fact]
    public async Task CartridgeSourceRefusesWorldAssetUpdateFlagAsync() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(path: "game.puck", text: "schema: \"puck.cartridge.v1\"");

        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--update-assets"]));
        Assert.False(condition: File.Exists(path: directory.PathOf(path: "game.assets.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(path: "game.cartridge.json")));
    }
    [Fact]
    public async Task AssetCompilationRefusesRelocatingOutputAwayFromSourceAsync() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteBytes(path: "game.gb", bytes: "game"u8);
        var source = directory.WriteText(path: "cabinet.puck", text: MachineSource(model: "cgb"));

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--update-assets"]));
        var relocated = directory.PathOf(path: "elsewhere/cabinet.world.json");

        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--output", relocated]));
        Assert.False(condition: File.Exists(path: relocated));
    }

    private static string MachineSource(string model) => $$"""
        schema: "puck.world.definition.v1"
        machines [
          {
            name: "cabinet"
            engine: "gaming-brick"
            configuration {
              schema: "puck.gaming-brick.configuration.v1"
              model: "{{model}}"
              content { path: asset "game.gb" }
            }
            running: false
          }
        ]
        """;

    private sealed class TemporaryDirectory : IDisposable {
        private readonly string m_path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: ("puck-composition-cli-" + Guid.NewGuid().ToString(format: "n"))
        );

        public TemporaryDirectory() {
            _ = Directory.CreateDirectory(path: m_path);
        }

        public string PathOf(string path) =>
            Path.Combine(path1: m_path, path2: path.Replace(newChar: Path.DirectorySeparatorChar, oldChar: '/'));
        public string WriteBytes(string path, ReadOnlySpan<byte> bytes) {
            var fullPath = PathOf(path: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: fullPath)!);
            File.WriteAllBytes(path: fullPath, bytes: bytes);
            return fullPath;
        }
        public string WriteText(string path, string text) =>
            WriteBytes(path: path, bytes: System.Text.Encoding.UTF8.GetBytes(s: text));
        public void Dispose() {
            Directory.Delete(path: m_path, recursive: true);
        }
    }
}

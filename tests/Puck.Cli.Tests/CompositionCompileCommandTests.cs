using Puck.Testing;
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
        var source = directory.WriteText(name: "map.puck", text: TwoWorlds);

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source]));

        using var north = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: directory.PathOf(name: "north.world.json")));
        using var south = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: directory.PathOf(name: "south.world.json")));

        Assert.Equal(expected: "north", actual: north.RootElement.GetProperty(propertyName: "documentId").GetString());
        Assert.Equal(expected: "south", actual: south.RootElement.GetProperty(propertyName: "documentId").GetString());
    }
    [Fact]
    public async Task OneInvalidWorldPreventsEveryCompositionOutputAsync() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(name: "map.puck", text: """
            module room() { state { world { slot score = 0 } } }
            world north = room()
            world south = missing()
            """);

        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source]));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "north.world.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "south.world.json")));
    }
    // A composition's worlds decompile together into the one source that emits them, with the border between them
    // read back from the rows it generated in both; that source compiles to the same documents, byte for byte.
    [Fact]
    public async Task CompositionWorldsDecompileTogetherIntoTheSourceThatEmitsThemAsync() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(name: "map.puck", text: """
            module plot(centre: Point) {
                ground yard { center: centre size [12m, 12m] }
            }
            world north = plot(centre: [0m, 0m, -6m])
            world south = plot(centre: [0m, 0m, 6m])
            border north.south, south.north { height: 4m }
            """);
        var decompiled = directory.PathOf(name: "again/map.puck");

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source]));
        Assert.Equal(expected: 2, actual: await PuckRootCommand.InvokeAsync(args: ["decompile", directory.PathOf(name: "north.world.json"), directory.PathOf(name: "south.world.json")]));
        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["decompile", directory.PathOf(name: "north.world.json"), directory.PathOf(name: "south.world.json"), "--output", decompiled]));
        Assert.Contains(expectedSubstring: "border north.south, south.north", actualString: File.ReadAllText(path: decompiled), comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", decompiled]));

        foreach (var world in new[] { "north", "south" }) {
            Assert.Equal(
                expected: File.ReadAllBytes(path: directory.PathOf(name: $"{world}.world.json")),
                actual: File.ReadAllBytes(path: directory.PathOf(name: $"again/{world}.world.json"))
            );
        }
    }
    [Fact]
    public async Task CompositionOutputOptionNamesDestinationDirectoryAsync() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(name: "map.puck", text: TwoWorlds);
        var destination = directory.PathOf(name: "generated");

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--output", destination]));

        Assert.True(condition: File.Exists(path: Path.Combine(path1: destination, path2: "north.world.json")));
        Assert.True(condition: File.Exists(path: Path.Combine(path1: destination, path2: "south.world.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "north.world.json")));
    }
    [Fact]
    public async Task AssetUpdateRequiresSemanticValidationAndDefaultCompileRefusesStaleBytesAsync() {
        using var directory = new TemporaryDirectory();
        var asset = directory.WriteBytes(bytes: "first"u8, name: "game.gb");
        var invalid = directory.WriteText(name: "invalid.puck", text: MachineSource(model: "not-a-model"));

        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", invalid, "--update-assets"]));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "invalid.assets.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "invalid.world.json")));

        var source = directory.WriteText(name: "cabinet.puck", text: MachineSource(model: "cgb"));

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--update-assets"]));
        Assert.True(condition: File.Exists(path: directory.PathOf(name: "cabinet.assets.json")));
        var output = directory.PathOf(name: "cabinet.world.json");
        var originalOutput = File.ReadAllBytes(path: output);

        File.WriteAllBytes(path: asset, bytes: "changed"u8.ToArray());
        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source]));
        Assert.Equal(expected: originalOutput, actual: File.ReadAllBytes(path: output));
    }
    [Fact]
    public async Task CartridgeSourceRefusesWorldAssetUpdateFlagAsync() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(name: "game.puck", text: "schema: \"puck.cartridge.v1\"");

        Assert.Equal(expected: 1, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--update-assets"]));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "game.assets.json")));
        Assert.False(condition: File.Exists(path: directory.PathOf(name: "game.cartridge.json")));
    }
    [Fact]
    public async Task AssetCompilationRefusesRelocatingOutputAwayFromSourceAsync() {
        using var directory = new TemporaryDirectory();

        _ = directory.WriteBytes(bytes: "game"u8, name: "game.gb");
        var source = directory.WriteText(name: "cabinet.puck", text: MachineSource(model: "cgb"));

        Assert.Equal(expected: 0, actual: await PuckRootCommand.InvokeAsync(args: ["compile", source, "--update-assets"]));
        var relocated = directory.PathOf(name: "elsewhere/cabinet.world.json");

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

}

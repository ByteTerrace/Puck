using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.World;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>A staged world's machine configuration names its content where it lands: the content path a cabinet
/// authors beside its source is relocated to the staged document, as its fonts and asset rows are.</summary>
public sealed class WorldStagingMachineLawTests {
    // THE LAW: composition relocates each inherited cabinet from the document that authored it before staging the
    // merged world. A same-named root file is the red control: relocating an inherited path as a root path loads it.
    [InlineData("basis")]
    [InlineData("import")]
    [InlineData("alias")]
    [Theory]
    public void An_inherited_cabinet_keeps_its_content_origin_when_staged(string mode) {
        using var files = new TemporaryDirectory(prefix: "puck-staging-inherited-machine-");
        const string Authored = "carts/game.cgb.cartridge.json";

        files.WriteText(name: $"source/library/{Authored}", text: "inherited cartridge");
        files.WriteText(name: $"source/{Authored}", text: "root decoy");
        files.WriteText(name: "source/library/cabinet.world.json", text: $$"""
            {
              "schema": "{{WorldDefinition.SchemaVersion}}",
              "machines": [
                {
                  "name": "cabinet",
                  "engine": "gaming-brick",
                  "configuration": {
                    "schema": "puck.gaming-brick.configuration.v1",
                    "model": "cgb",
                    "boot": "fast",
                    "content": { "path": "{{Authored}}" }
                  }
                }
              ]
            }
            """);
        var world = new JsonObject { ["schema"] = WorldDefinition.SchemaVersion };

        if (mode == "basis") {
            world["basis"] = "library/cabinet";
        } else {
            var import = new JsonObject { ["document"] = "library/cabinet" };

            if (mode == "alias") {
                import["as"] = "exhibit";
            }

            world["imports"] = new JsonArray { import };
        }

        Assert.True(condition: WorldStaging.TryWrite(
            catalog: CliWorldVocabulary.EnsureInstalled(),
            directory: files.PathOf(name: "staged"),
            name: "gallery",
            path: out var staged,
            reason: out var reason,
            sourceDirectory: files.PathOf(name: "source"),
            world: world
        ), userMessage: reason);

        var machine = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: staged))!["machines"]![0]!;
        var relocated = machine["configuration"]!["content"]!["path"]!.GetValue<string>();
        var resolved = WorldDocumentPaths.Resolve(documentDirectory: Path.GetDirectoryName(path: staged), path: relocated);

        Assert.Equal(expected: "inherited cartridge", actual: File.ReadAllText(path: resolved));
        Assert.Equal(expected: ((mode == "alias") ? "exhibit$cabinet" : "cabinet"), actual: machine["name"]!.GetValue<string>());
        Assert.Equal(expected: "root decoy", actual: File.ReadAllText(path: files.PathOf(name: $"source/{Authored}")));
    }
    // THE LAW: a cabinet's content path resolves beside the staged document to the file it named beside its source. The
    // red leg is the path as authored, which names nothing beside the staged document.
    [Fact]
    public void A_staged_cabinets_content_resolves_to_the_file_beside_its_source() {
        using var files = new TemporaryDirectory(prefix: "puck-staging-machine-");
        const string Authored = "carts/game.cgb.cartridge.json";

        files.WriteText(name: $"source/{Authored}", text: "{}");

        var world = ((JsonObject)JsonNode.Parse(json: $$"""
            {
              "schema": "{{WorldDefinition.SchemaVersion}}",
              "machines": [
                {
                  "name": "cabinet",
                  "engine": "gaming-brick",
                  "configuration": {
                    "schema": "puck.gaming-brick.configuration.v1",
                    "model": "cgb",
                    "boot": "fast",
                    "content": { "path": "{{Authored}}" }
                  }
                }
              ]
            }
            """)!);

        Assert.True(
            condition: WorldStaging.TryWrite(
                catalog: CliWorldVocabulary.EnsureInstalled(),
                directory: files.PathOf(name: "staged/compositions"),
                name: "gallery",
                path: out var staged,
                reason: out var reason,
                sourceDirectory: files.PathOf(name: "source"),
                world: world
            ),
            userMessage: reason
        );

        var stagedDirectory = Path.GetDirectoryName(path: staged)!;
        var relocated = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: staged))!["machines"]![0]!["configuration"]!["content"]!["path"]!.GetValue<string>();

        Assert.Equal(
            actual: Path.GetFullPath(path: Path.Combine(path1: stagedDirectory, path2: relocated)),
            expected: Path.GetFullPath(path: files.PathOf(name: $"source/{Authored}"))
        );
        Assert.False(condition: File.Exists(path: Path.Combine(path1: stagedDirectory, path2: Authored)));
    }
}

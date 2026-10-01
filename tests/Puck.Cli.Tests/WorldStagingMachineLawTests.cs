using System.Text.Json.Nodes;
using Puck.Testing;
using Puck.World;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>A staged world's machine configuration names its content where it lands: the content path a cabinet
/// authors beside its source is relocated to the staged document, as its fonts and asset rows are.</summary>
public sealed class WorldStagingMachineLawTests {
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

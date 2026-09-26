using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Shaders;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <see cref="WorldPostRenderExtensionPasses"/>: an id the document names more than once composes one
/// pass per entry, and a parameter binding's write lands over each pass's own config, so neither entry's other fields
/// are overwritten by the other's; a write a pass refuses changes no pass's kept config; and with no root attached every
/// write is refused.</summary>
public sealed class WorldPostRenderExtensionPassesLawTests {
    private const string FilmGrain = "sdf-film-grain";

    private static JsonElement Json(string text) => JsonDocument.Parse(json: text).RootElement.Clone();
    private static (WorldPostRenderExtensionPasses Passes, Dictionary<string, JsonNode?> Written) Twice() {
        WorldRenderExtensionEntry[] extensions = [
            new(Config: Json(text: """{"intensity":0.1,"size":2}"""), Id: FilmGrain),
            new(Config: Json(text: """{"intensity":0.3,"size":4}"""), Id: FilmGrain),
        ];
        var passes = new WorldPostRenderExtensionPasses();

        passes.Attach(
            extensions: extensions,
            graph: WorldRootGraph.Compose(
                extensions: extensions,
                overlay: false,
                packages: RenderGraphPackageCatalog.Shipped
            ),
            root: static () => null
        );

        return (passes, new Dictionary<string, JsonNode?>(comparer: StringComparer.Ordinal));
    }
    private static void AssertConfig(Dictionary<string, JsonNode?> written, string pass, string json) => Assert.True(
        condition: JsonNode.DeepEquals(
            node1: written[pass],
            node2: JsonNode.Parse(json: json)
        ),
        userMessage: $"{pass}: {written[pass]?.ToJsonString()}"
    );

    [Fact]
    public void ABindingWritesItsFieldOverEachRepeatedEntrysOwnConfig() {
        var (passes, written) = Twice();

        bool Write(string pass, JsonElement config) {
            written[pass] = JsonNode.Parse(json: config.GetRawText());

            return true;
        }

        Assert.True(condition: passes.TrySetConfig(
            field: "intensity",
            id: FilmGrain,
            value: 0.5f,
            write: Write
        ));
        AssertConfig(json: """{"intensity":0.5,"size":2}""", pass: FilmGrain, written: written);
        AssertConfig(json: """{"intensity":0.5,"size":4}""", pass: $"{FilmGrain}-2", written: written);

        Assert.True(condition: passes.TrySetConfig(
            field: "size",
            id: FilmGrain,
            value: 8f,
            write: Write
        ));
        AssertConfig(json: """{"intensity":0.5,"size":8}""", pass: FilmGrain, written: written);
        AssertConfig(json: """{"intensity":0.5,"size":8}""", pass: $"{FilmGrain}-2", written: written);
    }
    [Fact]
    public void AWriteAPassRefusesChangesNoPasssKeptConfig() {
        var (passes, written) = Twice();

        Assert.False(condition: passes.TrySetConfig(
            field: "intensity",
            id: FilmGrain,
            value: 0.9f,
            write: (pass, _) => (pass == FilmGrain)
        ));
        Assert.True(condition: passes.TrySetConfig(
            field: "size",
            id: FilmGrain,
            value: 3f,
            write: (pass, config) => {
                written[pass] = JsonNode.Parse(json: config.GetRawText());

                return true;
            }
        ));
        AssertConfig(json: """{"intensity":0.1,"size":3}""", pass: FilmGrain, written: written);
        AssertConfig(json: """{"intensity":0.3,"size":3}""", pass: $"{FilmGrain}-2", written: written);
    }
    [Fact]
    public void WithNoRootAttachedEveryWriteIsRefused() {
        var (passes, _) = Twice();

        Assert.False(condition: passes.TrySetConfig(
            field: "intensity",
            id: FilmGrain,
            value: 0.5f
        ));
        Assert.False(condition: new WorldPostRenderExtensionPasses().TrySetConfig(
            field: "intensity",
            id: FilmGrain,
            value: 0.5f,
            write: static (_, _) => true
        ));
    }
}

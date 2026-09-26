using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.Shaders;
using Puck.World.Client;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for <see cref="WorldPostPasses"/>: two rows running one package are two passes, and a parameter
/// binding's write lands over the named pass's own config, so the other pass's config is untouched; a write a pass
/// refuses changes no pass's kept config; and with no root attached every write is refused.</summary>
public sealed class WorldPostPassesLawTests {
    private const string First = "grain";
    private const string Second = "heavy-grain";

    private static JsonElement Json(string text) => JsonDocument.Parse(json: text).RootElement.Clone();
    private static WorldPostPasses Twice() {
        var passes = new WorldPostPasses();

        passes.Attach(
            graph: WorldRootGraph.Compose(
                overlay: false,
                packages: RenderGraphPackageCatalog.Engine,
                post: [
                    new(Config: Json(text: """{"intensity":0.1,"size":2}"""), Name: First, Package: RenderGraphPackageCatalog.SdfFilmGrain),
                    new(Config: Json(text: """{"intensity":0.3,"size":4}"""), Name: Second, Package: RenderGraphPackageCatalog.SdfFilmGrain),
                ]
            ),
            root: static () => null
        );

        return passes;
    }
    private static void AssertConfig(Dictionary<string, JsonNode?> written, string pass, string json) => Assert.True(
        condition: JsonNode.DeepEquals(
            node1: written[pass],
            node2: JsonNode.Parse(json: json)
        ),
        userMessage: $"{pass}: {written[pass]?.ToJsonString()}"
    );

    [Fact]
    public void ABindingWritesItsFieldOverThePassesOwnConfig() {
        var passes = Twice();
        var written = new Dictionary<string, JsonNode?>(comparer: StringComparer.Ordinal);

        bool Write(string pass, JsonElement config) {
            written[pass] = JsonNode.Parse(json: config.GetRawText());

            return true;
        }

        Assert.True(condition: passes.TrySetConfig(field: "intensity", pass: First, value: 0.5f, write: Write));
        Assert.True(condition: passes.TrySetConfig(field: "intensity", pass: Second, value: 0.5f, write: Write));
        AssertConfig(json: """{"intensity":0.5,"size":2}""", pass: First, written: written);
        AssertConfig(json: """{"intensity":0.5,"size":4}""", pass: Second, written: written);

        Assert.True(condition: passes.TrySetConfig(field: "size", pass: First, value: 8f, write: Write));
        AssertConfig(json: """{"intensity":0.5,"size":8}""", pass: First, written: written);
        AssertConfig(json: """{"intensity":0.5,"size":4}""", pass: Second, written: written);
    }
    [Fact]
    public void AWriteAPassRefusesChangesNoPasssKeptConfig() {
        var passes = Twice();
        var written = new Dictionary<string, JsonNode?>(comparer: StringComparer.Ordinal);

        Assert.False(condition: passes.TrySetConfig(field: "intensity", pass: First, value: 0.9f, write: static (_, _) => false));
        Assert.True(condition: passes.TrySetConfig(
            field: "size",
            pass: First,
            value: 3f,
            write: (pass, config) => {
                written[pass] = JsonNode.Parse(json: config.GetRawText());

                return true;
            }
        ));
        AssertConfig(json: """{"intensity":0.1,"size":3}""", pass: First, written: written);
    }
    [Fact]
    public void WithNoRootAttachedEveryWriteIsRefused() {
        Assert.False(condition: Twice().TrySetConfig(field: "intensity", pass: First, value: 0.5f));
        Assert.False(condition: new WorldPostPasses().TrySetConfig(field: "intensity", pass: First, value: 0.5f, write: static (_, _) => true));
        Assert.False(condition: Twice().TrySetConfig(field: "intensity", pass: "absent", value: 0.5f, write: static (_, _) => true));
    }
}

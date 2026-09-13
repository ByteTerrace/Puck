using System.Text.Json.Nodes;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public class CanonicalBasisTests {
    [Fact]
    public void CourtyardPuckBasisComposesToTheSameWorldAsItsGeneratedBasis() {
        var rootPath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: "moth-courtyard.world.json"
        );
        var authored = JsonNode.Parse(File.ReadAllBytes(path: rootPath))!.AsObject();

        Assert.Equal(
            "avatars/moth.puck",
            authored["basis"]?.ToString()
        );
        var generated = authored.DeepClone().AsObject();

        generated["basis"] = "avatars/moth.world.json";
        Assert.True(
            condition: PuckDocumentComposer.TryComposeWorldDocument(
                rootPath,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(authored),
                out var sourceWorld,
                out _,
                out var sourceReason
            ),
            userMessage: sourceReason
        );
        Assert.True(
            condition: PuckDocumentComposer.TryComposeWorldDocument(
                rootPath,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(generated),
                out var generatedWorld,
                out _,
                out var generatedReason
            ),
            userMessage: generatedReason
        );
        Assert.True(condition: JsonNode.DeepEquals(
            node1: sourceWorld,
            node2: generatedWorld
        ));
    }
}

using System.Text.Json;
using Puck.Testing;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Sky baseline fixtures exercise admitted sky features and isolate each counted change class.</summary>
public sealed class SkyBaselineFixtureLawTests {
    private static string PathOf(string path) {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));

        return Path.Combine(path1: root!, path2: path);
    }
    private static WorldDefinition Read(string path) => WorldDefinitionSerialization.Deserialize(
        documentDirectory: Path.GetDirectoryName(path: PathOf(path: path)),
        utf8Json: ShippedWorldDocuments.Read(path: PathOf(path: path))
    );

    [InlineData("still")]
    [InlineData("drift")]
    [InlineData("twinkle")]
    [InlineData("cycle")]
    [Theory]
    public void Counted_sky_fixtures_isolate_one_animated_value(string name) {
        var definition = Read(path: $"tests/Puck.Counters/sky-{name}.puck");
        var stars = Assert.Single(collection: definition.Render.Sky!.Layers!.OfType<WorldRenderSkyLayer.Stars>());
        var clouds = Assert.Single(collection: definition.Render.Sky.Layers!.OfType<WorldRenderSkyLayer.Clouds>());

        Assert.True(condition: (stars.Brightness?.Literal > 0f));
        Assert.Equal(expected: (name == "twinkle"), actual: (stars.Twinkle!.Share?.Literal > 0f));
        Assert.Equal(expected: (name == "cycle"), actual: (definition.Render.Sky.Keys is not null));
        Assert.Equal(expected: 0f, actual: clouds.Spin?.Literal);

        using var document = JsonDocument.Parse(utf8Json: ShippedWorldDocuments.Read(path: PathOf(path: $"tests/Puck.Counters/sky-{name}.puck")));
        var layer = document.RootElement.GetProperty(propertyName: "render").GetProperty(propertyName: "sky").GetProperty(propertyName: "layers")
            .EnumerateArray().Single(predicate: static candidate => (candidate.GetProperty(propertyName: "$type").GetString() == "clouds"));

        Assert.Equal(expected: (name == "drift"), actual: (layer.GetProperty(propertyName: "drift")[0].GetSingle() > 0f));
        Assert.Equal(expected: 0f, actual: layer.GetProperty(propertyName: "shear")[0].GetSingle());
    }
    [InlineData("sky-layers")]
    [InlineData("sky-cycle")]
    [Theory]
    public void Sky_canary_documents_clear_world_admission(string name) {
        Assert.NotNull(@object: Read(path: $"tests/Puck.World.Canaries/{name}/fixture.puck"));

        if (name == "sky-cycle") {
            using var courtyard = JsonDocument.Parse(utf8Json: ShippedWorldDocuments.Read(path: PathOf(path: "src/Puck.World/Assets/worlds/moth-courtyard.puck")));
            using var fixture = JsonDocument.Parse(utf8Json: ShippedWorldDocuments.Read(path: PathOf(path: "tests/Puck.World.Canaries/sky-cycle/fixture.puck")));

            Assert.True(condition: JsonElement.DeepEquals(
                element1: courtyard.RootElement.GetProperty(propertyName: "render"),
                element2: fixture.RootElement.GetProperty(propertyName: "render")
            ));
            // The courtyard's sky keys on its clocks, so the fixture carries the same timeline.
            Assert.True(condition: JsonElement.DeepEquals(
                element1: courtyard.RootElement.GetProperty(propertyName: "timeline"),
                element2: fixture.RootElement.GetProperty(propertyName: "timeline")
            ));
        }
    }
}

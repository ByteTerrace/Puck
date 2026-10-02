using System.Numerics;
using System.Text.Json;
using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Named partial records compile to the existing value resolver and retain the original source shape.</summary>
public sealed class SectionKeyLawTests {
    private static WorldSectionKey Key(double at, string fields) => new(at) {
        Values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(fields)!,
    };
    private static WorldDefinition World(WorldRenderDefaults render) => new(RenderRaw: render,
        TimelineRaw: new WorldTimelineSection(Clocks: [new WorldClock("day", PeriodSeconds: 8d)]));

    [Fact]
    public void Named_rows_and_omitted_leaves_resolve_identically_after_reordering_and_preserve_authored_keys() {
        var sun = new WorldRenderLight.Directional(Color: new BindableColor(Raw: "#00FF00")) { Name = "sun" };
        var fill = new WorldRenderLight.Hemisphere(Color: new BindableColor(Raw: "#000000")) { Name = "fill" };
        var keys = new WorldSectionKeys(Clock: "day", Keys: [
            Key(at: 0d, fields: """{"lights":{"sun":{"color":"#FF0000"}}}"""),
            Key(at: 2d, fields: """{"lights":{"fill":{"color":"#FFFFFF"}}}"""),
            Key(at: 4d, fields: """{"lights":{"sun":{"color":"#0000FF"}}}"""),
        ]);

        foreach (var rows in new WorldRenderLight[][] { [sun, fill], [fill, sun] }) {
            var original = World(render: new WorldRenderDefaults(Lighting: new WorldRenderLighting(rows) { Keys = keys }));
            var prepared = WorldPresentationValues.Of(definition: original);

            Assert.Empty(collection: prepared.Errors);
            Assert.Same(prepared, WorldPresentationValues.Of(definition: original));
            Assert.Same(keys, original.Render.Lighting!.Keys);
            Assert.Null(@object: prepared.Definition.Render.Lighting!.Keys);
            var resolved = Assert.IsType<WorldRenderLight.Directional>(@object: prepared.Definition.Render.Lighting.Lights!.Single(predicate: row => (row.Name == "sun")));
            var curve = resolved.Color!.Value;

            Assert.Equal(new Vector4(w: 1f, x: 0.5f, y: 0f, z: 0.5f), new WorldValueResolver(prepared.Definition, new PresentedTick(Fraction: 0d, Whole: (3UL * EngineTicks.PerSecond))).Color(curve, Vector4.Zero));
            Assert.Equal(new Vector4(w: 1f, x: 0.5f, y: 0f, z: 0.5f), new WorldValueResolver(prepared.Definition, new PresentedTick(Fraction: 0d, Whole: (6UL * EngineTicks.PerSecond))).Color(curve, Vector4.Zero));
            var ambient = Assert.IsType<WorldRenderLight.Hemisphere>(@object: prepared.Definition.Render.Lighting.Lights!.Single(predicate: row => (row.Name == "fill")));

            Assert.Equal(Vector4.One, new WorldValueResolver(prepared.Definition, default).Color(ambient.Color!.Value, Vector4.Zero));
        }
    }
    [InlineData("{\"layers\":{\"stars\":{\"seed\":2}}}", "render.sky.layers.stars.seed", "structure")]
    [InlineData("{\"layers\":{\"missing\":{\"seed\":2}}}", "render.sky.layers.missing", "existing authored row")]
    [InlineData("{\"layers\":[]}", "render.sky.layers", "structure")]
    [InlineData("{\"layers\":{\"stars\":{\"name\":\"renamed\"}}}", "render.sky.layers.stars.name", "structure")]
    [Theory]
    public void Structure_and_missing_named_rows_are_refused_at_the_authored_path(string fields, string path, string detail) {
        var original = World(render: new WorldRenderDefaults(Sky: new WorldRenderSky(Layers: [new WorldRenderSkyLayer.Stars(Seed: 1) { Name = "stars" }]) {
            Keys = new WorldSectionKeys(Clock: "day", Keys: [Key(at: 0d, fields: fields), Key(at: 4d, fields: "{}")]),
        }));
        var errors = WorldPresentationValues.Of(definition: original).Errors;

        Assert.Contains(collection: errors, filter: error => (error.Contains(comparisonType: StringComparison.Ordinal, value: path) && error.Contains(comparisonType: StringComparison.Ordinal, value: detail)));
    }
}

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
        TimelineRaw: new WorldTimelineSection([new WorldClock("day", PeriodSeconds: 8d)]));

    [Fact]
    public void Named_rows_and_omitted_leaves_resolve_identically_after_reordering_and_preserve_authored_keys() {
        var sun = new WorldRenderLight.Directional(Color: new BindableColor("#00FF00")) { Name = "sun" };
        var fill = new WorldRenderLight.Hemisphere(Color: new BindableColor("#000000")) { Name = "fill" };
        var keys = new WorldSectionKeys("day", [
            Key(0d, """{"lights":{"sun":{"color":"#FF0000"}}}"""),
            Key(2d, """{"lights":{"fill":{"color":"#FFFFFF"}}}"""),
            Key(4d, """{"lights":{"sun":{"color":"#0000FF"}}}"""),
        ]);
        foreach (var rows in new WorldRenderLight[][] { [sun, fill], [fill, sun] }) {
            var original = World(new WorldRenderDefaults(Lighting: new WorldRenderLighting(rows) { Keys = keys }));
            var prepared = WorldPresentationValues.Of(original);
            Assert.Empty(prepared.Errors);
            Assert.Same(prepared, WorldPresentationValues.Of(original));
            Assert.Same(keys, original.Render.Lighting!.Keys);
            Assert.Null(prepared.Definition.Render.Lighting!.Keys);
            var resolved = Assert.IsType<WorldRenderLight.Directional>(prepared.Definition.Render.Lighting.Lights!.Single(row => row.Name == "sun"));
            var curve = resolved.Color!.Value;
            Assert.Equal(new Vector4(0.5f, 0f, 0.5f, 1f), new WorldValueResolver(prepared.Definition, new PresentedTick(3UL * EngineTicks.PerSecond, 0d)).Color(curve, Vector4.Zero));
            Assert.Equal(new Vector4(0.5f, 0f, 0.5f, 1f), new WorldValueResolver(prepared.Definition, new PresentedTick(6UL * EngineTicks.PerSecond, 0d)).Color(curve, Vector4.Zero));
            var ambient = Assert.IsType<WorldRenderLight.Hemisphere>(prepared.Definition.Render.Lighting.Lights!.Single(row => row.Name == "fill"));
            Assert.Equal(Vector4.One, new WorldValueResolver(prepared.Definition, default).Color(ambient.Color!.Value, Vector4.Zero));
        }
    }

    [Theory]
    [InlineData("{\"layers\":{\"stars\":{\"seed\":2}}}", "render.sky.layers.stars.seed", "structure")]
    [InlineData("{\"layers\":{\"missing\":{\"seed\":2}}}", "render.sky.layers.missing", "existing authored row")]
    [InlineData("{\"layers\":[]}", "render.sky.layers", "structure")]
    [InlineData("{\"layers\":{\"stars\":{\"name\":\"renamed\"}}}", "render.sky.layers.stars.name", "keyable")]
    public void Structure_and_missing_named_rows_are_refused_at_the_authored_path(string fields, string path, string detail) {
        var original = World(new WorldRenderDefaults(Sky: new WorldRenderSky([new WorldRenderSkyLayer.Stars(Seed: 1) { Name = "stars" }]) {
            Keys = new WorldSectionKeys("day", [Key(0d, fields), Key(4d, "{}")]),
        }));
        var errors = WorldPresentationValues.Of(original).Errors;
        Assert.Contains(errors, error => error.Contains(path, StringComparison.Ordinal) && error.Contains(detail, StringComparison.Ordinal));
    }
}

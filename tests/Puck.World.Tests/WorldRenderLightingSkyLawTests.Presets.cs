using System.Text.Json.Serialization.Metadata;
using Puck.SignedDistance;
using Puck.Testing;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.World.Tests;

// The shipped sky presets: each template in skies.puck, expanded inside a world's sky or atmosphere block, lowers to
// ordinary render.sky or render.atmosphere rows, and those rows resolve to the stack or the air the template spells, layer
// by layer under its own labels, with the argument it was given reaching its layer.
public sealed partial class WorldRenderLightingSkyLawTests {
    private const string SkiesSource = $"{ShippedWorldDocuments.WorldDirectory}/skies.puck";

    // The sky a world importing skies.puck resolves when its sky block (or the given section's) expands the given preset
    // call.
    private static SdfSky PresetSky(string call, string section = "sky") {
        using var scratch = new TemporaryDirectory(prefix: "puck-sky-preset-");
        var source = Path.Combine(path1: scratch.RootPath, path2: "probe.puck");

        File.Copy(destFileName: Path.Combine(path1: scratch.RootPath, path2: "skies.puck"), sourceFileName: RepositoryPaths.Resolve(relativePath: SkiesSource));
        File.WriteAllText(
            contents: $"schema: \"puck.world.definition.v1\"\ndocumentId: \"probe\"\n\nimport \"skies.puck\"\n\nrender {{\n  {section} {{\n    {call}\n  }}\n}}\n",
            path: source
        );

        var compilation = WorldCompiler.CompileFile(path: source);

        Assert.True(condition: compilation.Success, userMessage: $"{call} does not compile against {SkiesSource}.");
        Assert.True(
            condition: WorldJsonPayload.TryParse(
                error: out var error,
                info: ((JsonTypeInfo<WorldRenderDefaults>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(WorldRenderDefaults))),
                json: compilation.RequireJson()["render"]!.ToJsonString(),
                value: out var render
            ),
            userMessage: error
        );

        return Resolve(defaults: BaseDefaults() with { Atmosphere = render.Atmosphere, Sky = render.Sky }).Sky;
    }
    private static (SdfSkyLayerKind Kind, string Label)[] StackOf(SdfSky sky) => [.. Enumerable.Range(count: sky.LayerCount, start: 0).Select(selector: index => (sky.LayerAt(index: index).Kind, sky.LabelAt(index: index)))];

    [Fact]
    public void EachShippedPresetResolvesToTheStackItSpells() {
        var clearDay = PresetSky(call: "clearDay(cloudiness: 0.4)");

        Assert.Equal(
            actual: StackOf(sky: clearDay),
            expected: [(SdfSkyLayerKind.Gradient, "air"), (SdfSkyLayerKind.Disc, "sun"), (SdfSkyLayerKind.Clouds, "cumulus")]
        );
        Assert.Equal(expected: 0.4f, actual: clearDay.Parameters<SdfSkyClouds>(index: 2).Coverage);
        Assert.Equal(expected: 0.004f, actual: PresetSky(call: "clearDayAir()", section: "atmosphere").Atmosphere.FogDensity);

        var starryNight = PresetSky(call: "starryNight(density: 32)");

        Assert.Equal(
            actual: StackOf(sky: starryNight),
            expected: [(SdfSkyLayerKind.Gradient, "air"), (SdfSkyLayerKind.Stars, "stars")]
        );
        Assert.Equal(expected: 32f, actual: starryNight.Parameters<SdfSkyStars>(index: 1).Density);

        var polarNight = PresetSky(call: "polarNight(clock: \"night\", intensity: 2)");

        Assert.Equal(
            actual: StackOf(sky: polarNight),
            expected: [(SdfSkyLayerKind.Gradient, "air"), (SdfSkyLayerKind.Stars, "stars"), (SdfSkyLayerKind.Aurora, "curtains")]
        );
        Assert.Equal(expected: 2f, actual: polarNight.Parameters<SdfSkyAurora>(index: 2).Intensity);

        var overcast = PresetSky(call: "overcast(cover: 0.7)");

        Assert.Equal(
            actual: StackOf(sky: overcast),
            expected: [(SdfSkyLayerKind.Gradient, "air"), (SdfSkyLayerKind.Clouds, "deck")]
        );
        Assert.Equal(expected: 0.7f, actual: overcast.Parameters<SdfSkyClouds>(index: 1).Coverage);
        Assert.Equal(expected: 0.01f, actual: PresetSky(call: "overcastAir()", section: "atmosphere").Atmosphere.FogDensity);
    }
}

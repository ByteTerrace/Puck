using System.Text.Json.Nodes;
using Puck.Maths;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class WorldDocumentSamplingLawTests {
    [Fact]
    public void CompileTimeScatterEqualsTheRuntimeSamplerAndSupportsOrdinaryAuthoredMasks() {
        var lowered = WorldSources.LowerClean("""
            let points = scatterPoints(cellSize: 0.25, width: 32, depth: 24, spacing: 8, radius: 1, seed: 317, worldSeed: 517)
            _points: points
            _selected: filter(map(points, (p, i) => { id: i, position: p }), p => p.position[0] > 0)
            """);
        var actual = Assert.IsType<JsonArray>(lowered["_points"]);
        var expected = CreationStampSampling.ResolveScatter(FixedQ4816.FromDouble(.25), 32, 24, 8, 1, 317, 517);
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++) {
            Assert.Equal((double)expected[i].X, actual[i]![0]!.GetValue<double>());
            Assert.Equal((double)expected[i].Z, actual[i]![2]!.GetValue<double>());
        }
        Assert.Equal(expected.Count(p => p.X > FixedQ4816.Zero), Assert.IsType<JsonArray>(lowered["_selected"]).Count);
    }
    [Theory]
    [InlineData("scatterPoints(1, 8, 8, 0)")]
    [InlineData("scatterPoints(1, 8, 8, 8, radius: 5)")]
    [InlineData("scatterPoints(1, 8, 8, 8, seed: -1)")]
    [InlineData("scatterPoints(1, 8, 8, 8, unknown: 1)")]
    public void InvalidSamplerInputsAreNamed(string expression) {
        Assert.Contains(WorldSources.Diagnose("_points: " + expression), diagnostic => diagnostic.Message.Contains("scatterPoints"));
    }
}

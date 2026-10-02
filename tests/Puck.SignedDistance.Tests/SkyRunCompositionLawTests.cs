using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a sky's layer stack, cut into runs without reordering it (<see cref="SdfSkyRuns.Runs"/>), each field
/// run summarized as the one affine map it applies to the colour beneath, and the runs walked in their authored order
/// (<see cref="SdfSkyRuns.Composite"/>), shows exactly the stack's one ordered evaluation (<see cref="SdfSkyRuns.Evaluate"/>),
/// within a float tolerance of 1e-5 a channel: stars beneath clouds are dimmed by them, and a <c>multiply</c> field over
/// an <c>add</c> point layer multiplies it, whatever the stack interleaves.
/// </summary>
public sealed class SkyRunCompositionLawTests {
    private const float Tolerance = 1e-5f;

    [Fact]
    public void StarsBeneathCloudsAreDimmedByThemAsAuthored() {
        var gradient = new Vector3(x: 0.10f, y: 0.13f, z: 0.20f);
        var star = new Vector3(x: 0.9f, y: 0.8f, z: 0.7f);
        var cloud = new Vector3(x: 0.8f, y: 0.8f, z: 0.85f);
        const float CloudAlpha = 0.7f;
        // Today's stack: the gradient, a field run over nothing; the disc and the stars, a point run; the clouds, a field
        // run over them.
        SdfSkyLayerSample[] stack = [
            new(Alpha: 1f, Blend: SdfSkyBlend.Over, Class: SdfSkyLayerClass.Field, Color: gradient),
            new(Alpha: 1f, Blend: SdfSkyBlend.Add, Class: SdfSkyLayerClass.Point, Color: star),
            new(Alpha: CloudAlpha, Blend: SdfSkyBlend.Over, Class: SdfSkyLayerClass.Field, Color: cloud),
        ];
        var runs = SdfSkyRuns.Runs(stack: stack);

        Assert.Equal(expected: [true, false, true], actual: runs.Select(selector: static run => run.Field));

        var composite = SdfSkyRuns.Composite(beneath: Vector3.Zero, runs: runs);
        var expected = (((1f - CloudAlpha) * (gradient + star)) + (CloudAlpha * cloud));

        Near(actual: composite, expected: expected);
        Near(actual: composite, expected: SdfSkyRuns.Evaluate(beneath: Vector3.Zero, stack: stack));
        // The star reaches the pixel only through the clouds' transmission.
        var starless = SdfSkyRuns.Composite(beneath: Vector3.Zero, runs: SdfSkyRuns.Runs(stack: [stack[0], stack[2]]));

        Near(actual: (composite - starless), expected: ((1f - CloudAlpha) * star));
    }
    [Fact]
    public void AMixedStackComposesAsItsOneOrderedEvaluation() {
        var random = new Random(Seed: 1805);

        for (var trial = 0; (trial < 500); trial++) {
            var stack = new SdfSkyLayerSample[random.Next(maxValue: 10, minValue: 1)];

            for (var layer = 0; (layer < stack.Length); layer++) {
                stack[layer] = new SdfSkyLayerSample(
                    Alpha: random.NextSingle(),
                    Blend: ((SdfSkyBlend)random.Next(maxValue: 4)),
                    Class: ((random.Next(maxValue: 2) == 0) ? SdfSkyLayerClass.Field : SdfSkyLayerClass.Point),
                    Color: new Vector3(x: random.NextSingle(), y: random.NextSingle(), z: random.NextSingle())
                );
            }

            var beneath = new Vector3(x: random.NextSingle(), y: random.NextSingle(), z: random.NextSingle());

            Near(actual: SdfSkyRuns.Composite(beneath: beneath, runs: SdfSkyRuns.Runs(stack: stack)), expected: SdfSkyRuns.Evaluate(beneath: beneath, stack: stack));
        }
    }
    [Fact]
    public void AMultiplyFieldOverAnAddPointLayerMultipliesIt() {
        var tint = new Vector3(x: 0.5f, y: 0.25f, z: 1f);
        var glow = new Vector3(x: 0.4f, y: 0.4f, z: 0.4f);
        SdfSkyLayerSample[] stack = [
            new(Alpha: 1f, Blend: SdfSkyBlend.Add, Class: SdfSkyLayerClass.Point, Color: glow),
            new(Alpha: 1f, Blend: SdfSkyBlend.Multiply, Class: SdfSkyLayerClass.Field, Color: tint),
            new(Alpha: 0.5f, Blend: SdfSkyBlend.Screen, Class: SdfSkyLayerClass.Field, Color: Vector3.One),
        ];
        var runs = SdfSkyRuns.Runs(stack: stack);

        // The two field layers are one run, one map.
        Assert.Equal(expected: 2, actual: runs.Count);
        Near(actual: SdfSkyRuns.Composite(beneath: Vector3.Zero, runs: runs), expected: (((0.5f * (glow * tint)) + new Vector3(value: 0.5f))));
    }

    private static void Near(Vector3 actual, Vector3 expected) {
        Assert.InRange(actual: actual.X, high: (expected.X + Tolerance), low: (expected.X - Tolerance));
        Assert.InRange(actual: actual.Y, high: (expected.Y + Tolerance), low: (expected.Y - Tolerance));
        Assert.InRange(actual: actual.Z, high: (expected.Z + Tolerance), low: (expected.Z - Tolerance));
    }
}

using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a sky's layer stack, cut into runs without reordering it (<see cref="SdfSkyRuns.Runs"/>), each field
/// run summarized as the one affine map it applies to the colour beneath, and the runs walked in their authored order
/// (<see cref="SdfSkyRuns.Composite"/>), shows exactly the stack's one ordered evaluation (<see cref="SdfSkyRuns.Evaluate"/>),
/// within a float tolerance of 1e-5 a channel: stars beneath clouds are dimmed by them, a <c>multiply</c> field over
/// an <c>add</c> point layer multiplies it, a disc between two cloud layers is covered by the upper one alone, and
/// repeated kinds compose in their own order, whatever the stack interleaves. Every kind takes its class from its
/// parameter record (<see cref="ISdfSkyKind.Class"/>), and a stack opens at most <see cref="SdfSky.MaxUpperFieldRuns"/>
/// field runs above its lowest run (<see cref="SdfSkyRunCount"/>), past which <see cref="SdfSky.Pack"/> writes no entry.
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
    [Fact]
    public void EveryKindTakesItsClassAndAStackOfEveryKindComposesAsItsOneOrderedEvaluation() {
        var kinds = new (SdfSkyLayerKind Kind, SdfSkyLayerClass Class)[] {
            (SdfSkyGradient.Kind, SdfSkyGradient.Class),
            (SdfSkyStars.Kind, SdfSkyStars.Class),
            (SdfSkyClouds.Kind, SdfSkyClouds.Class),
            (SdfSkyAurora.Kind, SdfSkyAurora.Class),
            (SdfSkyNoise.Kind, SdfSkyNoise.Class),
            (SdfSkyPattern.Kind, SdfSkyPattern.Class),
            (SdfSkyPanorama.Kind, SdfSkyPanorama.Class),
            (SdfSkyDisc.Kind, SdfSkyDisc.Class),
            (SdfSkyPanel.Kind, SdfSkyPanel.Class),
        };

        Assert.Equal(actual: kinds.Select(selector: static kind => kind.Kind).Order(), expected: Enum.GetValues<SdfSkyLayerKind>().Order());
        Assert.Equal(
            actual: kinds.Where(predicate: static kind => (kind.Class == SdfSkyLayerClass.Point)).Select(selector: static kind => kind.Kind).Order(),
            expected: new[] { SdfSkyLayerKind.Stars, SdfSkyLayerKind.Disc, SdfSkyLayerKind.Panel }.Order()
        );

        // Every kind in a stack, each blend in turn: a gradient, stars, clouds, aurora, noise, a pattern, a panorama and a
        // disc, which cut into three runs.
        var random = new Random(Seed: 818);
        var stack = kinds.Select(selector: (kind, index) => new SdfSkyLayerSample(
            Alpha: (0.25f + (0.5f * random.NextSingle())),
            Blend: ((SdfSkyBlend)(index % 4)),
            Class: kind.Class,
            Color: new Vector3(x: random.NextSingle(), y: random.NextSingle(), z: random.NextSingle())
        )).ToArray();
        var runs = SdfSkyRuns.Runs(stack: stack);

        Assert.Equal(expected: [true, false, true, false], actual: runs.Select(selector: static run => run.Field));
        Near(actual: SdfSkyRuns.Composite(beneath: Vector3.Zero, runs: runs), expected: SdfSkyRuns.Evaluate(beneath: Vector3.Zero, stack: stack));
    }
    [Fact]
    public void RepeatedKindsComposeInTheirOwnOrder() {
        var low = new Vector3(x: 0.9f, y: 0.9f, z: 0.95f);
        var high = new Vector3(x: 0.3f, y: 0.3f, z: 0.4f);
        SdfSkyLayerSample[] stack = [
            new(Alpha: 1f, Blend: SdfSkyBlend.Over, Class: SdfSkyGradient.Class, Color: new Vector3(value: 0.2f)),
            new(Alpha: 0.6f, Blend: SdfSkyBlend.Over, Class: SdfSkyClouds.Class, Color: low),
            new(Alpha: 0.5f, Blend: SdfSkyBlend.Over, Class: SdfSkyClouds.Class, Color: high),
        ];
        var runs = SdfSkyRuns.Runs(stack: stack);
        var composite = SdfSkyRuns.Composite(beneath: Vector3.Zero, runs: runs);

        // Two cloud layers and the gradient beneath them are one field run; the upper cloud covers the lower.
        Assert.Single(collection: runs);
        Near(actual: composite, expected: SdfSkyRuns.Evaluate(beneath: Vector3.Zero, stack: stack));
        Near(actual: composite, expected: ((0.5f * high) + (0.5f * ((0.6f * low) + (0.4f * new Vector3(value: 0.2f))))));
        Assert.NotEqual(expected: composite, actual: SdfSkyRuns.Composite(beneath: Vector3.Zero, runs: SdfSkyRuns.Runs(stack: [stack[0], stack[2], stack[1]])));
    }
    [Fact]
    public void ADiscBetweenTwoCloudLayersIsCoveredByTheUpperOneAlone() {
        var disc = new Vector3(x: 4f, y: 3.8f, z: 3f);
        const float LowAlpha = 0.8f;
        const float HighAlpha = 0.3f;
        SdfSkyLayerSample[] stack = [
            new(Alpha: 1f, Blend: SdfSkyBlend.Over, Class: SdfSkyGradient.Class, Color: new Vector3(value: 0.1f)),
            new(Alpha: LowAlpha, Blend: SdfSkyBlend.Over, Class: SdfSkyClouds.Class, Color: new Vector3(value: 0.9f)),
            new(Alpha: 1f, Blend: SdfSkyBlend.Add, Class: SdfSkyDisc.Class, Color: disc),
            new(Alpha: HighAlpha, Blend: SdfSkyBlend.Over, Class: SdfSkyClouds.Class, Color: new Vector3(value: 0.7f)),
        ];
        var runs = SdfSkyRuns.Runs(stack: stack);
        var composite = SdfSkyRuns.Composite(beneath: Vector3.Zero, runs: runs);
        var discless = SdfSkyRuns.Composite(beneath: Vector3.Zero, runs: SdfSkyRuns.Runs(stack: [stack[0], stack[1], stack[3]]));

        Assert.Equal(expected: [true, false, true], actual: runs.Select(selector: static run => run.Field));
        Near(actual: composite, expected: SdfSkyRuns.Evaluate(beneath: Vector3.Zero, stack: stack));
        // The disc shows through the upper layer's transmission alone; the lower layer, beneath it, never dims it.
        Near(actual: (composite - discless), expected: ((1f - HighAlpha) * disc));
    }
    // A stack opens at most two field runs above its lowest; one more is refused, and the sky writes no entry for it.
    [Fact]
    public void AFieldRunPastTheUpperRunsIsRefusedAndWritesNoEntry() {
        var count = new SdfSkyRunCount();

        Assert.True(condition: count.TryAdd(layerClass: SdfSkyLayerClass.Field));
        Assert.True(condition: count.TryAdd(layerClass: SdfSkyLayerClass.Field));
        for (var run = 0; (run < SdfSky.MaxUpperFieldRuns); run++) {
            Assert.True(condition: count.TryAdd(layerClass: SdfSkyLayerClass.Point));
            Assert.True(condition: count.TryAdd(layerClass: SdfSkyLayerClass.Field));
        }
        Assert.True(condition: count.TryAdd(layerClass: SdfSkyLayerClass.Point));
        Assert.False(condition: count.TryAdd(layerClass: SdfSkyLayerClass.Field));
        Assert.True(condition: count.BaseRun);
        Assert.Equal(expected: SdfSky.MaxUpperFieldRuns, actual: count.UpperRuns);

        var sky = new SdfSky();

        for (var run = 0; (run <= SdfSky.MaxUpperFieldRuns); run++) {
            _ = sky.Add(blend: SdfSkyBlend.Add, label: $"stars{run}", parameters: new SdfSkyStars { Brightness = 1f });
            _ = sky.Add(label: $"clouds{run}", parameters: new SdfSkyClouds { Coverage = 0.5f });
        }

        var layers = new SdfSkyLayer[SdfSky.MaxLayers];

        sky.Pack(block: out var block, details: new SdfSkyDetails(), farDistance: 40f, layers: layers, lights: SdfLights.Default());
        Assert.Equal(actual: block.LayerCount, expected: 6u);
        Assert.Equal(actual: block.BaseRun, expected: 1u);
        Assert.Equal(actual: block.UpperRuns, expected: ((uint)SdfSky.MaxUpperFieldRuns));
        Assert.Equal(expected: SdfSkyLayerKind.Stars, actual: layers[5].Kind);
    }

    private static void Near(Vector3 actual, Vector3 expected) {
        Assert.InRange(actual: actual.X, high: (expected.X + Tolerance), low: (expected.X - Tolerance));
        Assert.InRange(actual: actual.Y, high: (expected.Y + Tolerance), low: (expected.Y - Tolerance));
        Assert.InRange(actual: actual.Z, high: (expected.Z + Tolerance), low: (expected.Z - Tolerance));
    }
}

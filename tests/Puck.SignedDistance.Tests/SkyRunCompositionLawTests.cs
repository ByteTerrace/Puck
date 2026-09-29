using System.Numerics;
using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SkyRunCompositionLawTests {
    [Fact]
    public void RunsPreserveEveryLayerAndSplitOnlyAtClassChanges() {
        SdfSkyLayerClass[] layers = [SdfSkyLayerClass.Field, SdfSkyLayerClass.Field,
            SdfSkyLayerClass.Point, SdfSkyLayerClass.Point, SdfSkyLayerClass.Screen,
            SdfSkyLayerClass.Field, SdfSkyLayerClass.Point, SdfSkyLayerClass.Field];
        Span<SdfSkyRun> runs = stackalloc SdfSkyRun[layers.Length];
        int count = SdfSkyRuns.Write(layers, runs);
        Assert.Equal(6, count);
        Assert.Equal(new(SdfSkyLayerClass.Field, 0, 2), runs[0]);
        Assert.Equal(new(SdfSkyLayerClass.Point, 2, 2), runs[1]);
        Assert.Equal(new(SdfSkyLayerClass.Screen, 4, 1), runs[2]);
        Assert.Equal(new(SdfSkyLayerClass.Field, 5, 1), runs[3]);
        Assert.Equal(new(SdfSkyLayerClass.Point, 6, 1), runs[4]);
        Assert.Equal(new(SdfSkyLayerClass.Field, 7, 1), runs[5]);
    }

    [Fact]
    public void OnlyAFieldRunAtTheBottomCanOmitItsScaleImage() {
        SdfSkyRun bottom = new(SdfSkyLayerClass.Field, 0, 2);
        SdfSkyRun abovePoint = new(SdfSkyLayerClass.Field, 1, 3);
        Assert.Equal(1, bottom.SummaryImageCount);
        Assert.Equal(2, abovePoint.SummaryImageCount);
        Assert.Equal(384UL, bottom.SummaryTexelWrites(24, 16));
        Assert.Equal(768UL, abovePoint.SummaryTexelWrites(24, 16));
        Assert.Equal(0UL, new SdfSkyRun(SdfSkyLayerClass.Point, 0, 1).SummaryTexelWrites(24, 16));
        Assert.Equal(0UL, new SdfSkyRun(SdfSkyLayerClass.Screen, 2, 1).SummaryTexelWrites(24, 16));
        Assert.Equal(0UL, abovePoint.SummaryTexelWrites(0, 16));
        Assert.Throws<OverflowException>(() => abovePoint.SummaryTexelWrites(uint.MaxValue, uint.MaxValue));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FieldSummariesMatchIndependentOrderedBlendEquations(bool pointFirst) {
        Layer[] layers = [new(SdfSkyLayerClass.Field, Blend.Over, new(.2f, .4f, .8f), .7f),
            new(SdfSkyLayerClass.Field, Blend.Add, new(.04f, .03f, .02f)),
            new(SdfSkyLayerClass.Point, Blend.Add, new(.8f, .2f, .1f)),
            new(SdfSkyLayerClass.Field, Blend.Multiply, new(.3f, .5f, .7f)),
            new(SdfSkyLayerClass.Field, Blend.Screen, new(.1f, .4f, .2f)),
            new(SdfSkyLayerClass.Screen, Blend.Over, new(.9f, .1f, .2f), .25f),
            new(SdfSkyLayerClass.Point, Blend.Screen, new(.05f, .08f, .3f)),
            new(SdfSkyLayerClass.Field, Blend.Over, new(.4f, .2f, .1f), .1f)];
        if (pointFirst) { layers[0] = layers[0] with { Class = SdfSkyLayerClass.Point }; }
        SdfSkyLayerClass[] classes = layers.Select(static layer => layer.Class).ToArray();
        Span<SdfSkyRun> runs = stackalloc SdfSkyRun[layers.Length];
        int count = SdfSkyRuns.Write(classes, runs);
        Vector3 actual = Vector3.Zero;
        foreach (SdfSkyRun run in runs[..count]) {
            if (run.Class == SdfSkyLayerClass.Field) {
                SdfSkyAffine summary = SdfSkyAffine.Identity;
                foreach (Layer layer in layers.AsSpan(run.FirstLayer, run.LayerCount)) { summary = summary.Then(Map(layer)); }
                actual = run.SummaryImageCount == 1 ? summary.Offset : summary.Apply(actual);
            }
            else {
                foreach (Layer layer in layers.AsSpan(run.FirstLayer, run.LayerCount)) { actual = Map(layer).Apply(actual); }
            }
        }
        double[] expected = [0, 0, 0];
        foreach (Layer layer in layers) {
            for (int channel = 0; channel < 3; channel++) {
                double color = layer.Color[channel];
                expected[channel] = layer.Blend switch {
                    Blend.Over => layer.Alpha * color + (1 - (double)layer.Alpha) * expected[channel],
                    Blend.Add => color + expected[channel],
                    Blend.Multiply => color * expected[channel],
                    Blend.Screen => color + (1 - color) * expected[channel],
                    _ => throw new InvalidOperationException(),
                };
            }
        }
        for (int channel = 0; channel < 3; channel++) { Assert.InRange(Math.Abs(expected[channel] - actual[channel]), 0, 2e-7); }
    }

    [Fact]
    public void OpaqueLayerReplacesEverythingBeneathAndOrderIsNotCommutative() {
        SdfSkyAffine add = new(Vector3.One, new(.8f, .6f, .4f));
        SdfSkyAffine multiply = new(new(.2f, .3f, .5f), Vector3.Zero);
        SdfSkyAffine opaque = new(Vector3.Zero, new(.1f, .2f, .3f));
        Assert.Equal(opaque.Offset, add.Then(multiply).Then(opaque).Apply(new(7, 8, 9)));
        Assert.NotEqual(add.Then(multiply).Apply(Vector3.Zero), multiply.Then(add).Apply(Vector3.Zero));
        Assert.Equal(add, SdfSkyAffine.Identity.Then(add));
        Assert.Equal(add, add.Then(SdfSkyAffine.Identity));
    }

    [Fact]
    public void EmptyStackWritesNothingAndPartitioningAllocatesNothing() {
        Assert.Equal(0, SdfSkyRuns.Write([], []));
        SdfSkyLayerClass[] layers = [SdfSkyLayerClass.Point, SdfSkyLayerClass.Field, SdfSkyLayerClass.Screen];
        SdfSkyRun[] runs = new SdfSkyRun[3];
        SdfSkyRuns.Write(layers, runs);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) { SdfSkyRuns.Write(layers, runs); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(new(SdfSkyLayerClass.Field, 1, 1), runs[1]);
    }

    [Fact]
    public void InvalidClassAndInsufficientStorageAreNamed() {
        ArgumentOutOfRangeException invalid = Assert.Throws<ArgumentOutOfRangeException>(() =>
            SdfSkyRuns.Write([SdfSkyLayerClass.Field, (SdfSkyLayerClass)255], new SdfSkyRun[2]));
        Assert.Contains("Sky layer 1", invalid.Message);
        Assert.Contains("255", invalid.Message);
        ArgumentException capacity = Assert.Throws<ArgumentException>(() =>
            SdfSkyRuns.Write([SdfSkyLayerClass.Point], []));
        Assert.Equal("runs", capacity.ParamName);
    }

    private static SdfSkyAffine Map(in Layer layer) => layer.Blend switch {
        Blend.Over => new(new(1 - layer.Alpha), layer.Alpha * layer.Color),
        Blend.Add => new(Vector3.One, layer.Color),
        Blend.Multiply => new(layer.Color, Vector3.Zero),
        Blend.Screen => new(Vector3.One - layer.Color, layer.Color),
        _ => throw new InvalidOperationException(),
    };

    private enum Blend { Over, Add, Multiply, Screen }
    private readonly record struct Layer(SdfSkyLayerClass Class, Blend Blend, Vector3 Color, float Alpha = 1);
}

using Puck.Abstractions.Presentation;
using Xunit;

namespace Puck.SignedDistance.Tests;

/// <summary>The frame snapshot retains authored runs while gates move and copies the exact typed parameters into
/// independent, reusable frame storage.</summary>
public sealed class SkySnapshotLawTests {
    private static SdfSkyLayerInfo Layer(string name, SdfSkyLayerClass kind, float opacity = 1f) => new(
        name, name, kind, SdfSkyBlend.Over, SdfSkyVisibility.Camera, QualityTier.Low,
        opacity, opacity > 0f, null, 0d);

    [Fact]
    public void GateChangesRetainTheAuthoredRunRangesAndSummaryStorage() {
        var sky = new SdfSkySnapshot([
            Layer("ground", SdfSkyLayerClass.Field), Layer("stars", SdfSkyLayerClass.Point),
            Layer("veil", SdfSkyLayerClass.Field),
        ], QualityTier.High);

        sky.SetLayer(1, Layer("stars", SdfSkyLayerClass.Point, 0f));
        Assert.Equal(3, sky.RunCount);
        Assert.Equal(new SdfSkyRun(SdfSkyLayerClass.Field, 0, 1), sky.Run(0));
        Assert.Equal(new SdfSkyRun(SdfSkyLayerClass.Point, 1, 1), sky.Run(1));
        Assert.Equal(new SdfSkyRun(SdfSkyLayerClass.Field, 2, 1), sky.Run(2));
        Assert.Equal(1, sky.Run(0).SummaryImageCount);
        Assert.Equal(2, sky.Run(2).SummaryImageCount);
        Assert.False(sky.Layer(1).Enabled);
    }
    [Fact]
    public void AResolvedUpdateCannotReplaceThePreparedIdentityOrClass() {
        var sky = new SdfSkySnapshot([Layer("stars", SdfSkyLayerClass.Point)], QualityTier.High);

        Assert.Throws<ArgumentException>(() => sky.SetLayer(0, Layer("other", SdfSkyLayerClass.Point)));
        Assert.Throws<ArgumentException>(() => sky.SetLayer(0, Layer("stars", SdfSkyLayerClass.Field)));
        Assert.Equal("stars", sky.Layer(0).Name);
        Assert.Equal(SdfSkyLayerClass.Point, sky.Layer(0).Class);
    }
    [Fact]
    public void NativeRecordsRetainIntegerBitsAndPreviouslyPublishedFrames() {
        var table = new SdfSkyParameterTable<uint>("stars", 2);
        table.Rows[0] = 0x80000001u;
        table.Rows[1] = uint.MaxValue;
        var sky = new SdfSkySnapshot([Layer("stars", SdfSkyLayerClass.Point)], QualityTier.High, [table]);
        var published = Assert.IsType<SdfSkySnapshot>(SdfSkySnapshot.Copy(sky, null));
        var copy = Assert.IsType<SdfSkyParameterTable<uint>>(published.Table(0));

        table.Rows[0] = 0x01000001u;
        sky.SetLayer(0, Layer("stars", SdfSkyLayerClass.Point, .5f));
        Assert.Equal(0x80000001u, copy.Rows[0]);
        Assert.Equal(uint.MaxValue, copy.Rows[1]);
        Assert.Equal(1f, published.Layer(0).Opacity);
        Assert.Same(published, SdfSkySnapshot.Copy(sky, published));
        Assert.Same(copy, published.Table(0));
        Assert.Equal(0x01000001u, copy.Rows[0]);
        Assert.Equal(.5f, published.Layer(0).Opacity);
        Assert.True(table.Bytes.SequenceEqual(copy.Bytes));
    }
    [Fact]
    public void TableCopiesRequireTheSameKindTypeAndCount() {
        var into = new SdfSkyParameterTable<uint>("stars", 2);

        Assert.Throws<ArgumentException>(() => into.CopyFrom(new SdfSkyParameterTable<float>("stars", 2)));
        Assert.Throws<ArgumentException>(() => into.CopyFrom(new SdfSkyParameterTable<uint>("clouds", 2)));
        Assert.Throws<ArgumentException>(() => into.CopyFrom(new SdfSkyParameterTable<uint>("stars", 1)));
    }
    [Fact]
    public void PreparedCopiesAllocateNothingUntilTheAuthoredLayoutChanges() {
        var table = new SdfSkyParameterTable<uint>("stars", 2);
        var source = new SdfSkySnapshot([Layer("stars", SdfSkyLayerClass.Point)], QualityTier.High, [table]);
        var destination = SdfSkySnapshot.Copy(source, null)!;
        for (var index = 0; index < 20; index++) { destination = SdfSkySnapshot.Copy(source, destination)!; }
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++) {
            table.Rows[0] = (uint)index;
            source.SetLayer(0, Layer("stars", SdfSkyLayerClass.Point, index / 100f));
            destination = SdfSkySnapshot.Copy(source, destination)!;
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        var replacement = new SdfSkySnapshot([Layer("stars", SdfSkyLayerClass.Point)], QualityTier.High, [table]);
        Assert.NotSame(destination, SdfSkySnapshot.Copy(replacement, destination));
    }
}

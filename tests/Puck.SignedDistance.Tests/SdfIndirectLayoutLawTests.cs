using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfIndirectLayoutLawTests {
    [InlineData(SdfIndirectTier.Medium, 256, 128, 4, 128, 37_683_200UL)]
    [InlineData(SdfIndirectTier.High, 512, 256, 8, 512, 142_475_264UL)]
    [Theory]
    public void TierPoolsAndUpdateCeilingsFitTheirAllocation(SdfIndirectTier tier, int bricks, int rays, int classifications, int traces, ulong bytes) {
        var layout = new SdfIndirectLayout(tier);

        Assert.Equal(bricks, layout.BrickCapacity);
        Assert.Equal(rays, layout.RaysPerProbe);
        Assert.Equal(classifications, layout.ClassifyBudget);
        Assert.Equal(traces, layout.TraceBudget);
        Assert.Equal(((traces * 64) * 105), layout.TraceEvaluationCeiling);
        Assert.Equal((classifications * ((((64 * 28) * 2) * 16) + 192)), layout.ClassifyEvaluationCeiling);
        Assert.Equal(8, SdfIndirectLayout.ProofsPerCell);
        Assert.Equal(bytes, layout.ByteLength);
    }
    [InlineData(SdfIndirectTier.Off)]
    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    [Theory]
    public void RecordsTileTheAllocationWithoutOverlapping(SdfIndirectTier tier) {
        var layout = new SdfIndirectLayout(tier);

        Assert.Equal((layout.ProbeCapacity * SdfIndirectLayout.ProbeWords), layout.CellWordOffset);
        Assert.Equal((layout.CellWordOffset + (layout.ProbeCapacity * SdfIndirectLayout.CellWords)), layout.HitWordOffset);
        Assert.Equal((layout.HitWordOffset + ((layout.ProbeCapacity * layout.RaysPerProbe) * SdfIndirectLayout.HitWords)), layout.ProofWordOffset);
        Assert.Equal((layout.ProofWordOffset + (layout.ProofCapacity * SdfIndirectLayout.ProofWords)), layout.WordCount);
        Assert.Equal((((ulong)layout.WordCount) * sizeof(uint)), layout.ByteLength);
    }
    [Fact]
    public void OffOwnsNoWorkOrStorage() {
        var layout = new SdfIndirectLayout(SdfIndirectTier.Off);

        Assert.Equal(0UL, layout.ByteLength);
        Assert.Empty(layout.Levels);
        Assert.Empty(layout.Pools);
        Assert.Equal(0, layout.TraceEvaluationCeiling);
        Assert.Equal(0, layout.ClassifyEvaluationCeiling);
    }
}

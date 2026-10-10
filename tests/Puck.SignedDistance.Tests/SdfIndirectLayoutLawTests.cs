using Xunit;

namespace Puck.SignedDistance.Tests;

public sealed class SdfIndirectLayoutLawTests {
    [InlineData(SdfIndirectTier.Medium, 256, 128, 4, 128, 100_731_436UL)]
    [InlineData(SdfIndirectTier.High, 512, 256, 8, 512, 562_172_972UL)]
    [Theory]
    public void TierPoolsAndUpdateCeilingsFitTheirAllocation(SdfIndirectTier tier, int bricks, int rays, int classifications, int traces, ulong bytes) {
        var layout = new SdfIndirectLayout(tier: tier);

        Assert.Equal(bricks, layout.BrickCapacity);
        Assert.Equal(rays, layout.RaysPerProbe);
        Assert.Equal(classifications, layout.ClassifyBudget);
        Assert.Equal(traces, layout.TraceBudget);
        Assert.Equal(((traces * 64) * 105), layout.TraceEvaluationCeiling);
        Assert.Equal((classifications * ((((64 * 28) * 2) * 16) + 192)), layout.ClassifyEvaluationCeiling);
        Assert.Equal(actual: SdfIndirectLayout.ProofsPerCell, expected: 8);
        Assert.Equal(bytes, layout.ByteLength);
    }
    [InlineData(SdfIndirectTier.Off, 0, 0, 0)]
    [InlineData(SdfIndirectTier.Medium, 12_288, 4_096, 2_621_440)]
    [InlineData(SdfIndirectTier.High, 0, 32_768, 41_943_040)]
    [Theory]
    public void RecordsTileTheAllocationWithoutOverlapping(SdfIndirectTier tier, int radianceProbeOffset,
        int radianceProbes, int radianceGenerationWords) {
        var layout = new SdfIndirectLayout(tier: tier);

        Assert.Equal(radianceProbeOffset, layout.RadianceProbeOffset);
        Assert.Equal(radianceProbes, layout.RadianceProbeCapacity);
        Assert.Equal(radianceGenerationWords, layout.RadianceGenerationWords);
        Assert.Equal((layout.ProbeCapacity * SdfIndirectLayout.ProbeWords), layout.CellWordOffset);
        Assert.Equal((layout.CellWordOffset + (layout.ProbeCapacity * SdfIndirectLayout.CellWords)), layout.HitWordOffset);
        Assert.Equal((layout.HitWordOffset + ((layout.ProbeCapacity * layout.RaysPerProbe) * SdfIndirectLayout.HitWords)), layout.ProofWordOffset);
        Assert.Equal((layout.ProofWordOffset + (layout.ProofCapacity * SdfIndirectLayout.ProofWords)), layout.RadianceWordOffset);
        Assert.Equal((layout.RadianceWordOffset + (2 * layout.RadianceGenerationWords)), layout.IrradianceWordOffset);
        Assert.Equal((layout.IrradianceWordOffset + (2 * layout.IrradianceGenerationWords)), layout.PublicationWordOffset);
        Assert.Equal((layout.PublicationWordOffset + (2 * layout.ProbeCapacity)), layout.ReceiverProofWordOffset);
        Assert.Equal((layout.ReceiverProofWordOffset + ((tier == SdfIndirectTier.Off) ? 0 : 1)), layout.ShadeScratchWordOffset);
        // One split probe's rays, five source words each, follow the receiver admission word; the measured-cost
        // counters, a visits and a units word per kind, end the allocation.
        Assert.Equal((layout.ShadeScratchWordOffset + (layout.RaysPerProbe * SdfIndirectLayout.RadianceWords)), layout.CostWordOffset);
        Assert.Equal((layout.CostWordOffset + ((tier == SdfIndirectTier.Off) ? 0 : (2 * SdfIndirectLayout.CostKinds))), layout.WordCount);
        Assert.Equal((layout.ProbeCapacity - layout.RadianceProbeOffset), layout.RadianceProbeCapacity);
        Assert.Equal(actual: SdfIndirectLayout.SourceCount, expected: 5);
        Assert.Equal(((layout.RadianceProbeCapacity * layout.RaysPerProbe) * 5), layout.RadianceGenerationWords);
        Assert.Equal(((layout.ProbeCapacity * 64) * 5), layout.IrradianceGenerationWords);
        Assert.Equal((((ulong)layout.WordCount) * sizeof(uint)), layout.ByteLength);
    }
    [Fact]
    public void OffOwnsNoWorkOrStorage() {
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.Off);

        Assert.Equal(0UL, layout.ByteLength);
        Assert.Empty(collection: layout.Levels);
        Assert.Empty(collection: layout.Pools);
        Assert.Equal(0, layout.TraceEvaluationCeiling);
        Assert.Equal(0, layout.ClassifyEvaluationCeiling);
        Assert.Equal(0, layout.ShadeBudget);
        Assert.Equal(0, layout.ReceiverProofBudget);
    }
}

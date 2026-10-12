using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    private const int ChainInstructions = 150;

    [Fact]
    public void AMeasuredShadeIsPricedAtItsCountedVisitsWithoutItsConservativeRecordBounds() {
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.Medium);
        var units = SdfIndirectCost.ShadeUnits(frame: CostFrame(shapes: 1), layout: layout);
        var measured = (units with { MeasuredFieldCost = 19 });

        // Before a measurement the continuation search and the reduction are priced at their bounds; once the kernel has
        // counted its visits, record loads and reduction included, the count is the price.
        Assert.True(condition: (units.ItemCost(instructionCount: ChainInstructions) >= (((long)layout.RaysPerProbe) * ((8L * layout.RaysPerProbe) + SdfIndirectLayout.IrradianceTexels))));
        Assert.Equal(expected: (layout.RaysPerProbe * 19L), actual: measured.ItemCost(instructionCount: ChainInstructions));
        // The parity world's solve at Medium: every allocated probe shaded once for the direct sweep and once for each
        // of the two feedback sweeps, at the visits the RTX 2060 counted there, takes few produced frames' chunks.
        var probes = (((layout.BounceLimit + 1) * layout.BrickCapacity) * SdfIndirectLayout.ProbesPerBrick);
        var chunks = SdfIndirectCost.Admit(count: probes, instructionCount: ChainInstructions, limit: SdfIndirectCost.FrameCost(layout: layout), units: measured);

        Assert.InRange(actual: chunks.Count, high: (SdfIndirectCache.FinishFrameBound / 16), low: 1);
    }
    [Fact]
    public void ANarrowChunkCostsItsLongestLaneAndAWideOneItsVisits() {
        var units = (SdfIndirectCost.TraceUnits with { MeasuredFieldCost = 100 });
        var lane = units.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: 1, UnitFirst: 0), instructionCount: ChainInstructions);

        // A lane alone takes as long as its whole chain, far longer than its visits at the device's throughput; lanes
        // beside it add nothing until their visits outlast it.
        Assert.True(condition: (lane > (64L * 100L)), userMessage: $"{lane}");
        Assert.Equal(expected: lane, actual: units.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: 64, UnitFirst: 0), instructionCount: ChainInstructions));
        var wide = new SdfIndirectChunk(ItemFirst: 0, UnitCount: checked((int)(((lane / 100L) * 4L))), UnitFirst: 0);

        Assert.Equal(expected: (wide.UnitCount * 100L), actual: units.CostOf(chunk: wide, instructionCount: ChainInstructions));
        // A shading lane runs a probe's rays in turn, so its chain is that many rays of the same price.
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.High);
        var shade = (SdfIndirectCost.ShadeUnits(frame: CostFrame(shapes: 1), layout: layout) with { MeasuredFieldCost = 100 });
        var shaded = shade.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: 1, UnitFirst: 0), instructionCount: ChainInstructions);

        Assert.Equal(expected: (lane * (layout.RaysPerProbe / IrradianceLattice.RaysPerStratum)), actual: shaded);
        Assert.Equal(expected: shaded, actual: shade.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: layout.RaysPerProbe, UnitFirst: 0), instructionCount: ChainInstructions));
    }
    [Fact]
    public void AChunkWhoseLongestLaneOutlastsTheSliceFillsTheLanesBesideIt() {
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.High);
        var slice = SdfIndirectCost.FrameCost(layout: layout);
        var units = (SdfIndirectCost.TraceUnits with { MeasuredFieldCost = 10_000 });
        var lane = units.CostOf(chunk: new SdfIndirectChunk(ItemFirst: 0, UnitCount: 1, UnitFirst: 0), instructionCount: ChainInstructions);

        Assert.True(condition: (lane > slice), userMessage: "The fixture's single ray must outlast the slice.");
        var chunks = SdfIndirectCost.Admit(count: 512, instructionCount: ChainInstructions, limit: slice, units: units);

        // Every chunk but the last holds more visits than the slice, the rays that run beside its longest one, and no
        // chunk takes more device time than that one ray.
        Assert.All(collection: chunks.Take(count: (chunks.Count - 1)), action: chunk => Assert.True(condition: ((chunk.UnitCount * 10_000L) > slice), userMessage: $"{chunk}"));
        Assert.All(collection: chunks, action: chunk => Assert.Equal(expected: lane, actual: units.CostOf(chunk: chunk, instructionCount: ChainInstructions)));
        Assert.Equal(expected: (512 * IrradianceLattice.RaysPerStratum), actual: chunks.Sum(selector: chunk => chunk.UnitCount));
        // A light unit's chunk fills the slice with visits instead.
        var light = (SdfIndirectCost.TraceUnits with { MeasuredFieldCost = 10 });

        Assert.All(collection: SdfIndirectCost.Admit(count: 512, instructionCount: ChainInstructions, limit: slice, units: light),
            action: chunk => Assert.InRange(actual: light.CostOf(chunk: chunk, instructionCount: ChainInstructions), high: slice, low: 1));
    }
}

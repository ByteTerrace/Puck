using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Puck.Shaders;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfIndirectCacheLawTests {
    private const int Instructions = 11_159;

    [Fact]
    public void AKindIsPricedAtTheVisitsItsKernelsCountedNeverAboveItsConservativePrice() {
        using var rig = new Rig();
        var cache = rig.Cache;
        var conservative = SdfIndirectCost.TraceUnits.UnitCost(instructionCount: Instructions);

        Assert.Null(value: cache.MeasuredFieldCost(kind: SdfIndirectLayout.CostTrace));
        Assert.Equal(conservative, cache.TransportUnits(part: SdfWorldPackage.IndirectTrace).UnitCost(instructionCount: Instructions));
        var revision = cache.PriceRevision;

        // The first copy of a generation is its baseline and prices nothing.
        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostTrace, units: 10, visits: 1_000), generation: cache.CostGeneration, sequence: 1);
        Assert.Null(value: cache.MeasuredFieldCost(kind: SdfIndirectLayout.CostTrace));
        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostTrace, units: (10 + 64), visits: (1_000 + (64 * 900))), generation: cache.CostGeneration, sequence: 2);
        Assert.Equal(900L, cache.MeasuredFieldCost(kind: SdfIndirectLayout.CostTrace));
        Assert.Equal(900L, cache.TransportUnits(part: SdfWorldPackage.IndirectTrace).UnitCost(instructionCount: Instructions));
        Assert.True(condition: (cache.PriceRevision > revision), userMessage: "A changed price advances the revision a pending plan rechunks at.");
        Assert.True(condition: (cache.PlanPrices(instructionCount: Instructions).Trace < SdfIndirectCost.TraceUnits.ItemCost(instructionCount: Instructions)));
        // A stale copy, one of an earlier sequence and one of a cleared buffer, measures nothing.
        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostTrace, units: 0, visits: 0), generation: cache.CostGeneration, sequence: 1);
        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostTrace, units: 75, visits: uint.MaxValue), generation: (cache.CostGeneration - 1), sequence: 3);
        Assert.Equal(900L, cache.MeasuredFieldCost(kind: SdfIndirectLayout.CostTrace));
        // The largest recent measurement prices the kind, never above every query against the complete program.
        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostTrace, units: (10 + 128), visits: ((1_000 + (64 * 900)) + (64 * 2_000_000))), generation: cache.CostGeneration, sequence: 4);
        Assert.Equal(2_000_000L, cache.MeasuredFieldCost(kind: SdfIndirectLayout.CostTrace));
        Assert.Equal(conservative, cache.TransportUnits(part: SdfWorldPackage.IndirectTrace).UnitCost(instructionCount: Instructions));
        Assert.Null(value: cache.MeasuredFieldCost(kind: SdfIndirectLayout.CostPlace));
    }
    [Fact]
    public void AResetRestartsTheCounterBaselineAndKeepsItsPrices() {
        using var rig = new Rig();
        var cache = rig.Cache;

        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostClassify, units: 0, visits: 0), generation: cache.CostGeneration, sequence: 1);
        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostClassify, units: 64, visits: 6_400), generation: cache.CostGeneration, sequence: 2);
        var cleared = cache.CostGeneration;

        cache.Reset(epoch: 2);
        Assert.NotEqual(cleared, cache.CostGeneration);
        // The cleared buffer counts from zero: its first copy is a baseline, never a wrapped difference.
        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostClassify, units: 64, visits: 64), generation: cache.CostGeneration, sequence: 3);
        Assert.Equal(100L, cache.MeasuredFieldCost(kind: SdfIndirectLayout.CostClassify));
        cache.ObserveCost(counters: Counters(kind: SdfIndirectLayout.CostClassify, units: 128, visits: (64 + (64 * 300))), generation: cache.CostGeneration, sequence: 4);
        Assert.Equal(300L, cache.MeasuredFieldCost(kind: SdfIndirectLayout.CostClassify));
    }
    [Fact]
    public void AWaitNeverHoldsForASolveTheSceneKeepsWithdrawing() {
        using var rig = new Rig();
        var cache = rig.Cache;
        var since = cache.Invalidations;

        Cycle(cache: cache);
        Assert.Null(@object: cache.CannotFinishReason(since: since));
        var moved = new IrradianceSphere(Center: Double3.Zero, Radius: 0.05);

        cache.MarkGeometry(current: moved, previous: moved);
        cache.Plan(inputs: Inputs);
        Assert.Null(@object: cache.CannotFinishReason(since: since));
        Cycle(cache: cache);
        cache.MarkGeometry(current: moved, previous: moved);
        cache.Plan(inputs: Inputs);
        Assert.Contains(expectedSubstring: "withdrew its admitted transport 2 times", actualString: cache.CannotFinishReason(since: since));
        Assert.Null(@object: cache.CannotFinishReason(since: cache.Invalidations));

        // Completes the current cycle: its transport traced, and its finite solve published.
        static void Cycle(SdfIndirectCache cache) {
            do { cache.Plan(inputs: Inputs); cache.Submitted(); } while (!cache.TransportComplete);
            cache.BeginLighting();
            while (!cache.LightingComplete) { cache.PlanLighting(); cache.SubmittedLighting(); }
        }
    }
    [Fact]
    public void AWaitNeverHoldsForASolveBeyondTheFrameBoundAtItsMeasuredPrices() {
        using var rig = new Rig();
        var cache = rig.Cache;
        const int Heavy = 50_000;
        var wide = new IrradianceFrameInputs(Cameras: [Double3.Zero], Bounds: [new IrradianceSphere(Center: Double3.Zero, Radius: double.PositiveInfinity)],
            WorldMin: Double3.Zero, WorldMax: new Double3(X: 60, Y: 60, Z: 60));

        cache.Plan(inputs: wide, instructionCount: Heavy);
        // Unmeasured kinds with work left leave the estimate unknown, so the solve is presumed finishable.
        Assert.Null(value: cache.RemainingFrames);
        Assert.Null(@object: cache.CannotFinishReason(since: cache.Invalidations));
        var generation = cache.CostGeneration;

        cache.ObserveCost(counters: Measured(units: 0, visits: 0), generation: generation, sequence: 1);
        // A cheap field: every kind's units cost one visit, so the remaining transport fits the bound.
        cache.ObserveCost(counters: Measured(units: 10, visits: 10), generation: generation, sequence: 2);
        Assert.True(condition: (cache.RemainingFrames is null or <= SdfIndirectCache.FinishFrameBound), userMessage: $"{cache.RemainingFrames}");
        Assert.Null(@object: cache.CannotFinishReason(since: cache.Invalidations));
        // A field whose rays each walk the complete program at every step: its traces alone need far more produced frames.
        cache.ObserveCost(counters: Measured(units: 20, visits: (10u + (10u * (100u * Heavy)))), generation: generation, sequence: 3);
        Assert.True(condition: (cache.RemainingFrames is > SdfIndirectCache.FinishFrameBound), userMessage: $"{cache.RemainingFrames}");
        Assert.Contains(expectedSubstring: "beyond the 4096-frame bound", actualString: cache.CannotFinishReason(since: cache.Invalidations));

        static uint[] Measured(uint visits, uint units) {
            var counters = new uint[SdfIndirectLayout.CostWords];

            for (var kind = 0; (kind < SdfIndirectLayout.CostKinds); kind++) {
                counters[(2 * kind)] = visits;
                counters[((2 * kind) + 1)] = units;
            }
            return counters;
        }
    }
    [Fact]
    public void QueuedGeometryWaitsForTheRunningSolveThenWithdrawsItsTransport() {
        using var rig = new Rig();
        var cache = rig.Cache;

        for (var frame = 0; (frame < 16); frame++) { cache.Plan(inputs: Inputs); cache.Submitted(); }
        Assert.True(condition: cache.IsComplete);
        cache.BeginLighting();
        cache.PlanLighting();
        Assert.True(condition: (cache.ShadeCount > 0));
        var moved = new IrradianceSphere(Center: Double3.Zero, Radius: 0.05);

        // Bodies keep moving while the solve runs: it finishes on the transport it pinned, and publishes.
        while (!cache.LightingComplete) {
            cache.MarkGeometry(current: moved, near: true, previous: moved);
            cache.Plan(inputs: Inputs);
            Assert.True(condition: cache.TransportComplete, userMessage: "A running solve's transport stands while geometry queues.");
            Assert.True(condition: cache.HasQueuedGeometry);
            Assert.False(condition: cache.IsComplete, userMessage: "Queued geometry is not a settled cache.");
            cache.PlanLighting();
            cache.SubmittedLighting();
        }
        Assert.True(condition: (cache.PublishedSweeps > 0));
        // The next plan withdraws what the queued motion reaches, and only then.
        cache.Plan(inputs: Inputs);
        Assert.False(condition: cache.HasQueuedGeometry);
        Assert.False(condition: cache.TransportComplete);
    }

    private static uint[] Counters(int kind, uint visits, uint units) {
        var counters = new uint[SdfIndirectLayout.CostWords];

        counters[(2 * kind)] = visits;
        counters[((2 * kind) + 1)] = units;
        return counters;
    }
}

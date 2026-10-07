using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.SignedDistance.Illumination;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>Shade admission prices every ray's possible directional fallback against the existing transport
/// evaluation allowance. Splitting a sweep changes submission boundaries, never its probe order or publications.</summary>
public sealed class SdfIndirectShadeBudgetLawTests {
    [InlineData(SdfIndirectTier.Medium, 52, 8)]
    [InlineData(SdfIndirectTier.High, 105, 17)]
    [Theory]
    public void EveryDirectionalChannelFitsTheExistingFieldEvaluationAllowance(SdfIndirectTier tier, int one, int six) {
        var layout = new SdfIndirectLayout(tier: tier);

        Assert.Equal(expected: one, actual: SdfIndirectWork.ShadeProbeBudget(directionalChannels: 1, layout: layout));
        Assert.Equal(expected: six, actual: SdfIndirectWork.ShadeProbeBudget(directionalChannels: 6, layout: layout));
        Assert.Equal(expected: layout.ShadeBudget, actual: SdfIndirectWork.ShadeProbeBudget(directionalChannels: 0, layout: layout));
        for (var channels = 1; (channels <= 6); channels++) {
            var admitted = SdfIndirectWork.ShadeProbeBudget(directionalChannels: channels, layout: layout);
            var perProbe = ((layout.RaysPerProbe * channels) * SdfIndirectLightLayout.MarchSteps);

            Assert.InRange(actual: (admitted * perProbe), low: 1, high: layout.TraceEvaluationCeiling);
            Assert.True(condition: (((admitted + 1) * perProbe) > layout.TraceEvaluationCeiling));
        }
    }
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    [Theory]
    public void InvalidChannelCountsAreRefusedBeforeBudgetArithmetic(int channels) {
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => SdfIndirectWork.ShadeProbeBudget(
            layout: new SdfIndirectLayout(tier: SdfIndirectTier.High), directionalChannels: channels));
    }
    [Fact]
    public void OnlyDirectionalStableAndActiveIncomingChannelsSpendTheAllowance() {
        var layout = new SdfIndirectLayout(tier: SdfIndirectTier.Medium);
        var lights = SdfLights.Default();

        lights.Set(index: 1, light: new SdfLight(Kind: SdfLightKind.Point, Direction: Vector3.One,
            Color: Vector3.One, Weight: 1, Param: 1, Shadows: false));
        lights.Count = 2;
        lights.ShadowSlots.Configure(fadeCapacity: 2, slots: 4);
        lights.ShadowSlots.SetSlot(light: 0, slot: 0);
        lights.ShadowSlots.SetSlot(light: 1, slot: 1);
        lights.ShadowSlots.SetSlot(light: 0, slot: 2);
        Assert.Equal(expected: SdfIndirectWork.ShadeProbeBudget(directionalChannels: 2, layout: layout),
            actual: SdfIndirectWork.ShadeProbeBudget(layout: layout, lights: lights));
        lights.ShadowSlots.SetHandoffs(handoffs: [
            new SdfShadowHandoff(Incoming: 0, Outgoing: 0, Slot: 0, Weight: 0.5f),
            new SdfShadowHandoff(Incoming: 1, Outgoing: 0, Slot: 2, Weight: 0.5f),
        ]);
        Assert.Equal(expected: SdfIndirectWork.ShadeProbeBudget(directionalChannels: 3, layout: layout),
            actual: SdfIndirectWork.ShadeProbeBudget(layout: layout, lights: lights));
        lights.Count = 0;
        Assert.Equal(expected: layout.ShadeBudget, actual: SdfIndirectWork.ShadeProbeBudget(layout: layout, lights: lights));
        lights.Count = 2;
        lights.ShadowSlots.Configure(fadeCapacity: 2, slots: 4);
        lights.ShadowSlots.SetSlot(light: 1, slot: 0);
        Assert.Equal(expected: layout.ShadeBudget, actual: SdfIndirectWork.ShadeProbeBudget(layout: layout, lights: lights));
        Assert.Equal(expected: 0, actual: SdfIndirectWork.ShadeProbeBudget(
            layout: new SdfIndirectLayout(tier: SdfIndirectTier.Off), directionalChannels: 6));
    }
    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    [Theory]
    public void SmallerBatchesPreserveEveryProbeAndCompleteBankPublication(SdfIndirectTier tier) {
        var layout = new SdfIndirectLayout(tier: tier);
        var levels = Enumerable.Range(start: 0, count: layout.Levels.Count)
            .Select(selector: level => ((IReadOnlyList<IrradianceProbeKey>)Enumerable.Range(count: 257, start: 0)
                .Select(selector: index => new IrradianceProbeKey(Level: level, X: index, Y: 0, Z: 0)).ToArray())).ToArray();

        static (List<(int Sweep, int Read, int Write, IrradianceProbeKey Probe)> Visits, List<(int Sweeps, int Bank)> Publications)
            Run(IrradianceSolveSchedule solve) {
            var visits = new List<(int Sweep, int Read, int Write, IrradianceProbeKey Probe)>();
            var publications = new List<(int Sweeps, int Bank)>();

            while (!solve.IsComplete) {
                var batch = Assert.IsType<IrradianceSolveBatch>(@object: solve.Plan());

                Assert.Same(expected: batch, actual: solve.Plan());
                foreach (var probe in batch.Probes) { visits.Add(item: (batch.Sweep, batch.ReadGeneration, batch.WriteGeneration, probe)); }
                var previousBank = solve.PublishedGeneration;

                solve.Submitted();
                if (batch.CompletesSweep) { publications.Add(item: (solve.CompletedSweeps, solve.PublishedGeneration)); } else { Assert.Equal(expected: previousBank, actual: solve.PublishedGeneration); }
            }
            return (visits, publications);
        }
        var original = Run(solve: new IrradianceSolveSchedule(levels: levels, bounces: layout.BounceLimit,
            probeBudget: layout.ShadeBudget, publishedGeneration: 1));
        var bounded = Run(solve: new IrradianceSolveSchedule(levels: levels, bounces: layout.BounceLimit,
            probeBudget: SdfIndirectWork.ShadeProbeBudget(directionalChannels: 6, layout: layout), publishedGeneration: 1));

        Assert.Equal(actual: bounded.Visits, expected: original.Visits);
        Assert.Equal(actual: bounded.Publications, expected: original.Publications);
    }
}

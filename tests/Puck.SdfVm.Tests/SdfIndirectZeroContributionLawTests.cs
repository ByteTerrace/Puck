using Puck.SignedDistance;
using Puck.Testing;

using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData(SdfIndirectTier.Medium)]
    [InlineData(SdfIndirectTier.High)]
    [Theory]
    public void DisabledDirectLightSpendsNoFallbackAllowance(SdfIndirectTier tier) {
        foreach (var (direct, gain) in new[] { (false, 1f), (true, 0f), (true, 1f), (true, float.Epsilon) }) {
            var source = Frame() with {
                FarDistance = 1f,
                IndirectTier = tier,
                IndirectBounces = 0,
                IndirectSources = SdfIndirectSources.Emission | (direct ? SdfIndirectSources.Direct : 0),
                IndirectGains = (SdfIndirectGains.One with { Lights = gain }),
            };
            var gpu = new FakeGpuDevice();
            using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
                frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
                name: "zero-contribution", pipelines: SdfTestPipelines.Cache(), width: Extent);
            var context = ContextOf(gpu: gpu);

            residency.ProduceFirstFrame(context: context);
            var cache = residency.Tables!.Indirect!;

            TestLiveness.Within(frames: 128, step: () => {
                residency.BeginFrame();
                Assert.True(condition: residency.Prepare(context: context));
                _ = residency.Submit(context: context);
                if (cache.ShadeCount > 0) { return true; }
                cache.Submitted();
                return false;
            }, building: () => false,
                reason: () => $"No shade batch for {tier}, direct={direct}, gain={gain}; place={cache.PlaceCount}, classify={cache.ClassifyCount}, trace={cache.TraceCount}.");
            var firstLevel = cache.Snapshot().Bricks.GroupBy(keySelector: brick => brick.Key.Level)
                .OrderByDescending(keySelector: level => level.Key).First();
            var probes = (firstLevel.Count() * SdfIndirectLayout.ProbesPerBrick);
            var fallbackBudget = SdfIndirectWork.ShadeProbeBudget(layout: cache.Layout, lights: source.Lights);
            var queryBudget = ((direct && (gain != 0f)) ? fallbackBudget : cache.Layout.ShadeBudget);
            var perProbeCost = (SdfIndirectCost.EstimateCost(SdfIndirectCost.ShadeQueries(cache.Layout, source), source.Program.InstructionCount)
                + SdfIndirectCost.ShadeCacheCost(layout: cache.Layout));
            var budget = Math.Min(val1: queryBudget, val2: ((int)(SdfIndirectCost.SubmissionCostLimit / perProbeCost)));

            Assert.True(condition: (probes > fallbackBudget), userMessage: "The first shade level must distinguish full admission from fallback admission.");
            Assert.Equal(expected: Math.Min(val1: probes, val2: budget), actual: cache.ShadeCount);
            Assert.InRange((cache.ShadeCount * perProbeCost), 1, SdfIndirectCost.SubmissionCostLimit);
            Assert.NotNull(@object: cache.LightingSource);
        }
    }
}

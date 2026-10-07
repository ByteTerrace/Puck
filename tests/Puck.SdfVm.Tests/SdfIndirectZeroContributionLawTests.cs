using System.Numerics;

using Puck.SignedDistance;
using Puck.Testing;

using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfIndirectZeroContributionLawTests {
    [Fact]
    public void ExactlyZeroContributionOmitsVisibilityWithoutChangingTheSourceFold() {
        Vector3[] reflectances = [Vector3.Zero, Vector3.One, new(x: 0f, y: 0.25f, z: 0f), new(x: float.Epsilon, y: 0f, z: 0f)];
        var emission = new Vector3(x: 0.125f, y: 0.25f, z: 0.5f);
        var previous = new Vector3(x: 0.5f, y: 0.25f, z: 0.125f);
        const float Attenuation = 0.5f;

        foreach (var direct in new[] { false, true }) {
            foreach (var gain in new[] { 0f, float.Epsilon, 0.5f, 1f }) {
                foreach (var reflected in reflectances) {
                    foreach (var visibility in new[] { 0f, 0.25f, 1f }) {
                        var needed = (direct && (gain != 0f) && (reflected != Vector3.Zero));
                        var original = (direct ? ((reflected * visibility) * (Attenuation * gain)) : Vector3.Zero);
                        var selected = (direct ? ((reflected * (needed ? visibility : 1f)) * (Attenuation * gain)) : Vector3.Zero);
                        var independent = (emission + ((reflected * previous) * Attenuation));

                        Assert.Equal(actual: (selected + independent), expected: (original + independent));
                        if (direct && (gain == 1f) && (reflected.X == float.Epsilon)) { Assert.True(condition: needed); }
                    }
                }
            }
        }
        var shader = File.ReadAllText(path: RepositoryPaths.Resolve(
            relativePath: "src/Puck.SdfVm/Assets/Shaders/Sdf/indirect/sdf-indirect-shade.hlsli"));
        const string Guard = "if ((passGroup.indirectSources & SdfIndirectSourcesDirect) != 0u && passGroup.indirectSourceGains.x != 0.0 && any(reflected != 0.0)) {";
        var guard = shader.IndexOf(comparisonType: StringComparison.Ordinal, value: Guard);

        Assert.True(condition: (guard >= 0));
        var end = shader.IndexOf(startIndex: guard, value: '}');
        var call = shader.IndexOf(comparisonType: StringComparison.Ordinal, value: "sdfIndirectDiffuseVisibilities(");

        Assert.InRange(actual: call, high: end, low: guard);
        Assert.Contains(actualString: shader[..guard], expectedSubstring: "surface.shadowVisibility = 1.0;");
        Assert.Contains(actualString: shader[..guard], expectedSubstring: "surface.incomingVisibility = 1.0;");
    }
}
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

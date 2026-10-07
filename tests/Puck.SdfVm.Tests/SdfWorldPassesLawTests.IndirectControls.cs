using System.Numerics;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData(SdfIndirectTier.Medium, 4, 3)]
    [InlineData(SdfIndirectTier.High, 0, 1)]
    [InlineData(SdfIndirectTier.High, 2, 3)]
    [Theory]
    public void IndirectControlsCapFiniteDepthAndKeepApplyOutsideThePinnedSolve(SdfIndirectTier tier, int bounces, int sweeps) {
        var gpu = new FakeGpuDevice();
        var source = Frame() with {
            FarDistance = 12f,
            IndirectTier = tier,
            IndirectBounces = bounces,
            IndirectSources = SdfIndirectSources.Direct | SdfIndirectSources.Emission | SdfIndirectSources.Feedback,
        };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "indirect-controls", pipelines: SdfTestPipelines.Cache(), width: Extent);
        var context = ContextOf(gpu: gpu);

        residency.ProduceFirstFrame(context: context);
        var cache = residency.Tables!.Indirect!;

        void Produce() {
            residency.BeginFrame();
            Assert.True(condition: residency.Prepare(context: context));
            _ = residency.Submit(context: context);
            cache.Submitted(); cache.SubmittedLighting();
        }
        void Complete() => TestLiveness.Within(frames: IndirectCompletionFrames(cache: cache, source: source), step: () => {
            Produce(); return (cache.IsComplete && cache.LightingComplete);
        }, building: () => false, reason: () => "The finite indirect control solve did not complete.");
        Complete();
        Assert.Equal(sweeps, cache.PublishedSweeps);
        var held = cache.PublishedLightingSource;

        Assert.NotNull(@object: held);
        var certificate = cache.CertificateRevision;

        source = source with { IndirectApply = new(.25f, Vector3.UnitX, 0f) };
        Produce();
        Assert.Same(held, cache.PublishedLightingSource);
        Assert.True(condition: cache.LightingComplete);
        source = source with { IndirectGains = source.IndirectGains with { Lights = .5f } };
        Complete();
        Assert.NotSame(held, cache.PublishedLightingSource);
        Assert.Equal(.5f, cache.PublishedLightingSource!.Gains.Lights);
        Assert.Equal(1f, held.Gains.Lights);
        Assert.Equal(sweeps, cache.PublishedSweeps);
        Assert.Equal(certificate, cache.CertificateRevision);
    }
}

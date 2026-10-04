using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void IndirectReadinessRequiresTheCurrentSourceAndItsExactCompletedPublication() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { FarDistance = 12f, IndirectTier = SdfIndirectTier.Medium, EnableCadenceGate = true };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(() => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "world", pipelines: pipelines, width: Extent);
        var views = new SdfWorldPasses(_ => new SdfWorldView(residency, 0));
        using var indirect = new SdfIndirectPasses(views);
        indirect.Register(residency.IndirectInstanceName, residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);
        packages.Register(package: RenderGraphPackageCatalog.SdfWorld, factory: views);
        packages.Register(package: RenderGraphPackageCatalog.Indirect, factory: indirect);
        var context = ContextOf(gpu);
        Assert.False(residency.IsIndirectReady);
        residency.ProduceFirstFrame(context);
        Assert.True(RenderGraphInstanceSet.TryCreate([
            new RenderGraphInstance(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new RenderGraphInstance(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), setRefusal?.Message);
        Assert.True(RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[2], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), refusal?.Message);
        using var graph = runtime;
        var frame = 0L;
        void Produce(bool complete) {
            if (complete) {
                foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
            }
            var scheduled = new RenderGraphFrame(DisplayHeight: (int)Extent, DisplayHertz: 60, DisplayWidth: (int)Extent,
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);
            _ = graph.ProduceFrame(frame: scheduled, context: context);
            graph.Node(0).PollReadbacks();
        }
        void Until(Func<bool> condition, bool complete) => TestLiveness.Within(frames: 64, step: () => {
            Produce(complete);
            return condition();
        }, building: () => graph.Node(0).IsBuildingCandidate || graph.Node(1).IsBuildingCandidate, reason: () => graph.Render.Reason);

        Until(() => residency.Tables?.Indirect is { LightingComplete: true } && graph.Render.Completion == FrameCompletion.Rendered,
            complete: false);
        var cache = residency.Tables!.Indirect!;
        Assert.True(cache.IsComplete);
        Assert.False(residency.IsIndirectReady);
        Until(() => residency.IsIndirectReady, complete: true);
        var completed = cache.PublishedLightingSource;
        Assert.NotNull(completed);
        var certificate = cache.CertificateRevision;
        var bytes = graph.Node(0).OwnedBytes;

        // A frozen old solve is still complete and fenced, but its source is no longer the desired frame.
        residency.IndirectFrozen = true;
        source = source with { IndirectSources = SdfIndirectSources.Direct };
        Produce(complete: true);
        Assert.True(cache.LightingComplete);
        Assert.Same(completed, cache.PublishedLightingSource);
        Assert.False(residency.IsIndirectReady);
        Assert.Equal(certificate, cache.CertificateRevision);
        residency.IndirectFrozen = false;
        Until(() => cache.LightingComplete && !ReferenceEquals(completed, cache.PublishedLightingSource), complete: false);
        Assert.False(residency.IsIndirectReady);
        Until(() => residency.IsIndirectReady, complete: true);
        Assert.Equal(certificate, cache.CertificateRevision);
        Assert.Equal(bytes, graph.Node(0).OwnedBytes);

        // Neither the queued reset nor an old signaled slot may stand for the replacement epoch's publication.
        var epoch = cache.Epoch;
        residency.RequestIndirectReset();
        Assert.False(residency.IsIndirectReady);
        Produce(complete: true);
        Assert.NotEqual(epoch, cache.Epoch);
        Assert.False(residency.IsIndirectReady);
        Until(() => cache.LightingComplete, complete: false);
        Assert.False(residency.IsIndirectReady);
        Until(() => residency.IsIndirectReady, complete: true);
        Assert.Equal(bytes, graph.Node(0).OwnedBytes);
    }
}

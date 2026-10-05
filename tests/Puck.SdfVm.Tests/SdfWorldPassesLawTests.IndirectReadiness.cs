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
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "world", pipelines: pipelines, width: Extent);
        var views = new SdfWorldPasses(_ => new SdfWorldView(Residency: residency, View: 0));
        using var indirect = new SdfIndirectPasses(views: views);

        indirect.Register(name: residency.IndirectInstanceName, residency: residency);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        packages.Register(factory: indirect, package: RenderGraphPackageCatalog.Indirect);
        var context = ContextOf(gpu: gpu);

        Assert.False(condition: residency.IsIndirectReady);
        residency.ProduceFirstFrame(context: context);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate([
            new RenderGraphInstance(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new RenderGraphInstance(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: indirect.FragmentOf(instance: residency.IndirectInstanceName)!.Passes.Count, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[2], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var frame = 0L;

        void Produce(bool complete) {
            if (complete) {
                foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
            }
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
            graph.Node(instance: 0).PollReadbacks();
        }
        void Until(Func<bool> condition, bool complete) => TestLiveness.Within(frames: 64, step: () => {
            Produce(complete: complete);
            return condition();
        }, building: () => (graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate), reason: () => graph.Render.Reason);

        Until(() => ((residency.Tables?.Indirect is { LightingComplete: true }) && (graph.Render.Completion == FrameCompletion.Rendered)),
            complete: false);
        var cache = residency.Tables!.Indirect!;

        Assert.True(condition: cache.IsComplete);
        Assert.False(condition: residency.IsIndirectReady);
        Until(() => residency.IsIndirectReady, complete: true);
        var completed = cache.PublishedLightingSource;

        Assert.NotNull(@object: completed);
        var certificate = cache.CertificateRevision;
        var bytes = graph.Node(instance: 0).OwnedBytes;

        // A frozen old solve is still complete and fenced, but its source is no longer the desired frame.
        residency.IndirectFrozen = true;
        source = source with { IndirectSources = SdfIndirectSources.Direct };
        Produce(complete: true);
        Assert.True(condition: cache.LightingComplete);
        Assert.Same(completed, cache.PublishedLightingSource);
        Assert.False(condition: residency.IsIndirectReady);
        Assert.Equal(certificate, cache.CertificateRevision);
        residency.IndirectFrozen = false;
        Until(() => (cache.LightingComplete && !ReferenceEquals(objA: completed, objB: cache.PublishedLightingSource)), complete: false);
        Assert.False(condition: residency.IsIndirectReady);
        Until(() => residency.IsIndirectReady, complete: true);
        Assert.Equal(certificate, cache.CertificateRevision);
        Assert.Equal(bytes, graph.Node(instance: 0).OwnedBytes);

        // Neither the queued reset nor an old signaled slot may stand for the replacement epoch's publication.
        var epoch = cache.Epoch;

        residency.RequestIndirectReset();
        Assert.False(condition: residency.IsIndirectReady);
        Produce(complete: true);
        Assert.NotEqual(epoch, cache.Epoch);
        Assert.False(condition: residency.IsIndirectReady);
        Until(() => cache.LightingComplete, complete: false);
        Assert.False(condition: residency.IsIndirectReady);
        Until(() => residency.IsIndirectReady, complete: true);
        Assert.Equal(bytes, graph.Node(instance: 0).OwnedBytes);
    }
}

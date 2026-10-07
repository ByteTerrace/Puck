using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void CapturingOneResidencyDoesNotFreezeAnUnrelatedHeldOrLaterResolvedSource() {
        var gpu = new FakeGpuDevice();
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { IndirectTier = SdfIndirectTier.Off };
        var ownCaptures = 0;
        var unrelatedCaptures = 0;
        var laterCaptures = 0;
        using var own = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => { ownCaptures++; return source; }),
            SdfTestPipelines.Kernels(), "own", Extent, Extent, brickPoolVoxelCapacity: 0);
        using var unrelated = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => { unrelatedCaptures++; return source; }),
            SdfTestPipelines.Kernels(), "unrelated", Extent, Extent, brickPoolVoxelCapacity: 0);
        using var later = new SdfWorldResidency(pipelines, new CapturingFrameSource(capture: () => { laterCaptures++; return source; }),
            SdfTestPipelines.Kernels(), "later", Extent, Extent, brickPoolVoxelCapacity: 0);
        var views = new SdfWorldPasses(name => new SdfWorldView(Residency: name switch { "own" => own, "later" => later, _ => unrelated }, View: 0));
        var context = ContextOf(gpu: gpu);

        _ = views.ViewOf(instance: "own");
        _ = views.ViewOf(instance: "unrelated");
        own.ProduceFirstFrame(context: context);
        unrelated.ProduceFirstFrame(context: context);
        views.BeginConvergence(instance: "own", convergence: new RenderGraphConvergence(request: new FrameCaptureRequest("unused-scoped-capture.png")));
        views.BeginFrame(context: context);
        _ = views.ViewOf(instance: "later");
        later.ProduceFirstFrame(context: context);
        var ownBefore = ownCaptures;
        var unrelatedBefore = unrelatedCaptures;
        var laterBefore = laterCaptures;

        for (var frame = 0; (frame < 3); frame++) { views.BeginFrame(context: context); }
        Assert.Equal(actual: ownCaptures, expected: ownBefore);
        Assert.Equal(actual: unrelatedCaptures, expected: (unrelatedBefore + 3));
        Assert.Equal(actual: laterCaptures, expected: (laterBefore + 3));
    }
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, true, true)]
    [Theory]
    public void CaptureReusesOnlyAnExactFencedFiniteLightingAnswer(bool completeFence, bool changeSource, bool taintedSource, bool frozenZeroReaders) {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { FarDistance = 12f, IndirectTier = SdfIndirectTier.Medium, EnableCadenceGate = true };

        if (taintedSource) {
            source = source with { IndirectSources = SdfIndirectSources.Sky };
            _ = source.Sky.Add(new SdfSkyPanorama { Intensity = 1f, Screen = 0 }, "capture-panorama", visibility: SdfSkyVisibility.Lighting);
        }
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "world", pipelines: pipelines, width: Extent,
            screenSources: (taintedSource ? new ClosureScreens("panorama-feed", emits: false) : null));
        var views = new SdfWorldPasses(_ => new SdfWorldView(Residency: residency, View: 0));
        using var indirect = new SdfIndirectPasses(views: views);
        using var environment = new SdfSkyEnvironmentPasses(views: views);
        using var panorama = new ClosurePanoramaFeed(gpu: gpu);

        indirect.Register(name: residency.IndirectInstanceName, residency: residency);
        if (taintedSource) { environment.Register(name: "environment", residency: residency, view: 0); }
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        packages.Register(factory: indirect, package: RenderGraphPackageCatalog.Indirect);
        packages.Register(factory: environment, package: RenderGraphPackageCatalog.SkyEnvironment);
        packages.RegisterProducer(factory: _ => panorama, package: "capture-panorama");
        var context = ContextOf(gpu: gpu);

        residency.ProduceFirstFrame(context: context);
        List<RenderGraphInstance> instances = [
            new(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer),
                    .. (taintedSource ? new RenderGraphRead[] { new("environment", Kind: ShaderPipelineResourceKind.Buffer), new("panorama-feed") } : [])],
                Refresh: RenderGraphRefresh.EveryFrame),
            new(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: indirect.FragmentOf(instance: residency.IndirectInstanceName)!.Passes.Count,
                Reads: (taintedSource ? [new("environment", Kind: ShaderPipelineResourceKind.Buffer)] : []), Refresh: RenderGraphRefresh.EveryFrame),
        ];

        if (taintedSource) {
            instances.Add(item: new(Name: "environment", ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment,
                Output: ShaderPipelineResourceKind.Buffer, Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count,
                Reads: [new("panorama-feed")], Refresh: RenderGraphRefresh.EveryFrame));
            instances.Add(item: new(Name: "panorama-feed", ExternalPackage: "capture-panorama", Passes: 1, Reads: [], Refresh: RenderGraphRefresh.EveryFrame));
        }
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances, out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[instances.Count], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var frame = 0L;

        void Produce() {
            if (completeFence) { foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; } }
            _ = graph.ProduceFrame(context: context, frame: new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60,
                DisplayWidth: ((int)Extent), Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1),
                    .. (taintedSource ? new RenderGraphRoot[] { new(Height: 1, Instance: "panorama-feed", Width: 1) } : [])], Tick: frame++));
            graph.Node(instance: 0).PollReadbacks();
        }
        TestLiveness.Within(frames: IndirectCompletionFrames(cache: residency.Tables!.Indirect!, source: source), step: () => {
            Produce();
            return (completeFence ? residency.IsIndirectReady : (residency.Tables!.Indirect!.LightingComplete && graph.Render.IsRendered));
        }, building: () => Enumerable.Range(count: (taintedSource ? 3 : 2), start: 0).Any(predicate: index => graph.Node(instance: index).IsBuildingCandidate), reason: () => graph.Render.Reason);
        var cache = residency.Tables!.Indirect!;
        var sourceBefore = cache.PublishedLightingSource;
        var stamp = cache.PublishedStamp;
        var certificates = cache.CertificateRevision;
        var allocation = cache.History.Allocation;
        var work = SdfIndirectWork.Kinds.Select(selector: kind => {
            Assert.True(condition: residency.IndirectWork.TryRead(kind: kind, value: out var value));
            return value;
        }).ToArray();

        Assert.NotNull(@object: sourceBefore);
        Assert.Equal(taintedSource, sourceBefore.Tainted);
        Assert.NotEqual(actual: stamp, expected: 0u);
        if (frozenZeroReaders) {
            residency.IndirectFrozen = true;
            panorama.Filling = true;
            gpu.WriteReadback = (name, bytes) => { if (name.Detail == "indirect-deferred") { bytes.Clear(); } };
            TestLiveness.Within(frames: 16, step: () => {
                Produce();
                return (!views.HasIndirectReaders(instance: "world") && !views.TaintedOf(instance: "world"));
            }, building: () => false, reason: () => "Clean zero-reader images retain unused cache taint.");
            Assert.Same(sourceBefore, cache.PublishedLightingSource);
            views.BeginConvergence(instance: "world", convergence: new RenderGraphConvergence(request: new FrameCaptureRequest("unused-frozen-zero-capture.png")));
            Produce();
            Assert.Same(sourceBefore, cache.PublishedLightingSource);
            Assert.True(condition: cache.PublishedLightingSource!.Tainted);
            Assert.False(condition: views.TaintedOf(instance: "world"));
            Assert.True(condition: views.CaptureReadinessOf(instance: "world").IsRendered);
            return;
        }
        if (changeSource) { source = source with { IndirectSources = SdfIndirectSources.Direct }; }
        views.BeginConvergence(instance: "world", convergence: new RenderGraphConvergence(request: new FrameCaptureRequest("unused-finite-capture.png")));
        views.BeginFrame(context: context);
        Assert.Equal(allocation, cache.History.Allocation);
        Assert.Equal(certificates, cache.CertificateRevision);
        if (completeFence && !changeSource && !taintedSource) {
            Assert.Same(sourceBefore, cache.PublishedLightingSource);
            Assert.Equal(stamp, cache.PublishedStamp);
            Assert.True(condition: residency.IsIndirectReady);
            Produce();
            Assert.Same(sourceBefore, cache.PublishedLightingSource);
            Assert.Equal(stamp, cache.PublishedStamp);
            Assert.Equal(work, SdfIndirectWork.Kinds.Select(selector: kind => {
                Assert.True(condition: residency.IndirectWork.TryRead(kind: kind, value: out var value));
                return value;
            }).ToArray());
        } else {
            Assert.Null(@object: cache.PublishedLightingSource);
            Assert.Equal(0u, cache.PublishedStamp);
            Assert.False(condition: residency.IsIndirectReady);
            Assert.False(condition: views.CaptureReadinessOf(instance: "world").IsRendered);
        }
    }
}

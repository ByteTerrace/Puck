using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void AFiniteSolvePinsEnvironmentBuffersAndKeepsTheirPublicationsThroughLiveRefresh() {
        var gpu = new FakeGpuDevice(holdFences: true, trackObjects: true);
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with {
            FarDistance = 12f,
            IndirectTier = SdfIndirectTier.Medium,
            IndirectSources = SdfIndirectSources.Direct | SdfIndirectSources.Sky | SdfIndirectSources.Screens,
            EnableCadenceGate = true,
        };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(capture: () => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "world", pipelines: pipelines, width: Extent);
        var views = new SdfWorldPasses(_ => new SdfWorldView(Residency: residency, View: 0));
        using var indirect = new SdfIndirectPasses(views: views);
        using var environment = new SdfSkyEnvironmentPasses(views: views);

        indirect.Register(name: residency.IndirectInstanceName, residency: residency);
        environment.Register(name: "environment", residency: residency, view: 0);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(factory: views, package: RenderGraphPackageCatalog.SdfWorld);
        packages.Register(factory: indirect, package: RenderGraphPackageCatalog.Indirect);
        packages.Register(factory: environment, package: RenderGraphPackageCatalog.SkyEnvironment);
        var context = ContextOf(gpu: gpu);

        residency.ProduceFirstFrame(context: context);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate([
            new(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer),
                    new("environment", Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 5,
                Reads: [new("environment", Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new(Name: "environment", ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment,
                Output: ShaderPipelineResourceKind.Buffer, Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[3], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var copies = new List<ulong>();

        gpu.OnBufferCopy = (_, bytes) => copies.Add(item: bytes);
        var frame = 0L;

        void Produce(bool complete) {
            if (complete) { foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; } }
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
            graph.Node(instance: 0).PollReadbacks();
        }
        void Until(Func<bool> condition, bool complete = false) => TestLiveness.Within(frames: 96, step: () => {
            Produce(complete: complete);
            return condition();
        }, building: () => Enumerable.Range(count: 3, start: 0).Any(predicate: index => graph.Node(instance: index).IsBuildingCandidate), reason: () => graph.Render.Reason);

        Until(() => (residency.Tables?.Indirect?.LightingSource?.Environment.IsKnown == true));
        var tables = residency.Tables!;
        var cache = tables.Indirect!;
        var first = cache.LightingSource!;
        var publication = tables.SubmittedSkyEnvironment!.Value.Publication;

        Assert.Same(tables, first.Environment.Owner);
        Assert.Equal(publication, first.Environment);
        var screenPublication = tables.SubmittedScreenEmission!.Value.Publication;

        Assert.True(condition: screenPublication.IsKnown);
        Assert.NotSame(publication.Owner, screenPublication.Owner);
        Assert.Equal(screenPublication, first.Screens);
        Assert.False(condition: first.Tainted);
        Assert.Single(collection: copies, predicate: bytes => (bytes == ((ulong)SdfSkyEnvironment.MapBytes)));
        Assert.Contains(collection: copies, expected: ((ulong)SdfSkyEnvironment.CoefficientBytes));
        Assert.Single(collection: copies, predicate: bytes => (bytes == ((ulong)SdfScreenEmission.Bytes)));
        var heldBytes = tables.IndirectBytes;

        // The frame's transport and authored lights do not change. The live environment alone refreshes during a
        // finite solve; its new publication cannot relabel the source or replace either held buffer mid-sweep.
        source.Sky.First<SdfSkyGradient>().SetStop(index: 1, elevation: 1f, color: new Vector3(x: .8f, y: .2f, z: .1f));
        source = source with { };
        Produce(complete: false);
        Assert.NotEqual(publication, tables.SubmittedSkyEnvironment!.Value.Publication);
        Assert.Equal(screenPublication, tables.SubmittedScreenEmission!.Value.Publication);
        Assert.Same(first, cache.LightingSource);
        Assert.False(condition: residency.IsIndirectReady);
        Assert.Single(collection: copies, predicate: bytes => (bytes == ((ulong)SdfSkyEnvironment.MapBytes)));
        Until(() => cache.LightingComplete);
        Assert.Same(first, cache.PublishedLightingSource);
        Assert.Equal(publication, cache.PublishedLightingSource!.Environment);
        Assert.Equal(screenPublication, cache.PublishedLightingSource.Screens);

        Until(() => ((cache.LightingSource is { } current) && !ReferenceEquals(objA: first, objB: current)));
        var second = cache.LightingSource!;

        Assert.Equal(tables.SubmittedSkyEnvironment!.Value.Publication, second.Environment);
        Assert.Equal(screenPublication, second.Screens);
        Assert.Same(first, cache.PublishedLightingSource);
        Assert.False(condition: residency.IsIndirectReady);
        Assert.Equal(2, copies.Count(predicate: bytes => (bytes == ((ulong)SdfSkyEnvironment.MapBytes))));
        Assert.Equal(2, copies.Count(predicate: bytes => (bytes == ((ulong)SdfScreenEmission.Bytes))));
        Assert.Equal(heldBytes, tables.IndirectBytes);
        Until(() => residency.IsIndirectReady, complete: true);
        Assert.Same(second, cache.PublishedLightingSource);
        Assert.Equal(2, copies.Count(predicate: bytes => (bytes == ((ulong)SdfSkyEnvironment.MapBytes))));

        // A capture must solve its source from cold radiance even when the prior completed lighting is current.
        // Its transport and allocation remain reusable: only a newly completed source may release the capture gate.
        var transport = cache.Buffer;
        var allocation = cache.History.Allocation;
        var capture = new RenderGraphConvergence(request: new FrameCaptureRequest("unused-cold-capture.png"));

        views.BeginConvergence(convergence: capture, instance: "world");
        Assert.False(condition: residency.IsIndirectReady);
        views.BeginFrame(context: context);
        Assert.True(condition: cache.IsComplete);
        Assert.Same(transport, cache.Buffer);
        Assert.Equal(allocation, cache.History.Allocation);
        Assert.Equal(0u, cache.PublishedStamp);
        Assert.Null(@object: cache.PublishedLightingSource);
        Assert.False(condition: views.CaptureReadinessOf(instance: "world").IsRendered);
        Until(() => residency.IsIndirectReady, complete: true);
        Assert.NotSame(second, cache.PublishedLightingSource);
        Assert.True(condition: (cache.PublishedLightingSource!.Sequence > second.Sequence));
        Assert.Equal(3, copies.Count(predicate: bytes => (bytes == ((ulong)SdfSkyEnvironment.MapBytes))));
        Assert.Equal(3, copies.Count(predicate: bytes => (bytes == ((ulong)SdfScreenEmission.Bytes))));
        Assert.Same(transport, cache.Buffer);
        Assert.True(condition: views.CaptureReadinessOf(instance: "world").IsRendered);
    }
}

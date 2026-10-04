using System.Numerics;
using Puck.Abstractions.Gpu;
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
        var source = Frame() with { FarDistance = 12f, IndirectTier = SdfIndirectTier.Medium,
            IndirectSources = SdfIndirectSources.Direct | SdfIndirectSources.Sky | SdfIndirectSources.Screens, EnableCadenceGate = true };
        using var residency = new SdfWorldResidency(brickPoolVoxelCapacity: 0,
            frameSource: new CapturingFrameSource(() => source), height: Extent, kernels: SdfTestPipelines.Kernels(),
            name: "world", pipelines: pipelines, width: Extent);
        var views = new SdfWorldPasses(_ => new SdfWorldView(residency, 0));
        using var indirect = new SdfIndirectPasses(views);
        using var environment = new SdfSkyEnvironmentPasses(views);
        indirect.Register(residency.IndirectInstanceName, residency);
        environment.Register("environment", residency, 0);
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);
        packages.Register(package: RenderGraphPackageCatalog.SdfWorld, factory: views);
        packages.Register(package: RenderGraphPackageCatalog.Indirect, factory: indirect);
        packages.Register(package: RenderGraphPackageCatalog.SkyEnvironment, factory: environment);
        var context = ContextOf(gpu);
        residency.ProduceFirstFrame(context);
        Assert.True(RenderGraphInstanceSet.TryCreate([
            new(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer),
                    new("environment", Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 5,
                Reads: [new("environment", Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new(Name: "environment", ExternalPackage: RenderGraphPackageCatalog.SkyEnvironment,
                Output: ShaderPipelineResourceKind.Buffer, Passes: SdfSkyEnvironmentGraph.Fragment.Passes.Count, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), setRefusal?.Message);
        Assert.True(RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[3], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), refusal?.Message);
        using var graph = runtime;
        var copies = new List<ulong>();
        gpu.OnBufferCopy = (_, bytes) => copies.Add(bytes);
        var frame = 0L;
        void Produce(bool complete) {
            if (complete) { foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; } }
            var scheduled = new RenderGraphFrame(DisplayHeight: (int)Extent, DisplayHertz: 60, DisplayWidth: (int)Extent,
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);
            _ = graph.ProduceFrame(frame: scheduled, context: context);
            graph.Node(0).PollReadbacks();
        }
        void Until(Func<bool> condition, bool complete = false) => TestLiveness.Within(frames: 96, step: () => {
            Produce(complete);
            return condition();
        }, building: () => Enumerable.Range(0, 3).Any(index => graph.Node(index).IsBuildingCandidate), reason: () => graph.Render.Reason);

        Until(() => residency.Tables?.Indirect?.LightingSource?.Environment.IsKnown == true);
        var tables = residency.Tables!;
        var cache = tables.Indirect!;
        var first = cache.LightingSource!;
        var publication = tables.SubmittedSkyEnvironment!.Value.Publication;
        Assert.Same(tables, first.Environment.Owner);
        Assert.Equal(publication, first.Environment);
        var screenPublication = tables.SubmittedScreenEmission!.Value.Publication;
        Assert.True(screenPublication.IsKnown);
        Assert.NotSame(publication.Owner, screenPublication.Owner);
        Assert.Equal(screenPublication, first.Screens);
        Assert.False(first.Tainted);
        Assert.Single(copies, bytes => bytes == (ulong)SdfSkyEnvironment.MapBytes);
        Assert.Contains((ulong)SdfSkyEnvironment.CoefficientBytes, copies);
        Assert.Single(copies, bytes => bytes == (ulong)SdfScreenEmission.Bytes);
        var heldBytes = tables.IndirectBytes;

        // The frame's transport and authored lights do not change. The live environment alone refreshes during a
        // finite solve; its new publication cannot relabel the source or replace either held buffer mid-sweep.
        source.Sky.First<SdfSkyGradient>().SetStop(index: 1, elevation: 1f, color: new Vector3(.8f, .2f, .1f));
        source = source with { };
        Produce(complete: false);
        Assert.NotEqual(publication, tables.SubmittedSkyEnvironment!.Value.Publication);
        Assert.Equal(screenPublication, tables.SubmittedScreenEmission!.Value.Publication);
        Assert.Same(first, cache.LightingSource);
        Assert.False(residency.IsIndirectReady);
        Assert.Single(copies, bytes => bytes == (ulong)SdfSkyEnvironment.MapBytes);
        Until(() => cache.LightingComplete);
        Assert.Same(first, cache.PublishedLightingSource);
        Assert.Equal(publication, cache.PublishedLightingSource!.Environment);
        Assert.Equal(screenPublication, cache.PublishedLightingSource.Screens);

        Until(() => cache.LightingSource is { } current && !ReferenceEquals(first, current));
        var second = cache.LightingSource!;
        Assert.Equal(tables.SubmittedSkyEnvironment!.Value.Publication, second.Environment);
        Assert.Equal(screenPublication, second.Screens);
        Assert.Same(first, cache.PublishedLightingSource);
        Assert.False(residency.IsIndirectReady);
        Assert.Equal(2, copies.Count(bytes => bytes == (ulong)SdfSkyEnvironment.MapBytes));
        Assert.Equal(2, copies.Count(bytes => bytes == (ulong)SdfScreenEmission.Bytes));
        Assert.Equal(heldBytes, tables.IndirectBytes);
        Until(() => residency.IsIndirectReady, complete: true);
        Assert.Same(second, cache.PublishedLightingSource);
        Assert.Equal(2, copies.Count(bytes => bytes == (ulong)SdfSkyEnvironment.MapBytes));

    }
}

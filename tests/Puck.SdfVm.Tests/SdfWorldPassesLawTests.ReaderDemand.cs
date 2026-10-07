using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void OnlyAFencedZeroReaderCountForTheCurrentSurfaceParksIndirectDemand() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { FarDistance = 12f, IndirectTier = SdfIndirectTier.Medium };
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

        residency.ProduceFirstFrame(context: context);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate([
            new RenderGraphInstance(Name: "world", ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
                Passes: SdfWorldPackage.NativeFragment.Passes.Count,
                Reads: [new(residency.IndirectInstanceName, Kind: ShaderPipelineResourceKind.Buffer)], Refresh: RenderGraphRefresh.EveryFrame),
            new RenderGraphInstance(Name: residency.IndirectInstanceName, ExternalPackage: RenderGraphPackageCatalog.Indirect,
                Output: ShaderPipelineResourceKind.Buffer, Passes: 4, Reads: [], Refresh: RenderGraphRefresh.EveryFrame),
        ], out var set, out var setRefusal), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(set, new RenderGraphRuntimeGraph?[2], "world", packages,
            pipelines.Pipelines, gpu, false, out var runtime, out var refusal), userMessage: refusal?.Message);
        using var graph = runtime;
        var readers = 1u;
        var completions = 0;

        gpu.WriteReadback = (name, bytes) => {
            if (name.Detail != "indirect-deferred") { return; }
            Assert.Equal((2 * sizeof(uint)), bytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[sizeof(uint)..], value: readers);
            completions++;
        };
        var frame = 0L;

        void Produce(bool complete = true) {
            if (complete) { foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; } }
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
            graph.Node(instance: 0).PollReadbacks();
        }
        bool Demand() => views.HasIndirectReaders(instance: "world");
        void SettleZero() => TestLiveness.Within(frames: 32, step: () => {
            Produce();
            return !Demand();
        }, building: () => (graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate),
            reason: () => $"reader evidence remains demanded after {completions} callbacks: {graph.Render.Reason}");

        Assert.True(condition: Demand());
        TestLiveness.Within(frames: 32, step: () => {
            Produce();
            return (completions > 0);
        }, building: () => (graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate),
            reason: () => graph.Render.Reason);
        Assert.True(condition: Demand());
        readers = 0u;
        var observed = completions;

        Produce(complete: false);
        Assert.Equal(actual: completions, expected: observed);
        Assert.True(condition: Demand());
        SettleZero();

        var cache = residency.Tables!.Indirect!;
        var cacheFrame = cache.Frame;
        var publication = cache.PublishedStamp;
        var scheduledWork = (cache.PlaceCount, cache.ClassifyCount, cache.TraceCount, cache.ShadeCount);
        var producerSubmissions = graph.Node(instance: 1).FrameCounter;
        var submissions = gpu.Submissions;

        for (var idle = 0; (idle < 6); idle++) {
            Produce();
            Assert.False(condition: Demand());
            Assert.Equal(cacheFrame, cache.Frame);
            Assert.Equal(publication, cache.PublishedStamp);
            Assert.Equal(scheduledWork, (cache.PlaceCount, cache.ClassifyCount, cache.TraceCount, cache.ShadeCount));
            Assert.Equal(producerSubmissions, graph.Node(instance: 1).FrameCounter);
        }
        // The cadence-disabled view submits its one shared upload and one frame; the cache contributes no queue work.
        Assert.InRange(actual: (gpu.Submissions - submissions), low: 0, high: 12);

        residency.DebugMode = 17;
        Assert.True(condition: Demand());
        residency.DebugMode = 0;
        Assert.False(condition: Demand());
        var extent = views.RenderExtentOf(instance: "world")!;

        _ = extent.FrameAt(height: Extent, width: (Extent * 2));
        Assert.True(condition: Demand());
        _ = extent.FrameAt(height: Extent, width: Extent);
        Assert.False(condition: Demand());

        source = source with { FarDistance = 13f };
        Produce(complete: false);
        Assert.True(condition: Demand());
        SettleZero();
        source = source with {
            Views = [source.Views[0] with {
            Camera = CameraSnapshot.LookAt(position: source.Views[0].Camera.Position, target: new Vector3(x: .1f, y: 0f, z: 0f),
                fieldOfViewRadians: 1f, viewportWidth: Extent, viewportHeight: Extent),
        }],
        };
        Produce(complete: false);
        Assert.True(condition: Demand());
        SettleZero();
        readers = 1u;
        source = source with { Views = [source.Views[0] with { CutRevision = 1 }] };
        observed = completions;
        TestLiveness.Within(frames: 8, step: () => {
            Produce();
            return (completions > observed);
        }, building: () => false, reason: () => graph.Render.Reason);
        Assert.True(condition: Demand());
    }
}

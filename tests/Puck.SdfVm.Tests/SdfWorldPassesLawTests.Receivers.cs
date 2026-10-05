using System.Buffers.Binary;
using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void DeferredReceiversKeepOnlyTheirShadingWarmUntilTheirCompletedCountIsZero(bool cadence) {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { FarDistance = 12f, IndirectTier = SdfIndirectTier.Medium, EnableCadenceGate = cadence };
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
        uint deferred = 1;
        var completions = 0;
        gpu.WriteReadback = (_, bytes) => {
            if (bytes.Length != sizeof(uint)) { return; }
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, deferred);
            completions++;
        };
        var frame = 0L;
        bool Produce(bool complete = true) {
            if (complete) {
                foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
            }
            var before = graph.Node(0).FrameCounter;
            var scheduled = new RenderGraphFrame(DisplayHeight: (int)Extent, DisplayHertz: 60, DisplayWidth: (int)Extent,
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);
            _ = graph.ProduceFrame(frame: scheduled, context: context);
            return graph.Node(0).FrameCounter == before;
        }
        TestLiveness.Within(frames: 64, step: () => {
            _ = Produce();
            return residency.Tables?.Indirect is { LightingComplete: true } && completions > 0;
        }, building: () => graph.Node(0).IsBuildingCandidate || graph.Node(1).IsBuildingCandidate, reason: () => graph.Render.Reason);
        for (var settle = 0; settle < 4; settle++) { _ = Produce(); }
        for (var pending = 0; pending < 6; pending++) {
            Assert.False(Produce());
            graph.Node(0).PollReadbacks();
            var work = new GpuWorkSample();
            Assert.True(graph.Node(0).TryReadCompleted(work));
            var executed = Executed(work);
            Assert.Contains(SdfWorldPackage.Parts.Views, executed);
            Assert.Equal(!cadence, executed.Contains(SdfWorldPackage.Parts.Primary));
            // Shared lighting is fenced, but this view still owes real receiver work.
            Assert.True(residency.IsIndirectReady);
            Assert.False(views.CaptureReadinessOf("world").IsRendered);
        }
        var observed = completions;
        residency.IndirectFrozen = true;
        for (var settle = 0; settle < 4; settle++) { _ = Produce(); }
        Assert.Equal(cadence, Produce());
        Assert.True(completions >= observed);
        residency.IndirectFrozen = false;
        Assert.False(Produce());
        deferred = 0;
        observed = completions;
        Assert.False(Produce(complete: false));
        Assert.Equal(observed, completions);
        TestLiveness.Within(frames: 8, step: () => {
            _ = Produce();
            return views.CaptureReadinessOf("world").IsRendered;
        }, building: () => false, reason: () => graph.Render.Reason);
        if (cadence) {
            TestLiveness.Within(frames: 8, step: () => Produce(), building: () => false, reason: () => graph.Render.Reason);
        }
        observed = completions;
        var cacheFrame = residency.Tables!.Indirect!.Frame;
        for (var standing = 0; standing < 4; standing++) {
            Assert.Equal(cadence, Produce());
            Assert.True(views.CaptureReadinessOf("world").IsRendered);
        }
        if (cadence) {
            Assert.Equal(observed, completions);
            Assert.Equal(cacheFrame, residency.Tables.Indirect.Frame);
        }

        // A changed camera must not inherit the preceding sample's completed zero, even though the cache is ready.
        deferred = 1;
        source = source with { Views = [source.Views[0] with {
            Camera = CameraSnapshot.LookAt(position: source.Views[0].Camera.Position, target: new Vector3(.1f, 0f, 0f),
                fieldOfViewRadians: 1f, viewportWidth: Extent, viewportHeight: Extent),
        }] };
        Assert.False(Produce());
        Assert.False(views.CaptureReadinessOf("world").IsRendered);
        for (var pending = 0; pending < 4; pending++) { Assert.False(Produce()); }
        Assert.False(views.CaptureReadinessOf("world").IsRendered);
        deferred = 0;
        TestLiveness.Within(frames: 8, step: () => {
            _ = Produce();
            return views.CaptureReadinessOf("world").IsRendered;
        }, building: () => false, reason: () => graph.Render.Reason);
    }
}

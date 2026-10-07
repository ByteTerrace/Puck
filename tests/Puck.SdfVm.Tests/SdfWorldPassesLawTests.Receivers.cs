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
        var deferred = 1U;
        var completions = 0;

        gpu.WriteReadback = (_, bytes) => {
            if (bytes.Length != (2 * sizeof(uint))) { return; }
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: deferred);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[sizeof(uint)..], value: 1u);
            completions++;
        };
        var frame = 0L;

        bool Produce(bool complete = true) {
            if (complete) {
                foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
            }
            var before = graph.Node(instance: 0).FrameCounter;
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
            return (graph.Node(instance: 0).FrameCounter == before);
        }
        TestLiveness.Within(frames: IndirectCompletionFrames(cache: residency.Tables!.Indirect!, source: source), step: () => {
            _ = Produce();
            return ((residency.Tables?.Indirect is { LightingComplete: true }) && (completions > 0));
        }, building: () => (graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate), reason: InitialReadiness);
        string InitialReadiness() {
            var cache = residency.Tables?.Indirect;

            return ((((((string)$"cadence={cadence}; reads={completions}; render={graph.Render.Completion}: {graph.Render.Reason}; cache frame={cache?.Frame}, transport={cache?.IsComplete}, lighting={cache?.LightingComplete}, ") +
                $"stamp={cache?.PublishedStamp}, sweeps={cache?.CompletedSweeps}, publish={cache?.NeedsPublish}, ") +
                $"place={cache?.PlaceCount}, classify={cache?.ClassifyCount}, trace={cache?.TraceCount}, shade={cache?.ShadeCount}; ") +
                $"view frames={graph.Node(instance: 0).FrameCounter}, passes={graph.Node(instance: 0).Plan?.Passes.Count}, error={graph.Node(instance: 0).LastSwapError}; ") +
                $"producer frames={graph.Node(instance: 1).FrameCounter}, error={graph.Node(instance: 1).LastSwapError}");
        }
        for (var settle = 0; (settle < 4); settle++) { _ = Produce(); }
        for (var pending = 0; (pending < 6); pending++) {
            Assert.False(condition: Produce());
            graph.Node(instance: 0).PollReadbacks();
            var work = new GpuWorkSample();

            Assert.True(condition: graph.Node(instance: 0).TryReadCompleted(sample: work));
            var executed = Executed(work: work);

            Assert.Contains(collection: executed, expected: SdfWorldPackage.Parts.Views);
            Assert.Equal(!cadence, executed.Contains(value: SdfWorldPackage.Parts.Primary));
            // Shared lighting is fenced, but this view still owes real receiver work.
            Assert.True(condition: residency.IsIndirectReady);
            Assert.False(condition: views.CaptureReadinessOf(instance: "world").IsRendered);
        }
        var observed = completions;

        residency.IndirectFrozen = true;
        for (var settle = 0; (settle < 4); settle++) { _ = Produce(); }
        Assert.Equal(cadence, Produce());
        Assert.True(condition: (completions >= observed));
        residency.IndirectFrozen = false;
        Assert.False(condition: Produce());
        deferred = 0;
        observed = completions;
        Assert.False(condition: Produce(complete: false));
        Assert.Equal(actual: completions, expected: observed);
        TestLiveness.Within(frames: 8, step: () => {
            _ = Produce();
            return views.CaptureReadinessOf(instance: "world").IsRendered;
        }, building: () => false, reason: () => graph.Render.Reason);
        if (cadence) {
            TestLiveness.Within(frames: 8, step: () => Produce(), building: () => false, reason: () => graph.Render.Reason);
        }
        observed = completions;
        var cacheFrame = residency.Tables!.Indirect!.Frame;

        for (var standing = 0; (standing < 4); standing++) {
            Assert.Equal(cadence, Produce());
            Assert.True(condition: views.CaptureReadinessOf(instance: "world").IsRendered);
        }
        if (cadence) {
            Assert.Equal(actual: completions, expected: observed);
            Assert.Equal(cacheFrame, residency.Tables.Indirect.Frame);
        }

        // A changed camera must not inherit the preceding sample's completed zero, even though the cache is ready.
        deferred = 1;
        source = source with {
            Views = [source.Views[0] with {
            Camera = CameraSnapshot.LookAt(position: source.Views[0].Camera.Position, target: new Vector3(x: .1f, y: 0f, z: 0f),
                fieldOfViewRadians: 1f, viewportWidth: Extent, viewportHeight: Extent),
        }],
        };
        Assert.False(condition: Produce());
        Assert.False(condition: views.CaptureReadinessOf(instance: "world").IsRendered);
        for (var pending = 0; (pending < 4); pending++) { Assert.False(condition: Produce()); }
        Assert.False(condition: views.CaptureReadinessOf(instance: "world").IsRendered);
        deferred = 0;
        TestLiveness.Within(frames: 8, step: () => {
            _ = Produce();
            return views.CaptureReadinessOf(instance: "world").IsRendered;
        }, building: () => false, reason: () => graph.Render.Reason);
    }
}

using System.Buffers.Binary;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void AConvergingCaptureCountsEachSampleOnItsOwnRenderWhileAStillCaptureWaitsForItsCensus() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var pipelines = SdfTestPipelines.Cache();
        var source = Frame() with { FarDistance = 12f, IndirectTier = SdfIndirectTier.Medium, EnableCadenceGate = false };
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

        // Every fenced census reads deferred receivers beside one reader: a census a capture waits for never completes.
        gpu.WriteReadback = (_, bytes) => {
            if (bytes.Length != (2 * sizeof(uint))) { return; }
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: 7u);
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes[sizeof(uint)..], value: 1u);
        };
        var frame = 0L;

        void Produce() {
            foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: frame, Roots: [new(Height: 1, Instance: "world", Width: 1)], Tick: frame++);

            _ = graph.ProduceFrame(context: context, frame: scheduled);
            foreach (var fence in gpu.SubmittedFences) { fence.Completed = true; }
            graph.Node(instance: 0).PollReadbacks();
        }
        TestLiveness.Within(frames: IndirectCompletionFrames(cache: residency.Tables!.Indirect!, source: source), step: () => {
            Produce();
            return residency.IsIndirectReady;
        }, building: () => (graph.Node(instance: 0).IsBuildingCandidate || graph.Node(instance: 1).IsBuildingCandidate), reason: () => graph.Render.Reason);
        // A still capture waits for its receivers' fenced census, which here never reaches zero.
        for (var still = 0; (still < 8); still++) { Produce(); }
        Assert.False(condition: views.CaptureReadinessOf(instance: "world").IsRendered,
            userMessage: "A still capture must wait for its receivers' fenced census.");
        Assert.False(condition: residency.Tables!.Indirect!.CaptureHold, userMessage: "No capture holds the clock yet.");
        var convergence = new RenderGraphConvergence(request: new FrameCaptureRequest("unused-converging-sample.png", converge: 9));

        // A converging capture's samples admit every receiver and defer none, so each render of a sample completes its
        // receivers and counts at once: waiting a fence would render the sample again uncounted, into its history.
        views.BeginConvergence(convergence: convergence, instance: "world");
        for (var sample = 0; (sample < 8); sample++) {
            Produce();
            Assert.True(condition: views.CaptureReadinessOf(instance: "world").IsRendered,
                userMessage: $"Sample {sample}: {views.CaptureReadinessOf(instance: "world").Reason}; ready={residency.IsIndirectReady}");
            // The capture holds the clock, so its frames take the held multiple of the slice.
            Assert.True(condition: residency.Tables!.Indirect!.CaptureHold);
            convergence.Count();
        }
    }
}

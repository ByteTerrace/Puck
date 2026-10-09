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
    public void AConvergingCaptureWaitsForTheSurfaceOfItsOwnSampleEvenWhereTheJitterRepeats() {
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

        // Every fenced census reads no deferred receiver and one reader, so only the sample identity can hold a capture.
        gpu.WriteReadback = (_, bytes) => {
            if (bytes.Length != (2 * sizeof(uint))) { return; }
            BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes, value: 0u);
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
        var convergence = new RenderGraphConvergence(request: new FrameCaptureRequest("unused-converging-sample.png", converge: 9));

        views.BeginConvergence(instance: "world", convergence: convergence);
        TestLiveness.Within(frames: 16, step: () => {
            Produce();
            return views.CaptureReadinessOf(instance: "world").IsRendered;
        }, building: () => false, reason: () => $"{views.CaptureReadinessOf(instance: "world").Reason}; ready={residency.IsIndirectReady}; tainted={residency.Tables!.Indirect!.PublishedLightingSource?.Tainted}; readers={views.HasIndirectReaders(instance: "world")}");
        // Eight counted samples later the Halton sequence repeats its first offset, but the submitted surface is still
        // the first sample's: the ninth sample's receivers have not been proved.
        for (var sample = 0; (sample < 8); sample++) { convergence.Count(); }
        Assert.False(condition: views.CaptureReadinessOf(instance: "world").IsRendered,
            userMessage: "A converging capture must wait for a surface its own sample recorded, not one whose jitter matches.");
        TestLiveness.Within(frames: 16, step: () => {
            Produce();
            return views.CaptureReadinessOf(instance: "world").IsRendered;
        }, building: () => false, reason: () => views.CaptureReadinessOf(instance: "world").Reason ?? graph.Render.Reason);
    }
}

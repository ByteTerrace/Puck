using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    [Fact]
    public void FollowingAViewOrCrossingToAnotherResidencyRestartsTheSequenceWithoutARebuild() {
        var gpu = new UploadModelGpu();
        var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
        var frame = Frame();

        frame = frame with { Views = [frame.Views[0], frame.Views[0]] };
        using var first = Residency(name: "first");
        using var second = Residency(name: "second");
        var selected = first;
        var viewIndex = 0;
        var passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: selected, View: viewIndex));
        var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

        packages.Register(factory: passes, package: RenderGraphPackageCatalog.SdfWorld);
        Assert.True(condition: RenderGraphInstanceSet.TryCreate(
            instances: [new RenderGraphInstance(ExternalPackage: RenderGraphPackageCatalog.SdfWorld, Name: "world",
                Passes: SdfWorldPackage.Fragment.Passes.Count, Reads: [], Refresh: RenderGraphRefresh.EveryFrame)],
            refusal: out var setRefusal, set: out var set), userMessage: setRefusal?.Message);
        Assert.True(condition: RenderGraphRuntime.TryCreate(
            deviceContext: gpu, graphs: new RenderGraphRuntimeGraph?[1], hostsOnDirectX: false,
            packages: packages, pipelines: pipelines.Pipelines, refusal: out var refusal,
            root: "world", runtime: out var runtime, set: set), userMessage: refusal?.Message);
        using var owned = runtime;
        var context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
            Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = gpu }),
            StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);
        var index = 0L;

        Assert.True(condition: SpinWait.SpinUntil(condition: () => {
            Produce();
            return (first.IsReady && passes.HasRenderedResolvedView(instance: "world") &&
                (owned.Node(instance: 0).Extent == (Extent, Extent)) && !owned.Node(instance: 0).IsBuildingCandidate);
        }, timeout: TimeSpan.FromSeconds(value: 30)));
        var request = new FrameCaptureRequest(converge: 256, path: "unused-temporal-law.png");

        owned.RequestCapture(request: request);
        Produce();
        Expect(sample: 0);
        Produce();
        Expect(sample: 1);
        var revision = passes.CounterOf(instance: "world")!.Revision;

        viewIndex = 1;
        Produce();
        Expect(sample: 0);
        Assert.Equal(expected: revision, actual: passes.CounterOf(instance: "world")!.Revision);
        Produce();
        Expect(sample: 1);
        selected = second;
        Produce();
        Expect(sample: 0);
        Assert.Equal(expected: revision, actual: passes.CounterOf(instance: "world")!.Revision);
        Produce();
        Expect(sample: 1);
        Assert.True(condition: request.TryFail(error: new OperationCanceledException()));

        SdfWorldResidency Residency(string name) => new(
            brickPoolVoxelCapacity: 0, frameSource: new FixedFrameSource(frame: frame),
            height: Extent, kernels: SdfTestPipelines.Kernels(), name: name, pipelines: pipelines, width: Extent);

        void Produce() {
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: [], Index: index, Roots: [new RenderGraphRoot(Height: 1, Instance: "world", Width: 1)], Tick: index++);

            _ = owned.ProduceFrame(context: in context, frame: in scheduled);
        }
        void Expect(uint sample) {
            var block = gpu.Memory(bufferHandle: gpu.BufferAt(set: gpu.BoundSet(group: 3), binding: 0));
            var layout = SdfWorldInterfaces.WorldParameters;

            Assert.Equal(expected: sample, actual: BitConverter.ToUInt32(value: block,
                startIndex: ((int)layout.BlockOffsetOf(member: SdfWorldPackage.HistoryFrames))));
            var jitter = ((int)layout.BlockOffsetOf(member: SdfWorldPackage.Jitter));

            Assert.Equal(expected: SdfTemporalHistory.Sample(index: sample),
                actual: new Vector2(x: BitConverter.ToSingle(startIndex: jitter, value: block),
                    y: BitConverter.ToSingle(startIndex: (jitter + sizeof(float)), value: block)));
        }
    }
}

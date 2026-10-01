using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldPassesLawTests {
    private const string Feed = "test.feed";

    [Fact]
    public void FollowingAViewOrCrossingToAnotherResidencyRestartsTheSequenceWithoutARebuild() {
        using var rig = new TemporalRig(views: 2, secondResidency: true);
        var request = new FrameCaptureRequest(converge: 256, path: "unused-temporal-law.png");

        rig.Runtime.RequestCapture(request: request);
        rig.Produce();
        Assert.Equal(expected: 0u, actual: rig.HistoryFrames());
        rig.Produce();
        Assert.Equal(expected: 1u, actual: rig.HistoryFrames());
        var revision = rig.Passes.CounterOf(instance: "world")!.Revision;

        rig.ViewIndex = 1;
        rig.Produce();
        Assert.Equal(expected: 0u, actual: rig.HistoryFrames());
        Assert.Equal(expected: revision, actual: rig.Passes.CounterOf(instance: "world")!.Revision);
        rig.Produce();
        Assert.Equal(expected: 1u, actual: rig.HistoryFrames());
        rig.Selected = rig.Second!;
        rig.Produce();
        Assert.Equal(expected: 0u, actual: rig.HistoryFrames());
        Assert.Equal(expected: revision, actual: rig.Passes.CounterOf(instance: "world")!.Revision);
        rig.Produce();
        Assert.Equal(expected: 1u, actual: rig.HistoryFrames());
        Assert.True(condition: request.TryFail(error: new OperationCanceledException()));
    }
    // A frame the runtime does not count, because the view read a tainted image, renders the same sample again, so the
    // capture's Nth counted render, the one it serves, is at jitter index N-1 however many tainted frames came first.
    [Fact]
    public void ARenderOverATaintedReadTakesTheSameSampleAgain() {
        const int Converge = 4;
        const int Tainted = 3;
        using var rig = new TemporalRig(views: 1, feed: true);
        var request = new FrameCaptureRequest(converge: Converge, path: "unused-tainted-temporal-law.png");

        rig.Runtime.RequestCapture(request: request);
        for (var frame = 0; (frame < Tainted); frame++) {
            rig.Produce();
            Assert.Equal(expected: 0u, actual: rig.HistoryFrames());
        }
        rig.Filling = true;
        for (var sample = 0U; (sample < Converge); sample++) {
            rig.Produce();
            Assert.Equal(expected: sample, actual: rig.HistoryFrames());
        }
        _ = request.TryFail(error: new OperationCanceledException());
    }
    // Cadence never keeps a jittered sample: once the converging capture ends, a still view renders once more at the
    // pixel center and then stands.
    [Fact]
    public void AStillViewRendersOnceUnjitteredAfterItsCaptureEndsAndThenStands() {
        const int Converge = 8;
        using var rig = new TemporalRig(views: 1, cadence: true);

        rig.Produce();
        Assert.True(condition: rig.Stood());
        var request = new FrameCaptureRequest(converge: Converge, path: "unused-still-temporal-law.png");

        rig.Runtime.RequestCapture(request: request);
        for (var sample = 0U; (sample < Converge); sample++) {
            rig.Produce();
            Assert.Equal(expected: sample, actual: rig.HistoryFrames());
        }
        Assert.NotEqual(expected: Vector2.Zero, actual: rig.Jitter());
        Assert.True(condition: request.TryFail(error: new OperationCanceledException()));
        rig.Produce();
        Assert.False(condition: rig.Stood());
        Assert.Equal(expected: Vector2.Zero, actual: rig.Jitter());
        rig.Produce();
        Assert.True(condition: rig.Stood());
    }
    // The motion view reads the previous view: turned on over a still scene it renders once with none, once with the
    // stationary previous view, and only then stands.
    [Fact]
    public void TheMotionViewRendersItsStationaryMotionUnderCadenceBeforeStanding() {
        using var rig = new TemporalRig(views: 1, cadence: true);

        rig.Produce();
        Assert.True(condition: rig.Stood());
        rig.Selected.DebugMode = DebugViewModes.Motion;
        rig.Produce();
        Assert.False(condition: rig.Stood());
        Assert.False(condition: rig.PreviousValid());
        rig.Produce();
        Assert.False(condition: rig.Stood());
        Assert.True(condition: rig.PreviousValid());
        rig.Produce();
        Assert.True(condition: rig.Stood());
        Assert.True(condition: rig.PreviousValid());
    }
    // A temporal view's resolve pipeline is leased into the residency it crosses to, so its passes follow in place, with
    // no rebuild and so no frame held of the departed world, and its history restarts at the pixel center.
    [Fact]
    public void ATemporalViewCrossesToAnotherResidencyInPlace() {
        using var rig = new TemporalRig(views: 1, secondResidency: true, temporal: true);
        var revision = rig.Passes.CounterOf(instance: "world")!.Revision;

        rig.Produce();
        Assert.NotEqual(expected: 0u, actual: rig.HistoryFrames());
        rig.Selected = rig.Second!;
        rig.Produce();
        Assert.Equal(expected: revision, actual: rig.Passes.CounterOf(instance: "world")!.Revision);
        Assert.True(condition: rig.Passes.HasRenderedResolvedView(instance: "world"));
        Assert.Equal(expected: 0u, actual: rig.HistoryFrames());
        Assert.Equal(expected: Vector2.Zero, actual: rig.Jitter());
    }
    // A view that asks for temporal reconstruction runs the temporal fragment even at native scale: a resolve after views,
    // and history the next frame reads, one image and one surface a frame slot at the output extent.
    [Fact]
    public void ATemporalViewRunsTheTemporalFragmentWithHistoryAtItsOutput() {
        using var rig = new TemporalRig(views: 1, temporal: true);

        Assert.Same(expected: SdfWorldPackage.TemporalFragment, actual: rig.Passes.FragmentOf(instance: "world"));
        var plan = rig.World.Plan!;

        Assert.EndsWith(expectedEndString: $"${SdfWorldPackage.Resolve}", actualString: plan.Passes[^1].Name);
        var history = plan.Storages.Where(predicate: static storage => storage.Declaration.History).Select(selector: static storage => storage.Name).Order(comparer: StringComparer.Ordinal);

        Assert.Equal(actual: history, expected: [$"{RenderGraphPackageCatalog.SdfWorld}${SdfWorldPackage.Parts.HistoryColor}", $"{RenderGraphPackageCatalog.SdfWorld}${SdfWorldPackage.Parts.HistorySurface}"]);
        Assert.Contains(collection: plan.Passes[^1].Accesses, filter: static access => access.PreviousFrame);
        Assert.True(condition: rig.Temporal());
        // Every render advances the sequence with no capture converging.
        var frames = rig.HistoryFrames();

        rig.Produce();
        Assert.Equal(expected: (frames + 1u), actual: rig.HistoryFrames());
        Assert.NotEqual(expected: Vector2.Zero, actual: rig.Jitter());
    }
    // Under cadence a still temporal view renders one jitter period from the frame its inputs last changed, the first at
    // the pixel center with no history, and then stands converged, whatever sample would come next.
    [Fact]
    public void AStillTemporalViewRendersOnePeriodAndThenStands() {
        using var rig = new TemporalRig(views: 1, cadence: true, temporal: true);

        // A debug view resets the epoch twice: on, then off again.
        rig.Selected.DebugMode = 1;
        rig.Produce();
        rig.Selected.DebugMode = 0;
        for (var sample = 0U; (sample < SdfTemporalHistory.Period); sample++) {
            rig.Produce();
            Assert.False(condition: rig.Stood(), userMessage: $"sample {sample}");
            Assert.Equal(expected: sample, actual: rig.HistoryFrames());
            Assert.Equal(expected: SdfTemporalHistory.Sample(index: sample), actual: rig.Jitter());
        }
        for (var frame = 0; (frame < 3); frame++) {
            rig.Produce();
            Assert.True(condition: rig.Stood(), userMessage: $"frame {frame} after the period");
        }
    }

    // One sdf.world instance, "world", over a fixed frame on the upload model, optionally reading a feed that hands out a
    // tainted image until it fills. Construction produces until the view has rendered its installed graph.
    private sealed class TemporalRig : IDisposable {
        private readonly UploadModelGpu m_gpu = new();
        private readonly FrameContext m_context;
        private readonly TaintingFeed? m_feed;

        private long m_index;
        private ulong m_rendered;

        public TemporalRig(int views, bool cadence = false, bool feed = false, bool secondResidency = false, bool temporal = false) {
            var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
            var frame = Frame() with { EnableCadenceGate = cadence };

            frame = frame with { Views = [frame.Views[0] with { Quality = new SdfViewQuality { Temporal = temporal } }] };

            frame = frame with { Views = [.. Enumerable.Repeat(element: frame.Views[0], count: views)] };
            Selected = Residency(name: "first");
            Second = (secondResidency ? Residency(name: "second") : null);
            Passes = new SdfWorldPasses(resolve: _ => new SdfWorldView(Residency: Selected, View: ViewIndex));
            var packages = new RenderGraphPackageRecorders(regionCopy: pipelines.RegionCopy);

            packages.Register(factory: Passes, package: RenderGraphPackageCatalog.SdfWorld);
            RenderGraphInstance[] instances = [
                new RenderGraphInstance(ExternalPackage: RenderGraphPackageCatalog.SdfWorld, Name: "world",
                    Passes: SdfWorldPackage.Fragment.Passes.Count, Reads: (feed ? [new RenderGraphRead(Producer: "feed")] : []), Refresh: RenderGraphRefresh.EveryFrame),
            ];

            if (feed) {
                m_feed = new TaintingFeed(gpu: m_gpu);
                packages.RegisterProducer(factory: _ => m_feed, package: Feed);
                instances = [new RenderGraphInstance(ExternalPackage: Feed, Name: "feed", Passes: 1, Reads: [], Refresh: RenderGraphRefresh.EveryFrame), .. instances];
            }
            Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances: instances, refusal: out var setRefusal, set: out var set), userMessage: setRefusal?.Message);
            Assert.True(condition: RenderGraphRuntime.TryCreate(
                deviceContext: m_gpu, graphs: new RenderGraphRuntimeGraph?[instances.Length], hostsOnDirectX: false,
                packages: packages, pipelines: pipelines.Pipelines, refusal: out var refusal,
                root: "world", runtime: out var runtime, set: set), userMessage: refusal?.Message);
            Runtime = runtime!;
            m_context = new FrameContext(AccumulatorTicks: 0, DeltaTicks: 0, ElapsedTicks: 0, FrameDeltaTicks: 0,
                Host: new HostContext(capabilities: new Dictionary<Type, object> { [typeof(IGpuDeviceContext)] = m_gpu }),
                StepTicks: 0, TargetHeight: Extent, TargetWidth: Extent);
            TestLiveness.Until(step: () => {
                Produce();
                return (Selected.IsReady && Passes.HasRenderedResolvedView(instance: "world") &&
                    (World.Extent == (Extent, Extent)) && !World.IsBuildingCandidate);
            }, reason: () => Selected.NotReadyReason, wait: Selected.WaitPipelineBuilds);
            Produce();

            SdfWorldResidency Residency(string name) => new(
                brickPoolVoxelCapacity: 0, frameSource: new FixedFrameSource(frame: frame),
                height: Extent, kernels: SdfTestPipelines.Kernels(), name: name, pipelines: pipelines, width: Extent);
        }

        public bool Filling {
            set => m_feed!.Filling = value;
        }
        public SdfWorldPasses Passes { get; }
        public RenderGraphRuntime Runtime { get; }
        public SdfWorldResidency? Second { get; }
        public SdfWorldResidency Selected { get; set; }
        public int ViewIndex { get; set; }
        public ShaderPipelineRenderNode World => Runtime.Node(instance: Runtime.Instances.IndexOf(name: "world"));

        public void Produce() {
            m_rendered = World.FrameCounter;
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)Extent), DisplayHertz: 60, DisplayWidth: ((int)Extent),
                Footprints: ((m_feed is null) ? [] : [new RenderGraphFootprint(Consumer: "world", Height: 1.0, Producer: "feed", Width: 1.0)]),
                Index: m_index, Roots: [new RenderGraphRoot(Height: 1, Instance: "world", Width: 1)], Tick: m_index++);

            _ = Runtime.ProduceFrame(context: in m_context, frame: in scheduled);
        }
        // Whether the latest frame let the view's previous output stand.
        public bool Stood() => (World.FrameCounter == m_rendered);
        public uint HistoryFrames() => BitConverter.ToUInt32(value: Block(), startIndex: Offset(member: SdfWorldPackage.HistoryFrames));
        public bool Temporal() => (BitConverter.ToUInt32(value: Block(), startIndex: Offset(member: SdfWorldPackage.Temporal)) != 0u);
        public Vector2 Jitter() {
            var block = Block();
            var jitter = Offset(member: SdfWorldPackage.Jitter);

            return new Vector2(x: BitConverter.ToSingle(startIndex: jitter, value: block), y: BitConverter.ToSingle(startIndex: (jitter + sizeof(float)), value: block));
        }
        public bool PreviousValid() => (BitConverter.ToSingle(value: Block(), startIndex: (Offset(member: SdfWorldPackage.PreviousView) + (3 * sizeof(float)))) != 0f);
        public void Dispose() {
            Runtime.Dispose();
            m_feed?.Dispose();
            Selected.Dispose();
            Second?.Dispose();
        }

        // The pass block the latest recorded pass bound, which every part of a frame writes the same temporal values to.
        private byte[] Block() => m_gpu.Memory(bufferHandle: m_gpu.BufferAt(set: m_gpu.BoundSet(group: 3), binding: 0));
        private static int Offset(string member) => ((int)SdfWorldInterfaces.WorldParameters.BlockOffsetOf(member: member));
    }
    // An external producer handing out one image, tainted until it fills.
    private sealed class TaintingFeed(UploadModelGpu gpu) : IRenderGraphExternalProducer {
        private IGpuImage? m_image;

        public bool Filling { get; set; }
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => ((m_image is null) ? "the feed has not produced" : null);
        public string? PendingCapturePath => null;
        public IGpuWorkSource Work { get; } = new GpuWorkLedger(framesInFlight: 3, name: Feed);

        public void Dispose() {
            m_image?.Dispose();
            m_image = null;
        }
        public void OnDeviceLost() => Dispose();
        public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            m_image ??= gpu.Services.ImageFactory.Create(format: Format, height: height, name: default, usage: GpuImageUsage.Sampled | GpuImageUsage.Storage, width: width);

            return true;
        }
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(error: new NotSupportedException(message: "The feed is captured through the view that reads it."));
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            if (m_image is not { } image) {
                output = default;
                return false;
            }
            output = new RenderGraphExternalOutput(
                Image: Surface.SameDeviceImage(format: Format, height: image.Height, imageHandle: image.ImageHandle, imageViewHandle: image.ImageViewHandle, width: image.Width),
                Layout: GpuImageLayout.ShaderReadOnly,
                Lease: image.ImageViewHandle,
                Tainted: !Filling
            );
            return true;
        }
    }
}

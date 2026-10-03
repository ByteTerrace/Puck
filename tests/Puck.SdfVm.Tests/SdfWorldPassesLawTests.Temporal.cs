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
    // pixel center and then stands. The capture asks for one sample more than the law renders, so it is still waiting,
    // never served, when the law withdraws it.
    [Fact]
    public void AStillViewRendersOnceUnjitteredAfterItsCaptureEndsAndThenStands() {
        const int Rendered = 8;
        using var rig = new TemporalRig(views: 1, cadence: true);

        rig.Produce();
        Assert.True(condition: rig.Stood());
        var request = new FrameCaptureRequest(converge: (Rendered + 1), path: "unused-still-temporal-law.png");

        rig.Runtime.RequestCapture(request: request);
        for (var sample = 0U; (sample < Rendered); sample++) {
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
    // A reconfiguration that keeps the view leaves its still output standing: the view rendered once and renders no
    // more, whatever set the runtime runs around it.
    [Fact]
    public void AStillViewKeptByAReconfigurationRendersNoMore() {
        using var rig = new TemporalRig(views: 1, cadence: true);

        Assert.Equal(expected: 1UL, actual: rig.World.FrameCounter);
        for (var reconfiguration = 0; (reconfiguration < 3); reconfiguration++) {
            Assert.True(condition: RenderGraphInstanceSet.TryCreate(instances: [.. rig.Runtime.Instances.Instances], refusal: out _, set: out var set));
            Assert.True(condition: rig.Runtime.TryReconfigure(graphs: new RenderGraphRuntimeGraph?[set.Instances.Count], refusal: out var refusal, root: "world", set: set), userMessage: refusal?.Message);
            for (var frame = 0; (frame < 4); frame++) {
                rig.Produce();
                Assert.True(condition: rig.Stood(), userMessage: $"reconfiguration {reconfiguration}, frame {frame}");
            }
        }
        Assert.Equal(expected: 1UL, actual: rig.World.FrameCounter);

        var completed = new GpuWorkSample();

        Assert.True(condition: rig.World.TryReadCompleted(sample: completed));
        Assert.Equal(expected: 1L, actual: completed.Submission);
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
    [Fact]
    public void RebuildingAnUnnamedViewDiscardsItsPreviousCameraEvenWhenItsEpochAndPosesStayStill() {
        using var rig = new TemporalRig(views: 1, cadence: true);

        rig.Selected.DebugMode = DebugViewModes.Motion;
        rig.Produce();
        rig.Produce();
        Assert.True(condition: rig.PreviousValid());
        var revision = rig.Passes.CounterOf(instance: "world")!.Revision;

        rig.Produce(named: false);
        Assert.Equal(expected: 0UL, actual: rig.Runtime.Node(instance: 0).OwnedBytes);
        Assert.False(condition: rig.Passes.HasRenderedResolvedView(instance: "world"));
        TestLiveness.Until(step: () => {
            rig.Produce();
            return !rig.Stood();
        }, reason: () => "The released view never rendered again.");

        Assert.Equal(expected: revision, actual: rig.Passes.CounterOf(instance: "world")!.Revision);
        Assert.False(condition: rig.PreviousValid());
        Assert.Equal(expected: 0U, actual: rig.HistoryFrames());
        rig.Produce();
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
    // the sky and the composite after it, and history the next frame reads, one image and one surface a frame slot at the output extent.
    [Fact]
    public void ATemporalViewRunsTheTemporalFragmentWithHistoryAtItsOutput() {
        using var rig = new TemporalRig(views: 1, temporal: true);

        Assert.Same(expected: SdfWorldPackage.TemporalFragment, actual: rig.Passes.FragmentOf(instance: "world"));
        var plan = rig.World.Plan!;

        var resolve = plan.Passes.Single(predicate: static pass => pass.Name.EndsWith(comparisonType: StringComparison.Ordinal, value: $"${SdfWorldPackage.Resolve}"));

        Assert.EndsWith(expectedEndString: $"${SdfWorldPackage.Parts.Composite}", actualString: plan.Passes[^1].Name);
        var history = plan.Storages.Where(predicate: static storage => storage.Declaration.History).Select(selector: static storage => storage.Name).Order(comparer: StringComparer.Ordinal);

        Assert.Equal(actual: history, expected: [$"{RenderGraphPackageCatalog.SdfWorld}${SdfWorldPackage.Parts.HistoryColor}", $"{RenderGraphPackageCatalog.SdfWorld}${SdfWorldPackage.Parts.HistorySurface}"]);
        Assert.Contains(collection: resolve.Accesses, filter: static access => access.PreviousFrame);
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
    [Fact]
    public void AGridMoveSettlesForOnePeriodWithoutDiscardingHistoryOrRebuilding() {
        using var rig = new TemporalRig(views: 1, cadence: true, temporal: true, renderScale: 0.75f);

        for (var frame = 0U; (frame <= SdfTemporalHistory.Period); frame++) {
            rig.Produce();
        }
        Assert.True(condition: rig.Stood());
        var revision = rig.World.WorkRevision;

        foreach (var grid in new[] { 0.5f, 0.625f }) {
            var preceding = rig.HistoryFrames();

            rig.RenderGrid = grid;
            for (var sample = 0U; (sample < SdfTemporalHistory.Period); sample++) {
                rig.Produce();
                Assert.False(condition: rig.Stood(), userMessage: $"grid {grid}, sample {sample}");
                Assert.Equal(expected: ((preceding + sample) + 1u), actual: rig.HistoryFrames());
                Assert.True(condition: rig.PreviousValid());
                Assert.Equal(expected: revision, actual: rig.World.WorkRevision);
            }
            // The node records the quantized grid with each submission it renders, as its completed work names them.
            var completed = new GpuWorkSample();
            var node = rig.Runtime.Node(instance: 0);

            Assert.True(condition: node.TryReadCompleted(sample: completed));
            Assert.True(condition: node.TryGetRenderGrid(grid: out var recorded, submission: completed.Submission));
            Assert.Equal(expected: RenderGraphExtent.Quantize(fraction: grid), actual: recorded);
            rig.Produce();
            Assert.True(condition: rig.Stood());
            // A different requested fraction in the same quantized grid needs no new samples.
            rig.RenderGrid = (grid - 0.001f);
            rig.Produce();
            Assert.True(condition: rig.Stood());
        }
    }
    [Fact]
    public void TheCompletionSummaryNamesEveryRenderNotOnlyTheNewest() {
        using var rig = new TemporalRig(views: 1, renderScale: 0.75f);
        var node = rig.Runtime.Node(instance: 0);
        var completed = new GpuWorkSample();

        rig.RenderGrid = 0.625f;
        for (var frame = 0; (frame < 4); frame++) {
            rig.Produce();
        }
        _ = node.TakeCompletions();

        // A render at 0.5 and then renders at 0.625 complete between two reads: the newest completed submission names
        // 0.625, but the summary spans both grids and names none.
        rig.RenderGrid = 0.5f;
        rig.Produce();
        rig.RenderGrid = 0.625f;
        for (var frame = 0; (frame < 3); frame++) {
            rig.Produce();
        }
        var mixed = node.TakeCompletions();

        Assert.True(condition: node.TryReadCompleted(sample: completed));
        Assert.True(condition: node.TryGetRenderGrid(grid: out var newest, submission: completed.Submission));
        Assert.Equal(actual: newest, expected: 0.625d);
        Assert.True(condition: (mixed.Renders >= 2), userMessage: $"{mixed.Renders} renders");
        Assert.Equal(expected: 0d, actual: mixed.Grid);

        // Renders at one grid since the read name it, and a read with nothing new names nothing.
        for (var frame = 0; (frame < 4); frame++) {
            rig.Produce();
        }
        var uniform = node.TakeCompletions();

        Assert.True(condition: (uniform.Renders > 0));
        Assert.Equal(expected: 0.625d, actual: uniform.Grid);
        Assert.Equal(expected: default, actual: node.TakeCompletions());
    }
    [InlineData(16u)]
    [InlineData(128u)]
    [Theory]
    public void AResizeAllocatesTheHistorySurfaceAtTheNewOutputExtent(uint extent) {
        using var rig = new TemporalRig(views: 1, temporal: true);

        Assert.Equal(expected: (((SdfWorldPackage.HistorySurfaceWords * sizeof(uint)) * Extent) * Extent), actual: rig.HistorySurfaceBytes());
        rig.OutputExtent = extent;
        TestLiveness.Until(step: () => {
            rig.Produce();
            return ((rig.World.Extent == (extent, extent)) && !rig.World.IsBuildingCandidate);
        }, reason: () => rig.World.LastSwapError?.Message);
        Assert.Equal(expected: (((SdfWorldPackage.HistorySurfaceWords * sizeof(uint)) * extent) * extent), actual: rig.HistorySurfaceBytes());
    }
    [Fact]
    public void ARenderGridDipRendersAFullPeriodBeforeStandingAgain() {
        using var rig = new TemporalRig(views: 1, cadence: true, temporal: true, renderScale: 0.5f);

        for (var frame = 0; (frame <= SdfTemporalHistory.Period); frame++) { rig.Produce(); }
        Assert.True(condition: rig.Stood());
        foreach (var scale in new[] { 0.25f, 0.5f }) {
            rig.ResolvedScale = scale;
            for (var sample = 0U; (sample < SdfTemporalHistory.Period); sample++) {
                rig.Produce();
                Assert.False(condition: rig.Stood(), userMessage: $"scale {scale}, sample {sample}");
            }
            rig.Produce();
            Assert.True(condition: rig.Stood());
        }
    }
    // A view the display stops showing is parked: nothing renders it. Shown again, a temporal view starts a new epoch, its
    // first render at the pixel center with no history and a full period before it stands, even though its binding,
    // camera, poses and extent never moved; a spatial view's still output stands at once, costing no render.
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AParkedViewShownAgainStartsANewEpochOnlyWhereItReconstructs(bool temporal) {
        using var rig = new TemporalRig(views: 1, cadence: true, temporal: temporal);

        for (var frame = 0; (frame <= SdfTemporalHistory.Period); frame++) { rig.Produce(); }
        Assert.True(condition: rig.Stood());
        rig.Parked = true;
        for (var frame = 0; (frame < 3); frame++) {
            rig.Produce();
            Assert.True(condition: rig.Stood(), userMessage: $"parked frame {frame}");
        }
        rig.Parked = false;
        rig.Produce();
        if (!temporal) {
            Assert.True(condition: rig.Stood());
            return;
        }
        for (var sample = 0U; (sample < SdfTemporalHistory.Period); sample++) {
            if (sample != 0U) { rig.Produce(); }
            Assert.False(condition: rig.Stood(), userMessage: $"sample {sample} after it is shown again");
            Assert.Equal(expected: sample, actual: rig.HistoryFrames());
            Assert.Equal(expected: SdfTemporalHistory.Sample(index: sample), actual: rig.Jitter());
        }
        rig.Produce();
        Assert.True(condition: rig.Stood());
    }
    // A render whose submission fails after its passes recorded commits no temporal sample: the retry takes the same
    // jitter sample the failed attempt took.
    [Fact]
    public void ARetryAfterAFailedSubmissionTakesTheSameSample() {
        using var rig = new TemporalRig(views: 1, temporal: true);

        rig.Produce();
        var frames = rig.HistoryFrames();

        rig.RefuseRenderSubmission();
        Assert.Throws<InvalidOperationException>(testCode: () => rig.Produce());
        rig.Produce();
        Assert.Equal(expected: (frames + 1u), actual: rig.HistoryFrames());
    }
    // A still view whose input changed on a frame whose submission failed renders again rather than standing on an
    // image that was never written.
    [Fact]
    public void AStillViewChangedOnAFailedSubmissionRendersAgain() {
        using var rig = new TemporalRig(views: 1, cadence: true);

        rig.Produce();
        Assert.True(condition: rig.Stood());
        rig.Selected.DebugMode = 1;
        rig.RefuseRenderSubmission();
        Assert.Throws<InvalidOperationException>(testCode: () => rig.Produce());
        rig.Produce();
        Assert.False(condition: rig.Stood());
        rig.Produce();
        Assert.True(condition: rig.Stood());
    }

    // One sdf.world instance, "world", over a frame on the upload model, optionally reading a feed that hands out a
    // tainted image until it fills. Construction produces until the view has rendered its installed graph.
    private sealed class TemporalRig : IDisposable {
        private static readonly string[] Names = ["world"];
        private static readonly RenderGraphRoot[] Roots = [new RenderGraphRoot(Height: 1, Instance: "world", Width: 1)];
        private readonly UploadModelGpu m_gpu = new();

        private readonly FrameContext m_context;
        private readonly TaintingFeed? m_feed;

        private SdfFrame m_sourceFrame;
        private long m_index;
        private ulong m_rendered;

        public TemporalRig(int views, bool cadence = false, bool feed = false, bool secondResidency = false, bool temporal = false, float renderScale = 1f) {
            var pipelines = SdfTestPipelines.Cache(regionCopy: UploadModelGpu.RegionCopyBytecode);
            var frame = Frame() with { EnableCadenceGate = cadence };

            frame = frame with { Views = [frame.Views[0] with { Quality = new SdfViewQuality { Temporal = temporal }, RenderScale = renderScale }] };

            frame = frame with { Views = [.. Enumerable.Repeat(element: frame.Views[0], count: views)] };
            m_sourceFrame = frame;
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
                brickPoolVoxelCapacity: 0, frameSource: new CapturingFrameSource(capture: () => m_sourceFrame),
                height: Extent, kernels: SdfTestPipelines.Kernels(), name: name, pipelines: pipelines, width: Extent);
        }

        public IReadOnlyList<UploadModelBufferBarrier> BufferBarriers => m_gpu.BufferBarriers;
        public bool Filling {
            set => m_feed!.Filling = value;
        }
        public SdfWorldPasses Passes { get; }
        public float RenderGrid {
            set => m_sourceFrame = m_sourceFrame with { Views = [m_sourceFrame.Views[0] with { ResolvedRenderScale = value }] };
        }
        public RenderGraphRuntime Runtime { get; }
        public SdfWorldResidency? Second { get; }
        public SdfWorldResidency Selected { get; set; }
        public SdfFrame SourceFrame { get => m_sourceFrame; set => m_sourceFrame = value; }
        public IReadOnlyList<string> StateConflicts => m_gpu.StateConflicts;
        public int ViewIndex { get; set; }

        public uint OutputExtent { get; set; } = Extent;

        // Whether the display shows nothing, so the scheduler leaves the view unread.
        public bool Parked { get; set; }
        public float ResolvedScale {
            set => m_sourceFrame = m_sourceFrame with { Views = [.. m_sourceFrame.Views.Select(selector: view => view with { ResolvedRenderScale = value })] };
        }
        public ShaderPipelineRenderNode World => Runtime.Node(instance: Runtime.Instances.IndexOf(name: "world"));

        public void Produce(bool named = true) {
            m_rendered = World.FrameCounter;
            var scheduled = new RenderGraphFrame(DisplayHeight: ((int)OutputExtent), DisplayHertz: 60, DisplayWidth: ((int)OutputExtent),
                Footprints: ((m_feed is null) ? [] : [new RenderGraphFootprint(Consumer: "world", Height: 1.0, Producer: "feed", Width: 1.0)]),
                Index: m_index, Named: (named ? Names : []), Roots: ((named && !Parked) ? Roots : []), Tick: m_index++);

            var context = m_context with { TargetHeight = OutputExtent, TargetWidth = OutputExtent };

            _ = Runtime.ProduceFrame(context: in context, frame: in scheduled);
        }
        // Whether the latest frame let the view's previous output stand.
        public bool Stood() => (World.FrameCounter == m_rendered);
        // Refuses the next frame's render submission, after its passes recorded: the residency's table upload is the
        // frame's first fenced submission and the view's node submits second.
        public void RefuseRenderSubmission() => m_gpu.FencedSubmissionsBeforeRefusal = 1;
        public uint HistoryFrames() => BitConverter.ToUInt32(value: Block(), startIndex: Offset(member: SdfWorldPackage.HistoryFrames));
        public bool Temporal() => (BitConverter.ToUInt32(value: Block(), startIndex: Offset(member: SdfWorldPackage.Temporal)) != 0u);
        public Vector2 Jitter() {
            var block = Block();
            var jitter = Offset(member: SdfWorldPackage.Jitter);

            return new Vector2(x: BitConverter.ToSingle(startIndex: jitter, value: block), y: BitConverter.ToSingle(startIndex: (jitter + sizeof(float)), value: block));
        }
        public bool PreviousValid() => (BitConverter.ToSingle(value: Block(), startIndex: (Offset(member: SdfWorldPackage.PreviousView) + (3 * sizeof(float)))) != 0f);
        // The bytes of the history surface the resolve of one more frame writes. The resolve's pass set is the third from the
        // frame's last: the sky and the composite follow it.
        public uint HistorySurfaceBytes() {
            m_gpu.SetBinds = [];
            Produce();
            var set = m_gpu.SetBinds.Where(predicate: static bind => (bind.Group == ((uint)ShaderInterfaceGroup.Pass))).Select(selector: static bind => bind.Set).Distinct().ToArray()[^3];

            m_gpu.SetBinds = null;

            return ((uint)m_gpu.Memory(bufferHandle: m_gpu.BufferAt(
                set: set,
                binding: SdfKernelInterfaces.BindingOf(layout: SdfWorldInterfaces.ResolveParameters.Layout, member: SdfWorldPackage.HistorySurfaceWritten))).Length);
        }
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
        public FrameRender Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            m_image ??= gpu.Services.ImageFactory.Create(format: Format, height: height, name: default, usage: GpuImageUsage.Sampled | GpuImageUsage.Storage, width: width);

            return FrameRender.Rendered;
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

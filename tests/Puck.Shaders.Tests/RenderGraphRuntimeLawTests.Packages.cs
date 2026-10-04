using System.Buffers.Binary;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// Package instances: an external instance of a package a recorder runs as a fragment renders the package's one-pass graph
// on a node, its passes the fragment's in order, its counted scratch sized by the counter the package states for it. Its
// latest render stands for a frame its package says nothing changed in, except while a capture of it is pending, and the
// device's loss reaches the package.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string PackageView = "view";

    // A final render's fence can signal after cadence stops scheduling its node. Both readbacks still become available,
    // without another render, and keep the submission and grid that actually produced them.
    [Fact]
    public void AStandingViewsFinalSubmissionIsCountedAndTimedAfterItsFenceSignals() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var recorders = new Recorders();
        var view = new ViewPackage();

        gpu.WriteReadback = (name, bytes) => {
            if (name.Part != "timing") { return; }
            for (var offset = 0; ((offset + 16) <= bytes.Length); offset += 16) {
                BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes[offset..], value: 0);
                BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes[(offset + 8)..], value: 1_000_000);
            }
        };
        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance()), PackageView, new RenderGraphRuntimeGraph[1]);
        var node = runtime.Node(instance: 0);
        var index = 0L;

        node.TimingEnabled = true;
        TestLiveness.Until(step: () => {
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
            return (node.FrameCounter > 0UL);
        });
        var fence = Assert.IsType<FakeGpuDevice.Fence>(@object: gpu.LastSubmittedFence);
        var rendered = node.FrameCounter;
        var recorded = view.Parts.Count;
        var completed = new GpuWorkSample();

        view.Unchanged = true;
        for (var frame = 0; (frame < 12); frame++) {
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
        }
        Assert.Equal(expected: rendered, actual: node.FrameCounter);
        Assert.False(condition: node.TryReadCompleted(sample: completed));
        Assert.Equal(expected: 0L, actual: node.LatestTimingSubmission);
        fence.Completed = true;
        ProducePackageFrame(frameIndex: index++, runtime: runtime);

        Assert.True(condition: node.TryReadCompleted(sample: completed));
        Assert.Equal(expected: 1L, actual: completed.Submission);
        Assert.Equal(expected: completed.Submission, actual: node.LatestTimingSubmission);
        Assert.Equal(expected: (2d * SdfWorldPackage.NativeFragment.Passes.Count), actual: node.LatestTimingMilliseconds);
        Assert.True(condition: node.TryGetRenderGrid(grid: out var grid, submission: completed.Submission));
        Assert.Equal(actual: grid, expected: 1d);
        for (var frame = 0; (frame < 12); frame++) {
            ProducePackageFrame(frameIndex: index++, runtime: runtime);
        }
        Assert.Equal(expected: recorded, actual: view.Parts.Count);
        Assert.Equal(expected: rendered, actual: node.FrameCounter);
        Assert.Equal(expected: 1L, actual: node.TimingFrames);
        Assert.True(condition: node.TryReadCompleted(sample: completed));
        Assert.Equal(expected: 1L, actual: completed.Submission);
    }
    // The runtime polls a node between its renders only while it owes a readback, so standing instances cost no poll
    // once their submissions are read back, however many there are.
    [Fact]
    public void StandingInstancesThatOweNoReadbackAreNeverPolled() {
        var gpu = new FakeGpuDevice(holdFences: true);
        var recorders = new Recorders();
        var view = new ViewPackage();
        string[] names = [PackageView, "second", "third", "fourth"];

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set([.. names.Select(selector: static name => PackageInstance() with { Name = name })]), PackageView, new RenderGraphRuntimeGraph[names.Length]);
        var index = 0L;

        void Frame() {
            var frame = new RenderGraphFrame(
                DisplayHeight: Display,
                DisplayHertz: 60,
                DisplayWidth: Display,
                Footprints: [],
                Index: index,
                Roots: [.. names.Select(selector: static name => new RenderGraphRoot(Height: 1.0, Instance: name, Width: 1.0))],
                Tick: index
            );

            index++;
            _ = runtime.ProduceFrame(context: default, frame: in frame);
        }

        TestLiveness.Until(step: () => {
            Frame();
            return names.Select(selector: name => runtime.Node(instance: runtime.Instances.IndexOf(name: name))).All(predicate: static node => (node.FrameCounter > 0UL));
        });
        view.Unchanged = true;
        Frame();
        // While their last submissions are in flight every standing node owes a readback and is polled once a frame.
        var owing = runtime.ReadbackPolls;

        Frame();
        Assert.Equal(expected: (owing + names.Length), actual: runtime.ReadbackPolls);
        foreach (var fence in gpu.SubmittedFences) {
            fence.Completed = true;
        }
        Frame();
        var settled = runtime.ReadbackPolls;

        for (var frame = 0; (frame < 12); frame++) {
            Frame();
        }
        Assert.Equal(expected: settled, actual: runtime.ReadbackPolls);
        Assert.All(collection: names.Select(selector: name => runtime.Node(instance: runtime.Instances.IndexOf(name: name))), action: static node => Assert.False(condition: node.OwesReadbacks));
    }
    [Fact]
    public void APackageInstanceRendersItsFragmentOnANodeAndStandsWhileUnchanged() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(
            factory: view,
            package: RenderGraphPackageCatalog.SdfWorld
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(PackageInstance()),
            PackageView,
            new RenderGraphRuntimeGraph[1]
        );
        var parts = SdfWorldPackage.NativeFragment.Passes.Select(selector: static pass => pass.Name).ToArray();
        var index = 0L;

        TestLiveness.Until(
            reason: () => $"The view recorded {view.Parts.Count} part(s).",
            step: () => {
                ProducePackageFrame(
                    frameIndex: index++,
                    runtime: runtime
                );

                return (view.Parts.Count >= parts.Length);
            }
        );
        Assert.Null(@object: runtime.Producer(instance: 0));
        Assert.Equal(
            actual: runtime.Graph(instance: 0)!.Pipeline.Plan.Passes.Select(selector: static pass => pass.Name),
            expected: parts.Select(selector: static part => RenderGraphPackageFragment.Spliced(
                name: part,
                pass: RenderGraphPackageCatalog.SdfWorld
            ))
        );
        Assert.Equal(
            actual: view.Parts.Take(count: parts.Length),
            expected: parts
        );

        // Nothing it renders from changed: the instance renders nothing more.
        view.Unchanged = true;

        var recorded = view.Parts.Count;

        for (var frame = 0; (frame < 3); frame++) {
            ProducePackageFrame(
                frameIndex: index++,
                runtime: runtime
            );
        }

        Assert.Equal(expected: recorded, actual: view.Parts.Count);

        // A capture of it is served only by a render.
        var request = CaptureRequest();

        runtime.CaptureTarget(instance: PackageView).RequestCapture(request: request);
        ProducePackageFrame(
            frameIndex: index++,
            runtime: runtime
        );
        Assert.Equal(expected: (recorded + parts.Length), actual: view.Parts.Count);

        runtime.OnDeviceLost();
        Assert.Equal(expected: 1, actual: view.Lost);
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AnUnchangedPackageFinishesResizingBeforeItStandsAgain(bool displayResize) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance()), PackageView, new RenderGraphRuntimeGraph[1]);
        var index = 0L;

        TestLiveness.Until(
            step: () => {
                ProducePackageFrame(frameIndex: index++, runtime: runtime);

                return (view.Parts.Count > 0);
            }
        );

        var display = (displayResize ? (Display * 2) : Display);
        var width = (displayResize ? 1.0 : 0.5);
        using var gate = new ManualResetEventSlim(initialState: false);

        view.Unchanged = true;
        view.BuildGate = gate;

        try {
            for (var frame = 0; (frame < 3); frame++) {
                ProducePackageFrame(display: display, frameIndex: index++, runtime: runtime, width: width);
                Assert.Equal(expected: RenderGraphInstanceStatus.Rendered, actual: runtime.Latest!.Instances[0].Status);
            }
        } finally {
            gate.Set();
        }

        var expected = (Width: ((uint)(display * width)), Height: ((uint)display));

        TestLiveness.Until(
            step: () => {
                ProducePackageFrame(display: display, frameIndex: index++, runtime: runtime, width: width);

                return (runtime.Node(instance: 0).Extent == expected);
            }
        );
        ProducePackageFrame(display: display, frameIndex: index++, runtime: runtime, width: width);
        Assert.Equal(expected: RenderGraphInstanceStatus.Waiting, actual: runtime.Latest!.Instances[0].Status);
    }
    [Fact]
    public void AnExternalInstanceOfAPackageThatRunsAsNoFragmentIsRefusedByName() {
        var recorders = new Recorders(Camera);

        Assert.False(condition: RenderGraphRuntime.TryCreate(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: new FakePipelineGpu(),
            graphs: [null],
            hostsOnDirectX: false,
            packages: recorders.Registry,
            refusal: out var refusal,
            root: PackageView,
            runtime: out _,
            set: Set(PackageInstance() with { ExternalPackage = Camera })
        ));
        Assert.Equal(expected: RenderGraphRuntimeRefusalCode.ExternalProducer, actual: refusal!.Code);
        Assert.Contains(expectedSubstring: "runs as no fragment", actualString: refusal.Message);
    }

    private static RenderGraphInstance PackageInstance() => new(
        ExternalPackage: RenderGraphPackageCatalog.SdfWorld,
        Name: PackageView,
        Passes: SdfWorldPackage.NativeFragment.Passes.Count,
        Reads: [],
        Refresh: RenderGraphRefresh.EveryFrame
    );

    // A frame the display shows nothing in parks the instance: its schedule leaves it unread, and the runtime counts the
    // frame. Its package is asked whether it is unchanged, and its next render records, with the count, which moves only
    // while it is parked, so the frame it is shown again in carries a count its preceding render did not.
    [Fact]
    public void AParkedInstanceCarriesTheFramesItWentUnreadToItsPackage() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(
            factory: view,
            package: RenderGraphPackageCatalog.SdfWorld
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(PackageInstance()),
            PackageView,
            new RenderGraphRuntimeGraph[1]
        );
        var index = 0L;

        TestLiveness.Until(
            reason: () => $"The view recorded {view.Parts.Count} part(s).",
            step: () => {
                ProducePackageFrame(
                    frameIndex: index++,
                    runtime: runtime
                );

                return (view.Parts.Count >= SdfWorldPackage.NativeFragment.Passes.Count);
            }
        );
        Assert.All(collection: view.RecordedUnread, action: static unread => Assert.Equal(actual: unread, expected: 0L));

        var recorded = view.Parts.Count;

        for (var frame = 0; (frame < 3); frame++) {
            ProducePackageFrame(
                frameIndex: index++,
                parked: true,
                runtime: runtime
            );
        }
        Assert.Equal(expected: recorded, actual: view.Parts.Count);
        for (var frame = 0; (frame < 2); frame++) {
            ProducePackageFrame(
                frameIndex: index++,
                runtime: runtime
            );
            Assert.Equal(expected: 3L, actual: view.AskedUnread[^1]);
            Assert.Equal(expected: 3L, actual: view.RecordedUnread[^1]);
        }
        Assert.True(condition: (view.Parts.Count > recorded));
    }
    // A reconfiguration keeps a surviving instance's node, graph and history, and so the frames it went unread: a parked
    // temporal view whose set is reconfigured around it still carries its count into the frame it is shown again in, while
    // an instance new to the set starts at zero, whatever the others have counted.
    [Fact]
    public void AKeptInstanceKeepsItsUnreadFramesThroughAReconfigurationAndANewOneStartsFresh() {
        const string Second = "second";
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders, Set(PackageInstance()), PackageView, new RenderGraphRuntimeGraph[1]);
        var index = 0L;

        TestLiveness.Until(
            reason: () => $"The view recorded {view.Parts.Count} part(s).",
            step: () => {
                ProducePackageFrame(frameIndex: index++, runtime: runtime);

                return (view.Parts.Count >= SdfWorldPackage.NativeFragment.Passes.Count);
            }
        );
        for (var frame = 0; (frame < 3); frame++) {
            ProducePackageFrame(frameIndex: index++, parked: true, runtime: runtime);
        }
        Assert.True(condition: runtime.TryReconfigure(
            graphs: new RenderGraphRuntimeGraph?[2],
            refusal: out var refusal,
            root: PackageView,
            set: Set(PackageInstance(), PackageInstance() with { Name = Second })
        ), userMessage: refusal?.Message);

        var asked = view.AskedUnreadBy[PackageView].Count;
        var newcomerAsked = false;

        for (var frame = 0; ((frame < 40) && !newcomerAsked); frame++) {
            var before = (view.AskedUnreadBy.TryGetValue(key: Second, value: out var seen) ? seen.Count : 0);

            ProducePackageFrame(frameIndex: index++, parked: true, runtime: runtime);
            // The kept view carries the three frames it was parked for before the reconfiguration, and one more a frame.
            Assert.Equal(expected: (3L + frame), actual: view.AskedUnreadBy[PackageView][(asked + frame)]);
            if (view.AskedUnreadBy.TryGetValue(key: Second, value: out var after) && (after.Count > before)) {
                // The newcomer counted nothing before the reconfiguration, so its first question carries only the frames
                // parked since.
                Assert.Equal(expected: ((long)frame), actual: after[before]);
                newcomerAsked = true;
            }
        }
        Assert.True(condition: newcomerAsked, userMessage: "The new instance was never asked.");
    }
    // A nested view remains visible in held consumer outputs: the scheduler keeps it waiting, never unread, while a consumer
    // the display shows skips its render, whether paced by refresh or standing unchanged, so its temporal epoch never
    // restarts. Removing the roots really parks it.
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [Theory]
    public void AHeldConsumerDoesNotParkItsNestedView(int divisor, bool unchanged) {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders();
        var view = new ViewPackage();

        recorders.Registry.Register(factory: view, package: RenderGraphPackageCatalog.SdfWorld);
        using var runtime = Runtime(gpu, recorders,
            Set(PackageInstance(),
                Instance(name: "portal", reads: new RenderGraphRead(Producer: PackageView)),
                Instance(name: "main", reads: new RenderGraphRead(Producer: "portal")) with { Refresh = RenderGraphRefresh.Every(divisor: divisor) }),
            "main", null!,
            Graph(ScreensGraph(pool: false, "screen"), ("screen", PackageView)),
            Graph(ScreensGraph(pool: false, "screen"), ("screen", "portal")));
        var index = 0L;

        void Produce(bool parked = false, bool stands = false) {
            var frame = new RenderGraphFrame(DisplayHeight: Display, DisplayHertz: 60, DisplayWidth: Display,
                // Inner first also exercises visibility through more than one held output.
                Footprints: [new RenderGraphFootprint(Consumer: "portal", Height: 1, Producer: PackageView, Width: 1),
                    new RenderGraphFootprint(Consumer: "main", Height: 1, Producer: "portal", Width: 1)],
                Index: index, Roots: (parked ? [] : [new RenderGraphRoot(Height: 1, Instance: "main", Width: 1)]),
                Tick: index++, Unchanged: (stands ? ["main"] : null));

            _ = runtime.ProduceFrame(context: default, frame: in frame);
        }

        TestLiveness.Until(step: () => {
            Produce();
            return ((view.Parts.Count > 0) && (runtime.Node(instance: 1).FrameCounter > 0) &&
                (runtime.Node(instance: 2).FrameCounter > 0) &&
                !runtime.Node(instance: 1).HasPendingCandidate && !runtime.Node(instance: 2).HasPendingCandidate);
        });
        var recorded = view.Parts.Count;
        var waited = false;

        for (var frame = 0; (frame < 24); frame++) {
            Produce(stands: unchanged);
            Assert.NotEqual(expected: RenderGraphInstanceStatus.Unread, actual: runtime.Latest!.Instances[0].Status);
            waited |= (runtime.Latest!.Instances[0].Status == RenderGraphInstanceStatus.Waiting);
        }
        Assert.True(condition: waited);
        Assert.All(collection: view.AskedUnread, action: static count => Assert.Equal(actual: count, expected: 0L));
        Assert.All(collection: view.RecordedUnread, action: static count => Assert.Equal(actual: count, expected: 0L));
        if (unchanged) {
            Assert.Equal(expected: recorded, actual: view.Parts.Count);
        } else {
            Assert.True(condition: (view.Parts.Count > recorded));
        }

        for (var frame = 0; (frame < 3); frame++) { Produce(parked: true); }
        Produce();
        Assert.Equal(expected: 3L, actual: view.AskedUnread[^1]);
        Assert.Equal(expected: 3L, actual: view.RecordedUnread[^1]);
    }

    private static void ProducePackageFrame(RenderGraphRuntime runtime, long frameIndex, int display = Display, double width = 1.0, bool parked = false) {
        var frame = new RenderGraphFrame(
            DisplayHeight: display,
            DisplayHertz: 60,
            DisplayWidth: display,
            Footprints: [],
            Index: frameIndex,
            Roots: (parked ? [] : [new RenderGraphRoot(Height: 1.0, Instance: PackageView, Width: width)]),
            Tick: frameIndex
        );

        _ = runtime.ProduceFrame(
            context: default,
            frame: in frame
        );
    }

    /// <summary>A view package whose recorders note the part each recording runs, counting its instance's scratch for
    /// one view of one instance, and saying nothing changed when told to. Its counter's revision moves when told to, as
    /// an <c>sdf.world</c> instance's does when its residency is replaced.</summary>
    private sealed class ViewPackage : IRenderGraphPackageFactory, IShaderPipelineStorageCounter {
        private int m_builds;

        public ManualResetEventSlim? BuildGate { get; set; }
        public int Builds => Volatile.Read(location: ref m_builds);
        public List<RenderGraphConvergence> Convergence { get; } = [];
        public int Lost { get; private set; }
        public List<string> Parts { get; } = [];
        public long Revision { get; set; }
        public bool SamplesReads { get; set; }
        public bool Unchanged { get; set; }
        // The unread frames each cadence question and each recording carried, in order.
        public List<long> AskedUnread { get; } = [];
        public List<long> RecordedUnread { get; } = [];
        // The sample index each render of the fragment's last part took from the latest convergence, while it converges.
        public List<int> Served { get; } = [];

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
            BuildGate?.Wait(cancellationToken: cancellationToken);
            _ = Interlocked.Increment(location: ref m_builds);

            return ValueTask.FromResult<IDisposable?>(result: null);
        }
        public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => new(
            Height: height,
            Width: width
        ) {
            InstanceMaskWords = 1,
            Instances = 1,
            SegmentTapeWords = 36,
            Tiles = ((((width + 15U) / 16U) * ((height + 15U) / 16U))),
            Viewports = 1,
        };
        public IShaderPipelineStorageCounter? CounterOf(string instance) => this;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(
            owner: this,
            part: context.Part!
        );

        public Dictionary<string, List<long>> AskedUnreadBy { get; } = [];

        public bool IsUnchanged(string instance, long unreadFrames, in FrameContext context) {
            AskedUnread.Add(item: unreadFrames);
            if (!AskedUnreadBy.TryGetValue(key: instance, value: out var asked)) {
                AskedUnreadBy[instance] = (asked = []);
            }

            asked.Add(item: unreadFrames);

            return Unchanged;
        }

        // Each named instance's render grid; an instance not named renders its output's grid.
        public Dictionary<string, double> Grids { get; } = [];

        public IShaderPipelineRenderExtent? RenderExtentOf(string instance) => (Grids.TryGetValue(key: instance, value: out var grid)
            ? new GridExtent(grid: grid)
            : null);
        public void OnDeviceLost() => Lost++;
        public void BeginConvergence(string instance, RenderGraphConvergence convergence) => Convergence.Add(item: convergence);

        private sealed class GridExtent(double grid) : IShaderPipelineRenderExtent {
            public double Grid => grid;
            public long Revision => 0L;

            public (uint Width, uint Height) CeilingAt(uint width, uint height) => (width, height);
            public (uint Width, uint Height) FrameAt(uint width, uint height) => (
                ((uint)RenderGraphExtent.Pixels(display: ((int)width), fraction: grid)),
                ((uint)RenderGraphExtent.Pixels(display: ((int)height), fraction: grid))
            );
        }
        private sealed class Recorder(ViewPackage owner, string part) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Parts.Add(item: part);
                owner.RecordedUnread.Add(item: recording.UnreadFrames);
                if ((part == SdfWorldPackage.NativeFragment.Passes[^1].Name) && (owner.Convergence.LastOrDefault() is { IsActive: true } convergence)) {
                    owner.Served.Add(item: convergence.Samples);
                }

                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}

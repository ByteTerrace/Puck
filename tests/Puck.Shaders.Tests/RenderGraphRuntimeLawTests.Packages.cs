using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// Package instances: an external instance of a package a recorder runs as a fragment renders the package's one-pass graph
// on a node, its passes the fragment's in order, its counted scratch sized by the counter the package states for it. Its
// latest render stands for a frame its package says nothing changed in, except while a capture of it is pending, and the
// device's loss reaches the package.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string PackageView = "view";

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
        var parts = SdfWorldPackage.Fragment.Passes.Select(selector: static pass => pass.Name).ToArray();
        var index = 0L;

        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    ProducePackageFrame(
                        frameIndex: index++,
                        runtime: runtime
                    );

                    return (view.Parts.Count >= parts.Length);
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: $"The view recorded {view.Parts.Count} part(s)."
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
        var request = new FrameCaptureRequest(path: Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"{Guid.NewGuid():N}.png"
        ));

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

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                ProducePackageFrame(frameIndex: index++, runtime: runtime);

                return (view.Parts.Count > 0);
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));

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

        Assert.True(condition: SpinWait.SpinUntil(
            condition: () => {
                ProducePackageFrame(display: display, frameIndex: index++, runtime: runtime, width: width);

                return (runtime.Node(instance: 0).Extent == expected);
            },
            timeout: TimeSpan.FromSeconds(value: 30)
        ));
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
        Passes: SdfWorldPackage.Fragment.Passes.Count,
        Reads: [],
        Refresh: RenderGraphRefresh.EveryFrame
    );
    private static void ProducePackageFrame(RenderGraphRuntime runtime, long frameIndex, int display = Display, double width = 1.0) {
        var frame = new RenderGraphFrame(
            DisplayHeight: display,
            DisplayHertz: 60,
            DisplayWidth: display,
            Footprints: [],
            Index: frameIndex,
            Roots: [new RenderGraphRoot(Height: 1.0, Instance: PackageView, Width: width)],
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
        public int Lost { get; private set; }
        public List<string> Parts { get; } = [];
        public long Revision { get; set; }
        public bool Unchanged { get; set; }

        public IDisposable? Build(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) {
            BuildGate?.Wait(cancellationToken: cancellationToken);
            _ = Interlocked.Increment(location: ref m_builds);

            return null;
        }
        public ShaderPipelineStorageCounts CountsAt(uint width, uint height) => new(
            Height: height,
            Width: width
        ) {
            InstanceMaskWords = 1,
            Instances = 1,
            Tiles = ((((width + 15U) / 16U) * ((height + 15U) / 16U))),
            Viewports = 1,
        };
        public IShaderPipelineStorageCounter? CounterOf(string instance) => this;
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) => new Recorder(
            owner: this,
            part: context.Part!
        );
        public bool IsUnchanged(string instance, in FrameContext context) => Unchanged;
        public void OnDeviceLost() => Lost++;

        private sealed class Recorder(ViewPackage owner, string part) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                owner.Parts.Add(item: part);

                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}

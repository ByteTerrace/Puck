using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// Demand, extent and edges: what renders, at what extent, and what each consumer binds.
public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void ACameraOnTwoScreensRendersOncePerFrameCounted() {
        const int Frames = 16;
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var set = Set(
            Instance(name: "camera"),
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "camera")
            ),
            Instance(
                name: "pane",
                reads: new RenderGraphRead(Producer: "camera")
            )
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(false, "left", "right"), ("left", "camera"), ("right", "camera")),
            Graph(ScreensGraph(false, "screen"), ("screen", "camera"))
        );
        var frames = new Frames(
            footprints: [
                new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "camera", Width: 0.5),
                new RenderGraphFootprint(Consumer: "pane", Height: 1.0, Producer: "camera", Width: 1.0),
            ],
            roots: [
                new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0),
                new RenderGraphRoot(Height: 0.25, Instance: "pane", Width: 0.25),
            ],
            runtime: runtime
        );
        var camera = set.IndexOf(name: "camera");

        frames.Settle();

        var records = recorders.Of(instance: "camera").Records;
        var submitted = runtime.Node(instance: camera).FrameCounter;

        for (var frame = 0; (frame < Frames); frame++) {
            _ = frames.Next();

            Assert.Equal(
                actual: runtime.Latest!.Renders.Count(predicate: index => (index == camera)),
                expected: 1
            );
        }

        // Three consumers' worth of screens, one render a frame: one package record and one submission of the camera's
        // own, counted apart from its consumers'.
        Assert.Equal(
            actual: (Records: (recorders.Of(instance: "camera").Records - records), Submitted: (runtime.Node(instance: camera).FrameCounter - submitted)),
            expected: (Records: ((long)Frames), Submitted: ((ulong)Frames))
        );

        var sample = new GpuWorkSample();

        Assert.True(condition: runtime.Work(instance: camera).TryReadCompleted(sample: sample));
        Assert.Equal(
            actual: sample.PassLabels.ToArray(),
            expected: ["view"]
        );
        Assert.True(condition: runtime.Work(instance: set.IndexOf(name: "main")).TryReadCompleted(sample: sample));
        Assert.Equal(
            actual: sample.PassLabels.ToArray(),
            expected: ["compose"]
        );
    }
    [Fact]
    public void ACameraWhoseOnlyScreenIsOffViewRendersZeroTimes() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var set = Set(
            Instance(name: "camera"),
            Instance(name: "hidden"),
            Instance(
                name: "main",
                reads: [
                    new RenderGraphRead(Producer: "camera"),
                    new RenderGraphRead(Producer: "hidden"),
                ]
            )
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(false, "near", "far"), ("near", "camera"), ("far", "hidden"))
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "camera", Width: 0.5)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        frames.Settle();
        frames.Next(count: 16);

        var hidden = set.IndexOf(name: "hidden");

        // The hidden camera's screen is off view: it is never scheduled, so its graph never builds and nothing records
        // it, while the view showing the other screen renders with a stand-in bound where the hidden camera's image
        // would be.
        Assert.Equal(
            actual: (Status: runtime.Latest!.Instances[hidden].Status, Submitted: runtime.Node(instance: hidden).FrameCounter, Built: runtime.Node(instance: hidden).IsReady, Recorders: recorders.Of(instance: "hidden").Created),
            expected: (Status: RenderGraphInstanceStatus.Unread, Submitted: 0UL, Built: false, Recorders: 0)
        );
        Assert.True(condition: (runtime.Node(instance: set.IndexOf(name: "main")).FrameCounter > 16UL));
        Assert.True(condition: (recorders.Of(instance: "camera").Records > 16L));
    }
    [Fact]
    public void AMirrorFacingItselfReadsThePreviousFrame() {
        var gpu = new FakePipelineGpu();
        var set = Set(Instance(
            name: "mirror",
            reads: new RenderGraphRead(Producer: "mirror")
        ));

        using var runtime = Runtime(
            gpu,
            new Recorders(),
            set,
            "mirror",
            Graph(pipeline: MirrorGraph())
        );
        // The mirror fills a quarter of its own image.
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "mirror", Height: 0.5, Producer: "mirror", Width: 0.5)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "mirror", Width: 1.0)],
            runtime: runtime
        );

        frames.Settle();

        for (var frame = 0; (frame < 6); frame++) {
            var previous = frames.Next();

            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;

            var shown = frames.Next();

            gpu.Recording = false;

            // Its read is a previous-frame edge the scheduler never refuses as a cycle, and it samples exactly the image
            // it published the frame before, while it publishes another.
            var read = Assert.Single(collection: runtime.Latest!.Reads);

            Assert.Equal(
                actual: (read.PreviousFrame, read.Frame),
                expected: (true, (frames.Index - 2))
            );
            Assert.Equal(
                actual: gpu.DescriptorWrites.Single(predicate: static write => (write.Binding == 1)).Handle,
                expected: previous.ImageViewHandle
            );
            Assert.NotEqual(
                actual: shown.ImageViewHandle,
                expected: previous.ImageViewHandle
            );
        }
    }
    [Fact]
    public void AnInputBoundToItsOwnInstanceReadsItsPreviousOutput() {
        var gpu = new FakePipelineGpu();
        var set = Set(Instance(
            name: "feedback",
            reads: new RenderGraphRead(Producer: "feedback")
        ));

        using var runtime = Runtime(
            gpu,
            new Recorders(),
            set,
            "feedback",
            Graph(ScreensGraph(false, "screen"), ("screen", "feedback"))
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "feedback", Height: 0.5, Producer: "feedback", Width: 0.5)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "feedback", Width: 1.0)],
            runtime: runtime
        );

        // Before it has any output of its own, it renders on schedule with a stand-in bound.
        frames.Settle();

        for (var frame = 0; (frame < 6); frame++) {
            var previous = frames.Next();

            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            _ = frames.Next();
            gpu.Recording = false;

            Assert.Equal(
                actual: gpu.DescriptorWrites.Single(predicate: static write => (write.Binding == 1)).Handle,
                expected: previous.ImageViewHandle
            );
        }
    }
    [Fact]
    public void ASameFrameCycleRefusesWithBothInstanceNames() {
        Assert.False(condition: RenderGraphInstanceSet.TryCreate(
            instances: [
                Instance(
                    name: "left",
                    reads: new RenderGraphRead(Producer: "right")
                ),
                Instance(
                    name: "right",
                    reads: new RenderGraphRead(Producer: "left")
                ),
            ],
            refusal: out var refusal,
            set: out _
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RenderGraphInstanceRefusalCode.SameFrameCycle
        );
        Assert.Equal(
            actual: refusal.Instances.Order(comparer: StringComparer.Ordinal),
            expected: ["left", "right"]
        );
    }
    [Fact]
    public void AQuarterScreenViewRendersAtAQuarterExtent() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);
        var set = Set(
            Instance(name: "camera"),
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "camera")
            )
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(false, "screen"), ("screen", "camera"))
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 0.5, Producer: "camera", Width: 0.5)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        frames.Settle();

        var counter = recorders.Of(instance: "camera");
        var shown = frames.Next();

        // Half of each axis is a quarter of the display: the camera's node is built, and its package records, at that
        // extent, while the view showing it renders at the display's.
        Assert.Equal(
            actual: (Camera: runtime.Node(instance: set.IndexOf(name: "camera")).Extent, Recorded: (counter.Width, counter.Height), Main: (shown.Width, shown.Height)),
            expected: (Camera: (((uint)(Display / 2)), ((uint)(Display / 2))), Recorded: (((uint)(Display / 2)), ((uint)(Display / 2))), Main: (((uint)Display), ((uint)Display)))
        );
    }
    [Fact]
    public void ABufferEdgeBindsTheProducersBuffer() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Pool);
        var set = Set(
            Instance(
                name: "pool",
                output: ShaderPipelineResourceKind.Buffer
            ),
            Instance(
                name: "main",
                reads: new RenderGraphRead(
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Producer: "pool"
                )
            )
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            Graph(pipeline: PoolGraph()),
            Graph(ScreensGraph(true), ("pool", "pool"))
        );
        var frames = new Frames(
            footprints: [],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        frames.Settle();

        var counter = recorders.Of(instance: "pool");
        var written = new HashSet<nint>();

        for (var frame = 0; (frame < 6); frame++) {
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            _ = frames.Next();
            gpu.Recording = false;

            // The buffer the pool's package wrote this frame is the one the reader's pass binds at the pool's binding.
            Assert.NotEqual(
                actual: counter.OutputBuffer,
                expected: 0
            );
            Assert.Equal(
                actual: gpu.DescriptorWrites.Single(predicate: static write => (write.Binding == 1)).Handle,
                expected: counter.OutputBuffer
            );
            written.Add(item: counter.OutputBuffer);
        }

        // The producer writes a buffer per frame slot, so the reader follows it around the ring.
        Assert.Equal(
            actual: written.Count,
            expected: 3
        );
    }
    [Fact]
    public void APackageNoRecorderServesIsRefusedByNameAtInstall() {
        var set = Set(
            Instance(name: "camera"),
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "camera")
            )
        );

        Assert.False(condition: RenderGraphRuntime.TryCreate(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: new FakePipelineGpu(),
            graphs: [
                Graph(pipeline: CameraGraph()),
                Graph(ScreensGraph(false, "screen"), ("screen", "camera")),
            ],
            hostsOnDirectX: false,
            packages: new Recorders(Pool).Registry,
            refusal: out var refusal,
            root: "main",
            runtime: out _,
            set: set
        ));
        Assert.Equal(
            actual: refusal.Code,
            expected: RenderGraphRuntimeRefusalCode.PackageUnserved
        );
        Assert.Equal(
            actual: refusal.Names,
            expected: ["camera", "view", Camera]
        );
    }
    [Fact]
    public void AnInputMustBindEveryExternalVersionToADeclaredRead() {
        var set = Set(
            Instance(name: "camera"),
            Instance(name: "main")
        );

        Assert.False(condition: RenderGraphRuntime.TryCreate(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: new FakePipelineGpu(),
            graphs: [
                Graph(pipeline: CameraGraph()),
                Graph(ScreensGraph(false, "screen"), ("screen", "camera")),
            ],
            hostsOnDirectX: false,
            packages: new Recorders(Camera).Registry,
            refusal: out var undeclared,
            root: "main",
            runtime: out _,
            set: set
        ));
        Assert.False(condition: RenderGraphRuntime.TryCreate(
            pipelines: new GpuPassPipelineCache(),
            deviceContext: new FakePipelineGpu(),
            graphs: [
                Graph(pipeline: CameraGraph()),
                Graph(pipeline: ScreensGraph(false, "screen")),
            ],
            hostsOnDirectX: false,
            packages: new Recorders(Camera).Registry,
            refusal: out var unbound,
            root: "main",
            runtime: out _,
            set: set
        ));
        Assert.Equal(
            actual: (undeclared.Code, unbound.Code),
            expected: (RenderGraphRuntimeRefusalCode.InputProducer, RenderGraphRuntimeRefusalCode.InputVersion)
        );
        Assert.Equal(
            actual: unbound.Names,
            expected: ["main", "screen"]
        );
    }
}

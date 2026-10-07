using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// Image lifetime under leases: every image the runtime hands a reader is leased from whichever node owns it, and an image
// its owner drops lives until the last lease on it retires. Feedback never stands for its own image, a capture of a root
// standing for a moving camera is served, a kept consumer's installed graph samples an image whose whole standing chain
// retired, and a reader's pending submission outlives the retirement that dropped the image it reads. Every frame of
// every law checks that the display is handed no released image and the fake device records no command naming one.
public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void APausedDisplayKeepsOneLeaseUntilAnotherSubmission() {
        using var scene = new StandingScene([], Set(Instance(name: "main")), Graph(pipeline: CameraGraph()));

        TestLiveness.Until(
            reason: () => "The root never rendered.",
            step: () => {
                _ = scene.Produce(footprints: [], roots: MainRoot);

                return scene.Runtime.IsSettled;
            }
        );
        scene.Node(instance: "main").Paused = true;
        var submissions = scene.Gpu.SubmissionsMade;

        for (var frame = 0; (frame < 128); frame++) {
            _ = scene.Produce(footprints: [], roots: MainRoot);
            Assert.Equal(expected: submissions, actual: scene.Gpu.SubmissionsMade);
            Assert.Equal(expected: 1, actual: scene.Runtime.PendingDisplayLeases);
        }

        scene.Node(instance: "main").Paused = false;
        _ = scene.Produce(footprints: [], roots: MainRoot);
        Assert.True(condition: (scene.Gpu.SubmissionsMade > submissions));
        Assert.Equal(expected: 1, actual: scene.Runtime.PendingDisplayLeases);
        scene.Runtime.Dispose();
        Assert.Equal(expected: 0, actual: scene.Runtime.PendingDisplayLeases);
        Assert.Empty(collection: scene.Gpu.UsesAfterRelease);
    }
    /// <summary>A paused root replaces its standing camera image with a capture copy or one drawn frame. Once the
    /// camera retires and the device drains, the displaced publication releases its lease without any further root
    /// submissions. The root keeps publishing the replacement while the camera image is disposed exactly once.</summary>
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ADisplacedPublicationRetiresWithoutAnotherRender(bool captureCopy) {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Instance(name: "spare"),
                Instance("main", reads: new RenderGraphRead(Producer: "camera"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera"))
        );
        RenderGraphFootprint[] filmed = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)];
        RenderGraphRoot[] roots = [
            .. MainRoot,
            new RenderGraphRoot(Height: 1.0, Instance: "spare", Width: 1.0),
        ];

        TestLiveness.Until(
            reason: () => "The camera, spare and root never settled.",
            step: () => {
                _ = scene.Produce(footprints: filmed, named: [], roots: roots);

                return scene.Runtime.IsSettled;
            }
        );
        var aliased = scene.ProduceUntilMainStandsForCamera(footprints: filmed, named: [], roots: roots);
        var main = scene.Node(instance: "main");

        main.Paused = true;

        if (captureCopy) {
            scene.Gpu.ReadbackSupported = true;
            var request = CaptureRequest();

            scene.Runtime.RequestCapture(request: request);
            _ = scene.Produce(footprints: filmed, named: [], roots: roots);
            Assert.True(condition: request.Completion.IsCompleted);
            Assert.Null(@object: Outcome(request: request).Error);
            Assert.Null(@object: main.PublishedBinding);
        }

        Assert.True(condition: scene.Runtime.TryReconfigure(
            graphs: [null, Graph(pipeline: ScreensGraph(pool: false))],
            refusal: out var refusal,
            root: "main",
            set: Set(Instance(name: "spare"), Instance(name: "main"))
        ), userMessage: refusal?.Message);
        TestLiveness.Until(
            reason: () => "The paused root never installed its replacement.",
            step: () => {
                _ = scene.Produce(footprints: [], named: [], roots: roots);

                return !main.HasPendingCandidate;
            }
        );

        if (!captureCopy) {
            main.Step();
            _ = scene.Produce(footprints: [], named: [], roots: roots);
        }

        var submissions = scene.Gpu.SubmissionsMade;
        // Releasing the spare drains all readers' leases, including those on the retired camera's image.
        for (var frame = 0; (frame < 8); frame++) {
            var shown = scene.Produce(footprints: [], named: [], roots: MainRoot);

            Assert.NotEqual(expected: 0, actual: shown.ImageHandle);
            Assert.NotEqual(expected: aliased.ImageHandle, actual: shown.ImageHandle);
            Assert.Equal(expected: submissions, actual: scene.Gpu.SubmissionsMade);
        }

        Assert.Equal(expected: 1, actual: scene.Gpu.CreatedObjects.Single(predicate: created =>
            (created.Handle == aliased.ImageHandle)).DisposeCount);
    }
    [Fact]
    public void AStandingPreviousFrameOutputShowsThePreviousCameraImage() {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Instance("main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "camera"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera"))
        );
        RenderGraphFootprint[] filmed = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)];

        TestLiveness.Until(
            reason: () => "The root never stood for its previous-frame input.",
            step: () => {
                _ = scene.Produce(footprints: filmed, roots: [.. MainRoot, Full(instance: "camera")]);

                return (scene.Runtime.IsSettled && (scene.Node(instance: "main").PublishedBinding == "world"));
            }
        );

        for (var frame = 0; (frame < 8); frame++) {
            var previous = scene.Recorders.Of(instance: "camera").OutputImage;

            scene.Node(instance: "camera").Frame = (scene.Node(instance: "camera").Frame with { StateTick = ((ulong)(frame + 1)), });
            var shown = scene.Produce(footprints: filmed, roots: [.. MainRoot, Full(instance: "camera")]);

            Assert.NotEqual(expected: previous, actual: scene.Recorders.Of(instance: "camera").OutputImage);
            Assert.Equal(expected: previous, actual: shown.ImageHandle);
        }

        scene.Gpu.ReadbackSupported = true;
        scene.Node(instance: "main").Paused = true;
        scene.Node(instance: "camera").Frame = (scene.Node(instance: "camera").Frame with { StateTick = 9UL, });
        var request = CaptureRequest();

        scene.Runtime.RequestCapture(request: request);
        _ = scene.Produce(footprints: filmed, roots: [.. MainRoot, Full(instance: "camera")]);
        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(expected: 8UL, actual: Outcome(request: request).Tick);
    }
    /// <summary>A root reads its own previous frame through a pass that draws over it. Once the pass would draw nothing,
    /// standing for that previous image would publish an image the root renders into again a few frames later, so the pass
    /// is told it may not stand in and draws: the root keeps a completed image of its own every frame, never
    /// nothing.</summary>
    [Fact]
    public void ARootReadingItsOwnPreviousFrameDrawsRatherThanStandingForItself() {
        using var scene = new StandingScene(
            ["main"],
            Set(Instance("main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "main"))),
            Graph(OverGraph(reader: false), ("world", "main"))
        );
        RenderGraphFootprint[] shown = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "main", Width: 1.0)];

        AssertFeedbackKeepsAnImage(
            loop: ["main"],
            scene: scene,
            shown: shown
        );
    }
    /// <summary>A view reads the root's previous frame and the root reads the view in the same frame, each through a pass
    /// that draws over its input. Once both would draw nothing, the loop would close on the root's own image, so the root
    /// draws and the view stands for the root's previous image: the loop keeps a completed image every frame, never
    /// nothing.</summary>
    [Fact]
    public void TwoInstancesReadingEachOtherNeverCloseALoopOfStandingOutputs() {
        using var scene = new StandingScene(
            ["main", "view"],
            Set(
                Instance("view", reads: new RenderGraphRead(PreviousFrame: true, Producer: "main")),
                Instance("main", reads: new RenderGraphRead(Producer: "view"))
            ),
            Graph(OverGraph(reader: false), ("world", "main")),
            Graph(OverGraph(reader: false), ("world", "view"))
        );
        RenderGraphFootprint[] shown = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "main", Width: 1.0),
        ];

        AssertFeedbackKeepsAnImage(
            loop: ["main", "view"],
            scene: scene,
            shown: shown
        );
        Assert.Equal(
            actual: scene.Node(instance: "view").PublishedBinding,
            expected: "world"
        );
    }
    /// <summary>A camera and a root standing for it both render every frame, so the camera has always rendered past the
    /// image the root last published by the time the root is asked for a capture: the capture still moves to the root and
    /// is served from the frame it renders.</summary>
    [Fact]
    public void ACaptureOfARootStandingForACameraRenderingEveryFrameIsServed() {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Instance("main", reads: new RenderGraphRead(Producer: "camera"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera"))
        );
        RenderGraphFootprint[] filmed = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)];

        scene.Gpu.ReadbackSupported = true;
        _ = scene.ProduceUntilMainStandsForCamera(
            footprints: filmed,
            roots: MainRoot
        );

        var request = CaptureRequest();

        scene.Runtime.RequestCapture(request: request);

        for (var frame = 0; ((frame < 8) && !request.Completion.IsCompleted); frame++) {
            _ = scene.Produce(footprints: filmed, roots: MainRoot);
        }

        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(
            actual: Assert.Single(collection: scene.Gpu.Readbacks).Image,
            expected: scene.Recorders.Of(instance: "camera").OutputImage
        );
    }
    /// <summary>A paused root keeps publishing the camera image it stood for when it last rendered, while the camera
    /// keeps rendering and writes that image again. A capture of the root, served without a render, reads a copy the
    /// root made of the camera's image of the frame that served it, never the image the camera has since rewritten.</summary>
    [Fact]
    public void APausedRootStandingForACameraServesACaptureFromTheServingFramesPixels() {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Instance("main", reads: new RenderGraphRead(Producer: "camera"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera"))
        );
        RenderGraphFootprint[] filmed = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)];

        scene.Gpu.ReadbackSupported = true;

        var stood = scene.ProduceUntilMainStandsForCamera(
            footprints: filmed,
            roots: MainRoot
        );
        var main = scene.Node(instance: "main");
        var rewritten = new HashSet<nint>();

        main.Paused = true;

        // The camera renders on into every image of its ring, the one the root still publishes among them.
        for (var frame = 0; (frame < 4); frame++) {
            _ = scene.Produce(footprints: filmed, roots: MainRoot);
            _ = rewritten.Add(item: scene.Recorders.Of(instance: "camera").OutputImage);
        }

        Assert.Contains(expected: stood.ImageHandle, collection: rewritten);

        var request = CaptureRequest();
        var served = ((nint)0);

        scene.Runtime.RequestCapture(request: request);

        for (var frame = 0; ((frame < 8) && !request.Completion.IsCompleted); frame++) {
            _ = scene.Produce(footprints: filmed, roots: MainRoot);
            served = scene.Recorders.Of(instance: "camera").OutputImage;
        }

        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Null(@object: Outcome(request: request).Error);

        var read = Assert.Single(collection: scene.Gpu.Readbacks).Image;

        Assert.Contains(
            expected: (served, read),
            collection: scene.Gpu.CopiedImages
        );
        Assert.True(condition: scene.Runtime.TryLatestImage(image: out var published, instance: "main"));
        Assert.Equal(expected: read, actual: published.ImageHandle);

        for (var frame = 0; (frame < 4); frame++) {
            Assert.Equal(expected: read, actual: scene.Produce(footprints: filmed, roots: MainRoot).ImageHandle);
        }
    }
    /// <summary>A paused root stands for the camera when a reconfiguration retires the camera and replaces the root's
    /// graph with one that reads nothing, and a later drain retires every finished submission's leases. The root's output
    /// stood for the retired camera, so it resolves to nothing: the display is handed nothing in its place, a capture of
    /// the root is never moved to its node and waits, naming why, and no command ever names the camera's released
    /// image.</summary>
    [Fact]
    public void APausedRootStandingForARetiredCameraNeverServesItsReleasedImage() {
        using var scene = new StandingScene(
            ["main"],
            Set(
                Instance(name: "camera"),
                Instance(name: "spare"),
                Instance("main", reads: new RenderGraphRead(Producer: "camera"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera"))
        );
        RenderGraphFootprint[] filmed = [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "camera", Width: 1.0)];
        RenderGraphRoot[] roots = [
            .. MainRoot,
            new RenderGraphRoot(Height: 1.0, Instance: "spare", Width: 1.0),
        ];

        scene.Gpu.ReadbackSupported = true;
        _ = scene.ProduceUntilMainStandsForCamera(
            footprints: filmed,
            named: [],
            roots: roots
        );

        var main = scene.Node(instance: "main");

        main.Paused = true;
        Assert.True(
            condition: scene.Runtime.TryReconfigure(
                graphs: [null, Graph(pipeline: ScreensGraph(pool: false))],
                refusal: out var refusal,
                root: "main",
                set: Set(
                    Instance(name: "spare"),
                    Instance(name: "main")
                )
            ),
            userMessage: refusal?.Message
        );
        TestLiveness.Until(
            reason: () => "The paused root never installed its replacement.",
            step: () => {
                _ = scene.Produce(footprints: [], named: [], roots: roots);

                return !main.HasPendingCandidate;
            }
        );

        // Nothing names the spare instance any more: its release drains the device and retires every finished lease.
        var shown = scene.Produce(footprints: [], named: [], roots: MainRoot);

        Assert.Equal(
            actual: scene.Runtime.Latest!.Instances[scene.Runtime.Instances.IndexOf(name: "spare")].Status,
            expected: RenderGraphInstanceStatus.Unnamed
        );
        Assert.Equal(
            actual: shown.ImageHandle,
            expected: 0
        );

        var request = CaptureRequest();

        scene.Runtime.RequestCapture(request: request);

        for (var frame = 0; (frame < 4); frame++) {
            _ = scene.Produce(footprints: [], named: [], roots: MainRoot);
        }

        Assert.False(condition: request.Completion.IsCompleted);
        Assert.Empty(collection: scene.Gpu.Readbacks);
        Assert.Contains(
            expectedSubstring: "retired",
            actualString: scene.Runtime.UnservedCaptureReasonOf(instance: "main")
        );
        Assert.True(condition: request.TryFail(error: new OperationCanceledException()));
    }
    /// <summary>A view may stand for the camera's previous frame, and a root may stand for the view's previous frame: the
    /// root's output would need the camera's output from two frames ago, older than the two each instance records. The set
    /// is refused at install, naming the chain, rather than resolving to nothing. A chain crossing one previous-frame
    /// read, or one whose root draws over the view rather than standing for it, installs.</summary>
    [Fact]
    public void AChainOfStandingOutputsAcrossTwoPreviousFrameReadsIsRefusedAtInstall() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera, Over);
        var refusal = Refusal(
            gpu,
            recorders,
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(PreviousFrame: true, Producer: "camera")),
                Instance("main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "view"))
            ),
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(OverGraph(reader: false), ("world", "view"))
        );

        Assert.Equal(
            actual: (refusal.Code, Names: string.Join(separator: " ", values: refusal.Names)),
            expected: (RenderGraphRuntimeRefusalCode.StandingChain, Names: "main view camera")
        );

        // One previous-frame read in the chain resolves through the two recorded outputs.
        using (Runtime(
            gpu,
            recorders,
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(Producer: "camera")),
                Instance("main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "view"))
            ),
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(OverGraph(reader: false), ("world", "view"))
        )) {
        }

        // A root that draws over the view's previous frame stands for nothing.
        using (Runtime(
            gpu,
            recorders,
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(PreviousFrame: true, Producer: "camera")),
                Instance("main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "view"))
            ),
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(ScreensGraph(false, "near"), ("near", "view"))
        )) {
        }
    }
    /// <summary>A package that turns a buffer into an image always draws, so its previous-frame buffer read breaks a
    /// standing chain even when a root may stand for that package's previous image.</summary>
    [Fact]
    public void APreviousFrameBufferReadBreaksAStandingChain() {
        const string View = "test.buffer-view";
        var catalog = new RenderGraphPackageCatalog(packages: [
            .. Catalog.Packages,
            new RenderGraphPackage(
                Id: View,
                Inputs: [RenderGraphPackagePort.Buffer(access: RenderGraphPortAccess.ComputeRead, count: null, strideBytes: null)],
                Members: [],
                Outputs: [RenderGraphPackagePort.Image(access: RenderGraphPortAccess.ComputeWrite)],
                Summary: "A view drawn from a buffer."
            ),
        ]);
        var plan = new RenderGraphCompiler(packages: catalog).Compile(definition: new RenderGraphDefinition(
            Name: "buffer-view",
            Outputs: ["image"],
            Packages: [new RenderGraphPackagePass(Inputs: ["pool"], Name: "view", Outputs: ["image"], Package: View)],
            Resources: [
                new ShaderPipelineResource(Initialization: ShaderPipelineInitialization.External,
                    Kind: ShaderPipelineResourceKind.Buffer, Name: "pool", SizeBytes: PoolBytes),
                Image(name: "image"),
            ],
            Schema: RenderGraphSchemas.Graph
        ));
        using var runtime = Runtime(
            new FakePipelineGpu(),
            new Recorders(Pool, View, Over),
            Set(
                Instance("pool", output: ShaderPipelineResourceKind.Buffer),
                Instance("view", reads: new RenderGraphRead(Kind: ShaderPipelineResourceKind.Buffer, PreviousFrame: true, Producer: "pool")),
                Instance("main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "view"))
            ),
            "main",
            Graph(pipeline: PoolGraph()),
            Graph(new CompiledShaderPipeline(plan: plan.Pipeline, shaders: new Dictionary<string, CompiledShader>()), ("pool", "pool")),
            Graph(OverGraph(reader: false), ("world", "view"))
        );
    }
    /// <summary>A package whose output is retained as history must draw into its own image, so it breaks a standing
    /// chain across two previous-frame instance reads.</summary>
    [Fact]
    public void AHistoryOutputBreaksAStandingChain() {
        var definition = OverGraph(reader: false).Plan.Definition;
        var history = Compile(definition: definition with {
            Resources = [.. definition.Resources.Select(selector: resource => ((resource.Name == "composed")
                ? resource with { History = true, Initialization = ShaderPipelineInitialization.Zero }
                : resource))],
        });
        using var runtime = Runtime(
            new FakePipelineGpu(),
            new Recorders(Camera, Over),
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(PreviousFrame: true, Producer: "camera")),
                Instance("main", reads: new RenderGraphRead(PreviousFrame: true, Producer: "view"))
            ),
            "main",
            Graph(pipeline: CameraGraph()),
            Graph(history, ("world", "camera")),
            Graph(OverGraph(reader: false), ("world", "view"))
        );
    }
    /// <summary>A view stands for the camera, and a kept root's installed graph reads the view. A reconfiguration retires
    /// both while the root's replacement waits in the driver: the root keeps sampling the image it bound, the camera's,
    /// under a lease, though neither the view nor the camera holds it any more; once the replacement installs, the image
    /// is disposed exactly once.</summary>
    [Fact]
    public void AKeptConsumerSamplesAnImageWhoseWholeStandingChainRetired() {
        using var scene = new StandingScene(
            ["view"],
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(Producer: "camera")),
                Instance("main", reads: new RenderGraphRead(Producer: "view"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(ScreensGraph(false, "near"), ("near", "view"))
        );
        RenderGraphFootprint[] filmed = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "camera", Width: 1.0),
        ];
        var camera = SettleStandingView(
            filmed: filmed,
            scene: scene
        );
        var main = scene.Node(instance: "main");

        Assert.True(
            condition: scene.Runtime.TryReconfigure(
                // A replacement with a shader pass of its own, whose pipeline the held driver creates.
                graphs: [Graph(pipeline: ScreensGraph(pool: false))],
                refusal: out var refusal,
                root: "main",
                set: Set(Instance(name: "main"))
            ),
            userMessage: refusal?.Message
        );

        using (var opener = new PipelineGateOpener()) {
            scene.Gpu.PipelineGate = opener.Gate;
            scene.Gpu.Recording = true;

            try {
                for (var frame = 0; (frame < 4); frame++) {
                    scene.Gpu.DescriptorWrites.Clear();
                    _ = scene.Produce(footprints: [], roots: MainRoot);

                    Assert.True(condition: main.HasPendingCandidate);
                    Assert.Contains(
                        collection: scene.Gpu.DescriptorWrites,
                        filter: write => (write.Handle == (camera + 1))
                    );
                    Assert.False(condition: scene.Gpu.IsReleased(handle: camera));
                }
            } finally {
                scene.Gpu.PipelineGate = null;
                scene.Gpu.Recording = false;
            }
        }

        main.WaitForBuild();

        for (var frame = 0; (frame < 4); frame++) {
            _ = scene.Produce(footprints: [], roots: MainRoot);
        }

        Assert.False(condition: main.HasPendingCandidate);
        Assert.Equal(
            actual: scene.Gpu.CreatedObjects.Single(predicate: created => (created.Handle == camera)).DisposeCount,
            expected: 1
        );
    }
    /// <summary>A view stands for the camera and the root reads the view. A reconfiguration retires the camera while the
    /// view's replacement builds, so the view's installed graph keeps the camera's image under a hold and the root binds
    /// it through the view. The fake queue finishes the view's submission of the next frame but not the root's, which
    /// reads the camera's image; the view then installs its replacement and releases its hold. The image survives the
    /// root's pending submission, under the root's own lease, and is disposed only once that submission has
    /// finished.</summary>
    [Fact]
    public void AReadersPendingSubmissionOutlivesTheRetirementThatDroppedTheImageItReads() {
        using var scene = new StandingScene(
            ["view"],
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(Producer: "camera")),
                Instance("main", reads: new RenderGraphRead(Producer: "view"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(ScreensGraph(false, "near"), ("near", "view"))
        );
        RenderGraphFootprint[] filmed = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "camera", Width: 1.0),
        ];
        var camera = SettleStandingView(
            filmed: filmed,
            scene: scene
        );
        var view = scene.Node(instance: "view");
        RenderGraphFootprint[] shown = [filmed[0]];

        Assert.True(
            condition: scene.Runtime.TryReconfigure(
                graphs: [Graph(pipeline: ScreensGraph(pool: false)), null],
                refusal: out var refusal,
                root: "main",
                set: Set(
                    Instance(name: "view"),
                    Instance("main", reads: new RenderGraphRead(Producer: "view"))
                )
            ),
            userMessage: refusal?.Message
        );

        scene.Gpu.TracksPendingReads = true;
        scene.Gpu.Recording = true;
        // The next frame's first submission, the view's, finishes; the root's after it stays pending.
        scene.Gpu.CompletedThrough = (scene.Gpu.SubmissionsMade + 1L);
        _ = scene.Produce(footprints: shown, roots: MainRoot);
        scene.Gpu.Recording = false;

        Assert.True(condition: view.HasPendingCandidate);
        Assert.Contains(
            collection: scene.Gpu.DescriptorWrites,
            filter: write => (write.Handle == (camera + 1))
        );

        view.WaitForBuild();

        // The view installs, releases its hold and renders its own images while the root's read stays pending until the
        // root waits on it. Then the queue catches up, as a device's does, and the image goes once nothing holds it.
        for (var frame = 0; (frame < 4); frame++) {
            _ = scene.Produce(footprints: shown, roots: MainRoot);
        }

        scene.Gpu.CompletedThrough = long.MaxValue;

        for (var frame = 0; (frame < 8); frame++) {
            _ = scene.Produce(footprints: shown, roots: MainRoot);
        }

        Assert.False(condition: view.HasPendingCandidate);
        Assert.True(condition: scene.Gpu.IsReleased(handle: camera));
    }

    // Produces until the view stands for the camera's latest image, and returns that image's handle.
    private static nint SettleStandingView(StandingScene scene, RenderGraphFootprint[] filmed) {
        TestLiveness.Until(
            reason: () => "The view never stood for the camera's image.",
            step: () => {
                _ = scene.Produce(footprints: filmed, roots: MainRoot);

                return (
                    scene.Runtime.IsSettled &&
                    (scene.Node(instance: "view").PublishedBinding == "world") &&
                    scene.Runtime.TryLatestImage(image: out var image, instance: "view") &&
                    (image.ImageHandle == scene.Recorders.Of(instance: "camera").OutputImage)
                );
            }
        );
        _ = scene.Produce(footprints: filmed, roots: MainRoot);

        return scene.Recorders.Of(instance: "camera").OutputImage;
    }
    // Draws the loop for a while, then has every pass in it draw nothing: each frame the root still has a completed image,
    // which the display is handed and which is no stand-in, and the root's own pass draws rather than standing for itself.
    private static void AssertFeedbackKeepsAnImage(StandingScene scene, RenderGraphFootprint[] shown, string[] loop) {
        scene.StandsIn = false;
        TestLiveness.Until(
            reason: () => "The loop never settled.",
            step: () => {
                _ = scene.Produce(footprints: shown, roots: MainRoot);

                return (scene.Runtime.IsSettled && loop.All(predicate: name => scene.Node(instance: name).IsReady));
            }
        );
        _ = scene.Produce(footprints: shown, roots: MainRoot);
        scene.StandsIn = true;

        for (var frame = 0; (frame < 8); frame++) {
            var displayed = scene.Produce(footprints: shown, roots: MainRoot);

            Assert.NotEqual(
                actual: displayed.ImageHandle,
                expected: 0
            );
            Assert.True(condition: scene.Runtime.TryLatestImage(image: out var latest, instance: "main"));
            Assert.Equal(
                actual: latest.ImageHandle,
                expected: displayed.ImageHandle
            );
            Assert.Null(@object: scene.Node(instance: "main").PublishedBinding);
        }
    }
}

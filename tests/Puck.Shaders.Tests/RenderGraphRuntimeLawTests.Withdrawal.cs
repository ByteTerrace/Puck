using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// Step-2 withdrawal provenance laws: carrying values, one output writer, filtered output reads, frame latches, and
// record-time validation of held bindings while replacement graphs are still building.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string ProvenanceFeed = "test.provenance";

    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public void AProducerCutAndRemovedByAReconfigurationInTheSameFrameLeavesNoReaderItsImage(bool cutBeforeReconfigure) {
        using var scene = new StandingScene(
            ["view"],
            Set(
                Instance(name: "camera"),
                Instance("view", reads: new RenderGraphRead(Producer: "camera")),
                Instance("paused", reads: new RenderGraphRead(Producer: "view")),
                Instance("consumer", reads: new RenderGraphRead(Producer: "view")),
                Instance("main", reads: new RenderGraphRead(Producer: "consumer"))
            ),
            Graph(pipeline: CameraGraph()),
            Graph(OverGraph(reader: false), ("world", "camera")),
            Graph(ScreensGraph(false, "near"), ("near", "view")),
            Graph(ScreensGraph(false, "near"), ("near", "view")),
            Graph(ScreensGraph(false, "near"), ("near", "consumer"))
        );

        var spans = new DeliverySpanSource();
        var cutSpan = spans.Span;

        scene.Runtime.SetRoute(instance: "camera", route: DeliveryLabel.Of(span: cutSpan));

        RenderGraphFootprint[] beforeFootprints = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "consumer", Width: 1.0),
            new RenderGraphFootprint(Consumer: "consumer", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "paused", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "camera", Width: 1.0),
        ];
        RenderGraphRoot[] beforeRoots = [
            .. MainRoot,
            new RenderGraphRoot(Height: 1.0, Instance: "paused", Width: 1.0),
        ];

        TestLiveness.Until(
            reason: () => "The camera, standing view, paused reader, consumer and root never settled.",
            step: () => {
                _ = scene.Produce(footprints: beforeFootprints, roots: beforeRoots);

                return (
                    scene.Runtime.IsSettled &&
                    (scene.Node(instance: "view").PublishedBinding == "world") &&
                    scene.Runtime.TryLatestImage(image: out var viewImage, instance: "view") &&
                    (viewImage.ImageHandle == scene.Recorders.Of(instance: "camera").OutputImage) &&
                    scene.Runtime.TryLatestImage(image: out _, instance: "paused")
                );
            }
        );

        scene.Gpu.Recording = true;
        scene.Gpu.DescriptorWrites.Clear();
        _ = scene.Produce(footprints: beforeFootprints, roots: beforeRoots);

        var cameraImage = scene.Recorders.Of(instance: "camera").OutputImage;
        var cameraView = (cameraImage + 1);

        // Positive control: before the cut, the consumer samples the camera's image view and the paused reader has a
        // completed image derived from it.
        Assert.Contains(
            collection: scene.Gpu.DescriptorWrites,
            filter: write => (write.Handle == cameraView)
        );
        Assert.True(condition: scene.Runtime.TryLatestImage(image: out _, instance: "paused"));
        scene.Node(instance: "paused").Paused = true;

        var consumer = scene.Node(instance: "consumer");

        if (cutBeforeReconfigure) {
            _ = spans.Advance();
        }

        Assert.True(
            condition: scene.Runtime.TryReconfigure(
                graphs: [
                    Graph(pipeline: ScreensGraph(pool: false)),
                    Graph(pipeline: ScreensGraph(pool: false)),
                    null,
                ],
                refusal: out var refusal,
                root: "main",
                set: Set(
                    Instance(name: "paused"),
                    Instance(name: "consumer"),
                    Instance("main", reads: new RenderGraphRead(Producer: "consumer"))
                )
            ),
            userMessage: refusal?.Message
        );

        if (!cutBeforeReconfigure) {
            _ = spans.Advance();
        }

        RenderGraphFootprint[] afterFootprints = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "consumer", Width: 1.0),
        ];

        using (var opener = new PipelineGateOpener()) {
            scene.Gpu.PipelineGate = opener.Gate;

            try {
                for (var frame = 0; (frame < 3); frame++) {
                    scene.Gpu.DescriptorWrites.Clear();
                    var shown = scene.Produce(footprints: afterFootprints, named: ["paused"], roots: MainRoot);

                    Assert.True(condition: consumer.HasPendingCandidate);
                    Assert.DoesNotContain(
                        collection: scene.Gpu.DescriptorWrites,
                        filter: write => (write.Handle == cameraView)
                    );
                    Assert.False(condition: scene.Runtime.TryLatestImage(image: out _, instance: "paused"));
                    Assert.NotEqual(expected: cameraImage, actual: shown.ImageHandle);
                }
            } finally {
                scene.Gpu.PipelineGate = null;
                scene.Gpu.Recording = false;
            }
        }

        consumer.WaitForBuild();
    }

    [Fact]
    public void AnUnclassifiedImageOrBufferIsRefusedAtEveryBind() {
        var gpu = new FakePipelineGpu {
            Recording = true,
        };
        var recorders = new Recorders();
        var feed = new ProvenanceSource(gpu: gpu) {
            Label = DeliveryLabel.Unclassified,
        };
        var screen = new ProvenanceScreen();

        recorders.Registry.RegisterProducer(
            factory: _ => feed,
            package: ProvenanceFeed
        );
        recorders.Registry.Register(
            factory: screen,
            package: Camera
        );

        using (var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            height: 16,
            hostsOnDirectX: false,
            name: "direct",
            pipelines: new GpuPassPipelineCache(),
            width: 16
        )) {
            node.Swap(pipeline: ScreensGraph(pool: true, "near"));

            using var buffer = gpu.CreateDeviceLocal(
                name: new GpuObjectName("test", "pool"),
                sizeBytes: PoolBytes,
                usage: GpuBufferUsage.Storage
            );
            var holdRetired = 0;

            _ = Assert.Throws<ArgumentException>(testCode: () => node.BindImage(
                image: default,
                name: "near"
            ));
            _ = Assert.Throws<ArgumentException>(testCode: () => node.BindImage(
                image: new ShaderPipelineExternalImage(
                    Format: GpuPixelFormat.R8G8B8A8Unorm,
                    Height: 16,
                    ImageHandle: 0x5100,
                    ImageViewHandle: 0x5101,
                    Label: DeliveryLabel.Unclassified,
                    Width: 16
                ),
                name: "near"
            ));
            _ = Assert.Throws<ArgumentException>(testCode: () => node.HoldBinding(
                image: new ShaderPipelineExternalImage(
                    Format: GpuPixelFormat.R8G8B8A8Unorm,
                    Height: 16,
                    ImageHandle: 0x5100,
                    ImageViewHandle: 0x5101,
                    Label: DeliveryLabel.Unclassified,
                    Width: 16
                ),
                lease: new GpuImageLease(
                    ImageViewHandle: 0x5101,
                    Release: _ => holdRetired++
                ),
                name: "near"
            ));
            Assert.Equal(expected: 1, actual: holdRetired);

            _ = Assert.Throws<ArgumentException>(testCode: () => node.BindBuffer(
                buffer: default,
                name: "pool"
            ));
            _ = Assert.Throws<ArgumentException>(testCode: () => node.BindBuffer(
                buffer: new ShaderPipelineExternalBuffer(
                    buffer: buffer,
                    label: DeliveryLabel.Unclassified
                ),
                name: "pool"
            ));
        }

        var readRetired = 0;
        var directReads = new RenderGraphExternalReads(producers: ["feed"]);

        _ = Assert.Throws<ArgumentException>(testCode: () => directReads.Bind(
            image: Surface.SameDeviceImage(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: 4,
                imageHandle: 0x5200,
                imageViewHandle: 0x5201,
                width: 4
            ),
            index: 0,
            label: DeliveryLabel.Unclassified,
            layout: GpuImageLayout.ShaderReadOnly,
            lease: new GpuImageLease(
                ImageViewHandle: 0x5201,
                Release: _ => readRetired++
            )
        ));
        Assert.Equal(expected: 1, actual: readRetired);

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(
                new RenderGraphInstance(
                    ExternalPackage: ProvenanceFeed,
                    Name: "feed",
                    Passes: 1,
                    Reads: [],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
                Instance("reader", reads: new RenderGraphRead(Producer: "feed")),
                Instance("main", reads: [
                    new RenderGraphRead(Producer: "feed"),
                    new RenderGraphRead(Producer: "reader"),
                ])
            ),
            "main",
            null!,
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(false, "near", "other"), ("near", "feed"), ("other", "reader"))
        );

        var footprints = new[] {
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "feed", Width: 1.0),
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "reader", Width: 1.0),
            new RenderGraphFootprint(Consumer: "reader", Height: 1.0, Producer: "feed", Width: 1.0),
        };
        var roots = new[] { new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0) };
        var frames = new Frames(footprints: footprints, roots: roots, runtime: runtime);

        TestLiveness.Until(
            reason: () => "The consumer graphs never installed.",
            step: () => {
                _ = frames.Next();

                return (runtime.NodeOf(instance: "main")!.IsReady && runtime.NodeOf(instance: "reader")!.IsReady);
            }
        );

        gpu.DescriptorWrites.Clear();
        screen.SeenViews.Clear();
        _ = frames.Next();

        Assert.NotEqual(expected: 0, actual: feed.ImageView);
        Assert.DoesNotContain(
            collection: gpu.DescriptorWrites,
            filter: write => (write.Handle == feed.ImageView)
        );
        Assert.Equal(expected: [0], actual: screen.SeenViews);
        Assert.Equal(expected: feed.Acquired, actual: feed.Released);
    }

    [Fact]
    public void AHeldBindingWhoseSpanEndedIsReplacedWhenItsNodeNextRecords() {
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

        var spans = new DeliverySpanSource();

        scene.Runtime.SetRoute(instance: "camera", route: DeliveryLabel.Of(span: spans.Span));

        RenderGraphFootprint[] filmed = [
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "view", Width: 1.0),
            new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "camera", Width: 1.0),
        ];
        var camera = SettleStandingView(filmed: filmed, scene: scene);
        var cameraView = (camera + 1);
        var main = scene.Node(instance: "main");

        Assert.True(
            condition: scene.Runtime.TryReconfigure(
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
                // Positive control: while the span stays open and the replacement builds, the node samples the held
                // camera image.
                scene.Gpu.DescriptorWrites.Clear();
                _ = scene.Produce(footprints: [], roots: MainRoot);
                Assert.True(condition: main.HasPendingCandidate);
                Assert.Contains(
                    collection: scene.Gpu.DescriptorWrites,
                    filter: write => (write.Handle == cameraView)
                );
                Assert.False(condition: scene.Gpu.IsReleased(handle: camera));

                // Ending the span causes the next recording of the still-installed graph to replace the held binding
                // with the stand-in and release the hold.
                spans.Close();
                scene.Gpu.DescriptorWrites.Clear();
                _ = scene.Produce(footprints: [], roots: MainRoot);
                Assert.True(condition: main.HasPendingCandidate);
                Assert.DoesNotContain(
                    collection: scene.Gpu.DescriptorWrites,
                    filter: write => (write.Handle == cameraView)
                );

                for (var frame = 0; (frame < 4); frame++) {
                    scene.Gpu.DescriptorWrites.Clear();
                    _ = scene.Produce(footprints: [], roots: MainRoot);
                    Assert.True(condition: main.HasPendingCandidate);
                    Assert.DoesNotContain(
                        collection: scene.Gpu.DescriptorWrites,
                        filter: write => (write.Handle == cameraView)
                    );
                }

                Assert.True(condition: scene.Gpu.IsReleased(handle: camera));
            } finally {
                scene.Gpu.PipelineGate = null;
                scene.Gpu.Recording = false;
            }
        }

        main.WaitForBuild();
    }

    [Fact]
    public void ACutAcceptedBetweenTheLatchAndTheReturnChangesNothingInThatFrameAndEverythingInTheNext() {
        var gpu = new FakePipelineGpu {
            Recording = true,
        };
        var recorders = new Recorders();
        var spans = new DeliverySpanSource();
        var feed = new ProvenanceSource(gpu: gpu) {
            Label = DeliveryLabel.Of(span: spans.Span),
        };
        var screen = new ProvenanceScreen();

        recorders.Registry.RegisterProducer(
            factory: _ => feed,
            package: ProvenanceFeed
        );
        recorders.Registry.Register(
            factory: screen,
            package: Camera
        );

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(
                new RenderGraphInstance(
                    ExternalPackage: ProvenanceFeed,
                    Name: "feed",
                    Passes: 1,
                    Reads: [],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
                Instance("reader", reads: new RenderGraphRead(Producer: "feed")),
                Instance("main", reads: [
                    new RenderGraphRead(Producer: "feed"),
                    new RenderGraphRead(Producer: "reader"),
                ])
            ),
            "main",
            null!,
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(false, "near", "other"), ("near", "feed"), ("other", "reader"))
        );

        var footprints = new[] {
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "feed", Width: 1.0),
            new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "reader", Width: 1.0),
            new RenderGraphFootprint(Consumer: "reader", Height: 1.0, Producer: "feed", Width: 1.0),
        };
        var roots = new[] { new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0) };
        var frames = new Frames(footprints: footprints, roots: roots, runtime: runtime);

        frames.Settle();
        var feedView = feed.ImageView;

        Assert.NotEqual(expected: 0, actual: feedView);

        // Cut the span mid-frame inside the producer's Produce, after ProduceFrame captured its latch and before
        // reader, main and RootImage decide against that latch.
        var cutTriggered = false;
        feed.OnProduce = () => {
            if (!cutTriggered) {
                cutTriggered = true;
                spans.Close();
            }
        };

        gpu.DescriptorWrites.Clear();
        screen.SeenViews.Clear();
        var duringCut = frames.Next();

        Assert.True(condition: cutTriggered);
        Assert.False(condition: duringCut.IsEmpty);
        Assert.True(condition: runtime.Render.IsRendered);
        Assert.Contains(
            collection: gpu.DescriptorWrites,
            filter: write => (write.Handle == feedView)
        );
        Assert.Equal(expected: [feedView], actual: screen.SeenViews);

        // Once ProduceFrame has returned, any new latch (and the next frame's latch) sees the ended span: pre-cut
        // outputs are filtered out, pausing main yields nothing, and resuming re-renders over stand-ins.
        Assert.False(condition: runtime.TryLatestImage(image: out _, instance: "main"));
        runtime.NodeOf(instance: "main")!.Paused = true;
        gpu.DescriptorWrites.Clear();
        screen.SeenViews.Clear();
        var pausedNext = frames.Next();

        Assert.True(condition: pausedNext.IsEmpty);
        Assert.False(condition: runtime.Render.IsRendered);
        Assert.False(condition: runtime.TryLatestImage(image: out _, instance: "main"));
        Assert.DoesNotContain(
            collection: gpu.DescriptorWrites,
            filter: write => (write.Handle == feedView)
        );
        Assert.Equal(expected: [0], actual: screen.SeenViews);

        runtime.NodeOf(instance: "main")!.Paused = false;
        gpu.DescriptorWrites.Clear();
        screen.SeenViews.Clear();
        var rerenderedNext = frames.Next();

        Assert.False(condition: rerenderedNext.IsEmpty);
        Assert.DoesNotContain(
            collection: gpu.DescriptorWrites,
            filter: write => (write.Handle == feedView)
        );
        Assert.Equal(expected: [0], actual: screen.SeenViews);
    }

    private sealed class ProvenanceSource(FakePipelineGpu gpu) : IRenderGraphExternalProducer {
        private IGpuImage? m_image;

        public int Acquired { get; private set; }
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public nint ImageView => (m_image?.ImageViewHandle ?? 0);
        public DeliveryLabel Label { get; set; }
        public string? NotReadyReason => ((m_image is null) ? "the provenance source has not produced" : null);
        public Action? OnProduce { get; set; }
        public string? PendingCapturePath => null;
        public int Released { get; private set; }
        public IGpuWorkSource Work { get; } = new GpuWorkLedger(framesInFlight: 3, name: ProvenanceFeed);

        public void Dispose() {
            m_image?.Dispose();
            m_image = null;
        }
        public void OnDeviceLost() => Dispose();
        public FrameRender Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            m_image ??= gpu.Create(
                format: Format,
                height: height,
                name: default,
                usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
                width: width
            );
            OnProduce?.Invoke();

            return FrameRender.Rendered;
        }
        public void RequestCapture(FrameCaptureRequest request) =>
            _ = request.TryFail(error: new NotSupportedException());
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            if (m_image is not { } image) {
                output = default;

                return false;
            }

            Acquired++;
            output = new RenderGraphExternalOutput(
                image: Surface.SameDeviceImage(
                    format: Format,
                    height: image.Height,
                    imageHandle: image.ImageHandle,
                    imageViewHandle: image.ImageViewHandle,
                    width: image.Width
                ),
                label: Label,
                layout: GpuImageLayout.ShaderReadOnly,
                lease: new GpuImageLease(
                    ImageViewHandle: image.ImageViewHandle,
                    Publication: new ReferenceRevision(Owner: this, Revision: 1),
                    Release: _ => Released++
                )
            );

            return true;
        }
    }

    private sealed class ProvenanceScreen : IRenderGraphPackageFactory {
        public bool SamplesReads => true;
        public List<nint> SeenViews { get; } = [];

        public ValueTask<IDisposable?> BuildAsync(RenderGraphPackageRecorderContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IDisposable?>(result: null);
        public IRenderGraphPackageRecorder Create(RenderGraphPackageRecorderContext context, IDisposable? built, RenderGraphPackageGroups groups) =>
            new Recorder(owner: this);

        private sealed class Recorder(ProvenanceScreen owner) : IRenderGraphPackageRecorder {
            public void Dispose() { }
            public RenderGraphPackageOutcome Record(in RenderGraphPackageRecording recording) {
                var view = ((
                    (recording.Reads is { } reads) &&
                    (reads.IndexOf(producer: "feed") is var index and >= 0) &&
                    reads.TrySample(imageViewHandle: out var sampled, index: index, latch: recording.Latch, leases: recording.Leases))
                    ? sampled
                    : 0);

                owner.SeenViews.Add(item: view);

                return RenderGraphPackageOutcome.Drew;
            }
        }
    }
}

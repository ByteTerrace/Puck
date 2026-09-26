using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// External producers: an instance a producer renders through its own submissions, as sdf.world's engine does, and the
// leases its consumers hold on its output. Also the install-time refusals that keep a consumer from binding what its
// producer does not publish, and the layout a package recorder is told its images are in.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string World = "test.world";
    // The SDF engine's pass count, which prices the world producer.
    private const int WorldPasses = 10;

    private static RenderGraphInstance External(string name, RenderGraphRefresh? refresh = null) => new(
        ExternalPackage: World,
        Name: name,
        Passes: WorldPasses,
        Reads: [],
        Refresh: (refresh ?? RenderGraphRefresh.EveryFrame)
    );
    // The world producer and a view showing it through one external version.
    private static (RenderGraphRuntime Runtime, Frames Frames, Producers Producers) WorldScene(FakePipelineGpu gpu, RenderGraphRefresh? refresh = null) {
        var producers = new Producers(gpu: gpu);
        var recorders = new Recorders();

        producers.Register(registry: recorders.Registry);

        var set = Set(
            External(
                name: "world",
                refresh: refresh
            ),
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "world")
            )
        );
        var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            null!,
            Graph(ScreensGraph(false, "screen"), ("screen", "world"))
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "world", Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        return (runtime, frames, producers);
    }

    [Fact]
    public void AGraphReadingAnExternalProducerBindsItsLatestOutput() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, producers) = WorldScene(gpu: gpu);

        using (runtime) {
            var world = producers.Only;

            // Until the producer completes a frame, the view shows the stand-in.
            world.Holding = true;
            frames.Next(count: 2);
            Assert.Equal(expected: (0, 0), actual: (world.Produced, world.Acquired));

            world.Holding = false;
            frames.Settle();
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            frames.Next(count: 4);
            gpu.Recording = false;

            // Produced first at the scheduled extent, then bound: every render samples the latest output.
            Assert.Equal(expected: (((uint)Display), ((uint)Display)), actual: world.Extent);
            Assert.Equal(
                actual: gpu.DescriptorWrites.Where(predicate: static write => (write.Binding == 1)).Select(selector: static write => write.Handle),
                expected: Enumerable.Repeat(
                    count: 4,
                    element: world.ImageView
                )
            );
            Assert.Same(
                actual: runtime.Work(instance: runtime.Instances.IndexOf(name: "world")),
                expected: world.Work
            );
            Assert.Same(
                actual: runtime.Producer(instance: runtime.Instances.IndexOf(name: "world")),
                expected: world
            );
            Assert.Throws<ArgumentException>(testCode: () => runtime.Node(instance: runtime.Instances.IndexOf(name: "world")));
        }
    }
    // A kept root presents its installed graph while its replacement builds, and that graph still reads an external
    // producer the reconfiguration removed, whose lease served one frame: the producer is held, not disposed, and the
    // root samples its last output on every frame until the replacement installs; then every acquisition is released
    // and the producer is disposed once.
    [Fact]
    public void ARemovedExternalProducerAKeptRootStillReadsIsHeldUntilTheRootsReplacementInstalls() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, producers) = WorldScene(gpu: gpu);

        using (runtime) {
            var world = producers.Only;
            var main = runtime.NodeOf(instance: "main")!;

            frames.Settle();

            var view = world.ImageView;

            Assert.True(
                condition: runtime.TryReconfigure(
                    graphs: [Graph(pipeline: ScreensGraph(pool: false))],
                    refusal: out var refusal,
                    root: "main",
                    set: Set(Instance(name: "main"))
                ),
                userMessage: refusal?.Message
            );

            var alone = new Frames(
                footprints: [],
                roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
                runtime: runtime
            );

            using (var opener = new PipelineGateOpener()) {
                gpu.PipelineGate = opener.Gate;
                gpu.DescriptorWrites.Clear();
                gpu.Recording = true;

                try {
                    // The replacement, which reads no screen, waits in the driver, so each of these frames records the
                    // installed graph over the removed producer's last output, bound once for all of them.
                    for (var frame = 0; (frame < 3); frame++) {
                        _ = alone.Next();
                        Assert.True(condition: main.HasPendingCandidate);
                        Assert.Equal(
                            actual: (world.Disposals, runtime.RetiredProducers),
                            expected: (0, 1)
                        );
                    }
                } finally {
                    gpu.Recording = false;
                    gpu.PipelineGate = null;
                }
            }

            Assert.All(
                action: write => Assert.Equal(
                    actual: write.Handle,
                    expected: view
                ),
                collection: gpu.DescriptorWrites.Where(predicate: static write => (write.Binding == 1))
            );

            main.WaitForBuild();
            _ = alone.Next();
            Assert.False(condition: main.HasPendingCandidate);
            Assert.Equal(
                actual: (world.Disposals, runtime.RetiredProducers, (world.Acquired - world.Released)),
                expected: (1, 0, 0)
            );
        }
    }
    [Fact]
    public void ALeaseRetiresOnlyAfterTheSamplingSlotsFence() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, producers) = WorldScene(gpu: gpu);

        using (runtime) {
            var world = producers.Only;

            frames.Settle();
            frames.Next(count: 4);
            gpu.Events.Clear();
            gpu.Recording = true;
            frames.Next(count: 8);
            gpu.Recording = false;

            // Each frame slot holds the lease its submission sampled until the frame that reuses the slot has waited its
            // fence: three slots in flight, and every release follows a fence wait.
            Assert.Equal(
                actual: (world.Acquired - world.Released),
                expected: 3
            );

            var releases = 0;

            for (var index = 0; (index < gpu.Events.Count); index++) {
                if (gpu.Events[index] != Producers.ReleaseEvent) {
                    continue;
                }

                releases++;
                Assert.StartsWith(
                    actualString: gpu.Events[(index - 1)],
                    expectedStartString: "wait fence"
                );
            }

            Assert.Equal(
                actual: releases,
                expected: 8
            );
        }
    }
    [Fact]
    public void ASkippedProducerFrameRebindsThePreviousOutput() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, producers) = WorldScene(
            gpu: gpu,
            refresh: RenderGraphRefresh.Every(divisor: 2)
        );

        using (runtime) {
            var world = producers.Only;

            frames.Settle();

            var produced = world.Produced;

            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            frames.Next(count: 8);
            gpu.Recording = false;

            // The world renders every other frame, and the view binds its latest output on every frame it renders.
            Assert.Equal(
                actual: (world.Produced - produced),
                expected: 4
            );
            Assert.Equal(
                actual: gpu.DescriptorWrites.Count(predicate: write => ((write.Binding == 1) && (write.Handle == world.ImageView))),
                expected: 8
            );
        }
    }
    [Fact]
    public void AProducerThatCouldNotProduceIsAskedAgainOnTheNextFrame() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, producers) = WorldScene(
            gpu: gpu,
            refresh: RenderGraphRefresh.Every(divisor: 4)
        );

        using (runtime) {
            var world = producers.Only;

            world.Holding = true;
            frames.Next(count: 1);
            Assert.Equal(expected: 0, actual: world.Produced);

            // The render it could not produce was withdrawn, so it is due again at once rather than a divisor later, and
            // its divisor counts from the frame it completed.
            world.Holding = false;
            frames.Next(count: 1);
            Assert.Equal(expected: 1, actual: world.Produced);
            frames.Next(count: 3);
            Assert.Equal(expected: 1, actual: world.Produced);
            frames.Next(count: 1);
            Assert.Equal(expected: 2, actual: world.Produced);
        }
    }
    [Fact]
    public void ADeviceLossAndDisposalReleaseEveryLease() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, producers) = WorldScene(gpu: gpu);
        var world = producers.Only;

        frames.Settle();
        Assert.NotEqual(
            actual: world.Released,
            expected: world.Acquired
        );

        runtime.OnDeviceLost();
        Assert.Equal(
            actual: (world.Released, world.DeviceLosses),
            expected: (world.Acquired, 1)
        );

        frames.Settle();
        runtime.Dispose();
        Assert.Equal(
            actual: (world.Released, world.Disposals),
            expected: (world.Acquired, 1)
        );
    }
    [Fact]
    public void ASteadyFrameWithAnExternalProducerAllocatesNothing() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, _) = WorldScene(gpu: gpu);

        using (runtime) {
            frames.Settle();
            frames.Next(count: 8);

            Assert.Equal(
                actual: AllocationWindow.Least(window: () => frames.Next(count: 64)),
                expected: 0L
            );
        }
    }
    [Fact]
    public void AnExternalInstanceIsRefusedByNameWithAGraphOrWithoutAProducer() {
        var gpu = new FakePipelineGpu();
        var set = Set(
            External(name: "world"),
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "world")
            )
        );
        var main = Graph(ScreensGraph(false, "screen"), ("screen", "world"));
        var served = new Recorders();

        new Producers(gpu: gpu).Register(registry: served.Registry);

        Assert.Equal(
            actual: Refusal(gpu, served, set, "main", Graph(pipeline: CameraGraph()), main).Code,
            expected: RenderGraphRuntimeRefusalCode.ExternalProducer
        );
        Assert.Equal(
            actual: Refusal(gpu, new Recorders(), set, "main", null!, main).Message,
            expected: $"External instance 'world' of package '{World}' names a package no external producer serves."
        );
        Assert.Equal(
            actual: Refusal(gpu, served, set, "nowhere", null!, main).Code,
            expected: RenderGraphRuntimeRefusalCode.Root
        );
    }
    // The external producer hands its image out in General, and the root publishes in ShaderReadOnly, the layout the
    // display's descriptor is written with: a pass over the producer's image may not leave its output standing for it,
    // since the root would then publish the producer's image in General. It draws instead, and a recording that draws
    // nothing anyway is refused by name.
    [Fact]
    public void APassOverAnExternalImageInAnotherLayoutMayNotStandForItAndPublishesItsOwnOutput() {
        var gpu = new FakePipelineGpu();
        var producers = new Producers(gpu: gpu);
        var recorders = new Recorders(Over);

        producers.Register(registry: recorders.Registry);

        var runtime = Runtime(
            gpu,
            recorders,
            Set(
                External(name: "world"),
                Instance(
                    name: "main",
                    reads: new RenderGraphRead(Producer: "world")
                )
            ),
            "main",
            null!,
            Graph(OverGraph(reader: false), ("world", "world"))
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "world", Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        using (runtime) {
            frames.Settle();

            var over = recorders.Of(instance: "main");
            var main = runtime.Node(instance: runtime.Instances.IndexOf(name: "main"));
            var shown = frames.Next();

            Assert.False(condition: over.MayStandIn);
            Assert.Equal(
                actual: (shown.ImageHandle, main.PublishedLayout),
                expected: (over.OutputImage, GpuImageLayout.ShaderReadOnly)
            );
            Assert.NotEqual(
                actual: shown.ImageHandle,
                expected: producers.Only.Image
            );

            over.Outcome = RenderGraphPackageOutcome.DrewNothing;

            Assert.Equal(
                actual: Assert.Throws<InvalidOperationException>(testCode: () => frames.Next()).Message,
                expected: "Package pass 'over' drew nothing, but its input 'world' is a host's image in General layout and the instance publishes in ShaderReadOnly, so its output cannot stand for it."
            );
        }
    }
    [Fact]
    public void APassOverAnImageInThePublishedLayoutMayStandForIt() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, recorders) = OverScene(gpu: gpu);

        using (runtime) {
            frames.Settle();
            Assert.True(condition: recorders.Of(instance: "main").MayStandIn);
        }
    }
    [Fact]
    public void AnExternalRootShowsItsLatestOutputAndServesItsCaptures() {
        var gpu = new FakePipelineGpu();
        var producers = new Producers(gpu: gpu);
        var recorders = new Recorders();

        producers.Register(registry: recorders.Registry);

        var runtime = Runtime(
            gpu,
            recorders,
            Set(External(name: "world")),
            "world",
            [null!]
        );
        var frames = new Frames(
            footprints: [],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "world", Width: 1.0)],
            runtime: runtime
        );

        using (runtime) {
            var world = producers.Only;

            world.Holding = true;
            Assert.True(condition: frames.Next().IsEmpty);
            Assert.Equal(
                actual: runtime.UnservedCaptureReason,
                expected: "the instance 'world' has produced no output: the fake world has not produced"
            );

            world.Holding = false;

            var shown = frames.Next();

            // The runtime shows the producer's image and holds no acquisition of it past the frame.
            Assert.Equal(
                actual: (shown.ImageViewHandle, (world.Acquired - world.Released)),
                expected: (world.ImageView, 0)
            );
            Assert.Null(@object: runtime.UnservedCaptureReason);

            var request = CaptureRequest();

            runtime.RequestCapture(request: request);
            _ = frames.Next();
            Assert.Equal(
                actual: (request.Completion.IsCompleted, Assert.Single(collection: world.Captured)),
                expected: (true, request.Path)
            );
            Assert.Null(@object: runtime.PendingCapturePath);
        }
    }
    [Fact]
    public void ACaptureTargetReadsTheInstanceItNamesAndARootCaptureWaitsForItsInputs() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, producers) = WorldScene(gpu: gpu);

        using (runtime) {
            var world = producers.Only;
            var main = runtime.Node(instance: runtime.Instances.IndexOf(name: "main"));

            // The view renders over the stand-in while the world holds, so a capture of it waits and names why.
            world.Holding = true;
            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        _ = frames.Next();

                        return main.IsReady;
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: "The view never installed its graph."
            );
            _ = frames.Next();

            var waiting = CaptureRequest();

            runtime.RequestCapture(request: waiting);
            frames.Next(count: 2);
            Assert.False(condition: waiting.Completion.IsCompleted);
            Assert.Equal(
                actual: runtime.UnservedCaptureReason,
                expected: "the instance 'main' has rendered only over a stand-in for 'world', which has produced no output"
            );

            // Once the world produces, the next frame the view renders over it serves the capture.
            world.Holding = false;
            frames.Settle();
            Assert.Null(@object: Outcome(request: waiting).Error);
            Assert.Empty(collection: world.Captured);

            // A capture naming the world reads the producer, not the root; an unknown instance is refused.
            var target = runtime.CaptureTarget(instance: "world");
            var named = CaptureRequest();

            Assert.Same(
                actual: runtime.CaptureTarget(instance: "world"),
                expected: target
            );
            target.RequestCapture(request: named);
            Assert.Equal(
                actual: target.PendingCapturePath,
                expected: named.Path
            );
            Assert.Throws<InvalidOperationException>(testCode: () => runtime.RequestCapture(request: CaptureRequest()));
            _ = frames.Next();
            Assert.Equal(
                actual: (named.Completion.IsCompleted, Assert.Single(collection: world.Captured)),
                expected: (true, named.Path)
            );
            Assert.Null(@object: runtime.UnservedCaptureReasonOf(instance: "world"));
            Assert.Throws<ArgumentException>(testCode: () => runtime.CaptureTarget(instance: "nowhere"));
        }
    }
    [Fact]
    public void AnInputOfAnotherFormatOrALargerBufferIsRefusedByName() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera, Pool);
        var cameras = Set(
            Instance(name: "camera"),
            Instance(
                name: "main",
                reads: new RenderGraphRead(Producer: "camera")
            )
        );
        var floatView = Compile(definition: new RenderGraphDefinition(
            Name: "float-view",
            Outputs: ["image"],
            Passes: [new ShaderPipelinePass(
                EntryPoint: "main",
                Inputs: [new ResourceReference(Name: "screen")],
                Kind: ShaderPipelineDocumentPassKind.Compute,
                Name: "compose",
                Outputs: [new ResourceReference(Name: "image")],
                Source: "compose.hlsl"
            )],
            Resources: [
                Image(name: "screen", external: true) with { Format = "R16G16B16A16Float" },
                Image(name: "image"),
            ],
            Schema: RenderGraphSchemas.Graph
        ));
        var format = Refusal(gpu, recorders, cameras, "main", Graph(pipeline: CameraGraph()), Graph(floatView, ("screen", "camera")));

        Assert.Equal(expected: RenderGraphRuntimeRefusalCode.InputFormat, actual: format.Code);
        Assert.Equal(expected: ["main", "screen", "camera"], actual: format.Names);
        Assert.Equal(
            actual: format.Message,
            expected: "Instance 'main' binds 'screen' as R16G16B16A16Float, but 'camera' publishes R8G8B8A8Unorm."
        );

        var pools = Set(
            Instance(
                name: "pool",
                output: ShaderPipelineResourceKind.Buffer
            ),
            Instance(name: "camera"),
            Instance(
                name: "main",
                reads: [
                    new RenderGraphRead(Producer: "camera"),
                    new RenderGraphRead(
                        Kind: ShaderPipelineResourceKind.Buffer,
                        Producer: "pool"
                    ),
                ]
            )
        );
        var larger = ScreensGraph(true, "screen");
        var doubled = Compile(definition: larger.Plan.Definition with {
            Resources = [.. larger.Plan.Definition.Resources.Select(selector: static resource => ((resource.Name == "pool")
                ? resource with { SizeBytes = (PoolBytes * 2) }
                : resource))],
        });
        var size = Refusal(gpu, recorders, pools, "main", Graph(pipeline: PoolGraph()), Graph(pipeline: CameraGraph()), Graph(doubled, ("screen", "camera"), ("pool", "pool")));

        Assert.Equal(expected: RenderGraphRuntimeRefusalCode.InputFormat, actual: size.Code);
        Assert.Equal(
            actual: size.Message,
            expected: $"Instance 'main' binds 'pool' as a {(PoolBytes * 2)}-byte buffer, but 'pool' publishes a {PoolBytes}-byte buffer."
        );
        Assert.Equal(expected: 0, actual: gpu.CreationCount);
    }
    [Fact]
    public void APackageRecorderIsToldTheLayoutItsImagesAreIn() {
        var gpu = new FakePipelineGpu();
        var recorders = new Recorders(Camera);

        using var runtime = Runtime(
            gpu,
            recorders,
            Set(Instance(name: "camera")),
            "camera",
            Graph(pipeline: CameraGraph())
        );

        new Frames(
            footprints: [],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "camera", Width: 1.0)],
            runtime: runtime
        ).Settle();

        // The camera's package pass writes its image as storage, so the planned barrier leaves it in General.
        Assert.Equal(
            actual: recorders.Of(instance: "camera").OutputLayout,
            expected: GpuImageLayout.General
        );
    }

    private static RenderGraphRuntimeRefusal Refusal(FakePipelineGpu gpu, Recorders recorders, RenderGraphInstanceSet set, string root, params RenderGraphRuntimeGraph[] graphs) {
        Assert.False(condition: RenderGraphRuntime.TryCreate(
            deviceContext: gpu,
            graphs: graphs,
            hostsOnDirectX: false,
            packages: recorders.Registry,
            refusal: out var refusal,
            root: root,
            runtime: out _,
            set: set
        ));

        return refusal;
    }

    /// <summary>The fake external producers a registry creates, one per external instance.</summary>
    private sealed class Producers(FakePipelineGpu gpu) {
        public const string ReleaseEvent = "release world lease";

        public List<FakeProducer> Created { get; } = [];
        public FakeProducer Only => Assert.Single(collection: Created);

        public void Register(RenderGraphPackageRecorders registry) => registry.RegisterProducer(
            factory: context => {
                var producer = new FakeProducer(gpu: gpu);

                Created.Add(item: producer);

                return producer;
            },
            package: World
        );
    }
    /// <summary>A producer standing in for the SDF engine: one image, written in place, left in General, and every
    /// acquisition and release counted.</summary>
    private sealed class FakeProducer : IRenderGraphExternalProducer {
        private readonly CaptureRequestSlot m_capture = new();

        private readonly FakePipelineGpu m_gpu;
        private readonly Action<int> m_release;
        private readonly Action<string> m_write;

        private IGpuImage? m_image;

        public FakeProducer(FakePipelineGpu gpu) {
            m_gpu = gpu;
            m_release = Release;
            m_write = Captured.Add;
        }

        public int Acquired { get; private set; }

        // The paths of the captures served, each by the frame produced after it was forwarded.
        public List<string> Captured { get; } = [];

        public int DeviceLosses { get; private set; }
        public int Disposals { get; private set; }
        public (uint Width, uint Height) Extent { get; private set; }
        public SurfaceFormat Format => SurfaceFormat.R8G8B8A8Unorm;
        public bool Holding { get; set; }
        public nint Image => m_image!.ImageHandle;
        public nint ImageView => m_image!.ImageViewHandle;
        public string? NotReadyReason => ((Produced == 0)
            ? "the fake world has not produced"
            : null);
        public string? PendingCapturePath => m_capture.PendingPath;
        public int Produced { get; private set; }
        public int Released { get; private set; }

        public IGpuWorkSource Work { get; } = new GpuWorkLedger(
            framesInFlight: 3,
            name: "test.world"
        );

        public void Dispose() {
            Disposals++;
            m_image?.Dispose();
            m_image = null;
        }
        public void OnDeviceLost() {
            DeviceLosses++;
            m_image?.Dispose();
            m_image = null;
        }
        public bool Produce(in FrameContext context, uint width, uint height) {
            if (Holding) {
                return false;
            }

            m_image ??= m_gpu.Create(
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: height,
                name: default,
                usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
                width: width
            );
            Extent = (width, height);
            Produced++;
            m_capture.Serve(
                failureLabel: "[test] capture failed",
                writer: m_write
            );

            return true;
        }
        public void RequestCapture(FrameCaptureRequest request) => m_capture.Arm(
            pendingPath: PendingCapturePath,
            request: request
        );
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            if (
                (Produced == 0) ||
                (m_image is not { } image)
            ) {
                output = default;

                return false;
            }

            Acquired++;
            output = new RenderGraphExternalOutput(
                Image: Surface.SameDeviceImage(
                    format: SurfaceFormat.R8G8B8A8Unorm,
                    height: image.Height,
                    imageHandle: image.ImageHandle,
                    imageViewHandle: image.ImageViewHandle,
                    width: image.Width
                ),
                Layout: GpuImageLayout.General,
                Lease: new GpuImageLease(
                    ImageViewHandle: image.ImageViewHandle,
                    Release: m_release,
                    ReleaseToken: Acquired
                )
            );

            return true;
        }

        private void Release(int token) {
            Released++;

            if (m_gpu.Recording) {
                m_gpu.Events.Add(item: Producers.ReleaseEvent);
            }
        }
    }
}

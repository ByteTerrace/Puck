using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// Taint: a camera's image handed out outside a capture frame is tainted, and so is a view whose latest render read it. A
// capture frame renders every tainted instance the capture reads again, whatever its divisor, so the view reads the
// camera's fill before the root composes, and a capture never reads a tainted output.
public sealed partial class RenderGraphRuntimeLawTests {
    private const string Feed = "test.feed";

    // A camera an external producer hands out, a view refreshed every eighth frame showing it, and the root showing the
    // view.
    private static (RenderGraphRuntime Runtime, Frames Frames, FakeCamera Camera) TaintScene(FakePipelineGpu gpu) {
        var camera = new FakeCamera(gpu: gpu);
        var recorders = new Recorders();

        recorders.Registry.RegisterProducer(
            factory: _ => camera,
            package: Feed
        );

        var runtime = Runtime(
            gpu,
            recorders,
            Set(
                new RenderGraphInstance(
                    ExternalPackage: Feed,
                    Name: "camera",
                    Passes: 1,
                    Reads: [],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
                new RenderGraphInstance(
                    Name: "view",
                    Output: ShaderPipelineResourceKind.Image,
                    Passes: 1,
                    Reads: [new RenderGraphRead(Producer: "camera")],
                    Refresh: RenderGraphRefresh.Every(divisor: 8)
                ),
                Instance(
                    name: "main",
                    reads: new RenderGraphRead(Producer: "view")
                )
            ),
            "main",
            null!,
            Graph(ScreensGraph(false, "screen"), ("screen", "camera")),
            Graph(ScreensGraph(false, "screen"), ("screen", "view"))
        );
        var frames = new Frames(
            footprints: [
                new RenderGraphFootprint(Consumer: "main", Height: 1.0, Producer: "view", Width: 1.0),
                new RenderGraphFootprint(Consumer: "view", Height: 1.0, Producer: "camera", Width: 1.0),
            ],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0)],
            runtime: runtime
        );

        return (runtime, frames, camera);
    }
    // Produces until the view renders and its node produces, then one frame more, so the view's latest output read the
    // camera unfilled and it is not due again for seven frames. A scheduled render is not a produced one: the view's node
    // takes its graph's build, which runs on the thread pool, only on a frame that schedules it, and the settling frames
    // may schedule only the camera and the root, so the view is held to a frame whose every scheduled instance produced.
    private static void PastAViewRender(RenderGraphRuntime runtime, Frames frames) {
        var view = runtime.Instances.IndexOf(name: "view");

        frames.Settle();
        Assert.True(
            condition: SpinWait.SpinUntil(
                condition: () => {
                    _ = frames.Next();

                    return (
                        (runtime.Latest!.Instances[view].Status == RenderGraphInstanceStatus.Rendered) &&
                        runtime.IsSettled
                    );
                },
                timeout: TimeSpan.FromSeconds(value: 30)
            ),
            userMessage: "The view's node never produced a scheduled render."
        );
        _ = frames.Next();
        Assert.Equal(
            actual: runtime.Latest!.Instances[view].Status,
            expected: RenderGraphInstanceStatus.Waiting
        );
    }

    [Fact]
    public void AViewAtDivisorEightThatReadACameraRendersAgainInTheCaptureFrameAndReadsTheFill() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, camera) = TaintScene(gpu: gpu);

        using (runtime) {
            var view = runtime.Instances.IndexOf(name: "view");

            camera.Filling = () => (runtime.PendingCapturePath is not null);
            PastAViewRender(
                frames: frames,
                runtime: runtime
            );

            var request = CaptureRequest();

            runtime.RequestCapture(request: request);
            gpu.DescriptorWrites.Clear();
            gpu.Recording = true;
            _ = frames.Next();
            gpu.Recording = false;

            // The capture frame renders the view though it is not due, over the camera's fill, and the root serves the
            // capture from it in the same frame.
            var row = runtime.Latest!.Instances[view];
            var sampled = gpu.DescriptorWrites.Where(predicate: static write => (write.Binding == 1)).Select(selector: static write => write.Handle).ToList();

            Assert.Equal(
                actual: (row.Status, row.LatestFrame),
                expected: (RenderGraphInstanceStatus.Rendered, (frames.Index - 1))
            );
            Assert.Contains(
                collection: sampled,
                expected: camera.FillView
            );
            Assert.DoesNotContain(
                collection: sampled,
                expected: camera.CameraView
            );
            Assert.Null(@object: Outcome(request: request).Error);

            // With the capture served the view keeps its divisor again.
            _ = frames.Next();
            Assert.Equal(
                actual: runtime.Latest!.Instances[view].Status,
                expected: RenderGraphInstanceStatus.Waiting
            );
        }
    }
    [Fact]
    public void ACaptureNeverReadsATaintedOutput() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, camera) = TaintScene(gpu: gpu);

        using (runtime) {
            var view = runtime.Instances.IndexOf(name: "view");

            // A gate that never fills: every image the camera hands out is tainted.
            camera.Filling = static () => false;
            PastAViewRender(
                frames: frames,
                runtime: runtime
            );

            var request = CaptureRequest();
            var rendered = 0;

            runtime.RequestCapture(request: request);

            for (var frame = 0; (frame < 10); frame++) {
                _ = frames.Next();
                rendered += ((runtime.Latest!.Instances[view].Status == RenderGraphInstanceStatus.Rendered) ? 1 : 0);
            }

            // Every capture frame renders the view again, but it only ever reads the camera, so the capture waits and
            // names why.
            Assert.Equal(
                actual: (rendered, request.Completion.IsCompleted),
                expected: (10, false)
            );
            Assert.Equal(
                actual: runtime.UnservedCaptureReason,
                expected: "the instance 'main' has rendered only over external content from 'view' that the capture gate did not fill"
            );

            // The first frame the camera's fill reaches the view serves the capture.
            camera.Filling = static () => true;
            _ = frames.Next();
            Assert.Null(@object: Outcome(request: request).Error);
            Assert.Null(@object: runtime.UnservedCaptureReason);
        }
    }
    // An external producer whose reads are tainted serves no capture, and the runtime names the read that holds it, so a
    // host polling readiness never calls it ready over external content.
    [Fact]
    public void ACaptureOfAnExternalProducerOverATaintedReadWaitsAndNamesTheRead() {
        var gpu = new FakePipelineGpu();
        var camera = new FakeCamera(gpu: gpu);
        var recorders = new Recorders();
        var producers = new Producers(gpu: gpu);

        producers.Register(registry: recorders.Registry);
        recorders.Registry.RegisterProducer(
            factory: _ => camera,
            package: Feed
        );

        var runtime = Runtime(
            gpu,
            recorders,
            Set(
                new RenderGraphInstance(
                    ExternalPackage: Feed,
                    Name: "camera",
                    Passes: 1,
                    Reads: [],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
                External(name: "world") with {
                    Reads = [new RenderGraphRead(Producer: "camera")],
                }
            ),
            "world",
            null!,
            null!
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "world", Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "world", Width: 1.0)],
            runtime: runtime
        );

        using (runtime) {
            var world = producers.Only;

            camera.Filling = static () => false;
            frames.Settle();

            var request = CaptureRequest();

            runtime.RequestCapture(request: request);
            frames.Next(count: 3);
            Assert.Equal(
                actual: (request.Completion.IsCompleted, world.Captured.Count),
                expected: (false, 0)
            );
            Assert.Equal(
                actual: runtime.UnservedCaptureReason,
                expected: "the instance 'world' has rendered only over external content from 'camera' that the capture gate did not fill"
            );

            // The first frame whose read is the camera's fill serves it.
            camera.Filling = static () => true;
            _ = frames.Next();
            Assert.Equal(
                actual: (request.Completion.IsCompleted, Assert.Single(collection: world.Captured)),
                expected: (true, request.Path)
            );
            Assert.Null(@object: runtime.UnservedCaptureReason);
        }
    }
    // A view that reads itself carries its taint from frame to frame through its previous-frame read, so a capture frame
    // binds no tainted previous-frame read: the view renders once without its own image, over the camera's fill, and the
    // capture is served.
    [Fact]
    public void ACaptureFrameBindsNoTaintedPreviousFrameReadSoAViewReadingItselfIsServed() {
        var gpu = new FakePipelineGpu();
        var camera = new FakeCamera(gpu: gpu);
        var recorders = new Recorders();
        var producers = new Producers(gpu: gpu);

        producers.Register(registry: recorders.Registry);
        recorders.Registry.RegisterProducer(
            factory: _ => camera,
            package: Feed
        );

        var runtime = Runtime(
            gpu,
            recorders,
            Set(
                new RenderGraphInstance(
                    ExternalPackage: Feed,
                    Name: "camera",
                    Passes: 1,
                    Reads: [],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
                External(name: "world") with {
                    Reads = [
                        new RenderGraphRead(Producer: "camera"),
                        new RenderGraphRead(
                            PreviousFrame: true,
                            Producer: "world"
                        ),
                    ],
                }
            ),
            "world",
            null!,
            null!
        );
        var frames = new Frames(
            footprints: [new RenderGraphFootprint(Consumer: "world", Height: 1.0, Producer: "camera", Width: 1.0)],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "world", Width: 1.0)],
            runtime: runtime
        );

        using (runtime) {
            var world = producers.Only;

            world.CarriesTaint = true;
            camera.Filling = static () => false;
            frames.Settle();
            frames.Next(count: 2);

            // Outside a capture the view shows its own tainted image.
            Assert.True(condition: world.WorldReadBound[^1]);

            var request = CaptureRequest();

            runtime.RequestCapture(request: request);
            camera.Filling = () => (runtime.PendingCapturePath is not null);
            _ = frames.Next();

            Assert.Equal(
                actual: (request.Completion.IsCompleted, Assert.Single(collection: world.Captured), world.WorldReadBound[^1]),
                expected: (true, request.Path, false)
            );
            Assert.Null(@object: runtime.UnservedCaptureReason);
        }
    }
    // Two views.graphs panes reading each other's previous frame carry a taint around the loop: the capture frame renders
    // both again, but the first binds the second's output from before, so it stays tainted and the second reads it again.
    // A capture frame binds a stand-in for a tainted previous-frame read, so the pane renders over the camera's fill alone
    // and the capture is served.
    [Fact]
    public void ACaptureFrameBindsAStandInForAGraphInstancesTaintedPreviousFrameRead() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var camera = new FakeCamera(gpu: gpu);
        var recorders = new Recorders();

        recorders.Registry.RegisterProducer(
            factory: _ => camera,
            package: Feed
        );

        var runtime = Runtime(
            gpu,
            recorders,
            Set(
                new RenderGraphInstance(
                    ExternalPackage: Feed,
                    Name: "camera",
                    Passes: 1,
                    Reads: [],
                    Refresh: RenderGraphRefresh.EveryFrame
                ),
                Instance(
                    name: "front",
                    reads: [
                        new RenderGraphRead(Producer: "camera"),
                        new RenderGraphRead(
                            PreviousFrame: true,
                            Producer: "back"
                        ),
                    ]
                ),
                Instance(
                    name: "back",
                    reads: new RenderGraphRead(
                        PreviousFrame: true,
                        Producer: "front"
                    )
                )
            ),
            "front",
            null!,
            Graph(ScreensGraph(false, "screen", "other"), ("screen", "camera"), ("other", "back")),
            Graph(ScreensGraph(false, "screen"), ("screen", "front"))
        );
        var frames = new Frames(
            footprints: [
                new RenderGraphFootprint(Consumer: "front", Height: 1.0, Producer: "camera", Width: 1.0),
                new RenderGraphFootprint(Consumer: "front", Height: 1.0, Producer: "back", Width: 1.0),
                new RenderGraphFootprint(Consumer: "back", Height: 1.0, Producer: "front", Width: 1.0),
            ],
            roots: [new RenderGraphRoot(Height: 1.0, Instance: "front", Width: 1.0)],
            runtime: runtime
        );

        using (runtime) {
            camera.Filling = static () => false;
            frames.Settle();
            frames.Next(count: 3);

            var request = CaptureRequest();

            runtime.RequestCapture(request: request);
            camera.Filling = () => (runtime.PendingCapturePath is not null);
            frames.Next(count: 2);

            Assert.Null(@object: Outcome(request: request).Error);
            Assert.Null(@object: runtime.UnservedCaptureReason);
        }
    }

    /// <summary>A camera handed out through a capture gate: its own image, tainted, or while the gate fills its fill,
    /// untainted. Both are same-device images, so a graph instance binds them.</summary>
    private sealed class FakeCamera(FakePipelineGpu gpu) : IRenderGraphExternalProducer {
        private IGpuImage? m_camera;
        private IGpuImage? m_fill;

        public nint CameraView => m_camera!.ImageViewHandle;
        public nint FillView => m_fill!.ImageViewHandle;

        public Func<bool> Filling { get; set; } = static () => false;

        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => ((m_camera is null)
            ? "the fake camera has not produced"
            : null);
        public string? PendingCapturePath => null;

        public IGpuWorkSource Work { get; } = new GpuWorkLedger(
            framesInFlight: 3,
            name: Feed
        );

        private IGpuImage Create(uint width, uint height) => gpu.Create(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: height,
            name: default,
            usage: GpuImageUsage.Sampled | GpuImageUsage.Storage,
            width: width
        );

        public void Dispose() {
            m_camera?.Dispose();
            m_fill?.Dispose();
            m_camera = null;
            m_fill = null;
        }
        public void OnDeviceLost() => Dispose();
        public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            m_camera ??= Create(
                height: height,
                width: width
            );
            m_fill ??= Create(
                height: 1U,
                width: 1U
            );

            return true;
        }
        public void RequestCapture(FrameCaptureRequest request) => _ = request.TryFail(error: new NotSupportedException(message: "The fake camera is captured through the instance that shows it."));
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            if (
                (m_camera is not { } camera) ||
                (m_fill is not { } fill)
            ) {
                output = default;

                return false;
            }

            var filling = Filling();
            var image = (filling ? fill : camera);

            output = new RenderGraphExternalOutput(
                Image: Surface.SameDeviceImage(
                    format: GpuPixelFormat.R8G8B8A8Unorm,
                    height: image.Height,
                    imageHandle: image.ImageHandle,
                    imageViewHandle: image.ImageViewHandle,
                    width: image.Width
                ),
                Layout: GpuImageLayout.ShaderReadOnly,
                Lease: image.ImageViewHandle,
                Tainted: !filling
            );

            return true;
        }
    }
}

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
    // Produces until the view renders, then one frame more, so the view's latest output read the camera unfilled and it
    // is not due again for seven frames.
    private static void PastAViewRender(RenderGraphRuntime runtime, Frames frames) {
        var view = runtime.Instances.IndexOf(name: "view");

        frames.Settle();

        while (runtime.Latest!.Instances[view].Status != RenderGraphInstanceStatus.Rendered) {
            _ = frames.Next();
        }

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

using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

// Captures, steady-state allocation, device loss and disposal.
public sealed partial class RenderGraphRuntimeLawTests {
    // The two-screen scene with an off-view camera and a buffer edge: every kind of edge and a stand-in in one frame.
    private static (RenderGraphRuntime Runtime, Frames Frames, Recorders Recorders) Scene(FakePipelineGpu gpu) {
        var recorders = new Recorders(Camera, Pool);
        var set = Set(
            Instance(
                name: "pool",
                output: ShaderPipelineResourceKind.Buffer
            ),
            Instance(name: "camera"),
            Instance(name: "hidden"),
            Instance(
                name: "main",
                reads: [
                    new RenderGraphRead(Producer: "camera"),
                    new RenderGraphRead(Producer: "hidden"),
                    new RenderGraphRead(
                        Kind: ShaderPipelineResourceKind.Buffer,
                        Producer: "pool"
                    ),
                ]
            ),
            Instance(
                name: "pane",
                reads: new RenderGraphRead(Producer: "camera")
            )
        );
        var runtime = Runtime(
            gpu,
            recorders,
            set,
            "main",
            Graph(pipeline: PoolGraph()),
            Graph(pipeline: CameraGraph()),
            Graph(pipeline: CameraGraph()),
            Graph(ScreensGraph(true, "left", "right", "far"), ("left", "camera"), ("right", "camera"), ("far", "hidden"), ("pool", "pool")),
            Graph(ScreensGraph(false, "screen"), ("screen", "camera"))
        );
        var frames = new Frames(
            footprints: [
                new RenderGraphFootprint(Consumer: "main", Height: 0.25, Producer: "camera", Width: 0.5),
                new RenderGraphFootprint(Consumer: "pane", Height: 1.0, Producer: "camera", Width: 1.0),
            ],
            roots: [
                new RenderGraphRoot(Height: 1.0, Instance: "main", Width: 1.0),
                new RenderGraphRoot(Height: 0.25, Instance: "pane", Width: 0.25),
            ],
            runtime: runtime
        );

        return (runtime, frames, recorders);
    }
    private static FrameCaptureRequest CaptureRequest() => new(path: Path.Combine(
        path1: Path.GetTempPath(),
        path2: $"{Guid.NewGuid():N}.png"
    ));
    // The outcome of a request some frame or refusal has already completed.
    private static FrameCaptureResult Outcome(FrameCaptureRequest request) {
        Assert.True(condition: request.Completion.IsCompleted);

        return request.Completion.Result;
    }

    [Fact]
    public void ASteadyFrameAllocatesNothing() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, _) = Scene(gpu: gpu);

        using (runtime) {
            frames.Settle();
            frames.Next(count: 8);

            Assert.Equal(
                actual: AllocationWindow.Least(window: () => frames.Next(count: 64)),
                expected: 0L
            );
            Assert.True(condition: runtime.IsSettled);
        }
    }
    [Fact]
    public void ACaptureIsServedFromTheRootOutput() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, _) = Scene(gpu: gpu);

        using (runtime) {
            frames.Settle();

            var request = CaptureRequest();

            runtime.RequestCapture(request: request);
            Assert.Equal(
                actual: runtime.PendingCapturePath,
                expected: request.Path
            );
            Assert.Throws<InvalidOperationException>(testCode: () => runtime.RequestCapture(request: CaptureRequest()));

            var shown = frames.Next();

            // The frame served it from the root's node, which read back the image the runtime returned.
            Assert.True(condition: request.Completion.IsCompleted);
            Assert.Null(@object: Outcome(request: request).Error);
            Assert.Null(@object: runtime.PendingCapturePath);
            Assert.Null(@object: runtime.UnservedCaptureReason);
            Assert.Equal(
                actual: gpu.CreatedObjects.Single(predicate: static created => (created.Kind == "readback staging")).Bytes,
                expected: ((((ulong)shown.Width) * shown.Height) * 4UL)
            );
        }
    }
    [Fact]
    public void ACaptureNoRootOutputServesIsRefusedByNameAndThenDropped() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, _) = Scene(gpu: gpu);

        using (runtime) {
            var held = CaptureRequest();

            // Nothing has rendered: the capture stays armed on the runtime, which names why no frame serves it.
            runtime.RequestCapture(request: held);
            Assert.Equal(
                actual: runtime.UnservedCaptureReason,
                expected: "the root instance 'main' has produced no output"
            );

            // The requester's hold runs out, and it refuses the capture naming that reason; the runtime drops the
            // withdrawn request instead of serving it once the root renders, and a new capture can be armed.
            Assert.True(condition: held.TryFail(error: new TimeoutException(message: $"unserved: {runtime.UnservedCaptureReason}")));
            Assert.Null(@object: runtime.PendingCapturePath);
            frames.Settle();
            Assert.Equal(
                actual: Outcome(request: held).Error?.Message,
                expected: "unserved: the root instance 'main' has produced no output"
            );
            Assert.DoesNotContain(
                collection: gpu.CreatedObjects,
                filter: static created => (created.Kind == "readback staging")
            );

            var disposed = CaptureRequest();

            runtime.RequestCapture(request: disposed);
            runtime.Dispose();
            Assert.IsType<ObjectDisposedException>(@object: Outcome(request: disposed).Error);
        }
    }
    [Fact]
    public void ADeviceLossReleasesEverythingAndTheNextFramesRebuild() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, recorders) = Scene(gpu: gpu);

        using (runtime) {
            frames.Settle();

            // A capture armed on the runtime, which no frame has forwarded yet, is refused by the loss.
            var armed = CaptureRequest();

            runtime.RequestCapture(request: armed);
            runtime.OnDeviceLost();
            Assert.Equal(
                actual: Assert.IsType<DeviceLostException>(@object: Outcome(request: armed).Error).Message,
                expected: CaptureRequestSlot.DeviceLostReason
            );
            Assert.Null(@object: runtime.PendingCapturePath);

            // Nothing created on the lost device survives: every node's graph and package recorders, and the stand-in,
            // are released, and no instance has a completed output to show or capture.
            Assert.Equal(
                actual: (Live: gpu.LiveBytes, Undisposed: gpu.CreatedObjects.Count(predicate: static created => (created.DisposeCount == 0)), Recorders: recorders.ByInstance.Values.Sum(selector: static counter => (counter.Created - counter.Disposed))),
                expected: (Live: 0UL, Undisposed: 0, Recorders: 0)
            );
            Assert.NotNull(@object: runtime.UnservedCaptureReason);

            frames.Settle();
            Assert.Null(@object: runtime.UnservedCaptureReason);
            Assert.All(
                collection: recorders.ByInstance.Where(predicate: static pair => (pair.Key != "hidden")),
                action: static pair => Assert.Equal(
                    actual: (pair.Value.Created - pair.Value.Disposed),
                    expected: 1
                )
            );
        }
    }
    [Fact]
    public void DisposalReleasesEveryInstanceAndItsRecorders() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, recorders) = Scene(gpu: gpu);

        frames.Settle();
        runtime.Dispose();

        Assert.Equal(
            actual: (Live: gpu.LiveBytes, Undisposed: gpu.CreatedObjects.Count(predicate: static created => (created.DisposeCount == 0)), Recorders: recorders.ByInstance.Values.Sum(selector: static counter => (counter.Created - counter.Disposed))),
            expected: (Live: 0UL, Undisposed: 0, Recorders: 0)
        );
        Assert.Throws<ObjectDisposedException>(testCode: () => frames.Next());
    }
}

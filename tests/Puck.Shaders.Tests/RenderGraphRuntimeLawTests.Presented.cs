using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

// A presented capture reads the image each frame hands the display, holds nothing, and is refused by name wherever a
// settled capture would be.
public sealed partial class RenderGraphRuntimeLawTests {
    private FrameCaptureRequest PresentedRequest(int frames = 1) => FrameCaptureRequest.Presented(
        frames: frames,
        path: CaptureRequest().Path
    );
    private static FrameCaptureResult Landed(FrameCaptureRequest request) {
        Assert.True(condition: request.Completion.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken));

        return request.Completion.Result;
    }

    [Fact]
    public void APresentedBurstReadsTheImageEachFrameShowsAndHoldsNothing() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        using var runtime = TwoInstances(gpu: gpu, recorders: new Recorders(Camera));
        var frames = new Frames(footprints: [], roots: CameraAndMain, runtime: runtime);

        frames.Settle();

        var settled = CaptureRequest();

        runtime.RequestCapture(request: settled);
        Assert.True(condition: runtime.HoldsPresentation);
        _ = frames.Next();
        Assert.Null(@object: Outcome(request: settled).Error);
        gpu.Readbacks.Clear();

        var request = PresentedRequest(frames: 3);
        var first = frames.Index;

        runtime.RequestCapture(request: request);
        Assert.Equal(expected: request.Path, actual: runtime.PendingCapturePath);
        Assert.False(condition: runtime.HoldsPresentation);
        Assert.Throws<InvalidOperationException>(testCode: () => runtime.RequestCapture(request: CaptureRequest()));
        Assert.Throws<InvalidOperationException>(testCode: () => runtime.RequestCapture(request: PresentedRequest()));

        var shown = new List<nint>();

        for (var frame = 0; (frame < 3); frame++) {
            shown.Add(item: frames.Next().ImageHandle);
        }

        var result = Landed(request: request);

        Assert.Null(@object: result.Error);
        Assert.Null(@object: runtime.PendingCapturePath);
        Assert.Equal(expected: shown, actual: gpu.Readbacks.Select(selector: static read => read.Image).ToList());
        Assert.Equal(expected: new[] { first, (first + 1), (first + 2) }, actual: result.Presented.Select(selector: static frame => frame.Presentation).ToArray());
        Assert.All(collection: result.Presented, action: frame => Assert.Equal(expected: frame.Presentation, actual: frame.Rendered));
        Assert.All(collection: result.Presented, action: frame => Assert.True(condition: File.Exists(path: frame.Path)));
        Assert.Null(@object: result.Presented[0].Delta);
        Assert.Equal(expected: 0L, actual: result.Presented[2].Delta!.Changed);
    }
    [Fact]
    public void APresentedCaptureReadsAWorkingImageThroughTheDisplayEncodeOnceItBuilds() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var hdr = Compile(definition: CameraGraph().Plan.Definition with {
            Resources = [Image(name: "color") with { Format = "R16G16B16A16Float" }],
        });

        using var runtime = Runtime(gpu, new Recorders(Camera), Set(Instance(name: "main"), Instance(name: "camera")), "main",
            Graph(pipeline: hdr), Graph(pipeline: CameraGraph()));
        var frames = new Frames(footprints: [], roots: CameraAndMain, runtime: runtime);

        frames.Settle();

        var request = PresentedRequest();
        using var gate = new ManualResetEventSlim(initialState: false);

        gpu.PipelineGate = gate;
        try {
            runtime.RequestCapture(request: request);
            frames.Next(count: 2);
            Assert.True(condition: gpu.PipelineGateEntered.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken));
            Assert.False(condition: request.Completion.IsCompleted);
            Assert.Empty(collection: gpu.Readbacks);
        } finally {
            gate.Set();
            gpu.PipelineGate = null;
        }

        TestLiveness.Until(step: () => {
            _ = frames.Next();

            return (runtime.PendingCapturePath is null);
        });
        Assert.Null(@object: Landed(request: request).Error);
    }
    [Fact]
    public void APresentedCaptureSkipsFramesItsInstanceCannotServeAndRefusesAPermanentFailure() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, producers) = WorldScene(gpu: gpu);

        using (runtime) {
            frames.Settle();
            Assert.Throws<InvalidOperationException>(testCode: () => runtime.CaptureTarget(instance: "world").RequestCapture(request: PresentedRequest()));

            var request = PresentedRequest(frames: 2);
            var world = producers.Only;

            world.Holding = true;
            runtime.RequestCapture(request: request);
            frames.Next(count: 3);
            Assert.False(condition: request.Completion.IsCompleted);
            Assert.Empty(collection: gpu.Readbacks);

            world.Holding = false;
            world.Ended = "the destination feed ended";
            _ = frames.Next();
            Assert.Equal(expected: runtime.Render.Reason, actual: Landed(request: request).Error!.Message);
            Assert.Null(@object: runtime.PendingCapturePath);
        }
    }
    [Fact]
    public void APresentedCaptureIsRefusedByNameWhenItsInstanceGoesOrStopsRendering() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        using var runtime = TwoInstances(gpu: gpu, recorders: new Recorders(Camera));
        var frames = new Frames(footprints: [], roots: CameraAndMain, runtime: runtime);

        frames.Settle();

        var named = PresentedRequest(frames: 2);

        runtime.CaptureTarget(instance: "main").RequestCapture(request: named);
        Assert.True(condition: runtime.TryReconfigure(graphs: [null], refusal: out var refusal, root: "camera", set: Set(Instance(name: "camera"))),
            userMessage: refusal?.Message);
        Assert.Equal(expected: "The render graph no longer has an instance 'main'.", actual: Landed(request: named).Error!.Message);

        var withdrawn = PresentedRequest(frames: 2);

        runtime.RequestCapture(request: withdrawn);
        Assert.True(condition: withdrawn.TryFail(error: new OperationCanceledException(message: "stopped waiting")));
        _ = new Frames(footprints: [], roots: [new RenderGraphRoot(Height: 1.0, Instance: "camera", Width: 1.0)], runtime: runtime).Next();
        Assert.Null(@object: runtime.PendingCapturePath);

        var lost = PresentedRequest();

        runtime.RequestCapture(request: lost);
        runtime.OnDeviceLost();
        Assert.IsType<DeviceLostException>(@object: Landed(request: lost).Error);

        var disposed = PresentedRequest();

        runtime.RequestCapture(request: disposed);
        runtime.Dispose();
        Assert.IsType<ObjectDisposedException>(@object: Landed(request: disposed).Error);
    }
    [Fact]
    public void APresentedCaptureOfAnExternalRootOrAFailedReadIsRefused() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, _) = WorldScene(gpu: gpu);

        using (runtime) {
            frames.Settle();

            var followed = PresentedRequest();

            runtime.RequestCapture(request: followed);
            Assert.True(condition: runtime.TryReconfigure(set: runtime.Instances, graphs: [null, null], root: "world", refusal: out var refusal),
                userMessage: refusal?.Message);
            _ = frames.Next();
            Assert.Equal(expected: "A presented capture reads a rendered graph instance; 'world' is an external producer.", actual: Landed(request: followed).Error!.Message);
        }

        var unread = new FakePipelineGpu();

        using var unsupported = TwoInstances(gpu: unread, recorders: new Recorders(Camera));
        var unsupportedFrames = new Frames(footprints: [], roots: CameraAndMain, runtime: unsupported);

        unsupportedFrames.Settle();

        var failed = PresentedRequest();

        unsupported.RequestCapture(request: failed);
        _ = unsupportedFrames.Next();
        Assert.IsType<NotSupportedException>(@object: Landed(request: failed).Error);

        var losing = new FakePipelineGpu { ReadbackSupported = true };

        using var lost = TwoInstances(gpu: losing, recorders: new Recorders(Camera));
        var lostFrames = new Frames(footprints: [], roots: CameraAndMain, runtime: lost);

        lostFrames.Settle();

        var loss = PresentedRequest();

        lost.RequestCapture(request: loss);
        losing.LoseNextReadback = true;
        Assert.Throws<DeviceLostException>(testCode: () => lostFrames.Next());
        Assert.IsType<DeviceLostException>(@object: Landed(request: loss).Error);
    }
}

using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Testing;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [Theory]
    public void AnArmedRootCaptureFollowsTheNextComposedRoot(bool keepOldRoot, int converge) {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders(Camera);

        using var runtime = TwoInstances(gpu: gpu, recorders: recorders);

        new Frames(footprints: [], roots: CameraAndMain, runtime: runtime).Settle();
        var request = new FrameCaptureRequest(path: CaptureRequest().Path, converge: converge);

        runtime.RequestCapture(request: request);
        var set = (keepOldRoot
            ? Set(Instance(name: "camera"), Instance(name: "main", reads: new RenderGraphRead(Producer: "camera")))
            : Set(Instance(name: "camera")));

        Assert.True(condition: runtime.TryReconfigure(graphs: (keepOldRoot ? [null, null] : [null]),
            refusal: out var refusal, root: "camera", set: set), userMessage: refusal?.Message);
        var frames = new Frames(footprints: [], roots: (keepOldRoot ? CameraAndMain
            : [new RenderGraphRoot(Height: 1.0, Instance: "camera", Width: 1.0)]), runtime: runtime);
        Surface shown = default;

        for (var frame = 0; (frame < Math.Max(val1: 1, val2: converge)); frame++) { shown = frames.Next(); }
        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(expected: shown.ImageHandle, actual: Assert.Single(collection: gpu.Readbacks).Image);
    }
    [Fact]
    public void AnArmedNamedCaptureKeepsItsInstanceWhenTheRootChanges() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders(Camera);

        using var runtime = TwoInstances(gpu: gpu, recorders: recorders);
        var frames = new Frames(footprints: [], roots: CameraAndMain, runtime: runtime);

        frames.Settle();
        var request = CaptureRequest();

        runtime.CaptureTarget(instance: "main").RequestCapture(request: request);
        Assert.True(condition: runtime.TryReconfigure(
            set: Set(Instance(name: "camera"), Instance(name: "main", reads: new RenderGraphRead(Producer: "camera"))),
            graphs: [null, null], root: "camera", refusal: out var refusal), userMessage: refusal?.Message);
        var shown = frames.Next();

        Assert.True(condition: request.Completion.IsCompleted);
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.NotEqual(expected: shown.ImageHandle, actual: Assert.Single(collection: gpu.Readbacks).Image);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AForwardedConvergingCaptureRetainsItsNthImageWhenTheRootChanges(bool reorder) {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var recorders = new Recorders(Camera);
        var hdr = Compile(definition: CameraGraph().Plan.Definition with {
            Resources = [Image(name: "color") with { Format = "R16G16B16A16Float" }],
        });
        var set = Set(Instance(name: "main"), Instance(name: "camera"));

        using var runtime = Runtime(gpu, recorders, set, "main", Graph(pipeline: hdr), Graph(pipeline: CameraGraph()));
        var frames = new Frames(footprints: [], roots: CameraAndMain, runtime: runtime);

        frames.Settle();
        var captured = runtime.NodeOf(instance: "main")!;
        var start = captured.FrameCounter;
        var request = new FrameCaptureRequest(path: CaptureRequest().Path, converge: 2);
        using var gate = new ManualResetEventSlim(initialState: false);

        gpu.PipelineGate = gate;
        try {
            runtime.RequestCapture(request: request);
            frames.Next(count: 2);
            Assert.True(condition: gpu.PipelineGateEntered.Wait(timeout: TestLiveness.Bound, cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(expected: (start + 2), actual: captured.FrameCounter);
            Assert.False(condition: request.Completion.IsCompleted);
            Assert.Equal(expected: request.Path, actual: captured.PendingCapturePath);
            Assert.True(condition: runtime.TryReconfigure(set: (reorder ? Set(Instance(name: "camera"), Instance(name: "main")) : set),
                graphs: [null, null], root: "camera", refusal: out var refusal),
                userMessage: refusal?.Message);
            frames.Next(count: 3);
            Assert.False(condition: request.Completion.IsCompleted);
            Assert.Equal(expected: (start + 2), actual: captured.FrameCounter);
        } finally {
            gate.Set();
            gpu.PipelineGate = null;
        }
        TestLiveness.Until(
            step: () => {
                _ = frames.Next();
                return request.Completion.IsCompleted;
            }
        );
        Assert.Null(@object: Outcome(request: request).Error);
        Assert.Equal(expected: (start + 2), actual: captured.FrameCounter);
    }
    [Fact]
    public void AConvergingCaptureRefusesAnExternalReplacementRoot() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };

        var (runtime, frames, _) = WorldScene(gpu: gpu);

        using (runtime) {
            frames.Settle();
            var request = new FrameCaptureRequest(path: CaptureRequest().Path, converge: 2);

            runtime.RequestCapture(request: request);
            Assert.True(condition: runtime.TryReconfigure(set: runtime.Instances, graphs: [null, null], root: "world", refusal: out var refusal),
                userMessage: refusal?.Message);
            Assert.True(condition: request.Completion.IsCompleted);
            var error = Assert.IsType<InvalidOperationException>(@object: Outcome(request: request).Error);

            Assert.Equal(expected: "A convergence capture requires a rendered graph instance.", actual: error.Message);
            Assert.Null(@object: runtime.PendingCapturePath);
        }
    }
}

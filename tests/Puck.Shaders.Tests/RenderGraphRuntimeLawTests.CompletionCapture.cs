using Puck.Hosting;

namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ACaptureWaitsForItsCurrentInputAndRefusesAPermanentFailure(bool refuses) {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var (runtime, frames, producers) = WorldScene(gpu: gpu);

        using (runtime) {
            frames.Settle();
            var request = CaptureRequest();
            var world = producers.Only;

            world.Holding = true;
            runtime.RequestCapture(request: request);
            for (var attempt = 0; (attempt < 3); attempt++) {
                // The root still has a real image to show, but its input has not produced the destination frame.
                Assert.False(condition: frames.Next().IsEmpty);
                Assert.Equal(expected: FrameCompletion.NotYetRenderable, actual: runtime.Render.Completion);
                Assert.False(condition: request.Completion.IsCompleted);
                Assert.Empty(collection: gpu.Readbacks);
                Assert.Equal(expected: request.Path, actual: runtime.PendingCapturePath);
                Assert.Equal(expected: runtime.Render.Reason, actual: runtime.UnservedCaptureReasonOf(instance: "main"));
            }

            world.Holding = false;
            world.Ended = (refuses ? "the destination feed ended" : null);
            var completed = frames.Next();

            Assert.True(condition: request.Completion.IsCompleted);
            Assert.Null(@object: runtime.PendingCapturePath);
            if (refuses) {
                Assert.Equal(expected: FrameCompletion.Refused, actual: runtime.Render.Completion);
                Assert.Equal(expected: runtime.Render.Reason, actual: Outcome(request: request).Error!.Message);
                Assert.Empty(collection: gpu.Readbacks);
            } else {
                Assert.Equal(expected: FrameCompletion.Rendered, actual: runtime.Render.Completion);
                Assert.Null(@object: Outcome(request: request).Error);
                Assert.Equal(expected: completed.ImageHandle, actual: Assert.Single(collection: gpu.Readbacks).Image);
            }
        }

        AssertStandingCaptureCompletes();
    }
    // The completion guard must distinguish a held rebuild from an image deliberately standing for this frame.
    private void AssertStandingCaptureCompletes() {
        var gpu = new FakePipelineGpu { ReadbackSupported = true };
        var (runtime, frames) = CompletionScene(gpu: gpu);

        using (runtime) {
            frames.Settle();
            var camera = runtime.Node(instance: 0);
            var rendered = camera.FrameCounter;
            var request = CaptureRequest();

            camera.Paused = true;
            runtime.RequestCapture(request: request);
            var completed = frames.Next();

            Assert.Equal(expected: rendered, actual: camera.FrameCounter);
            Assert.Equal(expected: FrameCompletion.Rendered, actual: runtime.Render.Completion);
            Assert.True(condition: request.Completion.IsCompleted);
            Assert.Null(@object: Outcome(request: request).Error);
            Assert.Equal(expected: completed.ImageHandle, actual: Assert.Single(collection: gpu.Readbacks).Image);
        }
    }
}

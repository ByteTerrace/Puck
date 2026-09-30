namespace Puck.Shaders.Tests;

public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void AnInstanceWhoseFirstFrameIsInFlightHoldsTheFirstFramesUntilTheGpuCompletesIt() {
        var gpu = new FakePipelineGpu { QueueHeld = true };

        var (runtime, frames, _) = Scene(gpu: gpu);

        using (runtime) {
            Assert.True(condition: runtime.FirstFramesCompleted);
            Assert.Null(@object: runtime.InFlightReason);

            // The root renders over completed outputs as far as the frame thread can tell, while the queue has finished
            // none of the submissions behind them.
            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        _ = frames.Next();

                        return (runtime.UnservedCaptureReason is null);
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: runtime.UnservedCaptureReason
            );
            Assert.False(condition: runtime.FirstFramesCompleted);
            Assert.EndsWith(
                actualString: runtime.InFlightReason,
                expectedEndString: "has no GPU-completed frame: its first submission is still in flight"
            );

            // The queue finishes: every submitted instance now has a GPU-completed frame, read with no further frame.
            gpu.QueueHeld = false;
            Assert.True(condition: runtime.FirstFramesCompleted);
            Assert.Null(@object: runtime.InFlightReason);

            // A device loss releases what the nodes submitted, and the rebuilt instances' first frames hold it again.
            runtime.OnDeviceLost();
            Assert.True(condition: runtime.FirstFramesCompleted);
            gpu.QueueHeld = true;
            _ = frames.Next();
            Assert.True(
                condition: SpinWait.SpinUntil(
                    condition: () => {
                        _ = frames.Next();

                        return (runtime.UnservedCaptureReason is null);
                    },
                    timeout: TimeSpan.FromSeconds(value: 30)
                ),
                userMessage: runtime.UnservedCaptureReason
            );
            Assert.False(condition: runtime.FirstFramesCompleted);
            gpu.QueueHeld = false;
            Assert.True(condition: runtime.FirstFramesCompleted);
        }
    }
}

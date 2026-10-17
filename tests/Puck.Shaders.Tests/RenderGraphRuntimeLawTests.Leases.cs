using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of the lease list a package recording holds leases in (<see cref="RenderGraphPackageRecording.Leases"/>): a
/// lease held by a frame retires only once that frame slot's submission has completed: when the instance waits the slot's
/// fence again, or at the start of a frame that finds the fence signaled; every held lease retires
/// at disposal.
/// </summary>
public sealed partial class RenderGraphRuntimeLawTests {
    [Fact]
    public void ALeaseARecordingHoldsRetiresAfterItsFrameSlotsFence() {
        var gpu = new FakePipelineGpu();

        var (runtime, frames, recorders) = OverScene(gpu: gpu);
        var retired = 0;

        using (runtime) {
            frames.Settle();
            // A held queue finishes a submission only when a fence wait completes it, so a lease can retire no sooner.
            gpu.QueueHeld = true;

            var over = recorders.Of(instance: "main");

            over.Lease = new GpuImageLease(
                ImageViewHandle: 0x9001,
                Release: _ => retired++
            );
            _ = frames.Next();
            over.Lease = default;

            // The frame slots after the holding frame's record without waiting its fence; the frame that reuses its slot
            // waits the fence and retires the lease.
            _ = frames.Next();
            _ = frames.Next();
            Assert.Equal(
                actual: retired,
                expected: 0
            );
            _ = frames.Next();
            Assert.Equal(
                actual: retired,
                expected: 1
            );

            over.Lease = new GpuImageLease(
                ImageViewHandle: 0x9002,
                Release: _ => retired++
            );
            _ = frames.Next();
            over.Lease = default;
        }

        Assert.Equal(
            actual: retired,
            expected: 2
        );
    }
}

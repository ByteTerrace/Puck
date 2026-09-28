using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of the lease list a package recording holds leases in (<see cref="RenderGraphPackageRecording.Leases"/>): a
/// lease held by a frame retires only once the instance waits that frame slot's fence again, and every held lease retires
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

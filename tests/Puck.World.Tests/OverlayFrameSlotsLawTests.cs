using Puck.Hosting;
using Puck.Overlays;

using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for the fixed frame-slot table's host-owned lease lifecycle and visible capacity refusal.</summary>
public sealed class OverlayFrameSlotsLawTests {
    [Fact]
    public void ADistinctNinthSourceSetsTheCapacitySignalWithoutAcquiringIt() {
        var events = new List<string>();
        var sources = new RecordingFrameSources(events: events);
        var slots = new OverlayFrameSlots(sources: sources);

        for (var key = 0; (key < OverlayFrameSlots.SlotCount); key++) {
            Assert.Equal(
                expected: key,
                actual: slots.Bind(key: key)
            );
        }

        Assert.Equal(
            expected: -1,
            actual: slots.Bind(key: OverlayFrameSlots.SlotCount)
        );
        Assert.True(condition: slots.CapacityExceeded);
        Assert.Equal(
            expected: OverlayFrameSlots.SlotCount,
            actual: sources.AcquisitionCount
        );

        slots.BeginFrame();

        Assert.False(condition: slots.CapacityExceeded);
        slots.RetireAll();
    }
    [Fact]
    public void AuthoringFrameSourceCapacityMatchesTheRuntimeSlotTable() => Assert.Equal(
        actual: OverlayFrameSlots.SlotCount,
        expected: WorldHudCapacity.MaxFrameSources
    );
    [Fact]
    public void RetiringAllReleasesEveryBoundAndHeldLeaseAtOnce() {
        var events = new List<string>();
        var slots = new OverlayFrameSlots(sources: new RecordingFrameSources(events: events));

        Assert.Equal(
            expected: 0,
            actual: slots.Bind(key: 21)
        );
        slots.BeginFrame();
        Assert.Equal(
            expected: 0,
            actual: slots.Bind(key: 22)
        );

        slots.RetireAll();

        Assert.Equal(
            actual: events,
            expected: ["release:21", "release:22"]
        );
        Assert.Equal(
            expected: 0,
            actual: slots.BoundCount
        );
    }

    private sealed class RecordingFrameSources(List<string> events) : IOverlayFrameSources {
        public int AcquisitionCount { get; private set; }

        public bool TryAcquire(int key, out GpuImageLease lease) {
            AcquisitionCount++;
            lease = new GpuImageLease(
                ImageViewHandle: (key + 1),
                Release: token => events.Add(item: $"release:{token}"),
                ReleaseToken: key
            );

            return true;
        }
    }
}

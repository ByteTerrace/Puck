using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class LatestSlotPublicationTests {
    [Fact]
    public void Cycles_slots_and_publishes_complete_state() {
        var publication = new LatestSlotPublication();

        Assert.Equal(
            actual: publication.LatestSlot,
            expected: -1
        );
        Assert.Equal(
            actual: publication.Version,
            expected: 0L
        );
        publication.Configure(targetCount: 3);
        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var first));
        Assert.Equal(
            actual: first,
            expected: 0
        );

        publication.Publish(fenceValue: 0UL, slot: 0);

        Assert.Equal(
            actual: publication.LatestSlot,
            expected: 0
        );
        Assert.Equal(
            actual: publication.Version,
            expected: 1L
        );
        Assert.True(condition: (publication.Timestamp > 0L));
        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var second));
        Assert.Equal(
            actual: second,
            expected: 1
        );

        publication.Publish(fenceValue: 0UL, slot: 2);

        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var wrapped));
        Assert.Equal(
            actual: wrapped,
            expected: 0
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new LatestSlotPublication().Configure(targetCount: 1));
    }
    [Fact]
    public void Latest_slot_stays_acquired_until_balanced_release() {
        var publication = new LatestSlotPublication();

        publication.Configure(targetCount: 2);
        publication.Publish(fenceValue: 0UL, slot: 0);
        Assert.True(condition: publication.TryAcquireLatest(fenceValue: out _, slot: out var first));
        Assert.True(condition: publication.TryAcquireLatest(fenceValue: out _, slot: out var second));
        Assert.Equal(
            actual: second,
            expected: first
        );
        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var writable));
        publication.Publish(fenceValue: 0UL, slot: writable);
        Assert.False(condition: publication.TryReserveWriteSlot(slot: out _));

        publication.Release(slot: first);
        Assert.False(condition: publication.TryReserveWriteSlot(slot: out _));
        publication.Release(slot: second);
        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var released));
        Assert.Equal(
            actual: released,
            expected: first
        );
        _ = Assert.Throws<InvalidOperationException>(testCode: () => publication.Publish(fenceValue: 0UL, slot: writable));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => publication.Release(slot: first));
    }
    [Fact]
    public void Producer_skips_acquired_slots_and_drops_when_none_are_safe() {
        var publication = new LatestSlotPublication();

        publication.Configure(targetCount: 3);
        publication.Publish(fenceValue: 0UL, slot: 0);
        Assert.True(condition: publication.TryAcquireLatest(fenceValue: out _, slot: out var firstLease));

        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var second));
        publication.Publish(fenceValue: 0UL, slot: second);
        Assert.True(condition: publication.TryAcquireLatest(fenceValue: out _, slot: out var secondLease));

        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var third));
        publication.Publish(fenceValue: 0UL, slot: third);
        Assert.True(condition: publication.TryAcquireLatest(fenceValue: out _, slot: out var thirdLease));
        Assert.False(condition: publication.TryReserveWriteSlot(slot: out _));

        publication.Release(slot: firstLease);
        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var released));
        Assert.Equal(
            actual: released,
            expected: firstLease
        );

        publication.Release(slot: secondLease);
        publication.Release(slot: thirdLease);
    }
    // A capture producer publishes faster than the renderer retires its frames, lapping the ring many times, while the
    // renderer holds each frame's lease until its frame-ring slot comes round again: no slot a lease holds is ever written,
    // so every lease reads, at its retirement, the write it acquired.
    [Fact]
    public void A_lapping_producer_never_writes_a_slot_a_lease_holds() {
        const int FramesInFlight = 2;
        const int WritesPerFrame = 3;
        var publication = new LatestSlotPublication();
        var written = new int[3];
        var held = new Queue<(int Slot, int Write)>();
        var write = 0;
        var dropped = 0;

        publication.Configure(targetCount: written.Length);

        for (var frame = 0; (frame < 64); frame++) {
            for (var tick = 0; (tick < WritesPerFrame); tick++) {
                if (!publication.TryReserveWriteSlot(slot: out var slot)) {
                    dropped++;

                    continue;
                }

                written[slot] = ++write;
                publication.Publish(
                    fenceValue: ((ulong)write),
                    slot: slot
                );
            }

            Assert.True(condition: publication.TryAcquireLatest(
                fenceValue: out var fenceValue,
                slot: out var leased
            ));
            Assert.Equal(expected: ((ulong)written[leased]), actual: fenceValue);
            held.Enqueue(item: (leased, written[leased]));

            if (held.Count > FramesInFlight) {
                var (slot, acquired) = held.Dequeue();

                Assert.Equal(expected: acquired, actual: written[slot]);
                publication.Release(slot: slot);
            }
        }

        // The producer lapped the ring, and dropped the ticks on which every slot but the latest was held.
        Assert.True(condition: (write > (3 * written.Length)));
        Assert.True(condition: (dropped > 0));
    }
    [Fact]
    public void Each_acquisition_carries_the_fence_value_its_slot_was_published_with() {
        var publication = new LatestSlotPublication();

        publication.Configure(targetCount: 3);
        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var first));
        publication.Publish(
            fenceValue: 7UL,
            slot: first
        );
        Assert.True(condition: publication.TryAcquireLatest(
            fenceValue: out var firstValue,
            slot: out var firstLease
        ));
        Assert.True(condition: publication.TryReserveWriteSlot(slot: out var second));
        publication.Publish(
            fenceValue: 0UL,
            slot: second
        );
        Assert.True(condition: publication.TryAcquireLatest(
            fenceValue: out var secondValue,
            slot: out var secondLease
        ));

        Assert.Equal(
            actual: (firstLease, firstValue, secondLease, secondValue),
            expected: (first, 7UL, second, 0UL)
        );

        publication.Release(slot: firstLease);
        publication.Release(slot: secondLease);
    }
}

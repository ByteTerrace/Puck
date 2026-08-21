using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class LatestSlotPublicationTests {
    [Fact]
    public void Cycles_slots_and_publishes_complete_state() {
        var publication = new LatestSlotPublication();

        Assert.Equal(actual: publication.LatestSlot, expected: -1);
        Assert.Equal(actual: publication.Version, expected: 0L);
        publication.Configure(targetCount: 3);
        Assert.True(publication.TryReserveWriteSlot(slot: out var first));
        Assert.Equal(expected: 0, actual: first);

        publication.Publish(slot: 0);

        Assert.Equal(actual: publication.LatestSlot, expected: 0);
        Assert.Equal(actual: publication.Version, expected: 1L);
        Assert.True(condition: (publication.Timestamp > 0L));
        Assert.True(publication.TryReserveWriteSlot(slot: out var second));
        Assert.Equal(expected: 1, actual: second);

        publication.Publish(slot: 2);

        Assert.True(publication.TryReserveWriteSlot(slot: out var wrapped));
        Assert.Equal(expected: 0, actual: wrapped);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new LatestSlotPublication().Configure(targetCount: 1));
    }
    [Fact]
    public void Producer_skips_acquired_slots_and_drops_when_none_are_safe() {
        var publication = new LatestSlotPublication();

        publication.Configure(targetCount: 3);
        publication.Publish(slot: 0);
        Assert.True(publication.TryAcquireLatest(slot: out var firstLease));

        Assert.True(publication.TryReserveWriteSlot(slot: out var second));
        publication.Publish(slot: second);
        Assert.True(publication.TryAcquireLatest(slot: out var secondLease));

        Assert.True(publication.TryReserveWriteSlot(slot: out var third));
        publication.Publish(slot: third);
        Assert.True(publication.TryAcquireLatest(slot: out var thirdLease));
        Assert.False(publication.TryReserveWriteSlot(slot: out _));

        publication.Release(slot: firstLease);
        Assert.True(publication.TryReserveWriteSlot(slot: out var released));
        Assert.Equal(expected: firstLease, actual: released);

        publication.Release(slot: secondLease);
        publication.Release(slot: thirdLease);
    }
    [Fact]
    public void Latest_slot_stays_acquired_until_balanced_release() {
        var publication = new LatestSlotPublication();

        publication.Configure(targetCount: 2);
        publication.Publish(slot: 0);
        Assert.True(publication.TryAcquireLatest(slot: out var first));
        Assert.True(publication.TryAcquireLatest(slot: out var second));
        Assert.Equal(expected: first, actual: second);
        Assert.True(publication.TryReserveWriteSlot(slot: out var writable));
        publication.Publish(slot: writable);
        Assert.False(publication.TryReserveWriteSlot(slot: out _));

        publication.Release(slot: first);
        Assert.False(publication.TryReserveWriteSlot(slot: out _));
        publication.Release(slot: second);
        Assert.True(publication.TryReserveWriteSlot(slot: out var released));
        Assert.Equal(expected: first, actual: released);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => publication.Publish(slot: writable));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => publication.Release(slot: first));
    }
}

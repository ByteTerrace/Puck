using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class SingleSlotPublicationTests {
    [Fact]
    public void A_reader_acquires_the_completed_write_with_its_fence_value_and_the_writer_waits_for_it() {
        var publication = new SingleSlotPublication();

        Assert.False(condition: publication.TryAcquireLatest(
            fenceValue: out _,
            slot: out _
        ));
        Assert.True(condition: publication.TryBeginWrite());
        publication.EndWrite(
            completed: true,
            fenceValue: 7UL
        );
        Assert.Equal(
            actual: publication.Version,
            expected: 1L
        );
        Assert.True(condition: publication.TryAcquireLatest(
            fenceValue: out var fenceValue,
            slot: out var slot
        ));
        Assert.Equal(
            actual: (slot, fenceValue),
            expected: (0, 7UL)
        );
        Assert.False(
            condition: publication.TryBeginWrite(),
            userMessage: "the producer reserved the image a reader holds"
        );
        publication.Release(slot: slot);
        Assert.True(condition: publication.TryBeginWrite());
        publication.EndWrite(
            completed: false,
            fenceValue: 9UL
        );
        Assert.True(condition: publication.TryAcquireLatest(
            fenceValue: out var kept,
            slot: out var keptSlot
        ));
        Assert.Equal(
            actual: kept,
            expected: 7UL
        );
        publication.Release(slot: keptSlot);
    }
    [Fact]
    public void Retirement_with_a_reader_holding_the_image_returns_at_once_and_refuses_every_later_use() {
        var publication = new SingleSlotPublication();

        Assert.True(condition: publication.TryBeginWrite());
        publication.EndWrite(
            completed: true,
            fenceValue: 3UL
        );
        Assert.True(condition: publication.TryAcquireLatest(
            fenceValue: out _,
            slot: out var held
        ));

        // A reader still holds the image; retiring must not wait for it.
        publication.Retire();

        Assert.False(condition: publication.HasCompletedFrame);
        Assert.Equal(
            actual: publication.LatestSlot,
            expected: -1
        );
        Assert.False(condition: publication.TryAcquireLatest(
            fenceValue: out _,
            slot: out _
        ));
        publication.Release(slot: held);
        Assert.False(
            condition: publication.TryBeginWrite(),
            userMessage: "a retired publication reserved another write"
        );
    }
}

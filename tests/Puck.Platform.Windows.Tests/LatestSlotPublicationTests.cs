using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class LatestSlotPublicationTests {
    [Fact]
    public void Cycles_slots_and_publishes_complete_state() {
        var publication = new LatestSlotPublication();

        Assert.Equal(actual: publication.LatestSlot, expected: -1);
        Assert.Equal(actual: publication.Version, expected: 0L);
        Assert.Equal(expected: 0, actual: publication.NextSlot(targetCount: 3));

        publication.Publish(slot: 0);

        Assert.Equal(actual: publication.LatestSlot, expected: 0);
        Assert.Equal(actual: publication.Version, expected: 1L);
        Assert.True(condition: (publication.Timestamp > 0L));
        Assert.Equal(expected: 1, actual: publication.NextSlot(targetCount: 3));

        publication.Publish(slot: 2);

        Assert.Equal(expected: 0, actual: publication.NextSlot(targetCount: 3));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => publication.NextSlot(targetCount: 0));
    }
}

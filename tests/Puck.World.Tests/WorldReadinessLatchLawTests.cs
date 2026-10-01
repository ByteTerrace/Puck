using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldReadinessLatch"/> reads ready only once the readiness conditions hold and the
/// host has produced a frame since a read first found them held, so the ticks the frame completing them cost are caught
/// up before a script's first wait after readiness; a read that finds a condition broken starts the latch over.
/// </summary>
public sealed class WorldReadinessLatchLawTests {
    [Fact]
    public void ReadinessOwesTheFrameAfterTheConditionsFirstHold() {
        var latch = new WorldReadinessLatch();

        Assert.False(condition: latch.Observe(conditionsHold: false, framesProduced: 3));
        // The frame that completed the conditions is frame 4; reads before frame 5 owe it.
        Assert.False(condition: latch.Observe(conditionsHold: true, framesProduced: 4));
        Assert.False(condition: latch.Observe(conditionsHold: true, framesProduced: 4));
        Assert.True(condition: latch.Observe(conditionsHold: true, framesProduced: 5));
        Assert.True(condition: latch.Observe(conditionsHold: true, framesProduced: 9));
    }
    [Fact]
    public void ABrokenConditionStartsTheLatchOver() {
        var latch = new WorldReadinessLatch();

        _ = latch.Observe(conditionsHold: true, framesProduced: 4);
        Assert.True(condition: latch.Observe(conditionsHold: true, framesProduced: 5));
        // A device loss or a rebuilt residency breaks a condition; holding again owes a fresh frame.
        Assert.False(condition: latch.Observe(conditionsHold: false, framesProduced: 6));
        Assert.False(condition: latch.Observe(conditionsHold: true, framesProduced: 7));
        Assert.True(condition: latch.Observe(conditionsHold: true, framesProduced: 8));
    }
}

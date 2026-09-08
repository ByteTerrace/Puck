using Xunit;

namespace Puck.Commands.Tests;

/// <summary>The selection-grace window, driven from a fake clock across the whole window, past its end, over the
/// tick counter's wrap, and with the window disabled.</summary>
public sealed class BindingWheelGraceTests {
    [Fact]
    public void AHeldSectorSurvivesExactlyTheAuthoredWindow() {
        var grace = new BindingWheelGrace();

        grace.BeginGesture(graceTicks: 10UL);

        Assert.Equal(expected: 10UL, actual: grace.Ticks);
        Assert.Equal(expected: 3, actual: grace.Observe(
            deadCentre: false,
            hoverSector: 3,
            nowTick: 100UL
        ));
        Assert.False(condition: grace.IsDwelling);

        // The dwell starts on the first dead-centre frame, not on the sector that preceded it.
        for (var tick = 105UL; (tick <= 115UL); tick++) {
            Assert.Equal(
                actual: grace.Observe(
                    deadCentre: true,
                    hoverSector: -1,
                    nowTick: tick
                ),
                expected: 3
            );
            Assert.True(condition: grace.IsDwelling);
        }

        // 116 is 11 ticks after the dwell began — one past a ten-tick window.
        Assert.Equal(expected: -1, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 116UL
        ));
        Assert.Equal(expected: -1, actual: grace.Sector);
    }
    [Fact]
    public void ALiveReadingRestartsTheWindowFromScratch() {
        var grace = new BindingWheelGrace();

        grace.BeginGesture(graceTicks: 4UL);

        _ = grace.Observe(
            deadCentre: false,
            hoverSector: 1,
            nowTick: 0UL
        );
        _ = grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 1UL
        );

        Assert.True(condition: grace.IsDwelling);
        Assert.Equal(expected: 2, actual: grace.Observe(
            deadCentre: false,
            hoverSector: 2,
            nowTick: 2UL
        ));
        Assert.False(condition: grace.IsDwelling);
        // The window is measured from the NEW dwell, so the sector survives to tick 3 + 4 rather than 1 + 4.
        Assert.Equal(expected: 2, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 3UL
        ));
        Assert.Equal(expected: 2, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 7UL
        ));
        Assert.Equal(expected: -1, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 8UL
        ));
    }
    [Fact]
    public void AWindowSpanningTheTickCounterWrapMeasuresItsTrueLength() {
        var grace = new BindingWheelGrace();

        grace.BeginGesture(graceTicks: 6UL);

        _ = grace.Observe(
            deadCentre: false,
            hoverSector: 5,
            nowTick: (ulong.MaxValue - 3UL)
        );

        Assert.Equal(expected: 5, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: (ulong.MaxValue - 2UL)
        ));
        // Four ticks later the counter has wrapped through zero; the unsigned difference is still four.
        Assert.Equal(expected: 5, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 1UL
        ));
        Assert.Equal(expected: 5, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 3UL
        ));
        Assert.Equal(expected: -1, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 4UL
        ));
    }
    [Fact]
    public void AZeroWindowDropsTheSectorOnTheDeadCentreFrameItself() {
        var grace = new BindingWheelGrace();

        grace.BeginGesture(graceTicks: 0UL);

        Assert.Equal(expected: 0UL, actual: grace.Ticks);
        Assert.Equal(expected: 7, actual: grace.Observe(
            deadCentre: false,
            hoverSector: 7,
            nowTick: 50UL
        ));
        Assert.Equal(expected: -1, actual: grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 50UL
        ));
        Assert.False(condition: grace.IsDwelling);
        Assert.False(condition: grace.TrySeed(sector: 7));
    }
    [Fact]
    public void OnlyADeadCentreReadingKeepsASector() {
        var grace = new BindingWheelGrace();

        grace.BeginGesture(graceTicks: 100UL);

        _ = grace.Observe(
            deadCentre: false,
            hoverSector: 2,
            nowTick: 0UL
        );

        // Cancelled, outside, or simply no selector sample: none of those is a dwell, so the sector drops at once.
        Assert.Equal(expected: -1, actual: grace.Observe(
            deadCentre: false,
            hoverSector: -1,
            nowTick: 1UL
        ));
        Assert.Equal(expected: -1, actual: grace.Sector);
    }
    [Fact]
    public void SeedingTakesTheFirstNeutralReadingAndNothingAfterIt() {
        var grace = new BindingWheelGrace();

        grace.BeginGesture(graceTicks: 20UL);

        Assert.True(condition: grace.TrySeed(sector: 4));
        Assert.Equal(expected: 4, actual: grace.Sector);
        // Already holding one.
        Assert.False(condition: grace.TrySeed(sector: 5));
        Assert.Equal(expected: 4, actual: grace.Sector);
        Assert.False(condition: grace.TrySeed(sector: -1));

        _ = grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 0UL
        );
        _ = grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 21UL
        );

        Assert.Equal(expected: -1, actual: grace.Sector);
        // A dwell is under way, so a late neutral reading cannot re-seed the window it just outlasted.
        Assert.True(condition: grace.IsDwelling);
        Assert.False(condition: grace.TrySeed(sector: 6));
    }
    [Fact]
    public void BeginningAGestureClearsTheWindowTheLastOneLeftBehind() {
        var grace = new BindingWheelGrace();

        grace.BeginGesture(graceTicks: 20UL);
        _ = grace.Observe(
            deadCentre: false,
            hoverSector: 3,
            nowTick: 0UL
        );
        _ = grace.Observe(
            deadCentre: true,
            hoverSector: -1,
            nowTick: 1UL
        );

        Assert.True(condition: grace.IsDwelling);

        grace.BeginGesture(graceTicks: 0UL);

        Assert.Equal(expected: -1, actual: grace.Sector);
        Assert.False(condition: grace.IsDwelling);
        Assert.Equal(expected: 0UL, actual: grace.Ticks);
    }
}

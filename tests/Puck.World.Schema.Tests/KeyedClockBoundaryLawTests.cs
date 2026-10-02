using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>A Step key changes at its exact engine tick, including after wrapping or seeking back to it.</summary>
public sealed class KeyedClockBoundaryLawTests {
    [InlineData(0UL)]
    [InlineData(1000UL)]
    [Theory]
    public void An_exact_key_tick_selects_that_key_without_a_phase_round_trip(ulong periods) {
        var clock = new WorldClock(Name: "day", PeriodSeconds: 100d);
        var track = new WorldKeyTrack<float>(clock: "day", keys: [
            new WorldKey<float>(At: 0d, Ease: WorldEase.Step, Value: 0f),
            new WorldKey<float>(At: 29d, Ease: WorldEase.Step, Value: 1f),
        ]);
        var at = ((periods * WorldClocks.PeriodTicks(clock: clock)) + (29UL * EngineTicks.PerSecond));

        foreach (var (tick, expected) in new[] { ((at - 1UL), 0f), (at, 1f), ((at + 1UL), 1f), (at, 1f) }) {
            Assert.Equal(expected: expected, actual: WorldKeyResolver.Scalar(
                phase: WorldClocks.Phase(clock: clock, tick: new PresentedTick(Fraction: 0d, Whole: tick)),
                span: clock.Span,
                track: track
            ));
        }
    }
}

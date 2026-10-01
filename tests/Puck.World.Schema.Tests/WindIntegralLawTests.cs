using Puck.Hosting;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a keyed rate integrates in closed form (<see cref="WorldKeyResolver.Integrate(WorldKeyTrack{float}, WorldClock, PresentedTick, double)"/>):
/// whole periods of its tick clock through the integral over one period, the period in progress through each key
/// segment's exact area. A cloud's offset therefore moves by one tick's worth of its rate between any two consecutive
/// ticks, where a key changes the rate, where the clock's period ends, and where the tick crosses 2^32, and it equals
/// the sum of every tick's worth of the eased rate.
/// </summary>
public sealed class WindIntegralLawTests {
    private const double Modulus = 64d;

    // A sixty-second clock whose wind holds 1 unit a second for half the period, steps to 3 and eases down to 2, then
    // falls back to 1 by the period's end.
    private static readonly WorldClock Clock = new(Name: "gust", PeriodSeconds: 60d);
    private static readonly WorldKeyTrack<float> Wind = new(
        clock: "gust",
        keys: [
            new WorldKey<float>(At: 0d, Ease: WorldEase.Step, Value: 1f),
            new WorldKey<float>(At: 30d, Ease: WorldEase.Smooth, Value: 3f),
            new WorldKey<float>(At: 45d, Ease: WorldEase.Linear, Value: 2f),
        ]
    );

    private static double At(ulong tick) => WorldKeyResolver.Integrate(
        clock: Clock,
        modulus: Modulus,
        tick: new PresentedTick(Fraction: 0d, Whole: tick),
        track: Wind
    );
    // The rate at an engine tick, as the keys ease it.
    private static double RateAt(ulong tick) => WorldKeyResolver.Scalar(
        phase: ((tick % WorldClocks.PeriodTicks(clock: Clock)) / ((double)WorldClocks.PeriodTicks(clock: Clock))),
        span: Clock.Span,
        track: Wind
    );
    private static double Step(ulong tick) => Math.IEEERemainder(
        x: (At(tick: (tick + 1UL)) - At(tick: tick)),
        y: Modulus
    );

    [Fact]
    public void A_keyed_rate_moves_one_ticks_worth_across_a_key() {
        var key = (30UL * EngineTicks.PerSecond);
        var perTick = (1d / EngineTicks.PerSecond);

        // Just before the key the wind is 1 and at the key it steps to 3: each step is one tick's worth of its rate.
        Assert.Equal(expected: (1d * perTick), actual: Step(tick: (key - 1UL)), precision: 9);
        Assert.Equal(expected: (3d * perTick), actual: Step(tick: key), precision: 9);
        Assert.InRange(actual: Step(tick: (key + (7UL * EngineTicks.PerSecond))), high: (3d * perTick), low: (2d * perTick));

        // Red leg: the rate times the elapsed time at each key's own rate jumps by the whole difference in rate.
        var naiveBefore = (RateAt(tick: (key - 1UL)) * ((key - 1UL) / ((double)EngineTicks.PerSecond)));
        var naiveAfter = (RateAt(tick: (key + 1UL)) * ((key + 1UL) / ((double)EngineTicks.PerSecond)));

        Assert.True(condition: (Math.Abs(value: (naiveAfter - naiveBefore)) > 1e-3d));
    }
    [Fact]
    public void A_keyed_rate_is_continuous_where_its_period_ends_and_past_two_to_the_thirty_two() {
        var period = WorldClocks.PeriodTicks(clock: Clock);
        var perTick = (1d / EngineTicks.PerSecond);
        // The last segment falls linearly from 2 to the first key's 1, which it reaches as the period ends.
        var wrapRate = 1d;

        Assert.Equal(expected: (wrapRate * perTick), actual: Step(tick: ((period * 3UL) - 1UL)), precision: 7);

        foreach (var tick in new[] { ((1UL << 32) - 1UL), (1UL << 32), ((1UL << 40) + 17UL) }) {
            Assert.InRange(actual: Step(tick: tick), high: (3.0001d * perTick), low: (0.9999d * perTick));
        }
    }
    [Fact]
    public void The_closed_form_equals_the_sum_of_every_ticks_worth_of_the_eased_rate() {
        // Summed per hundred ticks at the segment midpoints over one whole period, the area agrees to the step's error.
        var period = WorldClocks.PeriodTicks(clock: Clock);
        var total = 0d;
        const ulong Stride = 100UL;

        for (var tick = 0UL; (tick < period); tick += Stride) {
            total += (RateAt(tick: (tick + (Stride / 2UL))) * (Stride / ((double)EngineTicks.PerSecond)));
        }

        Assert.Equal(
            expected: Math.IEEERemainder(x: total, y: Modulus),
            actual: At(tick: period),
            precision: 3
        );
        Assert.Equal(expected: 0d, actual: At(tick: 0UL), precision: 12);
    }
    [Fact]
    public void A_rate_refuses_a_state_clock() {
        Assert.Throws<ArgumentException>(testCode: () => WorldKeyResolver.Integrate(
            clock: new WorldClock(Name: "tide", State: "tide"),
            modulus: Modulus,
            tick: default,
            track: Wind
        ));
    }
}

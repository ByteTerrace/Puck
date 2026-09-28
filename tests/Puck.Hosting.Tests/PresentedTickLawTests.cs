namespace Puck.Hosting.Tests;

/// <summary>The presentation clock's arithmetic: a presented tick splits exactly between two delivered ticks, a
/// periodic clock's phase is exact past 2^32 engine ticks and across a period's end, and a rate's integral stays
/// continuous where it wraps and where the tick crosses 2^32.</summary>
public sealed class PresentedTickLawTests {
    private const ulong Wrap = (1UL << 32);

    // The shortest step between two reduced integrals, taking the modulus's wrap into account.
    private static double Step(double from, double to, double modulus) => Math.IEEERemainder(
        x: (to - from),
        y: modulus
    );

    [Fact]
    public void A_presented_tick_splits_the_span_between_two_delivered_ticks_exactly() {
        var tick = PresentedTick.Between(
            current: 3360UL,
            fraction: 0.25f,
            previous: 1680UL
        );

        Assert.Equal(expected: 2100UL, actual: tick.Whole);
        Assert.Equal(expected: 0d, actual: tick.Fraction);
        Assert.Equal(
            actual: PresentedTick.Between(
                current: 3360UL,
                fraction: 1f,
                previous: 1680UL
            ),
            expected: new PresentedTick(
                Fraction: 0d,
                Whole: 3360UL
            )
        );
        // A later tick that is not after the earlier one (a seek) presents the later one alone.
        Assert.Equal(
            actual: PresentedTick.Between(
                current: 100UL,
                fraction: 0.5f,
                previous: 900UL
            ),
            expected: new PresentedTick(
                Fraction: 0d,
                Whole: 100UL
            )
        );
    }
    [Fact]
    public void A_phase_is_exact_past_two_to_the_thirty_two_ticks() {
        const ulong Period = (50400UL * 7UL);
        const ulong Offset = 12345UL;
        var tick = new PresentedTick(
            Fraction: 0d,
            Whole: (Wrap + Offset)
        );
        var expected = (((double)((Wrap + Offset) % Period)) / Period);

        Assert.Equal(expected: expected, actual: tick.Phase(periodTicks: Period));
        // Red leg: a clock carried in 32 bits wraps at 2^32 and reads another phase there.
        var truncated = (((double)(unchecked((uint)(Wrap + Offset)) % Period)) / Period);

        Assert.NotEqual(expected: truncated, actual: tick.Phase(periodTicks: Period));
    }
    [Fact]
    public void A_phase_wraps_to_zero_at_its_period_and_honors_its_start() {
        const ulong Period = 1000UL;

        Assert.Equal(expected: 0d, actual: new PresentedTick(Fraction: 0d, Whole: (Period * 5UL)).Phase(periodTicks: Period));
        Assert.Equal(expected: 0.999d, actual: new PresentedTick(Fraction: 0d, Whole: ((Period * 5UL) - 1UL)).Phase(periodTicks: Period), precision: 12);
        Assert.Equal(expected: 0.25d, actual: new PresentedTick(Fraction: 0d, Whole: 0UL).Phase(periodTicks: Period, startTicks: 250UL));
        Assert.Equal(expected: 0.0005d, actual: new PresentedTick(Fraction: 0.5d, Whole: 1000UL).Phase(periodTicks: Period), precision: 12);
    }
    [InlineData(0.02d, 4096d)]
    [InlineData(-3.5d, 4096d)]
    [InlineData(0.7d, 6.283185307179586d)]
    [Theory]
    public void An_integral_moves_by_one_ticks_worth_of_rate_across_its_wrap_and_across_two_to_the_thirty_two(double rate, double modulus) {
        var perTick = (rate / EngineTicks.PerSecond);
        // Where the tick crosses 2^32, and where the reduced value wraps its modulus.
        var wrapTick = ((ulong)Math.Ceiling(a: (((modulus / 2d) / Math.Abs(value: rate)) * EngineTicks.PerSecond)));

        foreach (var at in ((ulong[])[(Wrap - 1UL), wrapTick, (wrapTick * 1000UL)])) {
            var before = new PresentedTick(Fraction: 0d, Whole: at).Integrate(modulus: modulus, ratePerSecond: rate);
            var after = new PresentedTick(Fraction: 0d, Whole: (at + 1UL)).Integrate(modulus: modulus, ratePerSecond: rate);

            Assert.InRange(
                actual: Math.Abs(value: (Step(from: before, modulus: modulus, to: after) - perTick)),
                high: 1e-9d,
                low: 0d
            );
        }

        // Red leg: a 32-bit tick integrated as elapsed seconds times the rate jumps back where the tick wraps.
        double Truncated(ulong tick) => Math.IEEERemainder(
            x: ((((uint)tick) / ((double)EngineTicks.PerSecond)) * rate),
            y: modulus
        );

        Assert.True(condition: (Math.Abs(value: (Step(from: Truncated(tick: (Wrap - 1UL)), modulus: modulus, to: Truncated(tick: Wrap)) - perTick)) > 1e-6d));
    }
}

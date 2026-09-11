namespace Puck.GamingBricks.Tests;

/// <summary>Pins the shared resampler's emit cadence in both call shapes that drive it: the SM83 output stage's
/// per-T-cycle advance with the half-dot addend doubling (plus its quiet-stretch fast-forward), and the GBA APU's
/// per-span advance whose weight is a <see cref="long"/> product. The expected sequences are the cadence recorded
/// before the two stages were folded onto one accumulator, so a change in rounding direction, period, or addend
/// weighting moves them.</summary>
public sealed class RationalRateAccumulatorTests {
    // src/Puck.HumbleGamingBrick/AudioOutputComponent.cs: 4194304 dots per second, counted in half-dots so a
    // double-speed T-cycle weighs one and a normal-speed T-cycle weighs two.
    private const long HalfDotsPerSecond = (4_194_304L * 2L);
    // src/Puck.AdvancedGamingBrick/AgbApu.cs: the GBA master clock.
    private const long MasterClock = 16_777_216L;
    private const int SampleRate = 48_000;

    [Fact]
    public void TheSm83TickShapeEmitsOnTheRecordedNormalSpeedTicks() {
        Assert.Equal(
            actual: TickShapeEmitTicks(
                doubleSpeed: false,
                tickCount: 400
            ),
            expected: new List<int> { 87, 174, 262, 349 }
        );
    }
    [Fact]
    public void TheSm83TickShapeEmitsOnTheRecordedDoubleSpeedTicks() {
        Assert.Equal(
            actual: TickShapeEmitTicks(
                doubleSpeed: true,
                tickCount: 400
            ),
            expected: new List<int> { 174, 349 }
        );
    }
    [Fact]
    public void TheSm83TickShapeCarriesItsPhaseThroughASpeedSwitch() {
        var accumulator = default(RationalRateAccumulator);
        var emitted = new List<int>();

        for (var tick = 0; (tick < 400); ++tick) {
            var due = accumulator.Advance(
                period: HalfDotsPerSecond,
                weight: HalfDotAddend(doubleSpeed: (tick >= 200))
            );

            for (var sample = 0L; (sample < due); ++sample) {
                emitted.Add(item: tick);
            }
        }

        // The half-dot weighting is what makes the switch continuous: the rate against real emulated time is the
        // same on both sides of it, so the third emit lands 150 ticks after the second rather than 88.
        Assert.Equal(
            actual: emitted,
            expected: new List<int> { 87, 174, 324 }
        );
    }
    [Fact]
    public void TheSm83QuietStretchShapeSkipsExactlyToTheNextEmit() {
        var accumulator = default(RationalRateAccumulator);
        var observed = new List<(long Quiet, long Due)>();

        for (var stretch = 0; (stretch < 6); ++stretch) {
            var addend = HalfDotAddend(doubleSpeed: false);
            var quiet = accumulator.QuietSteps(
                period: HalfDotsPerSecond,
                weight: addend
            );

            accumulator.Skip(weight: (quiet * addend));

            observed.Add(item: (Quiet: quiet, Due: accumulator.Advance(
                period: HalfDotsPerSecond,
                weight: addend
            )));
        }

        // Every skipped stretch lands one T-cycle short of an emit, and the T-cycle after it emits exactly once.
        Assert.Equal(
            actual: observed,
            expected: new List<(long Quiet, long Due)> { (87L, 1L), (86L, 1L), (87L, 1L), (86L, 1L), (86L, 1L), (87L, 1L) }
        );
    }
    [Theory]
    // A power-of-two rate against the master clock is exact: 32768 Hz emits every 512 cycles with no remainder.
    [InlineData(32_768, new long[] { 512L, 512L, 512L, 512L, 512L, 512L, 512L, 512L }, new long[] { 1L, 1L, 1L, 1L, 1L, 1L, 1L, 1L })]
    [InlineData(32_768, new long[] { 1L, 100L, 511L, 512L, 513L, 4_096L, 280_896L, 7L }, new long[] { 0L, 0L, 1L, 1L, 1L, 8L, 548L, 0L })]
    [InlineData(44_100, new long[] { 1L, 100L, 511L, 512L, 513L, 4_096L, 280_896L, 7L, 65_535L }, new long[] { 0L, 0L, 1L, 1L, 2L, 11L, 738L, 0L, 172L })]
    // A full-frame span times a high rate exceeds int.MaxValue, which is why the weight is computed in long.
    [InlineData(96_000, new long[] { 280_896L, 280_896L, 280_896L, 280_896L }, new long[] { 1_607L, 1_607L, 1_607L, 1_608L })]
    public void TheGbaSpanShapeEmitsTheRecordedCountPerSpan(int sampleRate, long[] spans, long[] expected) {
        var accumulator = default(RationalRateAccumulator);
        var due = new long[spans.Length];

        for (var index = 0; (index < spans.Length); ++index) {
            due[index] = accumulator.Advance(
                period: MasterClock,
                weight: (spans[index] * sampleRate)
            );
        }

        Assert.Equal(
            actual: due,
            expected: expected
        );
    }
    [Fact]
    public void ResetStartsAFreshStreamAndPhaseRoundTripsThroughASnapshot() {
        var accumulator = default(RationalRateAccumulator);

        _ = accumulator.Advance(
            period: MasterClock,
            weight: (500L * 32_768L)
        );

        var captured = accumulator.Phase;

        Assert.NotEqual(
            actual: captured,
            expected: 0L
        );

        accumulator.Reset();

        Assert.Equal(
            actual: accumulator.Phase,
            expected: 0L
        );

        accumulator.Phase = captured;

        // A restored phase resumes the exact cadence the capture severed: 500 of the 512 cycles are already spent, so
        // the next 12-cycle span emits.
        Assert.Equal(
            actual: accumulator.Advance(
                period: MasterClock,
                weight: (12L * 32_768L)
            ),
            expected: 1L
        );
    }

    private static long HalfDotAddend(bool doubleSpeed) =>
        (doubleSpeed
            ? SampleRate
            : (SampleRate << 1));
    private static List<int> TickShapeEmitTicks(bool doubleSpeed, int tickCount) {
        var accumulator = default(RationalRateAccumulator);
        var emitted = new List<int>();

        for (var tick = 0; (tick < tickCount); ++tick) {
            var due = accumulator.Advance(
                period: HalfDotsPerSecond,
                weight: HalfDotAddend(doubleSpeed: doubleSpeed)
            );

            for (var sample = 0L; (sample < due); ++sample) {
                emitted.Add(item: tick);
            }
        }

        return emitted;
    }
}

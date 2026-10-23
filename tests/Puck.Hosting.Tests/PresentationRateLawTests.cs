namespace Puck.Hosting.Tests;

/// <summary>
/// Pins the floor a rated update holds by (<see cref="PresentationRate.Holds"/>): on a fixed grid an item updates every
/// whole number of refreshes that keeps it at or above its tier, the same frames as before at 60 Hz; off a grid (a
/// variable-refresh display) it holds by presentation time, so frames slower than the pacing target never lower its
/// rate, and an interval passes its period only by how much the frame that closed it outran the frame before. One machine
/// shows only its own display, so these sequences are the record of every other.
/// </summary>
public sealed class PresentationRateLawTests {
    // Steps an item that updated on frame zero through frames of the given spans and returns each interval between its
    // updates, with the span of the frame that closed it and of the frame before.
    private static List<(double Interval, double Closing, double Before)> Intervals(PresentationRate rate, int hertz, IReadOnlyList<double> spans) {
        var intervals = new List<(double, double, double)>();
        var elapsed = 0d;
        var before = 0d;

        foreach (var span in spans) {
            elapsed += span;

            if (rate.Holds(elapsedSeconds: elapsed, frameSeconds: span, hertz: hertz)) {
                before = span;

                continue;
            }

            intervals.Add(item: (elapsed, span, before));
            elapsed = 0d;
            before = span;
        }

        return intervals;
    }
    private static double[] Steady(double hertz, int count) => [.. Enumerable.Repeat(element: (1d / hertz), count: count)];

    // A fixed signal updates every whole number of its refreshes rounded down from the tier: never below the tier, and at
    // 60 Hz every second and fourth frame as before.
    [InlineData(60, 30, 2)]
    [InlineData(60, 15, 4)]
    [InlineData(75, 30, 2)]
    [InlineData(75, 15, 5)]
    [InlineData(144, 30, 4)]
    [InlineData(144, 15, 9)]
    [InlineData(40, 30, 1)]
    [InlineData(40, 15, 2)]
    [Theory]
    public void AFixedSignalUpdatesEveryWholeNumberOfRefreshesAtOrAboveTheTier(int signal, int hertz, int refreshes) {
        var rate = new PresentationRate(DisplayHertz: signal, GridHertz: signal);
        // Frame times jitter around the signal's period, as a sampled wall interval does.
        var spans = Enumerable.Range(start: 0, count: 240).Select(selector: index => ((1d / signal) * (1d + (((index % 3) - 1) * 0.04d)))).ToArray();
        var intervals = Intervals(hertz: hertz, rate: rate, spans: spans);

        Assert.All(collection: intervals, action: interval => Assert.Equal(expected: refreshes, actual: Math.Round(a: (interval.Interval * signal))));
        Assert.Equal(expected: (refreshes / (double)signal), actual: rate.PeriodSeconds(hertz: hertz), tolerance: 1e-12);
        Assert.True(condition: (rate.PeriodSeconds(hertz: hertz) <= (1d / hertz)));
    }
    // A fixed signal that misses frames counts the refreshes they took: a run of frames each two refreshes long updates
    // the 30 Hz tier every frame rather than every second frame, so it stays at 30 Hz.
    [Fact]
    public void MissedFramesOnAFixedSignalCountTheRefreshesTheyTook() {
        var rate = new PresentationRate(DisplayHertz: 60, GridHertz: 60d);
        var intervals = Intervals(hertz: 30, rate: rate, spans: Steady(hertz: 30d, count: 60));

        Assert.All(collection: intervals, action: static interval => Assert.Equal(expected: 2d, actual: Math.Round(a: (interval.Interval * 60d))));
    }
    // Off a grid, frames at any steady pace update each tier within its period and no sooner than one frame before it:
    // pacing at 141 Hz but rendering at 70 frames a second, the 30 Hz tier updates every second frame (35 Hz).
    [Fact]
    public void SteadyFramesOffAGridUpdateWithinThePeriodAndHoldAsLongAsItAllows() {
        var rate = new PresentationRate(DisplayHertz: 141, GridHertz: 0d);

        Assert.All(collection: Intervals(hertz: 30, rate: rate, spans: Steady(hertz: 70d, count: 140)), action: static interval => Assert.Equal(expected: (2d / 70d), actual: interval.Interval, tolerance: 1e-12));

        for (var fps = 31; (fps <= 240); fps++) {
            foreach (var hertz in (int[])[30, 15]) {
                var period = rate.PeriodSeconds(hertz: hertz);

                foreach (var (interval, closing, _) in Intervals(hertz: hertz, rate: rate, spans: Steady(hertz: fps, count: (fps * 2)))) {
                    Assert.True(condition: (interval <= (period + PresentationRate.ToleranceSeconds)), userMessage: $"{fps} fps at {hertz} Hz held {interval * 1000d:0.###} ms");
                    Assert.True(condition: ((interval + closing) > period), userMessage: $"{fps} fps at {hertz} Hz updated early after {interval * 1000d:0.###} ms");
                }
            }
        }
    }
    // A variable-refresh display paced at 60 Hz holds the same frames as a fixed one: its sampled frame times jitter a few
    // tenths of a millisecond either side of the period, within the floor's slack.
    [InlineData(30, 2)]
    [InlineData(15, 4)]
    [Theory]
    public void AJitteredSixtyHertzOffAGridHoldsTheSameFramesAsAFixedOne(int hertz, int frames) {
        var rate = new PresentationRate(DisplayHertz: 60, GridHertz: 0d);
        var spans = Enumerable.Range(start: 0, count: 240).Select(selector: static index => ((1d / 60d) + ((((index * 7) % 5) - 2) * 0.00015d))).ToArray();

        Assert.All(collection: Intervals(hertz: hertz, rate: rate, spans: spans), action: interval => Assert.Equal(expected: frames, actual: Math.Round(a: (interval.Interval * 60d))));
    }
    // Off a grid, irregular frames (a variable-refresh display, a hitch) keep each interval within the period but for
    // the amount the frame that closed it outran the frame before, or a single frame longer than the period itself.
    [InlineData(30)]
    [InlineData(15)]
    [Theory]
    public void IrregularFramesOffAGridPassThePeriodOnlyByTheUnforeseenPartOfAFrame(int hertz) {
        var rate = new PresentationRate(DisplayHertz: 141, GridHertz: 0d);
        var period = rate.PeriodSeconds(hertz: hertz);
        var spans = Enumerable.Range(start: 0, count: 2000).Select(selector: static index => {
            var wobble = Math.Sin(a: (index * 1.7d));
            var span = ((1d / 141d) * (1d + (4d * wobble * wobble)));

            return (((index % 97) == 0) ? (span * 6d) : span);
        }).ToArray();
        var intervals = Intervals(hertz: hertz, rate: rate, spans: spans);

        Assert.NotEmpty(collection: intervals);
        Assert.All(collection: intervals, action: interval => Assert.True(
            condition: (interval.Interval <= (Math.Max(val1: (period + Math.Max(val1: 0d, val2: (interval.Closing - interval.Before))), val2: interval.Closing) + PresentationRate.ToleranceSeconds)),
            userMessage: $"{interval.Interval * 1000d:0.###} ms closed by a {interval.Closing * 1000d:0.###} ms frame after {interval.Before * 1000d:0.###} ms"
        ));
    }
    // A host not paced to a display, and a tier of no rate, hold nothing; a cap rounds its divisor up and a display of
    // unknown rate renders it every frame.
    [Fact]
    public void AnUnpacedHostHoldsNothingAndACapNeverRendersFasterThanAsked() {
        Assert.False(condition: new PresentationRate(DisplayHertz: 0, GridHertz: 0d).Holds(elapsedSeconds: 0d, frameSeconds: 0.001d, hertz: 15));
        Assert.False(condition: new PresentationRate(DisplayHertz: 60, GridHertz: 60d).Holds(elapsedSeconds: 0d, frameSeconds: 0.001d, hertz: 0));
        Assert.Equal(expected: 5, actual: new PresentationRate(DisplayHertz: 144, GridHertz: 144d).FramesAtMost(hertz: 30));
        Assert.Equal(expected: 1, actual: new PresentationRate(DisplayHertz: 60, GridHertz: 60d).FramesAtMost(hertz: 60));
        Assert.Equal(expected: 1, actual: new PresentationRate(DisplayHertz: 0, GridHertz: 0d).FramesAtMost(hertz: 30));
        Assert.Equal(expected: (1d / 30d), actual: new PresentationRate(DisplayHertz: 141, GridHertz: 0d).PeriodSeconds(hertz: 30), tolerance: 1e-12);
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => new PresentationRate(DisplayHertz: 60, GridHertz: 0d).FramesAtMost(hertz: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: static () => new PresentationRate(DisplayHertz: 60, GridHertz: 0d).PeriodSeconds(hertz: 0));
    }
}

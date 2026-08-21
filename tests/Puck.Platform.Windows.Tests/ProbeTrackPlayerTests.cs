using System.Diagnostics;
using Puck.Platform.Probes;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class ProbeTrackPlayerTests {
    private static readonly long PeriodTicks = ((long)Math.Round(Stopwatch.Frequency / 30.0));

    // A sample time sitting exactly on the 30 Hz tick grid the assertions below walk.
    private static double GridSeconds(int index) => ((index * PeriodTicks) / (double)Stopwatch.Frequency);

    [Fact]
    public void Rejects_a_document_that_fails_a_shape_check() {
        var ring = new ProbeReadingRing();

        _ = Assert.Throws<InvalidDataException>(testCode: () => new ProbeTrackPlayer(
            document: new ProbeTrackDocument(Schema: "wrong"),
            ring: ring
        ));
        _ = Assert.Throws<InvalidDataException>(testCode: () => new ProbeTrackPlayer(
            document: new ProbeTrackDocument(RateHz: 0.0),
            ring: ring
        ));
        _ = Assert.Throws<InvalidDataException>(testCode: () => new ProbeTrackPlayer(
            document: new ProbeTrackDocument(Samples: []),
            ring: ring
        ));
        _ = Assert.Throws<InvalidDataException>(testCode: () => new ProbeTrackPlayer(
            document: new ProbeTrackDocument(Channels: 2, Samples: [new ProbeTrackSample(T: 0.0, C: [1.0])]),
            ring: ring
        ));
    }
    [Fact]
    public void Rejects_sample_times_that_are_negative_not_finite_or_not_ascending() {
        var ring = new ProbeReadingRing();

        _ = Assert.Throws<InvalidDataException>(testCode: () => new ProbeTrackPlayer(
            document: new ProbeTrackDocument(Samples: [new ProbeTrackSample(T: -0.1)]),
            ring: ring
        ));
        _ = Assert.Throws<InvalidDataException>(testCode: () => new ProbeTrackPlayer(
            document: new ProbeTrackDocument(Samples: [new ProbeTrackSample(T: double.NaN)]),
            ring: ring
        ));
        _ = Assert.Throws<InvalidDataException>(testCode: () => new ProbeTrackPlayer(
            document: new ProbeTrackDocument(Samples: [new ProbeTrackSample(T: 0.0), new ProbeTrackSample(T: 100.0), new ProbeTrackSample(T: 0.5)]),
            ring: ring
        ));
        _ = Assert.Throws<InvalidDataException>(testCode: () => new ProbeTrackPlayer(
            document: new ProbeTrackDocument(Samples: [new ProbeTrackSample(T: 0.0), new ProbeTrackSample(T: 0.0)]),
            ring: ring
        ));
    }
    [Fact]
    public void Irregular_sample_times_set_the_playback_cadence_and_the_loop_length() {
        var ring = new ProbeReadingRing();
        var document = new ProbeTrackDocument(
            RateHz: 30.0,
            Channels: 1,
            Samples: [
                new ProbeTrackSample(T: 0.0, C: [0.0]),
                new ProbeTrackSample(T: 0.5, C: [1.0]),
                new ProbeTrackSample(T: 0.6, C: [2.0]),
            ]
        );
        var player = new ProbeTrackPlayer(document: document, ring: ring);
        const long Origin = 7_000_000L;
        long Seconds(double seconds) => ((long)Math.Round(seconds * Stopwatch.Frequency));

        Assert.True(player.Advance(nowTimestamp: Origin));
        Assert.False(player.Advance(nowTimestamp: (Origin + Seconds(seconds: 0.25))));
        Assert.True(player.Advance(nowTimestamp: (Origin + Seconds(seconds: 0.5))));
        Assert.True(ring.TryReadLatest(reading: out var second));
        Assert.Equal(expected: 1, actual: ((int)Math.Round((double)second[0])));
        Assert.Equal(expected: (Origin + Seconds(seconds: 0.5)), actual: second.CaptureTimestamp);
        Assert.False(player.Advance(nowTimestamp: (Origin + Seconds(seconds: 0.55))));
        Assert.True(player.Advance(nowTimestamp: (Origin + Seconds(seconds: 0.6))));
        Assert.True(ring.TryReadLatest(reading: out var third));
        Assert.Equal(expected: 2, actual: ((int)Math.Round((double)third[0])));

        // The track loops one nominal period (1/30 s) after its last sample; a call inside that gap publishes
        // nothing, and the first call past it publishes sample zero again with the loop's own capture time.
        var loopSeconds = (0.6 + (1.0 / 30.0));

        Assert.False(player.Advance(nowTimestamp: (Origin + Seconds(seconds: 0.61))));
        Assert.True(player.Advance(nowTimestamp: (Origin + Seconds(seconds: loopSeconds) + 1L)));
        Assert.True(ring.TryReadLatest(reading: out var looped));
        Assert.Equal(expected: 0, actual: ((int)Math.Round((double)looped[0])));
        Assert.Equal(expected: (Origin + Seconds(seconds: 0.6) + PeriodTicks), actual: looped.CaptureTimestamp);
    }
    [Fact]
    public void Publishes_each_sample_exactly_once_per_period_and_loops() {
        var ring = new ProbeReadingRing();
        var document = new ProbeTrackDocument(
            RateHz: 30.0,
            Channels: 1,
            Samples: [
                new ProbeTrackSample(T: 0.0, C: [0.0]),
                new ProbeTrackSample(T: GridSeconds(index: 1), C: [1.0]),
                new ProbeTrackSample(T: GridSeconds(index: 2), C: [2.0]),
            ]
        );
        var player = new ProbeTrackPlayer(document: document, ring: ring);
        const long Origin = 1_000_000L;
        var observedSlots = new List<int>();
        var observedVersions = new List<long>();

        // Sample within each period's window a few times; only the first Advance per window should publish.
        for (var globalIndex = 0; (globalIndex < 9); globalIndex++) {
            var windowStart = (Origin + (globalIndex * PeriodTicks));

            Assert.True(player.Advance(nowTimestamp: windowStart));
            Assert.False(player.Advance(nowTimestamp: (windowStart + (PeriodTicks / 4))));
            Assert.False(player.Advance(nowTimestamp: (windowStart + (PeriodTicks / 2))));

            Assert.True(ring.TryReadLatest(reading: out var reading));
            observedSlots.Add(item: ((int)Math.Round((double)reading[0])));
            observedVersions.Add(item: ring.Version);
        }

        Assert.Equal(actual: observedSlots, expected: [0, 1, 2, 0, 1, 2, 0, 1, 2]);
        Assert.Equal(actual: observedVersions, expected: [1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L]);
    }
    [Fact]
    public void Capture_timestamp_is_the_sample_own_declared_time_not_the_advance_call_time() {
        var ring = new ProbeReadingRing();
        var document = new ProbeTrackDocument(
            RateHz: 30.0,
            Channels: 1,
            Samples: [
                new ProbeTrackSample(T: 0.0, C: [0.0]),
                new ProbeTrackSample(T: GridSeconds(index: 1), C: [1.0]),
            ]
        );
        var player = new ProbeTrackPlayer(document: document, ring: ring);
        const long Origin = 5_000_000L;
        var secondSampleOffsetTicks = PeriodTicks;

        Assert.True(player.Advance(nowTimestamp: Origin));
        Assert.True(ring.TryReadLatest(reading: out var first));
        Assert.Equal(actual: first.CaptureTimestamp, expected: Origin);

        // Advance well past the second sample's window; the reported capture time still tracks the sample's own
        // declared offset from the origin, not the (later) instant this call observed it.
        var lateArrival = (Origin + PeriodTicks + (PeriodTicks / 3));

        Assert.True(player.Advance(nowTimestamp: lateArrival));
        Assert.True(ring.TryReadLatest(reading: out var second));
        Assert.Equal(actual: second.CaptureTimestamp, expected: (Origin + secondSampleOffsetTicks));
        Assert.Equal(actual: second.CompletionTimestamp, expected: lateArrival);
    }
}

using Puck.Maths;
using Puck.Platform.Probes;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class ProbeReadingRingTests {
    [Fact]
    public void Reader_before_any_publish_gets_false_and_version_starts_zero() {
        var ring = new ProbeReadingRing();

        Assert.Equal(actual: ring.Version, expected: 0L);
        Assert.False(condition: ring.TryReadLatest(reading: out _));
    }
    [Fact]
    public void Publish_updates_version_and_reader_sees_the_latest_measurement() {
        var ring = new ProbeReadingRing();

        ring.Publish(reading: MakeMeasurement(sequence: 1L));
        Assert.Equal(actual: ring.Version, expected: 1L);
        Assert.True(condition: ring.TryReadLatest(reading: out var first));
        Assert.Equal(actual: first.Sequence, expected: 1L);

        ring.Publish(reading: MakeMeasurement(sequence: 2L));
        Assert.Equal(actual: ring.Version, expected: 2L);
        Assert.True(condition: ring.TryReadLatest(reading: out var second));
        Assert.Equal(actual: second.Sequence, expected: 2L);
    }
    [Fact]
    public async Task Concurrent_publish_and_read_never_observes_a_torn_or_regressed_measurement() {
        const int PublishCount = 200_000;
        var ring = new ProbeReadingRing();
        var readerException = default(Exception);
        var stop = 0;
        var cancellationToken = TestContext.Current.CancellationToken;

        var producer = Task.Run(() => {
            for (var sequence = 0L; (sequence < PublishCount); sequence++) {
                ring.Publish(reading: MakeMeasurement(sequence: sequence));
            }

            Volatile.Write(location: ref stop, value: 1);
        }, cancellationToken: cancellationToken);
        var reader = Task.Run(() => {
            var lastSeen = -1L;

            try {
                while (0 == Volatile.Read(location: ref stop)) {
                    if (!ring.TryReadLatest(reading: out var reading)) {
                        continue;
                    }

                    AssertUntornAndMonotone(lastSeen: ref lastSeen, reading: reading);
                }

                // Drain whatever landed after the producer's final publish but before this loop noticed `stop`.
                if (ring.TryReadLatest(reading: out var trailing)) {
                    AssertUntornAndMonotone(lastSeen: ref lastSeen, reading: trailing);
                }
            } catch (Exception exception) {
                readerException = exception;
            }
        }, cancellationToken: cancellationToken);

        await Task.WhenAll(producer, reader);

        if (readerException is not null) {
            throw readerException;
        }
    }

    private static void AssertUntornAndMonotone(ProbeReading reading, ref long lastSeen) {
        Assert.True(condition: (reading.Sequence >= lastSeen));

        var expected = FixedQ4816.FromDouble(value: reading.Sequence);

        for (var channel = 0; (channel < reading.ChannelCount); channel++) {
            Assert.Equal(actual: reading[channel], expected: expected);
        }

        lastSeen = reading.Sequence;
    }
    private static ProbeReading MakeMeasurement(long sequence) {
        var value = FixedQ4816.FromDouble(value: sequence);
        var channels = default(ProbeChannelValues);

        for (var channel = 0; (channel < ProbeReadingLimits.MaxChannels); channel++) {
            channels[channel] = value;
        }

        return new ProbeReading(
            sequence: sequence,
            captureTimestamp: sequence,
            completionTimestamp: sequence,
            confidence: FixedQ4816.One,
            channelCount: ProbeReadingLimits.MaxChannels,
            channels: channels
        );
    }
}

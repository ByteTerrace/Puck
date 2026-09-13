using Puck.Maths;
using Puck.Platform.Probes;
using Xunit;

namespace Puck.Platform.Windows.Tests;

public sealed class ProbeReadingRingTests {
    private static void AssertUntornAndMonotone(ProbeReading reading, ref long lastSeen) {
        Assert.True(condition: (reading.Sequence >= lastSeen));

        var expected = FixedQ4816.FromDouble(value: reading.Sequence);

        for (var channel = 0; (channel < reading.ChannelCount); channel++) {
            Assert.Equal(
                actual: reading[channel],
                expected: expected
            );
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

    [Fact]
    public async Task Concurrent_publish_and_read_never_observes_a_torn_or_regressed_measurement() {
        const int PublishCount = 200_000;
        var ring = new ProbeReadingRing();
        var stop = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token: TestContext.Current.CancellationToken);

        deadline.CancelAfter(delay: TimeSpan.FromSeconds(seconds: 10));
        var cancellationToken = deadline.Token;
        var firstPublished = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRead = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        var producer = Task.Run(
            async () => {
            try {
                ring.Publish(reading: MakeMeasurement(sequence: 0));
                firstPublished.TrySetResult();
                // Require a live reader before the remaining publications. Otherwise the producer can finish
                // before the reader is scheduled and a purported concurrency law checks only its final value.
                await firstRead.Task.WaitAsync(cancellationToken: cancellationToken);
                for (var sequence = 1L; (sequence < PublishCount); sequence++) {
                    cancellationToken.ThrowIfCancellationRequested();
                    ring.Publish(reading: MakeMeasurement(sequence: sequence));
                }
            } finally {
                Volatile.Write(
                    location: ref stop,
                    value: 1
                );
                firstPublished.TrySetResult();
            }
        },
            cancellationToken: cancellationToken
        );
        var reader = Task.Run(
            async () => {
            var lastSeen = -1L;

            try {
                await firstPublished.Task.WaitAsync(cancellationToken: cancellationToken);
                Assert.True(condition: ring.TryReadLatest(reading: out var initial));
                Assert.Equal(
                    0,
                    initial.Sequence
                );
                AssertUntornAndMonotone(
                    lastSeen: ref lastSeen,
                    reading: initial
                );
                firstRead.TrySetResult();
                while (0 == Volatile.Read(location: ref stop)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (ring.TryReadLatest(reading: out var reading)) {
                        AssertUntornAndMonotone(
                            lastSeen: ref lastSeen,
                            reading: reading
                        );
                    }
                }

                Assert.True(condition: ring.TryReadLatest(reading: out var trailing));
                AssertUntornAndMonotone(
                    lastSeen: ref lastSeen,
                    reading: trailing
                );
                Assert.Equal(
                    (PublishCount - 1),
                    trailing.Sequence
                );
            } finally {
                // A failed reader must not strand the producer at its handshake.
                firstRead.TrySetResult();
            }
        },
            cancellationToken: cancellationToken
        );

        await Task.WhenAll(
            producer,
            reader
        );
    }
    [Fact]
    public void Publish_updates_version_and_reader_sees_the_latest_measurement() {
        var ring = new ProbeReadingRing();

        ring.Publish(reading: MakeMeasurement(sequence: 1L));
        Assert.Equal(
            actual: ring.Version,
            expected: 1L
        );
        Assert.True(condition: ring.TryReadLatest(reading: out var first));
        Assert.Equal(
            actual: first.Sequence,
            expected: 1L
        );

        ring.Publish(reading: MakeMeasurement(sequence: 2L));
        Assert.Equal(
            actual: ring.Version,
            expected: 2L
        );
        Assert.True(condition: ring.TryReadLatest(reading: out var second));
        Assert.Equal(
            actual: second.Sequence,
            expected: 2L
        );
    }
    [Fact]
    public void Reader_before_any_publish_gets_false_and_version_starts_zero() {
        var ring = new ProbeReadingRing();

        Assert.Equal(
            actual: ring.Version,
            expected: 0L
        );
        Assert.False(condition: ring.TryReadLatest(reading: out _));
    }
}

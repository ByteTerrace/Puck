using Puck.Maths;

namespace Puck.Networking.Peers;

/// <summary>Deterministic delivery delay, jitter, and burst-bunching schedule applied to a <see cref="PeerStream"/>'s
/// asynchronous reads and writes. When <see cref="JitterMilliseconds"/> is zero or negative, stream I/O proceeds
/// without artificial delay.</summary>
/// <param name="JitterMilliseconds">Maximum per-message uniform jitter in milliseconds; periodic bunching stalls hold
/// frames for up to three to four times this window before releasing the queued burst in order at wire speed.</param>
/// <param name="Seed">Seed state fed into <see cref="Puck.Maths.Pcg32XshRr"/> to derive deterministic per-stream read
/// and write schedules.</param>
public readonly record struct PeerDeliverySchedule(int JitterMilliseconds, ulong Seed = 1UL) {
    /// <summary>Gets whether this schedule applies artificial delivery delay, jitter, and bunching.</summary>
    public bool IsEnabled => (JitterMilliseconds > 0);
}

internal sealed class PeerStreamPacer {
    private const int HandshakeMessages = 3;
    private const long MaxReadStallMilliseconds = 1200L;
    private const long MaxWriteStallMilliseconds = 900L;

    private readonly Lock m_gate = new();
    private readonly int m_jitterMilliseconds;
    private readonly TimeProvider m_timeProvider;

    private long m_lastReleaseTimestamp;
    private ulong m_messages;
    private long m_nextWindowTimestamp;
    private Pcg32XshRr m_rng;
    private long m_stallUntilTimestamp;

    public PeerStreamPacer(int jitterMilliseconds, ulong seed, ulong streamId, TimeProvider timeProvider) {
        m_jitterMilliseconds = jitterMilliseconds;
        m_timeProvider = timeProvider;
        m_rng = Pcg32XshRr.Create(
            state: seed,
            stream: (streamId & Pcg32XshRr.MaxStream)
        );
    }

    public long CurrentFloorTimestamp {
        get {
            lock (m_gate) { return m_lastReleaseTimestamp; }
        }
    }

    private long MillisecondsToTimestamps(long milliseconds) =>
        ((milliseconds * m_timeProvider.TimestampFrequency) / 1000L);

    public long ScheduleArrival() {
        lock (m_gate) {
            m_messages++;
            var now = m_timeProvider.GetTimestamp();
            long delayMs;

            if (m_messages <= HandshakeMessages) {
                delayMs = ((long)m_rng.NextUInt32(
                    maximum: ((uint)Math.Min(
                        val1: m_jitterMilliseconds,
                        val2: 50
                    )),
                    minimum: 0U
                ));
            } else if (m_rng.NextUInt32(
                maximum: 11U,
                minimum: 0U
            ) == 0U) {
                var minStall = ((uint)Math.Min(
                    val1: (2L * m_jitterMilliseconds),
                    val2: MaxReadStallMilliseconds
                ));
                var maxStall = ((uint)Math.Min(
                    val1: (4L * m_jitterMilliseconds),
                    val2: MaxReadStallMilliseconds
                ));

                delayMs = m_rng.NextUInt32(
                    maximum: Math.Max(
                        val1: minStall,
                        val2: maxStall
                    ),
                    minimum: minStall
                );
            } else {
                delayMs = m_rng.NextUInt32(
                    maximum: ((uint)m_jitterMilliseconds),
                    minimum: 0U
                );
            }

            var candidate = (now + MillisecondsToTimestamps(milliseconds: delayMs));

            if (candidate > m_lastReleaseTimestamp) {
                m_lastReleaseTimestamp = candidate;
            }

            return m_lastReleaseTimestamp;
        }
    }

    public ValueTask WaitUntilAsync(long releaseTimestamp, CancellationToken ct) {
        var now = m_timeProvider.GetTimestamp();

        if (releaseTimestamp <= now) {
            return ValueTask.CompletedTask;
        }

        var delay = m_timeProvider.GetElapsedTime(
            endingTimestamp: releaseTimestamp,
            startingTimestamp: now
        );

        return new ValueTask(task: Task.Delay(
            cancellationToken: ct,
            delay: delay,
            timeProvider: m_timeProvider
        ));
    }

    public ValueTask PaceWriteAsync(CancellationToken ct) {
        long targetTimestamp;

        lock (m_gate) {
            m_messages++;
            if (m_messages <= HandshakeMessages) {
                return ValueTask.CompletedTask;
            }

            var now = m_timeProvider.GetTimestamp();

            if (now < m_stallUntilTimestamp) {
                targetTimestamp = m_stallUntilTimestamp;
            } else if (now >= m_nextWindowTimestamp) {
                var minStall = ((uint)Math.Min(
                    val1: m_jitterMilliseconds,
                    val2: MaxWriteStallMilliseconds
                ));
                var maxStall = ((uint)Math.Min(
                    val1: (3L * m_jitterMilliseconds),
                    val2: MaxWriteStallMilliseconds
                ));
                var stallMs = m_rng.NextUInt32(
                    maximum: Math.Max(
                        val1: minStall,
                        val2: maxStall
                    ),
                    minimum: minStall
                );
                var clearMs = m_rng.NextUInt32(
                    maximum: 900U,
                    minimum: 450U
                );

                m_stallUntilTimestamp = (now + MillisecondsToTimestamps(milliseconds: stallMs));
                m_nextWindowTimestamp = (m_stallUntilTimestamp + MillisecondsToTimestamps(milliseconds: clearMs));
                targetTimestamp = m_stallUntilTimestamp;
            } else {
                return ValueTask.CompletedTask;
            }
        }

        return WaitUntilAsync(
            ct: ct,
            releaseTimestamp: targetTimestamp
        );
    }
}

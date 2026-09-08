using System.Diagnostics;
using System.Text.Json.Serialization;
using Puck.Maths;

namespace Puck.Platform.Probes;

/// <summary>One recorded sample in a <c>puck.probe-track.v1</c> track.</summary>
/// <param name="T">The elapsed capture time, in seconds from the track's own start. Finite, non-negative, and
/// strictly ascending across the track — the schedule playback follows.</param>
/// <param name="C">The channel values, one entry per the document's declared <see cref="ProbeTrackDocument.Channels"/>.</param>
/// <param name="K">The confidence, nominally in <c>[0, 1]</c>.</param>
public sealed record ProbeTrackSample(double T, IReadOnlyList<double>? C = null, double K = 1.0);
/// <summary>
/// A recorded reading track (<c>puck.probe-track.v1</c>): a timestamped sequence of samples an probe can be
/// fed instead of a live device, so its bindings are testable without hardware.
/// </summary>
/// <param name="Schema">The schema tag; must equal <see cref="SchemaVersion"/>.</param>
/// <param name="RateHz">The nominal sample rate. Playback cadence follows each sample's own
/// <see cref="ProbeTrackSample.T"/>; this rate only sets the gap after the last sample before the track loops.</param>
/// <param name="Channels">The number of channel values each sample carries.</param>
/// <param name="Samples">The recorded samples, in strictly ascending <see cref="ProbeTrackSample.T"/> order.</param>
public sealed record ProbeTrackDocument(string? Schema = ProbeTrackDocument.SchemaVersion, double RateHz = 30.0, int Channels = 0, IReadOnlyList<ProbeTrackSample>? Samples = null) {
    /// <summary>The only accepted schema tag.</summary>
    public const string SchemaVersion = "puck.probe-track.v1";
}
/// <summary>
/// The System.Text.Json source-generation context for <see cref="ProbeTrackDocument"/> — the only sanctioned entry
/// point for (de)serializing a probe track. Source-gen (not runtime reflection) keeps the boundary trimming/AOT-clean.
/// </summary>
[JsonSerializable(typeof(ProbeTrackDocument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
public sealed partial class ProbeTrackJsonContext : JsonSerializerContext {
}
/// <summary>
/// Plays a <see cref="ProbeTrackDocument"/> into a <see cref="ProbeReadingRing"/> on a <see cref="Stopwatch"/>
/// clock, looping — the hardware-free substitute for a live probe input. Each sample publishes once its own
/// <see cref="ProbeTrackSample.T"/> has elapsed since the playback origin (plus whole loops), so an irregularly
/// recorded track replays at its recorded cadence. <see cref="Advance"/> is driven from the host's own frame loop;
/// the player owns no thread of its own. Publication is latest-wins like every other probes stage: an
/// <see cref="Advance"/> call that lands after several samples' times have passed publishes only the latest of
/// them, never the ones it skipped past.
/// </summary>
public sealed class ProbeTrackPlayer {
    private readonly int m_channelCount;
    private readonly long m_loopTicks;
    private readonly ProbeReadingRing m_ring;
    private readonly ProbeTrackSample[] m_samples;
    private readonly long[] m_sampleTicks;

    private int m_cursor = -1;
    private long m_loop = -1L;
    private long m_originTimestamp = -1L;
    private long m_sequence;

    /// <summary>Loads and validates a track document, binding it to the ring it will publish into.</summary>
    /// <param name="document">The parsed track document.</param>
    /// <param name="ring">The ring to publish into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="ring"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="InvalidDataException">The document fails one of the <c>puck.probe-track.v1</c> shape
    /// checks: wrong schema tag, a non-positive rate, a channel count outside
    /// <c>0..</c><see cref="ProbeReadingLimits.MaxChannels"/>, no samples, a sample whose channel count
    /// disagrees with the declared <see cref="ProbeTrackDocument.Channels"/>, or a sample time that is not finite,
    /// is negative, or does not strictly ascend from the previous sample's.</exception>
    public ProbeTrackPlayer(ProbeTrackDocument document, ProbeReadingRing ring) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(ring);

        if (!string.Equals(a: document.Schema, b: ProbeTrackDocument.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"a probe track's $schema must be '{ProbeTrackDocument.SchemaVersion}'.");
        }
        if (!double.IsFinite(d: document.RateHz) || (document.RateHz <= 0.0)) {
            throw new InvalidDataException(message: "a probe track's rateHz must be positive.");
        }
        if (
            (document.Channels < 0) ||
            (document.Channels > ProbeReadingLimits.MaxChannels)
        ) {
            throw new InvalidDataException(message: $"a probe track's channels must be between 0 and {ProbeReadingLimits.MaxChannels}.");
        }

        var samples = (document.Samples ?? []);

        if (samples.Count == 0) {
            throw new InvalidDataException(message: "a probe track must declare at least one sample.");
        }

        var sampleTicks = new long[samples.Count];
        var previousTicks = -1L;

        for (var index = 0; (index < samples.Count); index++) {
            var sample = samples[index];

            if ((sample.C?.Count ?? 0) != document.Channels) {
                throw new InvalidDataException(message: $"every probe track sample must carry exactly {document.Channels} channel value(s).");
            }
            if (!double.IsFinite(d: sample.T) || (sample.T < 0.0)) {
                throw new InvalidDataException(message: $"probe track sample {index} has t {sample.T}; every t must be finite and non-negative.");
            }

            var ticks = ((long)Math.Round(a: (sample.T * Stopwatch.Frequency)));

            if (ticks <= previousTicks) {
                throw new InvalidDataException(message: $"probe track sample {index} has t {sample.T}, which does not ascend from the previous sample's.");
            }

            sampleTicks[index] = ticks;
            previousTicks = ticks;
        }

        var periodTicks = Math.Max(val1: 1L, val2: ((long)Math.Round(a: (Stopwatch.Frequency / document.RateHz))));

        m_channelCount = document.Channels;
        m_loopTicks = (previousTicks + periodTicks);
        m_ring = ring;
        m_samples = [.. samples];
        m_sampleTicks = sampleTicks;
    }

    /// <summary>Advances playback to <paramref name="nowTimestamp"/>, publishing at most one reading: the
    /// latest sample whose own time the elapsed playback has reached and that has not been published in this loop.
    /// The first call establishes the playback origin.</summary>
    /// <param name="nowTimestamp">The current <see cref="Stopwatch"/> timestamp.</param>
    /// <returns><see langword="true"/> when a reading was published.</returns>
    public bool Advance(long nowTimestamp) {
        if (m_originTimestamp < 0L) {
            m_originTimestamp = nowTimestamp;
        }

        var elapsedTicks = Math.Max(val1: 0L, val2: (nowTimestamp - m_originTimestamp));
        var loop = (elapsedTicks / m_loopTicks);
        var offsetTicks = (elapsedTicks % m_loopTicks);

        if (loop != m_loop) {
            m_cursor = -1;
            m_loop = loop;
        }

        var index = m_cursor;

        while (((index + 1) < m_sampleTicks.Length) && (m_sampleTicks[(index + 1)] <= offsetTicks)) {
            index++;
        }

        if (index == m_cursor) {
            return false;
        }

        m_cursor = index;

        var sample = m_samples[index];
        var captureTimestamp = ((m_originTimestamp + (loop * m_loopTicks)) + m_sampleTicks[index]);
        var channels = default(ProbeChannelValues);

        for (var channel = 0; (channel < m_channelCount); channel++) {
            channels[channel] = FixedQ4816.FromDouble(value: (sample.C?[channel] ?? 0.0));
        }

        m_ring.Publish(reading: new ProbeReading(
            sequence: m_sequence++,
            captureTimestamp: captureTimestamp,
            completionTimestamp: nowTimestamp,
            confidence: FixedQ4816.FromDouble(value: sample.K),
            channelCount: m_channelCount,
            channels: channels
        ));

        return true;
    }
}

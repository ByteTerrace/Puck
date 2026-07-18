using Puck.Abstractions.Recording;

namespace Puck.Recording.Audio;

/// <summary>
/// One live capture source bound into the audio lane: the PCM source, its linear gain, and whether it is an
/// isolated (own-track) row or part of the mixed-down default track.
/// </summary>
/// <param name="Source">The PCM capture source.</param>
/// <param name="Gain">The linear gain applied before mixing.</param>
/// <param name="Isolated"><see langword="true"/> for an own-track archival row; otherwise mixed to one stereo track.</param>
public sealed record AudioLaneSource(IAudioCaptureSource Source, float Gain, bool Isolated);

/// <summary>
/// The recording graph's audio lane: it drains one or more <see cref="IAudioCaptureSource"/>s on the session's
/// audio thread, resamples each to 48 kHz stereo, mixes the default rows into one stereo Opus track (float sum
/// with a <see cref="MathF.Tanh(float)"/> soft-clip guard, because a single-track upload is what a service such as
/// YouTube reads) and encodes each isolated row to its own track, then hands packets to an
/// <see cref="IAudioPacketSink"/>. Mixing aligns sources through per-source jitter rings, consuming the common
/// available sample count each pump so the tracks stay phase-locked; a lagging source simply waits.
/// </summary>
public sealed class OpusAudioLane : IDisposable {
    private const int SeekPreRollNanoseconds = 80_000_000;

    private sealed class SourceState {
        public required IAudioCaptureSource Source { get; init; }
        public required float Gain { get; init; }
        public required LinearResampler Resampler { get; init; }
        public required FloatRing Ring { get; init; }
        public required OpusStreamEncoder Encoder { get; init; }
        public required bool IsMix { get; init; }
        public bool HasEpoch { get; set; }
        public long EpochNanoseconds { get; set; }
    }

    private float[] m_dequeueScratch = new float[9600];
    private bool m_disposed;
    private readonly List<OpusStreamEncoder> m_encoders = [];
    private readonly List<SourceState> m_isolated = [];
    private float[] m_mixAccumulator = new float[9600];
    private long m_mixEpochNanoseconds;
    private bool m_mixEpochSet;
    private readonly List<SourceState> m_mix = [];
    private readonly OpusStreamEncoder? m_mixEncoder;
    private readonly float[] m_readScratch = new float[16384];

    /// <summary>Initializes a new instance of the <see cref="OpusAudioLane"/> class.</summary>
    /// <param name="bitrateBitsPerSecond">The per-track target bitrate.</param>
    /// <param name="sources">The bound capture sources.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sources"/> is <see langword="null"/>.</exception>
    public OpusAudioLane(int bitrateBitsPerSecond, IReadOnlyList<AudioLaneSource> sources) {
        ArgumentNullException.ThrowIfNull(argument: sources);

        var mixRows = 0;

        foreach (var source in sources) {
            if (!source.Isolated) {
                mixRows++;
            }
        }

        if (mixRows > 0) {
            m_mixEncoder = new OpusStreamEncoder(bitrateBitsPerSecond: bitrateBitsPerSecond);

            m_encoders.Add(item: m_mixEncoder);
        }

        foreach (var binding in sources) {
            var encoder = (binding.Isolated
                ? new OpusStreamEncoder(bitrateBitsPerSecond: bitrateBitsPerSecond)
                : m_mixEncoder!);
            var state = new SourceState {
                Encoder = encoder,
                Gain = binding.Gain,
                IsMix = !binding.Isolated,
                Resampler = new LinearResampler(inputSampleRate: binding.Source.SampleRate, inputChannels: binding.Source.Channels),
                Ring = new FloatRing(capacity: 19200),
                Source = binding.Source,
            };

            if (binding.Isolated) {
                m_encoders.Add(item: encoder);
                m_isolated.Add(item: state);
            } else {
                m_mix.Add(item: state);
            }
        }
    }

    /// <summary>Gets the Opus encoders (the mix track first, then each isolated track) so the session can register
    /// each as a Matroska track — reading its <see cref="OpusStreamEncoder.CodecPrivate"/> and codec delay — and
    /// assign its <see cref="OpusStreamEncoder.TrackNumber"/>.</summary>
    public IReadOnlyList<OpusStreamEncoder> Encoders =>
        m_encoders;

    /// <summary>Gets the seek pre-roll to declare for an Opus track, in nanoseconds.</summary>
    public static long SeekPreRoll =>
        SeekPreRollNanoseconds;

    /// <summary>Gets the total samples dropped to ring overflow across every bound source.</summary>
    public long DroppedSampleCount {
        get {
            var total = 0L;

            foreach (var state in m_mix) {
                total += state.Source.DroppedSampleCount;
            }

            foreach (var state in m_isolated) {
                total += state.Source.DroppedSampleCount;
            }

            return total;
        }
    }

    /// <summary>Starts device capture on every bound source.</summary>
    public void Start() {
        foreach (var state in m_mix) {
            state.Source.Start();
        }

        foreach (var state in m_isolated) {
            state.Source.Start();
        }
    }

    /// <summary>Stops device capture on every bound source (buffered samples remain drainable).</summary>
    public void Stop() {
        foreach (var state in m_mix) {
            state.Source.Stop();
        }

        foreach (var state in m_isolated) {
            state.Source.Stop();
        }
    }

    /// <summary>Drains every source, resamples, mixes, and emits any completed Opus frames. Call on the audio thread.</summary>
    /// <param name="sink">The packet sink.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public void Pump(IAudioPacketSink sink) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        DrainSources(states: m_mix);
        DrainSources(states: m_isolated);
        PumpMix(sink: sink);

        foreach (var state in m_isolated) {
            PumpIsolated(state: state, sink: sink);
        }
    }

    /// <summary>Encodes any buffered tail samples at end of session.</summary>
    /// <param name="sink">The packet sink.</param>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public void Flush(IAudioPacketSink sink) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        Pump(sink: sink);

        foreach (var encoder in m_encoders) {
            encoder.Flush(sink: sink);
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        foreach (var state in m_mix) {
            state.Source.Dispose();
        }

        foreach (var state in m_isolated) {
            state.Source.Dispose();
        }

        foreach (var encoder in m_encoders) {
            encoder.Dispose();
        }

        m_disposed = true;
    }

    private void DrainSources(List<SourceState> states) {
        foreach (var state in states) {
            var read = state.Source.Read(interleaved: m_readScratch, firstSampleTimestampNanoseconds: out var timestamp);

            if (read <= 0) {
                continue;
            }

            if (!state.HasEpoch) {
                state.HasEpoch = true;
                state.EpochNanoseconds = timestamp;
            }

            state.Resampler.Resample(input: m_readScratch.AsSpan(start: 0, length: read), output: state.Ring);
        }
    }

    private void PumpMix(IAudioPacketSink sink) {
        if ((m_mixEncoder is null) || (m_mix.Count == 0)) {
            return;
        }

        var available = int.MaxValue;

        foreach (var state in m_mix) {
            available = Math.Min(val1: available, val2: state.Ring.Count);
        }

        available -= (available % 2);

        if (available <= 0) {
            return;
        }

        EnsureScratch(length: available);
        m_mixAccumulator.AsSpan(start: 0, length: available).Clear();

        foreach (var state in m_mix) {
            _ = state.Ring.Dequeue(destination: m_dequeueScratch.AsSpan(start: 0, length: available));

            for (var index = 0; (index < available); index++) {
                m_mixAccumulator[index] += (state.Gain * m_dequeueScratch[index]);
            }

            if (!m_mixEpochSet && state.HasEpoch) {
                m_mixEpochSet = true;
                m_mixEpochNanoseconds = state.EpochNanoseconds;
            }
        }

        for (var index = 0; (index < available); index++) {
            m_mixAccumulator[index] = MathF.Tanh(x: m_mixAccumulator[index]);
        }

        m_mixEncoder.Append(
            firstSampleTimestampNanoseconds: m_mixEpochNanoseconds,
            interleavedStereo: m_mixAccumulator.AsSpan(start: 0, length: available),
            sink: sink
        );
    }

    private void PumpIsolated(SourceState state, IAudioPacketSink sink) {
        var available = state.Ring.Count;

        available -= (available % 2);

        if (available <= 0) {
            return;
        }

        EnsureScratch(length: available);
        _ = state.Ring.Dequeue(destination: m_dequeueScratch.AsSpan(start: 0, length: available));
        state.Encoder.Append(
            firstSampleTimestampNanoseconds: state.EpochNanoseconds,
            interleavedStereo: m_dequeueScratch.AsSpan(start: 0, length: available),
            sink: sink
        );
    }

    private void EnsureScratch(int length) {
        if (m_dequeueScratch.Length < length) {
            m_dequeueScratch = new float[length];
        }

        if (m_mixAccumulator.Length < length) {
            m_mixAccumulator = new float[length];
        }
    }
}

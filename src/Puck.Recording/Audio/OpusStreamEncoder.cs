using Concentus;
using Concentus.Enums;

namespace Puck.Recording.Audio;

/// <summary>
/// One Opus track's encoder: it accumulates interleaved 48 kHz stereo float samples, encodes them in fixed
/// 20 ms (960-sample) frames, and hands each packet to an <see cref="IAudioPacketSink"/> with a sample-count
/// presentation timestamp (drift-free, derived from the first sample's session time). Steady state is
/// allocation-free — the frame accumulator and packet buffer are rented once.
/// </summary>
/// <remarks>The managed Concentus encoder is forced (no native fallback) so a given float input yields the same
/// Opus bytes on the same build — the container-plus-codec output is reproducible.</remarks>
public sealed class OpusStreamEncoder : IDisposable {
    private const int Channels = 2;
    private const int FrameSamplesPerChannel = 960;
    private const int FrameSamples = (FrameSamplesPerChannel * Channels);
    private const int MaxPacketBytes = 8192;
    private const long NanosecondsPerSecond = 1_000_000_000L;
    private const int OutputSampleRate = 48000;

    static OpusStreamEncoder() {
        // Deterministic managed path: never load the native libopus, so the Opus bytes are reproducible per build.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    private bool m_disposed;
    private readonly IOpusEncoder m_encoder;
    private long m_epochNanoseconds;
    private readonly float[] m_frame = new float[FrameSamples];
    private bool m_hasEpoch;
    private int m_pending;
    private readonly byte[] m_packet = new byte[MaxPacketBytes];
    private long m_samplesEncoded;

    /// <summary>Initializes a new instance of the <see cref="OpusStreamEncoder"/> class.</summary>
    /// <param name="bitrateBitsPerSecond">The target bitrate.</param>
    public OpusStreamEncoder(int bitrateBitsPerSecond) {
        m_encoder = OpusCodecFactory.CreateEncoder(
            sampleRate: OutputSampleRate,
            numChannels: Channels,
            application: OpusApplication.OPUS_APPLICATION_AUDIO
        );
        m_encoder.Bitrate = bitrateBitsPerSecond;
        m_encoder.Complexity = 10;
        m_encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;
        m_encoder.UseVBR = true;

        var preSkip = m_encoder.Lookahead;

        CodecPrivate = OpusHead.Build(channelCount: Channels, preSkipSamples: preSkip, inputSampleRate: OutputSampleRate);
        CodecDelayNanoseconds = ((preSkip * NanosecondsPerSecond) / OutputSampleRate);
    }

    /// <summary>Gets or sets the Matroska track number packets are stamped with (assigned when the track registers).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Gets the <c>OpusHead</c> <c>CodecPrivate</c> payload for this track.</summary>
    public byte[] CodecPrivate { get; }

    /// <summary>Gets the codec delay (Opus pre-skip) in nanoseconds.</summary>
    public long CodecDelayNanoseconds { get; }

    /// <summary>Appends interleaved stereo samples, encoding and emitting every completed 20 ms frame.</summary>
    /// <param name="interleavedStereo">The interleaved 48 kHz stereo samples.</param>
    /// <param name="firstSampleTimestampNanoseconds">The session time of the first sample (sets the track epoch once).</param>
    /// <param name="sink">The packet sink.</param>
    public void Append(ReadOnlySpan<float> interleavedStereo, long firstSampleTimestampNanoseconds, IAudioPacketSink sink) {
        if (!m_hasEpoch) {
            m_epochNanoseconds = firstSampleTimestampNanoseconds;
            m_hasEpoch = true;
        }

        var consumed = 0;

        while (consumed < interleavedStereo.Length) {
            var take = Math.Min(val1: (FrameSamples - m_pending), val2: (interleavedStereo.Length - consumed));

            interleavedStereo.Slice(start: consumed, length: take).CopyTo(destination: m_frame.AsSpan(start: m_pending));

            m_pending += take;
            consumed += take;

            if (m_pending == FrameSamples) {
                EncodeFrame(sink: sink);
                m_pending = 0;
            }
        }
    }

    /// <summary>Encodes a final partial frame (zero-padded) so trailing samples are not lost at stop.</summary>
    /// <param name="sink">The packet sink.</param>
    public void Flush(IAudioPacketSink sink) {
        if (m_pending == 0) {
            return;
        }

        m_frame.AsSpan(start: m_pending).Clear();
        EncodeFrame(sink: sink);
        m_pending = 0;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_encoder.Dispose();
        m_disposed = true;
    }

    private void EncodeFrame(IAudioPacketSink sink) {
        var bytes = m_encoder.Encode(
            in_pcm: m_frame,
            frame_size: FrameSamplesPerChannel,
            out_data: m_packet,
            max_data_bytes: m_packet.Length
        );
        var timestamp = (m_epochNanoseconds + ((m_samplesEncoded * NanosecondsPerSecond) / OutputSampleRate));

        sink.WriteAudioPacket(trackNumber: TrackNumber, data: m_packet.AsSpan(start: 0, length: bytes), timestampNanoseconds: timestamp);
        m_samplesEncoded += FrameSamplesPerChannel;
    }
}

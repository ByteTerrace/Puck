using System.Buffers.Binary;
using System.Text;

namespace Puck.Recording.Audio;

/// <summary>
/// Builds the <c>OpusHead</c> identification header used as the Matroska <c>A_OPUS</c> <c>CodecPrivate</c>
/// payload, per the Ogg-Opus mapping (RFC 7845 §5.1) that Matroska reuses. The header is 19 bytes for channel
/// mapping family 0 (mono or stereo), with multi-byte fields little-endian.
/// </summary>
internal static class OpusHead {
    /// <summary>Builds the <c>OpusHead</c> payload for a family-0 stream.</summary>
    /// <param name="channelCount">The channel count (1 or 2).</param>
    /// <param name="preSkipSamples">The encoder look-ahead in 48 kHz samples.</param>
    /// <param name="inputSampleRate">The original input sample rate in hertz (informational).</param>
    /// <returns>The 19-byte identification header.</returns>
    public static byte[] Build(int channelCount, int preSkipSamples, int inputSampleRate) {
        var payload = new byte[19];

        Encoding.ASCII.GetBytes(chars: "OpusHead", bytes: payload.AsSpan(start: 0, length: 8));
        payload[8] = 1;
        payload[9] = (byte)channelCount;
        BinaryPrimitives.WriteUInt16LittleEndian(destination: payload.AsSpan(start: 10, length: 2), value: (ushort)preSkipSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(destination: payload.AsSpan(start: 12, length: 4), value: (uint)inputSampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(destination: payload.AsSpan(start: 16, length: 2), value: 0);
        payload[18] = 0;

        return payload;
    }
}

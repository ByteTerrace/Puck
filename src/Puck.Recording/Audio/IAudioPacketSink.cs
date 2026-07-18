namespace Puck.Recording.Audio;

/// <summary>
/// The egress the <see cref="OpusAudioLane"/> hands each encoded Opus packet to — the recording session
/// implements it to forward the packet to the muxer under its serialization lock. The payload span is valid only
/// for the duration of the call; a sink that retains it must copy (the muxer copies into its cluster buffer).
/// </summary>
public interface IAudioPacketSink {
    /// <summary>Consumes one encoded audio packet.</summary>
    /// <param name="trackNumber">The Matroska track number the packet belongs to.</param>
    /// <param name="data">The encoded Opus packet payload, valid only for this call.</param>
    /// <param name="timestampNanoseconds">The presentation timestamp in nanoseconds on the session clock.</param>
    void WriteAudioPacket(int trackNumber, ReadOnlySpan<byte> data, long timestampNanoseconds);
}

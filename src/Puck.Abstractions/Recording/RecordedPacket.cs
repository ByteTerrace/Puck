namespace Puck.Abstractions.Recording;

/// <summary>
/// One encoded media packet leaving an encoder for the muxer: the compressed payload, its presentation
/// timestamp, and whether it can start a decode (a video keyframe; every audio packet). The payload memory is
/// owned by the producing encoder and is valid only until its next encode/drain call — a consumer that keeps
/// it must copy.
/// </summary>
/// <param name="Data">The compressed payload (an AV1 temporal unit, an AVC access unit, an Opus packet).</param>
/// <param name="TimestampNanoseconds">The presentation timestamp in nanoseconds on the session clock.</param>
/// <param name="IsKeyframe">Whether a decoder can start at this packet.</param>
public readonly record struct RecordedPacket(
    ReadOnlyMemory<byte> Data,
    long TimestampNanoseconds,
    bool IsKeyframe
);

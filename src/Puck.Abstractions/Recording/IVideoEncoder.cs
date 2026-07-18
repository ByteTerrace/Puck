using Puck.Abstractions.Presentation;

namespace Puck.Abstractions.Recording;

/// <summary>
/// A configured video encoder producing timestamped compressed packets from CPU-pixel frames — the platform
/// half of the recording graph's video lane. Implementations own format conversion (the capture tap delivers
/// RGBA/BGRA; the codec consumes what it consumes) and their device/session resources.
/// </summary>
/// <remarks>Single-threaded: the recording session calls <see cref="EncodeFrame"/> and <see cref="Drain"/>
/// from its one encode thread. Returned packet lists (and the packets' payload memory) are valid only until
/// the next call.</remarks>
public interface IVideoEncoder : IDisposable {
    /// <summary>The Matroska codec id of the produced stream (<c>V_AV1</c>, <c>V_MPEG4/ISO/AVC</c>).</summary>
    string CodecId { get; }

    /// <summary>The Matroska <c>CodecPrivate</c> payload (<c>av1C</c> / <c>avcC</c>), or empty when the codec
    /// carries its configuration in-band. Guaranteed populated once <see cref="EncodeFrame"/> has returned at
    /// least one packet.</summary>
    ReadOnlyMemory<byte> CodecPrivate { get; }

    /// <summary>Encodes one frame; returns zero or more packets (encoders pipeline internally).</summary>
    /// <param name="pixels">The tightly packed CPU pixels.</param>
    /// <param name="format">The pixel format of <paramref name="pixels"/>.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <param name="timestampNanoseconds">The presentation timestamp on the session clock.</param>
    /// <returns>The packets ready after this frame, oldest first.</returns>
    IReadOnlyList<RecordedPacket> EncodeFrame(ReadOnlySpan<byte> pixels, SurfaceFormat format, int width, int height, long timestampNanoseconds);

    /// <summary>Flushes the pipeline at end of session; returns every remaining packet, oldest first.</summary>
    IReadOnlyList<RecordedPacket> Drain();
}

/// <summary>
/// Creates the best available <see cref="IVideoEncoder"/> for an ordered codec preference ladder — the seam
/// that keeps codec policy DATA (the recording document's ladder) while hardware reality stays a platform
/// concern. A factory declines (returns <see langword="null"/>) rather than throwing when no ladder entry is
/// encodable on this machine.
/// </summary>
public interface IVideoEncoderFactory {
    /// <summary>Creates an encoder for the first ladder entry this platform can encode, or <see langword="null"/>.</summary>
    /// <param name="codecLadder">Codec preferences in order (tokens: <c>av1</c>, <c>h264</c>).</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <param name="frameRate">The nominal frames per second.</param>
    /// <param name="bitrateKilobitsPerSecond">The target bitrate.</param>
    /// <param name="reason">Why creation declined (no hardware encoder, unsupported extent), or empty.</param>
    IVideoEncoder? Create(IReadOnlyList<string> codecLadder, int width, int height, int frameRate, int bitrateKilobitsPerSecond, out string reason);
}

using System.Numerics;
using Puck.Abstractions.Sources;

namespace Puck.Abstractions.Machines;

/// <summary>A presentation output that any number of screens may show. A screen reads it as an uploaded source: the
/// source writes one of the output's complete frames into its region and converts it once however many screens show
/// it. An output that completes frames on its own clock (<see cref="FrameRateHz"/> above zero) keeps its recent frames,
/// each stamped with the host clock position it completed at, so a display at any rate shows the frame due when it
/// presents (<see cref="WriteFrame(Span{byte}, ulong)"/>); any other output has one latest frame, written once per
/// completed tick. Writing never advances simulation, and which frame a display shows never changes how the machine
/// advances.</summary>
public interface IMachineVideoOutput {
    /// <summary>Gets the frame's average emitted color, normalized to 0..1.</summary>
    Vector3 EmittedLight { get; }
    /// <summary>Gets the pixel format the output's frames are written in: <see cref="ImagePixelFormat.R8G8B8A8Unorm"/>,
    /// or <see cref="ImagePixelFormat.Indexed8"/> with its palette; fixed for the output's lifetime.</summary>
    ImagePixelFormat Format { get; }
    /// <summary>Gets the image's height, in pixels; positive and fixed for the output's lifetime.</summary>
    int Height { get; }
    /// <summary>Gets the image's width, in pixels; positive and fixed for the output's lifetime.</summary>
    int Width { get; }
    /// <summary>Gets the most frames a second the output completes on its own clock, its native rate rounded up, or
    /// zero (the default) for an output whose image follows the simulation tick.</summary>
    uint FrameRateHz => 0U;

    /// <summary>Returns the sequence number of the frame due at a host clock position, without writing it: the newest
    /// complete frame stamped at or before <paramref name="dueTicks"/>, or the oldest the output still holds when every
    /// one is later. An output that keeps one latest frame answers zero (the default), and a caller writes to find
    /// out.</summary>
    /// <param name="dueTicks">The host clock position a display presents, in engine ticks on the clock the machine's
    /// segments were stamped with.</param>
    /// <returns>The due frame's sequence number, or zero when the output has no frame or does not track it.</returns>
    long FrameDue(ulong dueTicks) => 0L;
    /// <summary>Writes the frame due at a host clock position (<see cref="FrameDue"/>) into an uploaded source's region,
    /// as <see cref="WriteFrame(Span{byte})"/> writes the newest. An output that keeps one latest frame writes that one,
    /// which is the default.</summary>
    /// <param name="region">The region; at least <see cref="ImageSourceUploadLayout.ByteCount"/> bytes of the output's
    /// header.</param>
    /// <param name="dueTicks">The host clock position a display presents, in engine ticks.</param>
    /// <returns>The written frame's sequence number; zero when the output has no frame and wrote nothing.</returns>
    /// <exception cref="ArgumentException"><paramref name="region"/> is shorter than the layout.</exception>
    long WriteFrame(Span<byte> region, ulong dueTicks) => WriteFrame(region: region);

    /// <summary>Writes the most recent complete frame into an uploaded source's region, laid out by
    /// <see cref="ImageSourceUploadLayout.HeaderOf"/> of <see cref="Format"/>, <see cref="ImageColorEncoding.Srgb"/>,
    /// <see cref="Width"/> and <see cref="Height"/>: every plane, never the header. One frame is written whole, however
    /// the machine runs meanwhile.</summary>
    /// <param name="region">The region; at least <see cref="ImageSourceUploadLayout.ByteCount"/> bytes of that
    /// header.</param>
    /// <returns>The written frame's sequence number, which rises with each complete frame, so an unchanged frame
    /// returns the number it returned before; zero when the output has no frame and wrote nothing.</returns>
    /// <exception cref="ArgumentException"><paramref name="region"/> is shorter than the layout.</exception>
    long WriteFrame(Span<byte> region);
}
/// <summary>Optional named video outputs. Names and output identities remain stable for the runtime's lifetime.</summary>
public interface IMachineVideoOutputs {
    /// <summary>Gets the available outputs by provider-owned name.</summary>
    IReadOnlyDictionary<string, IMachineVideoOutput> VideoOutputs { get; }
}

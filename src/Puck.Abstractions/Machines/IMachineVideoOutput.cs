using System.Numerics;
using Puck.Abstractions.Sources;

namespace Puck.Abstractions.Machines;

/// <summary>A presentation output that any number of screens may show. A screen reads it as an uploaded source: the
/// source writes the output's latest complete frame into its region (<see cref="WriteFrame"/>) once per completed tick
/// and converts it once however many screens show it. Writing never advances simulation.</summary>
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

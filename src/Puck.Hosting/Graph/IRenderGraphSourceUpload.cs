using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;

namespace Puck.Hosting;

/// <summary>An uploaded source instance's producer as a render-graph runtime hosts it: the producer writes its pixels
/// into a region laid out by <see cref="ImageSourceUploadLayout"/>, and the runtime renders the instance through a graph
/// of one pass, the conversion <see cref="ImageSourceConversion.PassOf"/> names for the descriptor's format and color,
/// which reads the region through its host buffer port and writes the image every consumer reads. The runtime owns the
/// region, writes its header once, and asks for the planes each time the instance's cadence renders it. Every member
/// runs on the thread that produces frames.</summary>
public interface IRenderGraphSourceUpload : IDisposable {
    /// <summary>Gets what the producer declares for the source now: its format, color encoding, extent and cadence, which
    /// fix the region's layout and the conversion pass, or <see langword="null"/> while the producer has no image to
    /// declare (<see cref="Fault"/>). A declaration that changes (a machine replaced by one with another extent) makes the
    /// runtime rebuild the source's conversion graph and region from the new one before the next frame renders it, so
    /// reading it is cheap and allocates nothing while it holds.</summary>
    ImageSourceDescriptor? Descriptor { get; }
    /// <summary>Gets why the source has no image, naming its producer, or <see langword="null"/> while it has one.</summary>
    string? Fault { get; }

    /// <summary>Writes the source's image for one render into its region's planes, after the header the runtime wrote
    /// (<see cref="ImageSourceUploadLayout.HeaderOf"/> of <see cref="Descriptor"/>). The region owes only the words that
    /// differ from what it held, so an image rewritten whole costs only what changed.</summary>
    /// <param name="tick">The completed simulation tick the frame presents, which a deterministic source's image is a
    /// function of.</param>
    /// <param name="region">The region, <see cref="ImageSourceUploadLayout.ByteCount"/> bytes of the descriptor's
    /// header.</param>
    /// <returns><see langword="true"/> when the region holds an image to convert; <see langword="false"/> while the
    /// producer has none, and the runtime withdraws the render so the source is asked again.</returns>
    bool TryWrite(long tick, GpuRegion region);
}

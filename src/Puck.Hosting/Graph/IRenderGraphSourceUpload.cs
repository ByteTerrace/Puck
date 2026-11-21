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
    /// <summary>Gets whether the source has no image because its producer is not running, which is a defined outcome
    /// rather than a fault: the instance is current with no output, and every reader draws its own stand-in, so the
    /// source holds no frame incomplete and no reader inherits a refusal from it. <see cref="Fault"/> still names the
    /// state for a read-back. The default is <see langword="false"/>, a source with no image is faulted.</summary>
    bool IsAbsent => false;
    /// <summary>Gets whether the producer has an image the region does not hold yet, when it knows that itself: a
    /// producer that keeps its own frames (a machine's video) answers <see langword="true"/> on a frame that presents a
    /// new one and <see langword="false"/> on a frame that presents the one already written, and the source is due
    /// exactly then, whatever its cadence counts and whatever rate the display runs at. <see langword="null"/>, the
    /// default, leaves the decision to the cadence. Read once per frame, before it schedules.</summary>
    bool? HasNewImage => null;

    /// <summary>Writes the source's image for one render into its region's planes, after the header the runtime wrote
    /// (<see cref="ImageSourceUploadLayout.HeaderOf"/> of <see cref="Descriptor"/>). The region owes only the words that
    /// differ from what it held, so an image rewritten whole costs only what changed.</summary>
    /// <param name="tick">The completed simulation tick the frame presents, which a deterministic source's image is a
    /// function of.</param>
    /// <param name="region">The region, <see cref="ImageSourceUploadLayout.ByteCount"/> bytes of the descriptor's
    /// header.</param>
    /// <returns>Whether the region holds the tick's image to convert: <see cref="FrameRender.Rendered"/>; waiting only
    /// while writing the same tick again can deliver it (a region the runtime rebuilds for a changed shape); refused,
    /// naming why, when it cannot: its producer faulted, or its image is a function of the simulation and it has none
    /// for this tick, which only a later tick can change. A render that writes nothing is withdrawn, so the source is
    /// asked again.</returns>
    FrameRender Write(long tick, GpuRegion region);
}

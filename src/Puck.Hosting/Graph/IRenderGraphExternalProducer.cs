using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;

namespace Puck.Hosting;

/// <summary>One acquisition of an external producer's latest completed output: the image, the layout the producer
/// leaves it in between its submissions, and the lease that keeps it alive until the submission that samples it has
/// finished.</summary>
/// <param name="Image">The same-device image the producer last completed, or an empty surface for a source whose image
/// arrives from another thread or device as an image view alone, which the lease carries and only an external producer
/// samples; a graph instance reading it draws a stand-in.</param>
/// <param name="Layout">The layout the image is in whenever the producer is not recording into it; a consumer hands it
/// back in this layout.</param>
/// <param name="Lease">The acquisition, retired exactly once by the consumer that sampled it, after that submission's
/// fence, on device loss or at disposal.</param>
public readonly record struct RenderGraphExternalOutput(Surface Image, GpuImageLayout Layout, GpuImageLease Lease);
/// <summary>Renders a render-graph instance through its own submissions rather than a graph of its own: the SDF engine
/// behind <c>sdf.world</c>, which submits through its own frame ring. The graph runtime produces it before its
/// consumers when the schedule renders it, at the scheduled extent, handing it the latest completed image of every
/// instance its own instance reads, and each consumer acquires its latest completed output, on frames the schedule skips
/// or defers as well. A capture the runtime forwards to it is served by the next frame it produces, from the image that
/// frame completes. Every member runs on the thread that produces frames.</summary>
public interface IRenderGraphExternalProducer : ICaptureRequestTarget, IDisposable {
    /// <summary>Gets the format of every image the producer hands out.</summary>
    GpuPixelFormat Format { get; }
    /// <summary>Gets why the producer has no completed output to hand out or capture, phrased as the refusal of a
    /// capture that waited on it reads, or <see langword="null"/> once it has one. A caller polls it only to
    /// report.</summary>
    string? NotReadyReason { get; }
    /// <summary>Gets the producer's counted GPU work, per pass of its submissions.</summary>
    IGpuWorkSource Work { get; }

    /// <summary>Releases every device object after the device was lost, without waiting for any submission; the next
    /// produced frame rebuilds on the recreated device. Acquisitions released afterwards release nothing.</summary>
    void OnDeviceLost();
    /// <summary>Renders one frame at an extent, rebuilding its targets first when the extent differs from the last
    /// frame's.</summary>
    /// <param name="context">The host's frame context.</param>
    /// <param name="width">The extent width, in pixels, at least one.</param>
    /// <param name="height">The extent height, in pixels, at least one.</param>
    /// <param name="reads">The latest completed image of each instance the producer's instance reads, whose leases the
    /// producer takes for the images its submission samples (<see cref="RenderGraphExternalReads.Take"/>), or
    /// <see langword="null"/> when it reads none.</param>
    /// <returns><see langword="true"/> when a frame was submitted; <see langword="false"/> while the producer cannot
    /// render yet, such as while its pipelines build.</returns>
    bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null);
    /// <summary>Acquires the latest completed output. Each acquisition is retired once, through its lease.</summary>
    /// <param name="output">The output, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="false"/> when the producer has completed no output.</returns>
    bool TryAcquireOutput(out RenderGraphExternalOutput output);
}
/// <summary>The external producer of a source instance (<see cref="RenderGraphInstance.IsSource"/>): an image another
/// thread, device or process writes, such as a camera, a desktop capture or a machine's framebuffer. Its descriptor is
/// what the runtime declares to the scheduler each frame (<see cref="RenderGraphSourceState"/>), so the instance renders
/// at its producer's cadence and negotiated extent, and <see cref="IRenderGraphExternalProducer.Produce"/> publishes the
/// image the cadence owes.</summary>
public interface IRenderGraphSourceProducer : IRenderGraphExternalProducer {
    /// <summary>Gets what the producer declares for the source right now: its extent, zero on either axis while none is
    /// negotiated, and its cadence; or <see langword="null"/> while it has no image to declare.</summary>
    ImageSourceDescriptor? Descriptor { get; }
}

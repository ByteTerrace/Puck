using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;

namespace Puck.Hosting;

/// <summary>The image, resting layout and lease unlocked from a <see cref="RenderGraphExternalOutput"/> at a
/// <see cref="DeliveryLatch"/>.</summary>
/// <param name="Image">The same-device image the producer last completed, or an empty surface for a source whose image
/// arrives from another thread or device as an image view alone, which the lease carries and only an external producer
/// samples; a graph instance reading it draws a stand-in.</param>
/// <param name="Layout">The layout the image is in whenever the producer is not recording into it; a consumer hands it
/// back in this layout.</param>
/// <param name="Lease">The acquisition, retired exactly once by the consumer that sampled it, after that submission's
/// fence, on device loss or at disposal.</param>
public readonly record struct RenderGraphAcquiredImage(Surface Image, GpuImageLayout Layout, GpuImageLease Lease);

/// <summary>One acquisition of an external producer's latest completed output, pairing the image and its lease with its
/// <see cref="DeliveryLabel"/> so the handle and surface can be obtained only through <see cref="TryRead"/>.</summary>
public readonly struct RenderGraphExternalOutput {
    private readonly DeliveryContent<RenderGraphAcquiredImage> m_content;
    private readonly GpuImageLease m_retire;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphExternalOutput"/> struct.</summary>
    /// <param name="image">The same-device image the producer last completed, or an empty surface for a view-only
    /// source.</param>
    /// <param name="layout">The layout the image rests in between its producer's submissions.</param>
    /// <param name="lease">The acquisition that keeps the image alive until retired.</param>
    /// <param name="label">The output's provenance and external-content classification; an unclassified label is
    /// refused at every read.</param>
    public RenderGraphExternalOutput(Surface image, GpuImageLayout layout, GpuImageLease lease, DeliveryLabel label) {
        m_content = new DeliveryContent<RenderGraphAcquiredImage>(
            label: label,
            value: new RenderGraphAcquiredImage(
                Image: image,
                Layout: layout,
                Lease: lease
            )
        );
        m_retire = lease;
    }

    /// <summary>Gets the output's provenance and external-content classification.</summary>
    public DeliveryLabel Label => m_content.Label;
    /// <summary>Gets whether the output carries a classified label.</summary>
    public bool IsClassified => m_content.IsClassified;

    /// <summary>Reads the acquired image and lease when <see cref="Label"/> allows <paramref name="latch"/>.</summary>
    /// <param name="latch">The frame's latch.</param>
    /// <param name="acquired">The image, layout and lease when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when <see cref="Label"/> is classified and every span in it is current at
    /// <paramref name="latch"/>.</returns>
    public bool TryRead(in DeliveryLatch latch, out RenderGraphAcquiredImage acquired) =>
        m_content.TryRead(latch: in latch, value: out acquired);
    /// <summary>Retires the underlying acquisition when a caller does not hand the lease to a submission.</summary>
    public void Retire() => m_retire.Retire();
}

/// <summary>Renders a render-graph instance through its own submissions rather than a graph of its own, such as an image
/// producer whose image another device writes. The graph runtime produces it before its
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
    /// producer takes for the images its submission samples (<see cref="RenderGraphExternalReads.TryTake"/>), or
    /// <see langword="null"/> when it reads none.</param>
    /// <returns>Whether the frame was submitted: <see cref="FrameRender.Rendered"/>; waiting, with what for, only while
    /// asking again for the same frame can deliver it (its pipelines building, a first frame arriving on another
    /// thread); refused, naming why, when it cannot until something it was built from changes (a feed that ended or
    /// failed to open). An offscreen host holds its tick for a wait and steps on past a refusal.</returns>
    FrameRender Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null);
    /// <summary>Acquires the latest completed output. Each acquisition is retired once, through its lease or
    /// <see cref="RenderGraphExternalOutput.Retire"/>.</summary>
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
    /// <summary>Gets the source's availability without publishing, pulling or submitting work. The runtime reads it
    /// when the scheduler refuses the source or gives it no extent.</summary>
    FrameRender Answer { get; }
    /// <summary>Gets what the producer declares for the source right now: its extent, zero on either axis while none is
    /// negotiated, and its cadence; or <see langword="null"/> while it has no image to declare.</summary>
    ImageSourceDescriptor? Descriptor { get; }
}

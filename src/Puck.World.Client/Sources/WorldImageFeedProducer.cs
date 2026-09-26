using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>
/// The render-graph producer of a source instance whose producer is not uploaded (a camera, a desktop capture, or any
/// imported producer a host registers): it owns the feed the instance's factory opened, publishes it when the runtime
/// renders the instance, and hands its image out through the capture gate (<see cref="WorldCaptureGate.Resolve"/>),
/// which makes <see cref="TryAcquireOutput"/> the one place a source's image is acquired. A filled source hands out its
/// fill and never acquires the feed. The image arrives from another thread or device as an image view alone, so the
/// output's <see cref="RenderGraphExternalOutput.Image"/> is empty and its lease carries the view: an external producer
/// samples it, and a graph instance reading it draws a stand-in. Every member runs on the thread that produces frames.
/// </summary>
public sealed class WorldImageFeedProducer : IRenderGraphSourceProducer, IGpuWorkSource {
    private readonly Func<uint, GpuImageLease> m_fill;
    private readonly WorldCaptureGate m_gate;
    // Why the opened feed hands out no image, when it is no import feed.
    private readonly string? m_notImported;

    /// <summary>Initializes a new instance of the <see cref="WorldImageFeedProducer"/> class, which owns the opened
    /// feed.</summary>
    /// <param name="opening">What the source instance's factory opened: its feed, or why it has none.</param>
    /// <param name="gate">The gate that keeps external content out of captures.</param>
    /// <param name="fill">Returns the image of a packed RGBA8 capture fill.</param>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> or <paramref name="fill"/> is
    /// <see langword="null"/>.</exception>
    public WorldImageFeedProducer(WorldImageSourceOpening opening, WorldCaptureGate gate, Func<uint, GpuImageLease> fill) {
        ArgumentNullException.ThrowIfNull(argument: gate);
        ArgumentNullException.ThrowIfNull(argument: fill);

        m_fill = fill;
        m_gate = gate;
        Feed = (opening.Feed as IWorldImportFeed);
        m_notImported = (((opening.Feed is not null) && (Feed is null))
            ? $"image producer '{opening.Feed.Descriptor.Producer}' opened a feed that hands out no image"
            : null);
        Opening = opening;
    }

    /// <inheritdoc/>
    public ImageSourceDescriptor? Descriptor => Feed?.Descriptor;
    /// <summary>Gets why the source shows nothing: why its feed did not open, or the feed's own fault; or
    /// <see langword="null"/> while it shows its image.</summary>
    public string? Fault => (Opening.Fault ?? (m_notImported ?? Feed?.Fault));
    /// <summary>Gets the feed the producer publishes and acquires, or <see langword="null"/> when none opened or the one
    /// that opened hands out no image.</summary>
    public IWorldImportFeed? Feed { get; }
    /// <inheritdoc/>
    public GpuPixelFormat Format => ((Descriptor?.Format == ImagePixelFormat.R8G8B8A8Unorm)
        ? GpuPixelFormat.R8G8B8A8Unorm
        : GpuPixelFormat.B8G8R8A8Unorm
    );
    /// <inheritdoc/>
    public string? NotReadyReason => (Fault ?? (((Feed?.Handle() ?? 0) == 0)
        ? $"source '{Opening.Context.Instance}' has no image yet"
        : null));
    /// <summary>Gets what the source instance's factory opened.</summary>
    public WorldImageSourceOpening Opening { get; }
    /// <inheritdoc/>
    public string? PendingCapturePath => null;
    /// <inheritdoc/>
    public IGpuWorkSource Work => this;

    /// <inheritdoc/>
    public void Dispose() => Opening.Feed?.Dispose();
    /// <inheritdoc/>
    public void OnDeviceLost() => Feed?.NotifyDeviceLost();
    /// <inheritdoc/>
    /// <remarks>Publishes the feed for the frame and reports whether it has an image. The extent is the one the feed
    /// declared, so the arguments are not read.</remarks>
    public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
        if (Feed is not { } feed) {
            return false;
        }

        feed.Publish(context: in context);

        return (feed.Handle() != 0);
    }
    /// <inheritdoc/>
    /// <remarks>A source is captured through the instance that shows it, so a capture armed on the source itself is
    /// refused.</remarks>
    public void RequestCapture(FrameCaptureRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        _ = request.TryFail(error: new NotSupportedException(message: $"Source '{Opening.Context.Instance}' is captured through the instance that shows it."));
    }
    /// <inheritdoc/>
    /// <remarks>The image is the feed's acquired frame, or its capture fill while the gate fills its content class; the
    /// output's image is empty and its lease carries the image view.</remarks>
    public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
        if (Feed is not { } feed) {
            output = default;

            return false;
        }

        var lease = m_gate.Resolve(
            feed: feed,
            fill: m_fill
        );

        if (lease.ImageViewHandle == 0) {
            lease.Retire();
            output = default;

            return false;
        }

        output = new RenderGraphExternalOutput(
            Image: default,
            Layout: GpuImageLayout.ShaderReadOnly,
            Lease: lease
        );

        return true;
    }

    // A source's submissions are its producer's own, on another thread or device, so the render device counts none.
    bool IGpuWorkSource.TryReadCompleted(GpuWorkSample sample) => false;
}

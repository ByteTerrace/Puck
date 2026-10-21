using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>
/// Decides when an external image resolves to its declared capture fill (<see cref="ImageSourceDescriptor.CaptureFill"/>)
/// in place of its pixels. While the gate fills, every source whose content class is
/// <see cref="ImageContentClass.External"/> — a desktop capture, a camera, a probe of either — resolves to its fill, so no
/// captured frame, <c>puck parity</c> station or screenshot can hold its pixels. An offscreen host, which serves captures
/// and parity, fills always; a windowed host fills while a capture is armed on the render graph's runtime. Whether or not
/// the gate fills, an external feed with no image of its own (a first frame not yet delivered, a window minimized since
/// its capture opened, a target that is gone) resolves to its fill too, so a live source that has nothing to show stands
/// its fill in rather than holding every frame that shows it. An image it resolves outside a fill is tainted
/// (<see cref="RenderGraphExternalOutput.Tainted"/>), and the runtime renders every tainted instance a capture reads again
/// on the capture frame, so an image a slower instance rendered from the source earlier never reaches the capture.
/// Deterministic and presentation content is never filled. The gate is presentation state: simulation never reads it.
/// </summary>
public sealed class WorldCaptureGate {
    private readonly bool m_alwaysFills;
    private readonly Func<bool> m_captureArmed;

    /// <summary>Initializes a new instance of the <see cref="WorldCaptureGate"/> class.</summary>
    /// <param name="alwaysFills">Whether the host fills every frame: an offscreen host that serves captures and parity.</param>
    /// <param name="captureArmed">Answers whether a capture is armed on the render graph's runtime and not yet served, read
    /// each time a source resolves.</param>
    /// <exception cref="ArgumentNullException"><paramref name="captureArmed"/> is <see langword="null"/>.</exception>
    public WorldCaptureGate(bool alwaysFills, Func<bool> captureArmed) {
        ArgumentNullException.ThrowIfNull(argument: captureArmed);

        m_alwaysFills = alwaysFills;
        m_captureArmed = captureArmed;
    }

    /// <summary>Gets whether external content resolves to its fill right now.</summary>
    public bool Filling => (m_alwaysFills || m_captureArmed());

    /// <summary>Returns whether content of a class resolves to its fill right now.</summary>
    /// <param name="content">The content class.</param>
    /// <returns><see langword="true"/> for external content while <see cref="Filling"/>.</returns>
    public bool Fills(ImageContentClass content) => ((content == ImageContentClass.External) && Filling);
    /// <summary>Returns whether a feed resolves to its fill right now: external content while <see cref="Filling"/>, or an
    /// external feed whose answer (<see cref="IWorldImportFeed.Answer"/>) is not rendered, waiting or refused. Reading it
    /// produces no work.</summary>
    /// <param name="feed">The feed.</param>
    /// <returns><see langword="true"/> when the feed's image resolves to its capture fill.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feed"/> is <see langword="null"/>.</exception>
    public bool FillsFeed(IWorldImportFeed feed) {
        ArgumentNullException.ThrowIfNull(argument: feed);

        return (feed.Descriptor.FillsCaptures && (Filling || !feed.Answer.IsRendered));
    }
    /// <summary>Resolves one feed's image for a submitted frame: its fill when <see cref="FillsFeed"/> holds, otherwise its
    /// own acquired frame. A filled feed is never acquired, so nothing it holds needs retiring.</summary>
    /// <param name="feed">The feed.</param>
    /// <param name="fill">Returns the image of a packed RGBA8 fill color.</param>
    /// <param name="tainted">Returns whether the image is external content the gate did not fill, which no capture may
    /// read.</param>
    /// <returns>The lease the frame samples.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feed"/> or <paramref name="fill"/> is
    /// <see langword="null"/>.</exception>
    public GpuImageLease Resolve(IWorldImportFeed feed, Func<uint, GpuImageLease> fill, out bool tainted) {
        ArgumentNullException.ThrowIfNull(argument: feed);
        ArgumentNullException.ThrowIfNull(argument: fill);

        if (FillsFeed(feed: feed)) {
            tainted = false;

            return fill(arg: feed.Descriptor.CaptureFill);
        }

        tainted = (feed.Descriptor.Content == ImageContentClass.External);

        return feed.AcquireFrame();
    }
}

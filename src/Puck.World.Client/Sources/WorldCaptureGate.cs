using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>
/// Keeps external content out of captures. While the gate fills, every source whose content class is
/// <see cref="ImageContentClass.External"/> — a desktop capture, a camera, a probe of either — resolves to its declared
/// capture fill instead of its pixels, so no captured frame, <c>puck parity</c> station or screenshot can hold them. An
/// offscreen host, which serves captures and parity, fills always; a windowed host fills from the frame a capture is armed
/// for until <see cref="HoldFrames"/> frames after the last one it saw armed, so an image rendered for the capture frame
/// by a view refreshed on an earlier frame has been rendered again, filled, before it is shown. Deterministic and
/// presentation content is never filled. The gate is presentation state: simulation never reads it.
/// </summary>
public sealed class WorldCaptureGate {
    /// <summary>The produced frames the gate keeps filling after the last frame it saw a capture armed.</summary>
    public const int HoldFrames = 2;

    private readonly bool m_alwaysFills;
    private readonly Func<bool> m_captureArmed;

    private int m_holding;

    /// <summary>Initializes a new instance of the <see cref="WorldCaptureGate"/> class.</summary>
    /// <param name="alwaysFills">Whether the host fills every frame: an offscreen host that serves captures and parity.</param>
    /// <param name="captureArmed">Answers whether a capture is armed anywhere in the render chain for the next frame it
    /// serves.</param>
    /// <exception cref="ArgumentNullException"><paramref name="captureArmed"/> is <see langword="null"/>.</exception>
    public WorldCaptureGate(bool alwaysFills, Func<bool> captureArmed) {
        ArgumentNullException.ThrowIfNull(argument: captureArmed);

        m_alwaysFills = alwaysFills;
        m_captureArmed = captureArmed;
    }

    /// <summary>Gets whether external content resolves to its fill right now.</summary>
    public bool Filling => (m_alwaysFills || (m_holding > 0) || m_captureArmed());

    /// <summary>Advances the gate by one produced frame, before any source of that frame is resolved.</summary>
    public void BeginFrame() {
        // The armed frame itself counts one, so the gate holds for HoldFrames frames after it.
        if (m_captureArmed()) {
            m_holding = (HoldFrames + 1);
        } else if (m_holding > 0) {
            m_holding--;
        }
    }
    /// <summary>Returns whether content of a class resolves to its fill right now.</summary>
    /// <param name="content">The content class.</param>
    /// <returns><see langword="true"/> for external content while <see cref="Filling"/>.</returns>
    public bool Fills(ImageContentClass content) => ((content == ImageContentClass.External) && Filling);
    /// <summary>Resolves one feed's image for a submitted frame: its fill when the gate fills its class, otherwise its
    /// own acquired frame. A filled feed is never acquired, so nothing it holds needs retiring.</summary>
    /// <param name="feed">The feed.</param>
    /// <param name="fill">Returns the image of a packed RGBA8 fill color.</param>
    /// <returns>The lease the frame samples.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="feed"/> or <paramref name="fill"/> is
    /// <see langword="null"/>.</exception>
    public GpuImageLease Resolve(IWorldImportFeed feed, Func<uint, GpuImageLease> fill) {
        ArgumentNullException.ThrowIfNull(argument: feed);
        ArgumentNullException.ThrowIfNull(argument: fill);

        return (Fills(content: feed.Descriptor.Content)
            ? fill(arg: feed.Descriptor.CaptureFill)
            : feed.AcquireFrame()
        );
    }
}

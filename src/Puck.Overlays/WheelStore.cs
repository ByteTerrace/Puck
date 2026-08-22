using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>One sector as the wheel draws it: its resolved display text and its resolved icon content.</summary>
/// <param name="Label">The sector's display text (the hub shows it while hovered; a piece with no icon draws it).</param>
/// <param name="Icon">The sector's resolved icon content, or <see cref="OverlayResolvedGlyph.None"/>.</param>
public readonly record struct OverlayWheelSector(
    string Label,
    OverlayResolvedGlyph Icon
);
/// <summary>One ring of a presented wheel — the pieces the writer draws for one concentric shell.</summary>
/// <param name="Label">The ring's display label (drawn in the hub while the ring is active).</param>
/// <param name="Sectors">The ring's sectors, sector 0 at twelve o'clock and the rest clockwise — the same
/// convention the host's angle-to-sector selection uses, so what is drawn under the cursor is what commits.</param>
public readonly record struct OverlayWheelRing(
    string Label,
    ReadOnlyMemory<OverlayWheelSector> Sectors
);
/// <summary>What the wheel's glow is saying this frame.</summary>
public enum OverlayWheelOutcome : byte {
    /// <summary>Nothing decided: the hovered piece (if any) glows in the accent hue.</summary>
    None,
    /// <summary>A sector's command was dispatched: that piece glows positive while the wheel fades out.</summary>
    Accepted,
    /// <summary>The gesture cancelled (released over nothing, or revoked): the hub glows danger while the wheel fades out.</summary>
    Cancelled,
    /// <summary>A sector committed but its command could not be dispatched: that piece glows danger while the wheel fades out.</summary>
    Errored,
}
/// <summary>One seat's presented radial action menu, scoped to its viewport rect. Center and radii are pixels in
/// full-frame space; the seat's clip rect confines them, exactly as the drawn cursor's records are.</summary>
/// <param name="Viewport">The seat's viewport rect in normalized frame space.</param>
/// <param name="CenterX">The wheel hub x, px — either the opening pointer position or the viewport center, as
/// authored by the radial.</param>
/// <param name="CenterY">The wheel hub y, px.</param>
/// <param name="InnerRadius">The dead-zone radius, px — the innermost ring band starts here (a release inside it
/// cancels, so the hub is drawn quiet).</param>
/// <param name="RingWidth">One ring band's radial width, px — ring k occupies
/// [InnerRadius + k·RingWidth, InnerRadius + (k+1)·RingWidth).</param>
/// <param name="ActiveRing">The 0-based active ring (the one the cursor's angle selects within).</param>
/// <param name="HoveredSector">The 0-based hovered sector within the active ring, or <c>-1</c> when the active
/// selection input is in its dead zone, outside an authored hit-target ring, or nowhere known — the accent
/// highlight.</param>
/// <param name="RotationRadians">Sector-zero rotation clockwise from twelve o'clock.</param>
/// <param name="Clockwise">Whether sector indices advance clockwise.</param>
/// <param name="Rings">The rings, innermost first.</param>
/// <param name="Outcome">What the glow says this frame — hover accent while open, then the decided outcome for the
/// few frames the seat lingers after closing.</param>
/// <param name="OutcomeSector">The sector the outcome glow sits on, or -1 for the hub (a cancel with no piece).</param>
/// <param name="HubLabel">The hub's text while no sector is hovered — what releasing now does (a cancel).</param>
/// <param name="Fade">The whole wheel's opacity multiplier: 1 while open, then the authored ease curve from 1 to 0
/// across the fade-out after a commit or cancel.</param>
public readonly record struct OverlayWheelSeat(
    NormalizedRect Viewport,
    float CenterX,
    float CenterY,
    float InnerRadius,
    float RingWidth,
    int ActiveRing,
    int HoveredSector,
    float RotationRadians,
    bool Clockwise,
    ReadOnlyMemory<OverlayWheelRing> Rings,
    OverlayWheelOutcome Outcome = OverlayWheelOutcome.None,
    int OutcomeSector = -1,
    string HubLabel = "",
    float Fade = 1f
);
/// <summary>The per-frame wheel snapshot — one entry per seat whose wheel is currently open (the host owns the
/// open/close policy; an empty frame draws nothing, so closing is simply not publishing the seat).</summary>
/// <param name="Seats">The wheel-presenting seats, in slot order.</param>
public readonly record struct OverlayWheelFrame(
    ReadOnlyMemory<OverlayWheelSeat> Seats
);
/// <summary>The read seam <see cref="WheelWriter"/> consumes; the host's wheel feed is the writer.</summary>
public interface IWheelSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out OverlayWheelFrame frame);
}
/// <summary>
/// The radial-action-menu state store. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/>. Same
/// threading contract as <see cref="CursorStore"/>: the host's feed publishes once per produced frame and the
/// same-thread overlay writer reads, so backing arrays may be reused across publishes with zero steady-state
/// allocation.
/// </summary>
public sealed class WheelStore : IWheelSource {
    private readonly PublishBuffer<OverlayWheelFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in OverlayWheelFrame frame) => m_buffer.Publish(frame: frame);
    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayWheelFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}

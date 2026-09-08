using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Overlays;

/// <summary>One projected marker chip — an authored <c>markers</c> document row's instance made visible: the host
/// resolves the row's icon/style once against the document and projects its tracked source's pose into the seat's
/// viewport each frame. Every look field arrives already resolved (a bindable color/scalar's live value, not the
/// authored token) — Puck.Overlays never reads a document or live state itself. Positions are PIXELS in
/// full-frame space; the seat's clip rect confines them.</summary>
/// <param name="CenterX">The chip center x, px.</param>
/// <param name="CenterY">The chip center y, px.</param>
/// <param name="IconGlyph0">The row's resolved icon's first (or only) atlas glyph index, 1-based, 0 = none.</param>
/// <param name="IconGlyph1">The row's resolved icon's second atlas glyph index (a 2-cell icon's second cell),
/// 1-based, 0 = a single-glyph icon.</param>
/// <param name="ChipAlpha">The icon chip's resolved opacity.</param>
/// <param name="PlateHalf">The icon chip's resolved plate half-extent, px.</param>
/// <param name="RingRadiusPx">The projected support-radius ring, px (0 = no ring — a row with no ring policy, or a
/// tracked instance the policy's field does not resolve on).</param>
/// <param name="RingColor">The ring's resolved stroke color (meaningful only when <see cref="RingRadiusPx"/> is
/// positive).</param>
/// <param name="RingAlpha">The ring's resolved opacity (meaningful only when <see cref="RingRadiusPx"/> is
/// positive).</param>
/// <param name="Selected">Whether this instance is designated (the ACCENT chip tier).</param>
/// <param name="Pulse">Whether this instance's change shimmer is live (the HELD chip tier).</param>
public readonly record struct OverlayMarkerChip(
    float CenterX,
    float CenterY,
    ushort IconGlyph0,
    ushort IconGlyph1,
    float ChipAlpha,
    float PlateHalf,
    float RingRadiusPx,
    RgbaColor RingColor,
    float RingAlpha,
    bool Selected,
    bool Pulse
);
/// <summary>One seat's marker set, scoped to its viewport rect.</summary>
/// <param name="Viewport">The seat's viewport rect in normalized frame space.</param>
/// <param name="Chips">The projected chips visible in this seat's view.</param>
public readonly record struct OverlayMarkerSeat(
    NormalizedRect Viewport,
    ReadOnlyMemory<OverlayMarkerChip> Chips
);
/// <summary>The per-frame marker snapshot — one entry per seat (an empty frame draws nothing; the host publishes
/// every produced frame so an empty <c>markers</c> section, or none, clears the chips).</summary>
/// <param name="Seats">The seats, in slot order.</param>
public readonly record struct OverlayMarkerFrame(
    ReadOnlyMemory<OverlayMarkerSeat> Seats
);
/// <summary>The read seam <see cref="MarkerWriter"/> consumes; the host's frame source is the writer.</summary>
public interface IMarkerSource {
    /// <summary>Copies the latest published frame, when one exists.</summary>
    /// <param name="frame">The latest frame, when published.</param>
    /// <returns><see langword="true"/> when a frame has been published.</returns>
    bool TrySnapshot(out OverlayMarkerFrame frame);
}
/// <summary>
/// The marker state store. A thin named wrapper over the shared <see cref="PublishBuffer{T}"/>: the host's produce
/// path publishes and the same-thread overlay writer reads, so backing arrays may be reused across publishes with
/// zero steady-state allocation.
/// </summary>
public sealed class MarkerStore : IMarkerSource {
    private readonly PublishBuffer<OverlayMarkerFrame> m_buffer = new();

    /// <summary>Publishes a frame (the writer side).</summary>
    /// <param name="frame">The frame to publish.</param>
    public void Publish(in OverlayMarkerFrame frame) => m_buffer.Publish(frame: frame);
    /// <inheritdoc/>
    public bool TrySnapshot(out OverlayMarkerFrame frame) => m_buffer.TrySnapshot(frame: out frame);
}

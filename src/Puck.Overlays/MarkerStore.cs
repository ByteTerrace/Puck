using Puck.Abstractions.Presentation;

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
/// <summary>The marker state store, over the shared <see cref="OverlayStore{TFrame}"/>.</summary>
public sealed class MarkerStore : OverlayStore<OverlayMarkerFrame>;

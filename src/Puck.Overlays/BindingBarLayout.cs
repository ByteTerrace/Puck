using System.Numerics;

namespace Puck.Overlays;

/// <summary>
/// Tunes the binding-bar layout. Lengths are fractions of the target HEIGHT, so the cluster scales with resolution and
/// never depends on width; every <c>*Ratio</c>/<c>*Lift</c>/<c>*Spacing</c> is a multiple of the SCALED button size,
/// and every <c>*MinPx</c> is a device-pixel floor. All of it is authored data (<c>Puck.World.WorldBindingBarLayout</c>).
/// Where each plate SITS is authored too (the layout's slot table, in button pitches); this carries only the sizes.
/// </summary>
/// <param name="ButtonSize">The slot plate size.</param>
/// <param name="AnchorEdge">The edge the bar's own anchor (modifier row, page label, hints) hangs from.</param>
/// <param name="AnchorMargin">That anchor's inset from its edge, as a fraction of the region's extent along the
/// edge's axis.</param>
/// <param name="GlyphOffsetRatio">The gamepad glyph's corner offset, as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="GlyphSizeRatio">The gamepad glyph's size, as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="Scale">The uniform cluster scale.</param>
/// <param name="UnplacedRowLift">The unplaced row's lift above the anchor, in scaled button sizes.</param>
/// <param name="UnplacedSlotSpacing">The unplaced row's slot pitch, in scaled button sizes.</param>
/// <param name="BadgeCorner">The signed corner the badge nudges toward (+1 up-right, -1 down-left, 0 centered).</param>
/// <param name="ModifierHalfRatio">The modifier indicator's plate half-extent, in scaled button sizes.</param>
/// <param name="ModifierSpacingRatio">The modifier indicators' pitch, in scaled button sizes.</param>
/// <param name="ModifierGlyphRatio">The modifier badge's half-extent, as a fraction of the modifier plate half.</param>
/// <param name="LabelCellRatio">The page label's glyph-cell height, as a fraction of the modifier plate half.</param>
/// <param name="LabelCellMinPx">The page label's glyph-cell floor, px.</param>
/// <param name="LabelGapRatio">The page label's drop below the anchor, as a fraction of the modifier plate half.</param>
/// <param name="HintCellRatio">A chord-hint line's glyph-cell height, as a fraction of the modifier plate half.</param>
/// <param name="HintCellMinPx">A chord-hint line's glyph-cell floor, px.</param>
/// <param name="HintLineStepRatio">The chord-hint line pitch, as a fraction of the hint cell height.</param>
/// <param name="HintBaseGapRatio">The hint stack's lift above the anchor, as a fraction of the modifier plate half.</param>
public readonly record struct BindingBarLayoutOptions(
    float ButtonSize,
    OverlayBarEdge AnchorEdge,
    float AnchorMargin,
    float GlyphOffsetRatio,
    float GlyphSizeRatio,
    float Scale,
    float UnplacedRowLift,
    float UnplacedSlotSpacing,
    float BadgeCorner,
    float ModifierHalfRatio,
    float ModifierSpacingRatio,
    float ModifierGlyphRatio,
    float LabelCellRatio,
    float LabelCellMinPx,
    float LabelGapRatio,
    float HintCellRatio,
    float HintCellMinPx,
    float HintLineStepRatio,
    float HintBaseGapRatio
);
/// <summary>The region edge a bar hangs from — the overlay's own spelling of the authored edge.</summary>
public enum OverlayBarEdge : byte {
    Bottom,
    Top,
    Left,
    Right,
}
/// <summary>The pure geometry of a bar: an anchor on a region edge, and plates at pitches from it. A plate's pitches
/// arrive NORMALIZED to their anchor group (see <c>BindingBarSeatComposer.AnchorGroups</c>): along the edge's axis
/// 0 is the plate whose edge touches the margin, positive is inward; across it 0 is the group's center. Nothing here
/// knows what a control is — the shape is the document's.</summary>
public static class BindingBarLayout {
    /// <summary>The anchor point of an edge at its margin: the margin line's midpoint, region-height units, origin
    /// top-left. Along the edge's own axis the margin is a fraction of THAT axis's extent (width for left/right), so
    /// an authored "2.5% in" reads the same on every aspect.</summary>
    /// <param name="aspect">The region's width over its height.</param>
    /// <param name="edge">The edge.</param>
    /// <param name="margin">The inset from the edge, a fraction of the region's extent along the edge's axis.</param>
    /// <returns>The anchor point.</returns>
    public static Vector2 BarAnchor(float aspect, OverlayBarEdge edge, float margin) =>
        edge switch {
            OverlayBarEdge.Top => new Vector2(x: (aspect * 0.5f), y: margin),
            OverlayBarEdge.Left => new Vector2(x: (aspect * margin), y: 0.5f),
            OverlayBarEdge.Right => new Vector2(x: (aspect * (1f - margin)), y: 0.5f),
            _ => new Vector2(x: (aspect * 0.5f), y: (1f - margin)),
        };
    /// <summary>A plate's center from its normalized pitches: the anchor, plus half a plate so the pitch-0 plate's
    /// EDGE sits on the margin line, plus the pitches scaled to the button size — x right, y up, in the overlay's
    /// y-down frame.</summary>
    /// <param name="anchor">The anchor (see <see cref="BarAnchor"/>).</param>
    /// <param name="edge">The anchor's edge.</param>
    /// <param name="pitchX">Pitches right of the anchor (for a left/right edge: inward is +/−).</param>
    /// <param name="pitchY">Pitches above the anchor (for a top/bottom edge: inward is +/−).</param>
    /// <param name="buttonSize">The scaled button size, region-height units.</param>
    /// <returns>The plate center, region-height units.</returns>
    public static Vector2 PlateCenter(Vector2 anchor, OverlayBarEdge edge, float pitchX, float pitchY, float buttonSize) =>
        edge switch {
            OverlayBarEdge.Top => new Vector2(x: (anchor.X + (pitchX * buttonSize)), y: (anchor.Y + ((0.5f - pitchY) * buttonSize))),
            OverlayBarEdge.Left => new Vector2(x: (anchor.X + ((0.5f + pitchX) * buttonSize)), y: (anchor.Y - (pitchY * buttonSize))),
            OverlayBarEdge.Right => new Vector2(x: (anchor.X - ((0.5f - pitchX) * buttonSize)), y: (anchor.Y - (pitchY * buttonSize))),
            _ => new Vector2(x: (anchor.X + (pitchX * buttonSize)), y: (anchor.Y - ((0.5f + pitchY) * buttonSize))),
        };
}

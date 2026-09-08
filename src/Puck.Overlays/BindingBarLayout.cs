using Puck.Commands;
using System.Numerics;

namespace Puck.Overlays;

/// <summary>
/// Tunes the binding-bar layout. Lengths are fractions of the target HEIGHT, so the cluster scales with resolution and
/// never depends on width; every <c>*Ratio</c>/<c>*Lift</c>/<c>*Spacing</c> is a multiple of the SCALED button size,
/// and every <c>*MinPx</c> is a device-pixel floor. All of it is authored data (<c>Puck.World.WorldBindingBarLayout</c>).
/// Where each plate SITS is authored too (the layout's slot table, in button pitches); this carries only the sizes.
/// </summary>
/// <param name="ButtonSize">The slot plate size at most; see <see cref="CompiledBindingBarLayout.FitButtonSize(float, float)"/>.</param>
/// <param name="AnchorEdge">The edge the bar's own anchor (modifier row, page label, hints) hangs from.</param>
/// <param name="AnchorInset">That anchor's inset from its edge, in button pitches.</param>
/// <param name="GlyphOffsetRatio">The gamepad glyph's corner offset, as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="GlyphSizeRatio">The gamepad glyph's size, as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="Scale">The uniform cluster scale.</param>
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
    BindingBarEdge AnchorEdge,
    float AnchorInset,
    float GlyphOffsetRatio,
    float GlyphSizeRatio,
    float Scale,
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
/// <summary>The pure geometry of a bar: an anchor on a region edge, and plates at pitches from it. A plate's pitches
/// arrive normalized to their frame (see <see cref="CompiledBindingBarLayout"/>): along the edge's axis
/// 0 is the plate whose edge touches the inset, positive is inward; across it 0 is the group's center. Nothing here
/// knows what a control is — the shape is the document's.</summary>
public static class BindingBarLayout {
    /// <summary>The anchor point of an edge at its inset: the inset line's midpoint, region-height units, origin
    /// top-left.</summary>
    /// <param name="aspect">The region's width over its height.</param>
    /// <param name="edge">The edge.</param>
    /// <param name="inset">The inset from the edge, region-height units (pitches × button size).</param>
    /// <returns>The anchor point.</returns>
    public static Vector2 BarAnchor(float aspect, BindingBarEdge edge, float inset) =>
        edge switch {
            BindingBarEdge.Top => new Vector2(x: (aspect * 0.5f), y: inset),
            BindingBarEdge.Left => new Vector2(x: inset, y: 0.5f),
            BindingBarEdge.Right => new Vector2(x: (aspect - inset), y: 0.5f),
            _ => new Vector2(x: (aspect * 0.5f), y: (1f - inset)),
        };
    /// <summary>A plate's center from its normalized pitches: the anchor, plus half a plate so the pitch-0 plate's
    /// edge sits on the inset line, plus the pitches scaled to the button size — x right, y up, in the overlay's
    /// y-down frame.</summary>
    /// <param name="anchor">The anchor (see <see cref="BarAnchor"/>).</param>
    /// <param name="edge">The anchor's edge.</param>
    /// <param name="pitchX">Pitches right of the anchor (for a left/right edge: inward is +/−).</param>
    /// <param name="pitchY">Pitches above the anchor (for a top/bottom edge: inward is +/−).</param>
    /// <param name="buttonSize">The scaled button size, region-height units.</param>
    /// <returns>The plate center, region-height units.</returns>
    public static Vector2 PlateCenter(Vector2 anchor, BindingBarEdge edge, float pitchX, float pitchY, float buttonSize) =>
        edge switch {
            BindingBarEdge.Top => new Vector2(x: (anchor.X + (pitchX * buttonSize)), y: (anchor.Y + ((0.5f - pitchY) * buttonSize))),
            BindingBarEdge.Left => new Vector2(x: (anchor.X + ((0.5f + pitchX) * buttonSize)), y: (anchor.Y - (pitchY * buttonSize))),
            BindingBarEdge.Right => new Vector2(x: (anchor.X - ((0.5f - pitchX) * buttonSize)), y: (anchor.Y - (pitchY * buttonSize))),
            _ => new Vector2(x: (anchor.X + (pitchX * buttonSize)), y: (anchor.Y - ((0.5f + pitchY) * buttonSize))),
        };
}

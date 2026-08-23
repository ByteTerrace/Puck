using System.Numerics;

namespace Puck.Overlays;

/// <summary>
/// Tunes the binding-bar layout. Lengths are fractions of the target HEIGHT, so the cluster scales with resolution and
/// never depends on width; every <c>*Ratio</c>/<c>*Lift</c>/<c>*Spacing</c> is a multiple of the SCALED button size,
/// and every <c>*MinPx</c> is a device-pixel floor. All of it is authored data (<c>Puck.World.WorldBindingBarLayout</c>).
/// Where each plate SITS is authored too (the layout's slot table, in button pitches); this carries only the sizes.
/// </summary>
/// <param name="ButtonSize">The slot plate size at most; see <see cref="BindingBarLayout.FitButtonSize"/>.</param>
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
    OverlayBarEdge AnchorEdge,
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
/// <summary>The region edge a bar hangs from — the overlay's own spelling of the authored edge.</summary>
public enum OverlayBarEdge : byte {
    Bottom,
    Top,
    Left,
    Right,
}
/// <summary>The pure geometry of a bar: an anchor on a region edge, and plates at pitches from it. A plate's pitches
/// arrive normalized to their anchor group (see <c>BindingBarSeatComposer.AnchorGroups</c>): along the edge's axis
/// 0 is the plate whose edge touches the inset, positive is inward; across it 0 is the group's center. Nothing here
/// knows what a control is — the shape is the document's.</summary>
public static class BindingBarLayout {
    /// <summary>The anchor point of an edge at its inset: the inset line's midpoint, region-height units, origin
    /// top-left.</summary>
    /// <param name="aspect">The region's width over its height.</param>
    /// <param name="edge">The edge.</param>
    /// <param name="inset">The inset from the edge, region-height units (pitches × button size).</param>
    /// <returns>The anchor point.</returns>
    public static Vector2 BarAnchor(float aspect, OverlayBarEdge edge, float inset) =>
        edge switch {
            OverlayBarEdge.Top => new Vector2(x: (aspect * 0.5f), y: inset),
            OverlayBarEdge.Left => new Vector2(x: inset, y: 0.5f),
            OverlayBarEdge.Right => new Vector2(x: (aspect - inset), y: 0.5f),
            _ => new Vector2(x: (aspect * 0.5f), y: (1f - inset)),
        };
    /// <summary>The button size at which every anchor group of <paramref name="slots"/> fits the region: the
    /// authored size, shrunk until each group's inset plus its extent along its edge's axis fits that axis and its
    /// extent across fits the other. Pitches must already be normalized (<c>AnchorGroups</c>). One uniform size, so
    /// the groups keep their relationships; an empty bar keeps the authored size.</summary>
    /// <param name="slots">Every slot of one seat's bar.</param>
    /// <param name="buttonSize">The authored (scaled) button size, region-height units.</param>
    /// <param name="aspect">The region's width over its height.</param>
    /// <returns>The fitted button size.</returns>
    public static float FitButtonSize(ReadOnlySpan<OverlayBindingSlot> slots, float buttonSize, float aspect) {
        var fitted = buttonSize;

        for (var first = 0; (first < slots.Length); first++) {
            var edge = slots[first].AnchorEdge;
            var inset = slots[first].AnchorInset;
            var seen = false;

            for (var index = 0; (index < first); index++) {
                if ((slots[index].AnchorEdge == edge) && (slots[index].AnchorInset == inset)) {
                    seen = true;

                    break;
                }
            }

            if (seen) {
                continue;
            }

            var along = 0f;
            var acrossMin = float.MaxValue;
            var acrossMax = float.MinValue;

            for (var index = first; (index < slots.Length); index++) {
                ref readonly var slot = ref slots[index];

                if ((slot.AnchorEdge != edge) || (slot.AnchorInset != inset) || !slot.Visible) {
                    continue;
                }

                var sideways = (edge is OverlayBarEdge.Left or OverlayBarEdge.Right);

                along = MathF.Max(x: along, y: MathF.Abs(x: (sideways ? slot.PitchX : slot.PitchY)));
                acrossMin = MathF.Min(x: acrossMin, y: (sideways ? slot.PitchY : slot.PitchX));
                acrossMax = MathF.Max(x: acrossMax, y: (sideways ? slot.PitchY : slot.PitchX));
            }

            if (acrossMin > acrossMax) {
                continue;
            }

            // Plates are one pitch wide: the group spans (extent + 1) pitches, plus the inset along its own axis.
            var alongPitches = (along + 1f + inset);
            var acrossPitches = ((acrossMax - acrossMin) + 1f);
            var (alongLimit, acrossLimit) = ((edge is OverlayBarEdge.Left or OverlayBarEdge.Right)
                ? (aspect, 1f)
                : (1f, aspect)
            );

            fitted = MathF.Min(x: fitted, y: (alongLimit / alongPitches));
            fitted = MathF.Min(x: fitted, y: (acrossLimit / acrossPitches));
        }

        return fitted;
    }
    /// <summary>A plate's center from its normalized pitches: the anchor, plus half a plate so the pitch-0 plate's
    /// edge sits on the margin line, plus the pitches scaled to the button size — x right, y up, in the overlay's
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

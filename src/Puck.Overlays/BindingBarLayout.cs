using System.Numerics;
using Puck.Input;

namespace Puck.Overlays;

/// <summary>
/// Tunes the binding-bar layout. Lengths are fractions of the target HEIGHT (a 600-line reference: button 45px,
/// center gap 60px, anchor 220px above the bottom), so the cluster scales with resolution and never depends on width;
/// every <c>*Ratio</c>/<c>*Lift</c>/<c>*Spacing</c> is a multiple of the SCALED button size, and every <c>*MinPx</c>
/// is a device-pixel floor. All of it is authored data (<c>Puck.World.WorldBindingBarLayout</c>).
/// </summary>
/// <param name="ButtonSize">The slot plate size (45/600).</param>
/// <param name="CenterGap">The extra half-gap between the two mirrored clusters (60/600).</param>
/// <param name="AnchorOffsetY">The anchor's lift above the bottom edge (220/600).</param>
/// <param name="GlyphOffsetRatio">The gamepad glyph's corner offset, as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="GlyphSizeRatio">The gamepad glyph's size, as a fraction of <paramref name="ButtonSize"/> (24/45).</param>
/// <param name="Scale">The uniform cluster scale.</param>
/// <param name="CenterRowLift">The menu row's lift above the anchor, in scaled button sizes.</param>
/// <param name="CenterSlotSpacing">The menu row's slot pitch, in scaled button sizes.</param>
/// <param name="ExoticRowLift">The exotics row's lift above the anchor, in scaled button sizes.</param>
/// <param name="ExoticSlotSpacing">The exotics row's slot pitch, in scaled button sizes.</param>
/// <param name="BadgeCorner">The fixed corner direction a center/exotic slot's badge nudges toward (the compass
/// categories take their direction from <see cref="PadPictogramLayout"/> instead).</param>
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
    float CenterGap,
    float AnchorOffsetY,
    float GlyphOffsetRatio,
    float GlyphSizeRatio,
    float Scale,
    float CenterRowLift,
    float CenterSlotSpacing,
    float ExoticRowLift,
    float ExoticSlotSpacing,
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
/// <summary>One placed slot, in region-height units: x in [0, aspect], y in [0, 1], origin top-left.</summary>
/// <param name="Center">The plate center.</param>
/// <param name="GlyphCenter">The gamepad-glyph badge center (corner-offset from <paramref name="Center"/> by the modulo pattern).</param>
/// <param name="HalfSize">The plate half-extent.</param>
/// <param name="GlyphHalfSize">The glyph badge half-extent.</param>
public readonly record struct BindingSlotPlacement(
    Vector2 Center,
    Vector2 GlyphCenter,
    float HalfSize,
    float GlyphHalfSize
);
/// <summary>Which placement rule a physical control's slot follows.</summary>
public enum BindingSlotCategory {
    /// <summary>One of the twelve <see cref="BindingBarLayout.SlotSources"/> — the mirrored compass-diamond
    /// clusters.</summary>
    Classic,
    /// <summary>One of the three <see cref="BindingBarLayout.CenterSources"/> (back/guide/start) — the menu row
    /// between the two clusters.</summary>
    Center,
    /// <summary>Any other input source — the exotics row above the menu row.</summary>
    Exotic,
}
/// <summary>
/// The pure math that places one bar's binding slots around a bottom-center anchor: the twelve classic controls as
/// two mirrored six-slot compass clusters (within a cluster the <c>index % 6</c> pattern shapes a diamond — d-pad /
/// face buttons — with the stick press at its middle and the shoulder at its outer top; the right cluster mirrors the
/// left across the center); back/guide/start as a fixed three-slot row directly above the anchor, between the
/// clusters (real controllers place View–Guide–Menu in that left-to-right order, so <see cref="CenterSources"/>
/// fixes it too); and every other input source (touchpad, mute, the grips, a mouse button, …) as a row further above,
/// evenly spaced left-to-right in AUTHORED slot-set order — the caller's ordering choice, since these controls carry
/// no compass direction of their own. A bank's own 2D offset (region-height units, y-down, added by the caller)
/// shifts this whole placement to stack several banks around one anchor; the layout here is always single-bank. No
/// state, no rendering — categorized indices in, placements out.
/// </summary>
public static class BindingBarLayout {
    /// <summary>The number of slots a bar's compass clusters place — <see cref="SlotSources"/>' length as a
    /// constant.</summary>
    public const int SlotCount = 12;

    /// <summary>The input sources a bar's twelve classic slots represent, in slot order (the d-pad diamond, left
    /// shoulder, left stick press, the face diamond, right shoulder, right stick press). Exactly
    /// <see cref="SlotCount"/> entries.</summary>
    public static readonly string[] SlotSources = [
        InputSources.Gamepad.DpadUp,
        InputSources.Gamepad.DpadRight,
        InputSources.Gamepad.DpadDown,
        InputSources.Gamepad.DpadLeft,
        InputSources.Gamepad.LeftShoulder,
        InputSources.Gamepad.LeftStickPress,
        InputSources.Gamepad.ButtonNorth,
        InputSources.Gamepad.ButtonWest,
        InputSources.Gamepad.ButtonSouth,
        InputSources.Gamepad.ButtonEast,
        InputSources.Gamepad.RightShoulder,
        InputSources.Gamepad.RightStickPress,
    ];
    /// <summary>The input sources the center row represents, left to right (View/Back, Guide/home, Menu/Start — the
    /// real-controller order).</summary>
    public static readonly string[] CenterSources = [
        InputSources.Gamepad.Back,
        InputSources.Gamepad.Guide,
        InputSources.Gamepad.Start,
    ];

    /// <summary>Classifies an input source into its placement category.</summary>
    /// <param name="source">The provider-neutral input source id.</param>
    /// <param name="classicIndex">The source's index into <see cref="SlotSources"/> when
    /// <see cref="BindingSlotCategory.Classic"/>; otherwise -1.</param>
    /// <returns>The source's placement category.</returns>
    public static BindingSlotCategory Categorize(string source, out int classicIndex) {
        classicIndex = Array.IndexOf(array: SlotSources, value: source);

        if (classicIndex >= 0) {
            return BindingSlotCategory.Classic;
        }

        return (Array.IndexOf(array: CenterSources, value: source) >= 0
            ? BindingSlotCategory.Center
            : BindingSlotCategory.Exotic
        );
    }
    /// <summary>The bar's bottom-center anchor, in region-height units (y-down-from-top). The modifier indicators
    /// reuse this so they sit with the bar rather than floating at region center.</summary>
    /// <param name="aspect">The region aspect ratio (width / height).</param>
    /// <param name="anchorOffsetY">The anchor's lift above the bottom edge, as a fraction of the height.</param>
    /// <returns>The anchor point.</returns>
    public static Vector2 BarAnchor(float aspect, float anchorOffsetY) =>
        new(
            x: (aspect * 0.5f),
            y: (1f - anchorOffsetY)
        );
    /// <summary>The derived displacement of one stacked bank's plate, in whole button pitches, as a nesting of
    /// crosses: a compass cluster is a plus (its four corner cells are empty), and two pluses tile tightly when one
    /// sits two pitches up and two pitches across from the other, its arm tips filling the other's empty corners.
    /// The base bar is the outer middle of the weave and the wings nest INWARD of it — order 1 diagonally up and
    /// toward the bar's centre, order 2 diagonally down and toward the centre, orders 3 and 4 straight above and
    /// below the base — so every wing plate lands on the base bar's grid with no cluster-sized gaps. Each cluster
    /// steps relative to the bar's centre on its own side (a centre/exotic slot has no side and only climbs or
    /// drops). Orders past the table alternate further up and down.</summary>
    /// <param name="order">The bank's declared stack order; 0 sits on the anchor.</param>
    /// <param name="side">The plate's cluster side: -1 for the left cluster, +1 for the right, 0 for a slot with no
    /// side (centre row, exotic row).</param>
    /// <param name="buttonSize">The scaled slot-plate size, region-height units.</param>
    /// <returns>The plate's displacement, region-height units, y DOWN.</returns>
    public static Vector2 BankOffset(int order, int side, float buttonSize) {
        if (order <= 0) {
            return Vector2.Zero;
        }

        var (outward, up) = (order <= NestedWingPitches.Length)
            ? NestedWingPitches[(order - 1)]
            : (0, ((((order % 2) == 1) ? 1 : -1) * (4 + (2 * ((order - NestedWingPitches.Length + 1) / 2)))));

        return new Vector2(
            x: (side * outward * buttonSize),
            y: (-up * buttonSize)
        );
    }
    // The plus-nesting table, (outward, up) in button pitches per stack order — negative outward is toward the
    // bar's centre, negative up is below the base. See BankOffset.
    private static readonly (int Outward, int Up)[] NestedWingPitches = [
        (-2, 1),
        (-2, -2),
        (0, 4),
        (0, -4),
    ];
    /// <summary>The cluster side a placed slot belongs to — -1 for the left compass cluster, +1 for the right, 0 for
    /// a centre or exotic slot — the sign <see cref="BankOffset"/> fans a stacked bank by.</summary>
    /// <param name="category">The slot's placement category.</param>
    /// <param name="categoryIndex">The slot's index within that category.</param>
    /// <returns>-1, 0, or +1.</returns>
    public static int ClusterSide(BindingSlotCategory category, int categoryIndex) => ((category == BindingSlotCategory.Classic)
        ? ((categoryIndex < PadPictogramLayout.SlotsPerCluster)
            ? -1
            : 1)
        : 0
    );
    private static BindingSlotPlacement FromOffset(Vector2 anchor, float x, float yUp, float buttonSize, in BindingBarLayoutOptions options) {
        var center = new Vector2(
            x: (anchor.X + x),
            y: (anchor.Y - yUp)
        );
        // Center/exotic slots carry no compass direction, so their glyph badge nudges toward one fixed corner rather
        // than PadPictogramLayout's per-direction offset.
        var badge = ((buttonSize * options.GlyphOffsetRatio) * options.BadgeCorner);

        return new BindingSlotPlacement(
            Center: center,
            GlyphCenter: new Vector2(
                x: (center.X + badge),
                y: (center.Y - badge)
            ),
            GlyphHalfSize: ((buttonSize * options.GlyphSizeRatio) * 0.5f),
            HalfSize: (buttonSize * 0.5f)
        );
    }
    /// <summary>Places one slot: the shared <see cref="PadPictogramLayout"/> compass geometry for a
    /// <see cref="BindingSlotCategory.Classic"/> source (button center + badge direction from one source of truth),
    /// or the fixed menu-row / exotics-row geometry otherwise — anchored at the bar's bottom-center point and
    /// converted to the overlay's y-down frame. <see cref="SlotSources"/> already feeds the LEFT cluster pre-flipped
    /// slot indices (d-pad RIGHT at compass-west renders nearest the midpoint — the mirror puts it on the cluster's
    /// right side), per the primitive's documented mirror semantics.</summary>
    /// <param name="category">The slot's placement category (see <see cref="Categorize"/>).</param>
    /// <param name="categoryIndex">The slot's index within its category: 0-11 for <see cref="BindingSlotCategory.Classic"/>
    /// (its <see cref="SlotSources"/> index); 0-2 for <see cref="BindingSlotCategory.Center"/> (its
    /// <see cref="CenterSources"/> index); 0-(<paramref name="categoryCount"/>-1) for
    /// <see cref="BindingSlotCategory.Exotic"/> (its position among the authored slot set's exotics, left to
    /// right).</param>
    /// <param name="categoryCount">The total exotic slots in this bar (centers the exotics row); ignored for
    /// Classic/Center.</param>
    /// <param name="options">The layout tuning.</param>
    /// <param name="aspect">The region aspect ratio (width / height).</param>
    /// <returns>The slot's placement in region-height units.</returns>
    public static BindingSlotPlacement Place(BindingSlotCategory category, int categoryIndex, int categoryCount, in BindingBarLayoutOptions options, float aspect) {
        var buttonSize = (options.ButtonSize * options.Scale);
        var anchor = BarAnchor(
            aspect: aspect,
            anchorOffsetY: options.AnchorOffsetY
        );

        if (category == BindingSlotCategory.Classic) {
            var slot = PadPictogramLayout.Resolve(
                index: categoryIndex,
                options: new PadPictogramOptions(
                    ButtonSize: buttonSize,
                    CenterGap: (options.CenterGap * options.Scale),
                    GlyphOffsetRatio: options.GlyphOffsetRatio
                )
            );
            var center = new Vector2(
                x: (anchor.X + slot.X),
                y: (anchor.Y - slot.YUp)
            );

            return new BindingSlotPlacement(
                Center: center,
                GlyphCenter: new Vector2(
                    x: (center.X + slot.GlyphX),
                    y: (center.Y - slot.GlyphYUp)
                ),
                GlyphHalfSize: ((buttonSize * options.GlyphSizeRatio) * 0.5f),
                HalfSize: (buttonSize * 0.5f)
            );
        }

        // The center/exotic rows sit above the anchor (positive YUp) at multiples of the scaled button pitch, clear
        // of the classic clusters' own north-most slots (compass Y = 1, i.e. one button pitch above the anchor).
        if (category == BindingSlotCategory.Center) {
            var spacing = (buttonSize * options.CenterSlotSpacing);

            return FromOffset(
                anchor: anchor,
                buttonSize: buttonSize,
                options: in options,
                x: ((categoryIndex - 1) * spacing),
                yUp: (buttonSize * options.CenterRowLift)
            );
        }

        var exoticSpacing = (buttonSize * options.ExoticSlotSpacing);
        var exoticCenter = ((categoryCount - 1) * 0.5f);

        return FromOffset(
            anchor: anchor,
            buttonSize: buttonSize,
            options: in options,
            x: ((categoryIndex - exoticCenter) * exoticSpacing),
            yUp: (buttonSize * options.ExoticRowLift)
        );
    }
}

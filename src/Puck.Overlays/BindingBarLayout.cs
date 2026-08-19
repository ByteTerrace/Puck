using System.Numerics;
using Puck.Input.Devices;

namespace Puck.Overlays;

/// <summary>
/// Tunes the binding-bar layout. All lengths are fractions of the target HEIGHT (a 600-line reference: button 45px,
/// center gap 60px, anchor 220px above the bottom), so the cluster scales with resolution and never depends on width.
/// </summary>
/// <param name="ButtonSize">The slot plate size (45/600).</param>
/// <param name="CenterGap">The extra half-gap between the two mirrored clusters (60/600).</param>
/// <param name="AnchorOffsetY">The anchor's lift above the bottom edge (220/600).</param>
/// <param name="GlyphOffsetRatio">The gamepad glyph's corner offset, as a fraction of <paramref name="ButtonSize"/>.</param>
/// <param name="GlyphSizeRatio">The gamepad glyph's size, as a fraction of <paramref name="ButtonSize"/> (24/45).</param>
/// <param name="Scale">The uniform cluster scale.</param>
public readonly record struct BindingBarLayoutOptions(
    float ButtonSize,
    float CenterGap,
    float AnchorOffsetY,
    float GlyphOffsetRatio,
    float GlyphSizeRatio,
    float Scale
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
/// <summary>Which placement rule a physical button's slot follows.</summary>
public enum BindingSlotCategory {
    /// <summary>One of the twelve <see cref="BindingBarLayout.SlotButtons"/> — the mirrored compass-diamond
    /// clusters.</summary>
    Classic,
    /// <summary>One of the three <see cref="BindingBarLayout.CenterButtons"/> (Back/Guide/Start) — the menu row
    /// between the two clusters.</summary>
    Center,
    /// <summary>Any other <see cref="GamepadButtons"/> flag — the exotics row above the menu row.</summary>
    Exotic,
}
/// <summary>
/// The pure math that places one bar's binding slots around a bottom-center anchor: the twelve classic buttons as two
/// mirrored six-slot compass clusters (within a cluster the <c>index % 6</c> pattern shapes a diamond — d-pad / face
/// buttons — with the stick press at its middle and the shoulder at its outer top; the right cluster mirrors the
/// left across the center); Back/Guide/Start as a fixed three-slot row directly above the anchor, between the
/// clusters (real controllers place View–Guide–Menu in that left-to-right order, so <see cref="CenterButtons"/>
/// fixes it too); and every other physical button (Touchpad, Mute, the grips, …) as a row further above, evenly
/// spaced left-to-right in AUTHORED slot-set order — the caller's ordering choice, since these buttons carry no
/// compass direction of their own. A bank's own 2D offset (region-height units, y-down, added by the caller) shifts
/// this whole placement to stack several banks around one anchor; the layout here is always single-bank. No state,
/// no rendering — categorized indices in, placements out.
/// </summary>
public static class BindingBarLayout {
    // The center/exotic rows sit above the anchor (positive YUp) at multiples of the (scaled) button pitch, clear of
    // the classic clusters' own north-most slots (compass Y = 1, i.e. one button pitch above the anchor already).
    private const float CenterRowLift = 1.9f;
    private const float CenterSlotSpacing = 1.15f;
    private const float ExoticRowLift = (CenterRowLift + 1.7f);
    private const float ExoticSlotSpacing = 1.15f;
    // Center/exotic slots carry no compass direction, so their glyph badge nudges toward one fixed corner
    // (upper-right) rather than PadPictogramLayout's per-direction offset.
    private const float FixedBadgeCorner = 1f;

    /// <summary>The number of slots a bar places — <see cref="SlotButtons"/>' length as a constant.</summary>
    public const int SlotCount = 12;

    /// <summary>The physical buttons a bar's twelve classic slots represent, in slot order (the d-pad diamond, left
    /// shoulder, left stick, the face diamond, right shoulder, right stick). Exactly <see cref="SlotCount"/>
    /// entries.</summary>
    public static readonly GamepadButtons[] SlotButtons = [
        GamepadButtons.DpadUp,
        GamepadButtons.DpadRight,
        GamepadButtons.DpadDown,
        GamepadButtons.DpadLeft,
        GamepadButtons.LeftShoulder,
        GamepadButtons.LeftStickPress,
        GamepadButtons.ButtonNorth,
        GamepadButtons.ButtonWest,
        GamepadButtons.ButtonSouth,
        GamepadButtons.ButtonEast,
        GamepadButtons.RightShoulder,
        GamepadButtons.RightStickPress,
    ];
    /// <summary>The physical buttons the center row represents, left to right (View/Back, Guide/home, Menu/Start —
    /// the real-controller order).</summary>
    public static readonly GamepadButtons[] CenterButtons = [
        GamepadButtons.Back,
        GamepadButtons.Guide,
        GamepadButtons.Start,
    ];

    /// <summary>Classifies a physical button into its placement category.</summary>
    /// <param name="button">The physical button (one flag).</param>
    /// <param name="classicIndex">The button's index into <see cref="SlotButtons"/> when <see cref="BindingSlotCategory.Classic"/>;
    /// otherwise -1.</param>
    /// <returns>The button's placement category.</returns>
    public static BindingSlotCategory Categorize(GamepadButtons button, out int classicIndex) {
        classicIndex = Array.IndexOf(array: SlotButtons, value: button);

        if (classicIndex >= 0) {
            return BindingSlotCategory.Classic;
        }

        return (Array.IndexOf(array: CenterButtons, value: button) >= 0
            ? BindingSlotCategory.Center
            : BindingSlotCategory.Exotic
        );
    }
    /// <summary>The bar's bottom-center anchor, in region-height units (y-down-from-top). The modifier pips reuse
    /// this so they sit with the bar rather than floating at region center.</summary>
    /// <param name="aspect">The region aspect ratio (width / height).</param>
    /// <param name="anchorOffsetY">The anchor's lift above the bottom edge, as a fraction of the height.</param>
    /// <returns>The anchor point.</returns>
    public static Vector2 BarAnchor(float aspect, float anchorOffsetY) =>
        new(
            x: (aspect * 0.5f),
            y: (1f - anchorOffsetY)
        );
    private static BindingSlotPlacement FromOffset(Vector2 anchor, float x, float yUp, float buttonSize, in BindingBarLayoutOptions options) {
        var center = new Vector2(
            x: (anchor.X + x),
            y: (anchor.Y - yUp)
        );
        var badge = ((buttonSize * options.GlyphOffsetRatio) * FixedBadgeCorner);

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
    /// <see cref="BindingSlotCategory.Classic"/> button (button center + badge direction from one source of truth),
    /// or the fixed menu-row / exotics-row geometry otherwise — anchored at the bar's bottom-center point and
    /// converted to the overlay's y-down frame. <see cref="SlotButtons"/> already feeds the LEFT cluster pre-flipped
    /// slot indices (d-pad RIGHT at compass-west renders nearest the midpoint — the mirror puts it on the cluster's
    /// right side), per the primitive's documented mirror semantics.</summary>
    /// <param name="category">The slot's placement category (see <see cref="Categorize"/>).</param>
    /// <param name="categoryIndex">The slot's index within its category: 0-11 for <see cref="BindingSlotCategory.Classic"/>
    /// (its <see cref="SlotButtons"/> index); 0-2 for <see cref="BindingSlotCategory.Center"/> (its
    /// <see cref="CenterButtons"/> index); 0-(<paramref name="categoryCount"/>-1) for
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

        if (category == BindingSlotCategory.Center) {
            var spacing = (buttonSize * CenterSlotSpacing);

            return FromOffset(
                anchor: anchor,
                buttonSize: buttonSize,
                options: in options,
                x: ((categoryIndex - 1) * spacing),
                yUp: (buttonSize * CenterRowLift)
            );
        }

        var exoticSpacing = (buttonSize * ExoticSlotSpacing);
        var exoticCenter = ((categoryCount - 1) * 0.5f);

        return FromOffset(
            anchor: anchor,
            buttonSize: buttonSize,
            options: in options,
            x: ((categoryIndex - exoticCenter) * exoticSpacing),
            yUp: (buttonSize * ExoticRowLift)
        );
    }
}

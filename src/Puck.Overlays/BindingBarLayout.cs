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
public readonly record struct BindingBarLayoutOptions(
    float ButtonSize,
    float CenterGap,
    float AnchorOffsetY,
    float GlyphOffsetRatio,
    float GlyphSizeRatio
) {
    /// <summary>Gets the reference layout.</summary>
    public static BindingBarLayoutOptions Default => new(
        AnchorOffsetY: (220f / 600f),
        ButtonSize: (45f / 600f),
        CenterGap: (60f / 600f),
        GlyphOffsetRatio: 0.4375f,
        GlyphSizeRatio: (24f / 45f)
    );
}

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

/// <summary>
/// The pure math that places one bar's twelve binding slots — two mirrored six-slot clusters around a bottom-center
/// anchor: within a cluster the <c>index % 6</c> pattern shapes a diamond (d-pad / face buttons) with the stick press
/// at its middle and the shoulder at its outer top; slots 6-11 mirror 0-5 across the center. Lifted from the demo's
/// proven layout, minus its multi-bar quadrant fan-out — per-seat placement is the WRITER's job here (each seat's bar
/// lays out inside its own viewport region), so the layout itself is always single-bar. No state, no rendering —
/// indices in, placements out.
/// </summary>
public static class BindingBarLayout {
    /// <summary>The physical buttons a bar's twelve slots represent, in slot order (the d-pad diamond, left
    /// shoulder, left stick, the face diamond, right shoulder, right stick).</summary>
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

    /// <summary>The bar's bottom-center anchor, in region-height units (y-down-from-top). The modifier pips reuse
    /// this so they sit with the bar rather than floating at region center.</summary>
    /// <param name="aspect">The region aspect ratio (width / height).</param>
    /// <param name="anchorOffsetY">The anchor's lift above the bottom edge, as a fraction of the height.</param>
    /// <returns>The anchor point.</returns>
    public static Vector2 BarAnchor(float aspect, float anchorOffsetY) =>
        new(x: (aspect * 0.5f), y: (1f - anchorOffsetY));

    /// <summary>Places one slot.</summary>
    /// <param name="index">The layout slot index, 0-11.</param>
    /// <param name="options">The layout tuning.</param>
    /// <param name="aspect">The region aspect ratio (width / height).</param>
    /// <returns>The slot's placement in region-height units.</returns>
    public static BindingSlotPlacement Place(int index, in BindingBarLayoutOptions options, float aspect) {
        var iMod2 = (index % 2);
        var iMod6 = (index % 6);
        var isReflection = (index > 5);
        var sign = (isReflection ? 1f : -1f);
        var size = options.ButtonSize;

        // The column pattern: iMod6 1 sits innermost (2 sizes), 3 and 4 outermost (4 sizes), the rest on the
        // diamond spine (3 sizes); the center gap pushes both clusters apart.
        var xMultiplier = ((iMod6 == 1)
            ? 2f
            : (((iMod6 == 3) || (iMod6 == 4))
                ? 4f
                : 3f));
        var x = (((xMultiplier * size) + options.CenterGap) * sign);
        // Even slots offset one size vertically (up for the diamond top / shoulder columns, down otherwise, in the
        // y-up frame); odd slots ride the spine.
        var yUp = (((iMod2 == 1)
            ? 0f
            : size) * (((iMod6 == 0) || (iMod6 == 4))
            ? 1f
            : -1f));

        var anchor = BarAnchor(aspect: aspect, anchorOffsetY: options.AnchorOffsetY);
        var center = new Vector2(x: (anchor.X + x), y: (anchor.Y - yUp));

        // The gamepad glyph hugs the corner that faces away from the cluster, by the same modulo pattern.
        var badge = (size * options.GlyphOffsetRatio);
        var glyphX = (((iMod6 == 1)
            ? badge
            : (((iMod6 == 3) || (index == 4) || (index == 10))
                ? -badge
                : 0f)) * (isReflection ? -1f : 1f));
        var glyphYUp = (((iMod6 == 0) || (index == 4) || (index == 10))
            ? badge
            : ((iMod6 == 2)
                ? -badge
                : 0f));

        return new BindingSlotPlacement(
            Center: center,
            GlyphCenter: new Vector2(x: (center.X + glyphX), y: (center.Y - glyphYUp)),
            GlyphHalfSize: ((size * options.GlyphSizeRatio) * 0.5f),
            HalfSize: (size * 0.5f)
        );
    }
}

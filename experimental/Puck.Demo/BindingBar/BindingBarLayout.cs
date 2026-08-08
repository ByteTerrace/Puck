using System.Numerics;
using Puck.Input.Devices;

namespace Puck.Demo.BindingBar;

/// <summary>
/// Tunes the binding-bar layout. All lengths are fractions of the target HEIGHT (the WoW addon's pixel
/// constants at its 600-line reference: button 45px, center gap 60px, anchor 220px above the bottom), so the
/// cluster scales with resolution and never depends on width.
/// </summary>
/// <param name="ButtonSize">The slot plate size (45/600).</param>
/// <param name="CenterGap">The extra half-gap between the two mirrored clusters (60/600).</param>
/// <param name="AnchorOffsetY">The anchor's lift above the bottom edge (220/600).</param>
/// <param name="BarCount">How many 12-slot bars the layout places: 1 (the primary cluster) to 5 (all secondary bars).</param>
/// <param name="GlyphOffsetRatio">The gamepad glyph's corner offset, as a fraction of <paramref name="ButtonSize"/> (the addon's 0.4375).</param>
/// <param name="GlyphSizeRatio">The gamepad glyph's size, as a fraction of <paramref name="ButtonSize"/> (24/45).</param>
internal readonly record struct BindingBarLayoutOptions(
    float ButtonSize,
    float CenterGap,
    float AnchorOffsetY,
    int BarCount,
    float GlyphOffsetRatio,
    float GlyphSizeRatio
) {
    /// <summary>The WoW addon's reference layout, primary cluster only.</summary>
    public static BindingBarLayoutOptions Default => new(
        AnchorOffsetY: (220f / 600f),
        BarCount: 1,
        ButtonSize: (45f / 600f),
        CenterGap: (60f / 600f),
        GlyphOffsetRatio: 0.4375f,
        GlyphSizeRatio: (24f / 45f)
    );
}

/// <summary>One placed slot, in aspect units: x in [0, aspect], y in [0, 1], origin top-left (the overlay-shader
/// convention).</summary>
/// <param name="Center">The plate center.</param>
/// <param name="GlyphCenter">The gamepad-glyph badge center (corner-offset from <paramref name="Center"/> by the addon's modulo pattern).</param>
/// <param name="HalfSize">The plate half-extent.</param>
/// <param name="GlyphHalfSize">The glyph badge half-extent.</param>
/// <param name="Bar">The bar the slot belongs to (0 = primary).</param>
/// <param name="IndexInBar">The slot's index within its bar, 0-11.</param>
/// <param name="IsReflection">Whether the slot sits in the right (mirrored) cluster.</param>
internal readonly record struct BindingSlotPlacement(
    Vector2 Center,
    Vector2 GlyphCenter,
    float HalfSize,
    float GlyphHalfSize,
    int Bar,
    int IndexInBar,
    bool IsReflection
);

/// <summary>
/// The pure math that places binding-bar slots — a direct port of the WoW addon's modulo layout. Twelve slots
/// per bar form two mirrored six-slot clusters around a bottom-center anchor: within a cluster the
/// <c>index % 6</c> pattern shapes a diamond (d-pad / face buttons) with the stick press at its middle and the
/// shoulder at its outer top; slots 6-11 mirror 0-5 across the center. Secondary bars (1-4) reuse the same
/// pattern pushed outward/downward. No state, no rendering — indices in, normalized rectangles out.
/// </summary>
internal static class BindingBarLayout {
    // How much a per-player bar shrinks in the multiplayer quadrant layout (vs the single full-size bottom bar).
    private const float MultiBarScale = 0.62f;

    /// <summary>The physical buttons a bar's twelve slots represent, in slot order (the WoW addon's icon order:
    /// the d-pad diamond, left shoulder, left stick, the face diamond, right shoulder, right stick).</summary>
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

    /// <summary>The per-bar cluster anchor (aspect units, overlay y-down-from-top): a screen quadrant per bar in the
    /// multiplayer layout (bars 0/1 the bottom corners, 2/3 the top), or the shared bottom-center for a single bar.
    /// The modifier pips reuse this so they sit with their own bar rather than floating at frame center.</summary>
    public static Vector2 BarAnchor(int bar, int barCount, float aspect, float anchorOffsetY) =>
        ((barCount > 1)
            ? new Vector2(x: (aspect * (((bar % 2) == 0) ? 0.25f : 0.75f)), y: ((bar < 2) ? 0.76f : 0.26f))
            : new Vector2(x: (aspect * 0.5f), y: (1f - anchorOffsetY)));

    /// <summary>The bar scale for a given bar count (shrunk in the multiplayer quadrant layout).</summary>
    public static float BarScale(int barCount) =>
        ((barCount > 1) ? MultiBarScale : 1f);

    /// <summary>Places one slot.</summary>
    /// <param name="index">The layout slot index, 0 to (12 × <see cref="BindingBarLayoutOptions.BarCount"/> - 1).</param>
    /// <param name="options">The layout tuning.</param>
    /// <param name="aspect">The target aspect ratio (width / height).</param>
    /// <returns>The slot's placement in aspect units.</returns>
    public static BindingSlotPlacement Place(int index, in BindingBarLayoutOptions options, float aspect) {
        var bar = (index / 12);
        var indexInBar = (index % 12);
        var iMod2 = (indexInBar % 2);
        var iMod6 = (indexInBar % 6);
        var isReflection = (indexInBar > 5);
        var sign = (isReflection ? 1f : -1f);
        // MULTIPLAYER: each player's bar shrinks and moves to its OWN screen quadrant (no overlapping fan-out); a
        // single bar keeps the WoW addon's full-size bottom-center anchor.
        var scale = BarScale(barCount: options.BarCount);
        var size = (options.ButtonSize * scale);
        var centerGap = (options.CenterGap * scale);

        // The addon's column pattern: iMod6 1 sits innermost (2 sizes), 3 and 4 outermost (4 sizes), the rest on
        // the diamond spine (3 sizes); the center gap pushes both clusters apart.
        var xMultiplier = ((iMod6 == 1)
            ? 2f
            : (((iMod6 == 3) || (iMod6 == 4))
                ? 4f
                : 3f));
        var x = (((xMultiplier * size) + centerGap) * sign);
        // Even slots offset one size vertically (up for the diamond top / shoulder columns, down otherwise, in the
        // addon's y-up frame); odd slots ride the spine.
        var yUp = (((iMod2 == 1)
            ? 0f
            : size) * (((iMod6 == 0) || (iMod6 == 4))
            ? 1f
            : -1f));

        // Per-bar anchor (overlay y-down-from-top frame): multiplayer places bar b in a quadrant; a single bar uses the
        // shared bottom-center anchor.
        var anchor = BarAnchor(bar: bar, barCount: options.BarCount, aspect: aspect, anchorOffsetY: options.AnchorOffsetY);
        var center = new Vector2(x: (anchor.X + x), y: (anchor.Y - yUp));

        // The gamepad glyph hugs the corner that faces away from the cluster, by the same modulo pattern.
        var badge = (size * options.GlyphOffsetRatio);
        var glyphX = (((iMod6 == 1)
            ? badge
            : (((iMod6 == 3) || (indexInBar == 4) || (indexInBar == 10))
                ? -badge
                : 0f)) * (isReflection ? -1f : 1f));
        var glyphYUp = (((iMod6 == 0) || (indexInBar == 4) || (indexInBar == 10))
            ? badge
            : ((iMod6 == 2)
                ? -badge
                : 0f));

        return new BindingSlotPlacement(
            Bar: bar,
            Center: center,
            GlyphCenter: new Vector2(x: (center.X + glyphX), y: (center.Y - glyphYUp)),
            GlyphHalfSize: ((size * options.GlyphSizeRatio) * 0.5f),
            HalfSize: (size * 0.5f),
            IndexInBar: indexInBar,
            IsReflection: isReflection
        );
    }
}

using Puck.Input.Devices;

namespace Puck.Overlays;

/// <summary>
/// Procedural badge-glyph ids for the icon element kind's physical-button badges. Values below
/// <see cref="AtlasBase"/> select a procedural SDF function in <c>overlay-unified.frag.hlsl</c> (KEEP IN SYNC);
/// values at or above it are reserved for a texture-atlas tile (<c>id - AtlasBase</c>) so a themed texture path can
/// slot in later without touching the data model.
/// </summary>
public enum OverlayGlyphId : ushort {
    None = 0,
    ArrowUp = 1,
    ArrowRight = 2,
    ArrowDown = 3,
    ArrowLeft = 4,
    ShapeTriangle = 5,
    ShapeCircle = 6,
    ShapeCross = 7,
    ShapeSquare = 8,
    LetterA = 9,
    LetterB = 10,
    LetterX = 11,
    LetterY = 12,
    BumperLeft = 13,
    BumperRight = 14,
    TriggerLeft = 15,
    TriggerRight = 16,
    StickLeft = 17,
    StickRight = 18,

    /// <summary>Ids at or above this select a texture-atlas tile instead of a procedural function.</summary>
    AtlasBase = 1024,
}

/// <summary>
/// Procedural action-icon ids for the symbol drawn on an icon element's plate. The same procedural/atlas split as
/// <see cref="OverlayGlyphId"/> (KEEP IN SYNC with the shader); <see cref="Number1"/> through <see cref="Number12"/>
/// render as seven-segment numerals for generic placeholder actions. The editor verbs (<see cref="EditPrev"/>
/// onward) are the sculpt/select/place repertoire the P2 editor pages bind.
/// </summary>
public enum OverlayIconId : ushort {
    None = 0,
    /// <summary>An unrecognized icon string: a plain dot so a bound slot is never invisible.</summary>
    Generic = 1,
    Jump = 2,
    Interact = 3,
    Target = 4,
    Number1 = 8,
    Number2 = 9,
    Number3 = 10,
    Number4 = 11,
    Number5 = 12,
    Number6 = 13,
    Number7 = 14,
    Number8 = 15,
    Number9 = 16,
    Number10 = 17,
    Number11 = 18,
    Number12 = 19,

    // The editing verb icons. KEEP IN SYNC with the shader's actionIcon cases.
    /// <summary>Cycle to the previous item (a left-pointing loop).</summary>
    EditPrev = 20,
    /// <summary>Cycle to the next item (a right-pointing loop).</summary>
    EditNext = 21,
    /// <summary>Place the current item (a downward arrow onto a baseline).</summary>
    EditPlace = 22,
    /// <summary>Delete/undo (an X).</summary>
    EditDelete = 23,
    /// <summary>Exit the mode (a leftward return arrow).</summary>
    EditExit = 24,
    /// <summary>Duplicate the selection (two offset squares).</summary>
    EditDuplicate = 25,
    /// <summary>Link two selections (two interlocked rings).</summary>
    EditLink = 26,
    /// <summary>Cycle the target's material (a paint drop).</summary>
    EditMaterial = 27,
    /// <summary>Cycle a blend/boolean op (a two-circle venn).</summary>
    EditOpCycle = 28,
    /// <summary>Toggle a style knob (a half-filled circle).</summary>
    EditStyle = 29,
    /// <summary>Clear the selection (a slashed circle).</summary>
    EditDeselect = 30,
    /// <summary>Record the current pose/frame (a filled dot).</summary>
    EditRecord = 31,
    /// <summary>Play/stop (a play triangle).</summary>
    EditPlay = 32,

    /// <summary>Ids at or above this select a texture-atlas tile instead of a procedural function.</summary>
    AtlasBase = 1024,
}

/// <summary>
/// Resolves physical buttons and opaque binding icon strings to shader ids, on the CPU — the shader never knows
/// about controller families or icon names. Face-button glyphs follow the connected family: PlayStation 5 shapes,
/// Xbox letters (South = A), Switch Pro letters (South = B — Nintendo's positions differ from Xbox's).
/// </summary>
public static class OverlayGamepadGlyphs {
    /// <summary>Resolves a physical button to its family-specific badge glyph.</summary>
    /// <param name="button">The physical button (one flag).</param>
    /// <param name="family">The connected controller family; <see cref="GamepadType.Unknown"/> uses Xbox letters.</param>
    /// <returns>The glyph id.</returns>
    public static OverlayGlyphId Resolve(GamepadButtons button, GamepadType family) {
        return button switch {
            GamepadButtons.DpadUp => OverlayGlyphId.ArrowUp,
            GamepadButtons.DpadRight => OverlayGlyphId.ArrowRight,
            GamepadButtons.DpadDown => OverlayGlyphId.ArrowDown,
            GamepadButtons.DpadLeft => OverlayGlyphId.ArrowLeft,
            GamepadButtons.LeftShoulder => OverlayGlyphId.BumperLeft,
            GamepadButtons.RightShoulder => OverlayGlyphId.BumperRight,
            GamepadButtons.LeftStickPress => OverlayGlyphId.StickLeft,
            GamepadButtons.RightStickPress => OverlayGlyphId.StickRight,
            GamepadButtons.ButtonSouth or GamepadButtons.ButtonEast or GamepadButtons.ButtonWest or GamepadButtons.ButtonNorth => ResolveFace(button: button, family: family),
            _ => OverlayGlyphId.None,
        };
    }

    /// <summary>Resolves a modifier's input source id to its badge glyph.</summary>
    /// <param name="source">The provider-neutral input source id.</param>
    /// <returns>The glyph id.</returns>
    public static OverlayGlyphId ResolveModifierSource(string source) {
        return source switch {
            Puck.Input.InputSources.Gamepad.LeftTrigger => OverlayGlyphId.TriggerLeft,
            Puck.Input.InputSources.Gamepad.RightTrigger => OverlayGlyphId.TriggerRight,
            Puck.Input.InputSources.Gamepad.LeftShoulder => OverlayGlyphId.BumperLeft,
            Puck.Input.InputSources.Gamepad.RightShoulder => OverlayGlyphId.BumperRight,
            _ => OverlayGlyphId.None,
        };
    }

    /// <summary>Resolves a binding entry's opaque icon string (e.g. <c>action.jump</c>, <c>action.7</c>) to its icon id.</summary>
    /// <param name="icon">The icon string, or <see langword="null"/>.</param>
    /// <returns>The icon id; <see cref="OverlayIconId.Generic"/> when the string is unrecognized or absent.</returns>
    public static OverlayIconId ResolveIcon(string? icon) {
        return icon switch {
            "action.jump" => OverlayIconId.Jump,
            "action.interact" => OverlayIconId.Interact,
            "action.target" => OverlayIconId.Target,
            _ => (((icon is not null) &&
                icon.StartsWith(value: "action.", comparisonType: StringComparison.Ordinal) &&
                int.TryParse(s: icon.AsSpan(start: "action.".Length), result: out var number) &&
                (number is >= 1 and <= 12))
                ? (OverlayIconId)(((int)OverlayIconId.Number1) + (number - 1))
                : OverlayIconId.Generic),
        };
    }

    /// <summary>The short ASCII label for a physical-button badge, or <see langword="null"/> for the iconographic
    /// glyphs (d-pad arrows, the PlayStation face shapes) that read better as procedural symbols. A present label
    /// routes the badge to shared-atlas text; its absence leaves the procedural glyph path.</summary>
    /// <param name="glyph">The badge glyph.</param>
    /// <returns>The label, at most two characters, or <see langword="null"/>.</returns>
    public static string? BadgeLabel(OverlayGlyphId glyph) =>
        glyph switch {
            OverlayGlyphId.LetterA => "A",
            OverlayGlyphId.LetterB => "B",
            OverlayGlyphId.LetterX => "X",
            OverlayGlyphId.LetterY => "Y",
            OverlayGlyphId.BumperLeft => "LB",
            OverlayGlyphId.BumperRight => "RB",
            OverlayGlyphId.TriggerLeft => "LT",
            OverlayGlyphId.TriggerRight => "RT",
            OverlayGlyphId.StickLeft => "LS",
            OverlayGlyphId.StickRight => "RS",
            _ => null,
        };

    private static OverlayGlyphId ResolveFace(GamepadButtons button, GamepadType family) {
        if (family == GamepadType.PlayStation5) {
            return button switch {
                GamepadButtons.ButtonSouth => OverlayGlyphId.ShapeCross,
                GamepadButtons.ButtonEast => OverlayGlyphId.ShapeCircle,
                GamepadButtons.ButtonWest => OverlayGlyphId.ShapeSquare,
                _ => OverlayGlyphId.ShapeTriangle,
            };
        }

        if (family == GamepadType.SwitchPro) {
            // Nintendo letters sit in different positions: B is South and A is East (X North, Y West).
            return button switch {
                GamepadButtons.ButtonSouth => OverlayGlyphId.LetterB,
                GamepadButtons.ButtonEast => OverlayGlyphId.LetterA,
                GamepadButtons.ButtonWest => OverlayGlyphId.LetterY,
                _ => OverlayGlyphId.LetterX,
            };
        }

        // Xbox (and unknown devices): A South, B East, X West, Y North.
        return button switch {
            GamepadButtons.ButtonSouth => OverlayGlyphId.LetterA,
            GamepadButtons.ButtonEast => OverlayGlyphId.LetterB,
            GamepadButtons.ButtonWest => OverlayGlyphId.LetterX,
            _ => OverlayGlyphId.LetterY,
        };
    }
}

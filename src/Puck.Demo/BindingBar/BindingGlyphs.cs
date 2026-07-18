using Puck.Input.Devices;

namespace Puck.Demo.BindingBar;

/// <summary>
/// Shader glyph ids for the physical-button badges. Values below <see cref="AtlasBase"/> select a procedural SDF
/// function in <c>binding-bar-overlay.frag.hlsl</c> (KEEP IN SYNC); values at or above it are reserved for a
/// texture-atlas tile (<c>id - AtlasBase</c>) so a themed texture path can slot in later without touching the
/// data model.
/// </summary>
internal enum BindingGlyphId : ushort {
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
/// Shader icon ids for the bound-action symbol drawn on a slot plate. The same procedural/atlas split as
/// <see cref="BindingGlyphId"/> (KEEP IN SYNC with the shader); <see cref="Number1"/> through <see cref="Number12"/>
/// render as seven-segment numerals for the generic placeholder actions.
/// </summary>
internal enum BindingIconId : ushort {
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

    // Creator-mode action icons (the in-engine SDF authoring bar). KEEP IN SYNC with the shader's actionIcon cases.
    /// <summary>Cycle to the previous primitive (a left-pointing loop).</summary>
    CreatorPrev = 20,
    /// <summary>Cycle to the next primitive (a right-pointing loop).</summary>
    CreatorNext = 21,
    /// <summary>Place the current shape (a downward arrow onto a baseline).</summary>
    CreatorPlace = 22,
    /// <summary>Undo the last placed shape (an X).</summary>
    CreatorDelete = 23,
    /// <summary>Exit creator mode (a leftward return arrow).</summary>
    CreatorExit = 24,
    /// <summary>Duplicate the selected shape (two offset squares).</summary>
    CreatorDuplicate = 25,
    /// <summary>Link two selected shapes into a composition group (two interlocked rings).</summary>
    CreatorLink = 26,
    /// <summary>Cycle the target's material (a paint drop).</summary>
    CreatorMaterial = 27,
    /// <summary>Cycle the target's blend op (a two-circle boolean venn).</summary>
    CreatorOpCycle = 28,
    /// <summary>Toggle the bake style knob (a half-filled circle).</summary>
    CreatorStyle = 29,
    /// <summary>Clear the selection (a slashed circle).</summary>
    CreatorDeselect = 30,
    /// <summary>Record the current pose into the timeline frame (a filled dot).</summary>
    CreatorRecord = 31,
    /// <summary>Play/stop the frame loop (a play triangle).</summary>
    CreatorPlay = 32,

    /// <summary>Ids at or above this select a texture-atlas tile instead of a procedural function.</summary>
    AtlasBase = 1024,
}

/// <summary>
/// Resolves physical buttons and the profile's opaque icon strings to shader ids, on the CPU — the shader never
/// knows about controller families or icon names. Face-button glyphs follow the connected family: PlayStation 5
/// shapes, Xbox letters (South = A), Switch Pro letters (South = B — Nintendo's positions differ from Xbox's).
/// </summary>
internal static class BindingGlyphResolver {
    /// <summary>Resolves a physical button to its family-specific badge glyph.</summary>
    /// <param name="button">The physical button (one flag).</param>
    /// <param name="family">The connected controller family; <see cref="GamepadType.Unknown"/> uses Xbox letters.</param>
    /// <returns>The glyph id.</returns>
    public static BindingGlyphId Resolve(GamepadButtons button, GamepadType family) {
        return button switch {
            GamepadButtons.DpadUp => BindingGlyphId.ArrowUp,
            GamepadButtons.DpadRight => BindingGlyphId.ArrowRight,
            GamepadButtons.DpadDown => BindingGlyphId.ArrowDown,
            GamepadButtons.DpadLeft => BindingGlyphId.ArrowLeft,
            GamepadButtons.LeftShoulder => BindingGlyphId.BumperLeft,
            GamepadButtons.RightShoulder => BindingGlyphId.BumperRight,
            GamepadButtons.LeftStickPress => BindingGlyphId.StickLeft,
            GamepadButtons.RightStickPress => BindingGlyphId.StickRight,
            GamepadButtons.ButtonSouth or GamepadButtons.ButtonEast or GamepadButtons.ButtonWest or GamepadButtons.ButtonNorth => ResolveFace(button: button, family: family),
            _ => BindingGlyphId.None,
        };
    }

    /// <summary>Resolves a modifier's input source to its badge glyph (the default profile modifiers are the triggers).</summary>
    /// <param name="source">The provider-neutral input source id.</param>
    /// <returns>The glyph id.</returns>
    public static BindingGlyphId ResolveModifierSource(string source) {
        return source switch {
            Puck.Input.InputSources.Gamepad.LeftTrigger => BindingGlyphId.TriggerLeft,
            Puck.Input.InputSources.Gamepad.RightTrigger => BindingGlyphId.TriggerRight,
            Puck.Input.InputSources.Gamepad.LeftShoulder => BindingGlyphId.BumperLeft,
            Puck.Input.InputSources.Gamepad.RightShoulder => BindingGlyphId.BumperRight,
            _ => BindingGlyphId.None,
        };
    }

    /// <summary>Resolves a profile entry's opaque icon string (e.g. <c>action.jump</c>, <c>action.7</c>) to its icon id.</summary>
    /// <param name="icon">The icon string, or <see langword="null"/>.</param>
    /// <returns>The icon id; <see cref="BindingIconId.Generic"/> when the string is unrecognized or absent.</returns>
    public static BindingIconId ResolveIcon(string? icon) {
        return icon switch {
            "action.jump" => BindingIconId.Jump,
            "action.interact" => BindingIconId.Interact,
            "action.target" => BindingIconId.Target,
            _ => (((icon is not null) &&
                icon.StartsWith(value: "action.", comparisonType: StringComparison.Ordinal) &&
                int.TryParse(s: icon.AsSpan(start: "action.".Length), result: out var number) &&
                (number is >= 1 and <= 12))
                ? (BindingIconId)(((int)BindingIconId.Number1) + (number - 1))
                : BindingIconId.Generic),
        };
    }

    private static BindingGlyphId ResolveFace(GamepadButtons button, GamepadType family) {
        if (family == GamepadType.PlayStation5) {
            return button switch {
                GamepadButtons.ButtonSouth => BindingGlyphId.ShapeCross,
                GamepadButtons.ButtonEast => BindingGlyphId.ShapeCircle,
                GamepadButtons.ButtonWest => BindingGlyphId.ShapeSquare,
                _ => BindingGlyphId.ShapeTriangle,
            };
        }

        if (family == GamepadType.SwitchPro) {
            // Nintendo letters sit in different positions: B is South and A is East (X North, Y West).
            return button switch {
                GamepadButtons.ButtonSouth => BindingGlyphId.LetterB,
                GamepadButtons.ButtonEast => BindingGlyphId.LetterA,
                GamepadButtons.ButtonWest => BindingGlyphId.LetterY,
                _ => BindingGlyphId.LetterX,
            };
        }

        // Xbox (and unknown devices): A South, B East, X West, Y North.
        return button switch {
            GamepadButtons.ButtonSouth => BindingGlyphId.LetterA,
            GamepadButtons.ButtonEast => BindingGlyphId.LetterB,
            GamepadButtons.ButtonWest => BindingGlyphId.LetterX,
            _ => BindingGlyphId.LetterY,
        };
    }
}

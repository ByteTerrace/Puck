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
    /// <summary>The four-position face diamond with the NORTH position marked — the neutral, family-invariant
    /// face-button treatment (abstract positions, no vendor's branding).</summary>
    FaceNorth = 5,
    /// <summary>The face diamond with the EAST position marked.</summary>
    FaceEast = 6,
    /// <summary>The face diamond with the SOUTH position marked.</summary>
    FaceSouth = 7,
    /// <summary>The face diamond with the WEST position marked.</summary>
    FaceWest = 8,
    BumperLeft = 9,
    BumperRight = 10,
    TriggerLeft = 11,
    TriggerRight = 12,
    StickLeft = 13,
    StickRight = 14,

    /// <summary>Ids at or above this select a texture-atlas tile instead of a procedural function.</summary>
    AtlasBase = 1024,
}

/// <summary>
/// Procedural action-icon ids for the symbol drawn on an icon element's plate. The same procedural/atlas split as
/// <see cref="OverlayGlyphId"/> (KEEP IN SYNC with the shader); <see cref="Number1"/> through <see cref="Number12"/>
/// render as the icon grammar's hairline drafting digits for generic placeholder actions. The editor verbs
/// (<see cref="EditPrev"/> onward) are the sculpt/select/place repertoire the P2 editor pages bind.
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
    /// <summary>Step the local edit ring back (a leftward hook arrow over its arc).</summary>
    EditUndo = 33,
    /// <summary>Step the local edit ring forward (the undo arrow's x-mirror).</summary>
    EditRedo = 34,

    /// <summary>Ids at or above this select a texture-atlas tile instead of a procedural function.</summary>
    AtlasBase = 1024,
}

/// <summary>
/// Resolves physical buttons and opaque binding icon strings to shader ids, on the CPU — the shader never knows
/// about controller families or icon names. Face buttons resolve to the NEUTRAL four-position diamond glyphs
/// (the physical positions <c>Puck.Input</c> names — South/East/West/North — drawn as abstract positions), so the
/// badge artwork is family-invariant; the family parameter remains the seam a future themed atlas tier keys on.
/// </summary>
public static class OverlayGamepadGlyphs {
    /// <summary>Resolves a physical button to its badge glyph.</summary>
    /// <param name="button">The physical button (one flag).</param>
    /// <param name="family">The connected controller family — reserved for a future themed (atlas) glyph tier; the
    /// procedural glyph set is family-invariant by design.</param>
    /// <returns>The glyph id.</returns>
    public static OverlayGlyphId Resolve(GamepadButtons button, GamepadType family) {
        _ = family;

        return button switch {
            GamepadButtons.DpadUp => OverlayGlyphId.ArrowUp,
            GamepadButtons.DpadRight => OverlayGlyphId.ArrowRight,
            GamepadButtons.DpadDown => OverlayGlyphId.ArrowDown,
            GamepadButtons.DpadLeft => OverlayGlyphId.ArrowLeft,
            GamepadButtons.LeftShoulder => OverlayGlyphId.BumperLeft,
            GamepadButtons.RightShoulder => OverlayGlyphId.BumperRight,
            GamepadButtons.LeftStickPress => OverlayGlyphId.StickLeft,
            GamepadButtons.RightStickPress => OverlayGlyphId.StickRight,
            GamepadButtons.ButtonNorth => OverlayGlyphId.FaceNorth,
            GamepadButtons.ButtonEast => OverlayGlyphId.FaceEast,
            GamepadButtons.ButtonSouth => OverlayGlyphId.FaceSouth,
            GamepadButtons.ButtonWest => OverlayGlyphId.FaceWest,
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
            // The editing verb icons (the shader's Edit* cases) as binding-entry strings, so an editor page set is
            // pure data end to end.
            "edit.prev" => OverlayIconId.EditPrev,
            "edit.next" => OverlayIconId.EditNext,
            "edit.place" => OverlayIconId.EditPlace,
            "edit.delete" => OverlayIconId.EditDelete,
            "edit.exit" => OverlayIconId.EditExit,
            "edit.duplicate" => OverlayIconId.EditDuplicate,
            "edit.link" => OverlayIconId.EditLink,
            "edit.material" => OverlayIconId.EditMaterial,
            "edit.op" => OverlayIconId.EditOpCycle,
            "edit.style" => OverlayIconId.EditStyle,
            "edit.deselect" => OverlayIconId.EditDeselect,
            "edit.record" => OverlayIconId.EditRecord,
            "edit.play" => OverlayIconId.EditPlay,
            "edit.undo" => OverlayIconId.EditUndo,
            "edit.redo" => OverlayIconId.EditRedo,
            _ => (((icon is not null) &&
                icon.StartsWith(value: "action.", comparisonType: StringComparison.Ordinal) &&
                int.TryParse(s: icon.AsSpan(start: "action.".Length), result: out var number) &&
                (number is >= 1 and <= 12))
                ? (OverlayIconId)(((int)OverlayIconId.Number1) + (number - 1))
                : OverlayIconId.Generic),
        };
    }

    /// <summary>The short ASCII label for a physical-button badge, or <see langword="null"/> for the iconographic
    /// glyphs (d-pad arrows, the face-position diamonds) that read better as procedural symbols. A present label
    /// routes the badge to shared-atlas text; its absence leaves the procedural glyph path.</summary>
    /// <param name="glyph">The badge glyph.</param>
    /// <returns>The label, at most two characters, or <see langword="null"/>.</returns>
    public static string? BadgeLabel(OverlayGlyphId glyph) =>
        glyph switch {
            OverlayGlyphId.BumperLeft => "LB",
            OverlayGlyphId.BumperRight => "RB",
            OverlayGlyphId.TriggerLeft => "LT",
            OverlayGlyphId.TriggerRight => "RT",
            OverlayGlyphId.StickLeft => "LS",
            OverlayGlyphId.StickRight => "RS",
            _ => null,
        };
}

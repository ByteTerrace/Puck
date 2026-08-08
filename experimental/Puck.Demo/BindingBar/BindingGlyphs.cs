namespace Puck.Demo.BindingBar;

/// <summary>
/// Glyph ids for the physical-button badges on a binding chip, carried by <see cref="BindingSlotView"/> and drawn
/// by the diegetic chip tier (<c>DiegeticUiDirector</c>). Values at or above <see cref="AtlasBase"/> are reserved
/// for a texture-atlas tile (<c>id - AtlasBase</c>) so a themed texture path can slot in later without touching
/// the data model.
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
/// Icon ids for the bound-action symbol drawn on a slot plate. The same atlas split as
/// <see cref="BindingGlyphId"/>; <see cref="Number1"/> through <see cref="Number12"/>
/// render as numerals for the generic placeholder actions.
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

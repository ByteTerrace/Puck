namespace Puck.HumbleGamingBrick;

/// <summary>The direction held on the D-pad while a CGB boots, which (with the A/B modifiers) overrides the automatic
/// compatibility palette chosen for a DMG game. <see cref="None"/> leaves the title-hash default in place. The values
/// match the CGB boot ROM's scan order, where Right takes priority over Left, Left over Up, and Up over Down.</summary>
public enum BootPaletteDirection {
    /// <summary>No direction held; the boot ROM keeps the title-hash palette.</summary>
    None = 0,

    /// <summary>The right direction.</summary>
    Right = 1,

    /// <summary>The left direction.</summary>
    Left = 2,

    /// <summary>The up direction.</summary>
    Up = 3,

    /// <summary>The down direction.</summary>
    Down = 4,
}

/// <summary>
/// A button combination held during a CGB boot to pick one of the boot ROM's alternative compatibility palettes for a
/// DMG game (a direction plus optional A and/or B). The default value holds nothing, selecting the automatic
/// title-hash palette.
/// </summary>
/// <param name="Direction">The D-pad direction held, or <see cref="BootPaletteDirection.None"/> for the default.</param>
/// <param name="A">Whether the A button is held.</param>
/// <param name="B">Whether the B button is held.</param>
public readonly record struct BootPaletteSelection(
    BootPaletteDirection Direction = BootPaletteDirection.None,
    bool A = false,
    bool B = false
) {
    /// <summary>Gets the boot ROM's one-based key-combination index (1-16), or zero when no direction is held.</summary>
    public int KeyCombinationIndex =>
        ((Direction == BootPaletteDirection.None)
            ? 0
            : ((int)Direction + (A ? 4 : 0) + (B ? 8 : 0)));
}

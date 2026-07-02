using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The eight console buttons as a bit set, split into the two lines the hardware multiplexes: the low nibble is the
/// direction pad (selected by P14) and the high nibble is the action buttons (selected by P15). A set bit means the
/// button is held. The host hands the full state to <see cref="IJoypad.SetButtons"/> at a deterministic point.
/// </summary>
[Flags]
public enum JoypadButtons : byte {
    /// <summary>No button held.</summary>
    None = 0,
    /// <summary>The right direction (P14 line, bit 0).</summary>
    Right = (1 << 0),
    /// <summary>The left direction (P14 line, bit 1).</summary>
    Left = (1 << 1),
    /// <summary>The up direction (P14 line, bit 2).</summary>
    Up = (1 << 2),
    /// <summary>The down direction (P14 line, bit 3).</summary>
    Down = (1 << 3),
    /// <summary>The A button (P15 line, bit 0).</summary>
    A = (1 << 4),
    /// <summary>The B button (P15 line, bit 1).</summary>
    B = (1 << 5),
    /// <summary>The Select button (P15 line, bit 2).</summary>
    Select = (1 << 6),
    /// <summary>The Start button (P15 line, bit 3).</summary>
    Start = (1 << 7),
}

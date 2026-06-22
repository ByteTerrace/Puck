namespace Puck.GameBoy;

/// <summary>Specifies a Game Boy button. The flag value is the bit it occupies in the joypad's internal pressed
/// state: the low nibble holds the direction pad (read when <c>P14</c> is selected) and the high nibble the action
/// buttons (read when <c>P15</c> is selected).</summary>
[Flags]
public enum JoypadButton : byte {
    /// <summary>No button.</summary>
    None = 0,
    /// <summary>The right direction.</summary>
    Right = (1 << 0),
    /// <summary>The left direction.</summary>
    Left = (1 << 1),
    /// <summary>The up direction.</summary>
    Up = (1 << 2),
    /// <summary>The down direction.</summary>
    Down = (1 << 3),
    /// <summary>The A button.</summary>
    A = (1 << 4),
    /// <summary>The B button.</summary>
    B = (1 << 5),
    /// <summary>The Select button.</summary>
    Select = (1 << 6),
    /// <summary>The Start button.</summary>
    Start = (1 << 7),
}

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>Specifies keypad keys as active-high flags in KEYINPUT bit order (the kernel's EWRAM input bytes use
/// the same layout; the hardware register itself is active-low — <see cref="AgbVerifyMachineDriver"/> inverts on
/// the way in).</summary>
[Flags]
public enum AgbKeys : ushort {
    /// <summary>No keys.</summary>
    None = 0,
    /// <summary>The A button.</summary>
    A = (1 << 0),
    /// <summary>The B button.</summary>
    B = (1 << 1),
    /// <summary>The Select button.</summary>
    Select = (1 << 2),
    /// <summary>The Start button.</summary>
    Start = (1 << 3),
    /// <summary>D-pad right.</summary>
    Right = (1 << 4),
    /// <summary>D-pad left.</summary>
    Left = (1 << 5),
    /// <summary>D-pad up.</summary>
    Up = (1 << 6),
    /// <summary>D-pad down.</summary>
    Down = (1 << 7),
    /// <summary>The R shoulder button.</summary>
    R = (1 << 8),
    /// <summary>The L shoulder button.</summary>
    L = (1 << 9),
}

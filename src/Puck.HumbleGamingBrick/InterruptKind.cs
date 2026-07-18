namespace Puck.HumbleGamingBrick;

/// <summary>
/// The five maskable interrupt sources, as the bit each occupies in the IF and IE registers. The values double as a
/// flag set, so a request mask and the enable mask combine with ordinary bitwise operators, and the dispatch priority
/// is simply the lowest set bit (VBlank highest, joypad lowest).
/// </summary>
[Flags]
public enum InterruptKind : byte {
    /// <summary>No interrupt.</summary>
    None = 0,
    /// <summary>The PPU entered vertical blank (bit 0, highest priority).</summary>
    VBlank = (1 << 0),
    /// <summary>An enabled LCD STAT condition was met (bit 1).</summary>
    LcdStatus = (1 << 1),
    /// <summary>The timer counter overflowed (bit 2).</summary>
    Timer = (1 << 2),
    /// <summary>A serial transfer completed (bit 3).</summary>
    Serial = (1 << 3),
    /// <summary>A joypad line went low (bit 4, lowest priority).</summary>
    Joypad = (1 << 4),
}

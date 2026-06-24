namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>Specifies the five SM83 interrupt sources. The flag value is the bit each source occupies in
/// the interrupt-flag (<c>IF</c>, <c>0xFF0F</c>) and interrupt-enable (<c>IE</c>, <c>0xFFFF</c>) registers;
/// the bit order is also the service priority, lowest bit first (<see cref="VBlank"/> highest).</summary>
[Flags]
public enum InterruptKind : byte {
    /// <summary>No interrupt.</summary>
    None = 0,
    /// <summary>The PPU entered vertical blank (bit&#160;0, vector <c>0x40</c>).</summary>
    VBlank = (1 << 0),
    /// <summary>A configured PPU/LCD STAT condition was met (bit&#160;1, vector <c>0x48</c>).</summary>
    LcdStat = (1 << 1),
    /// <summary>The timer counter (<c>TIMA</c>) overflowed (bit&#160;2, vector <c>0x50</c>).</summary>
    Timer = (1 << 2),
    /// <summary>A serial transfer completed (bit&#160;3, vector <c>0x58</c>).</summary>
    Serial = (1 << 3),
    /// <summary>A joypad input line went low (bit&#160;4, vector <c>0x60</c>).</summary>
    Joypad = (1 << 4),
}

namespace Puck.GameBoyAdvance;

/// <summary>Specifies a Game Boy Advance interrupt source, encoded as its bit position within the IE and IF
/// registers.</summary>
public enum InterruptSource {
    /// <summary>The PPU entered the vertical blanking period.</summary>
    VBlank = 0,
    /// <summary>The PPU entered a horizontal blanking period.</summary>
    HBlank = 1,
    /// <summary>The PPU scanline counter matched the configured value.</summary>
    VCounter = 2,
    /// <summary>Timer 0 overflowed.</summary>
    Timer0 = 3,
    /// <summary>Timer 1 overflowed.</summary>
    Timer1 = 4,
    /// <summary>Timer 2 overflowed.</summary>
    Timer2 = 5,
    /// <summary>Timer 3 overflowed.</summary>
    Timer3 = 6,
    /// <summary>The serial communication unit signalled completion.</summary>
    Serial = 7,
    /// <summary>DMA channel 0 completed.</summary>
    Dma0 = 8,
    /// <summary>DMA channel 1 completed.</summary>
    Dma1 = 9,
    /// <summary>DMA channel 2 completed.</summary>
    Dma2 = 10,
    /// <summary>DMA channel 3 completed.</summary>
    Dma3 = 11,
    /// <summary>A configured keypad condition was met.</summary>
    Keypad = 12,
    /// <summary>The game pak signalled an interrupt.</summary>
    GamePak = 13,
}

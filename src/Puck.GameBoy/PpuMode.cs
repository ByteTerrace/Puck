namespace Puck.GameBoy;

/// <summary>Specifies the PPU's current scanline phase, as reported in the low two bits of the LCD status
/// register (<c>STAT</c>). The numeric values are the hardware mode numbers.</summary>
public enum PpuMode {
    /// <summary>Horizontal blank: the remainder of a visible scanline after pixel transfer; VRAM and OAM are accessible.</summary>
    HorizontalBlank = 0,
    /// <summary>Vertical blank: scanlines 144-153, after the visible frame; VRAM and OAM are accessible.</summary>
    VerticalBlank = 1,
    /// <summary>OAM scan: the first 80 dots of a visible scanline, selecting sprites; OAM is inaccessible.</summary>
    OamScan = 2,
    /// <summary>Pixel transfer: the PPU is drawing the scanline; both VRAM and OAM are inaccessible.</summary>
    Drawing = 3,
}

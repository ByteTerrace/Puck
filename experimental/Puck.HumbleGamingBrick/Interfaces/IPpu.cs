namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The picture processing unit as the bus sees it. It owns the display-timing registers (LCDC, STAT, LY, LYC) and
/// advances the scanline counter that the rest of the machine — and most test ROMs — synchronize to. The bus forwards
/// those registers here.
/// </summary>
public interface IPpu {
    /// <summary>Gets the current STAT mode (0 = HBlank, 1 = VBlank, 2 = OAM scan, 3 = drawing). HBlank DMA keys off the
    /// transition into mode 0.</summary>
    int Mode { get; }

    /// <summary>Reads one of the PPU's timing registers.</summary>
    /// <param name="address">The register address (LCDC, STAT, LY, or LYC).</param>
    /// <returns>The register value as the CPU observes it, including the read-only status bits STAT reports.</returns>
    byte ReadRegister(ushort address);
    /// <summary>Writes one of the PPU's timing registers, applying its side effects — notably that turning the LCD off
    /// through LCDC resets the scanline counter.</summary>
    /// <param name="address">The register address (LCDC, STAT, LY, or LYC).</param>
    /// <param name="value">The value being written.</param>
    void WriteRegister(ushort address, byte value);
}

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
    /// <summary>Gets whether the PPU currently blocks CPU reads of object attribute memory (blocked reads return open
    /// bus). Locked through the OAM scan and drawing, releasing a few dots after drawing ends — the CPU-facing unlock
    /// trails the internal mode-0 edge.</summary>
    bool BlocksOamReads { get; }
    /// <summary>Gets whether the PPU currently blocks CPU writes to object attribute memory (blocked writes are
    /// dropped). Runs the read lock's schedule except that it arms a machine cycle into the scan and briefly reopens
    /// between the end of the scan and the pixel pipeline engaging.</summary>
    bool BlocksOamWrites { get; }
    /// <summary>Gets whether the PPU currently blocks CPU reads of video RAM (blocked reads return open bus). Locked
    /// while drawing, releasing a few dots after the internal mode-0 edge.</summary>
    bool BlocksVideoRamReads { get; }
    /// <summary>Gets whether the PPU currently blocks CPU writes to video RAM (blocked writes are dropped). Follows
    /// the read lock except that writes still land during the pipeline's entry-latency dots after the mode-3
    /// flip.</summary>
    bool BlocksVideoRamWrites { get; }

    /// <summary>Reads one of the PPU's timing registers.</summary>
    /// <param name="address">The register address (LCDC, STAT, LY, or LYC).</param>
    /// <returns>The register value as the CPU observes it, including the read-only status bits STAT reports.</returns>
    byte ReadRegister(ushort address);
    /// <summary>Writes one of the PPU's timing registers, applying its side effects — notably that turning the LCD off
    /// through LCDC resets the scanline counter.</summary>
    /// <param name="address">The register address (LCDC, STAT, LY, or LYC).</param>
    /// <param name="value">The value being written.</param>
    void WriteRegister(ushort address, byte value);
    /// <summary>Applies the OAM corruption bug's write-corruption formula for a 16-bit register's increment/decrement
    /// unit output, if the PPU is currently scanning OAM (mode 2) and the address falls in its range. This is the
    /// register-bump-only trigger — <c>INC</c>/<c>DEC rr</c>, the implicit stack-pointer move that opens
    /// <c>PUSH</c>/<c>CALL</c>/<c>RST</c>/interrupt dispatch, and <c>LD SP,HL</c> — which corrupts the row the scan is
    /// reading regardless of whether the surrounding instruction also reads or writes memory.</summary>
    /// <param name="address">The register's value before the increment/decrement.</param>
    void NoteRegisterAddressBus(ushort address);
    /// <summary>Applies the OAM corruption bug for a CPU read that reaches OAM while the PPU blocks reads of it — every
    /// read instruction whose address lands in <c>0xFE00</c>-<c>0xFEFF</c> during the scan, independent of which
    /// register carried the address. The formula depends on which row (mod 4) the scan is currently reading: two of
    /// every four rows use the plain read-corruption formula, one uses the combined formula Pan Docs calls "read during
    /// increase/decrease," and one uses a row-specific formula unique to this hardware family.</summary>
    void NoteBlockedOamRead();
    /// <summary>Applies the OAM corruption bug's write-corruption formula for a CPU write that reaches OAM while the
    /// PPU blocks writes to it — every write instruction whose address lands in <c>0xFE00</c>-<c>0xFEFF</c> during the
    /// scan, independent of which register carried the address.</summary>
    void NoteBlockedOamWrite();
}

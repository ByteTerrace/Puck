namespace Puck.Demo.Forge.Framework;

/// <summary>
/// The framework's own hardware constants — high-page I/O ports, memory-mapped regions, LCDC bits, and the fixed
/// cartridge prologue addresses. Deliberately self-contained (about thirty lines) so the framework depends only on <see cref="Sm83Emitter"/>, <see cref="HgbImage"/>, and its own files
/// (the lift posture: this folder should extract cleanly into a standalone toolkit).
/// </summary>
internal static class Hw {
    // High-page (0xFF00 + port) I/O ports.
    /// <summary>P1/JOYP — the joypad matrix select/read register.</summary>
    public const byte PortJoypad = 0x00;
    /// <summary>SB — the serial link cable's data shift register (matches <c>MemoryMap.SerialData</c> in
    /// <c>experimental/Puck.HumbleGamingBrick/Interfaces/ISerial.cs</c>).</summary>
    public const byte PortSerialData = 0x01;
    /// <summary>SC — the serial link cable's control register (matches <c>MemoryMap.SerialControl</c>).</summary>
    public const byte PortSerialControl = 0x02;
    /// <summary>DIV — the free-running divider register (the upper byte of the 16-bit divider counter). Reading it is a
    /// cheap deterministic-yet-hard-to-predict entropy source: two machines that reached the same code point after
    /// different amounts of execution hold different DIV values, so a link protocol seeds a symmetry-breaking backoff
    /// from it (see <see cref="LinkProtocolModule"/>). Writing any value resets the whole counter to zero.</summary>
    public const byte PortDivider = 0x04;
    /// <summary>IF — the interrupt request flags.</summary>
    public const byte PortInterruptFlag = 0x0F;
    /// <summary>NR10 — pulse 1's frequency sweep.</summary>
    public const byte PortPulse1Sweep = 0x10;
    /// <summary>NR11 — pulse 1's duty/length.</summary>
    public const byte PortPulse1DutyLength = 0x11;
    /// <summary>NR12 — pulse 1's volume envelope (zeroing the top five bits turns the DAC off — silence).</summary>
    public const byte PortPulse1Envelope = 0x12;
    /// <summary>NR13 — pulse 1's period low byte.</summary>
    public const byte PortPulse1PeriodLow = 0x13;
    /// <summary>NR14 — pulse 1's period high bits + trigger (bit 7).</summary>
    public const byte PortPulse1PeriodHigh = 0x14;
    /// <summary>NR21 — pulse 2's duty/length.</summary>
    public const byte PortPulse2DutyLength = 0x16;
    /// <summary>NR22 — pulse 2's volume envelope.</summary>
    public const byte PortPulse2Envelope = 0x17;
    /// <summary>NR23 — pulse 2's period low byte.</summary>
    public const byte PortPulse2PeriodLow = 0x18;
    /// <summary>NR24 — pulse 2's period high bits + trigger (bit 7).</summary>
    public const byte PortPulse2PeriodHigh = 0x19;
    /// <summary>NR30 — the wave channel's DAC enable.</summary>
    public const byte PortWaveDacEnable = 0x1A;
    /// <summary>NR41 — the noise channel's length.</summary>
    public const byte PortNoiseLength = 0x20;
    /// <summary>NR42 — the noise channel's volume envelope.</summary>
    public const byte PortNoiseEnvelope = 0x21;
    /// <summary>NR43 — the noise channel's polynomial counter (clock shift, LFSR width, divider).</summary>
    public const byte PortNoisePolynomial = 0x22;
    /// <summary>NR44 — the noise channel's control + trigger (bit 7).</summary>
    public const byte PortNoiseControl = 0x23;
    /// <summary>NR50 — the left/right master volumes.</summary>
    public const byte PortMasterVolume = 0x24;
    /// <summary>NR51 — the per-channel left/right output routing.</summary>
    public const byte PortOutputRouting = 0x25;
    /// <summary>NR52 — the master sound on/off register.</summary>
    public const byte PortSoundOnOff = 0x26;
    /// <summary>LCDC — the LCD control register.</summary>
    public const byte PortLcdControl = 0x40;
    /// <summary>SCY — the background vertical scroll.</summary>
    public const byte PortScrollY = 0x42;
    /// <summary>SCX — the background horizontal scroll.</summary>
    public const byte PortScrollX = 0x43;
    /// <summary>LY — the current scanline.</summary>
    public const byte PortScanline = 0x44;
    /// <summary>DMA — writing a source page here arms the OAM DMA transfer.</summary>
    public const byte PortOamDma = 0x46;
    /// <summary>VBK — the Color VRAM bank select.</summary>
    public const byte PortVramBank = 0x4F;
    /// <summary>BGPI — the Color background palette index (bit 7 = auto-increment).</summary>
    public const byte PortBgPaletteIndex = 0x68;
    /// <summary>BGPD — the Color background palette data window.</summary>
    public const byte PortBgPaletteData = 0x69;
    /// <summary>OBPI — the Color object palette index (bit 7 = auto-increment).</summary>
    public const byte PortObjPaletteIndex = 0x6A;
    /// <summary>OBPD — the Color object palette data window.</summary>
    public const byte PortObjPaletteData = 0x6B;
    /// <summary>IE — the interrupt enable mask (0xFFFF, reachable as high-page port 0xFF).</summary>
    public const byte PortInterruptEnable = 0xFF;

    /// <summary>The auto-increment bit for the Color palette index ports.</summary>
    public const byte PaletteAutoIncrement = 0x80;
    /// <summary>The VBlank bit of IF/IE.</summary>
    public const byte InterruptVBlankBit = 0x01;

    // SC (PortSerialControl) bits — matches SerialComponent's TransferActive/ClockSelect/FastClock constants in
    // experimental/Puck.HumbleGamingBrick/SerialComponent.cs (0x80/0x01/0x02 respectively).
    /// <summary>SC bit 7 — writing it with the transfer bit set arms a link exchange (hardware clears it when the
    /// eighth bit shifts).</summary>
    public const byte SerialTransferStart = 0x80;
    /// <summary>SC bit 0 — this side drives the shift clock; unset, the port waits on a linked peer's clock edges
    /// instead (an unconnected cable then never completes — see <see cref="LinkModule"/>).</summary>
    public const byte SerialInternalClock = 0x01;
    /// <summary>SC bit 1 — the Color double-speed shift clock (32× the normal rate); meaningful only alongside
    /// <see cref="SerialInternalClock"/>.</summary>
    public const byte SerialFastClock = 0x02;

    // Memory-mapped regions.
    /// <summary>The tile-graphics region of VRAM.</summary>
    public const ushort VramTiles = 0x8000;
    /// <summary>The first 32×32 background tilemap.</summary>
    public const ushort VramBackgroundMap = 0x9800;
    /// <summary>Object attribute memory (the hardware sprite table).</summary>
    public const ushort ObjectAttributeMemory = 0xFE00;

    // LCDC bits: LCD on (0x80) | BG tile data @ 0x8000 (0x10) | 8×16 objects (0x04) | OBJ on (0x02) | BG on (0x01).
    /// <summary>LCDC bit 7 — the LCD master enable.</summary>
    public const byte LcdOn = 0x80;
    /// <summary>LCDC bit 4 — BG tile data at 0x8000 (unsigned tile ids).</summary>
    public const byte LcdBgTileData8000 = 0x10;
    /// <summary>LCDC bit 2 — 8×16 objects.</summary>
    public const byte LcdObj8x16 = 0x04;
    /// <summary>LCDC bit 1 — object display enable.</summary>
    public const byte LcdObjOn = 0x02;
    /// <summary>LCDC bit 0 — background display enable.</summary>
    public const byte LcdBgOn = 0x01;
    /// <summary>The framework's stock preset: LCD on, unsigned BG tiles, 8×8 objects, BG + OBJ enabled.</summary>
    public const byte LcdBackgroundAndObjects = (byte)(LcdOn | LcdBgTileData8000 | LcdObjOn | LcdBgOn);

    // The fixed cartridge prologue convention (see FrameworkCartridge).
    /// <summary>Where the routine is loaded and entered: the header trampoline jumps here, and the first emitted
    /// instruction must be <c>jp boot</c> (3 bytes) so the VBlank handler lands at <see cref="VBlankHandlerAddress"/>.</summary>
    public const ushort EntryAddress = 0x0150;
    /// <summary>The fixed address of the VBlank interrupt handler (the vector at 0x0040 jumps here).</summary>
    public const ushort VBlankHandlerAddress = 0x0153;

    /// <summary>Computes the background-map cell address for a (row, column) pair at BUILD time.</summary>
    /// <param name="row">The map row (0..31; rows 0..17 are on screen).</param>
    /// <param name="column">The map column (0..31; columns 0..19 are on screen).</param>
    /// <returns>The VRAM address of the cell.</returns>
    public static ushort MapCell(int row, int column) {
        if ((row < 0) || (row > 31) || (column < 0) || (column > 31)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(row), message: $"Map cell ({row}, {column}) is outside the 32×32 background map.");
        }

        return (ushort)((VramBackgroundMap + (row * 32)) + column);
    }
}

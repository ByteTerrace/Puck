namespace Puck.HumbleGamingBrick;

/// <summary>
/// The fixed addresses and region bounds of the colour brick's 16-bit address space. Centralizing them keeps the
/// bus's decode and every component's register handling reading one set of names rather than scattered magic numbers.
/// All values are inclusive bounds unless a name says otherwise.
/// </summary>
public static class MemoryMap {
    /// <summary>The first address of the cartridge ROM (fixed bank 0).</summary>
    public const ushort RomBank0Start = 0x0000;
    /// <summary>The last address of the cartridge ROM's fixed bank 0.</summary>
    public const ushort RomBank0End = 0x3FFF;
    /// <summary>The first address of the cartridge ROM's switchable bank.</summary>
    public const ushort RomBankNStart = 0x4000;
    /// <summary>The last address of the cartridge ROM's switchable bank.</summary>
    public const ushort RomBankNEnd = 0x7FFF;
    /// <summary>The first address of video RAM.</summary>
    public const ushort VideoRamStart = 0x8000;
    /// <summary>The last address of video RAM.</summary>
    public const ushort VideoRamEnd = 0x9FFF;
    /// <summary>The first address of the cartridge's external (save) RAM.</summary>
    public const ushort ExternalRamStart = 0xA000;
    /// <summary>The last address of the cartridge's external (save) RAM.</summary>
    public const ushort ExternalRamEnd = 0xBFFF;
    /// <summary>The first address of work RAM (fixed bank 0).</summary>
    public const ushort WorkRamBank0Start = 0xC000;
    /// <summary>The last address of work RAM's fixed bank 0.</summary>
    public const ushort WorkRamBank0End = 0xCFFF;
    /// <summary>The first address of work RAM's switchable bank (CGB banks 1–7).</summary>
    public const ushort WorkRamBankNStart = 0xD000;
    /// <summary>The last address of work RAM's switchable bank.</summary>
    public const ushort WorkRamBankNEnd = 0xDFFF;
    /// <summary>The first address of the echo of work RAM.</summary>
    public const ushort EchoRamStart = 0xE000;
    /// <summary>The last address of the echo of work RAM.</summary>
    public const ushort EchoRamEnd = 0xFDFF;
    /// <summary>The distance subtracted from an echo-RAM address to reach the work-RAM address it mirrors.</summary>
    public const ushort EchoRamMirrorOffset = (EchoRamStart - WorkRamBank0Start);
    /// <summary>The first address of object attribute memory (sprite table).</summary>
    public const ushort ObjectAttributeMemoryStart = 0xFE00;
    /// <summary>The last address of object attribute memory.</summary>
    public const ushort ObjectAttributeMemoryEnd = 0xFE9F;
    /// <summary>The first address of the unusable region between OAM and the I/O page.</summary>
    public const ushort UnusableStart = 0xFEA0;
    /// <summary>The last address of the unusable region.</summary>
    public const ushort UnusableEnd = 0xFEFF;
    /// <summary>The first address of the I/O register page.</summary>
    public const ushort IoRegistersStart = 0xFF00;
    /// <summary>The last address of the I/O register page.</summary>
    public const ushort IoRegistersEnd = 0xFF7F;
    /// <summary>The first address of high RAM.</summary>
    public const ushort HighRamStart = 0xFF80;
    /// <summary>The last address of high RAM.</summary>
    public const ushort HighRamEnd = 0xFFFE;

    /// <summary>The joypad register (P1/JOYP).</summary>
    public const ushort Joypad = 0xFF00;
    /// <summary>The serial transfer data register (SB).</summary>
    public const ushort SerialData = 0xFF01;
    /// <summary>The serial transfer control register (SC).</summary>
    public const ushort SerialControl = 0xFF02;
    /// <summary>The divider register (DIV).</summary>
    public const ushort Divider = 0xFF04;
    /// <summary>The timer counter register (TIMA).</summary>
    public const ushort TimerCounter = 0xFF05;
    /// <summary>The timer modulo register (TMA).</summary>
    public const ushort TimerModulo = 0xFF06;
    /// <summary>The timer control register (TAC).</summary>
    public const ushort TimerControl = 0xFF07;
    /// <summary>The interrupt flag register (IF): the set of currently requested interrupts.</summary>
    public const ushort InterruptFlag = 0xFF0F;
    /// <summary>The first address of the audio register block (NR10).</summary>
    public const ushort AudioStart = 0xFF10;
    /// <summary>The last address of the audio control register block (NR52).</summary>
    public const ushort AudioEnd = 0xFF26;
    /// <summary>The first address of wave-pattern RAM.</summary>
    public const ushort WaveRamStart = 0xFF30;
    /// <summary>The last address of wave-pattern RAM.</summary>
    public const ushort WaveRamEnd = 0xFF3F;
    /// <summary>The audio master-control register (NR52): power and per-channel status.</summary>
    public const ushort AudioMasterControl = 0xFF26;

    /// <summary>The LCD control register (LCDC).</summary>
    public const ushort LcdControl = 0xFF40;
    /// <summary>The object-attribute-memory DMA source register (DMA).</summary>
    public const ushort OamDmaSource = 0xFF46;
    /// <summary>The LCD status register (STAT).</summary>
    public const ushort LcdStatus = 0xFF41;
    /// <summary>The background scroll-Y register (SCY).</summary>
    public const ushort ScrollY = 0xFF42;
    /// <summary>The background scroll-X register (SCX).</summary>
    public const ushort ScrollX = 0xFF43;
    /// <summary>The LCD current-scanline register (LY).</summary>
    public const ushort LcdY = 0xFF44;
    /// <summary>The LCD scanline-compare register (LYC).</summary>
    public const ushort LcdYCompare = 0xFF45;
    /// <summary>The DMG background palette register (BGP).</summary>
    public const ushort BackgroundPalette = 0xFF47;
    /// <summary>The DMG object palette 0 register (OBP0).</summary>
    public const ushort ObjectPalette0 = 0xFF48;
    /// <summary>The DMG object palette 1 register (OBP1).</summary>
    public const ushort ObjectPalette1 = 0xFF49;
    /// <summary>The window scroll-Y register (WY).</summary>
    public const ushort WindowY = 0xFF4A;
    /// <summary>The window scroll-X-plus-7 register (WX).</summary>
    public const ushort WindowX = 0xFF4B;
    /// <summary>The CGB background color-palette index register (BCPS/BGPI).</summary>
    public const ushort BackgroundColorPaletteIndex = 0xFF68;
    /// <summary>The CGB background color-palette data register (BCPD/BGPD).</summary>
    public const ushort BackgroundColorPaletteData = 0xFF69;
    /// <summary>The CGB object color-palette index register (OCPS/OBPI).</summary>
    public const ushort ObjectColorPaletteIndex = 0xFF6A;
    /// <summary>The CGB object color-palette data register (OCPD/OBPD).</summary>
    public const ushort ObjectColorPaletteData = 0xFF6B;
    /// <summary>The CGB VRAM bank select register (VBK).</summary>
    public const ushort VramBankSelect = 0xFF4F;
    /// <summary>The boot ROM disable register (BANK): any nonzero write permanently unmaps the boot ROM overlay.</summary>
    public const ushort BootRomDisable = 0xFF50;
    /// <summary>The CGB HDMA source high byte (HDMA1).</summary>
    public const ushort HdmaSourceHigh = 0xFF51;
    /// <summary>The CGB HDMA source low byte (HDMA2).</summary>
    public const ushort HdmaSourceLow = 0xFF52;
    /// <summary>The CGB HDMA destination high byte (HDMA3).</summary>
    public const ushort HdmaDestinationHigh = 0xFF53;
    /// <summary>The CGB HDMA destination low byte (HDMA4).</summary>
    public const ushort HdmaDestinationLow = 0xFF54;
    /// <summary>The CGB HDMA length/mode/start register (HDMA5).</summary>
    public const ushort HdmaControl = 0xFF55;
    /// <summary>The CGB speed-switch register (KEY1).</summary>
    public const ushort SpeedSwitch = 0xFF4D;
    /// <summary>The CGB work-RAM bank select register (SVBK).</summary>
    public const ushort WorkRamBankSelect = 0xFF70;
    /// <summary>The CGB PCM amplitude register for channels 1 and 2 (PCM12): their live digital outputs, one per nibble.</summary>
    public const ushort PcmAmplitude12 = 0xFF76;
    /// <summary>The CGB PCM amplitude register for channels 3 and 4 (PCM34): their live digital outputs, one per nibble.</summary>
    public const ushort PcmAmplitude34 = 0xFF77;
    /// <summary>The interrupt enable register (IE): the set of interrupts permitted to dispatch.</summary>
    public const ushort InterruptEnable = 0xFFFF;
}

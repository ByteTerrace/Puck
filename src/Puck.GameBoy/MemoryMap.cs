namespace Puck.GameBoy;

/// <summary>The Game Boy CPU address map: the region boundaries the <see cref="SystemBus"/> decoder switches on
/// and the I/O register addresses components claim. Values are inclusive bounds unless named otherwise.</summary>
public static class MemoryMap {
    /// <summary>The last address of the cartridge ROM region (<c>0x0000</c>–<c>0x7FFF</c>).</summary>
    public const ushort RomEnd = 0x7FFF;
    /// <summary>The first address of the video-RAM region (<c>0x8000</c>–<c>0x9FFF</c>).</summary>
    public const ushort VideoRamBase = 0x8000;
    /// <summary>The last address of the video-RAM region.</summary>
    public const ushort VideoRamEnd = 0x9FFF;
    /// <summary>The first address of the cartridge-RAM region (<c>0xA000</c>–<c>0xBFFF</c>).</summary>
    public const ushort CartridgeRamBase = 0xA000;
    /// <summary>The last address of the cartridge-RAM region.</summary>
    public const ushort CartridgeRamEnd = 0xBFFF;
    /// <summary>The first address of the work-RAM region (<c>0xC000</c>–<c>0xDFFF</c>).</summary>
    public const ushort WorkRamBase = 0xC000;
    /// <summary>The last address of the work-RAM region.</summary>
    public const ushort WorkRamEnd = 0xDFFF;
    /// <summary>The first address of the echo-RAM region (<c>0xE000</c>–<c>0xFDFF</c>) that mirrors work RAM.</summary>
    public const ushort EchoRamBase = 0xE000;
    /// <summary>The last address of the echo-RAM region.</summary>
    public const ushort EchoRamEnd = 0xFDFF;
    /// <summary>The first address of the object-attribute-memory region (<c>0xFE00</c>–<c>0xFE9F</c>).</summary>
    public const ushort OamBase = 0xFE00;
    /// <summary>The last address of the object-attribute-memory region.</summary>
    public const ushort OamEnd = 0xFE9F;
    /// <summary>The first address of the unusable region (<c>0xFEA0</c>–<c>0xFEFF</c>).</summary>
    public const ushort UnusableBase = 0xFEA0;
    /// <summary>The last address of the unusable region.</summary>
    public const ushort UnusableEnd = 0xFEFF;
    /// <summary>The first address of the I/O register region (<c>0xFF00</c>–<c>0xFF7F</c>).</summary>
    public const ushort IoBase = 0xFF00;
    /// <summary>The last address of the I/O register region.</summary>
    public const ushort IoEnd = 0xFF7F;
    /// <summary>The first address of high RAM (<c>0xFF80</c>–<c>0xFFFE</c>).</summary>
    public const ushort HighRamBase = 0xFF80;
    /// <summary>The last address of high RAM.</summary>
    public const ushort HighRamEnd = 0xFFFE;

    /// <summary>The joypad register (<c>P1</c>/<c>JOYP</c>).</summary>
    public const ushort Joypad = 0xFF00;
    /// <summary>The serial transfer data register (<c>SB</c>).</summary>
    public const ushort SerialData = 0xFF01;
    /// <summary>The serial transfer control register (<c>SC</c>).</summary>
    public const ushort SerialControl = 0xFF02;
    /// <summary>The divider register (<c>DIV</c>): the high byte of the internal counter.</summary>
    public const ushort Divider = 0xFF04;
    /// <summary>The timer counter register (<c>TIMA</c>).</summary>
    public const ushort TimerCounter = 0xFF05;
    /// <summary>The timer modulo register (<c>TMA</c>).</summary>
    public const ushort TimerModulo = 0xFF06;
    /// <summary>The timer control register (<c>TAC</c>).</summary>
    public const ushort TimerControl = 0xFF07;
    /// <summary>The interrupt-flag register (<c>IF</c>).</summary>
    public const ushort InterruptFlag = 0xFF0F;
    /// <summary>The first address of the audio register block (<c>NR10</c>, <c>0xFF10</c>).</summary>
    public const ushort AudioBase = 0xFF10;
    /// <summary>The master sound control register (<c>NR52</c>): bit&#160;7 powers the APU, bits&#160;0-3 report channel activity.</summary>
    public const ushort AudioMasterControl = 0xFF26;
    /// <summary>The first address of wave-pattern RAM (channel&#160;3 sample table, <c>0xFF30</c>-<c>0xFF3F</c>).</summary>
    public const ushort WaveRamBase = 0xFF30;
    /// <summary>The last address of wave-pattern RAM, and of the audio block the APU claims (<c>0xFF10</c>-<c>0xFF3F</c>).</summary>
    public const ushort WaveRamEnd = 0xFF3F;
    /// <summary>The LCD control register (<c>LCDC</c>).</summary>
    public const ushort LcdControl = 0xFF40;
    /// <summary>The LCD status register (<c>STAT</c>).</summary>
    public const ushort LcdStatus = 0xFF41;
    /// <summary>The background scroll-Y register (<c>SCY</c>).</summary>
    public const ushort ScrollY = 0xFF42;
    /// <summary>The background scroll-X register (<c>SCX</c>).</summary>
    public const ushort ScrollX = 0xFF43;
    /// <summary>The LCD current-line register (<c>LY</c>); read-only.</summary>
    public const ushort LcdLine = 0xFF44;
    /// <summary>The LCD line-compare register (<c>LYC</c>).</summary>
    public const ushort LcdLineCompare = 0xFF45;
    /// <summary>The OAM DMA transfer-start register (<c>DMA</c>).</summary>
    public const ushort OamDmaStart = 0xFF46;
    /// <summary>The background palette register (<c>BGP</c>).</summary>
    public const ushort BackgroundPalette = 0xFF47;
    /// <summary>The object palette 0 register (<c>OBP0</c>).</summary>
    public const ushort ObjectPalette0 = 0xFF48;
    /// <summary>The object palette 1 register (<c>OBP1</c>).</summary>
    public const ushort ObjectPalette1 = 0xFF49;
    /// <summary>The window-Y register (<c>WY</c>).</summary>
    public const ushort WindowY = 0xFF4A;
    /// <summary>The window-X-plus-7 register (<c>WX</c>).</summary>
    public const ushort WindowX = 0xFF4B;
    /// <summary>The CGB speed-switch register (<c>KEY1</c>): bit&#160;7 reports current speed, bit&#160;0 arms a switch performed by <c>STOP</c>.</summary>
    public const ushort SpeedSwitch = 0xFF4D;
    /// <summary>The CGB video-RAM bank-select register (<c>VBK</c>).</summary>
    public const ushort VideoRamBank = 0xFF4F;
    /// <summary>The boot-ROM disable register (<c>BOOT</c>); a nonzero write unmaps the boot ROM permanently.</summary>
    public const ushort BootRomDisable = 0xFF50;
    /// <summary>The CGB work-RAM bank-select register (<c>SVBK</c>): selects work-RAM bank 1-7 at <c>0xD000</c>-<c>0xDFFF</c>.</summary>
    public const ushort WorkRamBank = 0xFF70;
    /// <summary>The interrupt-enable register (<c>IE</c>).</summary>
    public const ushort InterruptEnable = 0xFFFF;
}

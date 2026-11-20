namespace Puck.AdvancedGamingBrick;

/// <summary>The one map of the AGB's memory regions and memory-mapped I/O register addresses, for every piece of code
/// that names an address: the forge's emitters, the Post stages and their synthetic ROMs, and the core outside its own
/// offset decode.</summary>
/// <remarks>Each register is named for what it controls, with its GBATEK mnemonic in the summary. A 32-bit register's
/// upper half sits two bytes above its address.</remarks>
public static class AgbAddresses {
    /// <summary>EWRAM — the 256 KiB on-board work RAM.</summary>
    public const uint WorkRam = 0x02000000u;
    /// <summary>IWRAM — the 32 KiB on-chip work RAM.</summary>
    public const uint InternalWorkRam = 0x03000000u;
    /// <summary>The IWRAM word the BIOS interrupt dispatcher jumps through to the program's interrupt handler.</summary>
    public const uint BiosInterruptVector = 0x03007FFCu;
    /// <summary>The I/O page every register below sits in.</summary>
    public const uint IoBase = 0x04000000u;
    /// <summary>Palette RAM: 256 background colours, then 256 object colours, each BGR555.</summary>
    public const uint PaletteRam = 0x05000000u;
    /// <summary>VRAM; in mode 3 the 240×160 BGR555 framebuffer starts here.</summary>
    public const uint VideoRam = 0x06000000u;
    /// <summary>The object tile region of VRAM in the tiled modes.</summary>
    public const uint ObjectTileRam = 0x06010000u;
    /// <summary>OAM — the object attribute memory, eight bytes per object.</summary>
    public const uint ObjectAttributeRam = 0x07000000u;

    /// <summary>DISPCNT — the display control register.</summary>
    public const uint DisplayControl = 0x04000000u;
    /// <summary>GREENSWAP — the undocumented green-swap register.</summary>
    public const uint GreenSwap = 0x04000002u;
    /// <summary>DISPSTAT — the display status register; bit 0 is the V-blank flag.</summary>
    public const uint DisplayStatus = 0x04000004u;
    /// <summary>VCOUNT — the current scanline.</summary>
    public const uint VerticalCount = 0x04000006u;
    /// <summary>BG0CNT — background 0's control; BG1CNT through BG3CNT follow at two-byte steps.</summary>
    public const uint Background0Control = 0x04000008u;
    /// <summary>BG1CNT — background 1's control.</summary>
    public const uint Background1Control = 0x0400000Au;
    /// <summary>BG2CNT — background 2's control.</summary>
    public const uint Background2Control = 0x0400000Cu;
    /// <summary>BG3CNT — background 3's control.</summary>
    public const uint Background3Control = 0x0400000Eu;
    /// <summary>BG0HOFS — background 0's horizontal scroll; each background's horizontal and vertical scroll pair
    /// follows at four-byte steps.</summary>
    public const uint Background0HorizontalOffset = 0x04000010u;
    /// <summary>BG0VOFS — background 0's vertical scroll.</summary>
    public const uint Background0VerticalOffset = 0x04000012u;
    /// <summary>BG1HOFS — background 1's horizontal scroll.</summary>
    public const uint Background1HorizontalOffset = 0x04000014u;
    /// <summary>BG1VOFS — background 1's vertical scroll.</summary>
    public const uint Background1VerticalOffset = 0x04000016u;
    /// <summary>BG2HOFS — background 2's horizontal scroll.</summary>
    public const uint Background2HorizontalOffset = 0x04000018u;
    /// <summary>BG3HOFS — background 3's horizontal scroll.</summary>
    public const uint Background3HorizontalOffset = 0x0400001Cu;
    /// <summary>BG2PA — the first of background 2's four affine matrix halfwords (PA, PB, PC, PD).</summary>
    public const uint Background2Matrix = 0x04000020u;
    /// <summary>BG2X — background 2's affine reference point X, a 32-bit 20.8 fixed-point value.</summary>
    public const uint Background2ReferenceX = 0x04000028u;
    /// <summary>BG2Y — background 2's affine reference point Y.</summary>
    public const uint Background2ReferenceY = 0x0400002Cu;
    /// <summary>WIN0H — window 0's horizontal bounds.</summary>
    public const uint Window0Horizontal = 0x04000040u;
    /// <summary>WIN0V — window 0's vertical bounds.</summary>
    public const uint Window0Vertical = 0x04000044u;
    /// <summary>WININ — the layers shown inside windows 0 and 1.</summary>
    public const uint WindowInside = 0x04000048u;
    /// <summary>WINOUT — the layers shown outside the windows and inside the object window.</summary>
    public const uint WindowOutside = 0x0400004Au;
    /// <summary>BLDCNT — the colour special effect selection.</summary>
    public const uint BlendControl = 0x04000050u;
    /// <summary>BLDALPHA — the alpha blending coefficients.</summary>
    public const uint BlendAlpha = 0x04000052u;
    /// <summary>BLDY — the brightness coefficient.</summary>
    public const uint BlendBrightness = 0x04000054u;

    /// <summary>SOUND1CNT_L — tone channel 1's sweep.</summary>
    public const uint Sound1Sweep = 0x04000060u;
    /// <summary>SOUND1CNT_H — tone channel 1's duty, length and envelope.</summary>
    public const uint Sound1DutyEnvelope = 0x04000062u;
    /// <summary>SOUND1CNT_X — tone channel 1's frequency and control.</summary>
    public const uint Sound1Frequency = 0x04000064u;
    /// <summary>SOUND2CNT_L — tone channel 2's duty, length and envelope.</summary>
    public const uint Sound2DutyEnvelope = 0x04000068u;
    /// <summary>SOUND2CNT_H — tone channel 2's frequency and control.</summary>
    public const uint Sound2Frequency = 0x0400006Cu;
    /// <summary>SOUND3CNT_L — the wave channel's bank select and enable.</summary>
    public const uint Sound3Select = 0x04000070u;
    /// <summary>SOUND3CNT_H — the wave channel's length and volume.</summary>
    public const uint Sound3LengthVolume = 0x04000072u;
    /// <summary>SOUND3CNT_X — the wave channel's frequency and control.</summary>
    public const uint Sound3Frequency = 0x04000074u;
    /// <summary>SOUND4CNT_L — the noise channel's length and envelope.</summary>
    public const uint Sound4LengthEnvelope = 0x04000078u;
    /// <summary>SOUND4CNT_H — the noise channel's frequency and control.</summary>
    public const uint Sound4Frequency = 0x0400007Cu;
    /// <summary>SOUNDCNT_L — the tone channels' master volume and left/right enables.</summary>
    public const uint SoundControlLow = 0x04000080u;
    /// <summary>SOUNDCNT_H — the DMA sound channels' volume, routing and FIFO resets, and the tone mix ratio.</summary>
    public const uint SoundControlHigh = 0x04000082u;
    /// <summary>SOUNDCNT_X — the master sound enable and the channels' on flags.</summary>
    public const uint SoundControlEnable = 0x04000084u;
    /// <summary>SOUNDBIAS — the DAC bias level and amplitude resolution.</summary>
    public const uint SoundBias = 0x04000088u;
    /// <summary>WAVE_RAM — the wave channel's sixteen-byte pattern bank.</summary>
    public const uint WavePattern = 0x04000090u;
    /// <summary>FIFO_A — DMA sound channel A's sample queue.</summary>
    public const uint SoundFifoA = 0x040000A0u;
    /// <summary>FIFO_B — DMA sound channel B's sample queue.</summary>
    public const uint SoundFifoB = 0x040000A4u;

    /// <summary>DMA0SAD — DMA channel 0's source address.</summary>
    public const uint Dma0Source = 0x040000B0u;
    /// <summary>DMA0DAD — DMA channel 0's destination address.</summary>
    public const uint Dma0Destination = 0x040000B4u;
    /// <summary>DMA0CNT_L — DMA channel 0's unit count.</summary>
    public const uint Dma0Count = 0x040000B8u;
    /// <summary>DMA0CNT_H — DMA channel 0's control.</summary>
    public const uint Dma0Control = 0x040000BAu;
    /// <summary>DMA1SAD — DMA channel 1's source address.</summary>
    public const uint Dma1Source = 0x040000BCu;
    /// <summary>DMA1DAD — DMA channel 1's destination address.</summary>
    public const uint Dma1Destination = 0x040000C0u;
    /// <summary>DMA1CNT_L — DMA channel 1's unit count.</summary>
    public const uint Dma1Count = 0x040000C4u;
    /// <summary>DMA1CNT_H — DMA channel 1's control.</summary>
    public const uint Dma1Control = 0x040000C6u;
    /// <summary>DMA2CNT_H — DMA channel 2's control.</summary>
    public const uint Dma2Control = 0x040000D2u;
    /// <summary>DMA3SAD — DMA channel 3's source address.</summary>
    public const uint Dma3Source = 0x040000D4u;
    /// <summary>DMA3DAD — DMA channel 3's destination address.</summary>
    public const uint Dma3Destination = 0x040000D8u;
    /// <summary>DMA3CNT_L — DMA channel 3's unit count.</summary>
    public const uint Dma3Count = 0x040000DCu;
    /// <summary>DMA3CNT_H — DMA channel 3's control.</summary>
    public const uint Dma3Control = 0x040000DEu;

    /// <summary>TM0CNT_L — timer 0's counter on read and its reload value on write.</summary>
    public const uint Timer0Reload = 0x04000100u;
    /// <summary>TM0CNT_H — timer 0's control.</summary>
    public const uint Timer0Control = 0x04000102u;
    /// <summary>SIOMULTI0 — the serial port's 32-bit data, or player 0's multiplayer word.</summary>
    public const uint SerialData = 0x04000120u;
    /// <summary>SIOCNT — the serial port's control.</summary>
    public const uint SerialControl = 0x04000128u;
    /// <summary>KEYINPUT — the keypad, active-low: a clear bit is a pressed key.</summary>
    public const uint KeyInput = 0x04000130u;
    /// <summary>KEYCNT — the keypad interrupt control.</summary>
    public const uint KeyControl = 0x04000132u;
    /// <summary>RCNT — the serial port's mode select and general-purpose pins.</summary>
    public const uint SerialMode = 0x04000134u;
    /// <summary>IE — the interrupt enable register.</summary>
    public const uint InterruptEnable = 0x04000200u;
    /// <summary>IF — the interrupt request register; writing a set bit acknowledges that request.</summary>
    public const uint InterruptFlags = 0x04000202u;
    /// <summary>WAITCNT — the game-pak wait states and prefetch.</summary>
    public const uint WaitControl = 0x04000204u;
    /// <summary>IME — the interrupt master enable.</summary>
    public const uint InterruptMasterEnable = 0x04000208u;
    /// <summary>POSTFLG — the power-control byte pair whose upper byte is HALTCNT: a byte write of zero there halts the
    /// processor until an enabled interrupt is requested (IE &amp; IF), whatever IME holds.</summary>
    public const uint PowerControl = 0x04000300u;
}

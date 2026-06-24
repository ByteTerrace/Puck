namespace Puck.HumbleGamingBrick;

/// <summary>Specifies which clock a component follows, which fixes how many T-cycles it advances per CPU
/// machine cycle. On the DMG both domains run at the same 4.194304&#160;MHz rate (4 T-cycles per machine
/// cycle). On the CGB in double-speed mode the CPU clock doubles, so a machine cycle spans only 2 LCD-domain
/// T-cycles while still spanning 4 CPU-domain T-cycles.</summary>
public enum ClockDomain {
    /// <summary>The CPU/system clock: the CPU core, the timer and divider, OAM DMA, and the serial port. Always
    /// 4 T-cycles per machine cycle regardless of speed mode.</summary>
    Cpu = 0,
    /// <summary>The LCD/audio clock: the PPU and the APU. 4 T-cycles per machine cycle at normal speed, 2 under
    /// CGB double-speed.</summary>
    Lcd = 1,
}

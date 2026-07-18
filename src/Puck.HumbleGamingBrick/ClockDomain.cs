namespace Puck.HumbleGamingBrick.Timing;

/// <summary>
/// Identifies which of the console's two clocks a component is wired to. On the colour brick the CPU clock can be
/// switched to twice the LCD/audio clock, so a single master-timeline duration corresponds to a different number of
/// each domain's cycles. Tagging a component with its domain lets the timing core scale its sub-cycle advances without
/// the component needing to know the current speed mode.
/// </summary>
public enum ClockDomain {
    /// <summary>The CPU/system clock that drives the processor core, the divider and timer, OAM DMA, and the serial
    /// port. Its rate doubles in Color double-speed mode.</summary>
    Cpu = 0,
    /// <summary>The LCD/audio clock that drives the PPU and the APU. Its rate is fixed regardless of speed mode, so one
    /// LCD cycle always equals one master-timeline T-cycle.</summary>
    Lcd = 1,
}

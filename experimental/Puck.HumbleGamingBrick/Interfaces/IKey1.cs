namespace Puck.HumbleGamingBrick.Interfaces;

/// <summary>
/// The Color speed-switch register (KEY1 at <c>0xFF4D</c>) and the machine's STOP/speed-switch state. Software arms a
/// switch by setting the low bit, then executes STOP to perform it; the CPU calls <see cref="BeginSwitch"/> and stalls
/// while <see cref="IsSwitching"/> holds, syncing the component clock to <see cref="IsDoubleSpeed"/> each T-cycle (the
/// speed flips a few cycles into the stall). During the re-gear the unit raises block windows that the timer, the CPU's
/// interrupt dispatch, and the VRAM DMA honour. STOP without an armed switch instead parks the machine in stop mode
/// (<see cref="IsStopped"/>): DIV resets, the CPU-domain peripherals freeze, and the PPU blanks until a button wakes it.
/// </summary>
public interface IKey1 {
    /// <summary>Gets whether software has armed a speed switch (KEY1 bit 0), so the next STOP performs it.</summary>
    bool IsSwitchArmed { get; }
    /// <summary>Gets the current speed (KEY1 bit 7): <see langword="true"/> for double speed. The CPU reads this to drive
    /// the component clock during a switch or after a snapshot restore.</summary>
    bool IsDoubleSpeed { get; }
    /// <summary>Gets whether a speed-switch stall is in progress; the CPU stays stalled while this holds.</summary>
    bool IsSwitching { get; }
    /// <summary>Gets whether the machine is parked in stop mode (STOP without an armed switch).</summary>
    bool IsStopped { get; }
    /// <summary>Gets whether the switch's interrupt-block window is open; the CPU neither dispatches interrupts nor
    /// aborts the stall while it holds.</summary>
    bool AreInterruptsBlocked { get; }
    /// <summary>Gets whether the switch's timer-block window is open; the divider/timer freezes while it holds and
    /// reseeds DIV as it closes.</summary>
    bool AreTimersBlocked { get; }
    /// <summary>Gets whether the switch's DMA-block window is open; the VRAM DMA unit pauses while it holds.</summary>
    bool IsHdmaBlocked { get; }

    /// <summary>Reads KEY1: bit 7 = current speed (1 = double), bit 0 = armed, the rest read as 1.</summary>
    /// <returns>The register value.</returns>
    byte ReadRegister();
    /// <summary>Writes KEY1; only the arm bit (bit 0) is writable.</summary>
    /// <param name="value">The value being written.</param>
    void WriteRegister(byte value);
    /// <summary>Starts the armed speed switch: opens the stall and the block windows. The speed itself flips a couple of
    /// machine cycles in, as the unit ticks.</summary>
    void BeginSwitch();
    /// <summary>Aborts the stall early — a pending interrupt woke the CPU — releasing the block windows.</summary>
    void CancelSwitch();
    /// <summary>Parks the machine in stop mode.</summary>
    void EnterStop();
    /// <summary>Wakes the machine from stop mode (a button was pressed).</summary>
    void LeaveStop();
}

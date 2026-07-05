using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The Color double-speed switch (KEY1) and stop-mode state, a CPU-domain clocked component. A switch happens only when
/// the CPU executes STOP with the switch armed: the CPU calls <see cref="BeginSwitch"/> and stalls while
/// <see cref="IsSwitching"/> holds; this unit's per-tick countdowns flip the speed two machine cycles in, run the
/// hardware-measured stall (about two frames), and open the block windows the timer, the CPU's interrupt dispatch, and
/// the VRAM DMA honour while the clock re-gears. The unit owns the double-speed flag as snapshot state but does NOT
/// touch the timing substrate itself: the CPU reads <see cref="IsDoubleSpeed"/> and drives the component clock — keeping
/// the clock out of this unit's dependencies is deliberate, since the bus depends on KEY1. Stop mode (STOP without an
/// armed switch) is plain state the CPU and the frozen peripherals consult. All countdowns are in this unit's own
/// T-cycles (four per machine cycle), so double speed is handled by the domain ticking twice per dot.
/// </summary>
public sealed class Key1Component : IKey1, IClockedComponent, ISnapshotable {
    // The measured stall lengths, in machine cycles: 16386 * 2 to double speed and 16384 * 4 + 2 back to normal —
    // roughly two frames either way — converted to this unit's T-cycles (x4).
    private const int StallToDoubleTCycles = (16386 * 2) * 4;
    private const int StallToNormalTCycles = ((16384 * 4) + 2) * 4;
    // The speed itself flips two machine cycles into the stall.
    private const int SwitchEnterTCycles = 2 * 4;
    // The block windows, in machine cycles from the start of the switch (x4 for T-cycles): interrupts are blocked for
    // three (with an early release when a line is already pending near the end), the timer for four cycles switching up
    // and two switching down, and the VRAM DMA toggles blocked after three (up) or one (down) and unblocks two cycles
    // after the stall ends.
    private const int InterruptBlockTCycles = 3 * 4;
    private const int InterruptBlockEarlyReleaseTCycles = 1 * 4;
    private const int TimerBlockToDoubleTCycles = 4 * 4;
    private const int TimerBlockToNormalTCycles = 2 * 4;
    private const int HdmaToggleToDoubleTCycles = 3 * 4;
    private const int HdmaToggleToNormalTCycles = 1 * 4;
    private const int HdmaUnblockTCycles = 2 * 4;

    private readonly IInterruptController m_interrupts;

    private bool m_armed;
    private bool m_hdmaBlocked;
    private int m_hdmaToggleCountdown;
    private int m_interruptBlockCountdown;
    private bool m_interruptsBlocked;
    private bool m_isDoubleSpeed;
    private bool m_stopped;
    private int m_switchEnterCountdown;
    private int m_switchStallCountdown;
    private int m_timerBlockCountdown;
    private bool m_timersBlocked;

    /// <summary>Creates the unit wired to the interrupt controller whose pending lines release the switch's
    /// interrupt-block window early.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interrupts"/> is <see langword="null"/>.</exception>
    public Key1Component(IInterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(argument: interrupts);

        m_interrupts = interrupts;
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <inheritdoc/>
    public bool IsSwitchArmed =>
        m_armed;
    /// <inheritdoc/>
    public bool IsDoubleSpeed =>
        m_isDoubleSpeed;
    /// <inheritdoc/>
    public bool IsSwitching =>
        (m_switchStallCountdown > 0);
    /// <inheritdoc/>
    public bool IsStopped =>
        m_stopped;
    /// <inheritdoc/>
    public bool AreInterruptsBlocked =>
        m_interruptsBlocked;
    /// <inheritdoc/>
    public bool AreTimersBlocked =>
        m_timersBlocked;
    /// <inheritdoc/>
    public bool IsHdmaBlocked =>
        m_hdmaBlocked;

    /// <inheritdoc/>
    public void Tick() {
        if (m_switchEnterCountdown > 0) {
            if (--m_switchEnterCountdown == 0) {
                m_isDoubleSpeed = !m_isDoubleSpeed;
                m_armed = false;
            }
        }

        if (m_switchStallCountdown > 0) {
            if (--m_switchStallCountdown == 0) {
                // The stall ran to completion; the DMA block releases a couple of machine cycles later.
                m_hdmaToggleCountdown = HdmaUnblockTCycles;
            }
        }

        if (m_hdmaToggleCountdown > 0) {
            if (--m_hdmaToggleCountdown == 0) {
                m_hdmaBlocked = !m_hdmaBlocked;
            }
        }

        if (m_interruptBlockCountdown > 0) {
            --m_interruptBlockCountdown;

            // A line already pending near the end of the window releases it early.
            if ((m_interruptBlockCountdown <= InterruptBlockEarlyReleaseTCycles) && (m_interrupts.Pending != InterruptKind.None)) {
                m_interruptBlockCountdown = 0;
            }

            if (m_interruptBlockCountdown == 0) {
                m_interruptsBlocked = false;
            }
        }

        if (m_timerBlockCountdown > 0) {
            if (--m_timerBlockCountdown == 0) {
                m_timersBlocked = false;
            }
        }
    }
    /// <inheritdoc/>
    public byte ReadRegister() =>
        (byte)(0x7E | (m_armed ? 0x01 : 0x00) | (m_isDoubleSpeed ? 0x80 : 0x00));
    /// <inheritdoc/>
    public void WriteRegister(byte value) =>
        m_armed = ((value & 0x01) != 0);
    /// <inheritdoc/>
    public void BeginSwitch() {
        var toDouble = !m_isDoubleSpeed;

        m_switchEnterCountdown = SwitchEnterTCycles;
        m_switchStallCountdown = toDouble ? StallToDoubleTCycles : StallToNormalTCycles;
        m_timersBlocked = true;

        if (toDouble) {
            m_interruptsBlocked = true;
            m_interruptBlockCountdown = InterruptBlockTCycles;
            m_timerBlockCountdown = TimerBlockToDoubleTCycles;
            m_hdmaToggleCountdown = HdmaToggleToDoubleTCycles;
        }
        else {
            m_timerBlockCountdown = TimerBlockToNormalTCycles;
            m_hdmaToggleCountdown = HdmaToggleToNormalTCycles;
        }
    }
    /// <inheritdoc/>
    public void CancelSwitch() {
        m_switchStallCountdown = 0;
        m_hdmaBlocked = false;
        m_hdmaToggleCountdown = 0;
    }
    /// <inheritdoc/>
    public void ForceNormalSpeed() {
        // The live-swap demote to monochrome: drop double speed and tear down EVERY in-flight switch/block window so no
        // stale countdown re-flips speed or re-opens a block on a later Tick. The caller re-syncs the component clock's
        // own double-speed copy (which this unit deliberately does not own). Mono hardware has no KEY1, so nothing here
        // can re-arm afterward.
        m_armed = false;
        m_isDoubleSpeed = false;
        m_switchEnterCountdown = 0;
        m_switchStallCountdown = 0;
        m_interruptsBlocked = false;
        m_interruptBlockCountdown = 0;
        m_timersBlocked = false;
        m_timerBlockCountdown = 0;
        m_hdmaBlocked = false;
        m_hdmaToggleCountdown = 0;
    }
    /// <inheritdoc/>
    public void EnterStop() =>
        m_stopped = true;
    /// <inheritdoc/>
    public void LeaveStop() =>
        m_stopped = false;
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBoolean(value: m_armed);
        writer.WriteBoolean(value: m_isDoubleSpeed);
        writer.WriteBoolean(value: m_stopped);
        writer.WriteInt32(value: m_switchEnterCountdown);
        writer.WriteInt32(value: m_switchStallCountdown);
        writer.WriteBoolean(value: m_interruptsBlocked);
        writer.WriteInt32(value: m_interruptBlockCountdown);
        writer.WriteBoolean(value: m_timersBlocked);
        writer.WriteInt32(value: m_timerBlockCountdown);
        writer.WriteBoolean(value: m_hdmaBlocked);
        writer.WriteInt32(value: m_hdmaToggleCountdown);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_armed = reader.ReadBoolean();
        m_isDoubleSpeed = reader.ReadBoolean();
        m_stopped = reader.ReadBoolean();
        m_switchEnterCountdown = reader.ReadInt32();
        m_switchStallCountdown = reader.ReadInt32();
        m_interruptsBlocked = reader.ReadBoolean();
        m_interruptBlockCountdown = reader.ReadInt32();
        m_timersBlocked = reader.ReadBoolean();
        m_timerBlockCountdown = reader.ReadInt32();
        m_hdmaBlocked = reader.ReadBoolean();
        m_hdmaToggleCountdown = reader.ReadInt32();
    }
}

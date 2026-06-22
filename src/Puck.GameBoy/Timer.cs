namespace Puck.GameBoy;

/// <summary>
/// The timer and divider. A single 16-bit counter increments every CPU-domain T-cycle; its high byte is the
/// divider register (<c>DIV</c>, <c>0xFF04</c>), and writing <c>DIV</c> resets the whole counter. The timer
/// counter (<c>TIMA</c>, <c>0xFF05</c>) increments on the <em>falling edge</em> of a selected counter bit ANDed
/// with the enable bit of <c>TAC</c> (<c>0xFF07</c>) — modelling the increment as an edge detector (rather than
/// a periodic reset) is what reproduces the hardware quirks where writing <c>DIV</c> or <c>TAC</c> at the wrong
/// moment ticks <c>TIMA</c>. On overflow, <c>TIMA</c> reads zero for one machine cycle before it is reloaded
/// from the modulo (<c>TMA</c>, <c>0xFF06</c>) and the timer interrupt is raised.
/// </summary>
public sealed class Timer : IClockedComponent {
    private const int ReloadDelayTCycles = 4;

    private readonly InterruptController m_interrupts;

    private bool m_lastTimerSignal;
    private bool m_reloadedThisCycle;
    private ushort m_internalCounter;
    private int m_reloadDelay;
    private byte m_timerControl;
    private byte m_timerCounter;
    private byte m_timerModulo;

    /// <inheritdoc />
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <summary>Gets the 16-bit internal counter (the system divider whose high byte is <c>DIV</c>). Other clocks
    /// (the serial shift clock, and later the APU frame sequencer) are divided from this same counter, so they
    /// stay phase-aligned to it and to <c>DIV</c> resets.</summary>
    public int InternalCounter =>
        m_internalCounter;

    /// <summary>Initializes the timer wired to the interrupt controller it raises the timer interrupt through.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interrupts"/> is <see langword="null"/>.</exception>
    public Timer(InterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(interrupts);

        m_interrupts = interrupts;
    }

    /// <summary>Seeds the 16-bit internal counter (whose high byte is <c>DIV</c>), establishing the post-boot
    /// divider phase. The edge-detector baseline is resynchronized to the seeded counter so the seed itself
    /// never fabricates a falling edge (and thus never spuriously ticks <c>TIMA</c>).</summary>
    /// <param name="value">The internal counter value.</param>
    public void SetInternalCounter(ushort value) {
        m_internalCounter = value;
        m_lastTimerSignal = TimerSignal();
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        // Cleared at the start of each machine cycle's stepping; set if the reload lands this cycle, so a
        // coincident register write (resolved by the bus after this Step) can honor the reload precedence.
        m_reloadedThisCycle = false;

        for (var index = 0; index < tCycles; index += 1) {
            // The reload is resolved at the start of the cycle so a TIMA read during the delay window sees zero.
            if (m_reloadDelay > 0) {
                m_reloadDelay -= 1;

                if (m_reloadDelay == 0) {
                    m_timerCounter = m_timerModulo;
                    m_reloadedThisCycle = true;
                    m_interrupts.Request(kind: InterruptKind.Timer);
                }
            }

            m_internalCounter += 1;
            UpdateTimerSignal();
        }
    }

    /// <summary>Reads one of the timer's registers (<c>0xFF04</c>–<c>0xFF07</c>).</summary>
    /// <param name="address">The register address.</param>
    /// <returns>The register value with hardware read-as-one bits applied.</returns>
    public byte ReadRegister(ushort address) =>
        address switch {
            MemoryMap.Divider => (byte)(m_internalCounter >> 8),
            MemoryMap.TimerCounter => m_timerCounter,
            MemoryMap.TimerModulo => m_timerModulo,
            MemoryMap.TimerControl => (byte)(0xF8 | m_timerControl),
            _ => 0xFF,
        };
    /// <summary>Writes one of the timer's registers (<c>0xFF04</c>–<c>0xFF07</c>).</summary>
    /// <param name="address">The register address.</param>
    /// <param name="value">The value written.</param>
    public void WriteRegister(ushort address, byte value) {
        switch (address) {
            case MemoryMap.Divider:
                // Any write clears the whole counter; the resulting drop of the selected bit can tick TIMA.
                m_internalCounter = 0;
                UpdateTimerSignal();

                break;
            case MemoryMap.TimerCounter:
                // A TIMA write on the exact reload cycle is ignored (the TMA reload wins); a write during the
                // delay window before then aborts the pending reload and interrupt.
                if (!m_reloadedThisCycle) {
                    m_timerCounter = value;
                    m_reloadDelay = 0;
                }

                break;
            case MemoryMap.TimerModulo:
                m_timerModulo = value;

                // A TMA write on the reload cycle also lands in the just-reloaded TIMA.
                if (m_reloadedThisCycle) {
                    m_timerCounter = value;
                }

                break;
            case MemoryMap.TimerControl:
                m_timerControl = (byte)(value & 0x07);
                // A change to the selected bit or the enable bit can itself produce a falling edge.
                UpdateTimerSignal();

                break;
            default:
                break;
        }
    }

    private void UpdateTimerSignal() {
        var signal = TimerSignal();

        if (m_lastTimerSignal && !signal) {
            IncrementTimerCounter();
        }

        m_lastTimerSignal = signal;
    }
    private bool TimerSignal() {
        // The increment is gated by the enable bit; the divider bit selected by TAC's low two bits feeds the edge
        // detector (period 1024/16/64/256 T-cycles for select 0/1/2/3).
        var selectedBit = (m_timerControl & 0x03) switch {
            0 => 9,
            1 => 3,
            2 => 5,
            _ => 7,
        };

        return (((m_timerControl & 0x04) != 0) && (((m_internalCounter >> selectedBit) & 1) != 0));
    }
    private void IncrementTimerCounter() {
        if (m_timerCounter == 0xFF) {
            // Overflow: TIMA reads zero until the reload lands a machine cycle later.
            m_timerCounter = 0x00;
            m_reloadDelay = ReloadDelayTCycles;
        }
        else {
            m_timerCounter += 1;
        }
    }
}

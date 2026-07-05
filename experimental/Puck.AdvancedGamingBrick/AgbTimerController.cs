namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The four 16-bit timers, modelled one-for-one on the cycle-stepped hardware reference. Each timer is a
/// real per-cycle state machine: a prescaler-driven timer steps its counter when the global clock's low bits hit
/// the prescaler boundary; a count-up (cascade) timer steps only when the timer below it overflows — synchronously,
/// in the same cycle, so a chain can ripple timer0→timer1→timer2 at once. Control and reload writes are latched
/// and applied one cycle later (a fresh enable reloads the counter the cycle after that), which is the true source
/// of the hardware's start-up latency — no <c>(now+2)</c> heuristic and no lazy <c>(now-last)&gt;&gt;prescale</c>
/// reconstruction. <see cref="RunCycle"/> is the per-cycle driver the bus calls as it charges each access.
/// </summary>
public sealed class AgbTimerController : IAgbTimerController {
    // Prescaler period mask per the 2-bit control field: divide by 1 / 64 / 256 / 1024.
    private static readonly long[] s_mask = { 0, 63, 255, 1023 };

    private readonly IAgbInterruptController m_interrupts;
    private readonly IAgbApu m_apu;

    // Live timer state (post-latch). period is the 16-bit counter games read back.
    private readonly int[] m_period = new int[4];
    private readonly int[] m_reload = new int[4];
    private readonly int[] m_frequency = new int[4];
    private readonly bool[] m_enable = new bool[4];
    private readonly bool[] m_irqEnabled = new bool[4];
    private readonly bool[] m_cascade = new bool[4];
    private readonly bool[] m_pending = new bool[4];

    // Latched (deferred-by-one-cycle) control/reload writes, applied by StepLatch when m_timerLatched is set.
    private readonly bool[] m_controlFlag = new bool[4];
    private readonly int[] m_latchControl = new int[4];
    private readonly int[] m_reloadFlags = new int[4]; // bit0 = low byte pending, bit1 = high byte pending
    private readonly int[] m_latchReload = new int[4];
    private bool m_timerLatched;

    private bool m_anyRunning;

    /// <summary>Creates the timer block bound to the interrupt controller it signals and the APU it clocks for
    /// Direct Sound.</summary>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AgbTimerController(IAgbInterruptController interrupts, IAgbApu apu) {
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(apu);

        m_interrupts = interrupts;
        m_apu = apu;
    }

    /// <inheritdoc/>
    public bool HasRunningTimer => m_anyRunning;

    /// <inheritdoc/>
    public bool HasPendingLatch => m_timerLatched || m_pending[0] || m_pending[1] || m_pending[2] || m_pending[3];

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) {
        var timer = (int)((offset - 0x100u) >> 2);

        if ((offset & 0x2u) == 0u) {
            // CNT_L: the live counter, current because it is stepped every cycle.
            return (ushort)m_period[timer];
        }

        // CNT_H: the committed control fields.
        return (ushort)(PrescaleField(frequency: m_frequency[timer])
            | (m_cascade[timer] ? 0x4u : 0u)
            | (m_irqEnabled[timer] ? 0x40u : 0u)
            | (m_enable[timer] ? 0x80u : 0u));
    }

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value) {
        var timer = (int)((offset - 0x100u) >> 2);

        if ((offset & 0x2u) == 0u) {
            // CNT_L write: latch both reload bytes; applied next cycle.
            m_latchReload[timer] = value;
            m_reloadFlags[timer] = 0b11;
        }
        else {
            // CNT_H write: latch the control word; applied next cycle.
            m_latchControl[timer] = value & 0xFF;
            m_controlFlag[timer] = true;
        }

        m_timerLatched = true;
    }

    /// <inheritdoc/>
    public void RunCycle(long clock) {
        // Per the cycle-stepped hardware reference: run all four, then reload-latch all four, then (if a write is
        // pending) commit the control/reload latch. IRQ stepping is handled separately by the interrupt controller.
        Run(timer: 0, clock: clock);
        Run(timer: 1, clock: clock);
        Run(timer: 2, clock: clock);
        Run(timer: 3, clock: clock);

        ReloadLatch(timer: 0);
        ReloadLatch(timer: 1);
        ReloadLatch(timer: 2);
        ReloadLatch(timer: 3);

        if (m_timerLatched) {
            StepLatch(timer: 0);
            StepLatch(timer: 1);
            StepLatch(timer: 2);
            StepLatch(timer: 3);
            m_timerLatched = false;
        }
    }

    // Timer run: a prescaler-driven timer steps when the global clock hits its boundary.
    private void Run(int timer, long clock) {
        if (!m_enable[timer] || m_cascade[timer]) {
            return;
        }

        if ((clock & s_mask[m_frequency[timer]]) == 0L) {
            Step(timer: timer);
        }
    }

    // Timer step: increment; on overflow reload, raise the IRQ, clock Direct Sound, and
    // cascade synchronously into the timer above.
    private void Step(int timer) {
        m_period[timer] = (m_period[timer] + 1) & 0xFFFF;

        if (m_period[timer] != 0) {
            return;
        }

        m_period[timer] = m_reload[timer];

        if (m_irqEnabled[timer]) {
            m_interrupts.Request(source: (InterruptSource)((int)InterruptSource.Timer0 + timer));
        }

        if (timer < 2) {
            m_apu.OnTimerOverflow(timer: timer);
        }

        if ((timer < 3) && m_enable[timer + 1] && m_cascade[timer + 1]) {
            Step(timer: timer + 1);
        }
    }

    // Reload-latch: apply a pending enable reload one cycle after it was armed.
    private void ReloadLatch(int timer) {
        if (m_pending[timer]) {
            m_period[timer] = m_reload[timer];
            m_pending[timer] = false;
        }
    }

    // Step-latch: commit latched reload bytes and the latched control word; a fresh
    // 0→1 enable arms the reload that ReloadLatch applies on the following cycle.
    private void StepLatch(int timer) {
        if ((m_reloadFlags[timer] & 0b01) != 0) {
            m_reload[timer] = (m_reload[timer] & 0xFF00) | (m_latchReload[timer] & 0x00FF);
            m_reloadFlags[timer] &= 0b10;
        }

        if ((m_reloadFlags[timer] & 0b10) != 0) {
            m_reload[timer] = (m_reload[timer] & 0x00FF) | (m_latchReload[timer] & 0xFF00);
            m_reloadFlags[timer] &= 0b01;
        }

        if (m_controlFlag[timer]) {
            var wasEnabled = m_enable[timer];
            var control = m_latchControl[timer];

            m_frequency[timer] = control & 0x3;
            m_irqEnabled[timer] = (control & 0x40) != 0;
            m_enable[timer] = (control & 0x80) != 0;

            if (timer != 0) {
                m_cascade[timer] = (control & 0x4) != 0;
            }

            if (!wasEnabled && m_enable[timer]) {
                m_pending[timer] = true;
            }

            m_controlFlag[timer] = false;
            RefreshRunning();
        }
    }

    private void RefreshRunning() {
        m_anyRunning =
            (m_enable[0] && !m_cascade[0])
            || (m_enable[1] && !m_cascade[1])
            || (m_enable[2] && !m_cascade[2])
            || (m_enable[3] && !m_cascade[3]);
    }

    private static uint PrescaleField(int frequency) => frequency switch {
        1 => 1u,
        2 => 2u,
        3 => 3u,
        _ => 0u,
    };
}

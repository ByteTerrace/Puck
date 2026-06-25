namespace Puck.GameBoyAdvance;

/// <summary>
/// The four 16-bit timers, modelled exactly on mGBA's <c>timer.c</c>: a prescaler-driven timer is not stepped
/// every cycle but computed lazily from the scheduler clock — its live count is reconstructed from the time
/// elapsed since it was last touched, and its overflow is a single scheduled event. Count-up (cascade) timers
/// advance only when the timer below them overflows. This gives cycle-exact timer reads (which games such as
/// Pokémon Emerald busy-poll) without per-cycle work.
/// </summary>
public sealed class GbaTimerController : IGbaTimerController {
    // Prescaler shift per the 2-bit control field: 1 / 64 / 256 / 1024 = shifts of 0 / 6 / 8 / 10.
    private static readonly int[] s_prescaleBits = { 0, 6, 8, 10 };

    private readonly GbaScheduler m_scheduler;
    private readonly IGbaInterruptController m_interrupts;
    private readonly IGbaApu m_apu;

    private readonly int[] m_counter = new int[4];   // the live IO counter value (brought current lazily)
    private readonly int[] m_reload = new int[4];
    private readonly long[] m_lastEvent = new long[4]; // prescaler-aligned time the counter was last updated
    private readonly int[] m_prescaleBits = new int[4];
    private readonly bool[] m_enabled = new bool[4];
    private readonly bool[] m_countUp = new bool[4];
    private readonly bool[] m_irqEnabled = new bool[4];
    private readonly GbaScheduler.Event[] m_event = new GbaScheduler.Event[4];

    /// <summary>Creates the timer block bound to the scheduler it schedules overflows on, the interrupt controller
    /// it signals, and the APU it clocks for Direct Sound.</summary>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GbaTimerController(GbaScheduler scheduler, IGbaInterruptController interrupts, IGbaApu apu) {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(apu);

        m_scheduler = scheduler;
        m_interrupts = interrupts;
        m_apu = apu;

        for (var i = 0; i < 4; ++i) {
            var timer = i;

            m_event[i] = new GbaScheduler.Event { Callback = late => Overflow(timer: timer, cyclesLate: late) };
        }
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) {
        var timer = (int)((offset - 0x100u) >> 2);

        if ((offset & 0x2u) == 0u) {
            UpdateRegister(timer: timer, cyclesLate: 0);

            return (ushort)m_counter[timer];
        }

        return (ushort)(PrescaleField(bits: m_prescaleBits[timer])
            | (m_countUp[timer] ? 0x4u : 0u)
            | (m_irqEnabled[timer] ? 0x40u : 0u)
            | (m_enabled[timer] ? 0x80u : 0u));
    }

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value) {
        var timer = (int)((offset - 0x100u) >> 2);

        if ((offset & 0x2u) == 0u) {
            m_reload[timer] = value;

            return;
        }

        WriteControl(timer: timer, control: value);
    }

    // Brings the live counter current from the elapsed (prescaler-aligned) time, then reschedules the overflow.
    private void UpdateRegister(int timer, int cyclesLate) {
        if (!m_enabled[timer] || m_countUp[timer]) {
            return;
        }

        var prescaleBits = m_prescaleBits[timer];
        var tickMask = (1L << prescaleBits) - 1L;
        var currentTime = (m_scheduler.Now - cyclesLate) & ~tickMask;

        var ticks = (currentTime - m_lastEvent[timer]) >> prescaleBits;

        m_lastEvent[timer] = currentTime;
        ticks += m_counter[timer];

        while (ticks >= 0x10000) {
            ticks -= 0x10000 - m_reload[timer];
        }

        m_counter[timer] = (int)ticks;

        // Schedule the next overflow at the (aligned) time the counter will reach 0x10000.
        var untilOverflow = (0x10000L - ticks) << prescaleBits;

        m_scheduler.ScheduleAbsolute(e: m_event[timer], when: (currentTime + untilOverflow) & ~tickMask);
    }

    private void WriteControl(int timer, ushort control) {
        UpdateRegister(timer: timer, cyclesLate: 0);

        var prescaleBits = s_prescaleBits[control & 0x3u];
        var wasEnabled = m_enabled[timer];
        var wasCountUp = m_countUp[timer];
        var oldPrescale = m_prescaleBits[timer];

        m_prescaleBits[timer] = prescaleBits;
        m_countUp[timer] = (timer > 0) && ((control & 0x4u) != 0u);
        m_irqEnabled[timer] = (control & 0x40u) != 0u;
        m_enabled[timer] = (control & 0x80u) != 0u;

        var reschedule = false;

        if (wasEnabled != m_enabled[timer]) {
            reschedule = true;

            if (m_enabled[timer]) {
                m_counter[timer] = m_reload[timer];
            }
        }
        else if (wasCountUp != m_countUp[timer]) {
            reschedule = true;
        }
        else if (oldPrescale != prescaleBits) {
            reschedule = true;
        }

        if (!reschedule) {
            return;
        }

        m_scheduler.Deschedule(e: m_event[timer]);

        if (m_enabled[timer] && !m_countUp[timer]) {
            var tickMask = (1L << prescaleBits) - 1L;

            if (!wasEnabled) {
                // Fresh 0→1 enable: offset m_lastEvent by 2 cycles to account for (1) the I/O write
                // bus access charged AFTER WriteRegion returns and (2) the GBA timer control latch that
                // processes the enable flag one cycle after the write commits (Ares: stepLatch delays).
                // Skip the immediate UpdateRegister — calling it with currentTime < m_lastEvent would
                // compute negative ticks and corrupt m_counter. Schedule the overflow directly instead.
                m_lastEvent[timer] = (m_scheduler.Now + 2L) & ~tickMask;
                var untilOverflow = (0x10000L - m_counter[timer]) << prescaleBits;
                m_scheduler.ScheduleAbsolute(e: m_event[timer], when: (m_lastEvent[timer] + untilOverflow) & ~tickMask);
            }
            else {
                // Prescaler or cascade-mode change while already running: bring the counter current
                // from the old prescaler, then reschedule using the new one.
                m_lastEvent[timer] = m_scheduler.Now & ~tickMask;
                UpdateRegister(timer: timer, cyclesLate: 0);
            }
        }
    }

    // The scheduled-overflow callback: reload, raise the IRQ, clock Direct Sound, and cascade into a count-up
    // timer above. A prescaler-driven timer recomputes (which reschedules the next overflow).
    private void Overflow(int timer, int cyclesLate) {
        if (m_countUp[timer]) {
            m_counter[timer] = m_reload[timer];
        }
        else {
            UpdateRegister(timer: timer, cyclesLate: cyclesLate);
        }

        if (m_irqEnabled[timer]) {
            m_interrupts.Request(source: (InterruptSource)((int)InterruptSource.Timer0 + timer));
        }

        if (timer < 2) {
            m_apu.OnTimerOverflow(timer: timer);
        }

        if ((timer < 3) && m_countUp[timer + 1] && m_enabled[timer + 1]) {
            m_counter[timer + 1] = (m_counter[timer + 1] + 1) & 0xFFFF;

            if (m_counter[timer + 1] == 0) {
                Overflow(timer: timer + 1, cyclesLate: cyclesLate);
            }
        }
    }

    private static uint PrescaleField(int bits) => bits switch {
        6 => 1u,
        8 => 2u,
        10 => 3u,
        _ => 0u,
    };
}

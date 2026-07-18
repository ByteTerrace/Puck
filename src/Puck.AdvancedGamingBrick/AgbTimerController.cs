namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The four 16-bit timers. A prescaler-driven timer's counter is a closed form of the master clock — it steps once
/// per clock that hits the prescaler boundary (<c>(clock &amp; mask) == 0</c>, GLOBAL alignment, not enable-relative) —
/// so in steady state the block runs event-scheduled: a per-timer anchor (<see cref="m_anchorClock"/>,
/// <see cref="m_anchorValue"/>) records the counter's last materialized value, CNT_L reads reconstruct the live
/// counter from it, and each timer's next overflow is queued on the shared scheduler at its exact cycle. The bus
/// then collapses whole spans between overflows to a single clock advance instead of stepping every cycle.
/// <para>
/// The ≤2-cycle latch/IRQ windows stay per-cycle. A control/reload write latches and commits one and two cycles
/// later (a fresh enable reloads the counter the cycle after that); an overflow arms the IRQ request a fixed two
/// cycles later. The bus drives those windows through <see cref="RunCycle"/> — the same per-cycle state machine the
/// hardware reference models — bracketing them with <see cref="EnsurePerCycle"/> (materialize the closed-form
/// counter, drop the scheduled overflow) on entry and <see cref="EnsureScheduled"/> (re-anchor, re-queue the
/// overflow) on exit. Count-up (cascade) timers never step on the clock: they advance only inside the overflow of
/// the timer below, so their counter is always materialized and read directly.
/// </para>
/// </summary>
public sealed partial class AgbTimerController : IAgbTimerController {
    // Prescaler period mask per the 2-bit control field: divide by 1 / 64 / 256 / 1024.
    private static readonly long[] s_mask = { 0, 63, 255, 1023 };

    // log2 of the prescaler period (1 / 64 / 256 / 1024), so a clock >> s_shift is that clock's step index.
    private static readonly int[] s_shift = { 0, 6, 8, 10 };

    // Overflow→IRQ-request latency: the overflow's interrupt flag is not asserted the same cycle the counter wraps —
    // the signal takes this many master cycles to reach the interrupt controller. Layered on top of the interrupt
    // block's own register-visibility/recognition pipeline, it is what a timer-IRQ conformance suite measures as the
    // handler-visible reload delay (a fixed 2-region pattern of read-back values by nop distance). Timer IRQs only —
    // the reload and the Direct-Sound clock still land on the exact overflow cycle.
    private const int OverflowIrqDelay = 2;

    private readonly AgbScheduler m_scheduler;
    private readonly IAgbInterruptController m_interrupts;
    private readonly IAgbApu m_apu;

    // One scheduled overflow event per timer; queued only while that timer is an enabled prescaler in scheduled mode.
    private readonly AgbScheduler.Event[] m_overflowEvent = new AgbScheduler.Event[4];

    // Live timer state (post-latch). period is the 16-bit counter games read back (authoritative for cascade,
    // disabled, and in-window timers; a scheduled prescaler timer's counter is CounterAt() from its anchor instead).
    private readonly int[] m_period = new int[4];
    private readonly int[] m_reload = new int[4];
    private readonly int[] m_frequency = new int[4];
    private readonly bool[] m_enable = new bool[4];
    private readonly bool[] m_irqEnabled = new bool[4];
    private readonly bool[] m_cascade = new bool[4];
    private readonly bool[] m_pending = new bool[4];
    private readonly int[] m_irqCountdown = new int[4]; // >0 while an overflow's IRQ request is in flight (cycles left)

    // Closed-form anchor per prescaler timer: a read at absolute clock m_anchorClock returns m_anchorValue, and each
    // prescaler boundary crossed since then adds one step. Set at every overflow and at each per-cycle→scheduled
    // transition; unused while a timer is cascade/disabled/in-window.
    private readonly long[] m_anchorClock = new long[4];
    private readonly int[] m_anchorValue = new int[4];

    // Latched (deferred-by-one-cycle) control/reload writes, applied by StepLatch when m_timerLatched is set.
    private readonly bool[] m_controlFlag = new bool[4];
    private readonly int[] m_latchControl = new int[4];
    private readonly int[] m_reloadFlags = new int[4]; // bit0 = low byte pending, bit1 = high byte pending
    private readonly int[] m_latchReload = new int[4];
    private bool m_timerLatched;

    // true = event-scheduled (steady) mode: prescaler timers advance via overflow events and are read closed-form.
    // false = per-cycle window: RunCycle drives the latch/IRQ machinery. The bus flips this via EnsureScheduled /
    // EnsurePerCycle at each span boundary; a freshly constructed block is per-cycle until the bus schedules it.
    private bool m_scheduled;

    /// <summary>Creates the timer block bound to the scheduler it queues overflows on, the interrupt controller it
    /// signals, and the APU it clocks for Direct Sound.</summary>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AgbTimerController(AgbScheduler scheduler, IAgbInterruptController interrupts, IAgbApu apu) {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(apu);

        m_scheduler = scheduler;
        m_interrupts = interrupts;
        m_apu = apu;

        for (var index = 0; (index < 4); ++index) {
            var timer = index;

            m_overflowEvent[index] = new AgbScheduler.Event { Callback = (cyclesLate) => OverflowEvent(timer: timer, cyclesLate: cyclesLate) };
        }
    }

    /// <inheritdoc/>
    public bool HasPendingLatch =>
        (m_timerLatched
        || m_pending[0] || m_pending[1] || m_pending[2] || m_pending[3]
        || (m_irqCountdown[0] != 0) || (m_irqCountdown[1] != 0) || (m_irqCountdown[2] != 0) || (m_irqCountdown[3] != 0));

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) {
        var timer = (int)((offset - 0x100u) >> 2);

        if ((offset & 0x2u) == 0u) {
            // CNT_L: the live counter (closed-form for a scheduled prescaler timer, materialized otherwise).
            return (ushort)LiveCounter(timer: timer);
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
        } else {
            // CNT_H write: latch the control word; applied next cycle.
            m_latchControl[timer] = value & 0xFF;
            m_controlFlag[timer] = true;
        }

        m_timerLatched = true;
    }

    /// <inheritdoc/>
    public void EnsureScheduled(long now) {
        // Enter event-scheduled mode: anchor every running prescaler timer at the current clock and queue its next
        // overflow, so the bus can collapse the span to the next event. Idempotent — a no-op once scheduled.
        if (m_scheduled) {
            return;
        }

        for (var timer = 0; (timer < 4); ++timer) {
            if (m_enable[timer] && !m_cascade[timer]) {
                m_anchorClock[timer] = now;
                m_anchorValue[timer] = (m_period[timer] & 0xFFFF);

                ScheduleOverflow(timer: timer);
            } else {
                m_scheduler.Deschedule(e: m_overflowEvent[timer]);
            }
        }

        m_scheduled = true;
    }

    /// <inheritdoc/>
    public void EnsurePerCycle(long now) {
        // Leave event-scheduled mode: materialize every running prescaler timer's closed-form counter into its live
        // field and drop its scheduled overflow, so RunCycle drives the latch/IRQ windows exactly. Idempotent — a
        // no-op while already per-cycle.
        if (!m_scheduled) {
            return;
        }

        for (var timer = 0; (timer < 4); ++timer) {
            if (m_enable[timer] && !m_cascade[timer]) {
                m_period[timer] = CounterAt(timer: timer, now: now);
            }

            m_scheduler.Deschedule(e: m_overflowEvent[timer]);
        }

        m_scheduled = false;
    }

    /// <inheritdoc/>
    public void RunCycle(long clock) {
        // The per-cycle driver for the latch/IRQ windows (the bus calls EnsurePerCycle before entering them). Per the
        // cycle-stepped hardware reference: advance any in-flight overflow-IRQ delays, run all four prescaler timers,
        // then reload-latch all four, then (if a write is pending) commit the control/reload latch. Interrupt-line
        // stepping is handled separately by the interrupt controller.
        StepIrqDelay(timer: 0);
        StepIrqDelay(timer: 1);
        StepIrqDelay(timer: 2);
        StepIrqDelay(timer: 3);

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

    // The counter a CNT_L read returns: a scheduled prescaler timer is reconstructed closed-form from its anchor; a
    // cascade, disabled, or in-window timer holds its materialized value directly.
    private int LiveCounter(int timer) {
        if (m_scheduled && m_enable[timer] && !m_cascade[timer]) {
            return CounterAt(timer: timer, now: m_scheduler.Now);
        }

        return (m_period[timer] & 0xFFFF);
    }

    // The prescaler counter at absolute clock 'now', from the anchor: read(m_anchorClock) == m_anchorValue, and every
    // prescaler boundary in (m_anchorClock, now) adds one step. This is the exact closed form of the per-cycle Run —
    // it counts the same global-clock boundaries — and never wraps within one scheduled interval (the overflow event
    // re-anchors first), so the mask is only defensive.
    private int CounterAt(int timer, long now) {
        var shift = s_shift[m_frequency[timer]];
        var steps = (((now - 1L) >> shift) - ((m_anchorClock[timer] - 1L) >> shift));

        return (int)(m_anchorValue[timer] + steps) & 0xFFFF;
    }

    // Queue timer t's next overflow. The overflow STEP lands on the aligned clock where the counter wraps to reload;
    // the event fires the cycle after (overflow+1), matching the per-cycle model where a step at clock C becomes
    // visible to a read at C+1.
    private void ScheduleOverflow(int timer) {
        var shift = s_shift[m_frequency[timer]];
        var period = (1L << shift);
        var stepsToOverflow = (0x10000 - m_anchorValue[timer]);                     // increments until the counter wraps
        var firstAligned = ((m_anchorClock[timer] + period - 1L) & ~(period - 1L)); // first boundary at/after the anchor
        var overflowClock = (firstAligned + ((stepsToOverflow - 1) * period));

        m_scheduler.ScheduleAbsolute(e: m_overflowEvent[timer], when: (overflowClock + 1L));
    }

    // The scheduled overflow: fired the cycle the wrap becomes visible. Does exactly what the per-cycle Step does on
    // wrap — reload, arm the delayed IRQ, clock Direct Sound, ripple a cascade — then re-anchors (read(when) ==
    // reload) and queues the next overflow. The APU flag and IRQ arm are consumed later (RunPendingDma / the
    // per-cycle StepIrqDelay window opened by HasPendingLatch), so running them from an event is safe.
    private void OverflowEvent(int timer, int cyclesLate) {
        _ = cyclesLate; // the scheduler clamps to the exact cycle, so an overflow event never fires late

        var when = m_overflowEvent[timer].When; // overflow+1

        m_period[timer] = m_reload[timer];
        m_anchorClock[timer] = when;
        m_anchorValue[timer] = m_reload[timer];

        if (m_irqEnabled[timer] && (m_irqCountdown[timer] == 0)) {
            m_irqCountdown[timer] = OverflowIrqDelay;
        }

        if (timer < 2) {
            m_apu.OnTimerOverflow(timer: timer);
        }

        if ((timer < 3) && m_enable[(timer + 1)] && m_cascade[(timer + 1)]) {
            Step(timer: (timer + 1));
        }

        ScheduleOverflow(timer: timer);
    }

    // Overflow-IRQ delay: count an armed request down to its fire cycle, then assert the flag on the interrupt
    // controller. The controller's own two-stage synchronizer adds the register-visibility/recognition latency on top.
    private void StepIrqDelay(int timer) {
        if ((m_irqCountdown[timer] != 0) && (--m_irqCountdown[timer] == 0)) {
            m_interrupts.Request(source: (InterruptSource)((int)InterruptSource.Timer0 + timer));
        }
    }

    // Per-cycle timer run: a prescaler-driven timer steps when the global clock hits its boundary.
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

        if (m_irqEnabled[timer] && (m_irqCountdown[timer] == 0)) {
            // Arm the delayed request; the flag is asserted OverflowIrqDelay cycles later by StepIrqDelay. A request
            // already in flight is left alone (the interrupt flag is a level bit — a fresh arm only matters once the
            // in-flight one has fired), so a fast-overflowing timer cannot indefinitely defer its own recognition.
            m_irqCountdown[timer] = OverflowIrqDelay;
        }

        if (timer < 2) {
            m_apu.OnTimerOverflow(timer: timer);
        }

        if ((timer < 3) && m_enable[(timer + 1)] && m_cascade[(timer + 1)]) {
            Step(timer: (timer + 1));
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
            m_irqEnabled[timer] = ((control & 0x40) != 0);
            m_enable[timer] = ((control & 0x80) != 0);

            if (timer != 0) {
                m_cascade[timer] = ((control & 0x4) != 0);
            }

            if (!wasEnabled && m_enable[timer]) {
                m_pending[timer] = true;
            }

            m_controlFlag[timer] = false;
        }
    }
    private static uint PrescaleField(int frequency) => frequency switch {
        1 => 1u,
        2 => 2u,
        3 => 3u,
        _ => 0u,
    };
}

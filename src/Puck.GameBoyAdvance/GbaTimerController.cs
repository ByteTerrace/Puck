namespace Puck.GameBoyAdvance;

/// <summary>The default timer block: four cascading 16-bit timers with the standard 1/64/256/1024 prescalers.</summary>
public sealed class GbaTimerController : IGbaTimerController {
    private static readonly int[] s_prescaler = { 1, 64, 256, 1024 };

    private readonly IGbaInterruptController m_interrupts;
    private readonly IGbaApu m_apu;
    private readonly int[] m_counter = new int[4];
    private readonly int[] m_reload = new int[4];
    private readonly int[] m_prescaler = new int[4];
    private readonly int[] m_accumulator = new int[4];
    private readonly bool[] m_enabled = new bool[4];
    private readonly bool[] m_cascade = new bool[4];
    private readonly bool[] m_irqEnabled = new bool[4];

    /// <summary>Creates the timer block bound to the interrupt controller it signals on overflow and the APU it
    /// clocks for Direct Sound.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="apu">The audio unit, advanced on timer 0/1 overflow.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public GbaTimerController(IGbaInterruptController interrupts, IGbaApu apu) {
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(apu);

        m_interrupts = interrupts;
        m_apu = apu;

        for (var i = 0; i < 4; ++i) {
            m_prescaler[i] = 1;
        }
    }

    /// <inheritdoc/>
    public void Step(int cycles) {
        // Only prescaler-driven timers advance with the clock; count-up timers move when the one below overflows.
        // Timer 0 has no timer beneath it, so its count-up bit has no effect — it always counts the clock.
        for (var timer = 0; timer < 4; ++timer) {
            if (!m_enabled[timer] || (m_cascade[timer] && (timer != 0))) {
                continue;
            }

            m_accumulator[timer] += cycles;

            var ticks = m_accumulator[timer] / m_prescaler[timer];

            m_accumulator[timer] %= m_prescaler[timer];

            for (var i = 0; i < ticks; ++i) {
                Increment(timer: timer);
            }
        }
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) {
        var timer = (int)((offset - 0x100u) >> 2);

        if ((offset & 0x2u) == 0u) {
            return (ushort)m_counter[timer];
        }

        return (ushort)(((m_prescaler[timer] == 1) ? 0u : (m_prescaler[timer] == 64 ? 1u : (m_prescaler[timer] == 256 ? 2u : 3u)))
            | (m_cascade[timer] ? 0x4u : 0u)
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

        var wasEnabled = m_enabled[timer];

        m_prescaler[timer] = s_prescaler[value & 0x3u];
        m_cascade[timer] = (value & 0x4u) != 0u;
        m_irqEnabled[timer] = (value & 0x40u) != 0u;
        m_enabled[timer] = (value & 0x80u) != 0u;

        // Enabling a timer reloads its counter and restarts the prescaler phase.
        if (!wasEnabled && m_enabled[timer]) {
            m_counter[timer] = m_reload[timer];
            m_accumulator[timer] = 0;
        }
    }

    private void Increment(int timer) {
        if (++m_counter[timer] <= 0xFFFF) {
            return;
        }

        m_counter[timer] = m_reload[timer];

        if (m_irqEnabled[timer]) {
            m_interrupts.Request(source: (InterruptSource)((int)InterruptSource.Timer0 + timer));
        }

        // Timers 0 and 1 can clock the Direct Sound FIFOs.
        if (timer < 2) {
            m_apu.OnTimerOverflow(timer: timer);
        }

        // Overflow drives the next timer up if it is in count-up mode.
        if ((timer < 3) && m_enabled[timer + 1] && m_cascade[timer + 1]) {
            Increment(timer: timer + 1);
        }
    }
}

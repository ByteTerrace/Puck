using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The divider/timer hardware, the machine's first CPU-domain clocked component. A single 16-bit counter advances one
/// step per CPU T-cycle (so it runs at twice the rate per dot under Color double-speed); DIV is its high byte. TIMA
/// counts the falling edges of one selected counter bit ANDed with the timer-enable bit — the model that makes the DIV
/// reset, the TAC enable/frequency change, and the rapid-toggle quirks fall out of one edge detector rather than being
/// special-cased. When TIMA overflows it reads back as zero for four T-cycles before reloading from TMA and raising the
/// timer interrupt; a write to TIMA inside that window cancels the reload, and a write to TMA on the reload cycle is
/// picked up by it. All state is plain fields captured in a fixed order, so the timer snapshots and forks like the rest.
/// </summary>
public sealed class TimerComponent : ITimer, IClockedComponent, ISnapshotable {
    // The counter bit whose falling edge clocks TIMA, indexed by TAC's clock-select (low two bits): 4096, 262144,
    // 65536, and 16384 Hz respectively. The whole counter ticks every T-cycle, so bit N toggles every 2^(N+1) T-cycles.
    private const byte ClockEnableBit = 0x04;
    // The monochrome boot ROM always hands off with the same counter value; the Color handoff is header-dependent.
    private const ushort DmgPostBootDivCounter = 0xABCC;
    private const int OverflowReloadDelay = 4;
    private const byte TacWritableMask = 0x07;

    private readonly IInterruptController m_interrupts;
    private readonly IKey1 m_key1;

    private ushort m_counter;
    private bool m_lastTimaInput;
    private int m_overflowCountdown;
    private bool m_reloadedThisCycle;
    private bool m_stopLatched;
    private bool m_switchBlockLatched;
    private byte m_tac;
    private byte m_tima;
    private ushort m_timaInputMask;
    private byte m_tma;

    /// <summary>Creates the timer wired to the interrupt controller it raises the timer line on. Without a boot ROM the
    /// counter is seeded to the post-boot value — the fixed monochrome handoff, or the Color boot ROM's header-dependent
    /// counter; with one it powers on at zero and the executing boot program produces the handoff phase itself.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="key1">The speed-switch/stop unit whose block windows freeze the counter.</param>
    /// <param name="configuration">The machine configuration, which selects the post-boot counter seed.</param>
    /// <param name="header">The cartridge header steering the Color boot ROM's timing.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public TimerComponent(IInterruptController interrupts, IKey1 key1, MachineConfiguration configuration, CartridgeHeader header) {
        ArgumentNullException.ThrowIfNull(argument: interrupts);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: configuration);
        ArgumentNullException.ThrowIfNull(argument: header);

        m_interrupts = interrupts;
        m_key1 = key1;

        if (configuration.BootRom is null) {
            m_counter = ((configuration.Model == ConsoleModel.Cgb) ? CgbBootDivPrediction.Compute(header: header) : DmgPostBootDivCounter);
        }
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <inheritdoc/>
    public ushort DivCounter =>
        m_counter;

    /// <inheritdoc/>
    public void Tick() {
        // Stop mode freezes the whole block after resetting DIV once on the way in.
        if (m_key1.IsStopped) {
            if (!m_stopLatched) {
                m_stopLatched = true;

                SetCounter(value: 0);
            }

            return;
        }

        m_stopLatched = false;

        // The speed switch's timer-block window freezes the counter; as it closes, DIV is reseeded — the counter's low
        // bits survive, pulse once, and collapse to zero, producing exactly the TIMA edge the hardware shows.
        if (m_key1.AreTimersBlocked) {
            m_switchBlockLatched = true;

            return;
        }

        if (m_switchBlockLatched) {
            m_switchBlockLatched = false;

            SetCounter(value: (ushort)(m_counter & 0x000F));
            SetCounter(value: (ushort)(m_counter + 1));
            SetCounter(value: 0);
        }

        // Complete a pending post-overflow reload before this T-cycle's counter step, so the reload lands on its own
        // cycle (the cycle a TIMA write must lose to and a TMA write is folded into).
        m_reloadedThisCycle = false;

        if (m_overflowCountdown > 0) {
            if (--m_overflowCountdown == 0) {
                m_tima = m_tma;
                m_reloadedThisCycle = true;

                m_interrupts.Request(kind: InterruptKind.Timer);
            }
        }

        SetCounter(value: (ushort)(m_counter + 1));
    }
    /// <inheritdoc/>
    public byte ReadRegister(ushort address) =>
        address switch {
            MemoryMap.Divider => (byte)(m_counter >> 8),
            MemoryMap.TimerCounter => m_tima,
            MemoryMap.TimerModulo => m_tma,
            _ => (byte)(~TacWritableMask | m_tac),
        };
    /// <inheritdoc/>
    public void WriteRegister(ushort address, byte value) {
        switch (address) {
            case MemoryMap.Divider:
                // Any write resets the whole counter; the falling edge that may produce clocks TIMA.
                SetCounter(value: 0);

                break;
            case MemoryMap.TimerCounter:
                // A write on the reload cycle loses to the reload; one inside the window before it cancels the pending
                // reload and its interrupt.
                if (!m_reloadedThisCycle) {
                    m_overflowCountdown = 0;
                    m_tima = value;
                }

                break;
            case MemoryMap.TimerModulo:
                m_tma = value;

                // TMA written on the reload cycle is the value TIMA was just reloaded with.
                if (m_reloadedThisCycle) {
                    m_tima = value;
                }

                break;
            default:
                m_tac = (byte)(value & TacWritableMask);

                // Changing the enable bit or the selected frequency can drop the detector's input, clocking TIMA. The
                // input bit is fixed by TAC, so cache it here and the per-dot detector only has to mask the counter.
                UpdateTimaInputMask();
                UpdateTimaInput();

                break;
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteUInt16(value: m_counter);
        writer.WriteByte(value: m_tima);
        writer.WriteByte(value: m_tma);
        writer.WriteByte(value: m_tac);
        writer.WriteBoolean(value: m_lastTimaInput);
        writer.WriteInt32(value: m_overflowCountdown);
        writer.WriteBoolean(value: m_reloadedThisCycle);
        writer.WriteBoolean(value: m_stopLatched);
        writer.WriteBoolean(value: m_switchBlockLatched);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_counter = reader.ReadUInt16();
        m_tima = reader.ReadByte();
        m_tma = reader.ReadByte();
        m_tac = reader.ReadByte();
        m_lastTimaInput = reader.ReadBoolean();
        m_overflowCountdown = reader.ReadInt32();
        m_reloadedThisCycle = reader.ReadBoolean();
        m_stopLatched = reader.ReadBoolean();
        m_switchBlockLatched = reader.ReadBoolean();

        // The input mask is derived from TAC, not part of the snapshot; rebuild it from the restored TAC.
        UpdateTimaInputMask();
    }

    // The single counter bit that feeds the falling-edge detector when the timer is enabled, as a mask over the 16-bit
    // counter; zero when the timer is disabled, so a disabled timer always reads a low input. Folding the enable bit and
    // the TAC frequency select into one mask keeps the per-dot detector to a single AND. Rebuilt only when TAC changes.
    private void UpdateTimaInputMask() {
        var selectedBit = (m_tac & 0x03) switch {
            0 => 9,
            1 => 3,
            2 => 5,
            _ => 7,
        };

        m_timaInputMask = ((m_tac & ClockEnableBit) != 0) ? (ushort)(1 << selectedBit) : (ushort)0;
    }
    private void SetCounter(ushort value) {
        m_counter = value;

        UpdateTimaInput();
    }
    // The single edge detector: TIMA advances on the 1→0 transition of the selected counter bit gated by the enable,
    // both captured in the cached input mask.
    private void UpdateTimaInput() {
        var input = ((m_counter & m_timaInputMask) != 0);

        if (m_lastTimaInput && !input) {
            IncrementTima();
        }

        m_lastTimaInput = input;
    }
    private void IncrementTima() {
        if (m_tima == 0xFF) {
            // Overflow: TIMA reads zero until the reload lands a few T-cycles later (modelled by the countdown).
            m_tima = 0x00;
            m_overflowCountdown = OverflowReloadDelay;
        }
        else {
            ++m_tima;
        }
    }
}

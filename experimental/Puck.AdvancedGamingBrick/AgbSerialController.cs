namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The full serial communication subsystem (SIO / RCNT / JOY-bus), modelled on the hardware register spec.
/// SIOCNT, RCNT and the JOY registers are decomposed into
/// their hardware fields; writing SIOCNT's start bit begins a transfer in the selected mode (Normal 8/32-bit,
/// Multiplayer, UART) over the attached <see cref="IAgbLink"/>, and RCNT bit 15 hands the lines to General-purpose
/// (GPIO) or JOY-bus mode. With the default lone-console transport (<see cref="NullAgbLink"/>) a started transfer is
/// left PENDING — no fabricated completion and no serial IRQ — exactly as real hardware does, so cable-less games
/// (a representative commercial cartridge's boot SIO probe) proceed via their own timeouts instead of acting on a phantom partner.
/// </summary>
public sealed class AgbSerialController : IAgbSerialController {
    private readonly AgbScheduler m_scheduler;
    private readonly IAgbInterruptController m_interrupts;
    private readonly AgbScheduler.Event m_transferEvent;

    private IAgbLink m_link = NullAgbLink.Instance;
    private IAgbLinkNode m_node = NullAgbLink.Instance;

    // SIOCNT (0x128) fields — the serial register fields.
    private bool m_shiftClockInternal;   // bit 0  (SC: 1=internal/master, 0=external/slave)
    private bool m_shiftClock2MHz;        // bit 1
    private bool m_recvEnable;            // bit 2  (SI in multiplayer)
    private bool m_sendEnable;            // bit 3  (SD in multiplayer)
    private bool m_startBit;              // bit 7
    private byte m_uartFlags;             // bits 8-11
    private int m_sioMode;                // bits 12-13: 0=Normal8, 1=Normal32, 2=Multiplayer, 3=UART
    private bool m_irqEnable;             // bit 14
    private int m_multiplayerId;          // bits 4-5 (read-only, assigned on a multiplayer round)
    private bool m_multiplayerError;      // bit 6  (read-only)

    private readonly ushort[] m_data = new ushort[4]; // SIOMULTI0-3 / SIODATA32 (data[0..1]) / SIODATA8
    private ushort m_dataSend;                         // SIOMLT_SEND / SIODATA8 (0x12A)

    // RCNT (0x134) / JOY-bus fields — the JOY-bus register fields.
    private bool m_rcntSc, m_rcntSd, m_rcntSi, m_rcntSo;
    private bool m_rcntScMode, m_rcntSdMode, m_rcntSiMode, m_rcntSoMode;
    private bool m_siIrqEnable;
    private int m_rcntMode;               // RCNT bits 14-15 (2=general-purpose, 3=JOY-bus)

    private bool m_joyReset, m_joyRecvComplete, m_joySendComplete, m_joyResetIrqEnable;
    private uint m_joyRecv, m_joyTrans;
    private bool m_joyRecvFlag, m_joySendFlag;
    private int m_joyGeneralFlag;

    /// <summary>Creates the serial subsystem bound to the scheduler it times transfers on and the interrupt
    /// controller it signals on completion.</summary>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AgbSerialController(AgbScheduler scheduler, IAgbInterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(interrupts);

        m_scheduler = scheduler;
        m_interrupts = interrupts;
        m_transferEvent = new AgbScheduler.Event { Callback = _ => CompleteTransfer() };
    }

    /// <inheritdoc/>
    public void Connect(IAgbLink link) {
        ArgumentNullException.ThrowIfNull(link);

        m_link = link;
        m_node = link.Connect();
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) => offset switch {
        // 0x120/0x122 are SIODATA32 in Normal-32 and SIOMULTI0/1 in Multiplayer; 0x124/0x126 are SIOMULTI2/3
        // (Multiplayer only). In any other mode these read 0 on hardware — without this read-gating a
        // UART/Normal-8 read would return stale Normal-32 data.
        0x120u => (ushort)(((m_sioMode == 1) || (m_sioMode == 2)) ? m_data[0] : 0),
        0x122u => (ushort)(((m_sioMode == 1) || (m_sioMode == 2)) ? m_data[1] : 0),
        0x124u => (ushort)((m_sioMode == 2) ? m_data[2] : 0),
        0x126u => (ushort)((m_sioMode == 2) ? m_data[3] : 0),
        0x128u => PackSioControl(),
        0x12Au => (ushort)((m_sioMode == 3) ? 0 : m_dataSend), // SIODATA8/SIOMLT_SEND reads 0 in UART mode
        0x134u => PackRcnt(),
        0x140u => PackJoyControl(),
        0x150u => (ushort)m_joyRecv,
        0x152u => (ushort)(m_joyRecv >> 16),
        0x154u => (ushort)m_joyTrans,
        0x156u => (ushort)(m_joyTrans >> 16),
        0x158u => PackJoyStat(),
        _ => 0,
    };

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value) {
        switch (offset) {
            case 0x120u: if (m_sioMode == 1) { m_data[0] = value; } break; // SIODATA32_L: writable only in Normal-32
            case 0x122u: if (m_sioMode == 1) { m_data[1] = value; } break; // SIODATA32_H: writable only in Normal-32
            case 0x124u: break; // SIOMULTI2: received-only (read-only)
            case 0x126u: break; // SIOMULTI3: received-only (read-only)
            case 0x128u: WriteSioControl(value); break;
            case 0x12Au: m_dataSend = value; break;
            case 0x134u: WriteRcnt(value); break;
            case 0x140u: WriteJoyControl(value); break;
            // JOY_RECV/JOY_TRANS are live only on the JOY bus (RCNT mode 3); outside it, CPU writes are dropped and
            // the registers read 0.
            case 0x150u: if (m_rcntMode == 3) { m_joyRecv = (m_joyRecv & 0xFFFF0000u) | value; } break;
            case 0x152u: if (m_rcntMode == 3) { m_joyRecv = (m_joyRecv & 0x0000FFFFu) | ((uint)value << 16); } break;
            case 0x154u: if (m_rcntMode == 3) { m_joyTrans = (m_joyTrans & 0xFFFF0000u) | value; } break;
            case 0x156u: if (m_rcntMode == 3) { m_joyTrans = (m_joyTrans & 0x0000FFFFu) | ((uint)value << 16); } break;
            case 0x158u: WriteJoyStat(value); break;
            default: break;
        }
    }

    private ushort PackSioControl() {
        var value = (m_shiftClockInternal ? 0x0001u : 0u)
            | (m_shiftClock2MHz ? 0x0002u : 0u)
            | (m_recvEnable ? 0x0004u : 0u)
            | (m_sendEnable ? 0x0008u : 0u)
            | (m_startBit ? 0x0080u : 0u)
            | ((uint)m_uartFlags << 8)
            | ((uint)m_sioMode << 12)
            | (m_irqEnable ? 0x4000u : 0u);

        // In multiplayer mode bits 4-5 expose the assigned player id and bit 6 the error flag.
        if (m_sioMode == 2) {
            value |= (uint)m_multiplayerId << 4;

            if (m_multiplayerError) {
                value |= 0x0040u;
            }
        }

        // UART mode reads back bit 5 (receive-FIFO-empty status) set (the SIOCNT U-mask reads 0x7FAF on hardware).
        if (m_sioMode == 3) {
            value |= 0x0020u;
        }

        return (ushort)value;
    }

    private void WriteSioControl(ushort value) {
        m_shiftClockInternal = (value & 0x0001u) != 0u;
        m_shiftClock2MHz = (value & 0x0002u) != 0u;
        m_recvEnable = (value & 0x0004u) != 0u;
        m_sendEnable = (value & 0x0008u) != 0u;
        m_startBit = (value & 0x0080u) != 0u;
        m_uartFlags = (byte)((value >> 8) & 0xFu);
        m_sioMode = (value >> 12) & 0x3;
        m_irqEnable = (value & 0x4000u) != 0u;

        // Re-evaluate the transfer on every write while the start bit is set — not just on its rising edge. The
        // hardware begins a Normal master transfer the moment the internal clock is selected with start already set,
        // which is exactly how the RFU adapter detection arms it: it sets start while still external-clocked
        // (SIOCNT=0x5080), then switches to internal master (|= SIO_38400_BPS) with start held. An edge-only trigger
        // sees only the external write and never starts the transfer, so the serial IRQ never fires and the RFU
        // state machine (a representative commercial cartridge's boot) stalls forever. BeginTransfer no-ops if one is already in flight.
        if (m_startBit) {
            BeginTransfer();
        }
        else {
            m_scheduler.Deschedule(m_transferEvent);
        }
    }

    // Begins a transfer in the current mode. A self-clocked transfer (normal internal-clock master, multiplayer
    // parent, UART) completes after the appropriate bit-time — even on a lone console, where hardware still shifts
    // and reads the idle-high lines back as all ones. An externally-clocked normal transfer with no partner has no
    // clock source, so it correctly stays pending (the start bit holds set). A connected partner supplies the real
    // exchanged data through the link in CompleteTransfer.
    private void BeginTransfer() {
        // A transfer already in flight is not restarted by further SIOCNT writes (e.g. the RFU code re-asserts the
        // start bit after selecting the internal clock); the hardware shifts it through to completion regardless.
        if (m_transferEvent.Scheduled) {
            return;
        }

        // RCNT bit 15 hands the pins to general-purpose / JOY-bus mode; SIOCNT transfers are inactive there.
        if ((m_rcntMode & 0x2) != 0) {
            return;
        }

        var bitCycles = m_shiftClock2MHz ? 8 : 64; // 2 MHz vs 256 KHz internal shift clock
        int cycles;

        switch (m_sioMode) {
            case 0:
            case 1:
                // Normal: the master (internal clock) self-clocks the transfer and on real hardware it always
                // completes, shifting in idle-high 0xFFFF when no partner is attached (and raising the serial IRQ).
                // A slave (external clock) has no clock source with no partner, so it correctly stays pending.
                // (Some reference models have no transfer engine at all and never complete — a documented
                // simplification, not the hardware behavior; a representative commercial cartridge's boot
                // link-probe needs the real completion + idle-high data.)
                if (!m_shiftClockInternal && !m_link.HasPartner) {
                    return;
                }

                cycles = bitCycles * (m_sioMode == 1 ? 32 : 8);

                break;
            case 2:
                // Multiplayer: only the parent (SI line low, bit 2 clear) drives the round; a child waits for it.
                // A lone parent still completes, reading all-ones in the absent child slots.
                if (m_recvEnable && !m_link.HasPartner) {
                    return;
                }

                cycles = MultiplayerBaudCycles() * 16;

                break;
            default:
                cycles = MultiplayerBaudCycles() * 8; // UART

                break;
        }

        m_scheduler.Schedule(m_transferEvent, cyclesFromNow: cycles);
    }

    private int MultiplayerBaudCycles() => (m_recvEnable, m_sendEnable) switch {
        _ => ((m_uartFlags & 0x3) switch {
            0 => 64,   //   9600 bps
            1 => 32,   //  38400 bps
            2 => 16,   // 115200 bps
            _ => 8,
        }),
    };

    // Fired by the scheduler when a partner-driven transfer finishes: exchange the data through the link, clear the
    // start/busy bit, and raise the serial IRQ if enabled.
    private void CompleteTransfer() {
        m_startBit = false;

        switch (m_sioMode) {
            case 0: {
                var incoming = m_node.NormalExchange(outgoing: (uint)(m_data[0] & 0xFFu), word: false);

                m_data[0] = (ushort)((m_data[0] & 0xFF00u) | (incoming & 0xFFu));

                break;
            }
            case 1: {
                var outgoing = (uint)(m_data[0] | (m_data[1] << 16));
                var incoming = m_node.NormalExchange(outgoing: outgoing, word: true);

                m_data[0] = (ushort)incoming;
                m_data[1] = (ushort)(incoming >> 16);

                break;
            }
            case 2: {
                _ = m_node.MultiplayerExchange(send: m_dataSend, out var slots);

                m_data[0] = slots[0];
                m_data[1] = slots[1];
                m_data[2] = slots[2];
                m_data[3] = slots[3];
                m_multiplayerId = m_node.PlayerId;
                m_data[m_multiplayerId] = m_dataSend; // this console's own word appears in its own slot
                m_multiplayerError = false;

                break;
            }
            default:
                break; // UART: no framed payload modelled yet
        }

        if (m_irqEnable) {
            m_interrupts.Request(source: InterruptSource.Serial);
        }
    }

    private ushort PackRcnt() {
        var value = (m_rcntSc ? 0x0001u : 0u)
            | (m_rcntSd ? 0x0002u : 0u)
            | (m_rcntSi ? 0x0004u : 0u)
            | (m_rcntSo ? 0x0008u : 0u)
            | (m_rcntScMode ? 0x0010u : 0u)
            | (m_rcntSdMode ? 0x0020u : 0u)
            | (m_rcntSiMode ? 0x0040u : 0u)
            | (m_rcntSoMode ? 0x0080u : 0u)
            | (m_siIrqEnable ? 0x0100u : 0u)
            | ((uint)m_rcntMode << 14);

        // While RCNT is in SIO mode (not GPIO/JOY) and SIOCNT is in a Normal mode, the SD (bit 1) and SO (bit 3)
        // lines are driven by the serial unit and read back 0 (on hardware Normal RCNT reads 0x01F5 vs 0x01FF for M/UART).
        // Gate on RCNT-SIO so the RFU's GPIO line reads (a representative commercial cartridge) are untouched.
        if ((m_rcntMode < 2) && (m_sioMode <= 1)) {
            value &= ~0x000Au;
        }

        return (ushort)value;
    }

    private void WriteRcnt(ushort value) {
        m_rcntSc = (value & 0x0001u) != 0u;
        m_rcntSd = (value & 0x0002u) != 0u;
        m_rcntSi = (value & 0x0004u) != 0u;
        m_rcntSo = (value & 0x0008u) != 0u;
        m_rcntScMode = (value & 0x0010u) != 0u;
        m_rcntSdMode = (value & 0x0020u) != 0u;
        m_rcntSiMode = (value & 0x0040u) != 0u;
        m_rcntSoMode = (value & 0x0080u) != 0u;
        m_siIrqEnable = (value & 0x0100u) != 0u;
        m_rcntMode = (value >> 14) & 0x3;
    }

    private ushort PackJoyControl() => (ushort)(
        (m_joyReset ? 0x0001u : 0u)
        | (m_joyRecvComplete ? 0x0002u : 0u)
        | (m_joySendComplete ? 0x0004u : 0u)
        | (m_joyResetIrqEnable ? 0x0040u : 0u));

    private void WriteJoyControl(ushort value) {
        // The status bits are write-one-to-clear; bit 6 is the reset-IRQ enable.
        if ((value & 0x0001u) != 0u) {
            m_joyReset = false;
        }

        if ((value & 0x0002u) != 0u) {
            m_joyRecvComplete = false;
        }

        if ((value & 0x0004u) != 0u) {
            m_joySendComplete = false;
        }

        m_joyResetIrqEnable = (value & 0x0040u) != 0u;
    }

    private ushort PackJoyStat() => (ushort)(
        (m_joyRecvFlag ? 0x0002u : 0u)
        | (m_joySendFlag ? 0x0008u : 0u)
        | ((uint)m_joyGeneralFlag << 4));

    private void WriteJoyStat(ushort value) {
        m_joyRecvFlag = (value & 0x0002u) != 0u;
        m_joySendFlag = (value & 0x0008u) != 0u;
        m_joyGeneralFlag = (value >> 4) & 0x3;
    }
}

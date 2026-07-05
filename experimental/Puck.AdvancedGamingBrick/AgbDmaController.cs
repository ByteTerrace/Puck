namespace Puck.AdvancedGamingBrick;

/// <summary>The four DMA channels with per-word priority preemption: between each read, channels 0→3 are
/// checked for readiness so a higher-priority channel can interrupt a lower-priority transfer mid-stream.
/// ProcessEvents is called between bus operations so scheduler events (HBlank, VBlank) can activate timed
/// channels during an immediate transfer — matching the cycle-stepped hardware reference.</summary>
public sealed class AgbDmaController : IAgbDmaController {
    private const uint ChannelStride = 12u;
    private const int TimingImmediate = 0;
    private const int TimingVBlank = 1;
    private const int TimingHBlank = 2;

    private readonly IAgbInterruptController m_interrupts;
    private readonly uint[] m_source = new uint[4];
    private readonly uint[] m_destination = new uint[4];
    private readonly uint[] m_count = new uint[4];
    private readonly ushort[] m_control = new ushort[4];
    private readonly uint[] m_sourceLatch = new uint[4];
    private readonly uint[] m_destinationLatch = new uint[4];
    private readonly uint[] m_remaining = new uint[4];

    // Last value latched onto the internal DMA bus. A read from an undrivable source (BIOS region < 0x02000000)
    // returns this open-bus latch rather than fetching; a halfword read mirrors into both halves so the destination
    // alignment selects the right half (open-bus DMA behavior).
    private readonly uint[] m_dataLatch = new uint[4];
    private readonly bool[] m_active = new bool[4];
    private bool m_running;
    private int m_activeChannel = -1;

    /// <summary>Creates the DMA block bound to the interrupt controller it signals on completion.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interrupts"/> is <see langword="null"/>.</exception>
    public AgbDmaController(IAgbInterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(interrupts);

        m_interrupts = interrupts;
    }

    /// <inheritdoc/>
    public void RunPending(IAgbBus bus) {
        // RunPending: the bus calls this at the start of every CPU access. If a channel
        // has become ready, run the whole burst now (stalling the CPU) before the access proceeds — so the DMA's
        // cycles and its completion IRQ are charged to the consuming instruction, matching hardware. The m_running guard
        // makes the DMA's own bus accesses (which re-enter here) no-ops.
        if (m_running) {
            return;
        }

        if (!m_active[0] && !m_active[1] && !m_active[2] && !m_active[3]) {
            return;
        }

        RunDmaLoop(bus: bus);
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) {
        var local = (offset - 0xB0u) % ChannelStride;

        return (local == 10u)
            ? m_control[(offset - 0xB0u) / ChannelStride]
            : (ushort)0;
    }

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value, IAgbBus bus) {
        var channel = (int)((offset - 0xB0u) / ChannelStride);
        var local = (offset - 0xB0u) % ChannelStride;

        switch (local) {
            case 0u:
                m_source[channel] = (m_source[channel] & 0xFFFF0000u) | value;

                break;
            case 2u:
                m_source[channel] = (m_source[channel] & 0x0000FFFFu) | ((uint)value << 16);

                break;
            case 4u:
                m_destination[channel] = (m_destination[channel] & 0xFFFF0000u) | value;

                break;
            case 6u:
                m_destination[channel] = (m_destination[channel] & 0x0000FFFFu) | ((uint)value << 16);

                break;
            case 8u:
                m_count[channel] = value;

                break;
            default: // 10: control
                WriteControl(channel: channel, value: value, bus: bus);

                break;
        }
    }

    /// <inheritdoc/>
    public void OnVBlank(IAgbBus bus) => ActivateTimed(timing: TimingVBlank, bus: bus);

    /// <inheritdoc/>
    public void OnHBlank(IAgbBus bus) => ActivateTimed(timing: TimingHBlank, bus: bus);

    /// <inheritdoc/>
    public void OnFifo(int fifo, IAgbBus bus) {
        var fifoDestination = (fifo == 0) ? 0x040000A0u : 0x040000A4u;

        var stall = bus.BeginDmaStall();

        for (var channel = 1; channel <= 2; ++channel) {
            var control = m_control[channel];

            if (((control & 0x8000) == 0) || (((control >> 12) & 0x3) != 3) || (m_destinationLatch[channel] != (fifoDestination & DestinationMask(channel: channel)))) {
                continue;
            }

            var sourceControl = (control >> 7) & 0x3;

            for (var i = 0; i < 4; ++i) {
                bus.Write32(address: fifoDestination, value: bus.Read32(address: m_sourceLatch[channel], access: BusAccessType.Sequential), access: BusAccessType.Sequential);
                m_sourceLatch[channel] = Step(address: m_sourceLatch[channel], control: sourceControl, unit: 4u);
            }

            if ((control & 0x4000) != 0) {
                m_interrupts.Request(source: (InterruptSource)((int)InterruptSource.Dma0 + channel));
            }
        }

        bus.EndDmaStall(previous: stall);
    }

    /// <inheritdoc/>
    public void OnVideoCapture(IAgbBus bus) {
        _ = bus;

        if (((m_control[3] & 0x8000) != 0) && (((m_control[3] >> 12) & 0x3) == 3)) {
            // Mark DMA3 pending for this video-capture scanline; the bus runs it at the CPU's next access.
            ActivateChannel(channel: 3);
            m_active[3] = true;
        }
    }

    /// <inheritdoc/>
    public void OnVideoCaptureEnd() {
        if (((m_control[3] & 0x8000) != 0) && (((m_control[3] >> 12) & 0x3) == 3)) {
            m_control[3] &= unchecked((ushort)~0x8000);
            m_active[3] = false;
        }
    }

    private void WriteControl(int channel, ushort value, IAgbBus bus) {
        var wasEnabled = (m_control[channel] & 0x8000) != 0;

        m_control[channel] = value;

        if (wasEnabled || ((value & 0x8000) == 0)) {
            return;
        }

        m_sourceLatch[channel] = m_source[channel] & SourceMask(channel: channel);
        m_destinationLatch[channel] = m_destination[channel] & DestinationMask(channel: channel);
        ActivateChannel(channel: channel);

        if (((value >> 12) & 0x3) == TimingImmediate) {
            // Mark the channel pending; the bus runs the burst at the CPU's next access (RunPending), not here.
            m_active[channel] = true;
        }
    }

    private void ActivateTimed(int timing, IAgbBus bus) {
        // Mark matching channels pending; the bus runs them at the CPU's next access (RunPending). The bus
        // argument is unused here now but kept on the public surface for the trigger callers.
        _ = bus;

        for (var ch = 0; ch < 4; ++ch) {
            if (((m_control[ch] & 0x8000) != 0) && (((m_control[ch] >> 12) & 0x3) == timing)) {
                ActivateChannel(channel: ch);
                m_active[ch] = true;
            }
        }
    }

    private void ActivateChannel(int channel) {
        var maximum = (channel == 3) ? 0x10000u : 0x4000u;

        m_remaining[channel] = (m_count[channel] == 0u) ? maximum : m_count[channel];
    }

    private void RunDmaLoop(IAgbBus bus) {
        if (m_running) {
            return;
        }

        m_running = true;

        // Freeze the IRQ-recognition pipeline for the burst (the CPU-stall behavior): timers keep counting through
        // the transfer cycles, but a request raised mid-burst is not recognised until the CPU regains the bus.
        var stall = bus.BeginDmaStall();

        while (true) {
            var ch = -1;

            for (var i = 0; i < 4; ++i) {
                if (m_active[i]) {
                    ch = i;

                    break;
                }
            }

            if (ch < 0) {
                break;
            }

            var control = m_control[ch];
            var word = (control & 0x400) != 0;
            var unit = word ? 4u : 2u;
            var sourceControl = (control >> 7) & 0x3;
            var destinationControl = (control >> 5) & 0x3;
            var access = (ch != m_activeChannel) ? BusAccessType.NonSequential : BusAccessType.Sequential;

            m_activeChannel = ch;

            // Only an addressable source (>= 0x02000000) actually drives the bus; a BIOS-region source leaves the
            // previous latch in place (open bus). A halfword read mirrors into both halves of the latch.
            if (m_sourceLatch[ch] >= 0x02000000u) {
                if (word) {
                    m_dataLatch[ch] = bus.Read32(address: m_sourceLatch[ch], access: access);
                }
                else {
                    var half = bus.Read16(address: m_sourceLatch[ch], access: access) & 0xFFFFu;
                    m_dataLatch[ch] = half | (half << 16);
                }
            }

            bus.ProcessEvents();

            if (word) {
                // 32-bit transfer force-aligns; write the full latched word (the bus aligns the destination).
                bus.Write32(address: m_destinationLatch[ch], value: m_dataLatch[ch], access: access);
            }
            else {
                // 16-bit transfer: the destination's bit 1 selects which mirrored half of the latch drives the bus.
                var half = m_dataLatch[ch] >> (int)((m_destinationLatch[ch] & 2u) * 8u);
                bus.Write16(address: m_destinationLatch[ch], value: (ushort)half, access: access);
            }

            m_sourceLatch[ch] = Step(address: m_sourceLatch[ch], control: sourceControl, unit: unit);
            m_destinationLatch[ch] = Step(address: m_destinationLatch[ch], control: destinationControl, unit: unit);

            --m_remaining[ch];

            if (m_remaining[ch] == 0) {
                CompleteChannel(channel: ch);
            }

            bus.ProcessEvents();
        }

        bus.EndDmaStall(previous: stall);

        m_activeChannel = -1;
        m_running = false;
    }

    private void CompleteChannel(int channel) {
        m_active[channel] = false;

        var control = m_control[channel];
        var destinationControl = (control >> 5) & 0x3;

        if (destinationControl == 3) {
            m_destinationLatch[channel] = m_destination[channel] & DestinationMask(channel: channel);
        }

        if ((control & 0x4000) != 0) {
            m_interrupts.Request(source: (InterruptSource)((int)InterruptSource.Dma0 + channel));
        }

        if ((control & 0x200) == 0) {
            m_control[channel] &= unchecked((ushort)~0x8000);
        }
    }

    private static uint Step(uint address, int control, uint unit) => control switch {
        0 => address + unit, // increment
        1 => address - unit, // decrement
        3 => address + unit, // increment, reload at repeat
        _ => address,        // fixed
    };

    private static uint SourceMask(int channel) => (channel == 0)
        ? 0x07FFFFFFu
        : 0x0FFFFFFFu;

    private static uint DestinationMask(int channel) => (channel == 3)
        ? 0x0FFFFFFFu
        : 0x07FFFFFFu;
}

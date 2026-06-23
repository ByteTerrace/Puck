namespace Puck.GameBoyAdvance;

/// <summary>The default DMA block: four channels with increment/decrement/fixed/reload addressing, 16- or
/// 32-bit units, repeat, and completion interrupts.</summary>
public sealed class GbaDmaController : IGbaDmaController {
    private const uint ChannelStride = 12u;
    private const int TimingImmediate = 0;
    private const int TimingVBlank = 1;
    private const int TimingHBlank = 2;

    private readonly IGbaInterruptController m_interrupts;
    private readonly uint[] m_source = new uint[4];
    private readonly uint[] m_destination = new uint[4];
    private readonly uint[] m_count = new uint[4];
    private readonly ushort[] m_control = new ushort[4];
    private readonly uint[] m_sourceLatch = new uint[4];
    private readonly uint[] m_destinationLatch = new uint[4];
    private readonly uint[] m_countLatch = new uint[4];

    /// <summary>Creates the DMA block bound to the interrupt controller it signals on completion.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interrupts"/> is <see langword="null"/>.</exception>
    public GbaDmaController(IGbaInterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(interrupts);

        m_interrupts = interrupts;
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) {
        var local = (offset - 0xB0u) % ChannelStride;

        // Only the control halfword reads back; the address and count registers are write-only.
        return (local == 10u)
            ? m_control[(offset - 0xB0u) / ChannelStride]
            : (ushort)0;
    }

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value, IGbaBus bus) {
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
    public void OnVBlank(IGbaBus bus) => RunTimed(timing: TimingVBlank, bus: bus);

    /// <inheritdoc/>
    public void OnHBlank(IGbaBus bus) => RunTimed(timing: TimingHBlank, bus: bus);

    /// <inheritdoc/>
    public void OnFifo(int fifo, IGbaBus bus) {
        var fifoDestination = (fifo == 0) ? 0x040000A0u : 0x040000A4u;

        for (var channel = 1; channel <= 2; ++channel) {
            var control = m_control[channel];

            // Enabled, special-timing, and pointed at this FIFO.
            if (((control & 0x8000) == 0) || (((control >> 12) & 0x3) != 3) || (m_destinationLatch[channel] != (fifoDestination & DestinationMask(channel: channel)))) {
                continue;
            }

            var sourceControl = (control >> 7) & 0x3;

            // A FIFO transfer is always four 32-bit words into the fixed FIFO address.
            for (var i = 0; i < 4; ++i) {
                bus.Write32(address: fifoDestination, value: bus.Read32(address: m_sourceLatch[channel], access: BusAccessType.Sequential), access: BusAccessType.Sequential);
                m_sourceLatch[channel] = Step(address: m_sourceLatch[channel], control: sourceControl, unit: 4u);
            }

            if ((control & 0x4000) != 0) {
                m_interrupts.Request(source: (InterruptSource)((int)InterruptSource.Dma0 + channel));
            }
        }
    }

    /// <inheritdoc/>
    public void OnVideoCapture(IGbaBus bus) {
        // Only DMA3 has special (video-capture) timing; for DMA1/2 the special mode is the Direct Sound FIFO.
        if (((m_control[3] & 0x8000) != 0) && (((m_control[3] >> 12) & 0x3) == 3)) {
            RunTransfer(channel: 3, bus: bus);
        }
    }

    /// <inheritdoc/>
    public void OnVideoCaptureEnd() {
        if (((m_control[3] & 0x8000) != 0) && (((m_control[3] >> 12) & 0x3) == 3)) {
            m_control[3] &= unchecked((ushort)~0x8000);
        }
    }

    private void WriteControl(int channel, ushort value, IGbaBus bus) {
        var wasEnabled = (m_control[channel] & 0x8000) != 0;

        m_control[channel] = value;

        if (wasEnabled || ((value & 0x8000) == 0)) {
            return;
        }

        // Newly enabled: latch the programmed source/destination/count, then run now if immediate.
        m_sourceLatch[channel] = m_source[channel] & SourceMask(channel: channel);
        m_destinationLatch[channel] = m_destination[channel] & DestinationMask(channel: channel);
        m_countLatch[channel] = m_count[channel];

        if (((value >> 12) & 0x3) == TimingImmediate) {
            RunTransfer(channel: channel, bus: bus);
        }
    }

    private void RunTimed(int timing, IGbaBus bus) {
        for (var channel = 0; channel < 4; ++channel) {
            if (((m_control[channel] & 0x8000) != 0) && (((m_control[channel] >> 12) & 0x3) == timing)) {
                RunTransfer(channel: channel, bus: bus);
            }
        }
    }

    private void RunTransfer(int channel, IGbaBus bus) {
        var control = m_control[channel];
        var word = (control & 0x400) != 0;
        var destinationControl = (control >> 5) & 0x3;
        var sourceControl = (control >> 7) & 0x3;
        var unit = word ? 4u : 2u;
        var maximum = (channel == 3) ? 0x10000u : 0x4000u;
        var count = (m_countLatch[channel] == 0u) ? maximum : m_countLatch[channel];
        var source = m_sourceLatch[channel];
        var destination = m_destinationLatch[channel];
        var access = BusAccessType.NonSequential;

        for (var i = 0u; i < count; ++i) {
            if (word) {
                bus.Write32(address: destination, value: bus.Read32(address: source, access: access), access: access);
            }
            else {
                bus.Write16(address: destination, value: bus.Read16(address: source, access: access), access: access);
            }

            source = Step(address: source, control: sourceControl, unit: unit);
            destination = Step(address: destination, control: destinationControl, unit: unit);
            access = BusAccessType.Sequential;
        }

        // Preserve the running source; the destination reloads when its control selects increment-and-reload.
        m_sourceLatch[channel] = source;

        if (destinationControl != 3) {
            m_destinationLatch[channel] = destination;
        }

        if ((control & 0x4000) != 0) {
            m_interrupts.Request(source: (InterruptSource)((int)InterruptSource.Dma0 + channel));
        }

        // Without repeat the channel disables itself once the transfer completes.
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

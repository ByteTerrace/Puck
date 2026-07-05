using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The serial port, a CPU-domain clocked component. Its shift clock is not a private countdown but the DIV counter
/// itself: an internal-clock transfer advances on the falling edges of one DIV bit — bit 7 at the normal rate, bit 2 at
/// the Color fast rate (SC bit 1) — shifting one bit out every second falling edge, so resetting DIV perturbs (and can
/// speed up) a running transfer exactly as on hardware, and double speed doubles the rate for free. With no peer
/// attached each incoming bit is a one, so a completed transfer leaves SB at <c>0xFF</c>; the transfer bit (SC bit 7)
/// clears itself and the serial interrupt fires when the eighth bit shifts. An external-clock transfer has no peer to
/// clock it, so it stays pending. Stop mode freezes the port. All state is plain fields captured in a fixed order.
/// </summary>
public sealed class SerialComponent : ISerial, IClockedComponent, ISnapshotable {
    private const byte ClockSelect = 0x01;
    private const byte FastClock = 0x02;
    // The DIV counter bits whose falling edges clock the shifter: two edges per bit shifted.
    private const int FastDivBit = 2;
    private const int NormalDivBit = 7;
    private const byte MeaningfulMask = 0x83;
    private const byte TransferActive = 0x80;
    private const byte UnusedBits = 0x7C;

    private readonly IInterruptController m_interrupts;
    private readonly IKey1 m_key1;
    private readonly ITimer m_timer;

    private int m_bitsRemaining;
    private byte m_control;
    private byte m_data;
    private int m_edgeToggle;
    private bool m_lastDivBit;

    /// <summary>Creates the serial port wired to the interrupt controller it raises the serial line on, the timer whose
    /// DIV counter clocks its shifter, and the stop unit that freezes it.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="timer">The divider/timer block, read for the shift-clock edge.</param>
    /// <param name="key1">The speed-switch/stop unit.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public SerialComponent(IInterruptController interrupts, ITimer timer, IKey1 key1) {
        ArgumentNullException.ThrowIfNull(argument: interrupts);
        ArgumentNullException.ThrowIfNull(argument: timer);
        ArgumentNullException.ThrowIfNull(argument: key1);

        m_interrupts = interrupts;
        m_key1 = key1;
        m_timer = timer;
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;

    /// <summary>An optional observer invoked with the byte an internal-clock transfer sends, at the instant the transfer
    /// starts. It is a host-side observation seam — conformance harnesses use it to read a ROM's serial output — and is
    /// not emulated state: it is never serialized, so setting it cannot perturb determinism, and it is <see
    /// langword="null"/> in a normal run. A future bidirectional link peer will subsume it.</summary>
    public Action<byte>? ByteTransmitted { get; set; }

    /// <inheritdoc/>
    public void Tick() {
        if (m_key1.IsStopped) {
            return;
        }

        // The shift clock is followed every T-cycle so a transfer started mid-phase waits out the current DIV period.
        var bit = DivBit();
        var falling = (m_lastDivBit && !bit);

        m_lastDivBit = bit;

        // Only an internal-clock transfer in progress self-advances.
        if (!falling || ((m_control & (TransferActive | ClockSelect)) != (TransferActive | ClockSelect)) || (m_bitsRemaining == 0)) {
            return;
        }

        if (++m_edgeToggle < 2) {
            return;
        }

        m_edgeToggle = 0;

        // Shift one bit out; with no peer the incoming bit is a one.
        m_data = (byte)((m_data << 1) | 0x01);

        if (--m_bitsRemaining == 0) {
            m_control &= unchecked((byte)~TransferActive);

            m_interrupts.Request(kind: InterruptKind.Serial);
        }
    }
    /// <inheritdoc/>
    public byte ReadRegister(ushort address) =>
        (address == MemoryMap.SerialData) ? m_data : (byte)(m_control | UnusedBits);
    /// <inheritdoc/>
    public void WriteRegister(ushort address, byte value) {
        if (address == MemoryMap.SerialData) {
            m_data = value;

            return;
        }

        m_control = (byte)(value & MeaningfulMask);

        // A write that starts a transfer on the internal clock begins shifting from a fresh edge phase; an external-
        // clock transfer waits for a peer that never comes. Rewriting SC mid-transfer restarts the progress.
        if ((m_control & (TransferActive | ClockSelect)) == (TransferActive | ClockSelect)) {
            m_bitsRemaining = 8;
            m_edgeToggle = 0;

            // Surface the byte being sent for a host observer (e.g. a serial-text test-output reader). This is the value
            // latched in SB at the start of the transfer; the shift below overwrites it with incoming ones.
            ByteTransmitted?.Invoke(obj: m_data);
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteByte(value: m_data);
        writer.WriteByte(value: m_control);
        writer.WriteInt32(value: m_bitsRemaining);
        writer.WriteInt32(value: m_edgeToggle);
        writer.WriteBoolean(value: m_lastDivBit);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_data = reader.ReadByte();
        m_control = reader.ReadByte();
        m_bitsRemaining = reader.ReadInt32();
        m_edgeToggle = reader.ReadInt32();
        m_lastDivBit = reader.ReadBoolean();
    }

    // The DIV bit driving the shifter: the Color fast clock (SC bit 1) selects a bit 32x faster than the normal rate.
    private bool DivBit() {
        var bit = ((m_control & FastClock) != 0) ? FastDivBit : NormalDivBit;

        return (m_timer.DivCounter & (1 << bit)) != 0;
    }
}

using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The serial port, a CPU-domain clocked component. Its shift clock is not a private countdown but the DIV counter
/// itself: an internal-clock transfer advances on the falling edges of one DIV bit — bit 7 at the normal rate, bit 2 at
/// the Color fast rate (SC bit 1) — shifting one bit out every second falling edge, so resetting DIV perturbs (and can
/// speed up) a running transfer exactly as on hardware, and double speed doubles the rate for free. With no peer
/// attached each incoming bit is a one, so a completed transfer leaves SB at <c>0xFF</c>; the transfer bit (SC bit 7)
/// clears itself and the serial interrupt fires when the eighth bit shifts. An external-clock transfer waits for a
/// peer's clock edges — with no cable attached it stays pending forever. Stop mode freezes the port. All state is
/// plain fields captured in a fixed order.
/// <para>
/// The link cable: <see cref="SerialLinkSession"/> wires two ports as peers. The internally-clocked port then drives
/// the exchange — each time its shifter advances it pushes its outgoing bit into the peer and pulls the peer's
/// outgoing bit in, synchronously, inside its own tick. The peer's shifter is clocked by those edges (an armed
/// external-clock transfer counts its eight bits and completes with the serial interrupt exactly like a master
/// transfer; an idle port still shifts but raises nothing). The peer reference is host wiring, not emulated state — it
/// is never serialized, and both ports' transfer progress lives in their own snapshotted fields — but the synchronous
/// exchange means a linked pair MUST be advanced on one thread in a deterministic interleave, which the session owns.
/// </para>
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
    // The link peer (host wiring, never serialized — see the class remarks); null is the no-cable default.
    private SerialComponent? m_peer;

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
    /// langword="null"/> in a normal run. It observes alongside the link peer, never instead of it.</summary>
    public Action<byte>? ByteTransmitted { get; set; }

    /// <summary>Gets whether a link peer is attached (see <see cref="SerialLinkSession"/>).</summary>
    public bool IsLinked =>
        (m_peer is not null);

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

        // Shift one bit out, clocking the linked peer's shifter with the same edge (the simultaneous exchange a real
        // cable performs); with no peer the incoming bit is a one.
        var outgoing = ((m_data & 0x80) != 0);
        var incoming = (m_peer?.ShiftFromPeerClock(incoming: outgoing) ?? true);

        m_data = (byte)((m_data << 1) | (incoming ? 0x01 : 0x00));

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

        // A write that starts a transfer arms the eight-bit counter. On the internal clock the shifter begins from a
        // fresh edge phase and this port drives the exchange; on the external clock the transfer waits for a linked
        // peer's edges (with no cable attached it stays pending forever). Rewriting SC mid-transfer restarts the
        // progress either way.
        if ((m_control & TransferActive) == TransferActive) {
            m_bitsRemaining = 8;
            m_edgeToggle = 0;

            // Surface the byte an internal-clock transfer sends for a host observer (e.g. a serial-text test-output
            // reader). This is the value latched in SB at the start of the transfer; the shifting overwrites it with
            // the incoming bits.
            if ((m_control & ClockSelect) == ClockSelect) {
                ByteTransmitted?.Invoke(obj: m_data);
            }
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

    // Wires two ports as link peers. Internal (not public) on purpose: SerialLinkSession is the one blessed connect
    // seam, because a connected pair must also be STEPPED as a pair — the session owns both halves.
    internal static void Connect(SerialComponent first, SerialComponent second) {
        if (ReferenceEquals(objA: first, objB: second)) {
            throw new ArgumentException(message: "A serial port cannot be linked to itself.", paramName: nameof(second));
        }

        if ((first.m_peer is not null) || (second.m_peer is not null)) {
            throw new InvalidOperationException(message: "A serial port is already linked; disconnect its session first.");
        }

        first.m_peer = second;
        second.m_peer = first;
    }
    // Severs a port's link, clearing both ends; a no-op for an unlinked port.
    internal static void Disconnect(SerialComponent port) {
        if (port.m_peer is { } peer) {
            peer.m_peer = null;
            port.m_peer = null;
        }
    }

    // One serial clock edge arriving over the cable from the peer's internal clock: exchange one bit. Returns this
    // port's outgoing bit (its shifter's MSB) and shifts the incoming bit in, mirroring the simultaneous exchange of
    // the hardware shift registers. An armed external-clock transfer counts the edge and completes (SC bit 7 clears,
    // the serial interrupt fires) on the eighth; an idle port still shifts but raises nothing. A stopped port is
    // frozen (the line reads idle-high, nothing shifts), and a port driving its OWN internal clock ignores the peer's
    // edges — two masters on one cable each keep their own transfer consistent, deterministically.
    private bool ShiftFromPeerClock(bool incoming) {
        if (m_key1.IsStopped) {
            return true;
        }

        if ((m_control & (TransferActive | ClockSelect)) == (TransferActive | ClockSelect)) {
            return ((m_data & 0x80) != 0);
        }

        var outgoing = ((m_data & 0x80) != 0);

        m_data = (byte)((m_data << 1) | (incoming ? 0x01 : 0x00));

        if (((m_control & TransferActive) == TransferActive) && (m_bitsRemaining > 0) && (--m_bitsRemaining == 0)) {
            m_control &= unchecked((byte)~TransferActive);

            m_interrupts.Request(kind: InterruptKind.Serial);
        }

        return outgoing;
    }

    // The DIV bit driving the shifter: the Color fast clock (SC bit 1) selects a bit 32x faster than the normal rate.
    private bool DivBit() {
        var bit = ((m_control & FastClock) != 0) ? FastDivBit : NormalDivBit;

        return (m_timer.DivCounter & (1 << bit)) != 0;
    }
}

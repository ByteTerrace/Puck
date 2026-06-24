namespace Puck.HumbleGamingBrick;

/// <summary>
/// The serial port: the transfer data register (<c>SB</c>, <c>0xFF01</c>) and the control register (<c>SC</c>,
/// <c>0xFF02</c>). Starting a transfer with the internal clock (<c>SC</c> bit 7 and bit 0 set) shifts the eight
/// bits of <c>SB</c> out at 8192&#160;Hz; with no link partner connected, ones are shifted in, so <c>SB</c> ends
/// at <c>0xFF</c> and the serial interrupt fires. The byte presented at transfer start is surfaced through
/// <see cref="ByteTransmitted"/>, which is how test ROMs that print results over the link cable are captured.
/// <para>
/// The 8192&#160;Hz shift clock is not a counter started when <c>SC</c> is written: it is divided from the shared
/// system counter (bit 8, whose falling edge recurs every 512 T-cycles), so a transfer's bit edges — and thus its
/// completion — align to the counter's phase (and to <c>DIV</c> resets), not to the moment <c>SC</c> was written.
/// </para>
/// </summary>
public sealed class Serial : ISerial {
    // The system-counter bit whose falling edge clocks one serial bit: bit 8 recurs every 512 T-cycles = 8192 Hz.
    private const int ClockBitMask = 0x100;

    private readonly IInterruptController m_interrupts;
    private readonly Func<int> m_systemCounter;

    private bool m_lastClockBit;
    private byte m_control;
    private byte m_data;
    private int m_bitsRemaining;

    /// <inheritdoc />
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <summary>Gets or sets a callback invoked with each byte as a transfer begins, for capturing serial output.</summary>
    public Action<byte>? ByteTransmitted { get; set; }

    /// <summary>Initializes the serial port wired to the interrupt controller it raises the serial interrupt through,
    /// and to the divider/timer its shift clock is divided from.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <param name="timer">The divider/timer whose internal counter drives the serial shift clock.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Serial(IInterruptController interrupts, ITimer timer) {
        ArgumentNullException.ThrowIfNull(interrupts);
        ArgumentNullException.ThrowIfNull(timer);

        m_interrupts = interrupts;
        m_systemCounter = () => timer.InternalCounter;
    }

    /// <summary>Reads the transfer data register (<c>SB</c>).</summary>
    public byte ReadData() =>
        m_data;
    /// <summary>Reads the control register (<c>SC</c>) with the unused bits set.</summary>
    public byte ReadControl() =>
        (byte)(m_control | 0x7E);
    /// <summary>Writes the transfer data register (<c>SB</c>).</summary>
    /// <param name="value">The value written.</param>
    public void WriteData(byte value) =>
        m_data = value;
    /// <summary>Writes the control register (<c>SC</c>), starting a transfer when the start and internal-clock bits are set.</summary>
    /// <param name="value">The value written.</param>
    public void WriteControl(byte value) {
        m_control = (byte)(value & 0x81);

        // Starting a transfer arms the eight shifts but does NOT reset the clock; the first shift lands on the next
        // falling edge of the system counter's serial-clock bit, which is what aligns completion to the counter phase.
        if ((m_control & 0x81) == 0x81) {
            m_bitsRemaining = 8;
            ByteTransmitted?.Invoke(obj: m_data);
        }
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        // Sample the serial-clock bit every machine cycle (whether or not a transfer is active) so the falling-edge
        // baseline stays current; the timer is clocked before the serial each machine cycle, so the counter already
        // reflects this cycle. A bit is shifted on each falling edge while a transfer is in progress.
        var clockBit = ((m_systemCounter() & ClockBitMask) != 0);

        if (m_lastClockBit && !clockBit && (m_bitsRemaining > 0)) {
            // Shift one bit out; with no connected partner a one is shifted in.
            m_data = (byte)((m_data << 1) | 1);
            m_bitsRemaining -= 1;

            if (m_bitsRemaining == 0) {
                m_control &= 0x7F;
                m_interrupts.Request(kind: InterruptKind.Serial);
            }
        }

        m_lastClockBit = clockBit;
    }
}

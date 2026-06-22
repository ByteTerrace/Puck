namespace Puck.GameBoy;

/// <summary>
/// The serial port: the transfer data register (<c>SB</c>, <c>0xFF01</c>) and the control register (<c>SC</c>,
/// <c>0xFF02</c>). Starting a transfer with the internal clock (<c>SC</c> bit 7 and bit 0 set) shifts the eight
/// bits of <c>SB</c> out at 8192&#160;Hz; with no link partner connected, ones are shifted in, so <c>SB</c> ends
/// at <c>0xFF</c> and the serial interrupt fires. The byte presented at transfer start is surfaced through
/// <see cref="ByteTransmitted"/>, which is how test ROMs that print results over the link cable are captured.
/// </summary>
public sealed class Serial : IClockedComponent {
    private const int TCyclesPerBit = 512;

    private readonly InterruptController m_interrupts;

    private byte m_control;
    private byte m_data;
    private int m_bitsRemaining;
    private int m_counter;

    /// <inheritdoc />
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <summary>Gets or sets a callback invoked with each byte as a transfer begins, for capturing serial output.</summary>
    public Action<byte>? ByteTransmitted { get; set; }

    /// <summary>Initializes the serial port wired to the interrupt controller it raises the serial interrupt through.</summary>
    /// <param name="interrupts">The interrupt controller.</param>
    /// <exception cref="ArgumentNullException"><paramref name="interrupts"/> is <see langword="null"/>.</exception>
    public Serial(InterruptController interrupts) {
        ArgumentNullException.ThrowIfNull(interrupts);

        m_interrupts = interrupts;
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

        if ((m_control & 0x81) == 0x81) {
            m_bitsRemaining = 8;
            m_counter = TCyclesPerBit;
            ByteTransmitted?.Invoke(obj: m_data);
        }
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        if (m_bitsRemaining == 0) {
            return;
        }

        m_counter -= tCycles;

        while ((m_counter <= 0) && (m_bitsRemaining > 0)) {
            m_counter += TCyclesPerBit;
            // Shift one bit out; with no connected partner a one is shifted in.
            m_data = (byte)((m_data << 1) | 1);
            m_bitsRemaining -= 1;

            if (m_bitsRemaining == 0) {
                m_control &= 0x7F;
                m_interrupts.Request(kind: InterruptKind.Serial);
            }
        }
    }
}

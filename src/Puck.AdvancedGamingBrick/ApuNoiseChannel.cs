namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The noise PSG channel: a 15- or 7-bit linear-feedback shift register clocked at a divisor/shift-derived
/// rate, gated by a volume envelope and a length counter. The output is high whenever the register's low bit
/// is clear.
/// </summary>
public sealed partial class ApuNoiseChannel {
    private static readonly int[] Divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };

    private int m_frequencyTimer;
    private ApuLengthCounter m_length;
    private ApuEnvelopeUnit m_envelope;
    private int m_divisorCode;
    private int m_shiftClock;
    private bool m_widthMode;
    private bool m_dacEnabled;
    private bool m_enabled;
    private ushort m_lfsr = 0x7FFF;

    /// <summary>Gets a value indicating whether the channel is currently producing sound.</summary>
    public bool Active => (m_enabled && m_dacEnabled);
    /// <summary>Gets the current output amplitude, 0–15.</summary>
    public int Output => ((Active && ((m_lfsr & 1) == 0))
        ? m_envelope.Volume
        : 0);

    /// <summary>Advances the LFSR when the frequency timer expires.</summary>
    /// <param name="cycles">Master clock cycles to advance.</param>
    public void Step(int cycles) {
        m_frequencyTimer -= cycles;

        while (m_frequencyTimer <= 0) {
            m_frequencyTimer += ((Divisors[m_divisorCode] << m_shiftClock) * 4); // ×4 for the AGB master clock

            var feedback = (m_lfsr ^ (m_lfsr >> 1)) & 1;

            m_lfsr = ((ushort)((m_lfsr >> 1) | (feedback << 14)));

            if (m_widthMode) {
                m_lfsr = ((ushort)((m_lfsr & ~0x40) | (feedback << 6)));
            }
        }
    }
    /// <summary>Reads back the envelope register (NR42): initial volume, direction, and period.</summary>
    public byte ReadEnvelope() =>
        m_envelope.Read();
    /// <summary>Reads back the polynomial register (NR43): divisor, width mode, and shift clock.</summary>
    public byte ReadPolynomial() => ((byte)(m_divisorCode | (m_widthMode
        ? 0x8
        : 0) | (m_shiftClock << 4)));
    /// <summary>Reads back NR44's length-enable bit (the only readable bit).</summary>
    public byte ReadControl() => ((byte)(m_length.Enabled
        ? 0x40
        : 0));
    /// <summary>Reloads the length counter (NR41).</summary>
    public void WriteLength(byte value) {
        m_length.Counter = (64 - (value & 0x3F));
    }
    /// <summary>Sets the envelope (NR42); clearing the upper five bits disables the DAC.</summary>
    public void WriteEnvelope(byte value) {
        m_dacEnabled = m_envelope.Write(value: value);

        if (!m_dacEnabled) {
            m_enabled = false;
        }
    }
    /// <summary>Sets the divisor, LFSR width, and shift clock (NR43).</summary>
    public void WritePolynomial(byte value) {
        m_divisorCode = value & 0x7;
        m_widthMode = ((value & 0x8) != 0);
        m_shiftClock = (value >> 4) & 0xF;
    }
    /// <summary>Sets control (NR44); bit 7 triggers the channel, bit 6 enables the length counter.</summary>
    public void WriteControl(byte value) {
        m_length.Enabled = ((value & 0x40) != 0);

        if ((value & 0x80) != 0) {
            m_enabled = m_dacEnabled;
            m_lfsr = 0x7FFF;
            m_envelope.Trigger();
            m_frequencyTimer = ((Divisors[m_divisorCode] << m_shiftClock) * 4);

            if (m_length.Counter == 0) {
                m_length.Counter = 64;
            }
        }
    }
    /// <summary>Clocks the length counter (256&#160;Hz).</summary>
    public void ClockLength() {
        if (m_length.Clock()) {
            m_enabled = false;
        }
    }
    /// <summary>Clocks the volume envelope (64&#160;Hz).</summary>
    public void ClockEnvelope() =>
        m_envelope.Clock();
}

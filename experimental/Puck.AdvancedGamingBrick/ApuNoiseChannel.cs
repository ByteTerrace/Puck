namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The noise PSG channel: a 15- or 7-bit linear-feedback shift register clocked at a divisor/shift-derived
/// rate, gated by a volume envelope and a length counter. The output is high whenever the register's low bit
/// is clear.
/// </summary>
public sealed partial class ApuNoiseChannel {
    private static readonly int[] s_divisors = { 8, 16, 32, 48, 64, 80, 96, 112 };

    private int m_frequencyTimer;
    private int m_lengthCounter;
    private int m_envelopeVolume;
    private int m_envelopeInitial;
    private int m_envelopePeriod;
    private int m_envelopeTimer;
    private int m_divisorCode;
    private int m_shiftClock;
    private bool m_envelopeIncrease;
    private bool m_widthMode;
    private bool m_dacEnabled;
    private bool m_enabled;
    private bool m_lengthEnabled;
    private ushort m_lfsr = 0x7FFF;

    /// <summary>Gets a value indicating whether the channel is currently producing sound.</summary>
    public bool Active => m_enabled && m_dacEnabled;

    /// <summary>Gets the current output amplitude, 0–15.</summary>
    public int Output => (Active && ((m_lfsr & 1) == 0))
        ? m_envelopeVolume
        : 0;

    /// <summary>Advances the LFSR when the frequency timer expires.</summary>
    /// <param name="cycles">Master clock cycles to advance.</param>
    public void Step(int cycles) {
        m_frequencyTimer -= cycles;

        while (m_frequencyTimer <= 0) {
            m_frequencyTimer += (s_divisors[m_divisorCode] << m_shiftClock) * 4; // ×4 for the GBA master clock

            var feedback = (m_lfsr ^ (m_lfsr >> 1)) & 1;

            m_lfsr = (ushort)((m_lfsr >> 1) | (feedback << 14));

            if (m_widthMode) {
                m_lfsr = (ushort)((m_lfsr & ~0x40) | (feedback << 6));
            }
        }
    }

    /// <summary>Reads back the envelope register (NR42): initial volume, direction, and period.</summary>
    public byte ReadEnvelope() => (byte)((m_envelopeInitial << 4) | (m_envelopeIncrease ? 0x8 : 0) | m_envelopePeriod);

    /// <summary>Reads back the polynomial register (NR43): divisor, width mode, and shift clock.</summary>
    public byte ReadPolynomial() => (byte)(m_divisorCode | (m_widthMode ? 0x8 : 0) | (m_shiftClock << 4));

    /// <summary>Reads back NR44's length-enable bit (the only readable bit).</summary>
    public byte ReadControl() => (byte)(m_lengthEnabled ? 0x40 : 0);

    /// <summary>Reloads the length counter (NR41).</summary>
    public void WriteLength(byte value) {
        m_lengthCounter = 64 - (value & 0x3F);
    }

    /// <summary>Sets the envelope (NR42); clearing the upper five bits disables the DAC.</summary>
    public void WriteEnvelope(byte value) {
        m_envelopeInitial = (value >> 4) & 0xF;
        m_envelopeIncrease = (value & 0x8) != 0;
        m_envelopePeriod = value & 0x7;
        m_dacEnabled = (value & 0xF8) != 0;

        if (!m_dacEnabled) {
            m_enabled = false;
        }
    }

    /// <summary>Sets the divisor, LFSR width, and shift clock (NR43).</summary>
    public void WritePolynomial(byte value) {
        m_divisorCode = value & 0x7;
        m_widthMode = (value & 0x8) != 0;
        m_shiftClock = (value >> 4) & 0xF;
    }

    /// <summary>Sets control (NR44); bit 7 triggers the channel, bit 6 enables the length counter.</summary>
    public void WriteControl(byte value) {
        m_lengthEnabled = (value & 0x40) != 0;

        if ((value & 0x80) != 0) {
            m_enabled = m_dacEnabled;
            m_lfsr = 0x7FFF;
            m_envelopeVolume = m_envelopeInitial;
            m_envelopeTimer = m_envelopePeriod;
            m_frequencyTimer = (s_divisors[m_divisorCode] << m_shiftClock) * 4;

            if (m_lengthCounter == 0) {
                m_lengthCounter = 64;
            }
        }
    }

    /// <summary>Clocks the length counter (256&#160;Hz).</summary>
    public void ClockLength() {
        if (m_lengthEnabled && (m_lengthCounter > 0) && (--m_lengthCounter == 0)) {
            m_enabled = false;
        }
    }

    /// <summary>Clocks the volume envelope (64&#160;Hz).</summary>
    public void ClockEnvelope() {
        if (m_envelopePeriod == 0) {
            return;
        }

        if (--m_envelopeTimer <= 0) {
            m_envelopeTimer = m_envelopePeriod;

            if (m_envelopeIncrease && (m_envelopeVolume < 15)) {
                ++m_envelopeVolume;
            }
            else if (!m_envelopeIncrease && (m_envelopeVolume > 0)) {
                --m_envelopeVolume;
            }
        }
    }
}

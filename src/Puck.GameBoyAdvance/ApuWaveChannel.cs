namespace Puck.GameBoyAdvance;

/// <summary>
/// The wave PSG channel: it plays 32 four-bit samples from wave RAM at a programmable rate, scaled by a coarse
/// volume, gated by a length counter. (Single 32-sample bank; the Advance's optional 64-sample two-bank mode
/// is a follow-up.) A sample lasts (2048 − frequency) × 8 master cycles.
/// </summary>
public sealed class ApuWaveChannel {
    private readonly byte[] m_waveRam;

    private int m_frequency;
    private int m_frequencyTimer;
    private int m_samplePosition;
    private int m_lengthCounter;
    private int m_volumeShift;
    private bool m_dacEnabled;
    private bool m_enabled;
    private bool m_lengthEnabled;

    /// <summary>Creates the wave channel over the shared 16-byte wave-RAM buffer (32 four-bit samples).</summary>
    /// <param name="waveRam">The wave-RAM bytes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="waveRam"/> is <see langword="null"/>.</exception>
    public ApuWaveChannel(byte[] waveRam) {
        ArgumentNullException.ThrowIfNull(waveRam);

        m_waveRam = waveRam;
    }

    /// <summary>Gets a value indicating whether the channel is currently producing sound.</summary>
    public bool Active => m_enabled && m_dacEnabled;

    /// <summary>Gets the current output amplitude, 0–15.</summary>
    public int Output {
        get {
            if (!Active) {
                return 0;
            }

            var packed = m_waveRam[(m_samplePosition >> 1) & 0xF];
            var nibble = ((m_samplePosition & 1) == 0) ? (packed >> 4) : (packed & 0xF);

            return nibble >> m_volumeShift;
        }
    }

    /// <summary>Advances the sample position when the frequency timer expires.</summary>
    /// <param name="cycles">Master clock cycles to advance.</param>
    public void Step(int cycles) {
        m_frequencyTimer -= cycles;

        while (m_frequencyTimer <= 0) {
            m_frequencyTimer += (2048 - m_frequency) * 8;
            m_samplePosition = (m_samplePosition + 1) & 31;
        }
    }

    /// <summary>Sets the DAC enable (NR30); clearing it silences the channel.</summary>
    public void WriteEnable(byte value) {
        m_dacEnabled = (value & 0x80) != 0;

        if (!m_dacEnabled) {
            m_enabled = false;
        }
    }

    /// <summary>Reloads the length counter (NR31).</summary>
    public void WriteLength(byte value) {
        m_lengthCounter = 256 - value;
    }

    /// <summary>Sets the coarse output volume (NR32): mute / 100% / 50% / 25%.</summary>
    public void WriteVolume(byte value) {
        m_volumeShift = ((value >> 5) & 0x3) switch {
            1 => 0,
            2 => 1,
            3 => 2,
            _ => 4, // 0 → mute (a 4-bit sample shifted right by 4 is always 0)
        };
    }

    /// <summary>Sets the low byte of the frequency (NR33).</summary>
    public void WriteFrequencyLow(byte value) {
        m_frequency = (m_frequency & 0x700) | value;
    }

    /// <summary>Sets the high frequency bits and control (NR34); bit 7 triggers the channel.</summary>
    public void WriteControl(byte value) {
        m_frequency = (m_frequency & 0xFF) | ((value & 0x7) << 8);
        m_lengthEnabled = (value & 0x40) != 0;

        if ((value & 0x80) != 0) {
            m_enabled = m_dacEnabled;
            m_samplePosition = 0;
            m_frequencyTimer = (2048 - m_frequency) * 8;

            if (m_lengthCounter == 0) {
                m_lengthCounter = 256;
            }
        }
    }

    /// <summary>Clocks the length counter (256&#160;Hz), disabling the channel when it reaches zero.</summary>
    public void ClockLength() {
        if (m_lengthEnabled && (m_lengthCounter > 0) && (--m_lengthCounter == 0)) {
            m_enabled = false;
        }
    }
}

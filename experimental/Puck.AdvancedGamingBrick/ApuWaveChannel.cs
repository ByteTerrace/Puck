namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The wave PSG channel: it plays four-bit samples from wave RAM at a programmable rate, scaled by a coarse
/// volume, gated by a length counter. The Advanced GamingBrick carries <em>two</em> banks of 32 samples (64 total):
/// NR30 bit 5 selects single-bank (32-sample) or double-bank (64-sample) playback, and bit 6 selects which bank
/// plays first. The CPU's wave-RAM window (0x90–0x9F) addresses the bank that is <em>not</em> currently selected
/// for playback, so a game can prepare the next bank while one plays. A sample lasts (2048 − frequency) × 8
/// master cycles.
/// </summary>
public sealed class ApuWaveChannel {
    // Two 16-byte banks (32 four-bit samples each) = 64 samples total.
    private readonly byte[] m_waveRam = new byte[0x20];

    private int m_frequency;
    private int m_frequencyTimer;
    private int m_samplePosition;
    private int m_lengthCounter;
    private int m_volumeShift;
    private bool m_forceVolume75; // NR32 bit 7 (GBA-only): forces 75% regardless of the 2-bit volume field
    private bool m_dacEnabled;
    private bool m_enabled;
    private bool m_lengthEnabled;
    private bool m_twoBank;       // NR30 bit 5: 0 = single 32-sample bank, 1 = double 64-sample
    private int m_bank;           // NR30 bit 6: the bank that plays first

    /// <summary>Creates the wave channel with its own two-bank wave RAM.</summary>
    public ApuWaveChannel() {
    }

    /// <summary>Gets a value indicating whether the channel is currently producing sound.</summary>
    public bool Active => m_enabled && m_dacEnabled;

    /// <summary>Reads a wave-RAM byte through the CPU window (0x90–0x9F), which addresses the bank not playing.</summary>
    /// <param name="index">The byte index within the 16-byte window (0–15).</param>
    public byte ReadRam(int index) => m_waveRam[CpuBankBase + (index & 0xF)];

    /// <summary>Writes a wave-RAM byte through the CPU window (0x90–0x9F).</summary>
    /// <param name="index">The byte index within the 16-byte window (0–15).</param>
    /// <param name="value">The byte to store.</param>
    public void WriteRam(int index, byte value) => m_waveRam[CpuBankBase + (index & 0xF)] = value;

    // The CPU accesses the bank opposite the one currently selected for playback (bank 0 → window shows bank 1).
    private int CpuBankBase => (m_bank ^ 1) * 0x10;

    /// <summary>Gets the current output amplitude, 0–15.</summary>
    public int Output {
        get {
            if (!Active) {
                return 0;
            }

            // In single-bank mode the 32-sample position indexes the selected bank; in double-bank mode the
            // 64-sample position spans both banks starting at the selected one.
            var pos = m_twoBank ? m_samplePosition : ((m_bank * 32) + m_samplePosition);
            var packed = m_waveRam[(pos >> 1) & 0x1F];
            var nibble = ((pos & 1) == 0) ? (packed >> 4) : (packed & 0xF);

            return m_forceVolume75 ? ((nibble * 3) >> 2) : (nibble >> m_volumeShift);
        }
    }

    /// <summary>Advances the sample position when the frequency timer expires.</summary>
    /// <param name="cycles">Master clock cycles to advance.</param>
    public void Step(int cycles) {
        m_frequencyTimer -= cycles;

        while (m_frequencyTimer <= 0) {
            m_frequencyTimer += (2048 - m_frequency) * 8;

            var limit = m_twoBank ? 64 : 32;

            if (++m_samplePosition >= limit) {
                m_samplePosition = 0;

                // In double-bank mode the playing bank flips each time the 64-sample sweep wraps.
                if (m_twoBank) {
                    m_bank ^= 1;
                }
            }
        }
    }

    /// <summary>Sets the DAC enable, bank mode, and bank number (NR30); clearing the DAC silences the channel.</summary>
    public void WriteEnable(byte value) {
        m_twoBank = (value & 0x20) != 0;
        m_bank = (value >> 6) & 0x1;
        m_dacEnabled = (value & 0x80) != 0;

        if (!m_dacEnabled) {
            m_enabled = false;
        }
    }

    /// <summary>Reads back NR30 (DAC/bank mode/bank number).</summary>
    public byte ReadEnable() => (byte)((m_twoBank ? 0x20 : 0) | (m_bank << 6) | (m_dacEnabled ? 0x80 : 0));

    /// <summary>Reloads the length counter (NR31).</summary>
    public void WriteLength(byte value) {
        m_lengthCounter = 256 - value;
    }

    /// <summary>Sets the coarse output volume (NR32): mute / 100% / 50% / 25%, or the GBA 75% override (bit 7).</summary>
    public void WriteVolume(byte value) {
        m_forceVolume75 = (value & 0x80) != 0;
        m_volumeShift = ((value >> 5) & 0x3) switch {
            1 => 0,
            2 => 1,
            3 => 2,
            _ => 4, // 0 → mute (a 4-bit sample shifted right by 4 is always 0)
        };
    }

    /// <summary>Reads back NR32 (volume field + the 75% override bit).</summary>
    public byte ReadVolume() {
        var field = m_volumeShift switch { 0 => 1, 1 => 2, 2 => 3, _ => 0 };

        return (byte)((field << 5) | (m_forceVolume75 ? 0x80 : 0));
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

    /// <summary>Reads back NR34's length-enable bit (the only readable bit).</summary>
    public byte ReadControl() => (byte)(m_lengthEnabled ? 0x40 : 0);

    /// <summary>Clocks the length counter (256&#160;Hz), disabling the channel when it reaches zero.</summary>
    public void ClockLength() {
        if (m_lengthEnabled && (m_lengthCounter > 0) && (--m_lengthCounter == 0)) {
            m_enabled = false;
        }
    }
}

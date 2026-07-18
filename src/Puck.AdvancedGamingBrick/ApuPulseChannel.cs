namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A square-wave PSG channel: an 8-step duty waveform gated by a volume envelope and an optional length
/// counter, with channel 1 additionally carrying a frequency sweep. The frequency timer is counted in master
/// clock cycles — a duty step lasts (2048 − frequency) × 16 of them, four times the Humble GamingBrick period
/// because the Advanced GamingBrick's master clock is four times faster.
/// </summary>
public sealed partial class ApuPulseChannel {
    private static readonly byte[] s_dutyPatterns = { 0b00000001, 0b00000011, 0b00001111, 0b11111100 };
    private readonly bool m_hasSweep;
    private int m_dutyPattern;
    private int m_dutyStep;
    private int m_frequency;
    private int m_frequencyTimer;
    private int m_lengthCounter;
    private bool m_lengthEnabled;
    private int m_envelopeVolume;
    private int m_envelopeInitial;
    private bool m_envelopeIncrease;
    private int m_envelopePeriod;
    private int m_envelopeTimer;
    private bool m_dacEnabled;
    private bool m_enabled;
    private int m_sweepPeriod;
    private bool m_sweepDecrease;
    private int m_sweepShift;
    private int m_sweepTimer;
    private int m_sweepShadow;
    private bool m_sweepActive;

    /// <summary>Creates a pulse channel.</summary>
    /// <param name="hasSweep">Whether this is channel 1 (which carries the frequency sweep unit).</param>
    public ApuPulseChannel(bool hasSweep) {
        m_hasSweep = hasSweep;
    }

    /// <summary>Gets a value indicating whether the channel is currently producing sound.</summary>
    public bool Active => (m_enabled && m_dacEnabled);

    /// <summary>Gets the current output amplitude, 0–15.</summary>
    public int Output {
        get {
            if (!Active) {
                return 0;
            }

            return ((((s_dutyPatterns[m_dutyPattern] >> m_dutyStep) & 1) != 0)
                ? m_envelopeVolume
                : 0);
        }
    }

    /// <summary>Advances the frequency timer, stepping the duty position when it expires.</summary>
    /// <param name="cycles">Master clock cycles to advance.</param>
    public void Step(int cycles) {
        m_frequencyTimer -= cycles;

        while (m_frequencyTimer <= 0) {
            m_frequencyTimer += ((2048 - m_frequency) * 16);
            m_dutyStep = (m_dutyStep + 1) & 7;
        }
    }

    /// <summary>Sets the sweep parameters (channel 1, NR10 / 0x60).</summary>
    public void WriteSweep(ushort value) {
        m_sweepPeriod = (value >> 4) & 0x7;
        m_sweepDecrease = ((value & 0x8) != 0);
        m_sweepShift = value & 0x7;
    }

    /// <summary>Reads back the sweep register (NR10): shift, direction, and period (all bits readable).</summary>
    public byte ReadSweep() => (byte)((m_sweepPeriod << 4) | (m_sweepDecrease ? 0x8 : 0) | m_sweepShift);

    /// <summary>Reads back the duty field of NRx1 (the length sub-field is write-only and reads as zero).</summary>
    public byte ReadDutyLength() => (byte)(m_dutyPattern << 6);

    /// <summary>Reads back the envelope register (NRx2): initial volume, direction, and period.</summary>
    public byte ReadEnvelope() => (byte)((m_envelopeInitial << 4) | (m_envelopeIncrease ? 0x8 : 0) | m_envelopePeriod);

    /// <summary>Reads back NRx4's length-enable bit (the only readable bit).</summary>
    public byte ReadControl() => (byte)(m_lengthEnabled ? 0x40 : 0);

    /// <summary>Sets duty and reloads the length counter (NRx1).</summary>
    public void WriteDutyLength(byte value) {
        m_dutyPattern = (value >> 6) & 0x3;
        m_lengthCounter = (64 - (value & 0x3F));
    }

    /// <summary>Sets the envelope (NRx2); clearing the upper five bits disables the channel's DAC.</summary>
    public void WriteEnvelope(byte value) {
        m_envelopeInitial = (value >> 4) & 0xF;
        m_envelopeIncrease = ((value & 0x8) != 0);
        m_envelopePeriod = value & 0x7;
        m_dacEnabled = ((value & 0xF8) != 0);

        if (!m_dacEnabled) {
            m_enabled = false;
        }
    }

    /// <summary>Sets the low byte of the frequency (NRx3).</summary>
    public void WriteFrequencyLow(byte value) {
        m_frequency = (m_frequency & 0x700) | value;
    }

    /// <summary>Sets the high frequency bits and control (NRx4); bit 7 triggers (restarts) the channel.</summary>
    public void WriteControl(byte value) {
        m_frequency = (m_frequency & 0xFF) | ((value & 0x7) << 8);
        m_lengthEnabled = ((value & 0x40) != 0);

        if ((value & 0x80) != 0) {
            Trigger();
        }
    }

    /// <summary>Clocks the length counter (256&#160;Hz), disabling the channel when it reaches zero.</summary>
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
            } else if (!m_envelopeIncrease && (m_envelopeVolume > 0)) {
                --m_envelopeVolume;
            }
        }
    }

    /// <summary>Clocks the frequency sweep (128&#160;Hz, channel 1 only).</summary>
    public void ClockSweep() {
        if (!m_hasSweep || !m_sweepActive || (--m_sweepTimer > 0)) {
            return;
        }

        m_sweepTimer = ((m_sweepPeriod == 0) ? 8 : m_sweepPeriod);

        if (m_sweepPeriod == 0) {
            return;
        }

        var next = ComputeSweep();

        if ((next <= 2047) && (m_sweepShift > 0)) {
            m_sweepShadow = next;
            m_frequency = next;

            // A second calculation checks for overflow (which disables the channel).
            if (ComputeSweep() > 2047) {
                m_enabled = false;
            }
        } else if (next > 2047) {
            m_enabled = false;
        }
    }

    private int ComputeSweep() {
        var delta = (m_sweepShadow >> m_sweepShift);

        return (m_sweepDecrease
            ? (m_sweepShadow - delta)
            : (m_sweepShadow + delta));
    }
    private void Trigger() {
        m_enabled = m_dacEnabled;

        if (m_lengthCounter == 0) {
            m_lengthCounter = 64;
        }

        m_frequencyTimer = ((2048 - m_frequency) * 16);
        m_envelopeVolume = m_envelopeInitial;
        m_envelopeTimer = m_envelopePeriod;

        if (m_hasSweep) {
            m_sweepShadow = m_frequency;
            m_sweepTimer = ((m_sweepPeriod == 0) ? 8 : m_sweepPeriod);
            m_sweepActive = ((m_sweepPeriod > 0) || (m_sweepShift > 0));

            if ((m_sweepShift > 0) && (ComputeSweep() > 2047)) {
                m_enabled = false;
            }
        }
    }
}

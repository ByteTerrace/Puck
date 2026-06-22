namespace Puck.GameBoy;

/// <summary>
/// The noise channel: a linear-feedback shift register clocked at a programmable rate produces a pseudo-random
/// bit stream, gated by a volume envelope and an optional length counter. A width-mode bit shortens the register
/// from 15 to 7 stages for a coarser, more periodic tone.
/// </summary>
internal sealed class NoiseChannel {
    // The base divisors selected by NR43 bits 0-2 (index 0 is 8, not 0).
    private static readonly int[] s_divisors = [8, 16, 32, 48, 64, 80, 96, 112];

    private byte m_nr1;
    private byte m_nr2;
    private byte m_nr3;
    private byte m_nr4;
    private bool m_enabled;
    private int m_lengthCounter;
    private int m_frequencyTimer;
    private int m_envelopeVolume;
    private int m_envelopeTimer;
    private int m_lfsr;

    /// <summary>Gets whether the channel is currently producing sound (the <c>NR52</c> status bit).</summary>
    public bool Enabled =>
        m_enabled;
    /// <summary>Gets the channel's current digital output level (0-15), or 0 when off or its DAC is disabled.</summary>
    public int Output =>
        ((m_enabled && DacEnabled && ((~m_lfsr & 0x01) != 0))
            ? m_envelopeVolume
            : 0);
    private bool DacEnabled =>
        ((m_nr2 & 0xF8) != 0);

    /// <summary>Reads one of the channel's four registers (raw; the APU applies the read mask).</summary>
    /// <param name="register">The register index 1-4 (<c>NR41</c>-<c>NR44</c>); index 0 is unused.</param>
    public byte ReadRegister(int register) =>
        register switch {
            1 => m_nr1,
            2 => m_nr2,
            3 => m_nr3,
            _ => m_nr4,
        };
    /// <summary>Writes one of the channel's four registers, updating the derived state and triggering on <c>NR44</c> bit 7.</summary>
    /// <param name="register">The register index 1-4 (<c>NR41</c>-<c>NR44</c>); index 0 is unused.</param>
    /// <param name="value">The value written.</param>
    /// <param name="nextStepClocksLength">Whether the frame sequencer's next step will clock the length counter, for the obscure extra-length-clock behavior.</param>
    public void WriteRegister(int register, byte value, bool nextStepClocksLength) {
        switch (register) {
            case 1:
                m_nr1 = value;
                m_lengthCounter = (64 - (value & 0x3F));

                break;
            case 2:
                m_nr2 = value;

                if (!DacEnabled) {
                    m_enabled = false;
                }

                break;
            case 3:
                m_nr3 = value;

                break;
            default:
                var wasLengthEnabled = ((m_nr4 & 0x40) != 0);

                m_nr4 = value;

                var lengthEnabled = ((value & 0x40) != 0);

                // Obscure behavior: enabling length during a non-length-clocking step clocks the counter once.
                if (!nextStepClocksLength && !wasLengthEnabled && lengthEnabled && (m_lengthCounter > 0)) {
                    m_lengthCounter -= 1;

                    if ((m_lengthCounter == 0) && ((value & 0x80) == 0)) {
                        m_enabled = false;
                    }
                }

                if ((value & 0x80) != 0) {
                    Trigger(nextStepClocksLength: nextStepClocksLength);
                }

                break;
        }
    }

    /// <summary>Advances the frequency timer, clocking the shift register when it expires.</summary>
    /// <param name="cycles">The number of T-cycles elapsed.</param>
    public void Step(int cycles) {
        m_frequencyTimer -= cycles;

        while (m_frequencyTimer <= 0) {
            m_frequencyTimer += Period();
            ClockShiftRegister();
        }
    }

    /// <summary>Clocks the length counter (256&#160;Hz), disabling the channel when it runs out.</summary>
    public void StepLength() {
        if (((m_nr4 & 0x40) != 0) && (m_lengthCounter > 0)) {
            m_lengthCounter -= 1;

            if (m_lengthCounter == 0) {
                m_enabled = false;
            }
        }
    }
    /// <summary>Clocks the volume envelope (64&#160;Hz).</summary>
    public void StepEnvelope() {
        var period = (m_nr2 & 0x07);

        if (period == 0) {
            return;
        }

        m_envelopeTimer -= 1;

        if (m_envelopeTimer <= 0) {
            m_envelopeTimer = period;

            if (((m_nr2 & 0x08) != 0) && (m_envelopeVolume < 15)) {
                m_envelopeVolume += 1;
            }
            else if (((m_nr2 & 0x08) == 0) && (m_envelopeVolume > 0)) {
                m_envelopeVolume -= 1;
            }
        }
    }

    /// <summary>Restarts the channel: re-enables it (if its DAC is on), reloads the timers, and refills the shift register.</summary>
    /// <param name="nextStepClocksLength">Whether the frame sequencer's next step will clock length, for the extra-clock-on-trigger behavior.</param>
    public void Trigger(bool nextStepClocksLength) {
        m_enabled = true;

        if (m_lengthCounter == 0) {
            m_lengthCounter = 64;

            if (!nextStepClocksLength && ((m_nr4 & 0x40) != 0)) {
                m_lengthCounter -= 1;
            }
        }

        m_frequencyTimer = Period();
        m_envelopeVolume = (m_nr2 >> 4);
        m_envelopeTimer = (m_nr2 & 0x07);
        m_lfsr = 0x7FFF;

        if (!DacEnabled) {
            m_enabled = false;
        }
    }
    /// <summary>Loads the length counter from a register value while the APU is powered down (DMG keeps the
    /// length-load registers writable with the APU off).</summary>
    /// <param name="value">The value written to <c>NR41</c>; its low six bits set the length.</param>
    public void WriteLengthLoad(byte value) =>
        m_lengthCounter = (64 - (value & 0x3F));

    /// <summary>Clears every register and all channel state, as a power-down does — except the length counter, which
    /// the DMG preserves through a power cycle.</summary>
    public void PowerOff() {
        m_nr1 = 0;
        m_nr2 = 0;
        m_nr3 = 0;
        m_nr4 = 0;
        m_enabled = false;
        m_frequencyTimer = 0;
        m_envelopeVolume = 0;
        m_envelopeTimer = 0;
        m_lfsr = 0;
    }

    private int Period() {
        var shift = (m_nr3 >> 4);

        return (s_divisors[m_nr3 & 0x07] << shift);
    }
    private void ClockShiftRegister() {
        var feedback = ((m_lfsr & 0x01) ^ ((m_lfsr >> 1) & 0x01));

        m_lfsr >>= 1;
        m_lfsr |= (feedback << 14);

        // Width mode also feeds bit 6, shortening the sequence to a 7-stage register.
        if ((m_nr3 & 0x08) != 0) {
            m_lfsr = ((m_lfsr & ~0x40) | (feedback << 6));
        }
    }
}

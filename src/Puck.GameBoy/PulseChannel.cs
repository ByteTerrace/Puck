namespace Puck.GameBoy;

/// <summary>
/// A square-wave channel: one of the duty cycle's eight steps is emitted each frequency-timer period, gated by a
/// volume envelope and an optional length counter, and (channel&#160;1 only) retuned by a frequency sweep. Channels
/// 1 and 2 are identical apart from the sweep, which channel&#160;2 omits.
/// </summary>
internal sealed class PulseChannel {
    // The eight duty patterns (12.5%, 25%, 50%, 75%), one bit per step of the period.
    private static readonly byte[][] s_dutyPatterns = [
        [0, 0, 0, 0, 0, 0, 0, 1],
        [1, 0, 0, 0, 0, 0, 0, 1],
        [1, 0, 0, 0, 0, 1, 1, 1],
        [0, 1, 1, 1, 1, 1, 1, 0],
    ];

    private readonly bool m_hasSweep;

    private byte m_nr0;
    private byte m_nr1;
    private byte m_nr2;
    private byte m_nr3;
    private byte m_nr4;
    private bool m_enabled;
    private bool m_sweepEnabled;
    private bool m_sweepNegateUsed;
    private int m_lengthCounter;
    private int m_dutyPosition;
    private int m_frequencyTimer;
    private int m_envelopeVolume;
    private int m_envelopeTimer;
    private int m_sweepShadow;
    private int m_sweepTimer;

    /// <summary>Initializes a pulse channel, with or without the frequency sweep unit (channel&#160;1 has it).</summary>
    /// <param name="hasSweep">Whether this channel has the <c>NR10</c> sweep unit.</param>
    public PulseChannel(bool hasSweep) {
        m_hasSweep = hasSweep;
    }

    /// <summary>Gets whether the channel is currently producing sound (the <c>NR52</c> status bit).</summary>
    public bool Enabled =>
        m_enabled;
    /// <summary>Gets the channel's current digital output level (0-15), or 0 when off or its DAC is disabled.</summary>
    public int Output =>
        ((m_enabled && DacEnabled && (s_dutyPatterns[m_nr1 >> 6][m_dutyPosition] != 0))
            ? m_envelopeVolume
            : 0);
    // The DAC is powered by any nonzero volume-or-direction bits of NR12; with it off the channel cannot sound.
    private bool DacEnabled =>
        ((m_nr2 & 0xF8) != 0);
    private int Frequency =>
        (((m_nr4 & 0x07) << 8) | m_nr3);

    /// <summary>Reads one of the channel's five registers (raw; the APU applies the read mask).</summary>
    /// <param name="register">The register index 0-4 (<c>NRx0</c>-<c>NRx4</c>).</param>
    public byte ReadRegister(int register) =>
        register switch {
            0 => m_nr0,
            1 => m_nr1,
            2 => m_nr2,
            3 => m_nr3,
            _ => m_nr4,
        };
    /// <summary>Writes one of the channel's five registers, updating the derived state and triggering on <c>NRx4</c> bit 7.</summary>
    /// <param name="register">The register index 0-4 (<c>NRx0</c>-<c>NRx4</c>).</param>
    /// <param name="value">The value written.</param>
    /// <param name="nextStepClocksLength">Whether the frame sequencer's next step will clock the length counter, for the obscure extra-length-clock behavior on enabling length or triggering.</param>
    public void WriteRegister(int register, byte value, bool nextStepClocksLength) {
        switch (register) {
            case 0:
                m_nr0 = value;

                // Leaving negate mode after a calculation has used it disables the channel (sweep details quirk).
                if (((value & 0x08) == 0) && m_sweepNegateUsed) {
                    m_enabled = false;
                }

                break;
            case 1:
                m_nr1 = value;
                m_lengthCounter = (64 - (value & 0x3F));

                break;
            case 2:
                m_nr2 = value;

                // Clearing the DAC bits immediately disables the channel.
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

                // Obscure behavior: enabling the length counter during a frame-sequencer period that will NOT clock
                // length clocks it once immediately; if that zeroes it without a simultaneous trigger, the channel off.
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

    /// <summary>Advances the frequency timer, stepping the duty position when it expires.</summary>
    /// <param name="cycles">The number of T-cycles elapsed.</param>
    public void Step(int cycles) {
        m_frequencyTimer -= cycles;

        while (m_frequencyTimer <= 0) {
            m_frequencyTimer += ((2048 - Frequency) * 4);
            m_dutyPosition = ((m_dutyPosition + 1) & 0x07);
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
    /// <summary>Clocks the frequency sweep (128&#160;Hz); a no-op on channel&#160;2.</summary>
    public void StepSweep() {
        if (!m_hasSweep) {
            return;
        }

        if (m_sweepTimer > 0) {
            m_sweepTimer -= 1;
        }

        if (m_sweepTimer != 0) {
            return;
        }

        var period = ((m_nr0 >> 4) & 0x07);

        m_sweepTimer = ((period == 0) ? 8 : period);

        if (m_sweepEnabled && (period != 0)) {
            var newFrequency = CalculateSweep();

            // A valid in-range result with a nonzero shift is written back, then the overflow check repeats.
            if ((newFrequency <= 2047) && ((m_nr0 & 0x07) != 0)) {
                m_sweepShadow = newFrequency;
                m_nr3 = (byte)(newFrequency & 0xFF);
                m_nr4 = (byte)((m_nr4 & ~0x07) | ((newFrequency >> 8) & 0x07));

                _ = CalculateSweep();
            }
        }
    }

    /// <summary>Restarts the channel: re-enables it (if its DAC is on), reloads the timers, and re-arms the sweep.</summary>
    /// <param name="nextStepClocksLength">Whether the frame sequencer's next step will clock length, for the extra-clock-on-trigger behavior.</param>
    public void Trigger(bool nextStepClocksLength) {
        m_enabled = true;

        if (m_lengthCounter == 0) {
            m_lengthCounter = 64;

            // A trigger that reloads length to maximum during a non-length-clocking step, with length enabled,
            // immediately clocks it once.
            if (!nextStepClocksLength && ((m_nr4 & 0x40) != 0)) {
                m_lengthCounter -= 1;
            }
        }

        m_frequencyTimer = ((2048 - Frequency) * 4);
        m_envelopeVolume = (m_nr2 >> 4);
        m_envelopeTimer = (m_nr2 & 0x07);

        if (m_hasSweep) {
            m_sweepShadow = Frequency;
            m_sweepNegateUsed = false;

            var period = ((m_nr0 >> 4) & 0x07);

            m_sweepTimer = ((period == 0) ? 8 : period);
            m_sweepEnabled = ((period != 0) || ((m_nr0 & 0x07) != 0));

            // A nonzero shift makes the trigger perform an immediate overflow check.
            if ((m_nr0 & 0x07) != 0) {
                _ = CalculateSweep();
            }
        }

        if (!DacEnabled) {
            m_enabled = false;
        }
    }
    /// <summary>Loads the length counter from a register value while the APU is powered down. On the DMG the
    /// length-load registers remain writable with the APU off, even though the rest do not.</summary>
    /// <param name="value">The value written to <c>NRx1</c>; its low six bits set the length.</param>
    public void WriteLengthLoad(byte value) =>
        m_lengthCounter = (64 - (value & 0x3F));

    /// <summary>Clears every register and all channel state, as a power-down does — except the length counter, which
    /// the DMG preserves through a power cycle.</summary>
    public void PowerOff() {
        m_nr0 = 0;
        m_nr1 = 0;
        m_nr2 = 0;
        m_nr3 = 0;
        m_nr4 = 0;
        m_enabled = false;
        m_sweepEnabled = false;
        m_sweepNegateUsed = false;
        m_dutyPosition = 0;
        m_frequencyTimer = 0;
        m_envelopeVolume = 0;
        m_envelopeTimer = 0;
        m_sweepShadow = 0;
        m_sweepTimer = 0;
    }

    private int CalculateSweep() {
        var negate = ((m_nr0 & 0x08) != 0);
        var delta = (m_sweepShadow >> (m_nr0 & 0x07));
        var newFrequency = (negate ? (m_sweepShadow - delta) : (m_sweepShadow + delta));

        // Record that a calculation ran in negate mode (leaving negate mode afterward disables the channel).
        if (negate) {
            m_sweepNegateUsed = true;
        }

        // An overflow past the 11-bit range disables the channel.
        if (newFrequency > 2047) {
            m_enabled = false;
        }

        return newFrequency;
    }
}

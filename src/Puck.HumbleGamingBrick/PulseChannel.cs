namespace Puck.HumbleGamingBrick;

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
    private readonly System.Func<bool>? m_isCgb;
    private readonly System.Func<bool>? m_isDoubleSpeed;

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
    private int m_volumeCountdown;
    private bool m_envelopeClock;
    private bool m_envelopeShouldLock;
    private bool m_envelopeLocked;
    private int m_sweepShadow;
    private int m_sweepTimer;

    // CGB-only sub-cycle timing model (the 2 MHz square generator). m_sampleCountdown is a 2 MHz down-counter; the
    // duty index advances when it reloads. A per-trigger start delay derived from the lf_div (1 MHz) phase is what
    // makes pulse trigger timing on the CGB observable. m_phase tracks the 2 MHz parity that lf_div is read from.
    private int m_sampleCountdown;
    private int m_delay;
    private bool m_justReloaded;
    private bool m_sampleHigh;
    private bool m_sampleSuppressed;
    private uint m_phase;

    /// <summary>Initializes a pulse channel, with or without the frequency sweep unit (channel&#160;1 has it).</summary>
    /// <param name="hasSweep">Whether this channel has the <c>NR10</c> sweep unit.</param>
    /// <param name="isCgb">Reads whether the console is a CGB (selects the sub-cycle 2 MHz timing model); <see langword="null"/> (the default) is treated as DMG.</param>
    /// <param name="isDoubleSpeed">Reads whether the CGB is in double-speed mode; <see langword="null"/> (the default) is treated as single-speed.</param>
    public PulseChannel(bool hasSweep, System.Func<bool>? isCgb = null, System.Func<bool>? isDoubleSpeed = null) {
        m_hasSweep = hasSweep;
        m_isCgb = isCgb;
        m_isDoubleSpeed = isDoubleSpeed;
    }

    /// <summary>Gets whether the channel is currently producing sound (the <c>NR52</c> status bit).</summary>
    public bool Enabled =>
        m_enabled;
    /// <summary>Gets the channel's current digital output level (0-15), or 0 when off or its DAC is disabled.</summary>
    public int Output {
        get {
            if (!m_enabled || !DacEnabled) {
                return 0;
            }

            // CGB: the duty sample is latched at each step and held suppressed (silent) from a trigger until the first
            // step. DMG reads the live duty position.
            if (IsCgb) {
                return ((!m_sampleSuppressed && m_sampleHigh) ? m_envelopeVolume : 0);
            }

            return ((s_dutyPatterns[m_nr1 >> 6][m_dutyPosition] != 0) ? m_envelopeVolume : 0);
        }
    }
    // The DAC is powered by any nonzero volume-or-direction bits of NR12; with it off the channel cannot sound.
    private bool DacEnabled =>
        ((m_nr2 & 0xF8) != 0);
    private int Frequency =>
        (((m_nr4 & 0x07) << 8) | m_nr3);
    private bool IsCgb =>
        (m_isCgb?.Invoke() ?? false);

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
                // Writing NR12/NR22 while the channel is active "zombie"-adjusts the live volume (the NRx2 zombie glitch).
                if (IsCgb && m_enabled) {
                    Nrx2Glitch(value: value);
                }

                m_nr2 = value;

                // Clearing the DAC bits immediately disables the channel.
                if (!DacEnabled) {
                    m_enabled = false;
                }

                break;
            case 3:
                m_nr3 = value;

                // On CGB a frequency-low write that lands as the counter reloads re-derives the countdown.
                if (IsCgb && m_justReloaded) {
                    m_sampleCountdown = (((Frequency ^ 0x7FF) * 2) + 1);
                }

                break;
            default:
                var wasLengthEnabled = ((m_nr4 & 0x40) != 0);

                m_nr4 = value;

                // A frequency-high (NR14) write that lands as the counter reloads re-derives the countdown.
                if (IsCgb && m_justReloaded) {
                    m_sampleCountdown = (((Frequency ^ 0x7FF) * 2) + 1);
                }

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
        if (IsCgb) {
            StepCgb(cycles: cycles);

            return;
        }

        // DMG flat-period model: the duty timer only runs while the channel is active.
        if (!m_enabled) {
            return;
        }

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
    /// <summary>The volume-countdown decrement on the primary frame-sequencer edge (the (step&amp;7)==7 event): a held
    /// envelope clock pauses it.</summary>
    public void DecrementEnvelopeCountdown() {
        if (!m_envelopeClock) {
            m_volumeCountdown = ((m_volumeCountdown - 1) & 0x07);
        }
    }
    /// <summary>The volume tick on the primary edge: fires only if the clock was armed by the previous secondary edge,
    /// then disarms (applying the lock when the boundary was reached).</summary>
    public void TickEnvelope() {
        if (!m_envelopeClock) {
            return;
        }

        SetEnvelopeClock(value: false, direction: false, volume: 0);

        if (m_envelopeLocked || ((m_nr2 & 0x07) == 0)) {
            return;
        }

        if ((m_nr2 & 0x08) != 0) {
            m_envelopeVolume = ((m_envelopeVolume + 1) & 0x0F);
        }
        else {
            m_envelopeVolume = ((m_envelopeVolume - 1) & 0x0F);
        }
    }
    /// <summary>The envelope arm on the secondary frame-sequencer edge: when the countdown has expired, reload it and
    /// arm the clock so the next primary edge ticks the volume.</summary>
    public void ArmEnvelopeClock() {
        if (m_enabled && (m_volumeCountdown == 0)) {
            m_volumeCountdown = (m_nr2 & 0x07);

            SetEnvelopeClock(value: true, direction: ((m_nr2 & 0x08) != 0), volume: m_envelopeVolume);
        }
    }
    // The NRx2 zombie glitch (CGB-E variant): a write to NR12/NR22 on a running channel glitches the live volume via
    // the envelope clock's odd connections. old = the register value before this write.
    private void Nrx2Glitch(byte value) {
        var old = m_nr2;

        if (m_envelopeClock) {
            m_volumeCountdown = (value & 0x07);
        }

        var shouldTick = (((value & 0x07) != 0) && ((old & 0x07) == 0) && !m_envelopeLocked);
        var shouldInvert = (((value & 0x08) ^ (old & 0x08)) != 0);

        if (((value & 0x0F) == 0x08) && ((old & 0x0F) == 0x08) && !m_envelopeLocked) {
            shouldTick = true;
        }

        if (shouldInvert) {
            if ((value & 0x08) != 0) {
                if (((old & 0x07) == 0) && !m_envelopeLocked) {
                    m_envelopeVolume ^= 0x0F;
                }
                else {
                    m_envelopeVolume = ((0x0E - m_envelopeVolume) & 0x0F);
                }

                shouldTick = false;
            }
            else {
                m_envelopeVolume = ((0x10 - m_envelopeVolume) & 0x0F);
            }
        }

        if (shouldTick) {
            m_envelopeVolume = ((m_envelopeVolume + (((value & 0x08) != 0) ? 1 : -1)) & 0x0F);
        }
        else if (((value & 0x07) == 0) && m_envelopeClock) {
            SetEnvelopeClock(value: false, direction: false, volume: 0);
        }
    }
    private void SetEnvelopeClock(bool value, bool direction, int volume) {
        if (m_envelopeClock == value) {
            return;
        }

        if (value) {
            m_envelopeClock = true;
            m_envelopeShouldLock = ((volume == 0x0F) && direction) || ((volume == 0x00) && !direction);
        }
        else {
            m_envelopeClock = false;
            m_envelopeLocked |= m_envelopeShouldLock;
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
        var wasActive = m_enabled;

        m_enabled = true;

        if (m_lengthCounter == 0) {
            m_lengthCounter = 64;

            // A trigger that reloads length to maximum during a non-length-clocking step, with length enabled,
            // immediately clocks it once.
            if (!nextStepClocksLength && ((m_nr4 & 0x40) != 0)) {
                m_lengthCounter -= 1;
            }
        }

        m_envelopeVolume = (m_nr2 >> 4);
        m_volumeCountdown = (m_nr2 & 0x07);
        m_envelopeClock = false;
        m_envelopeShouldLock = false;
        m_envelopeLocked = false;

        if (IsCgb) {
            // The CGB-E square trigger delay: a freshly started channel waits (6 - lf_div) 2 MHz ticks before its
            // first sample; an already-active retrigger starts two ticks earlier, (4 - lf_div). lf_div is the 1 MHz
            // phase — the low bit of the 2 MHz counter, with the double-speed access-phase correction applied. In
            // double-speed the 1 MHz phase is the 2 MHz counter's low bit; in single-speed the 2 MHz counter advances
            // two per machine cycle (so its low bit is always 0) and the live phase comes entirely from the odd
            // sub-machine-cycle split of the register write, which lands lf_div at 1.
            var lfDiv = ((m_isDoubleSpeed?.Invoke() ?? false) ? (int)(m_phase & 1) : 1);

            m_delay = ((wasActive ? 4 : 6) - lfDiv);
            m_sampleCountdown = (((Frequency ^ 0x7FF) * 2) + m_delay);

            // A fresh trigger suppresses output until the first duty step — except the CGB-E edge case where
            // the duty index advances immediately (forcing the sample unsuppressed).
            if (!wasActive) {
                var forceUnsuppressed = (((m_nr4 & 0x04) == 0) && ((((m_sampleCountdown - m_delay) / 2) & 0x400) == 0));

                if (forceUnsuppressed) {
                    m_dutyPosition = ((m_dutyPosition + 1) & 0x07);
                    m_sampleHigh = (s_dutyPatterns[m_nr1 >> 6][m_dutyPosition] != 0);
                    m_sampleSuppressed = false;
                }
                else {
                    m_sampleSuppressed = true;
                }
            }
        }
        else {
            m_frequencyTimer = ((2048 - Frequency) * 4);
        }

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

    /// <summary>Clears every register and all channel state, as a power-down does — except the length counter when
    /// <paramref name="clearLength"/> is <see langword="false"/> (the DMG preserves it; the CGB clears it).</summary>
    /// <param name="clearLength">Whether to also clear the length counter, as the CGB does on power-down.</param>
    public void PowerOff(bool clearLength) {
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
        m_volumeCountdown = 0;
        m_envelopeClock = false;
        m_envelopeShouldLock = false;
        m_envelopeLocked = false;
        m_sweepShadow = 0;
        m_sweepTimer = 0;
        m_sampleCountdown = 0;
        m_delay = 0;
        m_justReloaded = false;
        m_sampleHigh = false;
        m_sampleSuppressed = false;
        m_phase = 0;

        if (clearLength) {
            m_lengthCounter = 0;
        }
    }

    // The CGB 2 MHz square step: runs one 2 MHz tick at a time so the lf_div phase keeps its parity. The duty index
    // advances each time the sample countdown reloads, after an initial per-trigger start delay.
    private void StepCgb(int cycles) {
        var twoMhzTicks = (cycles / 2);

        for (var tick = 0; tick < twoMhzTicks; tick += 1) {
            m_phase += 1;

            if (!m_enabled) {
                continue;
            }

            // A duty step consumes (countdown + 1) ticks — the counter is checked for zero first,
            // then reloaded, so the effective period is (2048 - frequency) * 2 ticks.
            if (m_sampleCountdown == 0) {
                m_sampleCountdown = (((Frequency ^ 0x7FF) * 2) + 1);
                m_justReloaded = true;
                m_dutyPosition = ((m_dutyPosition + 1) & 0x07);
                m_sampleSuppressed = false;
                m_sampleHigh = (s_dutyPatterns[m_nr1 >> 6][m_dutyPosition] != 0);
            }
            else {
                m_sampleCountdown -= 1;
                m_justReloaded = false;
            }
        }
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

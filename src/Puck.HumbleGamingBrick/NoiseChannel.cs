namespace Puck.HumbleGamingBrick;

/// <summary>
/// The noise channel: a linear-feedback shift register clocked at a programmable rate produces a pseudo-random
/// bit stream, gated by a volume envelope and an optional length counter. A width-mode bit shortens the register
/// from 15 to 7 stages for a coarser, more periodic tone.
/// </summary>
internal sealed class NoiseChannel {
    // The base divisors selected by NR43 bits 0-2 (index 0 is 8, not 0).
    private static readonly int[] s_divisors = [8, 16, 32, 48, 64, 80, 96, 112];

    private readonly System.Func<bool>? m_isCgb;
    private readonly System.Func<bool>? m_isDoubleSpeed;

    private byte m_nr1;
    private byte m_nr2;
    private byte m_nr3;
    private byte m_nr4;
    private bool m_enabled;
    private int m_lengthCounter;
    private int m_frequencyTimer;
    private int m_envelopeVolume;
    private int m_volumeCountdown;
    private bool m_envelopeClock;
    private bool m_envelopeShouldLock;
    private bool m_envelopeLocked;
    private int m_lfsr;

    // CGB-only sub-cycle timing model (the 2 MHz noise counter). The frequency generator is a free-running
    // 14-bit up-counter clocked at the fixed 2 MHz audio rate; the LFSR steps on the rising edge of counter bit
    // NR43>>4. The counter keeps running in the background after the channel is disabled, so a retrigger observes a
    // particular phase — this phase is what makes retrigger timing on the CGB observable.
    private int m_counter;
    private int m_counterCountdown;
    private uint m_alignment;
    private bool m_counterActive;
    private bool m_backgroundCounterActive;
    private bool m_startedWithDacDisabled;
    private bool m_didStepCounter;
    private bool m_countdownReloaded;

    /// <summary>Initializes the noise channel.</summary>
    /// <param name="isCgb">Reads whether the console is a CGB (selects the sub-cycle 2 MHz counter timing model); <see langword="null"/> (the default) is treated as DMG.</param>
    /// <param name="isDoubleSpeed">Reads whether the CGB is in double-speed mode; <see langword="null"/> (the default) is treated as single-speed.</param>
    public NoiseChannel(System.Func<bool>? isCgb = null, System.Func<bool>? isDoubleSpeed = null) {
        m_isCgb = isCgb;
        m_isDoubleSpeed = isDoubleSpeed;
    }

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
                // Disabling the DAC kills the channel; on CGB it also stops the background counter (with a one-step
                // nudge if a step was imminent).
                if ((value & 0xF8) == 0) {
                    if (IsCgb && m_enabled && ((m_nr3 & 0x07) != 0)) {
                        if (m_counterCountdown <= 2) {
                            m_counter = ((m_counter + 1) & 0x3FFF);
                        }

                        m_backgroundCounterActive = false;
                    }

                    m_nr2 = value;
                    m_enabled = false;
                    m_counterActive = false;
                }
                else {
                    // Writing NR42 while the channel is active zombie-adjusts the live volume (the NRx2 zombie glitch).
                    if (IsCgb && m_enabled) {
                        Nrx2Glitch(value: value);
                    }

                    m_nr2 = value;
                }

                break;
            case 3:
                var oldNr3 = m_nr3;

                // On CGB a NR43 write that lands exactly as the counter reloaded re-derives the countdown from the
                // new divisor and the current alignment.
                if (IsCgb && m_countdownReloaded) {
                    var newDivisor = ((value & 0x07) << 2);

                    if (newDivisor == 0) {
                        newDivisor = 2;
                    }

                    int[] offsets = [2, 1, 0, 3];

                    m_counterCountdown = (newDivisor + ((newDivisor == 2) ? 0 : offsets[m_alignment & 3]));
                }

                m_nr3 = value;

                if (IsCgb && m_enabled) {
                    Nr43Glitch(old: oldNr3);
                }

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
        if (IsCgb) {
            StepCgb(cycles: cycles);

            return;
        }

        // DMG flat-period model: the LFSR only clocks while the channel is active.
        if (!m_enabled) {
            return;
        }

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
    /// <summary>The volume-countdown decrement on the primary frame-sequencer edge; a held clock pauses it.</summary>
    public void DecrementEnvelopeCountdown() {
        if (!m_envelopeClock) {
            m_volumeCountdown = ((m_volumeCountdown - 1) & 0x07);
        }
    }
    /// <summary>The volume tick on the primary edge: fires only if armed by the previous secondary edge, then disarms.</summary>
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
    /// <summary>The envelope arm on the secondary frame-sequencer edge.</summary>
    public void ArmEnvelopeClock() {
        if (m_enabled && (m_volumeCountdown == 0)) {
            m_volumeCountdown = (m_nr2 & 0x07);

            SetEnvelopeClock(value: true, direction: ((m_nr2 & 0x08) != 0), volume: m_envelopeVolume);
        }
    }
    // The NRx2 zombie glitch (CGB-E variant): a write to NR42 on a running channel glitches the live volume.
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

    /// <summary>Restarts the channel: re-enables it (if its DAC is on), reloads the timers, and refills the shift register.</summary>
    /// <param name="nextStepClocksLength">Whether the frame sequencer's next step will clock length, for the extra-clock-on-trigger behavior.</param>
    public void Trigger(bool nextStepClocksLength) {
        if (m_lengthCounter == 0) {
            m_lengthCounter = 64;

            if (!nextStepClocksLength && ((m_nr4 & 0x40) != 0)) {
                m_lengthCounter -= 1;
            }
        }

        m_envelopeVolume = (m_nr2 >> 4);
        m_volumeCountdown = (m_nr2 & 0x07);
        m_envelopeClock = false;
        m_envelopeShouldLock = false;
        m_envelopeLocked = false;
        m_lfsr = 0x7FFF;

        if (IsCgb) {
            PrepareNoiseStart();

            m_didStepCounter = ((m_alignment & 3) == 2);
            m_enabled = DacEnabled;

            return;
        }

        m_frequencyTimer = Period();
        m_enabled = DacEnabled;
    }
    /// <summary>Loads the length counter from a register value while the APU is powered down (DMG keeps the
    /// length-load registers writable with the APU off).</summary>
    /// <param name="value">The value written to <c>NR41</c>; its low six bits set the length.</param>
    public void WriteLengthLoad(byte value) =>
        m_lengthCounter = (64 - (value & 0x3F));

    /// <summary>Clears every register and all channel state, as a power-down does — except the length counter when
    /// <paramref name="clearLength"/> is <see langword="false"/> (the DMG preserves it; the CGB clears it).</summary>
    /// <param name="clearLength">Whether to also clear the length counter, as the CGB does on power-down.</param>
    public void PowerOff(bool clearLength) {
        m_nr1 = 0;
        m_nr2 = 0;
        m_nr3 = 0;
        m_nr4 = 0;
        m_enabled = false;
        m_frequencyTimer = 0;
        m_envelopeVolume = 0;
        m_volumeCountdown = 0;
        m_envelopeClock = false;
        m_envelopeShouldLock = false;
        m_envelopeLocked = false;
        m_lfsr = 0;
        m_counter = 0;
        m_counterCountdown = 0;
        m_alignment = 0;
        m_counterActive = false;
        m_backgroundCounterActive = false;
        m_startedWithDacDisabled = false;
        m_didStepCounter = false;
        m_countdownReloaded = false;

        if (clearLength) {
            m_lengthCounter = 0;
        }
    }

    private bool IsCgb =>
        (m_isCgb?.Invoke() ?? false);

    private int Period() {
        var shift = (m_nr3 >> 4);

        return (s_divisors[m_nr3 & 0x07] << shift);
    }

    // The CGB 2 MHz counter step: runs one 2 MHz tick at a time so the alignment phase keeps its parity. Each T-cycle
    // M-cycle handed in carries two 2 MHz ticks (one in double-speed), matching the fixed audio master clock.
    private void StepCgb(int cycles) {
        var twoMhzTicks = (cycles / 2);

        for (var tick = 0; tick < twoMhzTicks; tick += 1) {
            m_alignment += 1;

            if (!m_counterActive && !m_backgroundCounterActive) {
                continue;
            }

            var divisor = ((m_nr3 & 0x07) << 2);

            if (divisor == 0) {
                divisor = 2;
            }

            if (m_counterCountdown == 0) {
                m_counterCountdown = divisor;
            }

            m_counterCountdown -= 1;

            if (m_counterCountdown > 0) {
                m_countdownReloaded = false;

                continue;
            }

            m_counterCountdown = divisor;
            m_countdownReloaded = true;

            var mask = (1 << (m_nr3 >> 4));
            var oldBit = ((m_counter & mask) != 0);

            m_counter = ((m_counter + 1) & 0x3FFF);
            m_didStepCounter = true;

            var newBit = ((m_counter & mask) != 0);

            if (newBit && !oldBit && m_enabled) {
                ClockShiftRegister();
            }
        }
    }

    // Computes the post-trigger counter start delay from the divisor and the current 2 MHz alignment phase,
    // specialized to the CGB-E (model > CGB-C) revision.
    private void PrepareNoiseStart() {
        m_counterActive = ((m_nr2 & 0xF8) != 0);

        var wasStartedWithDacDisabled = m_startedWithDacDisabled;

        m_startedWithDacDisabled = !m_counterActive;

        var divisor = (m_nr3 & 0x07);
        var wasBackgroundCounting = m_backgroundCounterActive;

        m_backgroundCounterActive = true;

        var instantStep = false;
        var isActive = m_enabled;

        // Correct the raw counter alignment to the phase the hardware samples at the access point: this counter runs a
        // constant offset ahead of the hardware's (one 2 MHz unit in double-speed, two in single-speed), removed here.
        var align = (m_alignment - (((m_isDoubleSpeed?.Invoke() ?? false)) ? 1u : 2u));

        if ((divisor > 1) && (m_counterCountdown == 1)) {
            m_counter = ((m_counter + 1) & 0x3FFF);
        }
        else if ((m_counterCountdown == 2) && ((align & 3) == 0) && isActive) {
            if (divisor == 0) {
                divisor = 8;
            }
            else if (divisor == 1) {
                var mask = (1 << (m_nr3 >> 4));
                var oldBit = ((m_counter & mask) != 0);

                m_counter = ((m_counter + 1) & 0x3FFF);

                var newBit = ((m_counter & mask) != 0);

                if (newBit && !oldBit) {
                    instantStep = true;
                }
            }
        }

        m_counterCountdown = ((divisor == 0) ? 6 : ((divisor * 4) + 6));
        m_counterCountdown += AlignmentDelay(align: align, divisor: divisor, isActive: isActive, wasBackgroundCounting: wasBackgroundCounting);
        m_counterCountdown += BackgroundStartGlitch(align: align, divisor: divisor, isActive: isActive, wasBackgroundCounting: wasBackgroundCounting, wasStartedWithDacDisabled: wasStartedWithDacDisabled);

        // The reset LFSR value is normally 0 (inverted 0x7FFF here); one obscure alignment edge case seeds 0x0055.
        m_lfsr = ((divisor == 0) && isActive && ((align & 3) == 3)) ? 0x7FAA : 0x7FFF;

        if (instantStep) {
            ClockShiftRegister();
        }
    }

    // The alignment-phase adjustment to the post-trigger counter start delay (CGB-E).
    private int AlignmentDelay(uint align, int divisor, bool isActive, bool wasBackgroundCounting) {
        if ((align & 1) != 0) {
            if (divisor == 0) {
                return wasBackgroundCounting ? -1 : +1;
            }

            if ((align & 2) != 0) {
                return ((divisor == 1) && !isActive) ? +1 : -3;
            }

            return ((divisor == 1) && isActive) ? -5 : -1;
        }

        if (divisor == 0) {
            return 0;
        }

        if ((align & 2) != 0) {
            return -2;
        }

        if (divisor > 1) {
            return -4;
        }

        return ((divisor == 1) && isActive && ((m_nr3 & 0xF0) == 0)) ? -4 : 0;
    }

    // Background-counting start glitches that further nudge the counter start delay.
    private int BackgroundStartGlitch(uint align, int divisor, bool isActive, bool wasBackgroundCounting, bool wasStartedWithDacDisabled) {
        if (divisor > 1) {
            return (!m_counterActive && ((align & 3) == 0)) ? +4 : 0;
        }

        if (!wasBackgroundCounting || isActive || ((align & 3) != 0)) {
            return 0;
        }

        if (divisor == 0) {
            return wasStartedWithDacDisabled ? +28 : 0;
        }

        return -4;
    }

    // The NR43-write LFSR glitch (CGB-E): a NR43 shift-bit change can clock the LFSR an extra time.
    // The conditions read the free-running counter (so the inverted-LFSR convention doesn't matter); the step uses the
    // already-updated width via ClockShiftRegister.
    private void Nr43Glitch(byte old) {
        var value = m_nr3;

        if ((old & 0xF0) == (value & 0xF0)) {
            return;
        }

        var oldBit = (((m_counter >> (old >> 4)) & 1) != 0);
        var glitchValue = ((old & 0x7F) | (value & 0x80));
        var glitchBit = (((m_counter >> (glitchValue >> 4)) & 1) != 0);
        var newBit = (((m_counter >> (value >> 4)) & 1) != 0);

        if ((oldBit == newBit) && (newBit != glitchBit) && newBit && ((value & 0x80) == 0)) {
            ClockShiftRegister();
        }
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

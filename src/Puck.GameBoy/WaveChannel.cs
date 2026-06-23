namespace Puck.GameBoy;

/// <summary>
/// The wave channel: it plays back the 32 four-bit samples held in wave-pattern RAM (<c>0xFF30</c>-<c>0xFF3F</c>),
/// advancing one sample each frequency-timer period and scaling the result by a coarse volume shift. Unlike the
/// pulse and noise channels it has no envelope; its DAC is the explicit <c>NR30</c> bit&#160;7.
/// </summary>
internal sealed class WaveChannel {
    private const int WaveRamSize = 16;
    private const int SampleCount = 32;
    // The frequency-timer value at which a retrigger corrupts wave RAM (the DMG bug), i.e. one sample step away from
    // a fetch — the point where the channel is about to fetch the next sample byte.
    private const int WaveCorruptionTimer = 2;

    private readonly byte[] m_waveRam = new byte[WaveRamSize];
    private readonly System.Func<bool>? m_isCgb;

    private byte m_nr0;
    private byte m_nr1;
    private byte m_nr2;
    private byte m_nr3;
    private byte m_nr4;
    private bool m_enabled;
    private bool m_waveFormJustRead;
    private int m_lengthCounter;
    private int m_samplePosition;
    private int m_frequencyTimer;
    // The byte the channel last *fetched* from wave RAM. The output is driven from this latch, not a live read of
    // the current index — so a (re)trigger's delay window keeps emitting the previously fetched sample until the
    // first post-trigger fetch lands.
    private byte m_sampleBuffer;

    /// <summary>Initializes the wave channel.</summary>
    /// <param name="isCgb">Reads whether the console is a CGB, which lets the CPU read/write wave RAM freely while the channel plays (the DMG only allows it during the brief post-fetch window); <see langword="null"/> (the default) is treated as DMG.</param>
    public WaveChannel(System.Func<bool>? isCgb = null) {
        m_isCgb = isCgb;
    }

    /// <summary>Gets whether the channel is currently producing sound (the <c>NR52</c> status bit).</summary>
    public bool Enabled =>
        m_enabled;
    /// <summary>Gets the channel's current digital output level (0-15), or 0 when off, muted, or its DAC is disabled.</summary>
    public int Output {
        get {
            if (!m_enabled || !DacEnabled) {
                return 0;
            }

            var shift = ((m_nr2 >> 5) & 0x03);

            // Volume code 0 mutes; 1/2/3 attenuate by 0/1/2 bits.
            return ((shift == 0)
                ? 0
                : (CurrentSample() >> (shift - 1)));
        }
    }
    // Channel 3's DAC is gated by NR30 bit 7 alone.
    private bool DacEnabled =>
        ((m_nr0 & 0x80) != 0);
    private int Frequency =>
        (((m_nr4 & 0x07) << 8) | m_nr3);

    /// <summary>Reads one of the channel's five registers (raw; the APU applies the read mask).</summary>
    /// <param name="register">The register index 0-4 (<c>NR30</c>-<c>NR34</c>).</param>
    public byte ReadRegister(int register) =>
        register switch {
            0 => m_nr0,
            1 => m_nr1,
            2 => m_nr2,
            3 => m_nr3,
            _ => m_nr4,
        };
    /// <summary>Writes one of the channel's five registers, updating the derived state and triggering on <c>NR34</c> bit 7.</summary>
    /// <param name="register">The register index 0-4 (<c>NR30</c>-<c>NR34</c>).</param>
    /// <param name="value">The value written.</param>
    /// <param name="nextStepClocksLength">Whether the frame sequencer's next step will clock the length counter, for the obscure extra-length-clock behavior.</param>
    public void WriteRegister(int register, byte value, bool nextStepClocksLength) {
        switch (register) {
            case 0:
                m_nr0 = value;

                if (!DacEnabled) {
                    m_enabled = false;
                }

                break;
            case 1:
                m_nr1 = value;
                m_lengthCounter = (256 - value);

                break;
            case 2:
                m_nr2 = value;

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

    /// <summary>Reads a wave-RAM byte (<c>0xFF30</c>-<c>0xFF3F</c>). While the channel is playing, the DMG only lets
    /// the CPU see wave RAM during the single cycle right after the channel fetched a sample, and then it reads the
    /// byte the channel just read; otherwise the read returns open bus.</summary>
    /// <param name="offset">The byte offset 0-15 into wave RAM.</param>
    public byte ReadWaveRam(int offset) {
        if (!m_enabled) {
            return m_waveRam[offset];
        }

        // On the CGB the CPU sees wave RAM freely while the channel plays, reading the byte at the current sample
        // position (synced with PCM34); the DMG only opens this window for the cycle just after a fetch.
        if (m_isCgb?.Invoke() ?? false) {
            return m_waveRam[m_samplePosition / 2];
        }

        return (m_waveFormJustRead
            ? m_waveRam[m_samplePosition / 2]
            : (byte)0xFF);
    }
    /// <summary>Writes a wave-RAM byte (<c>0xFF30</c>-<c>0xFF3F</c>). While the channel is playing, the DMG only lets
    /// the write land during the cycle right after a sample fetch, into the byte the channel just read; otherwise
    /// the write is dropped.</summary>
    /// <param name="offset">The byte offset 0-15 into wave RAM.</param>
    /// <param name="value">The value written.</param>
    public void WriteWaveRam(int offset, byte value) {
        if (!m_enabled) {
            m_waveRam[offset] = value;
        }
        else if ((m_isCgb?.Invoke() ?? false) || m_waveFormJustRead) {
            m_waveRam[m_samplePosition / 2] = value;
        }
    }

    /// <summary>Advances the frequency timer one T-cycle at a time, stepping to the next sample when it expires.
    /// Tracks the single cycle just after a fetch (<c>wave_form_just_read</c>) that opens the DMG wave-RAM access
    /// window — so it is stepped per cycle rather than in a whole machine cycle.</summary>
    /// <param name="cycles">The number of T-cycles elapsed.</param>
    public void Step(int cycles) {
        for (var cycle = 0; cycle < cycles; cycle += 1) {
            m_waveFormJustRead = false;

            // The sample fetcher only runs while the channel is active; a disabled channel does not advance its
            // position or latch a sample (so a trigger sees a clean phase, not whatever a free-running timer left).
            if (!m_enabled) {
                continue;
            }

            m_frequencyTimer -= 1;

            if (m_frequencyTimer <= 0) {
                m_frequencyTimer += ((2048 - Frequency) * 2);
                m_samplePosition = ((m_samplePosition + 1) % SampleCount);
                // Latch the freshly fetched byte; this is what the output presents until the next fetch.
                m_sampleBuffer = m_waveRam[m_samplePosition / 2];
                m_waveFormJustRead = true;
            }
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

    /// <summary>Restarts the channel: re-enables it (if its DAC is on), reloads the length, and resets playback.</summary>
    /// <param name="nextStepClocksLength">Whether the frame sequencer's next step will clock length, for the extra-clock-on-trigger behavior.</param>
    public void Trigger(bool nextStepClocksLength) {
        // DMG wave-RAM corruption: retriggering the channel while it is active and exactly one sample step away from
        // fetching (a frequency timer of 2 T-cycles under this per-T-cycle model) scrambles the low bytes of wave RAM
        // with the byte the fetcher was about to read. The CGB does not have this bug — its wave RAM survives a
        // retrigger intact.
        if (m_enabled && (m_frequencyTimer == WaveCorruptionTimer) && !(m_isCgb?.Invoke() ?? false)) {
            var nextByte = (((m_samplePosition + 1) >> 1) & 0x0F);

            if (nextByte < 4) {
                m_waveRam[0] = m_waveRam[nextByte];
            }
            else {
                var source = (nextByte & ~0x03);

                for (var i = 0; i < 4; i += 1) {
                    m_waveRam[i] = m_waveRam[source + i];
                }
            }
        }

        m_enabled = true;

        if (m_lengthCounter == 0) {
            m_lengthCounter = 256;

            if (!nextStepClocksLength && ((m_nr4 & 0x40) != 0)) {
                m_lengthCounter -= 1;
            }
        }

        // The first sample fetch after a trigger is delayed an extra three frequency-timer periods (six T-cycles)
        // beyond a normal sample step — the DMG wave-trigger delay. This phase governs the narrow post-fetch window
        // in which the DMG lets the CPU read or write wave RAM while the channel plays.
        m_frequencyTimer = (((2048 - Frequency) * 2) + 6);
        m_samplePosition = 0;

        if (!DacEnabled) {
            m_enabled = false;
        }
    }
    /// <summary>Loads the length counter from a register value while the APU is powered down (DMG keeps the
    /// length-load registers writable with the APU off).</summary>
    /// <param name="value">The value written to <c>NR31</c>; the full byte sets the length.</param>
    public void WriteLengthLoad(byte value) =>
        m_lengthCounter = (256 - value);

    /// <summary>Clears every register and all channel state, as a power-down does — except wave RAM, and the length
    /// counter when <paramref name="clearLength"/> is <see langword="false"/> (the DMG preserves it; the CGB clears it).</summary>
    /// <param name="clearLength">Whether to also clear the length counter, as the CGB does on power-down.</param>
    public void PowerOff(bool clearLength) {
        m_nr0 = 0;
        m_nr1 = 0;
        m_nr2 = 0;
        m_nr3 = 0;
        m_nr4 = 0;
        m_enabled = false;
        m_waveFormJustRead = false;
        m_samplePosition = 0;
        m_frequencyTimer = 0;
        m_sampleBuffer = 0;

        if (clearLength) {
            m_lengthCounter = 0;
        }
    }

    private int CurrentSample() {
        // The output reflects the last fetched byte (the latch), not a live read; only the nibble selection follows
        // the current index parity.
        var sampleByte = m_sampleBuffer;

        // Even positions take the high nibble, odd the low nibble.
        return (((m_samplePosition & 1) == 0)
            ? (sampleByte >> 4)
            : (sampleByte & 0x0F));
    }
}

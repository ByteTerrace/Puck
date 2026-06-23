namespace Puck.GameBoy;

/// <summary>
/// The audio processing unit: four sound channels (two pulse, one wave, one noise), the master control registers
/// (<c>NR50</c>-<c>NR52</c>), and channel-3 wave-pattern RAM, occupying <c>0xFF10</c>-<c>0xFF3F</c>. It owns the
/// frame sequencer — a 512&#160;Hz timeline divided from the system counter that clocks the length counters
/// (256&#160;Hz), the volume envelopes (64&#160;Hz), and the channel-1 frequency sweep (128&#160;Hz). Each register
/// exposes only some bits (the rest read as one), and the <c>NR52</c> power switch silences and zeroes everything
/// while it is off.
/// </summary>
public sealed class Apu : IApu {
    // The audio register block 0xFF10-0xFF26 (NR10..NR52), indexed from 0xFF10.
    private const int RegisterCount = 23;
    private const int MasterControlIndex = (MemoryMap.AudioMasterControl - MemoryMap.AudioBase);
    private const int Volume = 0xFF24;     // NR50
    private const int Panning = 0xFF25;    // NR51
    // The system-counter bit whose falling edge advances the frame sequencer (512 Hz). In CGB double-speed the
    // counter runs twice as fast, so the next bit up is used to keep the sequencer at 512 Hz.
    private const int FrameSequencerBit = 0x1000;
    private const int FrameSequencerBitDoubleSpeed = 0x2000;

    // Per-register read OR-masks: the bits that always read as one (write-only or unused bits), indexed from 0xFF10.
    private static readonly byte[] s_readMasks = [
        0x80, 0x3F, 0x00, 0xFF, 0xBF, // NR10 NR11 NR12 NR13 NR14
        0xFF,                         // 0xFF15 (unused)
        0x3F, 0x00, 0xFF, 0xBF,       // NR21 NR22 NR23 NR24
        0x7F, 0xFF, 0x9F, 0xFF, 0xBF, // NR30 NR31 NR32 NR33 NR34
        0xFF,                         // 0xFF1F (unused)
        0xFF, 0x00, 0x00, 0xBF,       // NR41 NR42 NR43 NR44
        0x00, 0x00, 0x70,             // NR50 NR51 NR52
    ];

    private readonly Func<int> m_systemCounter;
    private readonly Func<bool>? m_isDoubleSpeed;
    private readonly Func<bool>? m_isCgb;
    private readonly PulseChannel m_channel1;
    private readonly PulseChannel m_channel2;
    private readonly WaveChannel m_channel3;
    private readonly NoiseChannel m_channel4;

    // The power-on DIV-event skip glitch is a three-state machine: Skip arms it, the first DIV event consumes it
    // (Skipped) without advancing the divider, and the second restores Inactive — still without advancing — so the
    // divider lingers one event, shifting the whole length/sweep/envelope schedule by one.
    private const int SkipDivEventInactive = 0;
    private const int SkipDivEventSkip = 1;
    private const int SkipDivEventSkipped = 2;

    private bool m_powered;
    private int m_preSteppedTCycles;
    private bool m_pendingEnvelopeTick;
    private int m_skipDivEvent;
    private bool m_lastFrameSequencerBit;

    // The DIV-APU divider: increments each non-skipped DIV event, then length clocks on its odd
    // values, sweep on (&3)==3, and the envelope countdown decrements on (&7)==7.
    private int m_frameSequencerStep;
    private byte m_volume;
    private byte m_panning;

    // Audio output: when a sample rate is configured the mixer point-samples the four channels (NR50 master volume,
    // NR51 panning) at that rate and stores high-pass-filtered signed 16-bit stereo frames in a ring for the host to
    // drain. Disabled by default, so conformance runs are unaffected.
    private const int MasterClock = 4194304;
    private int m_audioSampleRate;
    private int m_audioCapacityFrames;
    private long m_audioSampleClock;
    private double m_audioHighPassAlpha;
    private double m_highPassCapacitorLeft;
    private double m_highPassCapacitorRight;
    private short[] m_audioRing = [];
    private int m_audioWriteFrame;
    private int m_audioReadFrame;
    private int m_audioFrameCount;

    /// <summary>Initializes the APU wired to the divider/timer its frame sequencer is divided from, the shared clock
    /// state (double-speed moves the frame-sequencer bit up one), and the machine configuration (the color model
    /// selects the noise channel's sub-cycle timing).</summary>
    /// <param name="timer">The divider/timer whose internal counter the frame sequencer divides.</param>
    /// <param name="clockState">The shared double-speed clock state.</param>
    /// <param name="configuration">The machine configuration.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public Apu(ITimer timer, ClockState clockState, MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(timer);
        ArgumentNullException.ThrowIfNull(clockState);
        ArgumentNullException.ThrowIfNull(configuration);

        Func<bool> isDoubleSpeed = () => clockState.DoubleSpeed;
        Func<bool> isCgb = () => (configuration.Model == ConsoleModel.Cgb);

        m_systemCounter = () => timer.InternalCounter;
        m_isDoubleSpeed = isDoubleSpeed;
        m_isCgb = isCgb;
        m_channel1 = new PulseChannel(hasSweep: true, isCgb: isCgb, isDoubleSpeed: isDoubleSpeed);
        m_channel2 = new PulseChannel(hasSweep: false, isCgb: isCgb, isDoubleSpeed: isDoubleSpeed);
        m_channel3 = new WaveChannel(isCgb: isCgb);
        m_channel4 = new NoiseChannel(isCgb: isCgb, isDoubleSpeed: isDoubleSpeed);
    }

    /// <inheritdoc />
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <summary>Gets the CGB <c>PCM12</c> value: channel 1's current digital output (0-15) in the low nibble,
    /// channel 2's in the high nibble.</summary>
    public byte PcmAmplitude12 =>
        (byte)((m_channel1.Output & 0x0F) | ((m_channel2.Output & 0x0F) << 4));
    /// <summary>Gets the CGB <c>PCM34</c> value: channel 3's current digital output (0-15) in the low nibble,
    /// channel 4's in the high nibble.</summary>
    public byte PcmAmplitude34 =>
        (byte)((m_channel3.Output & 0x0F) | ((m_channel4.Output & 0x0F) << 4));

    /// <summary>Reads an audio register or wave-RAM byte (<c>0xFF10</c>-<c>0xFF3F</c>).</summary>
    /// <param name="address">The address to read.</param>
    /// <returns>The value with hardware read-as-one bits applied.</returns>
    public byte Read(ushort address) {
        if (address >= MemoryMap.WaveRamBase) {
            return m_channel3.ReadWaveRam(offset: (address - MemoryMap.WaveRamBase));
        }

        var index = (address - MemoryMap.AudioBase);

        // 0xFF27-0xFF2F sit between the registers and wave RAM and are unmapped: open bus.
        if (index >= RegisterCount) {
            return 0xFF;
        }

        if (index == MasterControlIndex) {
            return ReadMasterControl();
        }

        return (byte)(RawRegister(address: address) | s_readMasks[index]);
    }
    /// <summary>Writes an audio register or wave-RAM byte (<c>0xFF10</c>-<c>0xFF3F</c>).</summary>
    /// <param name="address">The address to write.</param>
    /// <param name="value">The value written.</param>
    public void Write(ushort address, byte value) {
        if (address >= MemoryMap.WaveRamBase) {
            // Wave RAM stays accessible regardless of the power state.
            m_channel3.WriteWaveRam(offset: (address - MemoryMap.WaveRamBase), value: value);

            return;
        }

        var index = (address - MemoryMap.AudioBase);

        if (index >= RegisterCount) {
            return;
        }

        if (index == MasterControlIndex) {
            WriteMasterControl(value: value);

            return;
        }

        if (m_powered) {
            WriteRegister(address: address, value: value);
        }
        else if (!(m_isCgb?.Invoke() ?? false)) {
            // While powered down the registers are inert — except that on the DMG the length-load registers
            // (NRx1) remain writable (length portion only), and the length counters survive the power cycle. On the
            // CGB the registers are fully read-only while off (the NRx1 length-write exception is monochrome-only).
            switch (address) {
                case 0xFF11:
                    m_channel1.WriteLengthLoad(value: value);

                    break;
                case 0xFF16:
                    m_channel2.WriteLengthLoad(value: value);

                    break;
                case 0xFF1B:
                    m_channel3.WriteLengthLoad(value: value);

                    break;
                case 0xFF20:
                    m_channel4.WriteLengthLoad(value: value);

                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>Seeds the documented DMG post-boot register state: the APU is powered on with the register values
    /// the boot ROM's startup chime leaves behind, and channel&#160;1 is still active. Used when starting a cartridge
    /// without running a boot ROM, so reads of the audio block match hardware.</summary>
    public void InitializePostBoot() {
        m_powered = true;

        // The documented post-boot register values. Channels 2-4 land with their DACs off, so the channel-1
        // trigger in NR14 leaves only channel 1 sounding (NR52 = 0xF1).
        Write(address: 0xFF10, value: 0x80); // NR10
        Write(address: 0xFF11, value: 0xBF); // NR11
        Write(address: 0xFF12, value: 0xF3); // NR12
        Write(address: 0xFF14, value: 0xBF); // NR14 (triggers channel 1)
        Write(address: 0xFF16, value: 0x3F); // NR21
        Write(address: 0xFF19, value: 0xBF); // NR24
        Write(address: 0xFF1A, value: 0x7F); // NR30 (DAC off)
        Write(address: 0xFF1C, value: 0x9F); // NR32
        Write(address: 0xFF1E, value: 0xBF); // NR34
        Write(address: 0xFF23, value: 0xBF); // NR44
        Write(address: Volume, value: 0x77); // NR50
        Write(address: Panning, value: 0xF3); // NR51
    }

    /// <inheritdoc />
    public void Step(int tCycles) {
        // The mixer samples at the real master-cycle rate (the Cpu domain reports four T-cycles per machine cycle, but
        // a double-speed machine cycle is only two master cycles); it runs even while powered off, emitting silence.
        AccumulateAudio(masterCycles: ((m_isDoubleSpeed?.Invoke() ?? false) ? (tCycles / 2) : tCycles));

        if (!m_powered) {
            m_preSteppedTCycles = 0;
            m_pendingEnvelopeTick = false;

            return;
        }

        // A double-speed envelope tick deferred from the previous machine cycle fires now: in double-speed on the
        // CGB-D/E the volume tick lands one machine cycle after the DIV edge.
        if (m_pendingEnvelopeTick) {
            m_pendingEnvelopeTick = false;

            TickEnvelopes();
        }

        // The frame sequencer is clocked by the falling edge of a system-counter bit (sampled per machine cycle,
        // after the timer has advanced the counter this cycle).
        var frameSequencerBit = (((m_isDoubleSpeed?.Invoke() ?? false)) ? FrameSequencerBitDoubleSpeed : FrameSequencerBit);
        var bit = ((m_systemCounter() & frameSequencerBit) != 0);

        if (m_lastFrameSequencerBit && !bit) {
            // Primary edge: length, sweep, and the envelope volume tick/decrement (StepFrameSequencer absorbs the
            // power-on skip glitch internally via the skip_div_event state machine).
            StepFrameSequencer();
        }
        else if (!m_lastFrameSequencerBit && bit) {
            // Secondary edge: arm the envelope clock so the next primary edge ticks the volume — this half-period
            // delay between the secondary and primary DIV edges is the hardware's envelope timing.
            m_channel1.ArmEnvelopeClock();
            m_channel2.ArmEnvelopeClock();
            m_channel4.ArmEnvelopeClock();
        }

        m_lastFrameSequencerBit = bit;

        // A bus access this machine cycle may have already advanced the channels partway, to its sub-cycle sample
        // point (see AdvanceChannelsForAccess) — advance only the remainder so the cycle's total is exact.
        var remaining = (tCycles - m_preSteppedTCycles);

        m_preSteppedTCycles = 0;

        if (remaining > 0) {
            StepChannels(tCycles: remaining);
        }
    }

    /// <summary>Advances the channel frequency generators to the end of the current machine cycle, ahead of a bus
    /// access that reads or writes channel state. The deferred-cycle bus lands an access at the start of its machine
    /// cycle, but the real hardware latches it at the end — after that cycle's channel ticks — so the access must see
    /// the post-tick state (the sub-cycle phase the hardware exposes to the access). The matching <see cref="Step"/>
    /// then skips its channel advance for this cycle so the work is not counted twice. Only the channel generators
    /// move; the frame sequencer keeps its machine-cycle-boundary timing.</summary>
    /// <param name="tCycles">The machine cycle's CPU-domain T-cycle count (the same value <see cref="Step"/> receives).</param>
    public void AdvanceChannelsForAccess(int tCycles) {
        if (!m_powered || (m_preSteppedTCycles > 0)) {
            return;
        }

        StepChannels(tCycles: tCycles);

        m_preSteppedTCycles = tCycles;
    }

    /// <summary>Enables (or, with a non-positive rate, disables) audio output, sizing the internal ring to hold one
    /// second of stereo frames. While enabled the mixer point-samples the channels at this rate; the host drains the
    /// frames with <see cref="ReadAudioSamples"/>.</summary>
    /// <param name="sampleRate">The output sample rate in hertz, or zero/negative to turn output off.</param>
    public void ConfigureAudioOutput(int sampleRate) {
        if (sampleRate <= 0) {
            m_audioSampleRate = 0;
            m_audioRing = [];

            return;
        }

        m_audioSampleRate = sampleRate;
        m_audioCapacityFrames = sampleRate;
        m_audioRing = new short[m_audioCapacityFrames * 2];
        m_audioWriteFrame = 0;
        m_audioReadFrame = 0;
        m_audioFrameCount = 0;
        m_audioSampleClock = 0;
        m_highPassCapacitorLeft = 0;
        m_highPassCapacitorRight = 0;

        // The DC-blocking high-pass charge factor per output sample, scaled from the master clock to the sample rate.
        m_audioHighPassAlpha = Math.Pow(x: 0.999958, y: ((double)MasterClock / sampleRate));
    }

    /// <summary>Gets the number of buffered audio samples available to read (two per stereo frame).</summary>
    public int AvailableAudioSamples =>
        (m_audioFrameCount * 2);

    /// <summary>Drains buffered audio into the destination as interleaved left/right signed 16-bit samples.</summary>
    /// <param name="destination">The buffer to fill; whole frames are written until it or the buffer is exhausted.</param>
    /// <returns>The number of samples written (always even — two per frame).</returns>
    public int ReadAudioSamples(Span<short> destination) {
        var frames = Math.Min(val1: (destination.Length / 2), val2: m_audioFrameCount);

        for (var i = 0; i < frames; i += 1) {
            var index = (m_audioReadFrame * 2);

            destination[i * 2] = m_audioRing[index];
            destination[(i * 2) + 1] = m_audioRing[index + 1];
            m_audioReadFrame = ((m_audioReadFrame + 1) % m_audioCapacityFrames);
        }

        m_audioFrameCount -= frames;

        return (frames * 2);
    }

    private void AccumulateAudio(int masterCycles) {
        if (m_audioSampleRate <= 0) {
            return;
        }

        m_audioSampleClock += ((long)masterCycles * m_audioSampleRate);

        while (m_audioSampleClock >= MasterClock) {
            m_audioSampleClock -= MasterClock;

            PushAudioFrame();
        }
    }

    private void PushAudioFrame() {
        var s1 = m_channel1.Output;
        var s2 = m_channel2.Output;
        var s3 = m_channel3.Output;
        var s4 = m_channel4.Output;

        // NR51 routes each channel to the left (bits 4-7) and/or right (bits 0-3) mix.
        var left = 0;
        var right = 0;

        if ((m_panning & 0x10) != 0) { left += s1; }
        if ((m_panning & 0x20) != 0) { left += s2; }
        if ((m_panning & 0x40) != 0) { left += s3; }
        if ((m_panning & 0x80) != 0) { left += s4; }
        if ((m_panning & 0x01) != 0) { right += s1; }
        if ((m_panning & 0x02) != 0) { right += s2; }
        if ((m_panning & 0x04) != 0) { right += s3; }
        if ((m_panning & 0x08) != 0) { right += s4; }

        // NR50 scales each side by its master volume (the level is value + 1); the maximum mix is four channels of 15
        // at volume 8, so divide by 480 to normalize to 0..1 before the high-pass centres it.
        var rawLeft = ((left * (((m_volume >> 4) & 0x07) + 1)) / 480.0);
        var rawRight = ((right * ((m_volume & 0x07) + 1)) / 480.0);

        var outLeft = (rawLeft - m_highPassCapacitorLeft);
        var outRight = (rawRight - m_highPassCapacitorRight);

        m_highPassCapacitorLeft = (rawLeft - (outLeft * m_audioHighPassAlpha));
        m_highPassCapacitorRight = (rawRight - (outRight * m_audioHighPassAlpha));

        PushFrame(left: ToSample(value: outLeft), right: ToSample(value: outRight));
    }

    private void PushFrame(short left, short right) {
        if (m_audioFrameCount == m_audioCapacityFrames) {
            // The ring is full (the host has not drained fast enough); drop the oldest frame to make room.
            m_audioReadFrame = ((m_audioReadFrame + 1) % m_audioCapacityFrames);
            m_audioFrameCount -= 1;
        }

        var index = (m_audioWriteFrame * 2);

        m_audioRing[index] = left;
        m_audioRing[index + 1] = right;
        m_audioWriteFrame = ((m_audioWriteFrame + 1) % m_audioCapacityFrames);
        m_audioFrameCount += 1;
    }

    private static short ToSample(double value) {
        var scaled = (value * 32767.0);

        return (short)Math.Clamp(value: scaled, min: -32768.0, max: 32767.0);
    }

    // The frame sequencer above is DIV-driven (so it runs at the CPU rate, twice as fast in double-speed), but the
    // channel frequency generators are clocked by the fixed audio master clock — so per CPU machine cycle they advance
    // only half as far in double-speed.
    private void StepChannels(int tCycles) {
        var channelCycles = ((m_isDoubleSpeed?.Invoke() ?? false) ? (tCycles / 2) : tCycles);

        m_channel1.Step(cycles: channelCycles);
        m_channel2.Step(cycles: channelCycles);
        m_channel3.Step(cycles: channelCycles);
        m_channel4.Step(cycles: channelCycles);
    }

    private byte ReadMasterControl() {
        var status =
            (m_channel1.Enabled ? 0x01 : 0x00) |
            (m_channel2.Enabled ? 0x02 : 0x00) |
            (m_channel3.Enabled ? 0x04 : 0x00) |
            (m_channel4.Enabled ? 0x08 : 0x00);

        return (byte)(s_readMasks[MasterControlIndex] | (m_powered ? 0x80 : 0x00) | status);
    }
    private void WriteMasterControl(byte value) {
        var powerOn = ((value & 0x80) != 0);

        if (!powerOn && m_powered) {
            // Powering down zeroes every register and silences the channels; wave RAM is preserved. On the CGB the
            // length counters are also cleared (monochrome models preserve them; the CGB does not).
            var clearLength = (m_isCgb?.Invoke() ?? false);

            m_channel1.PowerOff(clearLength: clearLength);
            m_channel2.PowerOff(clearLength: clearLength);
            m_channel3.PowerOff(clearLength: clearLength);
            m_channel4.PowerOff(clearLength: clearLength);
            m_volume = 0;
            m_panning = 0;
        }
        else if (powerOn && !m_powered) {
            // Powering up restarts the DIV-APU divider. The power-on glitch: if the DIV bit
            // that drives the sequencer is already set, the first DIV event is skipped and the divider starts at 1, so
            // the held value clocks length on the second event.
            m_frameSequencerStep = 0;
            m_skipDivEvent = SkipDivEventInactive;

            var fsBit = ((m_isDoubleSpeed?.Invoke() ?? false) ? FrameSequencerBitDoubleSpeed : FrameSequencerBit);
            var bit = ((m_systemCounter() & fsBit) != 0);

            m_lastFrameSequencerBit = bit;

            if (bit) {
                m_skipDivEvent = SkipDivEventSkip;
                m_frameSequencerStep = 1;
            }
        }

        m_powered = powerOn;
    }

    private byte RawRegister(ushort address) =>
        address switch {
            >= 0xFF10 and <= 0xFF14 => m_channel1.ReadRegister(register: (address - 0xFF10)),
            >= 0xFF16 and <= 0xFF19 => m_channel2.ReadRegister(register: (address - 0xFF15)),
            >= 0xFF1A and <= 0xFF1E => m_channel3.ReadRegister(register: (address - 0xFF1A)),
            >= 0xFF20 and <= 0xFF23 => m_channel4.ReadRegister(register: (address - 0xFF1F)),
            Volume => m_volume,
            Panning => m_panning,
            _ => 0x00, // 0xFF15 / 0xFF1F unused; the read mask makes them 0xFF
        };
    private void WriteRegister(ushort address, byte value) {
        // The frame sequencer's next step clocks the length counter on its even steps; channels need this for the
        // obscure extra-length-clock behavior when length is enabled or the channel is triggered.
        var nextStepClocksLength = ((m_frameSequencerStep & 0x01) == 0);

        switch (address) {
            case >= 0xFF10 and <= 0xFF14:
                m_channel1.WriteRegister(register: (address - 0xFF10), value: value, nextStepClocksLength: nextStepClocksLength);

                break;
            case >= 0xFF16 and <= 0xFF19:
                m_channel2.WriteRegister(register: (address - 0xFF15), value: value, nextStepClocksLength: nextStepClocksLength);

                break;
            case >= 0xFF1A and <= 0xFF1E:
                m_channel3.WriteRegister(register: (address - 0xFF1A), value: value, nextStepClocksLength: nextStepClocksLength);

                break;
            case >= 0xFF20 and <= 0xFF23:
                m_channel4.WriteRegister(register: (address - 0xFF1F), value: value, nextStepClocksLength: nextStepClocksLength);

                break;
            case Volume:
                m_volume = value;

                break;
            case Panning:
                m_panning = value;

                break;
            default:
                break;
        }
    }

    private void StepFrameSequencer() {
        // The DIV-APU event. The power-on skip glitch lingers the divider for two events: the first event is
        // consumed without advancing or acting, the second restores Inactive (still no advance) so it acts on the held
        // divider value — shifting the whole 256/128/64 Hz schedule by one event.
        if (m_skipDivEvent == SkipDivEventSkip) {
            m_skipDivEvent = SkipDivEventSkipped;

            return;
        }

        if (m_skipDivEvent == SkipDivEventSkipped) {
            m_skipDivEvent = SkipDivEventInactive;
        }
        else {
            m_frameSequencerStep = ((m_frameSequencerStep + 1) & 0x07);
        }

        // The envelope countdown decrements once per eight events (on the post-increment (step&7)==7).
        if ((m_frameSequencerStep & 0x07) == 0x07) {
            m_channel1.DecrementEnvelopeCountdown();
            m_channel2.DecrementEnvelopeCountdown();
            m_channel4.DecrementEnvelopeCountdown();
        }

        // The volume tick fires every primary edge, but only takes effect when the prior secondary edge armed the
        // clock (i.e. the countdown had expired) — the deferred envelope. In double-speed it is deferred a
        // further machine cycle.
        if (m_isDoubleSpeed?.Invoke() ?? false) {
            m_pendingEnvelopeTick = true;
        }
        else {
            TickEnvelopes();
        }

        // Length clocks at 256 Hz (odd divider values); sweep at 128 Hz ((&3)==3).
        if ((m_frameSequencerStep & 0x01) == 0x01) {
            ClockLength();
        }

        if ((m_frameSequencerStep & 0x03) == 0x03) {
            m_channel1.StepSweep();
        }
    }
    private void TickEnvelopes() {
        m_channel1.TickEnvelope();
        m_channel2.TickEnvelope();
        m_channel4.TickEnvelope();
    }
    private void ClockLength() {
        m_channel1.StepLength();
        m_channel2.StepLength();
        m_channel3.StepLength();
        m_channel4.StepLength();
    }
}

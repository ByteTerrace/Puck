namespace Puck.GameBoy;

/// <summary>
/// The audio processing unit: four sound channels (two pulse, one wave, one noise), the master control registers
/// (<c>NR50</c>-<c>NR52</c>), and channel-3 wave-pattern RAM, occupying <c>0xFF10</c>-<c>0xFF3F</c>. It owns the
/// frame sequencer — a 512&#160;Hz timeline divided from the system counter that clocks the length counters
/// (256&#160;Hz), the volume envelopes (64&#160;Hz), and the channel-1 frequency sweep (128&#160;Hz). Each register
/// exposes only some bits (the rest read as one), and the <c>NR52</c> power switch silences and zeroes everything
/// while it is off.
/// </summary>
public sealed class Apu : IClockedComponent {
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
    private readonly PulseChannel m_channel1;
    private readonly PulseChannel m_channel2;
    private readonly WaveChannel m_channel3;
    private readonly NoiseChannel m_channel4;

    private bool m_powered;
    private bool m_channelsPreStepped;
    private bool m_lastFrameSequencerBit;
    private int m_frameSequencerStep;
    private byte m_volume;
    private byte m_panning;

    /// <summary>Initializes the APU wired to the system counter its frame sequencer is divided from.</summary>
    /// <param name="systemCounter">Reads the shared 16-bit system counter (the timer's internal divider).</param>
    /// <param name="isDoubleSpeed">Reads whether the CGB is in double-speed mode, which moves the frame-sequencer bit up one; <see langword="null"/> (the default) is treated as single-speed.</param>
    /// <param name="isCgb">Reads whether the console is a CGB, selecting the noise channel's sub-cycle timing model; <see langword="null"/> (the default) is treated as DMG.</param>
    /// <exception cref="ArgumentNullException"><paramref name="systemCounter"/> is <see langword="null"/>.</exception>
    public Apu(Func<int> systemCounter, Func<bool>? isDoubleSpeed = null, Func<bool>? isCgb = null) {
        ArgumentNullException.ThrowIfNull(systemCounter);

        m_systemCounter = systemCounter;
        m_isDoubleSpeed = isDoubleSpeed;
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
        else {
            // While powered down the registers are inert — except that on the DMG the length-load registers
            // (NRx1) remain writable (length portion only), and the length counters survive the power cycle.
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
        if (!m_powered) {
            m_channelsPreStepped = false;

            return;
        }

        // The frame sequencer is clocked by the falling edge of a system-counter bit (sampled per machine cycle,
        // after the timer has advanced the counter this cycle).
        var frameSequencerBit = (((m_isDoubleSpeed?.Invoke() ?? false)) ? FrameSequencerBitDoubleSpeed : FrameSequencerBit);
        var bit = ((m_systemCounter() & frameSequencerBit) != 0);

        if (m_lastFrameSequencerBit && !bit) {
            // Primary edge: length, sweep, and the envelope volume tick/decrement.
            StepFrameSequencer();
        }
        else if (!m_lastFrameSequencerBit && bit) {
            // Secondary edge: arm the envelope clock so the next primary edge ticks the volume — this half-period
            // delay is what the SameSuite envelope-timing tests pin (SameBoy's GB_apu_div_secondary_event).
            m_channel1.ArmEnvelopeClock();
            m_channel2.ArmEnvelopeClock();
            m_channel4.ArmEnvelopeClock();
        }

        m_lastFrameSequencerBit = bit;

        // The channel frequency generators were already advanced to the end of this machine cycle by a bus access
        // that observed them (see AdvanceChannelsForAccess) — don't advance them twice.
        if (m_channelsPreStepped) {
            m_channelsPreStepped = false;

            return;
        }

        StepChannels(tCycles: tCycles);
    }

    /// <summary>Advances the channel frequency generators to the end of the current machine cycle, ahead of a bus
    /// access that reads or writes channel state. The deferred-cycle bus lands an access at the start of its machine
    /// cycle, but the real hardware latches it at the end — after that cycle's channel ticks — so the access must see
    /// the post-tick state (the sub-cycle phase the SameSuite alignment tests pin). The matching <see cref="Step"/>
    /// then skips its channel advance for this cycle so the work is not counted twice. Only the channel generators
    /// move; the frame sequencer keeps its machine-cycle-boundary timing.</summary>
    /// <param name="tCycles">The machine cycle's CPU-domain T-cycle count (the same value <see cref="Step"/> receives).</param>
    public void AdvanceChannelsForAccess(int tCycles) {
        if (!m_powered || m_channelsPreStepped) {
            return;
        }

        StepChannels(tCycles: tCycles);

        m_channelsPreStepped = true;
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
            // Powering down zeroes every register and silences the channels; wave RAM is preserved.
            m_channel1.PowerOff();
            m_channel2.PowerOff();
            m_channel3.PowerOff();
            m_channel4.PowerOff();
            m_volume = 0;
            m_panning = 0;
        }
        else if (powerOn && !m_powered) {
            // Powering up restarts the frame-sequencer timeline.
            m_frameSequencerStep = 0;
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
        // The eight-step 512 Hz sequence: length at 256 Hz, sweep at 128 Hz, envelope at 64 Hz.
        switch (m_frameSequencerStep) {
            case 0:
            case 4:
                ClockLength();

                break;
            case 2:
            case 6:
                ClockLength();
                m_channel1.StepSweep();

                break;
            default:
                break;
        }

        // SameBoy decrements the envelope countdown at post-increment div&7==7; this sequencer checks the step before
        // incrementing, so that lands on step 6 here.
        if (m_frameSequencerStep == 6) {
            m_channel1.DecrementEnvelopeCountdown();
            m_channel2.DecrementEnvelopeCountdown();
            m_channel4.DecrementEnvelopeCountdown();
        }

        // The volume tick fires every primary edge, but only takes effect when the prior secondary edge armed the
        // clock (i.e. the countdown had expired) — SameBoy's deferred envelope.
        m_channel1.TickEnvelope();
        m_channel2.TickEnvelope();
        m_channel4.TickEnvelope();

        m_frameSequencerStep = ((m_frameSequencerStep + 1) & 0x07);
    }
    private void ClockLength() {
        m_channel1.StepLength();
        m_channel2.StepLength();
        m_channel3.StepLength();
        m_channel4.StepLength();
    }
}

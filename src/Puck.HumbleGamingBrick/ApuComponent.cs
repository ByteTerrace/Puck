using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The audio processing unit. It owns the sound register file (NR10–NR52) and wave RAM, the master power switch, and
/// the four channels' length counters, envelopes, sweep unit, and generators. The unit straddles the console's two
/// clocks, so it is driven through two seams: its own CPU-domain <see cref="Tick"/> follows the DIV-APU event — the
/// falling edge of one bit of the timer's DIV counter (bit 12, or bit 13 under Color double speed) — which advances a
/// 512 Hz divider that clocks the length counters, the sweep unit, and the envelope pre-count, while the rising edge
/// of the same bit arms the envelope clocks half a period later; the channel generators (duty positions, the wave
/// sample fetcher, and the noise counter driving the LFSR) run on the fixed 2 MiHz audio clock, derived here by
/// halving the whole-dot stream <see cref="ApuGeneratorClock"/> delivers, so engaging Color double speed does not
/// raise the audio pitch. Reading DIV rather than a private divider is what makes resetting DIV perturb the sequencer
/// exactly as on hardware. Powering the unit off through NR52 clears the register file and every generator; wave RAM
/// stays accessible. All state is plain fields captured in a fixed order, so the APU snapshots and forks like every
/// other component.
/// </summary>
/// <remarks>
/// The channel outputs the CPU observes through PCM12/PCM34 are LATCHED, not recomputed: each channel publishes a new
/// digital level only when its generator steps, its envelope moves, or a register write reaches it, and a channel
/// whose DAC is off holds the level it last published. That latch is the emulated audio contract; the mixing that the
/// host stage does downstream is presentation.
/// </remarks>
public sealed class ApuComponent : IApu, IClockedComponent, ISnapshotable, IModeSwitchable {
    private const int ChannelCount = 4;
    private const int DoubleSpeedDivApuBit = 13;
    private const byte LengthDataMask = 0x3F;
    private const byte LengthEnableBit = 0x40;
    private const byte MasterPower = 0x80;
    private const int MaxSampleLength = 0x7FF;
    private const int NormalDivApuBit = 12;
    private const byte Nr52Readable = 0x70;
    private const int RegisterCount = 0x17;
    private const byte SquareNoiseDacMask = 0xF8;
    private const byte SweepNegate = 0x08;
    private const byte SweepShiftMask = 0x07;
    private const byte TriggerBit = 0x80;
    private const byte WaveDacEnable = 0x80;
    private const int WaveRamSize = 16;
    // The frame-sequencer divider skips its first event when the unit is powered on while the DIV-APU bit is already
    // high; these are the three states of that one-shot.
    private const int SkipDivEventInactive = 0;
    private const int SkipDivEventSkipped = 1;
    private const int SkipDivEventPending = 2;
    // CPU T-cycles between a DIV-APU event and the envelope step it defers under Color double speed on the later
    // colour steppings: the step lands one machine cycle after the event rather than inside it.
    private const int DeferredEnvelopeDelay = 4;
    // Audio ticks between the CPU's write strobe reaching the unit and its read strobe doing so. A write commits on
    // its machine cycle's first T-cycle and a read settles two T-cycles in, so a read observes one more audio tick
    // than the write that set the event up. The generators absorb it by loading their countdowns this much longer;
    // the write-side predicates undo it by looking one tick ahead (PeekSquare, PeekWaveFetch).
    private const int ReadStrobeSkew = 1;
    // Audio ticks the sweep unit's arming delay carries on top of the value the 128 Hz clock and a channel-1
    // trigger nominally load. The 128 Hz path is observed through a read and so carries the read-strobe skew.
    private const int SweepClockArmSkew = ReadStrobeSkew;
    // The trigger path is observed against the NR13/NR14 write that follows it rather than through a read, so it
    // does not carry the read-strobe skew; the early steppings additionally complete their trigger calculation one
    // tick sooner than the later ones. This pair stays calibrated, not derived: the trace pins the mechanism (the
    // overflow check disabling channel 1 one sweep tick early) but not the constant, and every other pair loses at
    // least one case across the sweep families of the two conformance suites and the sample-accurate suite.
    private const int SweepTriggerArmSkewEarly = -1;
    private const int SweepTriggerArmSkewLate = 0;
    // Audio ticks the channel-1 restart hold carries past the value the trigger loads, over which the sweep unit
    // refuses to refresh its shadow frequency. Also calibrated.
    private const int RestartHoldSkew = 1;
    // Audio ticks the wave channel's sample fetcher waits past a trigger, on top of the freshly loaded period.
    private const int WaveTriggerFetchDelay = (3 + ReadStrobeSkew);
    // NR10 sweep, NR11 duty/length, NR12 envelope, NR13 frequency low, NR14 trigger/control.
    private const int Nr10 = 0x00;
    private const int Nr11 = 0x01;
    private const int Nr12 = 0x02;
    private const int Nr13 = 0x03;
    private const int Nr14 = 0x04;
    private const int Nr21 = 0x06;
    private const int Nr22 = 0x07;
    private const int Nr23 = 0x08;
    private const int Nr24 = 0x09;
    private const int Nr30 = 0x0A;
    private const int Nr31 = 0x0B;
    private const int Nr32 = 0x0C;
    private const int Nr33 = 0x0D;
    private const int Nr34 = 0x0E;
    private const int Nr41 = 0x10;
    private const int Nr42 = 0x11;
    private const int Nr43 = 0x12;
    private const int Nr44 = 0x13;
    private const int Nr50 = 0x14;
    private const int Nr51 = 0x15;

    // The bits each register forces high when read, indexed by (address - 0xFF10). NR52 is special-cased.
    private static readonly byte[] ReadMasks = [
        0x80, 0x3F, 0x00, 0xFF, 0xBF, // NR10 NR11 NR12 NR13 NR14
        0xFF,                         // FF15 (unused)
        0x3F, 0x00, 0xFF, 0xBF,       // NR21 NR22 NR23 NR24
        0x7F, 0xFF, 0x9F, 0xFF, 0xBF, // NR30 NR31 NR32 NR33 NR34
        0xFF,                         // FF1F (unused)
        0xFF, 0x00, 0x00, 0xBF,       // NR41 NR42 NR43 NR44
        0x00, 0x00,                   // NR50 NR51
        Nr52Readable,                 // NR52
    ];
    // The maximum length-counter reload per channel: 64 for the two square channels and the noise channel, 256 for wave.
    private static readonly int[] LengthMaxima = [64, 64, 256, 64];
    // The four square-wave duty patterns (12.5/25/50/75%), one high/low bit per the eight steps of a period. Indexed
    // [duty * 8 + position]; the selected bit gates the channel's volume onto its digital output.
    private static readonly byte[] DutyTable = [
        0, 0, 0, 0, 0, 0, 0, 1,
        1, 0, 0, 0, 0, 0, 0, 1,
        1, 0, 0, 0, 0, 1, 1, 1,
        0, 1, 1, 1, 1, 1, 1, 0,
    ];
    // The wave channel's output right-shift selected by NR32 bits 5-6: 0 mutes (shift 4 zeroes a 4-bit nibble), then
    // 100%/50%/25% volume.
    private static readonly int[] WaveVolumeShift = [4, 0, 1, 2];
    // The reload phase a NR43 write picks up from the 1 MiHz alignment, indexed by the alignment's low two bits.
    private static readonly int[] NoiseReloadPhase = [2, 1, 0, 3];
    private static readonly int[] EarlyNoiseReloadPhase = [2, 1, 4, 3];

    private readonly IKey1 m_key1;
    private readonly ITimer m_timer;
    private readonly byte[] m_registers = new byte[RegisterCount];
    private readonly byte[] m_waveRam = new byte[WaveRamSize];

    // Whether each channel is sounding, and the four-bit digital level it last published. The level is a latch: a
    // channel whose DAC is off keeps the level it had, which is what makes a DAC cut a flat step rather than a click.
    private readonly bool[] m_channelActive = new bool[ChannelCount];
    private readonly int[] m_sample = new int[ChannelCount];
    private readonly int[] m_pulseLength = new int[ChannelCount];
    private readonly bool[] m_lengthEnabled = new bool[ChannelCount];

    // The envelope unit for the two square channels and the noise channel (the wave slot is unused). The volume moves
    // only while the envelope's own clock line is high: the DIV-APU falling edge counts the reload down, the rising
    // edge raises the line when the count lands, and the next falling edge steps the volume and drops it again. The
    // lock latches when the line rises with the volume already at the rail it would move toward, which is what stops a
    // rail-parked envelope from wrapping and what a NRx2 write can clear.
    private readonly int[] m_envelopeVolume = new int[ChannelCount];
    private readonly int[] m_envelopeCountdown = new int[ChannelCount];
    private readonly bool[] m_envelopeClock = new bool[ChannelCount];
    private readonly bool[] m_envelopeLocked = new bool[ChannelCount];
    private readonly bool[] m_envelopeShouldLock = new bool[ChannelCount];

    // The square channels' generator state. The sample length is the live eleven-bit frequency, which the sweep unit
    // rewrites without touching NR13/NR14; the countdown runs in 2 MiHz audio ticks and reloads to twice the
    // complement, so one duty step spans four dots per unit of (2048 - frequency).
    private readonly int[] m_squareSampleLength = new int[2];
    private readonly int[] m_squareSampleCountdown = new int[2];
    private readonly int[] m_squareSampleIndex = new int[2];
    private readonly bool[] m_squareSampleSuppressed = new bool[2];
    private readonly int[] m_squareDelay = new int[2];
    private readonly bool[] m_squareDidTick = new bool[2];
    private readonly bool[] m_squareJustReloaded = new bool[2];

    // Cached revision capabilities. Mutable so a live device swap re-gates them.
    private bool m_hasEarlyAudioStepping;
    private bool m_hasLateColorAudioQuirks;
    private bool m_hasPerChannelDacs;
    private bool m_hasShortSweepRestartHold;
    private bool m_isColor;

    private int m_channel1CompletedAddend;
    private int m_channel1RestartHold;
    private byte m_divDivider;
    private bool m_generatorHalfStep;
    private bool m_lastDivApuBit;
    // The 1 MiHz alignment phase inside the 2 MiHz audio clock: the sweep unit and the trigger delays are quoted
    // against it, so a write lands differently depending on which half of the slower clock it arrives in.
    private int m_lfDiv;
    private int m_noiseAlignment;
    private bool m_noiseBackgroundCounterActive;
    // The noise channel's fourteen-bit free-running counter and the divider that clocks it. The LFSR steps on the
    // rising edge of the counter bit NR43's clock shift selects, so two (divisor, shift) pairs naming the same rate
    // step the LFSR at the same instants, and a shift past the counter's width never steps it at all.
    private int m_noiseCounter;
    private bool m_noiseCounterActive;
    private int m_noiseCounterCountdown;
    private bool m_noiseCountdownReloaded;
    private bool m_noiseCurrentLfsrSample;
    private bool m_noiseDidStepCounter;
    private int m_noiseLfsr;
    private bool m_noiseNarrow;
    private bool m_noiseStartedWithDacDisabled;
    private int m_pendingEnvelopeDelay;
    private bool m_powered;
    private int m_shadowSweepSampleLength;
    private int m_skipDivEvent;
    private int m_sweepCalculateCountdown;
    private int m_sweepCalculateReloadTimer;
    private bool m_sweepInstantCalculationDone;
    private int m_sweepLengthAddend;
    private int m_squareSweepCountdown;
    private bool m_unshiftedSweep;
    private bool m_waveEnable;
    private bool m_waveFormJustRead;
    private bool m_wavePulsed;
    private byte m_waveSampleByte;
    private int m_waveSampleCountdown;
    private int m_waveSampleIndex;
    private int m_waveSampleLength;
    private int m_waveShift;

    /// <summary>Creates the APU wired to the timer whose DIV counter clocks its frame sequencer and the speed unit that
    /// selects which DIV bit does so. Without a boot ROM it is seeded powered on as the post-boot machine leaves it: the
    /// boot ROM's start-up chime leaves channel 1 still sounding (its envelope already decayed to zero) with the chime's
    /// frequency in NR13/NR14, full master volume routed to both terminals, and — on Color silicon that initializes it —
    /// the alternating wave-RAM pattern. With one it powers on silent (the wave-RAM pattern is the hardware's, so it
    /// stays).</summary>
    /// <param name="timer">The divider/timer block, read for the DIV-APU event.</param>
    /// <param name="key1">The Color speed-switch unit, read for the current speed.</param>
    /// <param name="configuration">The machine configuration, which selects the wave RAM's power-on pattern.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ApuComponent(ITimer timer, IKey1 key1, MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: timer);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_key1 = key1;
        m_timer = timer;

        ApplyModel(model: configuration.Model);
        ResetUnit();

        // With a boot ROM the unit powers on silent and unpowered — the boot program writes NR52 and plays its own
        // beep. Without one, the beep's register handoff is seeded directly.
        if (configuration.BootRom is null) {
            m_powered = true;

            // The boot beep's register handoff, common to both models.
            m_registers[Nr11] = 0x80; // duty 2 (50%), length data 0
            m_registers[Nr12] = 0xF3; // starting volume 15, decreasing, pace 3
            m_registers[Nr13] = 0xC1;
            m_registers[Nr14] = 0x87; // trigger latched, frequency high bits 0b111
            m_registers[Nr50] = 0x77;
            m_registers[Nr51] = 0xF3;
            m_squareSampleLength[0] = (m_registers[Nr13] | ((m_registers[Nr14] & 0x07) << 8));

            // The companion console's boot ROM plays no chime, so it hands off powered with every channel silent
            // (NR52 reads 0xF0 rather than 0xF1).
            if (configuration.Model.LeavesBootChimeSounding()) {
                m_channelActive[0] = true;
                m_pulseLength[0] = LengthMaxima[0];
                m_envelopeCountdown[0] = 1;
                m_squareSampleIndex[0] = 2;
                m_squareSampleCountdown[0] = 1020; // audio ticks, half the chime's remaining dots
            }

            // The boot ROM leaves the 512 Hz divider mid-cycle, not at zero: two DIV-APU events have elapsed on
            // Color and three on monochrome.
            m_divDivider = (configuration.Model.SupportsColor()
                ? (byte)2
                : (byte)3);
        }

        m_lastDivApuBit = DivApuBit();

        if (configuration.Model.SeedsWaveRamOnBoot()) {
            for (var offset = 0; (offset < WaveRamSize); offset += 2) {
                m_waveRam[(offset + 1)] = 0xFF;
            }
        }
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;

    /// <inheritdoc/>
    public void Tick() {
        // Stop mode freezes the DIV counter, so the frame sequencer has no edge to follow.
        if (m_key1.IsStopped) {
            return;
        }

        // The deferred envelope step lands one machine cycle after the DIV-APU event that raised it.
        if (
            (m_pendingEnvelopeDelay > 0) &&
            (--m_pendingEnvelopeDelay == 0)
        ) {
            StepPendingEnvelopes();
        }

        // The DIV-APU bit is followed every T-cycle even while powered off, so the divider's phase relative to DIV
        // survives a power cycle; only the stepping is gated on power.
        var bit = DivApuBit();

        if (
            m_lastDivApuBit &&
            !bit
        ) {
            DivEvent();
        } else if (
            !m_lastDivApuBit &&
            bit
        ) {
            DivSecondaryEvent();
        }

        m_lastDivApuBit = bit;
    }
    /// <summary>Advances the channel generators. <see cref="ApuGeneratorClock"/> calls this once per whole dot of the
    /// fixed 4 MiHz clock; the generators themselves run at half that, so every second call steps the unit and the one
    /// between it only carries the phase. The frequency timers therefore do not follow the CPU clock, and Color double
    /// speed leaves the audio pitch unchanged.</summary>
    public void TickGenerators() {
        // Colour silicon keeps the audio oscillator running through stop mode; monochrome halts it with the rest of
        // the SoC.
        if (
            m_key1.IsStopped &&
            !m_isColor
        ) {
            return;
        }

        m_generatorHalfStep = !m_generatorHalfStep;

        if (m_generatorHalfStep) {
            return;
        }

        RunAudioTick();
    }
    /// <inheritdoc/>
    public byte ReadRegister(ushort address) {
        if (address >= MemoryMap.WaveRamStart) {
            // While the channel plays, CPU access follows the channel, not the address: it lands on the byte at the
            // live sample position. Colour silicon buffers the port so the access always succeeds; monochrome access
            // succeeds only on the tick a fetch lands, and reads outside it float to 0xFF.
            if (m_channelActive[2]) {
                if (
                    !m_isColor &&
                    !m_waveFormJustRead
                ) {
                    return 0xFF;
                }

                if (!m_hasPerChannelDacs) {
                    return 0xFF;
                }

                return m_waveRam[(m_waveSampleIndex >> 1)];
            }

            return m_waveRam[(address - MemoryMap.WaveRamStart)];
        }

        if (address == MemoryMap.AudioMasterControl) {
            var status = 0;

            for (var channel = 0; (channel < ChannelCount); ++channel) {
                if (m_channelActive[channel]) {
                    status |= (1 << channel);
                }
            }

            return ((byte)(Nr52Readable | (m_powered
                ? MasterPower
                : 0) | status));
        }

        if (address > MemoryMap.AudioEnd) {
            return 0xFF; // FF27–FF2F are unused.
        }

        var offset = (address - MemoryMap.AudioStart);

        return ((byte)(m_registers[offset] | ReadMasks[offset]));
    }
    /// <inheritdoc/>
    public void WriteRegister(ushort address, byte value) {
        if (address >= MemoryMap.WaveRamStart) {
            WriteWaveRam(
                address: address,
                value: value
            );

            return;
        }

        if (address == MemoryMap.AudioMasterControl) {
            WriteMasterControl(value: value);

            return;
        }

        // Unused registers in the audio range ignore writes.
        if (address > MemoryMap.AudioEnd) {
            return;
        }

        var offset = (address - MemoryMap.AudioStart);

        // While powered off, Color ignores every write; monochrome hardware still lets the length-load registers
        // (NRx1) through, and even then only their length field reaches the counter.
        if (!m_powered) {
            if (
                m_isColor ||
                (offset is not (Nr11 or Nr21 or Nr31 or Nr41))
            ) {
                return;
            }
        }

        WriteChannelRegister(
            offset: offset,
            value: value
        );
    }
    /// <inheritdoc/>
    public byte ReadPcm(ushort address) {
        // PCM12 packs channel 1 in the low nibble and channel 2 in the high; PCM34 packs channel 3 (wave) low and
        // channel 4 (noise) high. Each nibble is the channel's latched digital level, or zero while it is silent.
        if (address == MemoryMap.PcmAmplitude12) {
            return ((byte)(PackedSample(channel: 0) | (PackedSample(channel: 1) << 4)));
        }

        return ((byte)(PackedSample(channel: 2) | (PackedSample(channel: 3) << 4)));
    }
    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) {
        m_hasEarlyAudioStepping = model.HasEarlyAudioStepping();
        m_hasLateColorAudioQuirks = model.HasLateColorAudioQuirks();
        m_hasPerChannelDacs = model.HasPerChannelDacs();
        m_hasShortSweepRestartHold = model.HasShortSweepRestartHold();
        m_isColor = model.SupportsColor();
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBoolean(value: m_powered);
        writer.WriteByte(value: m_divDivider);
        writer.WriteBoolean(value: m_lastDivApuBit);
        writer.WriteBoolean(value: m_generatorHalfStep);
        writer.WriteInt32(value: m_lfDiv);
        writer.WriteInt32(value: m_skipDivEvent);
        writer.WriteInt32(value: m_pendingEnvelopeDelay);
        writer.WriteInt32(value: m_channel1CompletedAddend);
        writer.WriteInt32(value: m_channel1RestartHold);
        writer.WriteInt32(value: m_shadowSweepSampleLength);
        writer.WriteInt32(value: m_squareSweepCountdown);
        writer.WriteInt32(value: m_sweepCalculateCountdown);
        writer.WriteInt32(value: m_sweepCalculateReloadTimer);
        writer.WriteInt32(value: m_sweepLengthAddend);
        writer.WriteBoolean(value: m_sweepInstantCalculationDone);
        writer.WriteBoolean(value: m_unshiftedSweep);
        writer.WriteBoolean(value: m_waveEnable);
        writer.WriteBoolean(value: m_waveFormJustRead);
        writer.WriteBoolean(value: m_wavePulsed);
        writer.WriteByte(value: m_waveSampleByte);
        writer.WriteInt32(value: m_waveSampleCountdown);
        writer.WriteInt32(value: m_waveSampleIndex);
        writer.WriteInt32(value: m_waveSampleLength);
        writer.WriteInt32(value: m_waveShift);
        writer.WriteInt32(value: m_noiseAlignment);
        writer.WriteBoolean(value: m_noiseBackgroundCounterActive);
        writer.WriteInt32(value: m_noiseCounter);
        writer.WriteBoolean(value: m_noiseCounterActive);
        writer.WriteInt32(value: m_noiseCounterCountdown);
        writer.WriteBoolean(value: m_noiseCountdownReloaded);
        writer.WriteBoolean(value: m_noiseCurrentLfsrSample);
        writer.WriteBoolean(value: m_noiseDidStepCounter);
        writer.WriteInt32(value: m_noiseLfsr);
        writer.WriteBoolean(value: m_noiseNarrow);
        writer.WriteBoolean(value: m_noiseStartedWithDacDisabled);
        writer.WriteBytes(value: m_registers);
        writer.WriteBytes(value: m_waveRam);

        for (var channel = 0; (channel < ChannelCount); ++channel) {
            writer.WriteBoolean(value: m_channelActive[channel]);
            writer.WriteBoolean(value: m_lengthEnabled[channel]);
            writer.WriteInt32(value: m_pulseLength[channel]);
            writer.WriteInt32(value: m_sample[channel]);
            writer.WriteInt32(value: m_envelopeVolume[channel]);
            writer.WriteInt32(value: m_envelopeCountdown[channel]);
            writer.WriteBoolean(value: m_envelopeClock[channel]);
            writer.WriteBoolean(value: m_envelopeLocked[channel]);
            writer.WriteBoolean(value: m_envelopeShouldLock[channel]);
        }

        for (var channel = 0; (channel < 2); ++channel) {
            writer.WriteInt32(value: m_squareSampleLength[channel]);
            writer.WriteInt32(value: m_squareSampleCountdown[channel]);
            writer.WriteInt32(value: m_squareSampleIndex[channel]);
            writer.WriteBoolean(value: m_squareSampleSuppressed[channel]);
            writer.WriteInt32(value: m_squareDelay[channel]);
            writer.WriteBoolean(value: m_squareDidTick[channel]);
            writer.WriteBoolean(value: m_squareJustReloaded[channel]);
        }
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_powered = reader.ReadBoolean();
        m_divDivider = reader.ReadByte();
        m_lastDivApuBit = reader.ReadBoolean();
        m_generatorHalfStep = reader.ReadBoolean();
        m_lfDiv = reader.ReadInt32();
        m_skipDivEvent = reader.ReadInt32();
        m_pendingEnvelopeDelay = reader.ReadInt32();
        m_channel1CompletedAddend = reader.ReadInt32();
        m_channel1RestartHold = reader.ReadInt32();
        m_shadowSweepSampleLength = reader.ReadInt32();
        m_squareSweepCountdown = reader.ReadInt32();
        m_sweepCalculateCountdown = reader.ReadInt32();
        m_sweepCalculateReloadTimer = reader.ReadInt32();
        m_sweepLengthAddend = reader.ReadInt32();
        m_sweepInstantCalculationDone = reader.ReadBoolean();
        m_unshiftedSweep = reader.ReadBoolean();
        m_waveEnable = reader.ReadBoolean();
        m_waveFormJustRead = reader.ReadBoolean();
        m_wavePulsed = reader.ReadBoolean();
        m_waveSampleByte = reader.ReadByte();
        m_waveSampleCountdown = reader.ReadInt32();
        m_waveSampleIndex = reader.ReadInt32();
        m_waveSampleLength = reader.ReadInt32();
        m_waveShift = reader.ReadInt32();
        m_noiseAlignment = reader.ReadInt32();
        m_noiseBackgroundCounterActive = reader.ReadBoolean();
        m_noiseCounter = reader.ReadInt32();
        m_noiseCounterActive = reader.ReadBoolean();
        m_noiseCounterCountdown = reader.ReadInt32();
        m_noiseCountdownReloaded = reader.ReadBoolean();
        m_noiseCurrentLfsrSample = reader.ReadBoolean();
        m_noiseDidStepCounter = reader.ReadBoolean();
        m_noiseLfsr = reader.ReadInt32();
        m_noiseNarrow = reader.ReadBoolean();
        m_noiseStartedWithDacDisabled = reader.ReadBoolean();
        reader.ReadBytes(destination: m_registers);
        reader.ReadBytes(destination: m_waveRam);

        for (var channel = 0; (channel < ChannelCount); ++channel) {
            m_channelActive[channel] = reader.ReadBoolean();
            m_lengthEnabled[channel] = reader.ReadBoolean();
            m_pulseLength[channel] = reader.ReadInt32();
            m_sample[channel] = reader.ReadInt32();
            m_envelopeVolume[channel] = reader.ReadInt32();
            m_envelopeCountdown[channel] = reader.ReadInt32();
            m_envelopeClock[channel] = reader.ReadBoolean();
            m_envelopeLocked[channel] = reader.ReadBoolean();
            m_envelopeShouldLock[channel] = reader.ReadBoolean();
        }

        for (var channel = 0; (channel < 2); ++channel) {
            m_squareSampleLength[channel] = reader.ReadInt32();
            m_squareSampleCountdown[channel] = reader.ReadInt32();
            m_squareSampleIndex[channel] = reader.ReadInt32();
            m_squareSampleSuppressed[channel] = reader.ReadBoolean();
            m_squareDelay[channel] = reader.ReadInt32();
            m_squareDidTick[channel] = reader.ReadBoolean();
            m_squareJustReloaded[channel] = reader.ReadBoolean();
        }
    }

    // One 2 MiHz audio tick: the sweep unit's 1 MiHz half-rate, the two square duty counters, the wave fetcher, and
    // the noise counter, in that order.
    private void RunAudioTick() {
        m_lfDiv ^= 1;
        m_noiseAlignment = ((m_noiseAlignment + 1) & 0xFF);

        RunSweepTick();
        RunSquareTick(channel: 0);
        RunSquareTick(channel: 1);
        RunWaveTick();
        RunNoiseTick();
    }
    // The sweep unit's own clock is half the audio clock, so it advances on the ticks that land on the slower clock's
    // edge. A reload timer covers the gap between arming a calculation and the calculation running.
    private void RunSweepTick() {
        // Nothing is armed and nothing is holding: the whole unit is idle, which is the common case.
        if (
            (m_channel1RestartHold == 0) &&
            (m_sweepCalculateCountdown == 0) &&
            (m_sweepCalculateReloadTimer == 0) &&
            !m_sweepInstantCalculationDone
        ) {
            return;
        }

        var sweepTicks = ((m_lfDiv == 0)
            ? 1
            : 0);

        if (m_sweepCalculateReloadTimer > sweepTicks) {
            m_sweepCalculateReloadTimer -= sweepTicks;
            sweepTicks = 0;
        } else {
            if (
                (m_sweepCalculateReloadTimer != 0) &&
                (m_sweepCalculateCountdown == 0) &&
                m_sweepInstantCalculationDone
            ) {
                SweepCalculationDone();
            }

            m_sweepInstantCalculationDone = false;
            sweepTicks -= m_sweepCalculateReloadTimer;
            m_sweepCalculateReloadTimer = 0;
        }

        // A zero shift with the unit not explicitly unshifted parks the calculation instead of advancing it.
        if (
            (m_sweepCalculateCountdown != 0) &&
            (((m_registers[Nr10] & SweepShiftMask) != 0) || m_unshiftedSweep)
        ) {
            if (m_sweepCalculateCountdown > sweepTicks) {
                m_sweepCalculateCountdown -= sweepTicks;
            } else {
                m_sweepCalculateCountdown = 0;

                SweepCalculationDone();
            }
        }

        if (m_channel1RestartHold > 0) {
            --m_channel1RestartHold;
        }
    }
    // One audio tick of a square channel's duty counter: count down and, on expiry, reload from the live frequency and
    // step the duty position. The position wraps but is never reset here, so it free-runs across triggers.
    private void RunSquareTick(int channel) {
        if (!m_channelActive[channel]) {
            return;
        }

        if (m_squareDelay[channel] > 0) {
            --m_squareDelay[channel];
        }

        if (m_squareSampleCountdown[channel] > 0) {
            --m_squareSampleCountdown[channel];
            m_squareJustReloaded[channel] = false;

            return;
        }

        m_squareSampleCountdown[channel] = (((m_squareSampleLength[channel] ^ MaxSampleLength) * 2) + 1);
        m_squareSampleIndex[channel] = ((m_squareSampleIndex[channel] + 1) & 0x07);
        m_squareSampleSuppressed[channel] = false;
        m_squareDidTick[channel] = true;
        m_squareJustReloaded[channel] = true;

        UpdateSquareSample(channel: channel);
    }
    // One audio tick of the wave channel's fetcher: on expiry the position advances and the addressed byte is latched
    // into the sample the output plays from.
    private void RunWaveTick() {
        m_waveFormJustRead = false;

        if (!m_channelActive[2]) {
            return;
        }

        if (m_waveSampleCountdown > 0) {
            --m_waveSampleCountdown;

            return;
        }

        m_waveSampleCountdown = (m_waveSampleLength ^ MaxSampleLength);
        m_waveSampleIndex = ((m_waveSampleIndex + 1) & 0x1F);
        m_waveSampleByte = m_waveRam[(m_waveSampleIndex >> 1)];
        m_waveFormJustRead = true;

        UpdateWaveSample();
    }
    // One audio tick of the noise channel's counter. The counter runs whenever the channel is armed OR has been armed
    // since the unit powered on — the background count is what makes a restart land on a deterministic phase — and the
    // LFSR steps on the rising edge of the selected counter bit.
    private void RunNoiseTick() {
        if (
            !m_noiseCounterActive &&
            !m_noiseBackgroundCounterActive
        ) {
            return;
        }

        if (m_noiseCounterCountdown > 1) {
            --m_noiseCounterCountdown;
            m_noiseCountdownReloaded = false;

            return;
        }

        var divisor = ((m_registers[Nr43] & 0x07) << 2);

        if (divisor == 0) {
            divisor = 2;
        }

        // A countdown parked at zero reloads and then spends this tick counting the reload down.
        if (m_noiseCounterCountdown == 0) {
            m_noiseCounterCountdown = (divisor - 1);
            m_noiseCountdownReloaded = false;

            return;
        }

        m_noiseCounterCountdown = divisor;
        m_noiseCountdownReloaded = true;

        StepNoiseCounter();
    }
    // Advance the noise counter one step and, when the selected bit rises, step the LFSR.
    private void StepNoiseCounter() {
        var mask = (1 << (m_registers[Nr43] >> 4));
        var oldBit = ((m_noiseCounter & mask) != 0);

        m_noiseCounter = ((m_noiseCounter + 1) & 0x3FFF);
        m_noiseDidStepCounter = true;

        if (
            ((m_noiseCounter & mask) != 0) &&
            !oldBit &&
            m_channelActive[3]
        ) {
            StepNoiseLfsr();
        }
    }
    // The single DIV-counter bit whose edges drive the frame sequencer, higher under double speed so the event stays
    // at 512 Hz.
    private bool DivApuBit() {
        var bit = (m_key1.IsDoubleSpeed
            ? DoubleSpeedDivApuBit
            : NormalDivApuBit);

        return ((m_timer.DivCounter & (1 << bit)) != 0);
    }
    // The DIV-APU event: advance the 512 Hz divider, count the envelope reloads down, step any envelope whose clock
    // line is high, clock the length counters at 256 Hz, and clock the sweep unit at 128 Hz.
    private void DivEvent() {
        if (!m_powered) {
            return;
        }

        if (m_skipDivEvent == SkipDivEventPending) {
            m_skipDivEvent = SkipDivEventSkipped;

            return;
        }

        if (m_skipDivEvent == SkipDivEventSkipped) {
            m_skipDivEvent = SkipDivEventInactive;
        } else {
            ++m_divDivider;
        }

        if ((m_divDivider & 7) == 7) {
            CountEnvelopeReloadDown(channel: 0);
            CountEnvelopeReloadDown(channel: 1);
            CountEnvelopeReloadDown(channel: 3);
        }

        // The later colour steppings defer the envelope step by one machine cycle under double speed.
        if (
            m_key1.IsDoubleSpeed &&
            m_hasLateColorAudioQuirks
        ) {
            m_pendingEnvelopeDelay = DeferredEnvelopeDelay;
        } else {
            StepPendingEnvelopes();
        }

        if ((m_divDivider & 1) == 1) {
            ClockLengthCounters();
        }

        if ((m_divDivider & 3) == 3) {
            m_squareSweepCountdown = ((m_squareSweepCountdown + 1) & 7);

            TriggerSweepCalculation();
        }
    }
    // The rising edge of the same DIV-APU bit, half a period after the event: an envelope whose reload has landed
    // raises its clock line here, and the next event is what actually moves the volume.
    private void DivSecondaryEvent() {
        if (!m_powered) {
            return;
        }

        ArmEnvelopeClock(
            channel: 0,
            register: Nr12
        );
        ArmEnvelopeClock(
            channel: 1,
            register: Nr22
        );
        ArmEnvelopeClock(
            channel: 3,
            register: Nr42
        );
    }
    private void ArmEnvelopeClock(int channel, int register) {
        if (
            !m_channelActive[channel] ||
            (m_envelopeCountdown[channel] != 0)
        ) {
            return;
        }

        var control = m_registers[register];

        m_envelopeCountdown[channel] = (control & 0x07);

        SetEnvelopeClock(
            channel: channel,
            direction: ((control & 0x08) != 0),
            value: (m_envelopeCountdown[channel] != 0)
        );
    }
    private void CountEnvelopeReloadDown(int channel) {
        if (!m_envelopeClock[channel]) {
            m_envelopeCountdown[channel] = ((m_envelopeCountdown[channel] - 1) & 0x07);
        }
    }
    private void StepPendingEnvelopes() {
        m_pendingEnvelopeDelay = 0;

        if (!m_powered) {
            return;
        }

        StepEnvelope(
            channel: 0,
            register: Nr12
        );
        StepEnvelope(
            channel: 1,
            register: Nr22
        );
        StepEnvelope(
            channel: 3,
            register: Nr42
        );
    }
    // One envelope step: drop the clock line, then — unless the lock latched as it rose — move the volume one unit in
    // the NRx2 direction. A zero pace leaves the volume alone.
    private void StepEnvelope(int channel, int register) {
        if (!m_envelopeClock[channel]) {
            return;
        }

        SetEnvelopeClock(
            channel: channel,
            direction: false,
            value: false
        );

        if (m_envelopeLocked[channel]) {
            return;
        }

        var control = m_registers[register];

        if ((control & 0x07) == 0) {
            return;
        }

        m_envelopeVolume[channel] += (((control & 0x08) != 0)
            ? 1
            : -1);

        if (!m_channelActive[channel]) {
            return;
        }

        if (channel == 3) {
            UpdateNoiseSample();
        } else {
            UpdateSquareSample(channel: channel);
        }
    }
    // Raise or drop an envelope's clock line. Raising it latches whether the volume is already parked at the rail the
    // envelope would move toward; dropping it commits that latch as the lock.
    private void SetEnvelopeClock(int channel, bool value, bool direction) {
        if (m_envelopeClock[channel] == value) {
            return;
        }

        if (value) {
            m_envelopeClock[channel] = true;
            m_envelopeShouldLock[channel] = (((m_envelopeVolume[channel] == 0x0F) && direction) || ((m_envelopeVolume[channel] == 0x00) && !direction));

            return;
        }

        m_envelopeClock[channel] = false;
        m_envelopeLocked[channel] |= m_envelopeShouldLock[channel];
    }
    // One length-counter clock: each channel whose length is enabled and non-zero counts down, silencing the channel
    // as it reaches zero.
    private void ClockLengthCounters() {
        for (var channel = 0; (channel < ChannelCount); ++channel) {
            if (
                !m_lengthEnabled[channel] ||
                (m_pulseLength[channel] == 0)
            ) {
                continue;
            }

            if (--m_pulseLength[channel] == 0) {
                m_channelActive[channel] = false;

                UpdateSample(
                    channel: channel,
                    value: 0
                );
            }
        }
    }
    // The master power switch. Switching the unit on resets every latch; switching it off additionally clears the
    // register file. Monochrome hardware carries the length counters across the switch-on, which is what makes a
    // length written while the unit is off take effect once it comes back; wave RAM is untouched either way.
    private void WriteMasterControl(byte value) {
        var lengths = (stackalloc int[ChannelCount]);
        var powerOn = ((value & MasterPower) != 0);

        for (var channel = 0; (channel < ChannelCount); ++channel) {
            lengths[channel] = m_pulseLength[channel];
        }

        if (
            !m_powered &&
            powerOn
        ) {
            ResetUnit();

            m_powered = true;

            // The unit skips its first DIV-APU event when it is switched on with the bit already high.
            if (DivApuBit()) {
                m_divDivider = 1;
                m_skipDivEvent = SkipDivEventPending;
            }
        } else if (
            m_powered &&
            !powerOn
        ) {
            for (var channel = 0; (channel < ChannelCount); ++channel) {
                UpdateSample(
                    channel: channel,
                    value: 0
                );
            }

            Array.Clear(array: m_registers);
            ResetUnit();

            m_powered = false;
        }

        if (
            m_isColor ||
            !powerOn
        ) {
            return;
        }

        for (var channel = 0; (channel < ChannelCount); ++channel) {
            m_pulseLength[channel] = lengths[channel];
        }
    }
    // The unit's power-on state: every generator, envelope, sweep, and length latch cleared, the wave output muted,
    // and the square counters parked so a channel that has never triggered never steps.
    private void ResetUnit() {
        Array.Clear(array: m_channelActive);
        Array.Clear(array: m_envelopeClock);
        Array.Clear(array: m_envelopeCountdown);
        Array.Clear(array: m_envelopeLocked);
        Array.Clear(array: m_envelopeShouldLock);
        Array.Clear(array: m_envelopeVolume);
        Array.Clear(array: m_lengthEnabled);
        Array.Clear(array: m_pulseLength);
        Array.Clear(array: m_sample);
        Array.Clear(array: m_squareDelay);
        Array.Clear(array: m_squareDidTick);
        Array.Clear(array: m_squareJustReloaded);
        Array.Clear(array: m_squareSampleIndex);
        Array.Clear(array: m_squareSampleLength);
        Array.Clear(array: m_squareSampleSuppressed);

        m_channel1CompletedAddend = 0;
        m_channel1RestartHold = 0;
        m_divDivider = 0;
        m_lfDiv = 1;
        m_noiseAlignment = 0;
        m_noiseBackgroundCounterActive = false;
        m_noiseCounter = 0;
        m_noiseCounterActive = false;
        m_noiseCounterCountdown = 0;
        m_noiseCountdownReloaded = false;
        m_noiseCurrentLfsrSample = false;
        m_noiseDidStepCounter = false;
        m_noiseLfsr = 0;
        m_noiseNarrow = false;
        m_noiseStartedWithDacDisabled = false;
        m_pendingEnvelopeDelay = 0;
        m_shadowSweepSampleLength = 0;
        m_skipDivEvent = SkipDivEventInactive;
        m_squareSampleCountdown[0] = 0xFFFF;
        m_squareSampleCountdown[1] = 0xFFFF;
        m_squareSweepCountdown = 0;
        m_sweepCalculateCountdown = 0;
        m_sweepCalculateReloadTimer = 0;
        m_sweepInstantCalculationDone = false;
        m_sweepLengthAddend = 0;
        m_unshiftedSweep = false;
        m_waveEnable = false;
        m_waveFormJustRead = false;
        m_wavePulsed = false;
        m_waveSampleByte = 0;
        m_waveSampleCountdown = 0;
        m_waveSampleIndex = 0;
        m_waveSampleLength = 0;
        m_waveShift = 4;
    }
    // Wave RAM is reachable regardless of power; while the wave channel plays, access follows the channel rather than
    // the address, and monochrome access outside the tick a fetch lands is dropped.
    private void WriteWaveRam(ushort address, byte value) {
        if (!m_channelActive[2]) {
            m_waveRam[(address - MemoryMap.WaveRamStart)] = value;

            return;
        }

        var (justRead, index, _) = PeekWaveFetch();

        if (
            (!m_isColor && !justRead) ||
            !m_hasPerChannelDacs
        ) {
            return;
        }

        m_waveRam[(index >> 1)] = value;
    }
    // A square channel's duty counter as the CPU's write strobe sees it: one audio tick ahead of the live state,
    // which is where a write lands relative to the read the same counter answers (see ReadStrobeSkew).
    private (int Countdown, bool DidTick, bool JustReloaded) PeekSquare(int channel) {
        if (!m_channelActive[channel]) {
            return (m_squareSampleCountdown[channel], m_squareDidTick[channel], m_squareJustReloaded[channel]);
        }

        if (m_squareSampleCountdown[channel] > 0) {
            return ((m_squareSampleCountdown[channel] - 1), m_squareDidTick[channel], false);
        }

        return ((((m_squareSampleLength[channel] ^ MaxSampleLength) * 2) + 1), true, true);
    }
    // The wave fetcher as the CPU's write strobe sees it: one audio tick ahead of the live state, which is where a
    // write lands relative to the read the same fetcher answers (see ReadStrobeSkew).
    private (bool JustRead, int Index, int Countdown) PeekWaveFetch() {
        if (!m_channelActive[2]) {
            return (false, m_waveSampleIndex, m_waveSampleCountdown);
        }

        if (m_waveSampleCountdown > 0) {
            return (false, m_waveSampleIndex, (m_waveSampleCountdown - 1));
        }

        return (true, ((m_waveSampleIndex + 1) & 0x1F), (m_waveSampleLength ^ MaxSampleLength));
    }
    private void WriteChannelRegister(int offset, byte value) {
        switch (offset) {
            case Nr10:
                WriteSweepControl(value: value);

                return;
            case Nr11:
            case Nr21:
                WriteSquareLength(
                    channel: ((offset == Nr11)
                        ? 0
                        : 1),
                    offset: offset,
                    value: value
                );

                return;
            case Nr12:
            case Nr22:
                WriteSquareEnvelope(
                    channel: ((offset == Nr12)
                        ? 0
                        : 1),
                    offset: offset,
                    value: value
                );

                return;
            case Nr13:
            case Nr23: {
                var channel = ((offset == Nr13)
                    ? 0
                    : 1);

                m_squareSampleLength[channel] = ((m_squareSampleLength[channel] & ~0xFF) | value);

                if (PeekSquare(channel: channel).JustReloaded) {
                    m_squareSampleCountdown[channel] = (((m_squareSampleLength[channel] ^ MaxSampleLength) * 2) + 1);
                }

                break;
            }
            case Nr14:
            case Nr24:
                WriteSquareControl(
                    channel: ((offset == Nr14)
                        ? 0
                        : 1),
                    offset: offset,
                    value: value
                );

                return;
            case Nr30:
                WriteWaveDac(value: value);

                break;
            case Nr31:
                m_pulseLength[2] = (LengthMaxima[2] - value);

                break;
            case Nr32:
                m_waveShift = WaveVolumeShift[((value >> 5) & 0x03)];
                m_registers[offset] = value;

                if (m_channelActive[2]) {
                    UpdateWaveSample();
                }

                return;
            case Nr33:
                m_waveSampleLength = ((m_waveSampleLength & ~0xFF) | value);

                break;
            case Nr34:
                WriteWaveControl(value: value);

                return;
            case Nr41:
                m_pulseLength[3] = (LengthMaxima[3] - (value & LengthDataMask));

                break;
            case Nr42:
                WriteNoiseEnvelope(value: value);

                return;
            case Nr43:
                WriteNoiseFrequency(value: value);

                return;
            case Nr44:
                WriteNoiseControl(value: value);

                return;
        }

        m_registers[offset] = value;
    }
    private void WriteSweepControl(byte value) {
        if (
            (m_sweepCalculateCountdown != 0) ||
            (m_sweepCalculateReloadTimer != 0)
        ) {
            ApplySweepControlWriteGlitch(value: value);
        }

        var oldNegate = (((m_registers[Nr10] & SweepNegate) != 0) || m_hasEarlyAudioStepping);

        m_registers[Nr10] = value;

        // Clearing the negate bit after a calculation has completed with it set disables the channel at once: the
        // completed addend can no longer keep the frequency in range.
        if (
            ((m_shadowSweepSampleLength + m_channel1CompletedAddend + (oldNegate
                ? 1
                : 0)) > MaxSampleLength) &&
            ((value & SweepNegate) == 0)
        ) {
            m_channelActive[0] = false;

            UpdateSample(
                channel: 0,
                value: 0
            );
        }

        TriggerSweepCalculation();
    }
    // A NR10 write that lands while a sweep calculation is in flight perturbs the calculation itself: the early
    // steppings can nudge it a step early or complete it outright, and the later ones re-load the countdown from the
    // value being written. The double-speed data corruption the early steppings show is instance-specific silicon
    // behaviour and is deliberately not modelled.
    private void ApplySweepControlWriteGlitch(byte value) {
        if (!m_hasEarlyAudioStepping) {
            if (m_sweepCalculateReloadTimer == 2) {
                m_sweepCalculateCountdown = (value & SweepShiftMask);

                if (m_sweepCalculateCountdown == 0) {
                    m_sweepCalculateReloadTimer = 0;
                }
            }

            if (
                ((value & SweepShiftMask) != 0) &&
                ((m_registers[Nr10] & SweepShiftMask) == 0) &&
                (m_lfDiv == 0) &&
                (m_sweepCalculateCountdown > 1)
            ) {
                if (--m_sweepCalculateCountdown == 0) {
                    SweepCalculationDone();
                }
            }

            return;
        }

        if (
            (m_sweepCalculateReloadTimer == 1) &&
            (m_lfDiv == 0)
        ) {
            return;
        }

        if (m_sweepCalculateReloadTimer > 1) {
            if (m_key1.IsDoubleSpeed) {
                m_sweepCalculateCountdown = (value & SweepShiftMask);
            }

            return;
        }

        if (m_sweepCalculateCountdown == 0) {
            return;
        }

        var zombieStep = (((m_registers[Nr10] & SweepShiftMask) == 0)
            ? ((m_lfDiv ^ (m_key1.IsDoubleSpeed
                ? 1
                : 0)) != 0)
            : (m_key1.IsDoubleSpeed && (m_sweepCalculateCountdown == 1)));

        if (!zombieStep) {
            return;
        }

        if (--m_sweepCalculateCountdown <= 1) {
            m_sweepCalculateCountdown = 0;

            SweepCalculationDone();
        }
    }
    private void WriteSquareLength(int channel, int offset, byte value) {
        m_pulseLength[channel] = (LengthMaxima[channel] - (value & LengthDataMask));
        m_registers[offset] = (m_powered
            ? value
            : (byte)(value & LengthDataMask));
    }
    private void WriteSquareEnvelope(int channel, int offset, byte value) {
        if ((value & SquareNoiseDacMask) == 0) {
            // The DAC is off: the channel stops sounding at once.
            m_registers[offset] = value;
            m_channelActive[channel] = false;

            UpdateSample(
                channel: channel,
                value: 0
            );

            return;
        }

        if (m_channelActive[channel]) {
            ApplyEnvelopeWriteGlitch(
                channel: channel,
                oldValue: m_registers[offset],
                value: value
            );

            m_registers[offset] = value;

            UpdateSquareSample(channel: channel);

            return;
        }

        m_registers[offset] = value;
    }
    // A square channel's control register (NRx4): the frequency high bits, the trigger, and the length enable.
    private void WriteSquareControl(int channel, int offset, byte value) {
        var wasActive = m_channelActive[channel];
        var previous = m_registers[offset];
        var (countdown, didTick, justReloaded) = PeekSquare(channel: channel);

        // Dropping the frequency's high bits out of the all-ones corner just before the counter reloads leaves the
        // duty position one step behind where the reload would otherwise carry it.
        if (
            ((value & TriggerBit) == 0) &&
            m_channelActive[channel] &&
            ((previous & 0x07) == 7) &&
            ((value & 0x07) != 7) &&
            (m_hasLateColorAudioQuirks || ((countdown & 1) != 0)) &&
            didTick &&
            ((countdown >> 1) == (m_squareSampleLength[channel] ^ MaxSampleLength))
        ) {
            m_squareSampleIndex[channel] = ((m_squareSampleIndex[channel] - 1) & 0x07);
            m_squareSampleSuppressed[channel] = false;
        }

        var oldSampleLength = m_squareSampleLength[channel];

        m_squareSampleLength[channel] = ((m_squareSampleLength[channel] & 0xFF) | ((value & 0x07) << 8));

        if (justReloaded) {
            m_squareSampleCountdown[channel] = (((m_squareSampleLength[channel] ^ MaxSampleLength) * 2) + 1);
        }

        if ((value & TriggerBit) != 0) {
            TriggerSquare(
                channel: channel,
                countdown: countdown,
                justReloaded: justReloaded,
                oldSampleLength: oldSampleLength,
                value: value,
                wasActive: wasActive
            );
        }

        ApplyLengthEnableGlitch(
            channel: channel,
            reload: (LengthMaxima[channel] - 1),
            value: value
        );

        m_lengthEnabled[channel] = ((value & LengthEnableBit) != 0);
        m_registers[offset] = value;
    }
    private void TriggerSquare(int channel, int countdown, bool justReloaded, int oldSampleLength, byte value, bool wasActive) {
        var control = m_registers[((channel == 0)
            ? Nr12
            : Nr22)];

        m_envelopeClock[channel] = false;
        m_envelopeLocked[channel] = false;
        m_squareDidTick[channel] = false;

        var forceUnsuppressed = false;

        if (!m_channelActive[channel]) {
            // The later colour steppings carry the duty position one step forward when a silent channel restarts on a
            // frequency whose counter has not wrapped.
            if (
                m_hasLateColorAudioQuirks &&
                ((value & 0x04) == 0) &&
                ((((countdown - m_squareDelay[channel]) / 2) & 0x400) == 0)
            ) {
                m_squareSampleIndex[channel] = ((m_squareSampleIndex[channel] + 1) & 0x07);
                forceUnsuppressed = true;
            }

            m_squareDelay[channel] = (6 + (m_lfDiv * ((m_hasEarlyAudioStepping && m_key1.IsDoubleSpeed)
                ? 1
                : -1)));
        } else {
            var extraDelay = 0;

            if (m_hasLateColorAudioQuirks) {
                if (
                    !justReloaded &&
                    ((value & 0x04) == 0) &&
                    (((((countdown - 1) - m_squareDelay[channel]) / 2) & 0x400) == 0)
                ) {
                    m_squareSampleIndex[channel] = ((m_squareSampleIndex[channel] + 1) & 0x07);
                    m_squareSampleSuppressed[channel] = false;
                } else if (
                    (m_squareSampleLength[channel] == MaxSampleLength) &&
                    (oldSampleLength != MaxSampleLength) &&
                    m_squareSampleSuppressed[channel]
                ) {
                    extraDelay += 2;
                }
            }

            // A channel that is already sounding starts its first step one audio tick earlier.
            m_squareDelay[channel] = ((4 - m_lfDiv) + extraDelay);
        }

        m_squareSampleCountdown[channel] = ((((m_squareSampleLength[channel] ^ MaxSampleLength) * 2) + m_squareDelay[channel]) + TriggerReadStrobeSkew());
        m_envelopeVolume[channel] = (control >> 4);

        // The volume the trigger loads takes effect at once, even though the waveform itself does not.
        if (m_channelActive[channel]) {
            UpdateSquareSample(channel: channel);
        }

        m_envelopeCountdown[channel] = (control & 0x07);

        if (
            ((control & SquareNoiseDacMask) != 0) &&
            !m_channelActive[channel]
        ) {
            m_channelActive[channel] = true;
            m_squareSampleSuppressed[channel] = !forceUnsuppressed;

            UpdateSample(
                channel: channel,
                value: 0
            );
        }

        if (m_pulseLength[channel] == 0) {
            m_pulseLength[channel] = LengthMaxima[channel];
            m_lengthEnabled[channel] = false;
        }

        if (channel == 0) {
            TriggerSweep(wasActive: wasActive);
        }
    }
    private void TriggerSweep(bool wasActive) {
        m_channel1CompletedAddend = 0;
        m_shadowSweepSampleLength = 0;
        m_sweepInstantCalculationDone = false;

        var control = m_registers[Nr10];

        if ((control & SweepShiftMask) != 0) {
            // A non-zero shift makes the trigger itself run an overflow check, after the unit's arming delay.
            m_sweepCalculateCountdown = (control & SweepShiftMask);
            m_sweepCalculateReloadTimer = ((((m_lfDiv ^ (m_key1.IsDoubleSpeed
                ? 1
                : 0)) != 0) && m_hasEarlyAudioStepping
                ? 3
                : 2) + (m_hasEarlyAudioStepping
                ? SweepTriggerArmSkewEarly
                : SweepTriggerArmSkewLate));
            m_unshiftedSweep = false;

            if (!wasActive) {
                ++m_sweepCalculateReloadTimer;
            }

            m_sweepLengthAddend = (m_squareSampleLength[0] >> (control & SweepShiftMask));
        } else {
            m_sweepLengthAddend = 0;
        }

        m_channel1RestartHold = (((2 + RestartHoldSkew) - m_lfDiv) + ((m_isColor && !m_hasShortSweepRestartHold)
            ? 2
            : 0));
        m_squareSweepCountdown = (((control >> 4) & 7) ^ 7);
    }
    // The 128 Hz sweep clock, and the same arming a NR10 write performs: when the countdown has run out, fold the
    // pending addend into the live frequency and arm the next calculation.
    private void TriggerSweepCalculation() {
        var control = m_registers[Nr10];

        if (
            ((control & 0x70) == 0) ||
            (m_squareSweepCountdown != 7)
        ) {
            return;
        }

        if ((control & SweepShiftMask) != 0) {
            m_squareSampleLength[0] = ((m_sweepLengthAddend + m_shadowSweepSampleLength + (((control & SweepNegate) != 0)
                ? 1
                : 0)) & MaxSampleLength);
        }

        if (m_channel1RestartHold == 0) {
            m_sweepLengthAddend = (m_squareSampleLength[0] >> (control & SweepShiftMask));
        }

        // The recalculation and its overflow check only run after a delay.
        m_sweepCalculateCountdown = (control & SweepShiftMask);
        m_sweepCalculateReloadTimer = ((1 + SweepClockArmSkew) + m_lfDiv);
        m_unshiftedSweep = ((control & SweepShiftMask) == 0);
        m_squareSweepCountdown = (((control >> 4) & 7) ^ 7);

        if (m_sweepCalculateCountdown == 0) {
            m_sweepInstantCalculationDone = true;
        }
    }
    // The overflow check the sweep unit runs once its calculation delay elapses. The check sees the addend added a
    // second time, which is why an overflow can disable the channel a step before the frequency itself would.
    private void SweepCalculationDone() {
        if (m_channel1RestartHold == 0) {
            m_shadowSweepSampleLength = m_squareSampleLength[0];
        }

        if ((m_registers[Nr10] & SweepNegate) != 0) {
            m_sweepLengthAddend ^= MaxSampleLength;
        }

        if (
            ((m_shadowSweepSampleLength + m_sweepLengthAddend) > MaxSampleLength) &&
            ((m_registers[Nr10] & SweepNegate) == 0)
        ) {
            m_channelActive[0] = false;

            UpdateSample(
                channel: 0,
                value: 0
            );
        }

        m_channel1CompletedAddend = m_sweepLengthAddend;
    }
    private void WriteWaveDac(byte value) {
        m_waveEnable = ((value & WaveDacEnable) != 0);
        m_registers[Nr30] = value;

        if (m_waveEnable) {
            return;
        }

        m_wavePulsed = false;

        if (
            m_channelActive[2] &&
            PeekWaveFetch().JustRead &&
            m_hasEarlyAudioStepping
        ) {
            m_waveSampleByte = m_waveRam[(Nr30 & 0x0F)];
        }

        m_channelActive[2] = false;

        UpdateSample(
            channel: 2,
            value: 0
        );
    }
    private void WriteWaveControl(byte value) {
        m_waveSampleLength = ((m_waveSampleLength & 0xFF) | ((value & 0x07) << 8));

        if ((value & TriggerBit) != 0) {
            var (_, peekIndex, peekCountdown) = PeekWaveFetch();

            m_wavePulsed = true;

            // Retriggering the playing channel on the tick its fetch lands corrupts the head of wave RAM on
            // monochrome hardware, where the trigger and the fetch collide on the RAM port; colour hardware buffers
            // the port and is immune.
            if (
                !m_isColor &&
                m_channelActive[2] &&
                (peekCountdown == 0)
            ) {
                CorruptWaveRamOnRetrigger(index: peekIndex);
            }

            m_waveSampleIndex = 0;

            if (
                m_channelActive[2] &&
                (peekCountdown == 0)
            ) {
                m_waveSampleByte = m_waveRam[0];
            }

            if (m_waveEnable) {
                m_channelActive[2] = true;

                UpdateSample(
                    channel: 2,
                    value: ((m_waveSampleByte >> 4) >> m_waveShift)
                );
            }

            // The first fetch lands one full period plus the trigger delay after the trigger; until then the channel
            // keeps playing the latch it already holds.
            m_waveSampleCountdown = ((m_waveSampleLength ^ MaxSampleLength) + WaveTriggerFetchDelay);

            if (m_pulseLength[2] == 0) {
                m_pulseLength[2] = LengthMaxima[2];
                m_lengthEnabled[2] = false;
            }
        }

        ApplyLengthEnableGlitch(
            channel: 2,
            reload: (LengthMaxima[2] - 1),
            value: value
        );

        m_lengthEnabled[2] = ((value & LengthEnableBit) != 0);
        m_registers[Nr34] = value;
    }
    private void WriteNoiseEnvelope(byte value) {
        if ((value & SquareNoiseDacMask) == 0) {
            if (
                m_channelActive[3] &&
                ((m_registers[Nr43] & 0x07) != 0)
            ) {
                if (m_noiseCounterCountdown <= 2) {
                    m_noiseCounter = ((m_noiseCounter + 1) & 0x3FFF);
                }

                m_noiseBackgroundCounterActive = false;
            }

            m_registers[Nr42] = value;
            m_channelActive[3] = false;
            m_noiseCounterActive = false;

            UpdateSample(
                channel: 3,
                value: 0
            );

            return;
        }

        if (m_channelActive[3]) {
            ApplyEnvelopeWriteGlitch(
                channel: 3,
                oldValue: m_registers[Nr42],
                value: value
            );

            m_registers[Nr42] = value;

            UpdateNoiseSample();

            return;
        }

        m_registers[Nr42] = value;
    }
    private void WriteNoiseFrequency(byte value) {
        // A write that lands on the tick the counter reloads re-phases the reload against the 1 MiHz alignment.
        if (m_noiseCountdownReloaded) {
            var divisor = ((value & 0x07) << 2);

            if (divisor == 0) {
                divisor = 2;
                m_noiseCounterCountdown = divisor;
            } else {
                var phase = (m_hasEarlyAudioStepping
                    ? EarlyNoiseReloadPhase[(m_noiseAlignment & 3)]
                    : NoiseReloadPhase[(m_noiseAlignment & 3)]);

                m_noiseCounterCountdown = (divisor + phase);
            }
        }

        m_noiseNarrow = ((value & 0x08) != 0);
        m_registers[Nr43] = value;
    }
    private void WriteNoiseControl(byte value) {
        if ((value & TriggerBit) != 0) {
            m_envelopeClock[3] = false;
            m_envelopeLocked[3] = false;
            m_noiseLfsr = 0;

            PrepareNoiseStart();

            m_envelopeVolume[3] = (m_registers[Nr42] >> 4);
            m_noiseCurrentLfsrSample = false;
            m_envelopeCountdown[3] = (m_registers[Nr42] & 0x07);
            m_noiseDidStepCounter = ((m_noiseAlignment & 3) == 2);

            if ((m_registers[Nr42] & SquareNoiseDacMask) != 0) {
                m_channelActive[3] = true;

                UpdateSample(
                    channel: 3,
                    value: 0
                );
            }

            if (m_pulseLength[3] == 0) {
                m_pulseLength[3] = LengthMaxima[3];
                m_lengthEnabled[3] = false;
            }
        }

        ApplyLengthEnableGlitch(
            channel: 3,
            reload: (LengthMaxima[3] - 1),
            value: value
        );

        m_lengthEnabled[3] = ((value & LengthEnableBit) != 0);
        m_registers[Nr44] = value;
    }
    // The noise counter's restart phasing: the counter keeps running in the background once the channel has been
    // armed, so a restart lands the reload on a phase derived from the 1 MiHz alignment rather than at zero.
    private void PrepareNoiseStart() {
        m_noiseCounterActive = ((m_registers[Nr42] & SquareNoiseDacMask) != 0);

        var wasStartedWithDacDisabled = m_noiseStartedWithDacDisabled;
        var wasBackgroundCounting = m_noiseBackgroundCounterActive;
        var divisor = (m_registers[Nr43] & 0x07);
        var instantStep = false;

        m_noiseStartedWithDacDisabled = !m_noiseCounterActive;
        m_noiseBackgroundCounterActive = true;

        if (
            (divisor > 1) &&
            (m_noiseCounterCountdown == 1)
        ) {
            m_noiseCounter = ((m_noiseCounter + 1) & 0x3FFF);
        } else if (
            (m_noiseCounterCountdown == 2) &&
            ((m_noiseAlignment & 3) == 0) &&
            m_channelActive[3]
        ) {
            if (divisor == 0) {
                divisor = 8;
            } else if (divisor == 1) {
                var mask = (1 << (m_registers[Nr43] >> 4));
                var oldBit = ((m_noiseCounter & mask) != 0);

                m_noiseCounter = ((m_noiseCounter + 1) & 0x3FFF);
                instantStep = (((m_noiseCounter & mask) != 0) && !oldBit);
            }
        }

        m_noiseCounterCountdown = ((divisor == 0)
            ? 6
            : ((divisor * 4) + 6));

        if ((m_noiseAlignment & 1) != 0) {
            if (divisor == 0) {
                m_noiseCounterCountdown += ((!m_hasEarlyAudioStepping && wasBackgroundCounting)
                    ? -1
                    : 1);
            } else if ((m_noiseAlignment & 2) != 0) {
                m_noiseCounterCountdown += (((divisor == 1) && !m_channelActive[3])
                    ? 1
                    : -3);
            } else {
                --m_noiseCounterCountdown;

                if (
                    (divisor == 1) &&
                    m_channelActive[3]
                ) {
                    m_noiseCounterCountdown -= 4;
                }
            }
        } else if (divisor != 0) {
            if ((m_noiseAlignment & 2) != 0) {
                m_noiseCounterCountdown -= 2;
            } else if (divisor > 1) {
                m_noiseCounterCountdown -= 4;
            } else if (
                m_channelActive[3] &&
                ((m_registers[Nr43] & 0xF0) == 0)
            ) {
                m_noiseCounterCountdown -= 4;
            }
        }

        // The background count itself shifts the phase when the channel restarts from silence.
        if (divisor > 1) {
            if (
                !m_noiseCounterActive &&
                ((m_noiseAlignment & 3) == 0)
            ) {
                m_noiseCounterCountdown += 4;
            }
        } else if (
            wasBackgroundCounting &&
            !m_channelActive[3] &&
            ((m_noiseAlignment & 3) == 0)
        ) {
            if (divisor == 0) {
                if (wasStartedWithDacDisabled) {
                    m_noiseCounterCountdown += 28;
                }
            } else {
                m_noiseCounterCountdown -= 4;
            }
        }

        m_noiseCounterCountdown += TriggerReadStrobeSkew();
        m_noiseLfsr = (((divisor == 0) && m_channelActive[3] && ((m_noiseAlignment & 3) == 3))
            ? 0x0055
            : 0);

        if (instantStep) {
            StepNoiseLfsr();
        }
    }
    // Enabling a length counter while the divider's low bit is set clocks it once on the spot; a trigger in the same
    // write keeps the channel alive by reloading the counter one short of its maximum instead.
    private void ApplyLengthEnableGlitch(int channel, int reload, byte value) {
        if (
            (((value & LengthEnableBit) == 0) && !(m_isColor && m_hasEarlyAudioStepping)) ||
            m_lengthEnabled[channel] ||
            ((m_divDivider & 1) == 0) ||
            (m_pulseLength[channel] == 0)
        ) {
            return;
        }

        if (--m_pulseLength[channel] != 0) {
            return;
        }

        if ((value & TriggerBit) != 0) {
            m_pulseLength[channel] = reload;

            return;
        }

        m_channelActive[channel] = false;

        UpdateSample(
            channel: channel,
            value: 0
        );
    }
    // A NRx2 write reaching a sounding channel does not simply load a new volume: the envelope's counter and its
    // direction line are wired so the write can step, invert, or freeze the volume outright. The early steppings pass
    // every write through an all-ones intermediate, which is why they land on a different volume than the later ones.
    private void ApplyEnvelopeWriteGlitch(int channel, byte oldValue, byte value) {
        if (m_hasEarlyAudioStepping) {
            ApplyEnvelopeWriteStep(
                channel: channel,
                oldValue: oldValue,
                value: 0xFF
            );
            ApplyEnvelopeWriteStep(
                channel: channel,
                oldValue: 0xFF,
                value: value
            );

            return;
        }

        ApplyEnvelopeWriteStep(
            channel: channel,
            oldValue: oldValue,
            value: value
        );
    }
    private void ApplyEnvelopeWriteStep(int channel, byte oldValue, byte value) {
        if (m_envelopeClock[channel]) {
            m_envelopeCountdown[channel] = (value & 0x07);
        }

        var shouldInvert = (((value & 0x08) ^ (oldValue & 0x08)) != 0);
        var shouldStep = (((value & 0x07) != 0) && ((oldValue & 0x07) == 0) && !m_envelopeLocked[channel]);

        if (
            ((value & 0x0F) == 0x08) &&
            ((oldValue & 0x0F) == 0x08) &&
            !m_envelopeLocked[channel]
        ) {
            shouldStep = true;
        }

        if (shouldInvert) {
            if ((value & 0x08) != 0) {
                m_envelopeVolume[channel] = (((((oldValue & 0x07) == 0) && !m_envelopeLocked[channel])
                    ? (m_envelopeVolume[channel] ^ 0x0F)
                    : (0x0E - m_envelopeVolume[channel])) & 0x0F);
                shouldStep = false;
            } else {
                m_envelopeVolume[channel] = ((0x10 - m_envelopeVolume[channel]) & 0x0F);
            }
        }

        if (shouldStep) {
            m_envelopeVolume[channel] = ((m_envelopeVolume[channel] + (((value & 0x08) != 0)
                ? 1
                : -1)) & 0x0F);

            return;
        }

        if (
            ((value & 0x07) == 0) &&
            m_envelopeClock[channel]
        ) {
            SetEnvelopeClock(
                channel: channel,
                direction: false,
                value: false
            );
        }
    }
    // Advance the noise LFSR one step: feed back the XNOR of its low two bits into bit 14 (and bit 6 in the 7-bit
    // width selected by NR43 bit 3), so the low bit that gates the output follows the pseudo-random sequence. The
    // clear on the false branch is what makes a width switch mid-sequence observable.
    private void StepNoiseLfsr() {
        var feedback = ((m_noiseLfsr ^ (m_noiseLfsr >> 1) ^ 1) & 1);
        var mask = (m_noiseNarrow
            ? 0x4040
            : 0x4000);

        m_noiseLfsr >>= 1;

        if (feedback != 0) {
            m_noiseLfsr |= mask;
        } else {
            m_noiseLfsr &= ~mask;
        }

        m_noiseCurrentLfsrSample = ((m_noiseLfsr & 1) != 0);

        UpdateNoiseSample();
    }
    // Publish a channel's digital level. A level a DAC-off channel would publish is dropped, so the channel holds the
    // level it had; publishing zero over an already-zero level is a no-op.
    private void UpdateSample(int channel, int value) {
        if (
            (value == 0) &&
            (m_sample[channel] == 0)
        ) {
            return;
        }

        if (!IsDacEnabled(channel: channel)) {
            return;
        }

        m_sample[channel] = value;
    }
    private void UpdateSquareSample(int channel) {
        // A freshly triggered channel publishes nothing until its first duty step.
        if (m_squareSampleSuppressed[channel]) {
            return;
        }

        var duty = (m_registers[((channel == 0)
            ? Nr11
            : Nr21)] >> 6);

        UpdateSample(
            channel: channel,
            value: ((DutyTable[((duty * 8) + m_squareSampleIndex[channel])] != 0)
                ? m_envelopeVolume[channel]
                : 0)
        );
    }
    private void UpdateWaveSample() =>
        UpdateSample(
            channel: 2,
            value: (((((m_waveSampleIndex & 1) != 0)
                ? (m_waveSampleByte & 0x0F)
                : (m_waveSampleByte >> 4))) >> m_waveShift)
        );
    private void UpdateNoiseSample() {
        if (!m_channelActive[3]) {
            return;
        }

        UpdateSample(
            channel: 3,
            value: (m_noiseCurrentLfsrSample
                ? m_envelopeVolume[3]
                : 0)
        );
    }
    // The monochrome retrigger collision: the byte the channel was about to fetch bleeds into the head of wave RAM —
    // a byte from the first four-byte row copies alone into byte 0, one from a later row drags its whole aligned
    // four-byte row over bytes 0-3.
    private void CorruptWaveRamOnRetrigger(int index) {
        var head = (((index + 1) >> 1) & 0x0F);

        if (head < 4) {
            m_waveRam[0] = m_waveRam[head];

            return;
        }

        var row = (head & 0x0C);

        for (var offset = 0; (offset < 4); ++offset) {
            m_waveRam[offset] = m_waveRam[(row + offset)];
        }
    }
    // The read-strobe skew a square or noise trigger loads on top of its own delay, in audio ticks. Where the skew
    // is observable follows from how a machine cycle divides into audio ticks: at normal speed a machine cycle
    // spans two of them, so the read strobe stays inside the same 1 MiHz half the duty counter and the noise
    // counter are quoted against and the skew moves no edge a reader can see; under double speed a machine cycle
    // is exactly one audio tick, so the same skew carries the first edge a whole step past where the reader
    // expects it and the trigger has to load one tick longer to put it back. The wave fetcher and the sweep unit
    // are quoted against the 2 MiHz clock directly, so they carry the skew at both speeds.
    private int TriggerReadStrobeSkew() =>
        (m_key1.IsDoubleSpeed
        ? ReadStrobeSkew
        : 0);
    private int PackedSample(int channel) =>
        (m_channelActive[channel]
        ? (m_sample[channel] & 0x0F)
        : 0);
    private bool IsDacEnabled(int channel) {
        if (!m_hasPerChannelDacs) {
            return true;
        }

        return channel switch {
            0 => ((m_registers[Nr12] & SquareNoiseDacMask) != 0),
            1 => ((m_registers[Nr22] & SquareNoiseDacMask) != 0),
            2 => m_waveEnable,
            _ => ((m_registers[Nr42] & SquareNoiseDacMask) != 0),
        };
    }
}

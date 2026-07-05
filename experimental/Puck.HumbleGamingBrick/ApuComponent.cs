using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The audio processing unit. It owns the sound register file (NR10–NR52) and wave RAM, the master power switch, and
/// the four channels' length counters. The unit straddles the console's two clocks, so it is driven through two seams:
/// its own CPU-domain <see cref="Tick"/> follows the DIV-APU event — a falling edge of one bit of the timer's DIV
/// counter (bit 12, or bit 13 under Color double speed) — which steps a 512 Hz eight-step frame sequencer that clocks
/// the length counters (steps 0/2/4/6), the sweep unit (2/6), and the envelopes (7); the channel generators (duty
/// positions, the wave sample fetcher, and the noise LFSR) run on the fixed 4 MiHz dot clock and are advanced once per
/// dot by <see cref="ApuGeneratorClock"/>, so engaging Color double speed does not raise the audio pitch. Reading DIV
/// rather than a private divider is what makes resetting DIV perturb the sequencer exactly as on hardware.
/// Powering the unit off through NR52 clears the register file and silences every channel; wave RAM stays accessible.
/// All state is plain fields captured in a fixed order, so the APU snapshots and forks like every other component.
/// </summary>
public sealed class ApuComponent : IApu, IClockedComponent, ISnapshotable, IModeSwitchable {
    private const int ChannelCount = 4;
    private const int DoubleSpeedDivApuBit = 13;
    private const byte LengthDataMask = 0x3F;
    private const byte LengthEnableBit = 0x40;
    private const byte MasterPower = 0x80;
    private const int MaxFrequency = 2047;
    // NR43 clock-shift codes 14 and 15 gate the LFSR's clock line entirely (the hardware-accurate references agree; a
    // naive implementation omits the gate): the noise timer freezes rather than counting toward a step that hardware never delivers.
    private const int NoiseShiftGateCode = 14;
    private const int NormalDivApuBit = 12;
    private const byte Nr52Readable = 0x70;
    private const int RegisterCount = 0x17;
    private const byte SquareNoiseDacMask = 0xF8;
    private const byte SweepNegate = 0x08;
    private const int SweepReloadPeriod = 8;
    private const byte SweepShiftMask = 0x07;
    private const byte TriggerBit = 0x80;
    private const byte WaveDacEnable = 0x80;
    // How many dots after a wave fetch the CPU's monochrome wave-RAM access window stays open (Color access always
    // succeeds); the same window is the retrigger-corruption "fetch busy" predicate (see Trigger). Swept as 1/2/3
    // against the hardware-accurate wave-RAM verdicts on the DMG machine: 2 is the unique value passing BOTH the
    // "wave read while on" and "wave trigger while on" cases (1 starves the read window, 3 widens the corruption window one slot too far).
    private const int WaveFetchWindowDots = 2;
    private const int WaveRamSize = 16;
    // Dots between a trigger and the wave channel's FIRST sample fetch, added on top of the freshly loaded period.
    // The cross-reference disagreement (+6 vs +4) was swept as 0/2/4/6/8/10 against the hardware-accurate colour-brick
    // "wave read while on" + "wave" cases and the wave-channel ff30 hardware verdicts: 8 is the UNIQUE
    // value that passes "wave read while on" (every other value shifts the read-phase table by one slot), and "wave" passes at any value.
    // In our countdown convention (a reload of N fires N dots later) 8 IS the +6 convention (its countdown
    // fires one 2-dot APU step after reaching zero); the +4 convention loses outright.
    private const int WaveTriggerFetchDelayDots = 8;

    // NR10 sweep, NR11 duty/length, NR12 envelope, NR13 frequency low, NR14 trigger/control.
    private const int Nr10 = 0x00;
    private const int Nr11 = 0x01;
    private const int Nr12 = 0x02;
    private const int Nr13 = 0x03;
    private const int Nr14 = 0x04;
    private const int Nr50 = 0x14;
    private const int Nr51 = 0x15;
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

    private readonly bool[] m_channelEnabled = new bool[ChannelCount];
    // The envelope volume (0-15) and its reload countdown, per channel (the two squares and noise; the wave channel has
    // no envelope). The volume scales the channel's gated waveform into its digital output.
    private readonly int[] m_envelopeTimer = new int[ChannelCount];
    private readonly int[] m_envelopeVolume = new int[ChannelCount];
    // Whether the machine is Color hardware (the machine model, not the cartridge's compatibility mode): Color silicon
    // buffers the wave-RAM port, so CPU access while the channel plays always succeeds; monochrome access must hit the
    // window right after a fetch.
    // Mutable so a LIVE device swap re-gates the warm-path color/mono APU rules (wave-RAM access window, powered-off
    // length writes, power-off length clearing, retrigger corruption). The boot-beep frame-sequencer phase and wave-RAM
    // power-on pattern stay construction-only.
    private bool m_isColor;
    private readonly IKey1 m_key1;
    private readonly int[] m_lengthCounter = new int[ChannelCount];
    private readonly bool[] m_lengthEnabled = new bool[ChannelCount];
    private readonly byte[] m_registers = new byte[RegisterCount];
    // The square channels' duty-cycle position (0-7) and frequency-timer countdown, indexed 0 = channel 1, 1 = channel 2.
    private readonly int[] m_squarePosition = new int[2];
    private readonly int[] m_squareTimer = new int[2];
    private readonly ITimer m_timer;
    private readonly byte[] m_waveRam = new byte[WaveRamSize];

    private int m_frameSequencerStep;
    private bool m_lastDivApuBit;
    private int m_noiseLfsr;
    private int m_noiseTimer;
    private bool m_powered;
    private bool m_sweepEnabled;
    private bool m_sweepNegateUsed;
    private int m_sweepShadow;
    private int m_sweepTimer;
    // The wave channel's fetch-follower state: the dots remaining in the monochrome access window that a fetch opens,
    // and the last-fetched byte itself — the latch the channel's digital output is read from (NOT a live wave-RAM read;
    // a CPU write while playing lands in RAM but is not heard until the next fetch). CPU access while playing lands on
    // the byte at the LIVE sample position (a trigger resets it to zero, so pre-first-fetch access hits byte 0 —
    // confirmed by cgb_sound 09, whose pre-fetch reads return 0x00, never the stale fetch's byte).
    private int m_waveFetchHold;
    private int m_wavePosition;
    private byte m_waveSampleLatch;
    private int m_waveTimer;

    /// <summary>Creates the APU wired to the timer whose DIV counter clocks its frame sequencer and the speed unit that
    /// selects which DIV bit does so. Without a boot ROM it is seeded powered on as the post-boot machine leaves it: the
    /// boot ROM's start-up beep leaves channel 1 still sounding (its envelope already decayed to zero) with the beep's
    /// frequency in NR13/NR14, full master volume routed to both terminals, and — on Color — the alternating wave-RAM
    /// pattern. With one it powers on silent (the wave-RAM pattern is the hardware's, so it stays).</summary>
    /// <param name="timer">The divider/timer block, read for the DIV-APU event.</param>
    /// <param name="key1">The Color speed-switch unit, read for the current speed.</param>
    /// <param name="configuration">The machine configuration, which selects the wave RAM's power-on pattern.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public ApuComponent(ITimer timer, IKey1 key1, MachineConfiguration configuration) {
        ArgumentNullException.ThrowIfNull(argument: timer);
        ArgumentNullException.ThrowIfNull(argument: key1);
        ArgumentNullException.ThrowIfNull(argument: configuration);

        m_isColor = configuration.Model.SupportsColor();
        m_key1 = key1;
        m_timer = timer;

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

            m_channelEnabled[0] = true;
            m_lengthCounter[0] = LengthMaxima[0];
            m_envelopeTimer[0] = 1;
            m_squarePosition[0] = 2;
            m_squareTimer[0] = 2041;
            // The boot ROM leaves the frame sequencer mid-cycle, not at step zero: one DIV-APU event has elapsed on Color
            // (eighteen on monochrome, whose step counter is that modulo eight). The hardware-accurate sound tests align themselves
            // to this phase through length-counter syncs, so the seed is load-bearing.
            m_frameSequencerStep = (configuration.Model.SupportsColor() ? 1 : 2);
        }

        m_lastDivApuBit = DivApuBit();

        // The alternating wave-RAM pattern is the Color hardware's power-on characteristic (the boot ROM never writes
        // wave RAM), so it is seeded on both paths.
        if (configuration.Model.SupportsColor()) {
            for (var offset = 0; (offset < WaveRamSize); offset += 2) {
                m_waveRam[offset + 1] = 0xFF;
            }
        }
    }

    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;

    /// <inheritdoc/>
    public void Tick() {
        // Stop mode freezes the unit along with the rest of the CPU-domain peripherals.
        if (m_key1.IsStopped) {
            return;
        }

        // The DIV-APU bit is followed every T-cycle even while powered off, so the frame sequencer's phase relative to
        // DIV survives a power cycle; only the stepping (and thus length/sweep/envelope clocking) is gated on power.
        var bit = DivApuBit();

        if (m_powered && m_lastDivApuBit && !bit) {
            StepFrameSequencer();
        }

        m_lastDivApuBit = bit;
    }
    /// <summary>Advances the channel generators by one dot of the fixed 4 MiHz LCD/audio clock — the frequency timers
    /// do not follow the CPU clock, so Color double speed leaves the audio pitch unchanged. Called once per whole dot
    /// by <see cref="ApuGeneratorClock"/>, the divider that derives the generator edge from the master clock's
    /// sub-dot phase.</summary>
    public void TickGenerators() {
        // Stop mode halts the oscillator for the whole SoC, generators included.
        if (m_key1.IsStopped) {
            return;
        }

        // The monochrome CPU access window a wave fetch opens drains one dot at a time.
        if (m_waveFetchHold > 0) {
            --m_waveFetchHold;
        }

        // The wave channel's frequency timer runs every dot while the channel plays, stepping through its 32 samples;
        // each expiry advances the position and FETCHES the addressed byte into the sample latch the output plays from.
        if (m_channelEnabled[2] && (--m_waveTimer <= 0)) {
            m_waveTimer = WavePeriod();
            m_wavePosition = ((m_wavePosition + 1) & 0x1F);
            m_waveSampleLatch = m_waveRam[m_wavePosition >> 1];
            m_waveFetchHold = WaveFetchWindowDots;
        }

        // The two square channels each advance their eight-step duty position when their frequency timer expires, and the
        // noise channel steps its LFSR the same way — all per dot while the channel plays. NR43 shift codes 14 and 15
        // gate the LFSR clock line entirely, freezing the countdown until a usable code is written back.
        AdvanceSquareTimer(channel: 0);
        AdvanceSquareTimer(channel: 1);

        if (m_channelEnabled[3] && ((m_registers[Nr43] >> 4) < NoiseShiftGateCode) && (--m_noiseTimer <= 0)) {
            m_noiseTimer = NoisePeriod();
            StepNoiseLfsr();
        }
    }
    /// <inheritdoc/>
    public byte ReadRegister(ushort address) {
        if (address >= MemoryMap.WaveRamStart) {
            if (!m_channelEnabled[2]) {
                return m_waveRam[address - MemoryMap.WaveRamStart];
            }

            // While the channel plays, CPU access follows the channel, not the address: it lands on the byte at the
            // live sample position. Color silicon buffers the port so the access always succeeds; monochrome access
            // succeeds only within the short window a fetch opens, and reads outside it float to 0xFF.
            return (m_isColor || (m_waveFetchHold > 0)) ? m_waveRam[m_wavePosition >> 1] : (byte)0xFF;
        }

        if (address == MemoryMap.AudioMasterControl) {
            var status = 0;

            for (var channel = 0; (channel < ChannelCount); ++channel) {
                if (m_channelEnabled[channel]) {
                    status |= (1 << channel);
                }
            }

            return (byte)(Nr52Readable | (m_powered ? MasterPower : 0) | status);
        }

        if (address > MemoryMap.AudioEnd) {
            return 0xFF; // FF27–FF2F are unused.
        }

        var offset = (address - MemoryMap.AudioStart);

        return (byte)(m_registers[offset] | ReadMasks[offset]);
    }
    /// <inheritdoc/>
    public void WriteRegister(ushort address, byte value) {
        // Wave RAM is reachable regardless of power; while the wave channel plays, access follows the channel (see
        // ReadRegister): a Color write lands on the byte at the live sample position — updating RAM but not the sample
        // latch, so it is not heard until that byte is fetched again — and a monochrome write outside the post-fetch
        // window is dropped.
        if (address >= MemoryMap.WaveRamStart) {
            if (!m_channelEnabled[2]) {
                m_waveRam[address - MemoryMap.WaveRamStart] = value;
            }
            else if (m_isColor || (m_waveFetchHold > 0)) {
                m_waveRam[m_wavePosition >> 1] = value;
            }

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

        // While powered off, Color ignores every write; monochrome hardware still lets the length-load registers (NRx1)
        // write their length counters — only the length, not the duty or anything else. This is what the hardware-accurate
        // "length counter during power" case checks.
        if (!m_powered) {
            if (!m_isColor) {
                WriteLengthCounterWhilePoweredOff(offset: offset, value: value);
            }

            return;
        }

        WriteChannelRegister(offset: offset, value: value);
    }
    /// <inheritdoc/>
    public byte ReadPcm(ushort address) {
        // PCM12 packs channel 1 in the low nibble and channel 2 in the high; PCM34 packs channel 3 (wave) low and
        // channel 4 (noise) high. Each nibble is the channel's live digital output, or zero while it is not sounding.
        if (address == MemoryMap.PcmAmplitude12) {
            return (byte)((m_channelEnabled[0] ? SquareOutput(channel: 0) : 0) | ((m_channelEnabled[1] ? SquareOutput(channel: 1) : 0) << 4));
        }

        return (byte)((m_channelEnabled[2] ? WaveOutput() : 0) | ((m_channelEnabled[3] ? NoiseOutput() : 0) << 4));
    }
    /// <inheritdoc/>
    public void ApplyModel(ConsoleModel model) =>
        m_isColor = model.SupportsColor();

    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        writer.WriteBoolean(value: m_powered);
        writer.WriteInt32(value: m_frameSequencerStep);
        writer.WriteBoolean(value: m_lastDivApuBit);
        writer.WriteBoolean(value: m_sweepEnabled);
        writer.WriteBoolean(value: m_sweepNegateUsed);
        writer.WriteInt32(value: m_sweepShadow);
        writer.WriteInt32(value: m_sweepTimer);
        writer.WriteInt32(value: m_waveFetchHold);
        writer.WriteInt32(value: m_wavePosition);
        writer.WriteByte(value: m_waveSampleLatch);
        writer.WriteInt32(value: m_waveTimer);
        writer.WriteInt32(value: m_noiseLfsr);
        writer.WriteInt32(value: m_noiseTimer);
        writer.WriteBytes(value: m_registers);
        writer.WriteBytes(value: m_waveRam);

        for (var channel = 0; (channel < ChannelCount); ++channel) {
            writer.WriteBoolean(value: m_channelEnabled[channel]);
            writer.WriteBoolean(value: m_lengthEnabled[channel]);
            writer.WriteInt32(value: m_lengthCounter[channel]);
            writer.WriteInt32(value: m_envelopeVolume[channel]);
            writer.WriteInt32(value: m_envelopeTimer[channel]);
        }

        for (var channel = 0; (channel < 2); ++channel) {
            writer.WriteInt32(value: m_squarePosition[channel]);
            writer.WriteInt32(value: m_squareTimer[channel]);
        }
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        m_powered = reader.ReadBoolean();
        m_frameSequencerStep = reader.ReadInt32();
        m_lastDivApuBit = reader.ReadBoolean();
        m_sweepEnabled = reader.ReadBoolean();
        m_sweepNegateUsed = reader.ReadBoolean();
        m_sweepShadow = reader.ReadInt32();
        m_sweepTimer = reader.ReadInt32();
        m_waveFetchHold = reader.ReadInt32();
        m_wavePosition = reader.ReadInt32();
        m_waveSampleLatch = reader.ReadByte();
        m_waveTimer = reader.ReadInt32();
        m_noiseLfsr = reader.ReadInt32();
        m_noiseTimer = reader.ReadInt32();
        reader.ReadBytes(destination: m_registers);
        reader.ReadBytes(destination: m_waveRam);

        for (var channel = 0; (channel < ChannelCount); ++channel) {
            m_channelEnabled[channel] = reader.ReadBoolean();
            m_lengthEnabled[channel] = reader.ReadBoolean();
            m_lengthCounter[channel] = reader.ReadInt32();
            m_envelopeVolume[channel] = reader.ReadInt32();
            m_envelopeTimer[channel] = reader.ReadInt32();
        }

        for (var channel = 0; (channel < 2); ++channel) {
            m_squarePosition[channel] = reader.ReadInt32();
            m_squareTimer[channel] = reader.ReadInt32();
        }
    }

    // The single DIV-counter bit whose falling edge advances the frame sequencer, higher under double speed so the event
    // stays at 512 Hz.
    private bool DivApuBit() {
        var bit = m_key1.IsDoubleSpeed ? DoubleSpeedDivApuBit : NormalDivApuBit;

        return (m_timer.DivCounter & (1 << bit)) != 0;
    }
    // Advance the 512 Hz eight-step frame sequencer: the even steps clock the length counters (sweep on 2/6 and the
    // envelopes on 7 join once those units land). Whether the *next* step clocks length drives the write-time quirks.
    private void StepFrameSequencer() {
        m_frameSequencerStep = ((m_frameSequencerStep + 1) & 7);

        if ((m_frameSequencerStep & 1) == 0) {
            ClockLengthCounters();
        }

        if ((m_frameSequencerStep == 2) || (m_frameSequencerStep == 6)) {
            ClockSweep();
        }

        if (m_frameSequencerStep == 7) {
            ClockEnvelopes();
        }
    }
    // One length-counter clock: each channel whose length is enabled and non-zero counts down, disabling the channel as
    // it reaches zero.
    private void ClockLengthCounters() {
        for (var channel = 0; (channel < ChannelCount); ++channel) {
            if (m_lengthEnabled[channel] && (m_lengthCounter[channel] > 0)) {
                if (--m_lengthCounter[channel] == 0) {
                    m_channelEnabled[channel] = false;
                }
            }
        }
    }
    // The frame sequencer clocks length on even steps, so the *next* step will clock length only when the current step is
    // odd. The length-enable and trigger quirks hinge on this being false.
    private bool NextStepClocksLength() =>
        ((m_frameSequencerStep & 1) == 1);
    private void WriteMasterControl(byte value) {
        var powerOn = ((value & MasterPower) != 0);

        if (m_powered && !powerOn) {
            PowerOff();
        }
        else if (!m_powered && powerOn) {
            // The step counter restarts so the first DIV-APU event advances it to step 0 (a length clock); the event's
            // timing itself is preserved by the continuous DIV tracking in Tick.
            m_powered = true;
            m_frameSequencerStep = 7;
        }
    }
    // Powering off clears the register file and silences every channel; on Color the length counters clear too. Wave RAM
    // is untouched.
    private void PowerOff() {
        Array.Clear(array: m_registers);

        for (var channel = 0; (channel < ChannelCount); ++channel) {
            m_channelEnabled[channel] = false;
            m_lengthEnabled[channel] = false;
            m_envelopeVolume[channel] = 0;
            m_envelopeTimer[channel] = 0;

            // Color clears the length counters on power-off; monochrome keeps them (they stay writable while off).
            if (m_isColor) {
                m_lengthCounter[channel] = 0;
            }
        }

        Array.Clear(array: m_squarePosition);
        Array.Clear(array: m_squareTimer);

        m_frameSequencerStep = 0;
        m_noiseLfsr = 0;
        m_noiseTimer = 0;
        m_powered = false;
        m_sweepEnabled = false;
        m_sweepNegateUsed = false;
        m_sweepShadow = 0;
        m_sweepTimer = 0;
        m_waveFetchHold = 0;
        m_wavePosition = 0;
        m_waveSampleLatch = 0;
        m_waveTimer = 0;
    }
    private void WriteChannelRegister(int offset, byte value) {
        switch (offset) {
            case Nr10:
                m_registers[offset] = value;

                // Clearing the sweep's negate bit after a negate calculation has run since the last trigger disables the
                // channel at once (the calculation could no longer keep the frequency in range).
                if (m_sweepNegateUsed && ((value & SweepNegate) == 0)) {
                    m_channelEnabled[0] = false;
                }

                break;
            case Nr11:
                m_registers[offset] = value;
                m_lengthCounter[0] = (LengthMaxima[0] - (value & LengthDataMask));

                break;
            case Nr21:
                m_registers[offset] = value;
                m_lengthCounter[1] = (LengthMaxima[1] - (value & LengthDataMask));

                break;
            case Nr31:
                m_registers[offset] = value;
                m_lengthCounter[2] = (LengthMaxima[2] - value);

                break;
            case Nr41:
                m_registers[offset] = value;
                m_lengthCounter[3] = (LengthMaxima[3] - (value & LengthDataMask));

                break;
            case Nr12:
            case Nr22:
            case Nr30:
            case Nr42:
                m_registers[offset] = value;
                DisableDeadChannel(channel: ChannelForDac(offset: offset));

                break;
            case Nr14:
                WriteControlRegister(channel: 0, offset: offset, value: value);

                break;
            case Nr24:
                WriteControlRegister(channel: 1, offset: offset, value: value);

                break;
            case Nr34:
                WriteControlRegister(channel: 2, offset: offset, value: value);

                break;
            case Nr44:
                WriteControlRegister(channel: 3, offset: offset, value: value);

                break;
            default:
                m_registers[offset] = value;

                break;
        }
    }
    // Monochrome-only: while the APU is powered off, a write to a length-load register (NRx1) still sets that channel's
    // length counter (Color blocks every write). Only the length is written — no duty, no register store, no trigger.
    private void WriteLengthCounterWhilePoweredOff(int offset, byte value) {
        switch (offset) {
            case Nr11:
                m_lengthCounter[0] = (LengthMaxima[0] - (value & LengthDataMask));

                break;
            case Nr21:
                m_lengthCounter[1] = (LengthMaxima[1] - (value & LengthDataMask));

                break;
            case Nr31:
                m_lengthCounter[2] = (LengthMaxima[2] - value);

                break;
            case Nr41:
                m_lengthCounter[3] = (LengthMaxima[3] - (value & LengthDataMask));

                break;
        }
    }
    // A channel's control register (NRx4) sets its length-enable bit and may trigger it. Enabling length mid-cycle, when
    // the next sequencer step will not clock length, immediately clocks the (non-zero) counter — and can retire an
    // untriggered channel on the spot; the trigger applies the same extra clock to a freshly reloaded counter.
    private void WriteControlRegister(int channel, int offset, byte value) {
        var wasLengthEnabled = m_lengthEnabled[channel];
        var nowLengthEnabled = ((value & LengthEnableBit) != 0);
        var triggering = ((value & TriggerBit) != 0);

        m_registers[offset] = value;

        if (nowLengthEnabled && !wasLengthEnabled && !NextStepClocksLength() && (m_lengthCounter[channel] > 0)) {
            if ((--m_lengthCounter[channel] == 0) && !triggering) {
                m_channelEnabled[channel] = false;
            }
        }

        m_lengthEnabled[channel] = nowLengthEnabled;

        if (triggering) {
            Trigger(channel: channel);
        }
    }
    // A trigger enables the channel when its DAC is on, reloads a spent length counter to the channel maximum, and — if
    // length is enabled and the next step will not clock it — applies the extra length clock to that fresh reload.
    private void Trigger(int channel) {
        m_channelEnabled[channel] = IsDacEnabled(channel: channel);

        if (m_lengthCounter[channel] == 0) {
            m_lengthCounter[channel] = LengthMaxima[channel];

            if (m_lengthEnabled[channel] && !NextStepClocksLength()) {
                --m_lengthCounter[channel];
            }
        }

        // Per-channel trigger effects. A square channel reloads its frequency timer and envelope but KEEPS its duty
        // position (it free-runs, reset only by an APU power-off); the wave channel restarts its sample position; the
        // noise channel clears its LFSR. Only the first square channel has a sweep unit.
        switch (channel) {
            case 0:
                m_squareTimer[0] = SquarePeriod(channel: 0);
                LoadEnvelope(channel: 0, register: Nr12);
                TriggerSweep();

                break;
            case 1:
                m_squareTimer[1] = SquarePeriod(channel: 1);
                LoadEnvelope(channel: 1, register: Nr22);

                break;
            case 2:
                // Retriggering the playing channel while its fetch is busy corrupts the head of wave RAM on monochrome
                // hardware (the CPU's trigger and the fetch collide on the RAM port); Color hardware buffers the port
                // and is immune. Predicate A/B (the hardware-accurate "wave trigger while on" case): the
                // fetch-busy flag — inside the window a fetch opens, i.e. (m_waveFetchHold > 0) — PASSES; the
                // fetch-imminent countdown alternatives (m_waveTimer == 2, == 1, <= 2) each fire one slot late and
                // FAIL the test's wave-RAM table CRC.
                if (!m_isColor && m_channelEnabled[2] && (m_waveFetchHold > 0)) {
                    CorruptWaveRamOnRetrigger();
                }

                // The position restarts but the sample latch is NOT refetched: the channel keeps playing the stale
                // latch until the first fetch, which lands one full period plus the post-trigger delay after the
                // trigger (the delay is the A/B'd cross-lineage constant — see WaveTriggerFetchDelayDots).
                m_wavePosition = 0;
                m_waveTimer = (WavePeriod() + WaveTriggerFetchDelayDots);

                break;
            default:
                m_noiseLfsr = 0;
                m_noiseTimer = NoisePeriod();
                LoadEnvelope(channel: 3, register: Nr42);

                break;
        }
    }
    // Load a channel's starting envelope volume and reload countdown from its NRx2 register (upper nibble = volume, low
    // three bits = period), applied on a trigger.
    private void LoadEnvelope(int channel, int register) {
        m_envelopeVolume[channel] = (m_registers[register] >> 4);
        m_envelopeTimer[channel] = (m_registers[register] & 0x07);
    }
    // The wave channel's frequency-timer period in dots: (2048 - the 11-bit frequency) doubled.
    private int WavePeriod() =>
        ((2048 - (m_registers[Nr33] | ((m_registers[Nr34] & 0x07) << 8))) * 2);
    // One dot of a square channel's frequency timer: while it plays, count down and, on expiry, reload from the
    // current frequency and step the duty position. The position wraps but is never reset here, so it free-runs.
    private void AdvanceSquareTimer(int channel) {
        if (m_channelEnabled[channel] && (--m_squareTimer[channel] <= 0)) {
            m_squareTimer[channel] = SquarePeriod(channel: channel);
            m_squarePosition[channel] = ((m_squarePosition[channel] + 1) & 0x07);
        }
    }
    // A square channel's frequency-timer period in dots: (2048 - the 11-bit frequency) times four.
    private int SquarePeriod(int channel) {
        var frequency = (channel == 0)
            ? (m_registers[Nr13] | ((m_registers[Nr14] & 0x07) << 8))
            : (m_registers[Nr23] | ((m_registers[Nr24] & 0x07) << 8));

        return ((2048 - frequency) * 4);
    }
    // The noise channel's frequency-timer period in dots: a divisor selected by NR43's low three bits (code 0 meaning
    // half a step) shifted up by NR43's upper-nibble clock-shift (codes 14/15 gate the clock entirely — see
    // TickGenerators, which never lets the countdown run under them).
    private int NoisePeriod() {
        var code = (m_registers[Nr43] & 0x07);
        var divisor = (code == 0) ? 8 : (code << 4);

        return (divisor << (m_registers[Nr43] >> 4));
    }
    // Advance the noise LFSR one step: feed back the XNOR of its low two bits into bit 14 (and bit 6 in the 7-bit width
    // selected by NR43 bit 3), so the low bit that gates the output follows the pseudo-random sequence.
    private void StepNoiseLfsr() {
        var feedback = ((m_noiseLfsr ^ (m_noiseLfsr >> 1) ^ 1) & 1);
        var mask = ((m_registers[Nr43] & 0x08) != 0) ? 0x4040 : 0x4000;

        m_noiseLfsr >>= 1;

        if (feedback != 0) {
            m_noiseLfsr |= mask;
        }
        else {
            m_noiseLfsr &= ~mask;
        }
    }
    // The envelope clock (frame-sequencer step 7, 64 Hz): step the two square channels' and the noise channel's volume.
    private void ClockEnvelopes() {
        ClockEnvelope(channel: 0, register: Nr12);
        ClockEnvelope(channel: 1, register: Nr22);
        ClockEnvelope(channel: 3, register: Nr42);
    }
    // One envelope clock for a channel: a zero period disables the envelope; otherwise count down and, on expiry, reload
    // and step the volume one unit in the NRx2 direction, holding at the 0 or 15 rail.
    private void ClockEnvelope(int channel, int register) {
        var period = (m_registers[register] & 0x07);

        if ((period == 0) || (--m_envelopeTimer[channel] > 0)) {
            return;
        }

        m_envelopeTimer[channel] = period;

        var next = (m_envelopeVolume[channel] + (((m_registers[register] & 0x08) != 0) ? 1 : -1));

        if ((next >= 0) && (next <= 15)) {
            m_envelopeVolume[channel] = next;
        }
    }
    // A square channel's current digital output (0-15): its envelope volume when the selected duty pattern's bit for the
    // current position is high, otherwise zero.
    private int SquareOutput(int channel) {
        var duty = (m_registers[(channel == 0) ? Nr11 : Nr21] >> 6);

        return (DutyTable[(duty * 8) + m_squarePosition[channel]] != 0) ? m_envelopeVolume[channel] : 0;
    }
    // The wave channel's current digital output: the four-bit sample from the LAST-FETCHED byte latch (never a live
    // wave-RAM read — a CPU write while playing changes RAM, not what is heard), right-shifted by the NR32 volume code.
    private int WaveOutput() {
        var nibble = ((m_wavePosition & 1) != 0) ? (m_waveSampleLatch & 0x0F) : (m_waveSampleLatch >> 4);

        return (nibble >> WaveVolumeShift[(m_registers[Nr32] >> 5) & 0x03]);
    }
    // The monochrome retrigger collision: the byte the channel was about to fetch bleeds into the head of wave RAM —
    // a byte from the first four-byte row copies alone into byte 0, one from a later row drags its whole aligned
    // four-byte row over bytes 0-3.
    private void CorruptWaveRamOnRetrigger() {
        var index = (((m_wavePosition + 1) & 0x1F) >> 1);

        if (index < 4) {
            m_waveRam[0] = m_waveRam[index];
        }
        else {
            var row = (index & 0x0C);

            for (var offset = 0; (offset < 4); ++offset) {
                m_waveRam[offset] = m_waveRam[row + offset];
            }
        }
    }
    // The noise channel's current digital output: its envelope volume when the LFSR's low bit is set, otherwise zero.
    private int NoiseOutput() =>
        ((m_noiseLfsr & 1) != 0) ? m_envelopeVolume[3] : 0;
    // A trigger arms the sweep unit: it copies the current frequency into the shadow register, reloads the sweep timer,
    // enables the unit when it has a period or a shift, and — when a shift is set — runs one frequency calculation
    // immediately so an already-overflowing sweep disables the channel on the trigger itself.
    private void TriggerSweep() {
        var period = SweepPeriod();
        var shift = (m_registers[Nr10] & SweepShiftMask);

        m_sweepShadow = CurrentFrequency();
        m_sweepTimer = (period != 0) ? period : SweepReloadPeriod;
        m_sweepEnabled = (period != 0) || (shift != 0);
        m_sweepNegateUsed = false;

        if (shift != 0) {
            _ = CalculateSweepFrequency();
        }
    }
    // One sweep clock (frame-sequencer steps 2 and 6): count the timer down and, when it lands, reload it; then, if the
    // unit is enabled with a real period, compute the next frequency, write it back through the shadow register when it
    // fits and the shift is non-zero, and run a second calculation purely for its overflow check.
    private void ClockSweep() {
        if (--m_sweepTimer > 0) {
            return;
        }

        var period = SweepPeriod();

        m_sweepTimer = (period != 0) ? period : SweepReloadPeriod;

        if (!m_sweepEnabled || (period == 0)) {
            return;
        }

        var newFrequency = CalculateSweepFrequency();

        if ((newFrequency <= MaxFrequency) && ((m_registers[Nr10] & SweepShiftMask) != 0)) {
            m_sweepShadow = newFrequency;

            WriteSweepFrequency(frequency: newFrequency);

            _ = CalculateSweepFrequency();
        }
    }
    // The next sweep frequency: the shadow shifted right and added to (or, in negate mode, subtracted from) itself. A
    // result past the 11-bit maximum overflows and disables the channel; using negate mode latches so that later clearing
    // it can disable the channel too.
    private int CalculateSweepFrequency() {
        var delta = (m_sweepShadow >> (m_registers[Nr10] & SweepShiftMask));
        int newFrequency;

        if ((m_registers[Nr10] & SweepNegate) != 0) {
            newFrequency = (m_sweepShadow - delta);
            m_sweepNegateUsed = true;
        }
        else {
            newFrequency = (m_sweepShadow + delta);
        }

        if (newFrequency > MaxFrequency) {
            m_channelEnabled[0] = false;
        }

        return newFrequency;
    }
    // Store an eleven-bit frequency back into NR13 (low byte) and NR14 (low three bits), leaving NR14's control bits.
    private void WriteSweepFrequency(int frequency) {
        m_registers[Nr13] = (byte)(frequency & 0xFF);
        m_registers[Nr14] = (byte)((m_registers[Nr14] & 0xF8) | ((frequency >> 8) & 0x07));
    }
    private int CurrentFrequency() =>
        (m_registers[Nr13] | ((m_registers[Nr14] & 0x07) << 8));
    private int SweepPeriod() =>
        ((m_registers[Nr10] >> 4) & 0x07);
    // Turning a channel's DAC off (the upper five envelope bits, or NR30 bit 7 for wave) silences it at once.
    private void DisableDeadChannel(int channel) {
        if (!IsDacEnabled(channel: channel)) {
            m_channelEnabled[channel] = false;
        }
    }
    private bool IsDacEnabled(int channel) =>
        channel switch {
            0 => ((m_registers[Nr12] & SquareNoiseDacMask) != 0),
            1 => ((m_registers[Nr22] & SquareNoiseDacMask) != 0),
            2 => ((m_registers[Nr30] & WaveDacEnable) != 0),
            _ => ((m_registers[Nr42] & SquareNoiseDacMask) != 0),
        };
    private static int ChannelForDac(int offset) =>
        offset switch {
            Nr12 => 0,
            Nr22 => 1,
            Nr30 => 2,
            _ => 3,
        };
}

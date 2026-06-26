// The APU's main() is a free-running 2 MHz coroutine and its IO routing is a large address switch; neither maps
// usefully onto the complexity/maintainability analyzers.
#pragma warning disable CA1502 // Avoid excessive complexity
#pragma warning disable CA1505 // Avoid unmaintainable code

namespace Puck.HumbleGamingBrick.Ares;

/// <summary>
/// The Game Boy APU, ported faithfully from ares (<c>gb/apu</c>). ares runs the APU as a 2 MHz cothread whose
/// <c>main()</c> advances each of the four sound channels by one tick, mixes the stereo sample, and — once every
/// 4096 ticks (512 Hz) — clocks the frame sequencer (length at 256 Hz, sweep at 128 Hz, envelope at 64 Hz). Here it
/// runs as a single-threaded driver: <see cref="AdvanceTo"/> steps that 2 MHz <c>main()</c> until it catches up to the
/// CPU clock, exactly as the PPU driver does — the APU is fixed at 2 MHz, so it advances once per <c>2 &lt;&lt; speedDouble</c>
/// CPU clocks (the CPU runs at 4 MHz, or 8 MHz in CGB double-speed). The frame sequencer's <c>phase</c> drives the
/// length-counter "extra clock" quirks that the blargg dmg_sound / mooneye APU tests exercise. Audio sample output is
/// modelled (and exposed via <see cref="SampleLeft"/>/<see cref="SampleRight"/>) but is not consumed by the harness.
/// </summary>
public sealed class AresApu : IAresIo {
    private readonly bool m_color;

    private readonly Square1 m_square1 = new();
    private readonly Square2 m_square2 = new();
    private readonly Wave m_wave = new();
    private readonly Noise m_noise = new();
    private readonly Sequencer m_sequencer = new();

    private int m_phase; // high 3-bits of clock counter (n3)
    private int m_cycle; // low 12-bits of clock counter (n12)

    // Driver: CPU clocks accumulated toward the next 2 MHz APU tick.
    private ulong m_clock;
    private int m_divider;

    /// <summary>Creates the APU for the given model and seeds its post-power state.</summary>
    /// <param name="color">Whether the machine is a Game Boy Color (affects power-off length resets, NRx1 writes,
    /// the wave-RAM trigger corruption quirk, and the PCM12/PCM34 registers).</param>
    public AresApu(bool color) {
        m_color = color;

        Power();
    }

    /// <summary>Supplies the current CPU double-speed flag (0 = normal, 1 = double); set during machine assembly.
    /// The APU's fixed 2 MHz rate means it advances once per <c>2 &lt;&lt; speedDouble</c> CPU clocks.</summary>
    public Func<int>? SpeedDouble { get; set; }

    /// <summary>The most recent mixed left output sample (ares <c>sequencer.left</c>).</summary>
    public short SampleLeft => m_sequencer.Left;

    /// <summary>The most recent mixed right output sample (ares <c>sequencer.right</c>).</summary>
    public short SampleRight => m_sequencer.Right;

    /// <summary>Advances the APU's 2 MHz clock until it catches up to the CPU's clock (ares synchronize,
    /// single-threaded). The APU steps once per <c>2 &lt;&lt; speedDouble</c> CPU clocks.</summary>
    /// <param name="cpuClock">The CPU's elapsed-clock count to catch up to.</param>
    public void AdvanceTo(ulong cpuClock) {
        var stride = 2 << (SpeedDouble?.Invoke() ?? 0);

        while (m_clock < cpuClock) {
            m_clock += 1;

            if (--m_divider <= 0) {
                m_divider = stride;
                Main();
            }
        }
    }

    private void Main() {
        m_square1.Run(apu: this);
        m_square2.Run(apu: this);
        m_wave.Run(apu: this);
        m_noise.Run(apu: this);
        m_sequencer.Run(apu: this);

        if (m_cycle == 0) { // 512 Hz
            if ((m_phase == 0) || (m_phase == 2) || (m_phase == 4) || (m_phase == 6)) { // 256 Hz
                m_square1.ClockLength();
                m_square2.ClockLength();
                m_wave.ClockLength();
                m_noise.ClockLength();
            }

            if ((m_phase == 2) || (m_phase == 6)) { // 128 Hz
                m_square1.ClockSweep();
            }

            if (m_phase == 7) { // 64 Hz
                m_square1.ClockEnvelope();
                m_square2.ClockEnvelope();
                m_noise.ClockEnvelope();
            }

            m_phase = (m_phase + 1) & 7;
        }

        m_cycle = (m_cycle + 1) & 0x0FFF;
    }

    private void Power() {
        m_square1.Power(apu: this, initializeLength: true);
        m_square2.Power(initializeLength: true);
        m_wave.Power(initializeLength: true);
        m_noise.Power(initializeLength: true);
        m_sequencer.Power();
        m_phase = 0;
        m_cycle = 0;

        // ares seeds wave RAM with a PRNG; a fixed pattern is sufficient for the register/timing conformance work.
        for (var n = 0; n < m_wave.Pattern.Length; n += 1) {
            m_wave.Pattern[n] = 0;
        }
    }

    /// <inheritdoc/>
    public byte ReadIo(int cycle, ushort address, byte data) {
        if (m_color) {
            // PCM12
            if ((address == 0xFF76) && (cycle == 2)) {
                return (byte)((m_square1.Sample & 0x0F) | ((m_square2.Sample & 0x0F) << 4));
            }

            // PCM34
            if ((address == 0xFF77) && (cycle == 2)) {
                return (byte)((m_wave.Sample & 0x0F) | ((m_noise.Sample & 0x0F) << 4));
            }
        }

        if ((address < 0xFF10) || (address > 0xFF3F)) {
            return data;
        }

        // NR10
        if ((address == 0xFF10) && (cycle == 2)) {
            return (byte)((data & ~0x7F)
                | (m_square1.SweepShift & 0x07)
                | SetBit(value: 0, shift: 3, set: m_square1.SweepDirection)
                | ((m_square1.SweepFrequency & 0x07) << 4));
        }

        // NR11
        if ((address == 0xFF11) && (cycle == 2)) {
            return (byte)((data & ~0xC0) | ((m_square1.Duty & 0x03) << 6));
        }

        // NR12
        if ((address == 0xFF12) && (cycle == 2)) {
            return (byte)((m_square1.EnvelopeFrequency & 0x07) | SetBit(value: 0, shift: 3, set: m_square1.EnvelopeDirection) | ((m_square1.EnvelopeVolume & 0x0F) << 4));
        }

        // NR13
        if ((address == 0xFF13) && (cycle == 2)) {
            return data;
        }

        // NR14
        if ((address == 0xFF14) && (cycle == 2)) {
            return (byte)((data & ~0x40) | SetBit(value: 0, shift: 6, set: m_square1.Counter));
        }

        // NR20
        if ((address == 0xFF15) && (cycle == 2)) {
            return data;
        }

        // NR21
        if ((address == 0xFF16) && (cycle == 2)) {
            return (byte)((data & ~0xC0) | ((m_square2.Duty & 0x03) << 6));
        }

        // NR22
        if ((address == 0xFF17) && (cycle == 2)) {
            return (byte)((m_square2.EnvelopeFrequency & 0x07) | SetBit(value: 0, shift: 3, set: m_square2.EnvelopeDirection) | ((m_square2.EnvelopeVolume & 0x0F) << 4));
        }

        // NR23
        if ((address == 0xFF18) && (cycle == 2)) {
            return data;
        }

        // NR24
        if ((address == 0xFF19) && (cycle == 2)) {
            return (byte)((data & ~0x40) | SetBit(value: 0, shift: 6, set: m_square2.Counter));
        }

        // NR30
        if ((address == 0xFF1A) && (cycle == 2)) {
            return (byte)((data & ~0x80) | SetBit(value: 0, shift: 7, set: m_wave.DacEnable));
        }

        // NR31
        if ((address == 0xFF1B) && (cycle == 2)) {
            return data;
        }

        // NR32
        if ((address == 0xFF1C) && (cycle == 2)) {
            return (byte)((data & ~0x60) | ((m_wave.Volume & 0x03) << 5));
        }

        // NR33
        if ((address == 0xFF1D) && (cycle == 2)) {
            return data;
        }

        // NR34
        if ((address == 0xFF1E) && (cycle == 2)) {
            return (byte)((data & ~0x40) | SetBit(value: 0, shift: 6, set: m_wave.Counter));
        }

        // NR40
        if ((address == 0xFF1F) && (cycle == 2)) {
            return data;
        }

        // NR41
        if ((address == 0xFF20) && (cycle == 2)) {
            return data;
        }

        // NR42
        if ((address == 0xFF21) && (cycle == 2)) {
            return (byte)((m_noise.EnvelopeFrequency & 0x07) | SetBit(value: 0, shift: 3, set: m_noise.EnvelopeDirection) | ((m_noise.EnvelopeVolume & 0x0F) << 4));
        }

        // NR43
        if ((address == 0xFF22) && (cycle == 2)) {
            return (byte)((m_noise.Divisor & 0x07) | SetBit(value: 0, shift: 3, set: m_noise.Narrow) | ((m_noise.Frequency & 0x0F) << 4));
        }

        // NR44
        if ((address == 0xFF23) && (cycle == 2)) {
            return (byte)((data & ~0x40) | SetBit(value: 0, shift: 6, set: m_noise.Counter));
        }

        // NR50
        if ((address == 0xFF24) && (cycle == 2)) {
            return (byte)((m_sequencer.RightVolume & 0x07)
                | SetBit(value: 0, shift: 3, set: m_sequencer.RightEnable)
                | ((m_sequencer.LeftVolume & 0x07) << 4)
                | SetBit(value: 0, shift: 7, set: m_sequencer.LeftEnable));
        }

        // NR51
        if ((address == 0xFF25) && (cycle == 2)) {
            return (byte)(SetBit(value: 0, shift: 0, set: m_sequencer.Square1.RightEnable)
                | SetBit(value: 0, shift: 1, set: m_sequencer.Square2.RightEnable)
                | SetBit(value: 0, shift: 2, set: m_sequencer.Wave.RightEnable)
                | SetBit(value: 0, shift: 3, set: m_sequencer.Noise.RightEnable)
                | SetBit(value: 0, shift: 4, set: m_sequencer.Square1.LeftEnable)
                | SetBit(value: 0, shift: 5, set: m_sequencer.Square2.LeftEnable)
                | SetBit(value: 0, shift: 6, set: m_sequencer.Wave.LeftEnable)
                | SetBit(value: 0, shift: 7, set: m_sequencer.Noise.LeftEnable));
        }

        // NR52 (latches at every cycle in ares — no cycle gate)
        if (address == 0xFF26) {
            return (byte)((data & ~0x8F)
                | SetBit(value: 0, shift: 0, set: m_square1.Enable)
                | SetBit(value: 0, shift: 1, set: m_square2.Enable)
                | SetBit(value: 0, shift: 2, set: m_wave.Enable)
                | SetBit(value: 0, shift: 3, set: m_noise.Enable)
                | SetBit(value: 0, shift: 7, set: m_sequencer.Enable));
        }

        if ((address >= 0xFF30) && (address <= 0xFF3F) && (cycle == 2)) {
            return m_wave.ReadRam(color: m_color, address: (address & 0x0F), data: data);
        }

        return data;
    }

    /// <inheritdoc/>
    public void WriteIo(int cycle, ushort address, byte data) {
        if ((address < 0xFF10) || (address > 0xFF3F)) {
            return;
        }

        if (!m_sequencer.Enable) {
            var valid = (address == 0xFF26); // NR52

            if (!m_color) {
                // NRx1 length is writable only on DMG/SGB; not on CGB. Duty stays 0 (only the low 6 bits land).
                if (address == 0xFF11) { valid = true; data &= 0x3F; } // NR11
                if (address == 0xFF16) { valid = true; data &= 0x3F; } // NR21
                if (address == 0xFF1B) { valid = true; } // NR31
                if (address == 0xFF20) { valid = true; } // NR41
            }

            if (!valid) {
                return;
            }
        }

        // NR10
        if ((address == 0xFF10) && (cycle == 2)) {
            if (m_square1.SweepEnable && m_square1.SweepNegate && ((data & 0x08) == 0)) {
                m_square1.Enable = false;
            }

            m_square1.SweepShift = (data & 0x07);
            m_square1.SweepDirection = ((data & 0x08) != 0);
            m_square1.SweepFrequency = ((data >> 4) & 0x07);

            return;
        }

        // NR11
        if ((address == 0xFF11) && (cycle == 2)) {
            m_square1.Length = 64 - (data & 0x3F);
            m_square1.Duty = ((data >> 6) & 0x03);

            return;
        }

        // NR12
        if ((address == 0xFF12) && (cycle == 2)) {
            m_square1.EnvelopeFrequency = (data & 0x07);
            m_square1.EnvelopeDirection = ((data & 0x08) != 0);
            m_square1.EnvelopeVolume = ((data >> 4) & 0x0F);

            if (!m_square1.DacEnable()) {
                m_square1.Enable = false;
            }

            return;
        }

        // NR13
        if ((address == 0xFF13) && (cycle == 2)) {
            m_square1.Frequency = (m_square1.Frequency & ~0xFF) | data;

            return;
        }

        // NR14
        if ((address == 0xFF14) && (cycle == 4)) {
            if (((m_phase & 1) != 0) && !m_square1.Counter && ((data & 0x40) != 0)) {
                if ((m_square1.Length != 0) && (--m_square1.Length == 0)) {
                    m_square1.Enable = false;
                }
            }

            m_square1.Frequency = (m_square1.Frequency & 0xFF) | ((data & 0x07) << 8);
            m_square1.Counter = ((data & 0x40) != 0);

            if ((data & 0x80) != 0) {
                m_square1.Trigger(apu: this);
            }

            return;
        }

        // NR20
        if ((address == 0xFF15) && (cycle == 2)) {
            return;
        }

        // NR21
        if ((address == 0xFF16) && (cycle == 2)) {
            m_square2.Length = 64 - (data & 0x3F);
            m_square2.Duty = ((data >> 6) & 0x03);

            return;
        }

        // NR22
        if ((address == 0xFF17) && (cycle == 2)) {
            m_square2.EnvelopeFrequency = (data & 0x07);
            m_square2.EnvelopeDirection = ((data & 0x08) != 0);
            m_square2.EnvelopeVolume = ((data >> 4) & 0x0F);

            if (!m_square2.DacEnable()) {
                m_square2.Enable = false;
            }

            return;
        }

        // NR23
        if ((address == 0xFF18) && (cycle == 2)) {
            m_square2.Frequency = (m_square2.Frequency & ~0xFF) | data;

            return;
        }

        // NR24
        if ((address == 0xFF19) && (cycle == 4)) {
            if (((m_phase & 1) != 0) && !m_square2.Counter && ((data & 0x40) != 0)) {
                if ((m_square2.Length != 0) && (--m_square2.Length == 0)) {
                    m_square2.Enable = false;
                }
            }

            m_square2.Frequency = (m_square2.Frequency & 0xFF) | ((data & 0x07) << 8);
            m_square2.Counter = ((data & 0x40) != 0);

            if ((data & 0x80) != 0) {
                m_square2.Trigger(apu: this);
            }

            return;
        }

        // NR30
        if ((address == 0xFF1A) && (cycle == 2)) {
            m_wave.DacEnable = ((data & 0x80) != 0);

            if (!m_wave.DacEnable) {
                m_wave.Enable = false;
            }

            return;
        }

        // NR31
        if ((address == 0xFF1B) && (cycle == 2)) {
            m_wave.Length = 256 - data;

            return;
        }

        // NR32
        if ((address == 0xFF1C) && (cycle == 2)) {
            m_wave.Volume = ((data >> 5) & 0x03);

            return;
        }

        // NR33
        if ((address == 0xFF1D) && (cycle == 2)) {
            m_wave.Frequency = (m_wave.Frequency & ~0xFF) | data;

            return;
        }

        // NR34
        if ((address == 0xFF1E) && (cycle == 4)) {
            if (((m_phase & 1) != 0) && !m_wave.Counter && ((data & 0x40) != 0)) {
                if ((m_wave.Length != 0) && (--m_wave.Length == 0)) {
                    m_wave.Enable = false;
                }
            }

            m_wave.Frequency = (m_wave.Frequency & 0xFF) | ((data & 0x07) << 8);
            m_wave.Counter = ((data & 0x40) != 0);

            if ((data & 0x80) != 0) {
                m_wave.Trigger(apu: this, color: m_color);
            }

            return;
        }

        // NR40
        if ((address == 0xFF1F) && (cycle == 2)) {
            return;
        }

        // NR41
        if ((address == 0xFF20) && (cycle == 2)) {
            m_noise.Length = 64 - (data & 0x3F);

            return;
        }

        // NR42
        if ((address == 0xFF21) && (cycle == 2)) {
            m_noise.EnvelopeFrequency = (data & 0x07);
            m_noise.EnvelopeDirection = ((data & 0x08) != 0);
            m_noise.EnvelopeVolume = ((data >> 4) & 0x0F);

            if (!m_noise.DacEnable()) {
                m_noise.Enable = false;
            }

            return;
        }

        // NR43
        if ((address == 0xFF22) && (cycle == 2)) {
            m_noise.Divisor = (data & 0x07);
            m_noise.Narrow = ((data & 0x08) != 0);
            m_noise.Frequency = ((data >> 4) & 0x0F);
            m_noise.Period = m_noise.GetPeriod();

            return;
        }

        // NR44
        if ((address == 0xFF23) && (cycle == 4)) {
            if (((m_phase & 1) != 0) && !m_noise.Counter && ((data & 0x40) != 0)) {
                if ((m_noise.Length != 0) && (--m_noise.Length == 0)) {
                    m_noise.Enable = false;
                }
            }

            m_noise.Counter = ((data & 0x40) != 0);

            if ((data & 0x80) != 0) {
                m_noise.Trigger(apu: this);
            }

            return;
        }

        // NR50
        if ((address == 0xFF24) && (cycle == 2)) {
            m_sequencer.RightVolume = (data & 0x07);
            m_sequencer.RightEnable = ((data & 0x08) != 0);
            m_sequencer.LeftVolume = ((data >> 4) & 0x07);
            m_sequencer.LeftEnable = ((data & 0x80) != 0);

            return;
        }

        // NR51
        if ((address == 0xFF25) && (cycle == 2)) {
            m_sequencer.Square1.RightEnable = ((data & 0x01) != 0);
            m_sequencer.Square2.RightEnable = ((data & 0x02) != 0);
            m_sequencer.Wave.RightEnable = ((data & 0x04) != 0);
            m_sequencer.Noise.RightEnable = ((data & 0x08) != 0);
            m_sequencer.Square1.LeftEnable = ((data & 0x10) != 0);
            m_sequencer.Square2.LeftEnable = ((data & 0x20) != 0);
            m_sequencer.Wave.LeftEnable = ((data & 0x40) != 0);
            m_sequencer.Noise.LeftEnable = ((data & 0x80) != 0);

            return;
        }

        // NR52
        if ((address == 0xFF26) && (cycle == 4)) {
            var newEnable = ((data & 0x80) != 0);

            if (m_sequencer.Enable != newEnable) {
                m_sequencer.Enable = newEnable;

                if (!m_sequencer.Enable) {
                    var resetLengthCounters = m_color;

                    m_square1.Power(apu: this, initializeLength: resetLengthCounters);
                    m_square2.Power(initializeLength: resetLengthCounters);
                    m_wave.Power(initializeLength: resetLengthCounters);
                    m_noise.Power(initializeLength: resetLengthCounters);
                    m_sequencer.Power();
                }
                else {
                    m_phase = 0;
                }
            }

            return;
        }

        if ((address >= 0xFF30) && (address <= 0xFF3F) && (cycle == 2)) {
            m_wave.WriteRam(color: m_color, address: (address & 0x0F), data: data);
        }
    }

    // Bit helpers: ares' n8::bit(i)=value semantics, restricted to the masked fields each register sets.

    private static int SetBit(int value, int shift, bool set) =>
        set ? (value | (1 << shift)) : (value & ~(1 << shift));

    internal int Phase => m_phase;

    // === Channels (ares apu/square1.cpp, square2.cpp, wave.cpp, noise.cpp, sequencer.cpp) ===

    private sealed class Square1 {
        public bool Enable;

        public int SweepFrequency; // n3
        public bool SweepDirection;
        public int SweepShift; // n3
        public bool SweepNegate;
        public int Duty; // n2
        public int Length;
        public int EnvelopeVolume; // n4
        public bool EnvelopeDirection;
        public int EnvelopeFrequency; // n3
        public int Frequency; // n11
        public bool Counter;

        public int Sample; // n4
        public short Output;
        public bool DutyOutput;
        public int Phase; // n3
        public int Period;
        public int EnvelopePeriod; // n3
        public int SweepPeriod; // n3
        public int FrequencyShadow; // s32
        public bool SweepEnable;
        public int Volume; // n4

        public bool DacEnable() =>
            (EnvelopeVolume != 0) || EnvelopeDirection;

        public void Run(AresApu apu) {
            if ((Period != 0) && (--Period == 0)) {
                Period = 2 * (2048 - Frequency);
                Phase = (Phase + 1) & 7;

                DutyOutput = Duty switch {
                    0 => (Phase == 6),
                    1 => (Phase >= 6),
                    2 => (Phase >= 4),
                    _ => (Phase <= 5),
                };
            }

            Sample = DutyOutput ? Volume : 0;

            if (!Enable) {
                Sample = 0;
            }

            Output = (short)Sample;
        }

        public void Sweep(bool update) {
            if (!SweepEnable) {
                return;
            }

            SweepNegate = SweepDirection;

            var delta = (FrequencyShadow >> SweepShift);
            var freq = FrequencyShadow + (SweepNegate ? -delta : delta);

            if (freq > 2047) {
                Enable = false;
            }
            else if ((SweepShift != 0) && update) {
                FrequencyShadow = freq;
                Frequency = (freq & 2047);
                Period = 2 * (2048 - Frequency);
            }
        }

        public void ClockLength() {
            if (Counter) {
                if ((Length != 0) && (--Length == 0)) {
                    Enable = false;
                }
            }
        }

        public void ClockSweep() {
            if (--SweepPeriod == 0) {
                SweepPeriod = (SweepFrequency != 0) ? SweepFrequency : 8;

                if (SweepEnable && (SweepFrequency != 0)) {
                    Sweep(update: true);
                    Sweep(update: false);
                }
            }
        }

        public void ClockEnvelope() {
            if (Enable && (EnvelopeFrequency != 0) && (--EnvelopePeriod == 0)) {
                EnvelopePeriod = (EnvelopeFrequency != 0) ? EnvelopeFrequency : 8;

                if (!EnvelopeDirection && (Volume > 0)) { Volume -= 1; }
                if (EnvelopeDirection && (Volume < 15)) { Volume += 1; }
            }
        }

        public void Trigger(AresApu apu) {
            Enable = DacEnable();
            Period = 2 * (2048 - Frequency);
            EnvelopePeriod = (EnvelopeFrequency != 0) ? EnvelopeFrequency : 8;
            Volume = EnvelopeVolume;

            if (Length == 0) {
                Length = 64;

                if (((apu.Phase & 1) != 0) && Counter) {
                    Length -= 1;
                }
            }

            FrequencyShadow = Frequency;
            SweepNegate = false;
            SweepPeriod = (SweepFrequency != 0) ? SweepFrequency : 8;
            SweepEnable = (SweepPeriod != 0) || (SweepShift != 0);

            if (SweepShift != 0) {
                Sweep(update: false);
            }
        }

        public void Power(AresApu apu, bool initializeLength) {
            _ = apu;

            Enable = false;

            SweepFrequency = 0;
            SweepDirection = false;
            SweepShift = 0;
            SweepNegate = false;
            Duty = 0;
            EnvelopeVolume = 0;
            EnvelopeDirection = false;
            EnvelopeFrequency = 0;
            Frequency = 0;
            Counter = false;

            Output = 0;
            DutyOutput = false;
            Phase = 0;
            Period = 0;
            EnvelopePeriod = 0;
            SweepPeriod = 0;
            FrequencyShadow = 0;
            SweepEnable = false;
            Volume = 0;

            if (initializeLength) {
                Length = 64;
            }
        }
    }

    private sealed class Square2 {
        public bool Enable;

        public int Duty; // n2
        public int Length;
        public int EnvelopeVolume; // n4
        public bool EnvelopeDirection;
        public int EnvelopeFrequency; // n3
        public int Frequency; // n11
        public bool Counter;

        public int Sample; // n4
        public short Output;
        public bool DutyOutput;
        public int Phase; // n3
        public int Period;
        public int EnvelopePeriod; // n3
        public int Volume; // n4

        public bool DacEnable() =>
            (EnvelopeVolume != 0) || EnvelopeDirection;

        public void Run(AresApu apu) {
            _ = apu;

            if ((Period != 0) && (--Period == 0)) {
                Period = 2 * (2048 - Frequency);
                Phase = (Phase + 1) & 7;

                DutyOutput = Duty switch {
                    0 => (Phase == 6),
                    1 => (Phase >= 6),
                    2 => (Phase >= 4),
                    _ => (Phase <= 5),
                };
            }

            Sample = DutyOutput ? Volume : 0;

            if (!Enable) {
                Sample = 0;
            }

            Output = (short)Sample;
        }

        public void ClockLength() {
            if (Counter) {
                if ((Length != 0) && (--Length == 0)) {
                    Enable = false;
                }
            }
        }

        public void ClockEnvelope() {
            if (Enable && (EnvelopeFrequency != 0) && (--EnvelopePeriod == 0)) {
                EnvelopePeriod = (EnvelopeFrequency != 0) ? EnvelopeFrequency : 8;

                if (!EnvelopeDirection && (Volume > 0)) { Volume -= 1; }
                if (EnvelopeDirection && (Volume < 15)) { Volume += 1; }
            }
        }

        public void Trigger(AresApu apu) {
            Enable = DacEnable();
            Period = 2 * (2048 - Frequency);
            EnvelopePeriod = (EnvelopeFrequency != 0) ? EnvelopeFrequency : 8;
            Volume = EnvelopeVolume;

            if (Length == 0) {
                Length = 64;

                if (((apu.Phase & 1) != 0) && Counter) {
                    Length -= 1;
                }
            }
        }

        public void Power(bool initializeLength) {
            Enable = false;

            Duty = 0;
            EnvelopeVolume = 0;
            EnvelopeDirection = false;
            EnvelopeFrequency = 0;
            Frequency = 0;
            Counter = false;

            Output = 0;
            DutyOutput = false;
            Phase = 0;
            Period = 0;
            EnvelopePeriod = 0;
            Volume = 0;

            if (initializeLength) {
                Length = 64;
            }
        }
    }

    private sealed class Wave {
        private static readonly int[] Shift = [4, 0, 1, 2]; // 0%, 100%, 50%, 25%

        public bool Enable;

        public bool DacEnable;
        public int Volume; // n2
        public int Frequency; // n11
        public bool Counter;
        public byte[] Pattern { get; } = new byte[16];

        public int Sample; // n4
        public short Output;
        public int Length;
        public int Period;
        public int PatternOffset; // n5
        public int PatternSample; // n4
        public int PatternHold;

        public int GetPattern(int offset) =>
            ((Pattern[(offset >> 1) & 0x0F] >> (((offset & 1) != 0) ? 0 : 4)) & 0x0F);

        public void Run(AresApu apu) {
            _ = apu;

            if (PatternHold != 0) {
                PatternHold -= 1;
            }

            if ((Period != 0) && (--Period == 0)) {
                Period = 2048 - Frequency;
                PatternOffset = (PatternOffset + 1) & 0x1F;
                PatternSample = GetPattern(offset: PatternOffset);
                PatternHold = 1;
            }

            Sample = (PatternSample >> Shift[Volume]);

            if (!Enable) {
                Sample = 0;
            }

            Output = (short)Sample;
        }

        public void ClockLength() {
            if (Counter) {
                if ((Length != 0) && (--Length == 0)) {
                    Enable = false;
                }
            }
        }

        public void Trigger(AresApu apu, bool color) {
            if (!color && (PatternHold != 0)) {
                // DMG/SGB trigger while the channel is being read corrupts wave RAM.
                if ((PatternOffset >> 1) <= 3) {
                    // If the current pattern is within 0-3, only byte 0 is corrupted.
                    Pattern[0] = Pattern[PatternOffset >> 1];
                }
                else {
                    // If the current pattern is within 4-15, pattern&~3 is copied to pattern[0-3].
                    Pattern[0] = Pattern[((PatternOffset >> 1) & ~3) + 0];
                    Pattern[1] = Pattern[((PatternOffset >> 1) & ~3) + 1];
                    Pattern[2] = Pattern[((PatternOffset >> 1) & ~3) + 2];
                    Pattern[3] = Pattern[((PatternOffset >> 1) & ~3) + 3];
                }
            }

            Enable = DacEnable;
            Period = 2048 - Frequency + 2;
            PatternOffset = 0;
            PatternSample = 0;
            PatternHold = 0;

            if (Length == 0) {
                Length = 256;

                if (((apu.Phase & 1) != 0) && Counter) {
                    Length -= 1;
                }
            }
        }

        public byte ReadRam(bool color, int address, byte data) {
            if (Enable) {
                if (!color && (PatternHold == 0)) {
                    return data;
                }

                return Pattern[(PatternOffset >> 1) & 0x0F];
            }

            return Pattern[address & 0x0F];
        }

        public void WriteRam(bool color, int address, byte data) {
            if (Enable) {
                if (!color && (PatternHold == 0)) {
                    return;
                }

                Pattern[(PatternOffset >> 1) & 0x0F] = data;
            }
            else {
                Pattern[address & 0x0F] = data;
            }
        }

        public void Power(bool initializeLength) {
            Enable = false;

            DacEnable = false;
            Volume = 0;
            Frequency = 0;
            Counter = false;

            Output = 0;
            Period = 0;
            PatternOffset = 0;
            PatternSample = 0;
            PatternHold = 0;

            if (initializeLength) {
                Length = 256;
            }
        }
    }

    private sealed class Noise {
        private static readonly int[] Table = [4, 8, 16, 24, 32, 40, 48, 56];

        public bool Enable;

        public int EnvelopeVolume; // n4
        public bool EnvelopeDirection;
        public int EnvelopeFrequency; // n3
        public int Frequency; // n4
        public bool Narrow;
        public int Divisor; // n3
        public bool Counter;

        public int Sample; // n4
        public short Output;
        public int Length;
        public int EnvelopePeriod; // n3
        public int Volume; // n4
        public int Period;
        public int Lfsr; // n15

        public bool DacEnable() =>
            (EnvelopeVolume != 0) || EnvelopeDirection;

        public int GetPeriod() =>
            (Table[Divisor] << Frequency);

        public void Run(AresApu apu) {
            _ = apu;

            if ((Period != 0) && (--Period == 0)) {
                Period = GetPeriod();

                if (Frequency < 14) {
                    var bit = ((Lfsr ^ (Lfsr >> 1)) & 1);

                    Lfsr = ((Lfsr >> 1) ^ (bit << (Narrow ? 6 : 14))) & 0x7FFF;
                }
            }

            Sample = ((Lfsr & 1) != 0) ? 0 : Volume;

            if (!Enable) {
                Sample = 0;
            }

            Output = (short)Sample;
        }

        public void ClockLength() {
            if (Counter) {
                if ((Length != 0) && (--Length == 0)) {
                    Enable = false;
                }
            }
        }

        public void ClockEnvelope() {
            if (Enable && (EnvelopeFrequency != 0) && (--EnvelopePeriod == 0)) {
                EnvelopePeriod = (EnvelopeFrequency != 0) ? EnvelopeFrequency : 8;

                if (!EnvelopeDirection && (Volume > 0)) { Volume -= 1; }
                if (EnvelopeDirection && (Volume < 15)) { Volume += 1; }
            }
        }

        public void Trigger(AresApu apu) {
            Enable = DacEnable();
            Lfsr = 0x7FFF;
            EnvelopePeriod = (EnvelopeFrequency != 0) ? EnvelopeFrequency : 8;
            Volume = EnvelopeVolume;

            if (Length == 0) {
                Length = 64;

                if (((apu.Phase & 1) != 0) && Counter) {
                    Length -= 1;
                }
            }
        }

        public void Power(bool initializeLength) {
            Enable = false;

            EnvelopeVolume = 0;
            EnvelopeDirection = false;
            EnvelopeFrequency = 0;
            Frequency = 0;
            Narrow = false;
            Divisor = 0;
            Counter = false;

            Output = 0;
            EnvelopePeriod = 0;
            Volume = 0;
            Period = 0;
            Lfsr = 0;

            if (initializeLength) {
                Length = 64;
            }
        }
    }

    private sealed class Sequencer {
        public bool LeftEnable;
        public int LeftVolume; // n3
        public bool RightEnable;
        public int RightVolume; // n3

        public Channel Square1 { get; } = new();
        public Channel Square2 { get; } = new();
        public Channel Wave { get; } = new();
        public Channel Noise { get; } = new();

        public bool Enable;

        public short Center;
        public short Left;
        public short Right;

        public void Run(AresApu apu) {
            if (!Enable) {
                Center = 0;
                Left = 0;
                Right = 0;

                return;
            }

            var sample = 0;

            sample += apu.m_square1.Output;
            sample += apu.m_square2.Output;
            sample += apu.m_wave.Output;
            sample += apu.m_noise.Output;
            Center = (short)((sample * 512) - 16384);

            sample = 0;
            if (Square1.LeftEnable) { sample += apu.m_square1.Output; }
            if (Square2.LeftEnable) { sample += apu.m_square2.Output; }
            if (Wave.LeftEnable) { sample += apu.m_wave.Output; }
            if (Noise.LeftEnable) { sample += apu.m_noise.Output; }
            sample = (sample * 512) - 16384;
            sample = (sample * (LeftVolume + 1)) / 8;
            Left = (short)sample;

            sample = 0;
            if (Square1.RightEnable) { sample += apu.m_square1.Output; }
            if (Square2.RightEnable) { sample += apu.m_square2.Output; }
            if (Wave.RightEnable) { sample += apu.m_wave.Output; }
            if (Noise.RightEnable) { sample += apu.m_noise.Output; }
            sample = (sample * 512) - 16384;
            sample = (sample * (RightVolume + 1)) / 8;
            Right = (short)sample;

            // Reduce audio volume.
            Center = (short)(Center >> 1);
            Left = (short)(Left >> 1);
            Right = (short)(Right >> 1);
        }

        public void Power() {
            LeftEnable = false;
            LeftVolume = 0;
            RightEnable = false;
            RightVolume = 0;
            Noise.LeftEnable = false;
            Wave.LeftEnable = false;
            Square2.LeftEnable = false;
            Square1.LeftEnable = false;
            Noise.RightEnable = false;
            Wave.RightEnable = false;
            Square2.RightEnable = false;
            Square1.RightEnable = false;
            Enable = false;

            Center = 0;
            Left = 0;
            Right = 0;
        }

        public sealed class Channel {
            public bool LeftEnable;
            public bool RightEnable;
        }
    }
}

namespace Puck.GameBoyAdvance;

/// <summary>
/// The default APU: the two square PSG channels plus the two Direct Sound FIFO channels, mixed per SOUNDCNT and
/// resampled into a drainable stereo ring. A 512&#160;Hz frame sequencer clocks the PSG length/envelope/sweep
/// units. The wave and noise PSG channels are registered but not yet sounded — a follow-up increment.
/// </summary>
public sealed class GbaApu : IGbaApu {
    private const int MasterClock = 16_777_216;
    private const int FrameSequencerPeriod = MasterClock / 512;

    private readonly ApuPulseChannel m_pulse1 = new(hasSweep: true);
    private readonly ApuPulseChannel m_pulse2 = new(hasSweep: false);
    private readonly byte[] m_waveRam = new byte[0x10];
    private readonly ApuWaveChannel m_wave;
    private readonly ApuNoiseChannel m_noise = new();
    private readonly Queue<sbyte> m_fifoA = new();
    private readonly Queue<sbyte> m_fifoB = new();

    private ushort m_soundControlLow;
    private ushort m_soundControlHigh;
    private bool m_masterEnable;
    private int m_frameSequencerTimer = FrameSequencerPeriod;
    private int m_frameSequencerStep;
    private int m_directSoundA;
    private int m_directSoundB;
    private bool m_fifoARefill;
    private bool m_fifoBRefill;

    private short[] m_outputRing = Array.Empty<short>();
    private int m_outputWrite;
    private int m_outputRead;
    private int m_cyclesPerSample;
    private int m_sampleTimer;

    /// <summary>Creates the APU, binding the wave channel to the shared wave RAM.</summary>
    public GbaApu() {
        m_wave = new ApuWaveChannel(waveRam: m_waveRam);
    }

    /// <inheritdoc/>
    public void ConfigureOutput(int sampleRate) {
        if (sampleRate <= 0) {
            m_outputRing = Array.Empty<short>();
            m_cyclesPerSample = 0;

            return;
        }

        m_outputRing = new short[sampleRate * 2]; // ~1 second of stereo headroom
        m_cyclesPerSample = MasterClock / sampleRate;
        m_sampleTimer = m_cyclesPerSample;
        m_outputWrite = 0;
        m_outputRead = 0;
    }

    /// <inheritdoc/>
    public int DrainSamples(Span<short> destination) {
        var written = 0;

        while ((written < destination.Length) && (m_outputRead != m_outputWrite)) {
            destination[written++] = m_outputRing[m_outputRead];
            m_outputRead = (m_outputRead + 1) % m_outputRing.Length;
        }

        return written;
    }

    /// <inheritdoc/>
    public void Step(int cycles) {
        m_pulse1.Step(cycles: cycles);
        m_pulse2.Step(cycles: cycles);
        m_wave.Step(cycles: cycles);
        m_noise.Step(cycles: cycles);

        m_frameSequencerTimer -= cycles;

        while (m_frameSequencerTimer <= 0) {
            m_frameSequencerTimer += FrameSequencerPeriod;

            ClockFrameSequencer();
        }

        if (m_cyclesPerSample > 0) {
            m_sampleTimer -= cycles;

            while (m_sampleTimer <= 0) {
                m_sampleTimer += m_cyclesPerSample;

                GenerateSample();
            }
        }
    }

    /// <inheritdoc/>
    public void OnTimerOverflow(int timer) {
        // SOUNDCNT_H bit 10 selects timer 0/1 for Direct Sound A; bit 14 for B.
        if ((((m_soundControlHigh >> 10) & 1) == timer) && (m_fifoA.Count > 0)) {
            m_directSoundA = m_fifoA.Dequeue();
        }

        if ((((m_soundControlHigh >> 14) & 1) == timer) && (m_fifoB.Count > 0)) {
            m_directSoundB = m_fifoB.Dequeue();
        }

        // A FIFO at half or less asks the DMA to top it up (handled by the bus, kept acyclic).
        if (m_fifoA.Count <= 16) {
            m_fifoARefill = true;
        }

        if (m_fifoB.Count <= 16) {
            m_fifoBRefill = true;
        }
    }

    /// <inheritdoc/>
    public bool ConsumeFifoARefill() {
        var requested = m_fifoARefill;

        m_fifoARefill = false;

        return requested;
    }

    /// <inheritdoc/>
    public bool ConsumeFifoBRefill() {
        var requested = m_fifoBRefill;

        m_fifoBRefill = false;

        return requested;
    }

    /// <inheritdoc/>
    public ushort ReadRegister(uint offset) {
        return offset switch {
            0x80u => m_soundControlLow,
            0x82u => m_soundControlHigh,
            0x84u => (ushort)((m_masterEnable ? 0x80u : 0u)
                | (m_pulse1.Active ? 0x1u : 0u)
                | (m_pulse2.Active ? 0x2u : 0u)
                | (m_wave.Active ? 0x4u : 0u)
                | (m_noise.Active ? 0x8u : 0u)),
            >= 0x90u and <= 0x9Fu => (ushort)(m_waveRam[offset - 0x90u] | (m_waveRam[(offset - 0x90u) + 1u] << 8)),
            _ => 0,
        };
    }

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value) {
        // Sound is fully writable only while the master enable is set, except for the enable itself.
        if (!m_masterEnable && (offset != 0x84u) && (offset < 0x90u)) {
            return;
        }

        switch (offset) {
            case 0x60u:
                m_pulse1.WriteSweep(value: value);

                break;
            case 0x62u:
                m_pulse1.WriteDutyLength(value: (byte)value);
                m_pulse1.WriteEnvelope(value: (byte)(value >> 8));

                break;
            case 0x64u:
                m_pulse1.WriteFrequencyLow(value: (byte)value);
                m_pulse1.WriteControl(value: (byte)(value >> 8));

                break;
            case 0x68u:
                m_pulse2.WriteDutyLength(value: (byte)value);
                m_pulse2.WriteEnvelope(value: (byte)(value >> 8));

                break;
            case 0x6Cu:
                m_pulse2.WriteFrequencyLow(value: (byte)value);
                m_pulse2.WriteControl(value: (byte)(value >> 8));

                break;
            case 0x70u:
                m_wave.WriteEnable(value: (byte)value);

                break;
            case 0x72u:
                m_wave.WriteLength(value: (byte)value);
                m_wave.WriteVolume(value: (byte)(value >> 8));

                break;
            case 0x74u:
                m_wave.WriteFrequencyLow(value: (byte)value);
                m_wave.WriteControl(value: (byte)(value >> 8));

                break;
            case 0x78u:
                m_noise.WriteLength(value: (byte)value);
                m_noise.WriteEnvelope(value: (byte)(value >> 8));

                break;
            case 0x7Cu:
                m_noise.WritePolynomial(value: (byte)value);
                m_noise.WriteControl(value: (byte)(value >> 8));

                break;
            case 0x80u:
                m_soundControlLow = value;

                break;
            case 0x82u:
                m_soundControlHigh = value;

                // Bits 11 and 15 reset the Direct Sound FIFOs.
                if ((value & 0x0800) != 0) {
                    m_fifoA.Clear();
                }

                if ((value & 0x8000) != 0) {
                    m_fifoB.Clear();
                }

                break;
            case 0x84u:
                m_masterEnable = (value & 0x80) != 0;

                break;
            case >= 0x90u and <= 0x9Fu:
                m_waveRam[offset - 0x90u] = (byte)value;
                m_waveRam[(offset - 0x90u) + 1u] = (byte)(value >> 8);

                break;
            case >= 0xA0u and <= 0xA3u:
                EnqueueFifo(fifo: m_fifoA, value: value);

                break;
            case >= 0xA4u and <= 0xA7u:
                EnqueueFifo(fifo: m_fifoB, value: value);

                break;
            default:
                break;
        }
    }

    private static void EnqueueFifo(Queue<sbyte> fifo, ushort value) {
        // The 16-byte FIFO holds 8-bit signed samples; a halfword write enqueues two.
        if (fifo.Count < 32) {
            fifo.Enqueue((sbyte)value);
            fifo.Enqueue((sbyte)(value >> 8));
        }
    }

    private void ClockFrameSequencer() {
        // 256 Hz length on even steps, 128 Hz sweep on steps 2/6, 64 Hz envelope on step 7.
        if ((m_frameSequencerStep & 1) == 0) {
            m_pulse1.ClockLength();
            m_pulse2.ClockLength();
            m_wave.ClockLength();
            m_noise.ClockLength();
        }

        if ((m_frameSequencerStep == 2) || (m_frameSequencerStep == 6)) {
            m_pulse1.ClockSweep();
        }

        if (m_frameSequencerStep == 7) {
            m_pulse1.ClockEnvelope();
            m_pulse2.ClockEnvelope();
            m_noise.ClockEnvelope();
        }

        m_frameSequencerStep = (m_frameSequencerStep + 1) & 7;
    }

    private void GenerateSample() {
        if (m_outputRing.Length == 0) {
            return;
        }

        short left = 0;
        short right = 0;

        if (m_masterEnable) {
            // PSG: sum the four channels (0–15 each), scaled by the SOUNDCNT_H PSG ratio (25/50/100%).
            var psgRatio = m_soundControlHigh & 0x3;
            var psgScale = (psgRatio == 0) ? 1 : (psgRatio == 1) ? 2 : 4;
            var psgRaw = m_pulse1.Output + m_pulse2.Output + m_wave.Output + m_noise.Output;

            // Centre the PSG amplitude (0–60 around 30) and add the Direct Sound channels (8-bit signed,
            // optional half volume).
            var mix = ((psgRaw - 30) * psgScale * 16)
                + (m_directSoundA << (((m_soundControlHigh & 0x4) != 0) ? 2 : 1))
                + (m_directSoundB << (((m_soundControlHigh & 0x8) != 0) ? 2 : 1));

            var clamped = (short)Math.Clamp(value: mix, min: -32768, max: 32767);

            left = clamped;
            right = clamped;
        }

        m_outputRing[m_outputWrite] = left;
        m_outputWrite = (m_outputWrite + 1) % m_outputRing.Length;
        m_outputRing[m_outputWrite] = right;
        m_outputWrite = (m_outputWrite + 1) % m_outputRing.Length;
    }
}

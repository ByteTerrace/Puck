namespace Puck.AdvancedGamingBrick;

/// <summary>
/// The default APU: all four PSG channels (pulse×2, wave, noise) plus two Direct Sound FIFO channels, mixed per
/// SOUNDCNT and resampled into a drainable stereo ring. A 512&#160;Hz frame sequencer clocks the PSG
/// length/envelope/sweep units.
/// </summary>
public sealed partial class AgbApu : IAgbApu {
    private const int MasterClock = 16_777_216;
    private const int FrameSequencerPeriod = MasterClock / 512;

    private readonly ApuPulseChannel m_pulse1 = new(hasSweep: true);
    private readonly ApuPulseChannel m_pulse2 = new(hasSweep: false);
    private readonly ApuWaveChannel m_wave = new();
    private readonly ApuNoiseChannel m_noise = new();
    private readonly DirectSoundFifo m_fifoA = new();
    private readonly DirectSoundFifo m_fifoB = new();

    private ushort m_soundControlLow;
    private ushort m_soundControlHigh;
    private bool m_masterEnable;

    // SOUNDBIAS (0x04000088): the DAC bias level + amplitude-resolution/sampling-rate field. It powers on at
    // 0x0200 (bias 0x100) and is independent of the master sound enable. The BIOS reads this during sound
    // init; returning 0 here (as an unimplemented register would) derails real games' boot — e.g. some commercial games.
    private ushort m_soundBias = 0x0200;
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
        // SOUNDCNT_H bit 10 selects timer 0/1 for Direct Sound A; bit 14 for B. The selected timer's overflow is the
        // FIFO's sample clock: it both requests a DMA top-up (when the ring has room) and advances the playing buffer
        // by one byte. The DMA request originating ONLY here is what upholds the hardware invariant that two DMA
        // requests can never occur without an intervening timer overflow (see DirectSoundFifo).
        if (((m_soundControlHigh >> 10) & 1) == timer) {
            if (m_fifoA.Tick(needsDma: out var dmaA, sample: out var sampleA)) {
                m_directSoundA = sampleA;
            }

            m_fifoARefill |= dmaA;
        }

        if (((m_soundControlHigh >> 14) & 1) == timer) {
            if (m_fifoB.Tick(needsDma: out var dmaB, sample: out var sampleB)) {
                m_directSoundB = sampleB;
            }

            m_fifoBRefill |= dmaB;
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
            0x60u => m_pulse1.ReadSweep(),
            0x62u => (ushort)(m_pulse1.ReadDutyLength() | (m_pulse1.ReadEnvelope() << 8)),
            0x64u => (ushort)(m_pulse1.ReadControl() << 8),
            0x68u => (ushort)(m_pulse2.ReadDutyLength() | (m_pulse2.ReadEnvelope() << 8)),
            0x6Cu => (ushort)(m_pulse2.ReadControl() << 8),
            0x78u => (ushort)(m_noise.ReadEnvelope() << 8),
            0x7Cu => (ushort)(m_noise.ReadPolynomial() | (m_noise.ReadControl() << 8)),
            0x80u => m_soundControlLow,
            0x82u => m_soundControlHigh,
            0x84u => (ushort)((m_masterEnable ? 0x80u : 0u)
                | (m_pulse1.Active ? 0x1u : 0u)
                | (m_pulse2.Active ? 0x2u : 0u)
                | (m_wave.Active ? 0x4u : 0u)
                | (m_noise.Active ? 0x8u : 0u)),
            0x88u => m_soundBias,
            0x70u => m_wave.ReadEnable(),
            0x72u => (ushort)(m_wave.ReadVolume() << 8),
            0x74u => (ushort)(m_wave.ReadControl() << 8),
            >= 0x90u and <= 0x9Fu => (ushort)(m_wave.ReadRam((int)(offset - 0x90u)) | (m_wave.ReadRam((int)(offset - 0x90u) + 1) << 8)),
            _ => 0,
        };
    }

    /// <inheritdoc/>
    public void WriteRegister(uint offset, ushort value) {
        // Sound is fully writable only while the master enable is set, except for the master enable (0x84) and
        // SOUNDBIAS (0x88), which are always accessible (they sit outside the gated PSG/FIFO control block).
        if (!m_masterEnable && (offset != 0x84u) && (offset != 0x88u) && (offset < 0x90u)) {
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

                // Bits 11 and 15 reset the Direct Sound FIFOs (ring + playing buffer both cleared).
                if ((value & 0x0800) != 0) {
                    m_fifoA.Reset();
                }

                if ((value & 0x8000) != 0) {
                    m_fifoB.Reset();
                }

                break;
            case 0x88u:
                m_soundBias = value;

                break;
            case 0x84u:
                m_masterEnable = (value & 0x80) != 0;

                break;
            case >= 0x90u and <= 0x9Fu:
                m_wave.WriteRam((int)(offset - 0x90u), (byte)value);
                m_wave.WriteRam((int)(offset - 0x90u) + 1, (byte)(value >> 8));

                break;
            case >= 0xA0u and <= 0xA3u:
                // A halfword register write streams two bytes into the fill word in write order.
                m_fifoA.WriteByte(value: (byte)value);
                m_fifoA.WriteByte(value: (byte)(value >> 8));

                break;
            case >= 0xA4u and <= 0xA7u:
                m_fifoB.WriteByte(value: (byte)value);
                m_fifoB.WriteByte(value: (byte)(value >> 8));

                break;
            default:
                break;
        }
    }

    /// <inheritdoc/>
    public void WriteFifoByte(int fifo, byte value) {
        // The 0xA0-0xA7 FIFO register windows accept 8/16/32-bit writes; the bus decomposes each to the exact bytes
        // it carries and streams them here in write order (a narrow write fills only part of the next word). A whole
        // word (four streamed bytes) is pushed into the ring; the common MP2K path is a 32-bit DMA fill.
        (fifo == 0 ? m_fifoA : m_fifoB).WriteByte(value: value);
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
            // SOUNDCNT_L (0x80): bits 0-2 right master volume, 4-6 left; bits 8-11 per-channel right enable,
            // 12-15 per-channel left enable (pulse1, pulse2, wave, noise). Each PSG channel outputs 0-15.
            var p1 = m_pulse1.Output;
            var p2 = m_pulse2.Output;
            var wv = m_wave.Output;
            var ns = m_noise.Output;

            var rightEnable = (m_soundControlLow >> 8) & 0xF;
            var leftEnable = (m_soundControlLow >> 12) & 0xF;

            var psgRight = (((rightEnable & 0x1) != 0) ? p1 : 0) + (((rightEnable & 0x2) != 0) ? p2 : 0)
                + (((rightEnable & 0x4) != 0) ? wv : 0) + (((rightEnable & 0x8) != 0) ? ns : 0);
            var psgLeft = (((leftEnable & 0x1) != 0) ? p1 : 0) + (((leftEnable & 0x2) != 0) ? p2 : 0)
                + (((leftEnable & 0x4) != 0) ? wv : 0) + (((leftEnable & 0x8) != 0) ? ns : 0);

            // SOUNDCNT_H bits 0-1: PSG mix ratio 25/50/100%. Master volume (0-7) scales as (vol+1)/8.
            var psgRatio = m_soundControlHigh & 0x3;
            var psgScale = (psgRatio == 0) ? 1 : (psgRatio == 1) ? 2 : 4; // 25% / 50% / 100% (×4 = full)
            var volRight = (m_soundControlLow & 0x7) + 1;
            var volLeft = ((m_soundControlLow >> 4) & 0x7) + 1;

            // PSG amplitude centred (each channel idles near 8, so subtract the enabled-channel midpoint),
            // scaled to 16-bit. Direct Sound A/B are 8-bit signed, panned by SOUNDCNT_H L/R enable bits, with
            // an optional half-volume (bits 2/3). Direct Sound is not affected by the PSG master volume.
            var psgMixRight = ((psgRight * 2) - CountBits(rightEnable) * 15) * psgScale * volRight;
            var psgMixLeft = ((psgLeft * 2) - CountBits(leftEnable) * 15) * psgScale * volLeft;

            var dsaShift = ((m_soundControlHigh & 0x4) != 0) ? 3 : 2; // 100% / 50%
            var dsbShift = ((m_soundControlHigh & 0x8) != 0) ? 3 : 2;
            var dsa = m_directSoundA << dsaShift;
            var dsb = m_directSoundB << dsbShift;

            var mixRight = psgMixRight
                + (((m_soundControlHigh & 0x0100) != 0) ? dsa : 0)
                + (((m_soundControlHigh & 0x1000) != 0) ? dsb : 0);
            var mixLeft = psgMixLeft
                + (((m_soundControlHigh & 0x0200) != 0) ? dsa : 0)
                + (((m_soundControlHigh & 0x2000) != 0) ? dsb : 0);

            // SOUNDBIAS adds a DC offset to the final mix on hardware; it nets out for line-level output, so we
            // clamp around zero. (The bias level field is honoured by games probing it; the audible effect is nil.)
            right = (short)Math.Clamp(value: mixRight, min: -32768, max: 32767);
            left = (short)Math.Clamp(value: mixLeft, min: -32768, max: 32767);
        }

        m_outputRing[m_outputWrite] = left;
        m_outputWrite = (m_outputWrite + 1) % m_outputRing.Length;
        m_outputRing[m_outputWrite] = right;
        m_outputWrite = (m_outputWrite + 1) % m_outputRing.Length;
    }

    private static int CountBits(int nibble) =>
        (nibble & 1) + ((nibble >> 1) & 1) + ((nibble >> 2) & 1) + ((nibble >> 3) & 1);

    /// <summary>The number of filled 32-bit words in a Direct Sound FIFO's ring (0-7) — a diagnostic peek that does
    /// not disturb state; <paramref name="fifo"/> is 0 (A) or 1 (B).</summary>
    public int DebugFifoWordCount(int fifo) => (fifo == 0 ? m_fifoA : m_fifoB).WordCount;

    /// <summary>The number of bytes remaining in a Direct Sound FIFO's 32-bit playing buffer (0-4) — a diagnostic
    /// peek; <paramref name="fifo"/> is 0 (A) or 1 (B).</summary>
    public int DebugFifoPlayingBytes(int fifo) => (fifo == 0 ? m_fifoA : m_fifoB).PlayingBytes;

    /// <summary>The 8-bit signed sample a Direct Sound channel is currently driving to the DAC (the last byte the
    /// playing buffer produced) — a diagnostic peek; <paramref name="fifo"/> is 0 (A) or 1 (B).</summary>
    public int DebugDirectSound(int fifo) => (fifo == 0) ? m_directSoundA : m_directSoundB;

    /// <summary>
    /// The hardware-measured Direct Sound FIFO: a 7-word (28-byte) ring buffer plus a separate 32-bit playing
    /// buffer (mGBA issue #1847). CPU/DMA writes stream bytes into the ring one word at a time; the selected timer's
    /// overflow both requests a DMA top-up (when the ring has &#8805;4 empty words) and, independently, refills the
    /// playing buffer from the ring when it empties. The DAC consumes one byte from the playing buffer per overflow.
    /// Integer-only and fully deterministic — no wall-clock, RNG, or float.
    /// </summary>
    private sealed class DirectSoundFifo {
        private const int RingWords = 7;

        private readonly uint[] m_ring = new uint[RingWords];
        private int m_head;     // index of the oldest filled word
        private int m_count;    // filled words in the ring (0..7)
        private uint m_fillWord;
        private int m_fillBytes; // bytes accumulated toward the next word (0..3)

        private uint m_playing;
        private int m_playingBytes; // bytes remaining in the 32-bit playing buffer (0..4)

        /// <summary>The number of filled words currently in the ring (0..7).</summary>
        public int WordCount => m_count;

        /// <summary>The bytes remaining in the playing buffer (0..4).</summary>
        public int PlayingBytes => m_playingBytes;

        /// <summary>Clears the whole FIFO — ring, fill accumulator, and playing buffer (a SOUNDCNT_H reset, or the
        /// auto-reset hardware performs on a write overrun).</summary>
        public void Reset() {
            Array.Clear(array: m_ring);
            m_head = 0;
            m_count = 0;
            m_fillWord = 0;
            m_fillBytes = 0;
            m_playing = 0;
            m_playingBytes = 0;
        }

        /// <summary>Streams one byte into the FIFO. Bytes accumulate in write order; every fourth byte completes a
        /// word and pushes it into the ring. Pushing into a full ring auto-resets the FIFO to empty (hardware drops
        /// the buffered samples rather than wrapping).</summary>
        public void WriteByte(byte value) {
            m_fillWord = (m_fillWord & ~(0xFFu << (m_fillBytes * 8))) | ((uint)value << (m_fillBytes * 8));

            if (++m_fillBytes < 4) {
                return;
            }

            var word = m_fillWord;

            m_fillWord = 0;
            m_fillBytes = 0;

            if (m_count >= RingWords) {
                // Write overrun: hardware auto-resets the FIFO to empty and drops the incoming word.
                Reset();

                return;
            }

            m_ring[(m_head + m_count) % RingWords] = word;
            ++m_count;
        }

        /// <summary>A selected-timer overflow. Requests a DMA top-up when the ring has &#8805;4 empty words; refills
        /// the playing buffer from the ring when the buffer is empty; then hands the DAC one byte. Returns whether a
        /// byte was produced (false on underrun — the caller holds the previous sample).</summary>
        /// <param name="needsDma">Set when the ring has room for a 4-word DMA burst.</param>
        /// <param name="sample">The 8-bit signed sample the DAC now plays (0 when none was produced).</param>
        /// <returns><see langword="true"/> when a sample was produced.</returns>
        public bool Tick(out bool needsDma, out sbyte sample) {
            // (a) The DMA request is evaluated on the pre-pop ring occupancy: >=4 empty words asks for a 4-word burst.
            needsDma = (RingWords - m_count) >= 4;

            // (b) Independently, an empty playing buffer pulls the next word from the ring.
            if ((m_playingBytes == 0) && (m_count > 0)) {
                m_playing = m_ring[m_head];
                m_head = (m_head + 1) % RingWords;
                --m_count;
                m_playingBytes = 4;
            }

            if (m_playingBytes > 0) {
                sample = (sbyte)(m_playing & 0xFFu);
                m_playing >>= 8;
                --m_playingBytes;

                return true;
            }

            sample = 0;

            return false;
        }

        /// <summary>Captures the whole FIFO — ring contents, cursors, fill accumulator, and playing buffer.</summary>
        public void SaveState(AgbStateWriter writer) {
            for (var i = 0; (i < RingWords); ++i) {
                writer.WriteUInt32(value: m_ring[i]);
            }

            writer.WriteInt32(value: m_head);
            writer.WriteInt32(value: m_count);
            writer.WriteUInt32(value: m_fillWord);
            writer.WriteInt32(value: m_fillBytes);
            writer.WriteUInt32(value: m_playing);
            writer.WriteInt32(value: m_playingBytes);
        }

        /// <summary>Restores the whole FIFO from <see cref="SaveState"/>'s image.</summary>
        public void LoadState(AgbStateReader reader) {
            for (var i = 0; (i < RingWords); ++i) {
                m_ring[i] = reader.ReadUInt32();
            }

            m_head = reader.ReadInt32();
            m_count = reader.ReadInt32();
            m_fillWord = reader.ReadUInt32();
            m_fillBytes = reader.ReadInt32();
            m_playing = reader.ReadUInt32();
            m_playingBytes = reader.ReadInt32();
        }
    }
}

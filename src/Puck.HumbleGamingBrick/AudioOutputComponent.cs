using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// The host audio output stage: a CPU-domain clocked consumer of the APU's per-channel digital outputs that mixes,
/// resamples, and buffers stereo frames for the host to drain through <see cref="IAudioSink"/>. It is registered
/// directly after the APU so each CPU T-cycle samples the channels <em>after</em> they have advanced for that cycle.
/// <para>
/// <b>Mix law.</b> Each side is the linear sum of the four channels' 0–15 digital outputs gated by its NR51 bits
/// (bits 0–3 route channels 1–4 right, bits 4–7 left), recentered around zero, then scaled by the side's NR50 master
/// volume as <c>(volume + 1) / 8</c>. NR50's VIN routing bits (3 and 7) are ignored — no cartridge audio input is
/// modelled. While the APU is powered off the mixer keeps running and emits exact silence (zero), and a channel whose
/// DAC is off contributes a constant zero to the sum, so its silence is a flat level rather than a click.
/// </para>
/// <para>
/// <b>DC handling.</b> All-integer midpoint-offset recentering: the gated sum is scaled to a 0–32768 span
/// (<c>sum × 512</c>) and the span's midpoint (16384) is subtracted, mapping the mixer's unipolar DAC range onto a
/// signed sample centred near zero. This stands in for the hardware's analog DC-blocking high-pass without any
/// floating point — a fixed-point RC filter would introduce a per-sample feedback state for little audible gain, so
/// the plain offset is the deliberate choice.
/// </para>
/// <para>
/// <b>Resampler.</b> An integer rational accumulator with zero drift: every CPU T-cycle adds the output rate weighted
/// by the T-cycle's width in half-dots (two at normal speed, one under Color double speed), and a frame is emitted
/// each time the accumulator crosses 8388608 (one emulated second of half-dots), subtracting rather than resetting.
/// At normal speed this is exactly "accumulator += rate per T-cycle, emit at 4194304" with both sides doubled, and
/// the half-dot weighting keeps the emission rate an exact rational of <em>real</em> emulated time straight through a
/// speed switch. The stage keeps emitting during stop mode — real time still passes, and the frozen mix is a flat,
/// pop-free level — so the host stream stays continuous.
/// </para>
/// <para>
/// <b>Snapshots.</b> Nothing here is emulated state: the ring, the accumulator, and the configured rate are
/// host-facing output plumbing that never feeds back into emulation, so <see cref="SaveState"/> writes nothing (the
/// snapshot stays bit-identical whatever the host's audio configuration) and <see cref="LoadState"/> clears the ring
/// and accumulator so a restored machine starts a fresh stream.
/// </para>
/// </summary>
public sealed class AudioOutputComponent : IAudioSink, IClockedComponent, ISnapshotable {
    private const int DotsPerSecond = 4_194_304;
    private const int HalfDotsPerSecond = (DotsPerSecond * 2);
    private const byte MasterPower = 0x80;
    private const int MixMidpoint = 16_384;
    private const int MixScale = 512;
    private const ushort Nr50Address = 0xFF24;
    private const ushort Nr51Address = 0xFF25;

    private readonly IApu m_apu;
    private readonly IKey1 m_key1;
    private int m_accumulator;
    private int m_capacityFrames;
    private int m_frameCount;
    private int m_readFrame;
    private short[] m_ring;
    private int m_sampleRate;
    private int m_writeFrame;

    /// <summary>Creates the output stage wired to the APU whose channel outputs it mixes and the speed unit that tells
    /// it how wide a CPU T-cycle is. Output starts disabled; the host opts in through <see cref="Configure"/>.</summary>
    /// <param name="apu">The audio processing unit, read for the channel outputs and the NR50/NR51/NR52 mix registers.</param>
    /// <param name="key1">The Color speed-switch unit, read for the current speed.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    public AudioOutputComponent(IApu apu, IKey1 key1) {
        ArgumentNullException.ThrowIfNull(argument: apu);
        ArgumentNullException.ThrowIfNull(argument: key1);

        m_apu = apu;
        m_key1 = key1;
        m_ring = [];
    }

    /// <inheritdoc/>
    public int AvailableSampleCount =>
        (m_frameCount * 2);
    /// <inheritdoc/>
    public ClockDomain Domain =>
        ClockDomain.Cpu;
    /// <inheritdoc/>
    public int SampleRate =>
        m_sampleRate;

    /// <inheritdoc/>
    public void Configure(int sampleRate) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: sampleRate);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: sampleRate, other: DotsPerSecond);

        m_accumulator = 0;
        m_capacityFrames = sampleRate;
        m_frameCount = 0;
        m_readFrame = 0;
        m_ring = ((sampleRate > 0) ? new short[(sampleRate * 2)] : []);
        m_sampleRate = sampleRate;
        m_writeFrame = 0;
    }
    /// <inheritdoc/>
    public int ReadSamples(Span<short> destination) {
        var frames = Math.Min(val1: (destination.Length / 2), val2: m_frameCount);

        for (var frame = 0; (frame < frames); ++frame) {
            var index = (m_readFrame * 2);

            destination[(frame * 2)] = m_ring[index];
            destination[((frame * 2) + 1)] = m_ring[(index + 1)];
            m_readFrame = ((m_readFrame + 1) % m_capacityFrames);
        }

        m_frameCount -= frames;

        return (frames * 2);
    }
    /// <inheritdoc/>
    public void Tick() {
        if (m_sampleRate == 0) {
            return;
        }

        // One CPU T-cycle is a whole dot at normal speed and half a dot under double speed; weighting the addend by
        // the T-cycle's half-dot width keeps the accumulator an exact function of real emulated time, so the output
        // rate is speed-invariant and a mid-run speed switch carries no drift or discontinuity.
        m_accumulator += (m_key1.IsDoubleSpeed ? m_sampleRate : (m_sampleRate << 1));

        while (m_accumulator >= HalfDotsPerSecond) {
            m_accumulator -= HalfDotsPerSecond;

            EmitFrame();
        }
    }
    /// <inheritdoc/>
    public void SaveState(StateWriter writer) {
        // Intentionally empty: the output stage carries no emulated state (see the class remarks), and writing
        // nothing keeps snapshots bit-identical regardless of the host's audio configuration.
        ArgumentNullException.ThrowIfNull(argument: writer);
    }
    /// <inheritdoc/>
    public void LoadState(StateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        // Nothing was saved; a restore resets the stream so a rewound machine does not replay stale output. The
        // configured sample rate is host configuration and survives untouched.
        m_accumulator = 0;
        m_frameCount = 0;
        m_readFrame = 0;
        m_writeFrame = 0;
    }

    // One side of the mix law: recenter the gated 0-60 channel sum around zero at the x512 scale, then apply the
    // side's NR50 volume as (volume + 1) / 8. The extremes land at -16384 and +14336, comfortably inside 16 bits.
    private static short MixSide(int gatedSum, int volume) =>
        (short)((((gatedSum * MixScale) - MixMidpoint) * (volume + 1)) / 8);

    // Sample the APU's live state into one stereo frame. Everything is read through the APU's side-effect-free
    // register surface: PCM12/34 pack the four channels' digital outputs (a disabled channel reads zero), and the
    // NR50/NR51 mix registers read back their raw bits.
    private void EmitFrame() {
        // Powered off, every DAC is off and the register file is cleared: the output is true silence, not the
        // recentered flat level a powered-but-silent mix produces.
        if ((m_apu.ReadRegister(address: MemoryMap.AudioMasterControl) & MasterPower) == 0) {
            PushFrame(left: 0, right: 0);

            return;
        }

        var pcm12 = m_apu.ReadPcm(address: MemoryMap.PcmAmplitude12);
        var pcm34 = m_apu.ReadPcm(address: MemoryMap.PcmAmplitude34);
        var nr50 = m_apu.ReadRegister(address: Nr50Address);
        var nr51 = m_apu.ReadRegister(address: Nr51Address);
        var channel1 = pcm12 & 0x0F;
        var channel2 = (pcm12 >> 4);
        var channel3 = pcm34 & 0x0F;
        var channel4 = (pcm34 >> 4);
        var left = 0;
        var right = 0;

        // NR51 routes each channel to the left (bits 4-7) and/or right (bits 0-3) sum.
        if ((nr51 & 0x10) != 0) { left += channel1; }
        if ((nr51 & 0x20) != 0) { left += channel2; }
        if ((nr51 & 0x40) != 0) { left += channel3; }
        if ((nr51 & 0x80) != 0) { left += channel4; }
        if ((nr51 & 0x01) != 0) { right += channel1; }
        if ((nr51 & 0x02) != 0) { right += channel2; }
        if ((nr51 & 0x04) != 0) { right += channel3; }
        if ((nr51 & 0x08) != 0) { right += channel4; }

        PushFrame(
            left: MixSide(gatedSum: left, volume: (nr50 >> 4) & 0x07),
            right: MixSide(gatedSum: right, volume: nr50 & 0x07)
        );
    }
    // Append one frame to the ring; when full, the oldest frame is dropped so the buffer always holds the newest
    // emulated second of audio (a stalled host loses the past, never the present).
    private void PushFrame(short left, short right) {
        if (m_frameCount == m_capacityFrames) {
            m_readFrame = ((m_readFrame + 1) % m_capacityFrames);
            --m_frameCount;
        }

        var index = (m_writeFrame * 2);

        m_ring[index] = left;
        m_ring[(index + 1)] = right;
        m_writeFrame = ((m_writeFrame + 1) % m_capacityFrames);
        ++m_frameCount;
    }
}

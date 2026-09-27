namespace Puck.HumbleGamingDeck;

/// <summary>
/// The presentation side of the APU: it mixes the five integer channel levels each CPU cycle through the APU's
/// nonlinear mixer, averages the mix over each output sample's span of CPU cycles, removes the DC offset with the
/// console's high-pass, and queues interleaved stereo samples for the host to drain. It is disabled until
/// <see cref="Configure"/> gives it a rate, and while disabled it does no work. None of it is machine state: it is not
/// snapshotted, and a machine runs identically with or without it.
/// https://www.nesdev.org/wiki/APU_Mixer
/// </summary>
public sealed class HgdAudioOutput {
    // The NTSC CPU clock is 19,687,500 cycles every 11 seconds.
    private const long CpuCyclesPerElevenSeconds = 19_687_500L;
    private const double HighPassHertz = 90.0;

    private static readonly float[] PulseTable = CreatePulseTable();
    private static readonly float[] TndTable = CreateTndTable();

    private StereoSampleRing m_ring;
    private RationalRateAccumulator m_phase;
    private int m_sampleRate;
    private double m_sum;
    private int m_count;
    private double m_highPassCoefficient;
    private double m_previousInput;
    private double m_previousOutput;

    /// <summary>Gets the configured output rate in frames per emulated second, or 0 while disabled.</summary>
    public int SampleRate => m_sampleRate;

    /// <summary>Configures the output rate, discarding queued samples; 0 disables the stage.</summary>
    /// <param name="sampleRate">The output rate in frames per emulated second, or 0.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sampleRate"/> is negative.</exception>
    public void Configure(int sampleRate) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: sampleRate);

        m_sampleRate = sampleRate;
        m_phase.Reset();
        m_sum = 0;
        m_count = 0;
        m_previousInput = 0;
        m_previousOutput = 0;
        if (sampleRate == 0) {
            m_ring.Configure(capacityFrames: 0);

            return;
        }

        m_ring.Configure(capacityFrames: (sampleRate / 2));
        m_highPassCoefficient = Math.Exp(d: (((-2.0 * Math.PI) * HighPassHertz) / sampleRate));
    }
    /// <summary>Mixes one CPU cycle's channel levels and emits any output sample that falls due.</summary>
    /// <param name="apu">The APU whose levels to mix.</param>
    public void Sample(HgdApu apu) {
        if (m_sampleRate == 0) {
            return;
        }

        m_sum += (PulseTable[(apu.PulseOneLevel + apu.PulseTwoLevel)] +
            TndTable[(((3 * apu.TriangleLevel) + (2 * apu.NoiseLevel)) + apu.DmcLevel)]);
        ++m_count;

        var due = m_phase.Advance(
            period: CpuCyclesPerElevenSeconds,
            weight: (11L * m_sampleRate)
        );

        if (due == 0) {
            return;
        }

        var input = (m_sum / m_count);

        m_sum = 0;
        m_count = 0;
        for (; (due > 0); --due) {
            var output = (m_highPassCoefficient * ((m_previousOutput + input) - m_previousInput));
            var sample = ((short)Math.Clamp(
                max: short.MaxValue,
                min: short.MinValue,
                value: Math.Round(a: (output * 30000.0))
            ));

            m_previousInput = input;
            m_previousOutput = output;
            m_ring.Push(
                left: sample,
                right: sample
            );
        }
    }
    /// <summary>Drains queued interleaved stereo samples.</summary>
    /// <param name="destination">The buffer to fill; its length should be a multiple of two.</param>
    /// <returns>The number of samples written, left and right counted separately.</returns>
    public int Read(Span<short> destination) =>
        m_ring.Read(destination: destination);
    /// <summary>Discards queued samples without changing the configured rate.</summary>
    public void Clear() =>
        m_ring.Clear();

    private static float[] CreatePulseTable() {
        var table = new float[31];

        for (var sum = 1; (sum < table.Length); ++sum) {
            table[sum] = ((float)(95.52 / ((8128.0 / sum) + 100.0)));
        }

        return table;
    }
    private static float[] CreateTndTable() {
        var table = new float[203];

        for (var sum = 1; (sum < table.Length); ++sum) {
            table[sum] = ((float)(163.67 / ((24329.0 / sum) + 100.0)));
        }

        return table;
    }
}

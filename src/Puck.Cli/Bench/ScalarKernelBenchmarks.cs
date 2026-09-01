using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Puck.Maths;

namespace Puck.Cli.Bench;

// The scalar multiply's two lanes: operands below 2^31 (the machine-word lane) against operands that force the
// Int128 product, plus the transcendentals whose kernels changed shape — Sqrt's nearest settle and Pow's Q32 exponent
// route — and the fused divide behind the planar and dual quotients.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class ScalarKernels {
    private const int Count = 1024;

    private FixedQ4816[] m_narrowLeft = [];
    private FixedQ4816[] m_narrowRight = [];
    private FixedQ4816[] m_wideLeft = [];
    private FixedQ4816[] m_wideRight = [];
    private FixedQ4816[] m_positive = [];
    private FixedQ4816[] m_exponents = [];
    private FixedComplex[] m_complexLeft = [];
    private FixedComplex[] m_complexRight = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_narrowLeft = new FixedQ4816[Count];
        m_narrowRight = new FixedQ4816[Count];
        m_wideLeft = new FixedQ4816[Count];
        m_wideRight = new FixedQ4816[Count];
        m_positive = new FixedQ4816[Count];
        m_exponents = new FixedQ4816[Count];
        m_complexLeft = new FixedComplex[Count];
        m_complexRight = new FixedComplex[Count];

        for (var i = 0; (i < Count); ++i) {
            m_narrowLeft[i] = FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng));
            m_narrowRight[i] = FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng));
            m_wideLeft[i] = FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng));
            m_wideRight[i] = FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng));
            m_positive[i] = FixedQ4816.FromRawBits(value: rng.NextInt64(maxValue: (1L << 40), minValue: 1L));
            // Fractional exponents inside the band |y·log2 x| < 16 so the exponential path runs rather than saturating.
            m_exponents[i] = FixedQ4816.FromRawBits(value: (rng.NextInt64(maxValue: (3L << 16), minValue: -(3L << 16)) | 1L));
            m_complexLeft[i] = new(Real: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)), Imaginary: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)));
            m_complexRight[i] = new(Real: FixedQ4816.FromRawBits(value: (Operands.NarrowRaw(rng: rng) | 1L)), Imaginary: FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)));
        }
    }
    [Benchmark(Baseline = true)]
    public long MultiplyNarrow() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= (m_narrowLeft[i] * m_narrowRight[i]).Value; }

        return sink;
    }
    [Benchmark]
    public long MultiplyWide() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= (m_wideLeft[i] * m_wideRight[i]).Value; }

        return sink;
    }
    [Benchmark]
    public long Sqrt() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= FixedQ4816.Sqrt(value: m_positive[i]).Value; }

        return sink;
    }
    [Benchmark]
    public long PowFractional() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= FixedQ4816.Pow(x: m_narrowRight[i].Value < 0L ? -m_narrowRight[i] : m_narrowRight[i], y: m_exponents[i]).Value; }

        return sink;
    }
    [Benchmark]
    public long SinCos() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) {
            var (sin, cos) = FixedQ4816.SinCos(angle: m_wideLeft[i]);

            sink ^= (sin.Value + cos.Value);
        }

        return sink;
    }
    [Benchmark]
    public long ComplexDivide() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= (m_complexLeft[i] / m_complexRight[i]).Real.Value; }

        return sink;
    }
}
// The per-body-per-tick rate integration at a 240 Hz time base: one tick per call, the shape the world server drives.
[MemoryDiagnoser]
public class RateAccumulation {
    private const int Count = 1024;

    private FixedRateAccumulator[] m_accumulators = [];
    private FixedQ4816[] m_rates = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_accumulators = new FixedRateAccumulator[Count];
        m_rates = new FixedQ4816[Count];

        for (var i = 0; (i < Count); ++i) {
            m_accumulators[i] = new FixedRateAccumulator(ticksPerSecond: 50_400L);
            m_rates[i] = FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng));
        }
    }
    [Benchmark]
    public long IntegrateOneTick() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= m_accumulators[i].Integrate(elapsedTicks: 1UL, ratePerSecond: m_rates[i]).Value; }

        return sink;
    }
}

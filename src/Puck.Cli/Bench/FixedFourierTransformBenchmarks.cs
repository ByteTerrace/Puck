using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Puck.Maths;

namespace Puck.Cli.Bench;

// FixedFourierTransform.Forward's radix-2 butterfly network (O(N log N)) against the direct O(N^2) DFT sum built
// from the SAME FixedComplex kernel (the fft.radix2-vs-direct-sum law's reference), at a length small enough for the
// direct sum to finish in reasonable time. A fixed-point kernel, so the disassembler rides along.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class FftForwardVsDirectSum {
    private const int Length = 128;

    private FixedComplex[] m_input = [];
    private FixedComplex[] m_scratch = [];
    private FixedFourierPlan m_plan = null!;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_plan = FixedFourierPlan.Create(length: Length);
        m_input = new FixedComplex[Length];
        m_scratch = new FixedComplex[Length];

        for (var i = 0; (i < Length); ++i) {
            m_input[i] = new(Real: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)), Imaginary: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)));
        }
    }
    [Benchmark(Baseline = true)]
    public long Direct() {
        var turn = ((-2.0 * Math.PI) / Length);
        var sink = 0L;

        for (var k = 0; (k < Length); ++k) {
            var sum = FixedComplex.AdditiveIdentity;

            for (var n = 0; (n < Length); ++n) {
                var twiddle = FixedComplex.FromAngle(angle: FixedQ4816.FromDouble(value: ((turn * k) * n)));

                sum += (m_input[n] * twiddle);
            }

            sink ^= sum.Real.Value;
        }

        return sink;
    }
    [Benchmark]
    public FixedQ4816 Radix2() {
        m_input.CopyTo(array: m_scratch, index: 0);
        FixedFourierTransform.Forward(plan: m_plan, values: m_scratch);

        return m_scratch[0].Real;
    }
}
// Forward and Inverse latency alone, at a length representative of a small audio block and one representative of a
// large one.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class FftForwardInverse {
    private FixedComplex[] m_smallValues = [];
    private FixedComplex[] m_largeValues = [];
    private FixedFourierPlan m_smallPlan = null!;
    private FixedFourierPlan m_largePlan = null!;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_smallPlan = FixedFourierPlan.Create(length: 256);
        m_largePlan = FixedFourierPlan.Create(length: 16384);
        m_smallValues = new FixedComplex[256];
        m_largeValues = new FixedComplex[16384];

        for (var i = 0; (i < m_smallValues.Length); ++i) { m_smallValues[i] = new(Real: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)), Imaginary: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng))); }
        for (var i = 0; (i < m_largeValues.Length); ++i) { m_largeValues[i] = new(Real: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)), Imaginary: FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng))); }
    }
    [Benchmark]
    public FixedQ4816 ForwardSmall() {
        FixedFourierTransform.Forward(plan: m_smallPlan, values: m_smallValues);

        return m_smallValues[0].Real;
    }
    [Benchmark]
    public FixedQ4816 InverseSmall() {
        FixedFourierTransform.Inverse(plan: m_smallPlan, values: m_smallValues);

        return m_smallValues[0].Real;
    }
    [Benchmark]
    public FixedQ4816 ForwardLarge() {
        FixedFourierTransform.Forward(plan: m_largePlan, values: m_largeValues);

        return m_largeValues[0].Real;
    }
    [Benchmark]
    public FixedQ4816 InverseLarge() {
        FixedFourierTransform.Inverse(plan: m_largePlan, values: m_largeValues);

        return m_largeValues[0].Real;
    }
}

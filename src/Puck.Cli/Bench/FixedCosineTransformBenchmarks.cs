using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using Puck.Maths;

namespace Puck.Cli.Bench;

// FixedCosineTransform.Forward's Fourier route (fold, one FFT, one post-twiddle per bin; O(N log N)) against the
// direct O(N^2) DCT-II sum built from the SAME FixedQ4816.SinCos kernel (the dct.forward-vs-direct-sum law's
// reference), at a length small enough for the direct sum to finish in reasonable time. A fixed-point kernel, so the
// disassembler rides along.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class DctForwardVsDirectSum {
    private const int Length = 128;

    private FixedQ4816[] m_input = [];
    private FixedQ4816[] m_scratch = [];
    private FixedComplex[] m_complexScratch = [];
    private FixedCosineTransformPlan m_plan = null!;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_plan = FixedCosineTransformPlan.Create(length: Length);
        m_input = new FixedQ4816[Length];
        m_scratch = new FixedQ4816[Length];
        m_complexScratch = new FixedComplex[Length];

        for (var i = 0; (i < Length); ++i) { m_input[i] = FixedQ4816.FromRawBits(value: Operands.WideRaw(rng: rng)); }
    }
    [Benchmark(Baseline = true)]
    public long Direct() {
        var sink = 0L;

        for (var k = 0; (k < Length); ++k) {
            var sum = FixedQ4816.Zero;

            for (var n = 0; (n < Length); ++n) {
                sum += (m_input[n] * FixedQ4816.Cos(angle: FixedQ4816.FromDouble(value: ((Math.PI * ((2 * n) + 1) * k) / (2.0 * Length)))));
            }

            sink ^= sum.Value;
        }

        return sink;
    }
    [Benchmark]
    public FixedQ4816 FourierRoute() {
        m_input.CopyTo(array: m_scratch, index: 0);
        FixedCosineTransform.Forward(plan: m_plan, scratch: m_complexScratch, values: m_scratch);

        return m_scratch[0];
    }
}
// Forward and Inverse latency alone, at a length representative of a small audio block and one representative of a
// large one.
[MemoryDiagnoser]
[DisassemblyDiagnoser(maxDepth: 3)]
public class DctForwardInverse {
    private FixedQ4816[] m_smallValues = [];
    private FixedQ4816[] m_largeValues = [];
    private FixedComplex[] m_smallScratch = [];
    private FixedComplex[] m_largeScratch = [];
    private FixedCosineTransformPlan m_smallPlan = null!;
    private FixedCosineTransformPlan m_largePlan = null!;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_smallPlan = FixedCosineTransformPlan.Create(length: 256);
        m_largePlan = FixedCosineTransformPlan.Create(length: 16384);
        m_smallValues = new FixedQ4816[256];
        m_largeValues = new FixedQ4816[16384];
        m_smallScratch = new FixedComplex[256];
        m_largeScratch = new FixedComplex[16384];

        for (var i = 0; (i < m_smallValues.Length); ++i) { m_smallValues[i] = FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)); }
        for (var i = 0; (i < m_largeValues.Length); ++i) { m_largeValues[i] = FixedQ4816.FromRawBits(value: Operands.NarrowRaw(rng: rng)); }
    }
    [Benchmark]
    public FixedQ4816 ForwardSmall() {
        FixedCosineTransform.Forward(plan: m_smallPlan, scratch: m_smallScratch, values: m_smallValues);

        return m_smallValues[0];
    }
    [Benchmark]
    public FixedQ4816 InverseSmall() {
        FixedCosineTransform.Inverse(plan: m_smallPlan, scratch: m_smallScratch, values: m_smallValues);

        return m_smallValues[0];
    }
    [Benchmark]
    public FixedQ4816 ForwardLarge() {
        FixedCosineTransform.Forward(plan: m_largePlan, scratch: m_largeScratch, values: m_largeValues);

        return m_largeValues[0];
    }
    [Benchmark]
    public FixedQ4816 InverseLarge() {
        FixedCosineTransform.Inverse(plan: m_largePlan, scratch: m_largeScratch, values: m_largeValues);

        return m_largeValues[0];
    }
}

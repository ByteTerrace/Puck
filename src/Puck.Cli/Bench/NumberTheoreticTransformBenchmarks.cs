using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// NumberTheoreticTransform.Convolve (forward-forward-pointwise-inverse, O(N log N)) against the O(N^2) definition
// computed directly over PrimeField64, at a length large enough for the asymptotic gap to show. Modular-ulong
// arithmetic, not a fixed-point kernel, so MemoryDiagnoser only (no disassembler).
[MemoryDiagnoser]
public class NttConvolveVsNaive {
    private const int Length = 1024;

    private ulong[] m_left = [];
    private ulong[] m_right = [];
    private ulong[] m_scratchLeft = [];
    private ulong[] m_scratchRight = [];
    private ulong[] m_destination = [];
    private NumberTheoreticTransformPlan m_plan = null!;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_plan = NumberTheoreticTransformPlan.Create(length: Length);
        m_left = new ulong[Length];
        m_right = new ulong[Length];
        m_scratchLeft = new ulong[Length];
        m_scratchRight = new ulong[Length];
        m_destination = new ulong[Length];

        for (var i = 0; (i < Length); ++i) {
            m_left[i] = NumberTheoreticTransform.Field.Reduce(value: ((ulong)rng.NextInt64(maxValue: 1_000_000L)));
            m_right[i] = NumberTheoreticTransform.Field.Reduce(value: ((ulong)rng.NextInt64(maxValue: 1_000_000L)));
        }
    }
    [Benchmark(Baseline = true)]
    public ulong Naive() {
        var field = NumberTheoreticTransform.Field;
        var sink = 0UL;

        for (var k = 0; (k < Length); ++k) {
            var sum = 0UL;

            for (var i = 0; (i < Length); ++i) {
                var j = ((((k - i) % Length) + Length) % Length);

                sum = field.Add(left: sum, right: field.Multiply(left: m_left[i], right: m_right[j]));
            }

            sink ^= sum;
        }

        return sink;
    }
    [Benchmark]
    public ulong Ntt() {
        m_left.CopyTo(array: m_scratchLeft, index: 0);
        m_right.CopyTo(array: m_scratchRight, index: 0);
        NumberTheoreticTransform.Convolve(destination: m_destination, left: m_scratchLeft, plan: m_plan, right: m_scratchRight);

        return m_destination[0];
    }
}
// Forward and Inverse latency alone (no naive sibling — an O(N log N) butterfly has none), at a length representative
// of a small audio block and one representative of a large one.
[MemoryDiagnoser]
public class NttForwardInverse {
    private ulong[] m_smallValues = [];
    private ulong[] m_largeValues = [];
    private NumberTheoreticTransformPlan m_smallPlan = null!;
    private NumberTheoreticTransformPlan m_largePlan = null!;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_smallPlan = NumberTheoreticTransformPlan.Create(length: 256);
        m_largePlan = NumberTheoreticTransformPlan.Create(length: 16384);
        m_smallValues = new ulong[256];
        m_largeValues = new ulong[16384];

        for (var i = 0; (i < m_smallValues.Length); ++i) { m_smallValues[i] = ((ulong)rng.NextInt64(maxValue: 1_000_000L)); }
        for (var i = 0; (i < m_largeValues.Length); ++i) { m_largeValues[i] = ((ulong)rng.NextInt64(maxValue: 1_000_000L)); }
    }
    [Benchmark]
    public ulong ForwardSmall() {
        NumberTheoreticTransform.Forward(plan: m_smallPlan, values: m_smallValues);

        return m_smallValues[0];
    }
    [Benchmark]
    public ulong InverseSmall() {
        NumberTheoreticTransform.Inverse(plan: m_smallPlan, values: m_smallValues);

        return m_smallValues[0];
    }
    [Benchmark]
    public ulong ForwardLarge() {
        NumberTheoreticTransform.Forward(plan: m_largePlan, values: m_largeValues);

        return m_largeValues[0];
    }
    [Benchmark]
    public ulong InverseLarge() {
        NumberTheoreticTransform.Inverse(plan: m_largePlan, values: m_largeValues);

        return m_largeValues[0];
    }
}

using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// WalshHadamardTransform.Forward's add/subtract network (O(N log N)) against the O(N^2) definition-form sum with the
// sign read from popcount parity, at a length large enough for the asymptotic gap to show. Integer arithmetic, not a
// fixed-point kernel, so MemoryDiagnoser only (no disassembler).
[MemoryDiagnoser]
public class WhtForwardVsNaive {
    private const int Length = 1024;

    private long[] m_input = [];
    private long[] m_scratch = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_input = new long[Length];
        m_scratch = new long[Length];

        for (var i = 0; (i < Length); ++i) { m_input[i] = Operands.WideRaw(rng: rng); }
    }
    [Benchmark(Baseline = true)]
    public long Naive() {
        var sink = 0L;

        for (var k = 0; (k < Length); ++k) {
            var sum = 0L;

            for (var n = 0; (n < Length); ++n) {
                sum += ((0 == (System.Numerics.BitOperations.PopCount(value: ((uint)(n & k))) & 1)) ? m_input[n] : -m_input[n]);
            }

            sink ^= sum;
        }

        return sink;
    }
    [Benchmark]
    public long Network() {
        m_input.CopyTo(array: m_scratch, index: 0);
        WalshHadamardTransform.Forward<long>(values: m_scratch);

        return m_scratch[0];
    }
}
// Forward and Inverse latency alone over the long carrier, at a length representative of a small block and one
// representative of a large one.
[MemoryDiagnoser]
public class WhtForwardInverse {
    private long[] m_smallValues = [];
    private long[] m_largeValues = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_smallValues = new long[256];
        m_largeValues = new long[16384];

        for (var i = 0; (i < m_smallValues.Length); ++i) { m_smallValues[i] = Operands.NarrowRaw(rng: rng); }
        for (var i = 0; (i < m_largeValues.Length); ++i) { m_largeValues[i] = Operands.NarrowRaw(rng: rng); }
    }
    [Benchmark]
    public long ForwardSmall() {
        WalshHadamardTransform.Forward<long>(values: m_smallValues);

        return m_smallValues[0];
    }
    [Benchmark]
    public long InverseSmall() {
        WalshHadamardTransform.Inverse<long>(values: m_smallValues);

        return m_smallValues[0];
    }
    [Benchmark]
    public long ForwardLarge() {
        WalshHadamardTransform.Forward<long>(values: m_largeValues);

        return m_largeValues[0];
    }
    [Benchmark]
    public long InverseLarge() {
        WalshHadamardTransform.Inverse<long>(values: m_largeValues);

        return m_largeValues[0];
    }
}

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
    private long[] m_smallForwardInput = [];
    private long[] m_largeForwardInput = [];
    private long[] m_smallInverseInput = [];
    private long[] m_largeInverseInput = [];
    private long[] m_smallValues = [];
    private long[] m_largeValues = [];

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_smallForwardInput = new long[256];
        m_largeForwardInput = new long[16384];
        m_smallValues = new long[m_smallForwardInput.Length];
        m_largeValues = new long[m_largeForwardInput.Length];

        for (var i = 0; (i < m_smallForwardInput.Length); ++i) { m_smallForwardInput[i] = Operands.NarrowRaw(rng: rng); }
        for (var i = 0; (i < m_largeForwardInput.Length); ++i) { m_largeForwardInput[i] = Operands.NarrowRaw(rng: rng); }

        m_smallInverseInput = ((long[])m_smallForwardInput.Clone());
        m_largeInverseInput = ((long[])m_largeForwardInput.Clone());
        WalshHadamardTransform.Forward<long>(values: m_smallInverseInput);
        WalshHadamardTransform.Forward<long>(values: m_largeInverseInput);
    }
    [IterationSetup(Target = nameof(ForwardSmall))]
    public void ResetForwardSmall() => m_smallForwardInput.CopyTo(array: m_smallValues, index: 0);
    [IterationSetup(Target = nameof(InverseSmall))]
    public void ResetInverseSmall() => m_smallInverseInput.CopyTo(array: m_smallValues, index: 0);
    [IterationSetup(Target = nameof(ForwardLarge))]
    public void ResetForwardLarge() => m_largeForwardInput.CopyTo(array: m_largeValues, index: 0);
    [IterationSetup(Target = nameof(InverseLarge))]
    public void ResetInverseLarge() => m_largeInverseInput.CopyTo(array: m_largeValues, index: 0);
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

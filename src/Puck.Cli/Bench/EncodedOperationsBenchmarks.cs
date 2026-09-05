using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Puck.Maths;

namespace Puck.Cli.Bench;

// Mixed small and wide coordinates, in a fixed shuffled order. Each return consumes every result.
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class EncodedOperations {
    private const int Count = 1024;
    private HexagonalIndex[] m_hex = [];
    private ulong[] m_square = [];

    [GlobalSetup]
    public void Setup() {
        var random = new Random(Operands.Seed);
        m_hex = new HexagonalIndex[Count];
        m_square = new ulong[Count];
        for (var i = 0; i < Count; ++i) {
            var bound = (i & 1) == 0 ? 64 : 100_000_000;
            m_hex[i] = HexagonalIndex.FromCoordinate(new(Q: random.Next(-bound, bound), R: random.Next(-bound, bound)));
            m_square[i] = ((uint)random.Next(bound)).ElegantPair<uint, ulong>((uint)random.Next(bound));
        }
        if (HexRadiusGeneral() != HexRadiusDirect() || HexNormDecoded() != HexNormDirect() || HexSwapDecoded() != HexSwapDirect()
            || HexScaleDecoded() != HexScaleDirect() || HexTranslateDecoded() != HexTranslateDirect()
            || SquareSwapDecoded() != SquareSwapDirect() || SquareScaleDecoded() != SquareScaleDirect()
            || SquareTranslateDecoded() != SquareTranslateDirect() || SquareSumDecoded() != SquareSumDirect()) {
            throw new InvalidOperationException("Encoded-operation benchmark baselines disagree.");
        }
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("HexRadius")]
    public long HexRadiusGeneral() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { sink ^= LayerSequence.CenteredHexagonal.LayerOf(m_hex[i].Value); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("HexRadius")]
    public long HexRadiusDirect() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { sink ^= m_hex[i].Radius; }
        return sink;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("HexNorm")]
    public long HexNormDecoded() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { var c = m_hex[i].ToCoordinate(); var q = (long)c.Q; var r = (long)c.R; sink ^= (q * q) - (q * r) + (r * r); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("HexNorm")]
    public long HexNormDirect() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { sink ^= m_hex[i].Norm; }
        return sink;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("HexSwap")]
    public long HexSwapDecoded() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { var c = m_hex[i].ToCoordinate(); sink ^= HexagonalIndex.FromCoordinate(new(Q: c.R, R: c.Q)).Value; }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("HexSwap")]
    public long HexSwapDirect() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { sink ^= m_hex[i].Swap().Value; }
        return sink;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("HexScale")]
    public long HexScaleDecoded() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { sink ^= HexagonalIndex.FromCoordinate(m_hex[i].ToCoordinate() * 3).Value; }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("HexScale")]
    public long HexScaleDirect() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { sink ^= m_hex[i].Scale(3).Value; }
        return sink;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("HexTranslate")]
    public long HexTranslateDecoded() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { sink ^= HexagonalIndex.FromCoordinate(m_hex[i].ToCoordinate() + new HexagonalCoordinate(3, 3)).Value; }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("HexTranslate")]
    public long HexTranslateDirect() {
        var sink = 0L;
        for (var i = 0; i < Count; ++i) { sink ^= m_hex[i].Translate(new(3, 3)).Value; }
        return sink;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("SquareSwap")]
    public ulong SquareSwapDecoded() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { var (x, y) = m_square[i].ElegantUnpair<ulong, uint>(); sink ^= y.ElegantPair<uint, ulong>(x); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("SquareSwap")]
    public ulong SquareSwapDirect() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { sink ^= m_square[i].ElegantSwap(); }
        return sink;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("SquareScale")]
    public ulong SquareScaleDecoded() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { var (x, y) = m_square[i].ElegantUnpair<ulong, uint>(); sink ^= checked(x * 3).ElegantPair<uint, ulong>(checked(y * 3)); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("SquareScale")]
    public ulong SquareScaleDirect() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { sink ^= m_square[i].ElegantScale(3UL); }
        return sink;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("SquareTranslate")]
    public ulong SquareTranslateDecoded() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { var (x, y) = m_square[i].ElegantUnpair<ulong, uint>(); sink ^= checked(x + 3).ElegantPair<uint, ulong>(checked(y + 3)); }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("SquareTranslate")]
    public ulong SquareTranslateDirect() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { sink ^= m_square[i].ElegantTranslate(3UL); }
        return sink;
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = Count), BenchmarkCategory("SquareSum")]
    public ulong SquareSumDecoded() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { var (x, y) = m_square[i].ElegantUnpair<ulong, uint>(); sink ^= (ulong)x + y; }
        return sink;
    }

    [Benchmark(OperationsPerInvoke = Count), BenchmarkCategory("SquareSum")]
    public ulong SquareSumDirect() {
        var sink = 0UL;
        for (var i = 0; i < Count; ++i) { sink ^= m_square[i].ElegantSum(); }
        return sink;
    }
}

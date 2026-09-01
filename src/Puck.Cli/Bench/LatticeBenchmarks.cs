using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// The two noise samplers at a field-fill shape (one seed, many positions), the hex-grid distance a pathfinder pays per
// neighbour, the modular cusp action, and a segmented sieve window — the machine-word paths that replaced wide or
// per-bit work.
[MemoryDiagnoser]
public class LatticeKernels {
    private const int Count = 4096;

    private FixedVector3[] m_positions = [];
    private HexagonalCoordinate[] m_cells = [];
    private (long Numerator, long Denominator)[] m_cusps = [];
    private ModularTransform m_word;

    [GlobalSetup]
    public void Setup() {
        var rng = new Random(Seed: Operands.Seed);

        m_positions = new FixedVector3[Count];
        m_cells = new HexagonalCoordinate[Count];
        m_cusps = new (long, long)[Count];

        for (var i = 0; (i < Count); ++i) {
            m_positions[i] = new(X: FixedQ4816.FromRawBits(value: rng.NextInt64(maxValue: (1L << 30), minValue: -(1L << 30))), Y: FixedQ4816.FromRawBits(value: rng.NextInt64(maxValue: (1L << 30), minValue: -(1L << 30))), Z: FixedQ4816.FromRawBits(value: rng.NextInt64(maxValue: (1L << 30), minValue: -(1L << 30))));
            m_cells[i] = new(Q: rng.Next(maxValue: 1 << 20, minValue: -(1 << 20)), R: rng.Next(maxValue: 1 << 20, minValue: -(1 << 20)));
            m_cusps[i] = (rng.NextInt64(maxValue: 1L << 20, minValue: -(1L << 20)), rng.NextInt64(maxValue: 1L << 20, minValue: 1L));
        }

        m_word = (ModularTransform.T * ModularTransform.S * ModularTransform.T * ModularTransform.T);
    }
    [Benchmark]
    public long FieldNoiseSample() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= FieldNoise.Sample(position: m_positions[i], seed: 42UL).Value; }

        return sink;
    }
    [Benchmark]
    public long FieldNoiseFourOctaves() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= FieldNoise.Sample(octaves: 4, position: m_positions[i], seed: 42UL).Value; }

        return sink;
    }
    [Benchmark]
    public long LatticeValueNoise() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= Pcg3dLatticeNoise.ValueNoise01(cellX: i, cellZ: (Count - i), noiseCells: 37, seed: 7U).Value; }

        return sink;
    }
    [Benchmark]
    public long HexDistance() {
        var sink = 0L;

        for (var i = 1; (i < Count); ++i) { sink += HexagonalCoordinate.Distance(left: m_cells[i], right: m_cells[(i - 1)]); }

        return sink;
    }
    [Benchmark]
    public long ModularCusp() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) {
            var (numerator, denominator) = m_word.Apply(denominator: m_cusps[i].Denominator, numerator: m_cusps[i].Numerator);

            sink ^= (numerator + denominator);
        }

        return sink;
    }
    [Benchmark]
    public long SieveWindow() {
        var count = 0L;

        NumberTheoryFunctions.SegmentedPrimeSieve(high: (1UL << 32) + (1UL << 20), low: (1UL << 32), onPrime: _ => ++count);

        return count;
    }
}

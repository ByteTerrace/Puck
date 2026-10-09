using BenchmarkDotNet.Attributes;
using Puck.Maths;

namespace Puck.Cli.Bench;

// Runtime descriptors exercise the abstraction without relying on constant propagation from a named preset.
[MemoryDiagnoser]
public class LayerSequenceQueries {
    private const int Count = 1024;

    private LayerSequence m_sequence;
    private long[] m_indices = [];

    [Params("HexMixed", "HexFull", "SquareFull", "TriangularFull", "Linear", "Shrinking", "Wide")]
    public string Shape { get; set; } = "HexMixed";

    [GlobalSetup]
    public void Setup() {
        m_sequence = Shape switch {
            "HexMixed" or "HexFull" => LayerSequence.CenteredHexagonal,
            "SquareFull" => LayerSequence.Square,
            "TriangularFull" => LayerSequence.Triangular,
            "Linear" => LayerSequence.Linear(
            seed: 3,
            size: 7
        ),
            "Shrinking" => LayerSequence.Create(
            seed: 1,
            start: 1_000_000,
            step: -2
        ),
            "Wide" => LayerSequence.Create(
            seed: 11,
            start: (1L << 40),
            step: 3
        ),
            _ => throw new InvalidOperationException(message: Shape),
        };
        var random = new Random(Seed: Operands.Seed);

        m_indices = new long[Count];
        for (var i = 0; (i < Count); ++i) {
            var limit = ((Shape == "HexMixed")
                ? (((i & 1) == 0)
                    ? 12_289L
                    : 120_000_000_000_000_001L)
                : m_sequence.Capacity
            );

            m_indices[i] = random.NextInt64(maxValue: limit);
            var location = m_sequence.Locate(index: m_indices[i]);

            if (
                (location.Layer != m_sequence.LayerOf(index: m_indices[i])) ||
                (((location.Layer == 0)
                ? location.Offset
                : (m_sequence.Count(layerCount: (location.Layer - 1)) + location.Offset)) != m_indices[i])
            ) {
                throw new InvalidOperationException(message: "Layer query benchmark setup disagrees.");
            }
        }
    }
    [Benchmark(OperationsPerInvoke = Count)]
    public long LayerOf() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { sink ^= m_sequence.LayerOf(index: m_indices[i]); }
        return sink;
    }
    [Benchmark(OperationsPerInvoke = Count)]
    public long Locate() {
        var sink = 0L;

        for (var i = 0; (i < Count); ++i) { var location = m_sequence.Locate(index: m_indices[i]); sink ^= location.Layer ^ location.Offset; }
        return sink;
    }
}

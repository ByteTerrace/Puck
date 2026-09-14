using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? LayerSequenceFullRange() {
        var shapes = new List<(long Start, long Step, long Seed)>();

        foreach (var seed in new long[] { 0, 1, ((1L << 62) - 1) }) {
            foreach (var step in new long[] { 1, 2, 3, 4, 5, 6, 8, 12, (1L << 32) }) { shapes.Add(item: (step, step, seed)); }
            shapes.Add(item: (1, 2, seed));
        }
        shapes.AddRange(collection: [(0, 0, 0), (0, 0, 3), (1, 0, 0), (7, 0, 3), (0, 1, 0),
            (6, -2, 1), (1_000_000, -2, 7), (1, -(1L << 32), 0),
            (((1L << 62) - 1), -(1L << 32), 0), (((1L << 62) - 1), 3, 11), (1, 3, 0)]);
        foreach (var (start, step, seed) in shapes) {
            var sequence = LayerSequence.Create(
                seed: seed,
                start: start,
                step: step
            );
            var indices = new HashSet<long> { -1, 0, 1, (seed - 1), seed, (seed + 1), (long.MaxValue - 1), long.MaxValue };
            // Both sides of the triangular fast path's discriminant-width transition.
            AddNeighbours(boundary: (((BigInteger)seed) + ((((BigInteger)ulong.MaxValue) >> 3) * start)));
            AddNeighbours(boundary: (((BigInteger)seed) + (((((BigInteger)ulong.MaxValue) >> 3) + 1) * start)));
            foreach (var layer in new long[] { 0, 1, 2, 3, 7, 63, 65535, 100_000_000, 1_753_413_055, 2_147_483_647, 3_037_000_499, 4_294_967_295 }) {
                if (
                    (step < 0) &&
                    ((start == 0) || (layer > (1 + ((start - 1) / -step))))
                ) { continue; }
                AddNeighbours(boundary: Oracles.LayerPrefix(
                    layer: layer,
                    seed: seed,
                    start: start,
                    step: step
                ));
            }
            foreach (var index in indices) {
                var expected = Oracles.LayerPosition(
                    index: index,
                    seed: seed,
                    start: start,
                    step: step
                );

                if (expected is null) {
                    Assert.Throws<ArgumentOutOfRangeException>(
                        paramName: "index",
                        testCode: () => sequence.LayerOf(index: index)
                    );
                    Assert.Throws<ArgumentOutOfRangeException>(
                        paramName: "index",
                        testCode: () => sequence.Locate(index: index)
                    );
                    if (index < 0) { Assert.Throws<ArgumentOutOfRangeException>(
                        paramName: "index",
                        testCode: () => sequence.Project(index: index)
                    ); }
                } else if (expected.Value.Layer > long.MaxValue) {
                    Assert.Throws<OverflowException>(testCode: () => sequence.LayerOf(index: index));
                    Assert.Throws<OverflowException>(testCode: () => sequence.Locate(index: index));
                    Assert.Throws<OverflowException>(testCode: () => sequence.Project(index: index));
                } else {
                    var layer = ((long)expected.Value.Layer);
                    var offset = ((long)expected.Value.Offset);

                    Assert.Equal(
                        expected: layer,
                        actual: sequence.LayerOf(index: index)
                    );
                    Assert.Equal(
                        expected: new LayerLocation(
                            Layer: layer,
                            Offset: offset
                        ),
                        actual: sequence.Locate(index: index)
                    );
                    Assert.Equal(
                        expected: new LayerProjection(
                            Depth: 0,
                            Layer: layer,
                            Overflow: 0
                        ),
                        actual: sequence.Project(index: index)
                    );
                }
            }
            void AddNeighbours(BigInteger boundary) {
                for (var delta = -1; (delta <= 1); ++delta) {
                    var index = (boundary + delta);

                    if (
                        (index >= 0) &&
                        (index <= long.MaxValue)
                    ) { indices.Add(item: ((long)index)); }
                }
            }
        }
        return null;
    }
}

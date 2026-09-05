using System.Numerics;
using Xunit;

namespace Puck.Maths.Tests;

internal static partial class Subjects {
    public static string? LayerSequenceFullRange() {
        var shapes = new List<(long Start, long Step, long Seed)>();
        foreach (var seed in new long[] { 0, 1, (1L << 62) - 1 }) {
            foreach (var step in new long[] { 1, 2, 3, 4, 5, 6, 8, 12, 1L << 32 }) { shapes.Add((step, step, seed)); }
            shapes.Add((1, 2, seed));
        }
        shapes.AddRange([(0, 0, 0), (0, 0, 3), (1, 0, 0), (7, 0, 3), (0, 1, 0),
            (6, -2, 1), (1_000_000, -2, 7), (1, -(1L << 32), 0),
            ((1L << 62) - 1, -(1L << 32), 0), ((1L << 62) - 1, 3, 11), (1, 3, 0)]);
        foreach (var (start, step, seed) in shapes) {
            var sequence = LayerSequence.Create(start: start, step: step, seed: seed);
            var indices = new HashSet<long> { -1, 0, 1, seed - 1, seed, seed + 1, long.MaxValue - 1, long.MaxValue };
            // Both sides of the triangular fast path's discriminant-width transition.
            AddNeighbours((BigInteger)seed + (((BigInteger)ulong.MaxValue >> 3) * start));
            AddNeighbours((BigInteger)seed + ((((BigInteger)ulong.MaxValue >> 3) + 1) * start));
            foreach (var layer in new long[] { 0, 1, 2, 3, 7, 63, 65535, 100_000_000, 1_753_413_055, 2_147_483_647, 3_037_000_499, 4_294_967_295 }) {
                if (step < 0 && (start == 0 || layer > 1 + ((start - 1) / -step))) { continue; }
                AddNeighbours(Oracles.LayerPrefix(start, step, seed, layer));
            }
            foreach (var index in indices) {
                var expected = Oracles.LayerPosition(start, step, seed, index);
                if (expected is null) {
                    Assert.Throws<ArgumentOutOfRangeException>("index", () => sequence.LayerOf(index));
                    Assert.Throws<ArgumentOutOfRangeException>("index", () => sequence.Locate(index));
                    if (index < 0) { Assert.Throws<ArgumentOutOfRangeException>("index", () => sequence.Project(index)); }
                } else if (expected.Value.Layer > long.MaxValue) {
                    Assert.Throws<OverflowException>(() => sequence.LayerOf(index));
                    Assert.Throws<OverflowException>(() => sequence.Locate(index));
                    Assert.Throws<OverflowException>(() => sequence.Project(index));
                } else {
                    var layer = (long)expected.Value.Layer;
                    var offset = (long)expected.Value.Offset;
                    Assert.Equal(expected: layer, actual: sequence.LayerOf(index));
                    Assert.Equal(expected: new LayerLocation(layer, offset), actual: sequence.Locate(index));
                    Assert.Equal(expected: new LayerProjection(layer, 0, 0), actual: sequence.Project(index));
                }
            }
            void AddNeighbours(BigInteger boundary) {
                for (var delta = -1; delta <= 1; ++delta) {
                    var index = boundary + delta;
                    if (index >= 0 && index <= long.MaxValue) { indices.Add((long)index); }
                }
            }
        }
        return null;
    }
}

using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    // KEEP IN SYNC with sdfCellNearest and sdfCellBorderFrom (field/sdf-noise.hlsli): centered, bounded PCG features;
    // z/y/x visit order; top sixteen hash bits are the fractional feature coordinates on both interpreters; the border's
    // 5x5x5 cells about the nearest feature's, and the skip of a cell whose feature box lies too far to bound it nearer.
    private static FixedQ4816 SampleCells(FixedVector3 point, uint seed, SdfCellMode mode, FixedQ4816 randomness) {
        var cx = (point.X.Value >> FixedQ4816.FractionBitCount);
        var cy = (point.Y.Value >> FixedQ4816.FractionBitCount);
        var cz = (point.Z.Value >> FixedQ4816.FractionBitCount);
        var fraction = (point - new FixedVector3(
            X: FixedQ4816.FromInteger(value: cx),
            Y: FixedQ4816.FromInteger(value: cy),
            Z: FixedQ4816.FromInteger(value: cz)
        ));
        var first = FixedQ4816.MaxValue;
        var second = FixedQ4816.MaxValue;
        var firstDelta = FixedVector3.Zero;
        var (firstX, firstY, firstZ) = (0, 0, 0);

        for (var z = -1; (z <= 1); z++) {
            for (var y = -1; (y <= 1); y++) {
                for (var x = -1; (x <= 1); x++) {
                    var delta = CellFeatureDelta(cx: cx, cy: cy, cz: cz, fraction: fraction, randomness: randomness, seed: seed, x: x, y: y, z: z);
                    var distance = delta.Length;

                    if (distance < first) {
                        second = first;
                        first = distance;
                        firstDelta = delta;
                        (firstX, firstY, firstZ) = (x, y, z);
                    } else if (distance < second) { second = distance; }
                }
            }
        }
        return mode switch {
            SdfCellMode.F1 => first,
            SdfCellMode.Border => CellBorder(cx: cx, cy: cy, cz: cz, first: first, firstDelta: firstDelta, firstX: firstX, firstY: firstY, firstZ: firstZ, fraction: fraction, randomness: randomness, seed: seed),
            _ => (second - first),
        };
    }
    // The distance from the point to the nearest feature's cell border: the least distance to the bisector plane between
    // that feature and another, (|δk|² − |δ1|²)/(2|δk − δ1|), over the 5x5x5 cells about its cell. A cell whose feature
    // box lies at least |δ1| + 2·border from the point is skipped: none of its features can bring a nearer plane, whose
    // distance is at least (|δk| − |δ1|)/2.
    private static FixedQ4816 CellBorder(long cx, long cy, long cz, FixedQ4816 first, FixedVector3 firstDelta, int firstX, int firstY, int firstZ, FixedVector3 fraction, FixedQ4816 randomness, uint seed) {
        var spread = (FixedQ4816.Abs(value: randomness) * Half);
        var border = FixedQ4816.MaxValue;

        for (var z = (firstZ - 2); (z <= (firstZ + 2)); z++) {
            for (var y = (firstY - 2); (y <= (firstY + 2)); y++) {
                for (var x = (firstX - 2); (x <= (firstX + 2)); x++) {
                    var reach = new FixedVector3(
                        X: FixedQ4816.Max(x: (FixedQ4816.Abs(value: (fraction.X - (FixedQ4816.FromInteger(value: x) + Half))) - spread), y: FixedQ4816.Zero),
                        Y: FixedQ4816.Max(x: (FixedQ4816.Abs(value: (fraction.Y - (FixedQ4816.FromInteger(value: y) + Half))) - spread), y: FixedQ4816.Zero),
                        Z: FixedQ4816.Max(x: (FixedQ4816.Abs(value: (fraction.Z - (FixedQ4816.FromInteger(value: z) + Half))) - spread), y: FixedQ4816.Zero)
                    ).Length;

                    if (((reach - first) * Half) >= border) { continue; }
                    var delta = CellFeatureDelta(cx: cx, cy: cy, cz: cz, fraction: fraction, randomness: randomness, seed: seed, x: x, y: y, z: z);
                    var apart = (delta - firstDelta);
                    var separation = apart.Length;

                    // The nearest feature itself, which bounds nothing.
                    if (separation == FixedQ4816.Zero) { continue; }
                    var distance = (FixedVector3.Dot(left: (delta + firstDelta), right: apart) / (separation + separation));

                    if (distance < border) { border = distance; }
                }
            }
        }
        return border;
    }
    // The offset from the cell (x, y, z) about (cx, cy, cz)'s feature to the point, as sdfCellNearest computes it.
    private static FixedVector3 CellFeatureDelta(long cx, long cy, long cz, FixedVector3 fraction, FixedQ4816 randomness, uint seed, int x, int y, int z) {
        var h = Pcg3dLatticeNoise.Pcg3d(
            x: unchecked((uint)(cx + x)) ^ seed,
            y: unchecked((uint)(cy + y)) ^ (seed ^ Pcg3dLatticeNoise.NoiseSeedStreamY),
            z: unchecked((uint)(cz + z)) ^ (seed ^ Pcg3dLatticeNoise.NoiseSeedStreamZ)
        );

        return (new FixedVector3(
            X: ((FixedQ4816.FromInteger(value: x) + Half) + (randomness * (FixedQ4816.FromRawBits(value: (h.X >> 16)) - Half))),
            Y: ((FixedQ4816.FromInteger(value: y) + Half) + (randomness * (FixedQ4816.FromRawBits(value: (h.Y >> 16)) - Half))),
            Z: ((FixedQ4816.FromInteger(value: z) + Half) + (randomness * (FixedQ4816.FromRawBits(value: (h.Z >> 16)) - Half)))
        ) - fraction);
    }
}

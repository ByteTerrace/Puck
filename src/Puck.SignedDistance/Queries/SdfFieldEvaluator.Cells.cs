using Puck.Maths;

namespace Puck.SignedDistance.Queries;

public sealed partial class SdfFieldEvaluator {
    // KEEP IN SYNC with sdfCellDistanceGrad: centered, bounded PCG features; z/y/x visit order;
    // top sixteen hash bits are the fractional feature coordinates on both interpreters.
    private static FixedQ4816 SampleCells(FixedVector3 point, uint seed, SdfCellMode mode, FixedQ4816 randomness) {
        var cx = point.X.Value >> FixedQ4816.FractionBitCount;
        var cy = point.Y.Value >> FixedQ4816.FractionBitCount;
        var cz = point.Z.Value >> FixedQ4816.FractionBitCount;
        var fraction = point - new FixedVector3(FixedQ4816.FromInteger(cx), FixedQ4816.FromInteger(cy), FixedQ4816.FromInteger(cz));
        var first = FixedQ4816.MaxValue;
        var second = FixedQ4816.MaxValue;
        for (var z = -1; z <= 1; z++) {
            for (var y = -1; y <= 1; y++) {
                for (var x = -1; x <= 1; x++) {
                    var h = Pcg3dLatticeNoise.Pcg3d(unchecked((uint)(cx + x)) ^ seed,
                        unchecked((uint)(cy + y)) ^ (seed ^ 0x9E3779B9u),
                        unchecked((uint)(cz + z)) ^ (seed ^ 0x85EBCA77u));
                    var delta = new FixedVector3(
                        FixedQ4816.FromInteger(x) + Half + randomness * (FixedQ4816.FromRawBits(h.X >> 16) - Half),
                        FixedQ4816.FromInteger(y) + Half + randomness * (FixedQ4816.FromRawBits(h.Y >> 16) - Half),
                        FixedQ4816.FromInteger(z) + Half + randomness * (FixedQ4816.FromRawBits(h.Z >> 16) - Half)) - fraction;
                    var distance = delta.Length;
                    if (distance < first) { second = first; first = distance; }
                    else if (distance < second) { second = distance; }
                }
            }
        }
        return mode == SdfCellMode.F1 ? first : second - first;
    }
}

using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    public static BigInteger LayerPrefix(long start, long step, long seed, BigInteger layer) =>
        seed + (start * layer) + (step * layer * (layer - 1) / 2);

    // Find the first prefix strictly beyond the index. This reference never inverts a quadratic.
    public static (BigInteger Layer, BigInteger Offset)? LayerPosition(long start, long step, long seed, long index) {
        if (index < 0) { return null; }
        if (index < seed) { return (BigInteger.Zero, index); }
        var upper = (BigInteger)long.MaxValue + 1;
        if (step < 0 || (step == 0 && start == 0)) {
            var last = start == 0 ? 0 : 1 + ((start - 1) / -step);
            if (LayerPrefix(start, step, seed, last) <= index) { return null; }
            upper = last;
        }
        var lower = BigInteger.Zero;
        while (upper - lower > 1) {
            var middle = (lower + upper) / 2;
            if (LayerPrefix(start, step, seed, middle) <= index) { lower = middle; } else { upper = middle; }
        }
        return (upper, index - LayerPrefix(start, step, seed, upper - 1));
    }
}

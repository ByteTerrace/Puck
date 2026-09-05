using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    public static (BigInteger X, BigInteger Y) ElegantCoordinate(BigInteger index) {
        BigInteger low = 0, high = index + 1;
        while (high - low > 1) {
            var middle = (low + high) / 2;
            if (middle * middle <= index) { low = middle; } else { high = middle; }
        }
        var offset = index - (low * low);
        var a = offset <= low ? offset : low;
        var b = offset <= low ? low : (2 * low) - offset;
        return low.IsEven ? (b, a) : (a, b);
    }

    public static BigInteger ElegantIndex(BigInteger x, BigInteger y) {
        var shell = BigInteger.Max(x, y);
        // Count the horizontal and vertical segments of the shell, reversing them on odd shells.
        var offset = shell.IsEven ? (x == shell ? y : (2 * shell) - x)
                                  : (y == shell ? x : (2 * shell) - y);
        return (shell * shell) + offset;
    }
}

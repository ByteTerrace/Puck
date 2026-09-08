using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    private static readonly (BigInteger Q, BigInteger R)[] HexIndexUnits = [
        (1, 0), (1, 1), (0, 1), (-1, 0), (-1, -1), (0, -1),
    ];

    public static (BigInteger Q, BigInteger R) HexIndexDirection(int direction) {
        var index = direction % 6;
        return HexIndexUnits[index < 0 ? index + 6 : index];
    }

    public static BigInteger HexIndexRadius(BigInteger q, BigInteger r) =>
        (BigInteger.Abs(q) + BigInteger.Abs(r) + BigInteger.Abs(q - r)) / 2;

    public static (BigInteger Q, BigInteger R) HexIndexCoordinate(BigInteger index) {
        if (index.IsZero) { return (0, 0); }
        BigInteger low = 1;
        BigInteger high = int.MaxValue;
        while (low < high) {
            var middle = (low + high) / 2;
            if (1 + (3 * middle * (middle + 1)) > index) { high = middle; }
            else { low = middle + 1; }
        }

        var offset = index - (1 + (3 * low * (low - 1)));
        offset = (offset + (5 * low) + 1) % (6 * low);
        var side = (int)(offset / low);
        var position = offset % low;
        var a = HexIndexUnits[side];
        var b = HexIndexUnits[(side + 1) % 6];
        return (((low - position) * a.Q) + (position * b.Q), ((low - position) * a.R) + (position * b.R));
    }

    public static BigInteger HexIndexValue(BigInteger q, BigInteger r) {
        var radius = HexIndexRadius(q, r);
        if (radius.IsZero) { return 0; }
        for (var side = 0; side < 6; ++side) {
            var a = HexIndexUnits[side];
            var b = HexIndexUnits[(side + 1) % 6];
            var dq = b.Q - a.Q;
            var dr = b.R - a.R;
            var position = !dq.IsZero ? (q - (radius * a.Q)) / dq : (r - (radius * a.R)) / dr;
            if (position >= 0 && position < radius &&
                q == (radius * a.Q) + (position * dq) && r == (radius * a.R) + (position * dr)) {
                var offset = ((side * radius) + position + radius - 1) % (6 * radius);
                return 1 + (3 * radius * (radius - 1)) + offset;
            }
        }

        throw new InvalidOperationException($"No perimeter edge contains ({q}, {r}).");
    }

    public static (BigInteger Q, BigInteger R) HexIndexProduct(BigInteger aq, BigInteger ar, BigInteger bq, BigInteger br) {
        var constant = aq * bq;
        var linear = (aq * br) + (ar * bq);
        var quadratic = ar * br;
        return (constant - quadratic, linear - quadratic);
    }

    public static IEnumerable<(BigInteger Index, BigInteger Q, BigInteger R, int Radius)> HexIndexPerimeter(int maximumRadius) {
        BigInteger index = 0;
        yield return (index++, 0, 0, 0);
        for (var radius = 1; radius <= maximumRadius; ++radius) {
            BigInteger q = 1;
            BigInteger r = 1 - radius;
            // Split the last geometric edge around the seam: r-1 steps, five full sides, then one final step.
            for (var segment = 0; segment < 7; ++segment) {
                var direction = HexIndexUnits[(segment + 1) % 6];
                var length = segment == 0 ? radius - 1 : (segment == 6 ? 1 : radius);
                for (var step = 0; step < length; ++step) {
                    yield return (index++, q, r, radius);
                    q += direction.Q;
                    r += direction.R;
                }
            }
        }
    }
}

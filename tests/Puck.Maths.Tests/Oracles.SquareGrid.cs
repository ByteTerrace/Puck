using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    private static readonly (BigInteger X, BigInteger Y)[] SquareUnits = [(1, 0), (0, 1), (-1, 0), (0, -1)];
    private static readonly (BigInteger X, BigInteger Y)[] SquareCorners = [(1, -1), (1, 1), (-1, 1), (-1, -1)];
    public static (BigInteger X, BigInteger Y) SquareDirection(int direction) => SquareUnits[((direction % 4) + 4) % 4];
    public static BigInteger SquareRadius(BigInteger x, BigInteger y) => BigInteger.Max(BigInteger.Abs(x), BigInteger.Abs(y));

    public static (BigInteger X, BigInteger Y) SquareCoordinateAt(BigInteger index) {
        if (index.IsZero) { return (0, 0); }
        BigInteger low = 1, high = int.MaxValue;
        while (low < high) {
            var middle = (low + high) / 2;
            if (BigInteger.Pow((2 * middle) + 1, 2) > index) { high = middle; }
            else { low = middle + 1; }
        }
        var offset = index - BigInteger.Pow((2 * low) - 1, 2);
        var edge = (int)(offset / (2 * low));
        var position = (offset % (2 * low)) + 1;
        var a = SquareCorners[edge];
        var b = SquareCorners[(edge + 1) % 4];
        return ((low * a.X) + (position * (b.X - a.X) / 2), (low * a.Y) + (position * (b.Y - a.Y) / 2));
    }

    public static BigInteger SquareIndexAt(BigInteger x, BigInteger y) {
        var radius = SquareRadius(x, y);
        if (radius.IsZero) { return 0; }
        for (var edge = 0; edge < 4; ++edge) {
            var corner = SquareCorners[edge];
            var direction = SquareUnits[(edge + 1) % 4];
            var position = direction.X.IsZero ? (y - (radius * corner.Y)) / direction.Y : (x - (radius * corner.X)) / direction.X;
            if (position > 0 && position <= 2 * radius &&
                x == (radius * corner.X) + (position * direction.X) && y == (radius * corner.Y) + (position * direction.Y)) {
                return BigInteger.Pow((2 * radius) - 1, 2) + (2 * edge * radius) + position - 1;
            }
        }
        throw new InvalidOperationException("No square perimeter edge contains the coordinate.");
    }

    public static IEnumerable<(int Index, BigInteger X, BigInteger Y)> SquareWalk(int count) {
        BigInteger x = 0, y = 0;
        var index = 0;
        yield return (index++, x, y);
        for (var segment = 0; index < count; ++segment) {
            var direction = SquareUnits[segment % 4];
            for (var step = 0; step < (segment / 2) + 1 && index < count; ++step) {
                x += direction.X;
                y += direction.Y;
                yield return (index++, x, y);
            }
        }
    }
}

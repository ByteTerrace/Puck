using System.Numerics;

namespace Puck.Maths;

/// <summary>Shared exact operations on arbitrary-width signed integers.</summary>
internal static class BigIntegerMath {
    /// <summary>Returns the floor square root of a non-negative integer.</summary>
    internal static BigInteger SquareRoot(BigInteger value) {
        if (value.Sign < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(value),
                message: "the square-root input must be non-negative"
            );
        }
        if (value.IsZero) { return BigInteger.Zero; }

        // This power of two is strictly above √value. Newton descent remains above the root and terminates when the
        // next estimate can no longer decrease, at which point the current integer is floor(√value).
        var estimate = (BigInteger.One << checked((int)((value.GetBitLength() + 1L) / 2L)));

        while (true) {
            var next = ((estimate + (value / estimate)) >> 1);

            if (next >= estimate) { return estimate; }

            estimate = next;
        }
    }

    /// <summary>Divides two integers and rounds toward negative infinity.</summary>
    internal static BigInteger FloorDivide(BigInteger numerator, BigInteger denominator) {
        var quotient = BigInteger.DivRem(dividend: numerator, divisor: denominator, remainder: out var remainder);

        return (quotient - (((remainder != BigInteger.Zero) && (remainder.Sign != denominator.Sign)) ? BigInteger.One : BigInteger.Zero));
    }

    /// <summary>Divides two integers and rounds toward positive infinity.</summary>
    internal static BigInteger CeilingDivide(BigInteger numerator, BigInteger denominator) =>
        -FloorDivide(numerator: -numerator, denominator: denominator);
}

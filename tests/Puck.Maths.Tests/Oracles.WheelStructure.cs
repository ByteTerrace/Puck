using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    /// <summary>The units modulo a modulus in ascending order, by a from-scratch Euclidean gcd.</summary>
    /// <param name="modulus">The modulus, at least two.</param>
    /// <returns>Every integer in <c>[1, modulus)</c> coprime to <paramref name="modulus"/>.</returns>
    public static int[] UnitsModulo(int modulus) {
        var units = new List<int>();

        for (var candidate = 1; (candidate < modulus); ++candidate) {
            var (a, b) = (candidate, modulus);

            while (b != 0) { (a, b) = (b, (a % b)); }
            if (a == 1) { units.Add(item: candidate); }
        }
        return [.. units];
    }
    /// <summary>The cyclotomic polynomial of an order, by exact integer division of <c>x^order − 1</c> by the
    /// cyclotomic polynomial of every proper divisor.</summary>
    /// <param name="order">The order, at least one.</param>
    /// <returns>The integer coefficients in ascending powers of <c>x</c>.</returns>
    public static BigInteger[] CyclotomicPolynomial(int order) {
        var dividend = new BigInteger[(order + 1)];

        dividend[0] = BigInteger.MinusOne;
        dividend[order] = BigInteger.One;
        for (var divisor = 1; (divisor < order); ++divisor) {
            if ((order % divisor) != 0) { continue; }
            dividend = DivideMonic(dividend: dividend, divisor: CyclotomicPolynomial(order: divisor));
        }
        return dividend;
    }

    /// <summary>Divides an integer polynomial by a monic one that divides it exactly.</summary>
    private static BigInteger[] DivideMonic(BigInteger[] dividend, BigInteger[] divisor) {
        var remainder = ((BigInteger[])dividend.Clone());
        var quotient = new BigInteger[((dividend.Length - divisor.Length) + 1)];

        for (var power = (quotient.Length - 1); (power >= 0); --power) {
            var coefficient = remainder[(power + (divisor.Length - 1))];

            quotient[power] = coefficient;
            for (var index = 0; (index < divisor.Length); ++index) { remainder[(power + index)] -= (coefficient * divisor[index]); }
        }
        if (remainder.Any(predicate: static value => !value.IsZero)) { throw new InvalidOperationException(message: "the division left a remainder"); }
        return quotient;
    }
}

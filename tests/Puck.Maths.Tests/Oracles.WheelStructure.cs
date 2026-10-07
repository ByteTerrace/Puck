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

    /// <summary>The primes in an inclusive range, by trial division.</summary>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound.</param>
    /// <returns>The primes in ascending order.</returns>
    public static int[] PrimesBetween(int low, int high) {
        var primes = new List<int>();

        for (var candidate = Math.Max(val1: 2, val2: low); (candidate <= high); ++candidate) {
            var prime = true;

            for (var divisor = 2; ((divisor * divisor) <= candidate); ++divisor) {
                if ((candidate % divisor) == 0) { prime = false; break; }
            }
            if (prime) { primes.Add(item: candidate); }
        }
        return [.. primes];
    }
    /// <summary>Measures, over one whole period, the longest run of consecutive integers that share a factor with the
    /// product of the given primes: Jacobsthal's function of that product, less one.</summary>
    /// <param name="primes">The primes, which must start 2, 3, 5; their product must fit an int.</param>
    /// <returns>The longest run of non-coprime integers.</returns>
    /// <remarks>Only integers coprime to thirty can be coprime to the product, so the scan stores one bit per unit of
    /// each thirty-integer block and strikes every multiple of each further prime by plain integer arithmetic. The
    /// coprime integers repeat with the period, so the run that wraps from the end of the period to its start counts.</remarks>
    public static int LongestNonCoprimeRun(int[] primes) {
        if ((primes.Length < 3) || (primes[0] != 2) || (primes[1] != 3) || (primes[2] != 5)) { throw new ArgumentException(message: "the primes must start 2, 3, 5", paramName: nameof(primes)); }
        var period = 1;

        foreach (var prime in primes) { period = checked((period * prime)); }
        var units = UnitsModulo(modulus: 30);
        var blocks = (period / 30);
        var bits = new byte[blocks];

        Array.Fill(array: bits, value: byte.MaxValue);
        for (var index = 3; (index < primes.Length); ++index) {
            var prime = primes[index];

            for (var bit = 0; (bit < units.Length); ++bit) {
                for (var block = 0; (block < blocks); ++block) {
                    if ((((30L * block) + units[bit]) % prime) != 0) { continue; }
                    for (var struck = block; (struck < blocks); struck += prime) { bits[struck] &= ((byte)~(1 << bit)); }
                    break;
                }
            }
        }
        var first = -1L;
        var previous = -1L;
        var longest = 0L;

        for (var block = 0; (block < blocks); ++block) {
            for (var bit = 0; (bit < units.Length); ++bit) {
                if ((bits[block] & (1 << bit)) == 0) { continue; }
                var value = ((30L * block) + units[bit]);

                if (first < 0) { first = value; } else { longest = Math.Max(val1: longest, val2: ((value - previous) - 1)); }
                previous = value;
            }
        }
        return ((int)Math.Max(val1: longest, val2: (((first + period) - previous) - 1)));
    }
    /// <summary>Covers the integers <c>1..length</c> greedily by one residue class per prime, smallest prime first, each
    /// class chosen to strike the most integers not yet struck (the smallest such class on a tie).</summary>
    /// <param name="primes">The primes, in the order they choose.</param>
    /// <param name="length">The interval length.</param>
    /// <returns>Each prime's chosen class, or <see langword="null"/> when an integer stays uncovered.</returns>
    public static int[]? GreedyCovering(int[] primes, int length) {
        var covered = new bool[(length + 1)];
        var classes = new int[primes.Length];

        for (var index = 0; (index < primes.Length); ++index) {
            var prime = primes[index];
            var best = 0;
            var bestCount = -1;

            for (var residue = 0; (residue < prime); ++residue) {
                var count = 0;

                for (var value = 1; (value <= length); ++value) {
                    if (!covered[value] && ((value % prime) == residue)) { ++count; }
                }
                if (count > bestCount) { (best, bestCount) = (residue, count); }
            }
            classes[index] = best;
            for (var value = 1; (value <= length); ++value) {
                if ((value % prime) == best) { covered[value] = true; }
            }
        }
        for (var value = 1; (value <= length); ++value) {
            if (!covered[value]) { return null; }
        }
        return classes;
    }
    /// <summary>Solves simultaneous congruences to pairwise coprime moduli by the Chinese remainder theorem.</summary>
    /// <param name="residues">The residues.</param>
    /// <param name="moduli">The pairwise coprime moduli.</param>
    /// <returns>The least non-negative solution.</returns>
    public static BigInteger ChineseRemainder(int[] residues, int[] moduli) {
        var value = BigInteger.Zero;
        var modulus = BigInteger.One;

        for (var index = 0; (index < moduli.Length); ++index) {
            var target = residues[index];
            var prime = moduli[index];

            while (((int)(value % prime)) != target) { value += modulus; }
            modulus *= prime;
        }
        return value;
    }
}

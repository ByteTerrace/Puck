using System.Buffers;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Provides deterministic primality testing and prime enumeration for 32-bit unsigned integers.
/// </summary>
/// <remarks>
/// The primality test is exact across the entire 32-bit range — it never returns a probabilistic answer — and the
/// supporting routines avoid hardware division in their hot loops by reducing through precomputed reciprocals.
/// </remarks>
public static class PrimeExtensions {
    /// <summary>Gets the Miller–Rabin witness bases <c>{ 2, 7, 61 }</c>, which make the test deterministic for every 32-bit value.</summary>
    private static ReadOnlySpan<ulong> MillerRabinBases32 => new ulong[] { 2UL, 7UL, 61UL, };

    /// <summary>Precomputes the reciprocal of <paramref name="divisor"/> used to reduce values without a hardware division.</summary>
    /// <param name="divisor">The divisor whose reciprocal is computed.</param>
    /// <returns>A 128-bit multiplier consumed by <see cref="Modulo(ulong, UInt128, ulong)"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static UInt128 GetModuloMultiplier(ulong divisor) =>
        ((UInt128.MaxValue / divisor) + UInt128.One);
    /// <summary>Reduces <paramref name="value"/> modulo <paramref name="divisor"/> using a precomputed reciprocal.</summary>
    /// <param name="divisor">The modulus.</param>
    /// <param name="multiplier">The reciprocal of <paramref name="divisor"/> obtained from <see cref="GetModuloMultiplier(ulong)"/>.</param>
    /// <param name="value">The value to reduce.</param>
    /// <returns>The remainder of <paramref name="value"/> divided by <paramref name="divisor"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Modulo(ulong divisor, UInt128 multiplier, ulong value) =>
        ((ulong)(((((multiplier * value) >> (sizeof(ulong) * 8)) + UInt128.One) * divisor) >> (sizeof(ulong) * 8)));

    /// <summary>Determines whether <paramref name="value"/> is a prime number.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is prime; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// Small candidates are settled by trial division against the primes below one hundred; the remainder are decided by
    /// a deterministic Miller–Rabin test using the witness bases <c>2</c>, <c>7</c>, and <c>61</c>, which is exact over
    /// the whole 32-bit range. The modular exponentiation reduces through a precomputed reciprocal, so the inner loop
    /// performs no hardware division.
    /// </remarks>
    public static bool IsPrime(this uint value) {
        if (0U == (value & 01U)) { return (02U == value); }
        if (0U == (value % 03U)) { return (03U == value); }
        if (0U == (value % 05U)) { return (05U == value); }
        if (0U == (value % 07U)) { return (07U == value); }
        if (0U == (value % 11U)) { return (11U == value); }
        if (0U == (value % 13U)) { return (13U == value); }
        if (0U == (value % 17U)) { return (17U == value); }
        if (0U == (value % 19U)) { return (19U == value); }
        if (0U == (value % 23U)) { return (23U == value); }
        if (0U == (value % 29U)) { return (29U == value); }
        if (0U == (value % 31U)) { return (31U == value); }
        if (0U == (value % 37U)) { return (37U == value); }
        if (0U == (value % 41U)) { return (41U == value); }
        if (0U == (value % 43U)) { return (43U == value); }
        if (0U == (value % 47U)) { return (47U == value); }
        if (0U == (value % 53U)) { return (53U == value); }
        if (0U == (value % 59U)) { return (59U == value); }
        if (0U == (value % 61U)) { return (61U == value); }
        if (0U == (value % 67U)) { return (67U == value); }
        if (0U == (value % 71U)) { return (71U == value); }
        if (0U == (value % 73U)) { return (73U == value); }
        if (0U == (value % 79U)) { return (79U == value); }
        if (0U == (value % 83U)) { return (83U == value); }
        if (0U == (value % 89U)) { return (89U == value); }
        if (0U == (value % 97U)) { return (97U == value); }

        var exponent = (value - 1U);
        var multiplier = GetModuloMultiplier(divisor: value);
        var shift = ((int)uint.TrailingZeroCount(value: exponent));
        var valueMinusOne = exponent;

        exponent >>= shift;

        for (var i = 0; (i < MillerRabinBases32.Length); ++i) { // IsStrongPseudoprime
            var @base = MillerRabinBases32[i];
            var index = exponent;
            var witness = 1UL;

            do { // ModularExponentiation
                if (0U != (index & 1U)) {
                    witness = Modulo(
                        divisor: value,
                        multiplier: multiplier,
                        value: (@base * witness)
                    );
                }

                @base = Modulo(
                    divisor: value,
                    multiplier: multiplier,
                    value: (@base * @base)
                );
            } while (0U != (index >>= 1));

            if (
                (1U != witness) &&
                (valueMinusOne != witness)
            ) {
                while (++index < shift) {
                    witness = Modulo(
                        divisor: value,
                        multiplier: multiplier,
                        value: (witness * witness)
                    );

                    if ((1U == witness)) { return false; }
                    if (valueMinusOne == witness) { break; }
                }

                if (index == shift) { return false; }
            }
        }

        return (1U != value);
    }
    /// <summary>Returns the prime at the zero-based index <paramref name="value"/>.</summary>
    /// <param name="value">The zero-based prime index, where <c>0</c> maps to <c>2</c>, <c>1</c> to <c>3</c>, <c>2</c> to <c>5</c>, and so on.</param>
    /// <returns>
    /// The prime at index <paramref name="value"/>, or <c>0</c> when <paramref name="value"/> exceeds <c>203280220</c> —
    /// the largest index addressable within the 32-bit range, since there are 203,280,221 primes below 2³².
    /// </returns>
    /// <remarks>
    /// An asymptotic estimate of the n-th prime seeds the search, which is then aligned with
    /// <see cref="PrimeCountingFunction(uint)"/> and advanced by a forward scan that tests candidates with
    /// <see cref="IsPrime(uint)"/>.
    /// </remarks>
    public static uint NthPrime(this uint value) {
        if (203280220U < value) { return 0U; }
        if (3U > value) {
            return ((0U == value)
                ? 2U
                : ((1U == value)
                ? 3U
                : 5U));
        }

        var factor = ((uint)((value * 1.10445d) * Math.Log(d: value))) | 1U;
        var index = ((factor.IsPrime()
            ? (factor - 1U)
            : factor).PrimeCountingFunction() - 1U);

        do {
            if (
                factor.IsPrime() &&
                (++index == value)
            ) { break; }

            factor += 2U;
        } while (index <= value);

        return factor;
    }
    /// <summary>Returns the number of primes less than or equal to <paramref name="value"/> (the prime-counting function π).</summary>
    /// <param name="value">The inclusive upper bound on the primes to count.</param>
    /// <returns>The count of primes not exceeding <paramref name="value"/>.</returns>
    /// <remarks>
    /// Implements the sublinear combinatorial prime-counting method adapted from the ThayirSadamLibrary
    /// (<see href="https://github.com/favre49/ThayirSadamLibrary/blob/main/number-theory/PrimeCount.hpp"/>). Working
    /// storage is rented from <see cref="ArrayPool{T}"/> and bounded by the square root of <paramref name="value"/>, so
    /// peak usage does not exceed roughly 512&#160;kilobytes.
    /// </remarks>
    public static uint PrimeCountingFunction(this uint value) {
        if (value < 9U) {
            return ((value < 2U)
                ? 0U
                : ((value < 3U)
                ? 1U
                : ((value < 5U)
                    ? 2U
                    : ((value < 7U)
                        ? 3U
                        : 4U))));
        }

        var squareRoot = ((uint)Math.Sqrt(d: value));
        var squareRootHalved = ((squareRoot + 1U) >> 1);
        var larges = ArrayPool<uint>.Shared.Rent(minimumLength: ((int)squareRootHalved));
        var roughs = ArrayPool<uint>.Shared.Rent(minimumLength: ((int)squareRootHalved));
        var smalls = ArrayPool<uint>.Shared.Rent(minimumLength: ((int)squareRootHalved));

        for (var index = 0U; (index < squareRootHalved); ++index) {
            larges[index] = (((value / ((index << 1) + 1U)) - 1U) >> 1);
            roughs[index] = ((index << 1) + 1U);
            smalls[index] = index;
        }

        var counter = 0U;
        var factor = 3U;

        if (factor <= squareRoot) {
            var ignore = ArrayPool<bool>.Shared.Rent(minimumLength: ((int)(squareRoot + 1U)));

            Array.Clear(array: ignore);

            do {
                if (!ignore[factor]) {
                    var factorSquared = (factor * factor);

                    if ((((ulong)factorSquared) * factorSquared) > value) { break; }

                    ignore[factor] = true;

                    var i = factorSquared;
                    var j = 0U;
                    var k = 0U;

                    while (i <= squareRoot) {
                        ignore[i] = true;
                        i += (factor << 1);
                    }
                    while (j < squareRootHalved) {
                        i = roughs[j];

                        if (!ignore[i]) {
                            var x = (i * factor);
                            var y = ((x > squareRoot)
                                ? smalls[(((value / x) - 1U) >> 1)]
                                : larges[(smalls[(x >> 1)] - counter)]);
                            var z = (larges[j] - y);

                            larges[k] = (z + counter);
                            roughs[k++] = i;
                        }

                        ++j;
                    }

                    i = ((squareRoot - 1U) >> 1);
                    j = ((squareRoot / factor) - 1U) | 1U;
                    squareRootHalved = k;

                    while (j >= factor) {
                        var x = (smalls[(j >> 1)] - counter);

                        for (k = ((j * factor) >> 1); (i >= k); --i) {
                            smalls[i] -= x;
                        }

                        j -= 2U;
                    }

                    ++counter;
                }

                factor += 2U;
            } while (factor <= squareRoot);

            ArrayPool<bool>.Shared.Return(array: ignore);
        }

        larges[0] += (((squareRootHalved + ((counter - 1U) << 1)) * (squareRootHalved - 1U)) >> 1);

        for (var i = 1U; (i < squareRootHalved); ++i) { larges[0] -= larges[i]; }
        for (var i = 1U; (i < squareRootHalved); ++i) {
            var w = roughs[i];
            var x = (value / w);
            var y = (smalls[(((x / w) - 1U) >> 1)] - counter);

            if (y < (i + 1U)) { break; }

            var z = 0U;

            for (var j = (i + 1U); (j <= y); ++j) {
                z += smalls[(((x / roughs[j]) - 1U) >> 1)];
            }

            larges[0] += (z - ((y - i) * ((counter + i) - 1U)));
        }

        value = (larges[0] + 1U);

        ArrayPool<uint>.Shared.Return(array: smalls);
        ArrayPool<uint>.Shared.Return(array: roughs);
        ArrayPool<uint>.Shared.Return(array: larges);

        return value;
    }
}

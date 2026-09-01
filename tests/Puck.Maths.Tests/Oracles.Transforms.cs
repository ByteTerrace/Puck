using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // ---- the transforms wing ----
    //
    // Definition-form references: every output is the plain O(N^2) sum its transform's definition writes down, formed
    // in BigInteger with no butterfly, no bit reversal, no twiddle table and no field or fixed-point kernel. Where a
    // subject rounds, the reference rounds ONCE at the end through the module's own RoundDyadic.

    /// <summary>The reference Walsh–Hadamard transform in Sylvester (natural) order —
    /// <c>X[k] = sum over n of x[n] * (-1)^popcount(n AND k)</c>, exact in <see cref="BigInteger"/>.</summary>
    /// <param name="values">The sequence; any length.</param>
    /// <returns>The exact spectrum.</returns>
    /// <remarks>The sign of each term comes from the parity of <c>popcount(n AND k)</c>, evaluated directly; the
    /// subject never forms that product at all, reaching the same numbers through <c>log2(N)</c> add/subtract
    /// stages.</remarks>
    public static BigInteger[] WalshHadamardNatural(ReadOnlySpan<long> values) {
        var n = values.Length;
        var result = new BigInteger[n];

        for (var k = 0; (k < n); ++k) {
            var sum = BigInteger.Zero;

            for (var index = 0; (index < n); ++index) {
                var term = new BigInteger(value: values[index]);

                sum += ((0 == (BitOperations.PopCount(value: ((uint)(index & k))) & 1))
                    ? term
                    : -term);
            }

            result[k] = sum;
        }

        return result;
    }
    /// <summary>The reference cyclic convolution of two complex fixed-point sequences given as raw Q16 component
    /// pairs — the exact <c>destination[k] = sum over i of left[i] * right[(k - i) mod N]</c> formed in
    /// <see cref="BigInteger"/> at Q32, then one ties-to-even rounding per component back to Q16.</summary>
    /// <param name="left">The first sequence's raw <c>(real, imaginary)</c> pairs.</param>
    /// <param name="right">The second sequence's raw pairs, the same length.</param>
    /// <returns>The rounded raw pairs of the convolution.</returns>
    /// <remarks>Never forwards, never inverts, never multiplies pointwise: the subject diagonalizes through three
    /// transforms and a pointwise product, and shares nothing with this double loop but the theorem.</remarks>
    public static (long Real, long Imaginary)[] CyclicConvolutionComplexRaw(ReadOnlySpan<(long Real, long Imaginary)> left, ReadOnlySpan<(long Real, long Imaginary)> right) {
        var n = left.Length;
        var result = new (long Real, long Imaginary)[n];

        for (var k = 0; (k < n); ++k) {
            var real = BigInteger.Zero;
            var imaginary = BigInteger.Zero;

            for (var i = 0; (i < n); ++i) {
                var j = ((((k - i) % n) + n) % n);
                var (leftReal, leftImaginary) = left[i];
                var (rightReal, rightImaginary) = right[j];

                real += ((((BigInteger)leftReal) * rightReal) - (((BigInteger)leftImaginary) * rightImaginary));
                imaginary += ((((BigInteger)leftReal) * rightImaginary) + (((BigInteger)leftImaginary) * rightReal));
            }

            result[k] = (
                Real: RoundDyadic(exact: real, shift: 16),
                Imaginary: RoundDyadic(exact: imaginary, shift: 16)
            );
        }

        return result;
    }
}

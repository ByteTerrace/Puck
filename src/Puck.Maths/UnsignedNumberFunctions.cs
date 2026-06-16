using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// Provides extension methods for unsigned binary integers, covering pairing functions, prime factorization, modular
/// inverses, and integer roots.
/// </summary>
/// <remarks>
/// Like the rest of <c>Puck.Maths</c>, these routines are generic over <see cref="IBinaryInteger{TSelf}"/> (further
/// constrained to <see cref="IUnsignedNumber{TSelf}"/>) so that a single implementation serves every unsigned width,
/// and they favor branchless, width-agnostic formulations.
/// </remarks>
public static class UnsignedNumberFunctions {
    /// <summary>Returns <c>1</c> when <paramref name="value"/> is greater than <paramref name="other"/> and <c>0</c> otherwise, without branching.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The first operand.</param>
    /// <param name="other">The second operand.</param>
    /// <returns><c>1</c> when <paramref name="value"/> is greater than <paramref name="other"/>; otherwise <c>0</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T IsGreaterThan<T>(this T value, T other) where T : IBinaryInteger<T> =>
        (value > other).As<T>();
    /// <summary>Returns the larger of <paramref name="value"/> and <paramref name="other"/> without branching.</summary>
    /// <typeparam name="T">The binary integer type.</typeparam>
    /// <param name="value">The first operand.</param>
    /// <param name="other">The second operand.</param>
    /// <returns>Whichever of <paramref name="value"/> and <paramref name="other"/> is greater.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T Maximum<T>(this T value, T other) where T : IBinaryInteger<T> =>
        value ^ ((value ^ other) & (-other.IsGreaterThan(other: value)));

    /// <summary>Combines two non-negative integers into a single non-negative integer using Szudzik's elegant pairing function.</summary>
    /// <typeparam name="TInput">The unsigned binary integer type of the operands.</typeparam>
    /// <typeparam name="TResult">The unsigned binary integer type of the packed result; it must be wide enough to hold the paired value.</typeparam>
    /// <param name="value">The first component of the pair.</param>
    /// <param name="other">The second component of the pair.</param>
    /// <returns>The unique <typeparamref name="TResult"/> that encodes the ordered pair (<paramref name="value"/>, <paramref name="other"/>).</returns>
    /// <remarks>
    /// The mapping is a bijection between pairs and single values; <see cref="ElegantUnpair{TInput, TResult}(TInput)"/>
    /// recovers the operands. It packs more densely than the Cantor pairing function, consuming all values up to the
    /// square of the larger operand.
    /// </remarks>
    public static TResult ElegantPair<TInput, TResult>(this TInput value, TInput other) where TInput : IBinaryInteger<TInput>, IUnsignedNumber<TInput> where TResult : IBinaryInteger<TResult>, IUnsignedNumber<TResult> {
        var x = value.Maximum(other: other);
        var y = ((value ^ other) * (x & TInput.One));
        var z = TResult.CreateTruncating(value: x);

        return ((z * (z + TResult.One)) + (TResult.CreateTruncating(value: y ^ other) - TResult.CreateTruncating(value: y ^ value)));
    }
    /// <summary>Recovers the two non-negative integers that Szudzik's elegant pairing function combined into <paramref name="value"/>.</summary>
    /// <typeparam name="TInput">The unsigned binary integer type of the packed input.</typeparam>
    /// <typeparam name="TResult">The unsigned binary integer type of each recovered component.</typeparam>
    /// <param name="value">The paired value to decode.</param>
    /// <returns>The pair (<c>x</c>, <c>y</c>) for which <see cref="ElegantPair{TInput, TResult}(TInput, TInput)"/> reproduces <paramref name="value"/>.</returns>
    /// <remarks>This is the inverse of <see cref="ElegantPair{TInput, TResult}(TInput, TInput)"/>.</remarks>
    public static (TResult x, TResult y) ElegantUnpair<TInput, TResult>(this TInput value) where TInput : IBinaryInteger<TInput>, IUnsignedNumber<TInput> where TResult : IBinaryInteger<TResult>, IUnsignedNumber<TResult> {
        var x = value.SquareRoot();
        var y = (value - (x * x));
        var z = x;

        if (y < z) {
            (y, z) = (z, y);
        } else {
            y = ((z << 1) - y);
        }

        if (TInput.IsOddInteger(value: TInput.Max(
            x: y,
            y: z
        ))) {
            (y, z) = (z, y);
        }

        return (TResult.CreateTruncating(value: y), TResult.CreateTruncating(value: z));
    }
    /// <summary>Enumerates the prime factors of <paramref name="value"/>, with multiplicity, from smallest to largest.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The value to factor.</param>
    /// <returns>
    /// A lazily evaluated sequence of the prime factors of <paramref name="value"/>, each repeated according to its
    /// multiplicity (for example, <c>360</c> yields <c>2, 2, 2, 3, 3, 5</c>). The sequence is empty when
    /// <paramref name="value"/> is prime or less than two, since neither case has a proper factorization to report.
    /// </returns>
    /// <remarks>
    /// The small prime factors are removed first, after which trial division advances over a modulo-30 wheel that
    /// visits only the eight residues coprime to 30 and stops once the trial factor exceeds the square root of the
    /// remaining cofactor.
    /// </remarks>
    public static IEnumerable<T> EnumeratePrimeFactors<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        if (T.CreateChecked(value: 4) > value) { yield break; }
        if (T.CreateChecked(value: 5) == value) { yield break; }
        if (T.CreateChecked(value: 7) == value) { yield break; }
        if (T.CreateChecked(value: 11) == value) { yield break; }
        if (T.CreateChecked(value: 13) == value) { yield break; }

        var index = value;

        while (T.Zero == (index & T.One)/* enumerate factors of 2 */) {
            yield return T.CreateChecked(value: 2);

            index >>= 1;
        }
        while (T.Zero == (index % T.CreateChecked(value: 3))/* enumerate factors of 3 */) {
            yield return T.CreateChecked(value: 3);

            index /= T.CreateChecked(value: 3);
        }
        while (T.Zero == (index % T.CreateChecked(value: 5))/* enumerate factors of 5 */) {
            yield return T.CreateChecked(value: 5);

            index /= T.CreateChecked(value: 5);
        }
        while (T.Zero == (index % T.CreateChecked(value: 7))/* enumerate factors of 7 */) {
            yield return T.CreateChecked(value: 7);

            index /= T.CreateChecked(value: 7);
        }
        while (T.Zero == (index % T.CreateChecked(value: 11))/* enumerate factors of 11 */) {
            yield return T.CreateChecked(value: 11);

            index /= T.CreateChecked(value: 11);
        }
        while (T.Zero == (index % T.CreateChecked(value: 13))/* enumerate factors of 13 */) {
            yield return T.CreateChecked(value: 13);

            index /= T.CreateChecked(value: 13);
        }

        var factor = T.CreateChecked(value: 17);
        var limit = index.SquareRoot();

        if (factor <= limit) {
            do {
                while (T.Zero == (index % factor)/* enumerate factors of (30k - 13) */) {
                    yield return factor;

                    index /= factor;
                }

                factor += T.CreateChecked(value: 2);

                while (T.Zero == (index % factor)/* enumerate factors of (30k - 11) */) {
                    yield return factor;

                    index /= factor;
                }

                factor += T.CreateChecked(value: 4);

                while (T.Zero == (index % factor)/* enumerate factors of (30k - 7) */) {
                    yield return factor;

                    index /= factor;
                }

                factor += T.CreateChecked(value: 6);

                while (T.Zero == (index % factor)/* enumerate factors of (30k - 1) */) {
                    yield return factor;

                    index /= factor;
                }

                factor += T.CreateChecked(value: 2);

                while (T.Zero == (index % factor)/* enumerate factors of (30k + 1) */) {
                    yield return factor;

                    index /= factor;
                }

                factor += T.CreateChecked(value: 6);

                while (T.Zero == (index % factor)/* enumerate factors of (30k + 7) */) {
                    yield return factor;

                    index /= factor;
                }

                factor += T.CreateChecked(value: 4);

                while (T.Zero == (index % factor)/* enumerate factors of (30k + 11) */) {
                    yield return factor;

                    index /= factor;
                }

                factor += T.CreateChecked(value: 2);

                while (T.Zero == (index % factor)/* enumerate factors of (30k + 13) */) {
                    yield return factor;

                    index /= factor;
                }

                factor += T.CreateChecked(value: 4);
                limit = index.SquareRoot();
            } while (factor <= limit);
        }

        if (
            (index != T.One) &&
            (index != value)
        ) {
            yield return index;
        }
    }
    /// <summary>Computes the multiplicative inverse of an odd <paramref name="value"/> modulo <c>2^w</c>, where <c>w</c> is the bit width of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The value to invert. It must be odd; even values have no inverse modulo a power of two, and passing one yields a meaningless result.</param>
    /// <returns>The unique value <c>r</c> for which <c>(<paramref name="value"/> * r)</c> is congruent to <c>1</c> modulo <c>2^w</c>.</returns>
    /// <remarks>
    /// Uses the Newton–Hensel doubling iteration from “An Improved Integer Multiplicative Inverse (modulo 2^w)” by
    /// Jeffrey Hurchalla, April 2022 (<see href="https://arxiv.org/ftp/arxiv/papers/2204/2204.04342.pdf"/>). The number
    /// of refinement steps is fixed by the width of <typeparamref name="T"/>, so a closed generic runs in constant time.
    /// </remarks>
    public static T ModularInverse<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var bitCount = int.CreateChecked(value: BinaryIntegerConstants<T>.Size);
        var x = (T.CreateChecked(value: 3) * value) ^ T.CreateChecked(value: 2);
        var y = (T.One - (value * x));

        x *= (y + T.One);

        if (bitCount > 8) {
            y *= y;
            x *= (y + T.One);
        }

        if (bitCount > 16) {
            y *= y;
            x *= (y + T.One);
        }

        if (bitCount > 32) {
            y *= y;
            x *= (y + T.One);
        }

        if (bitCount > 64) {
            y *= y;
            x *= (y + T.One);
        }

        if (bitCount > 128) {
            var i = (int.Log2(value: (bitCount / 4)) - 5);

            do {
                y *= y;
                x *= (y + T.One);
            } while (0 < --i);
        }

        return x;
    }
    /// <summary>Returns the smallest power of two greater than or equal to <paramref name="value"/>.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The value to round up.</param>
    /// <returns>
    /// The least power of two that is not smaller than <paramref name="value"/>. A value that is already a power of two
    /// is returned unchanged; the result is zero when <paramref name="value"/> is zero or when the next power of two
    /// would exceed the range of <typeparamref name="T"/>.
    /// </returns>
    public static T NextPowerOfTwo<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var x = int.CreateTruncating(value: (BinaryIntegerConstants<T>.Size - T.LeadingZeroCount(value: (value - T.One))));
        var y = int.CreateTruncating(value: BinaryIntegerConstants<T>.Log2Size);

        return ((T.One ^ T.CreateTruncating(value: (((uint)x) >> y))) << x);
    }
    /// <summary>Returns the smallest perfect square strictly greater than <paramref name="value"/>.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The value to round up.</param>
    /// <returns>The next perfect square above <paramref name="value"/>, computed as <c>(⌊√value⌋ + 1)²</c>. The result wraps on overflow.</returns>
    public static T NextSquare<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var squareRootPlusOne = (value.SquareRoot() + T.One);

        return (squareRootPlusOne * squareRootPlusOne);
    }
    /// <summary>Returns the integer square root of <paramref name="value"/> — the largest value whose square does not exceed <paramref name="value"/>.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The value whose floor square root is computed.</param>
    /// <returns>The floor of the square root of <paramref name="value"/>.</returns>
    /// <remarks>
    /// The width-specific branch is selected by the JIT, so a closed generic runs a fixed, value-independent
    /// instruction sequence (constant time). The 8-, 16-, 32-, and 64-bit widths seed the result with a fixed-latency
    /// hardware floating-point square root and settle it with a branchless integer correction, while wider widths use a
    /// branchless bit-by-bit algorithm whose iteration count is fixed by the width of <typeparamref name="T"/>.
    /// </remarks>
    public static T SquareRoot<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var bitCount = int.CreateChecked(value: BinaryIntegerConstants<T>.Size);

        return bitCount switch {
#if !FORCE_SOFTWARE_SQRT
            8 => T.CreateTruncating(value: ((uint)MathF.Sqrt(x: uint.CreateTruncating(value: value)))),
            16 => T.CreateTruncating(value: ((uint)MathF.Sqrt(x: uint.CreateTruncating(value: value)))),
            32 => T.CreateTruncating(value: ((uint)Math.Sqrt(d: uint.CreateTruncating(value: value)))),
            64 => T.CreateTruncating(value: Sqrt(value: ulong.CreateTruncating(value: value))),
#endif
            _ => SoftwareImplementation(value: value),
        };

        /*
             A branchless bit-by-bit (restoring) integer square root. The candidate bit starts at the highest power of
             four representable in T and is shifted down two positions per step, so the loop runs exactly (size / 2)
             iterations regardless of the value -- constant time, with no value-dependent branches.
         */
        static T SoftwareImplementation(T value) {
            var bit = (T.One << (int.CreateChecked(value: BinaryIntegerConstants<T>.Size) - 2)); // highest power of four representable in T
            var result = T.Zero;

            do {
                var candidate = (result + bit);
                var mask = (-(value >= candidate).As<T>()); // all bits set when (value >= candidate), else zero

                result >>>= 1;
                result += bit & mask;
                value -= candidate & mask;
                bit >>>= 2;
            } while (T.Zero < bit);

            return result;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong Sqrt(ulong value) {
            var x = ((ulong)Math.Sqrt(d: value)); // ulong -> double is the correct unsigned conversion (a signed cast would go negative for inputs >= 2^63)

            x -= unchecked(((x > 4294967295UL).As<ulong>() * (x - 4294967295UL))); // clamp to uint.MaxValue so (x * x) cannot overflow
            x -= ((x * x) > value).As<ulong>(); // settle a one-too-high estimate
            x += (x < 4294967295UL).As<ulong>() & (unchecked(((x + 1UL) * (x + 1UL))) <= value).As<ulong>(); // settle a one-too-low estimate

            return x;
        }
    }
}

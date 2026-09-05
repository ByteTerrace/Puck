using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;

namespace Puck.Maths;

/// <summary>
/// Provides extension methods for unsigned binary integers, covering pairing functions, prime factorization, the Jacobi
/// symbol, modular inverses, and integer roots.
/// </summary>
/// <remarks>
/// Like the rest of <c>Puck.Maths</c>, these routines are generic over <see cref="IBinaryInteger{TSelf}"/> (further
/// constrained to <see cref="IUnsignedNumber{TSelf}"/>) so that a single implementation serves every unsigned width,
/// and they favor branchless, width-agnostic formulations.
/// </remarks>
public static partial class UnsignedNumberFunctions {
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

    /// <summary>Combines two non-negative integers in a continuous walk through alternating square shells.</summary>
    /// <typeparam name="TInput">The unsigned binary integer type of the operands.</typeparam>
    /// <typeparam name="TResult">The unsigned binary integer type of the packed result; it must be wide enough to hold the paired value.</typeparam>
    /// <param name="value">The first component of the pair.</param>
    /// <param name="other">The second component of the pair.</param>
    /// <returns>The unique <typeparamref name="TResult"/> that encodes the ordered pair (<paramref name="value"/>, <paramref name="other"/>).</returns>
    /// <remarks>
    /// The mapping is a bijection between pairs and single values; <see cref="ElegantUnpair{TInput, TResult}(TInput)"/>
    /// recovers the operands. Shell <c>m = max(x, y)</c> occupies <c>m²</c> through <c>(m + 1)² − 1</c>.
    /// Its index is <c>m(m + 1) + (m odd ? x − y : y − x)</c>. Every consecutive index is a grid neighbour.
    /// The result type must hold the entire encoded value; conversions and arithmetic otherwise truncate or wrap.
    /// </remarks>
    public static TResult ElegantPair<TInput, TResult>(this TInput value, TInput other) where TInput : IBinaryInteger<TInput>, IUnsignedNumber<TInput> where TResult : IBinaryInteger<TResult>, IUnsignedNumber<TResult> {
        var x = value.Maximum(other: other);
        var y = ((value ^ other) * (x & TInput.One));
        var z = TResult.CreateTruncating(value: x);

        return ((z * (z + TResult.One)) + (TResult.CreateTruncating(value: y ^ other) - TResult.CreateTruncating(value: y ^ value)));
    }
    /// <summary>Recovers the two non-negative integers in an alternating square-shell encoding.</summary>
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
    /// The prime factors of <paramref name="value"/>, each repeated according to its multiplicity — for example,
    /// <c>360</c> yields <c>2, 2, 2, 3, 3, 5</c>. A prime yields itself, so the length is always Ω, and the sequence is
    /// empty only for a <paramref name="value"/> below two, which has no factorization at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Carriage only: the factorization is <see cref="PrimeKernels.Factorize(ulong, Span{ulong})"/> for a
    /// <typeparamref name="T"/> that fits a machine word, and
    /// <see cref="BigIntegerFunctions.EnumeratePrimeFactors(BigInteger)"/> for one that does not. Neither branch is a
    /// second factoring algorithm — the split is the carrier's, since the word-sized kernel reduces through a Montgomery
    /// ring and precomputed reciprocals that a wider operand cannot use.
    /// </para>
    /// <para>
    /// The output order is non-decreasing by contract — equal factors are always adjacent — so a caller may deduplicate
    /// by comparing each factor against its predecessor alone.
    /// </para>
    /// </remarks>
    public static IEnumerable<T> EnumeratePrimeFactors<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        if (T.CreateChecked(value: 2) > value) { return []; }

        return ((value <= T.CreateTruncating(value: ulong.MaxValue))
            ? WordFactors(value: value)
            : WideFactors(value: value)
        );
    }

    /// <summary>Factors a value that fits a machine word through the shared word-sized kernel.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The value to factor, which must be at least two and at most <see cref="ulong.MaxValue"/>.</param>
    /// <returns>The prime factors, ascending and with multiplicity.</returns>
    private static IEnumerable<T> WordFactors<T>(T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var factors = new ulong[64];
        var count = PrimeKernels.Factorize(
            value: ulong.CreateTruncating(value: value),
            destination: factors
        );

        for (var i = 0; (i < count); ++i) { yield return T.CreateTruncating(value: factors[i]); }
    }
    /// <summary>Factors a value too wide for a machine word through the arbitrary-width kernel.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The value to factor, which must exceed <see cref="ulong.MaxValue"/>.</param>
    /// <returns>The prime factors, ascending and with multiplicity.</returns>
    private static IEnumerable<T> WideFactors<T>(T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        foreach (var factor in BigIntegerFunctions.EnumeratePrimeFactors(value: BigInteger.CreateChecked(value: value))) {
            yield return T.CreateChecked(value: factor);
        }
    }

    /// <summary>Computes the Jacobi symbol of <paramref name="value"/> over an odd <paramref name="modulus"/>.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The upper argument. Every value is legal — it is reduced modulo <paramref name="modulus"/> before the descent begins.</param>
    /// <param name="modulus">The lower argument, which must be odd.</param>
    /// <returns><c>0</c> when the two arguments share a factor, otherwise <c>1</c> or <c>-1</c>.</returns>
    /// <remarks>
    /// <para>
    /// When <paramref name="modulus"/> is an odd prime this is the Legendre symbol — the quadratic character — but unlike
    /// Euler's criterion (<see cref="PrimeField64.LegendreCharacter(ulong)"/>) it never presumes the modulus prime, which is
    /// what makes it admissible as an input to a primality test rather than only a consequence of one.
    /// </para>
    /// <para>
    /// The binary — shift-and-flip — algorithm. Each round strips the trailing factors of two out of the upper argument,
    /// flipping the sign once per factor when the lower argument is congruent to three or five modulo eight, then swaps the
    /// pair under quadratic reciprocity, flipping again when both arguments are congruent to three modulo four, and reduces.
    /// The descent is Euclid's, so it bottoms out at the greatest common divisor and the shared-factor case falls out of the
    /// same loop instead of needing a test of its own. Neither factorization nor exponentiation is involved, the cost is
    /// logarithmic in <paramref name="modulus"/>, and nothing is allocated.
    /// </para>
    /// <para>
    /// Both sign rules are read as single bit tests rather than residue comparisons — <c>((n &gt;&gt; 1) ^ n)</c> carries the
    /// "congruent to three or five modulo eight" indicator for the lower argument in bit one, and <c>(a &amp; n)</c> carries
    /// "both are congruent to three modulo four" in that same position — so the two flips accumulate into one parity bit. The
    /// whole trailing-zero run leaves in a single shift, so a round spends one trailing-zero count instead of a halving loop.
    /// </para>
    /// <para>
    /// This is the fixed-width counterpart to <see cref="NumberTheoryFunctions.JacobiSymbol(BigInteger, BigInteger)"/>; the
    /// <see cref="IUnsignedNumber{TSelf}"/> constraint is what keeps the two from colliding, since <see cref="BigInteger"/>
    /// is signed.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> is even, zero included; the symbol is defined only over an odd modulus.</exception>
    public static int JacobiSymbol<T>(this T value, T modulus) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        if (!T.IsOddInteger(value: modulus)) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(modulus),
                message: "The Jacobi symbol requires an odd modulus."
            );
        }

        var lower = modulus;
        var parity = T.Zero; // the accumulated exponent of -1, carried in bit zero
        var upper = value.FloorModulo(modulus: modulus);

        while (T.Zero != upper) {
            var twoExponent = T.TrailingZeroCount(value: upper);

            upper >>>= int.CreateTruncating(value: twoExponent);

            // (2/lower) is -1 exactly for lower ≡ ±3 (mod 8), which bit one of ((lower >> 1) ^ lower) reports; only the
            // parity of the trailing-zero run can flip the sign, so both indicators meet in bit zero.
            parity ^= twoExponent & (((lower >>> 1) ^ lower) >>> 1) & T.One;
            // Reciprocity flips the sign exactly when both arguments are ≡ 3 (mod 4). Both are odd here, so their AND
            // carries that in bit one.
            parity ^= ((upper & lower) >>> 1) & T.One;
            (lower, upper) = (upper, (lower % upper));
        }

        // A descent that ends anywhere but one found a shared factor, and the symbol is zero however the parity landed.
        return ((T.One == lower)
            ? (1 - (int.CreateTruncating(value: parity) << 1))
            : 0
        );
    }
    /// <summary>Computes the multiplicative inverse of an odd <paramref name="value"/> modulo <c>2^w</c>, where <c>w</c> is the bit width of <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The unsigned binary integer type.</typeparam>
    /// <param name="value">The value to invert. It must be odd; even values have no inverse modulo a power of two, and passing one yields a meaningless result.</param>
    /// <returns>The unique value <c>r</c> for which <c>(<paramref name="value"/> * r)</c> is congruent to <c>1</c> modulo <c>2^w</c>.</returns>
    /// <remarks>
    /// Uses the Newton–Hensel doubling iteration: each step doubles the number of correct low-order bits, so a fixed
    /// number of steps recovers the full-width inverse. The number of refinement steps is fixed by the width of
    /// <typeparamref name="T"/>, so a closed generic runs in constant time. The step count assumes the storage width
    /// of <typeparamref name="T"/> is a power of two — true for every built-in integer; a custom width that is not
    /// leaves the top bits of the inverse unrefined.
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
    /// <remarks>The out-of-range guard assumes the storage width of <typeparamref name="T"/> is a power of two —
    /// true for every built-in integer. A custom width that is not a power of two trips the guard early and zeroes
    /// results for the type's upper bit positions.</remarks>
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
    /// hardware floating-point square root and settle it with a branchless integer correction; the 128-bit width seeds
    /// the same way and closes the seed's error with a single Newton step before the correction; wider widths use a
    /// branchless bit-by-bit algorithm whose iteration count is fixed by the width of <typeparamref name="T"/>.
    /// </remarks>
    public static T SquareRoot<T>(this T value) where T : IBinaryInteger<T>, IUnsignedNumber<T> {
        var bitCount = int.CreateChecked(value: BinaryIntegerConstants<T>.Size);

        return bitCount switch {
#if !FORCE_SOFTWARE_SQRT
            8 => T.CreateTruncating(value: ((uint)MathF.Sqrt(x: uint.CreateTruncating(value: value)))),
            16 => T.CreateTruncating(value: ((uint)MathF.Sqrt(x: uint.CreateTruncating(value: value)))),
            32 => T.CreateTruncating(value: ((uint)Math.Sqrt(d: uint.CreateTruncating(value: value)))),
            64 => T.CreateTruncating(value: Sqrt64(value: ulong.CreateTruncating(value: value))),
            128 => T.CreateTruncating(value: Sqrt128(value: UInt128.CreateTruncating(value: value))),
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
        static ulong Sqrt64(ulong value) {
            var x = ((ulong)Math.Sqrt(d: value)); // ulong -> double is the correct unsigned conversion (a signed cast would go negative for inputs >= 2^63)

            x -= unchecked(((x > 4294967295UL).As<ulong>() * (x - 4294967295UL))); // clamp to uint.MaxValue so (x * x) cannot overflow
            x -= ((x * x) > value).As<ulong>(); // settle a one-too-high estimate
            x += (x < 4294967295UL).As<ulong>() & (unchecked(((x + 1UL) * (x + 1UL))) <= value).As<ulong>(); // settle a one-too-low estimate

            return x;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static UInt128 Sqrt128(UInt128 value) {
            if (value <= ((UInt128)ulong.MaxValue)) { return Sqrt64(value: ((ulong)value)); }

            var maximumRoot = ((UInt128)ulong.MaxValue);
            var seed = ((ulong)Math.Sqrt(d: ((double)value))); // within 2^13 of the true root; the cast saturates at ulong.MaxValue, which the true root never exceeds
            var high = ((ulong)(value >> 64));
            UInt128 x;

            if (
                X86Base.X64.IsSupported &&
                (high < seed)
            ) {
#pragma warning disable SYSLIB5004
                var (quotient, _) = X86Base.X64.DivRem(
                    divisor: seed,
                    lower: ((ulong)value),
                    upper: high
                );
#pragma warning restore SYSLIB5004

                x = ((((UInt128)seed) + quotient) >> 1); // one Newton step collapses the seed's error to at most one either way
            } else {
                x = ((((UInt128)seed) + (value / seed)) >> 1);
            }

            x -= ((x > maximumRoot).As<UInt128>() * (x - maximumRoot)); // clamp to ulong.MaxValue so (x * x) cannot overflow
            x -= ((x * x) > value).As<UInt128>(); // settle a one-too-high estimate
            x -= ((x * x) > value).As<UInt128>(); // the clamp can leave the estimate two too high near the top of the range
            x += (x < maximumRoot).As<UInt128>() & (unchecked(((x + UInt128.One) * (x + UInt128.One))) <= value).As<UInt128>(); // settle a one-too-low estimate

            return x;
        }
    }
}

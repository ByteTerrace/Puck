using System.Buffers;
using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Provides exact number-theoretic routines over arbitrary-width integers: the Jacobi symbol, a segmented prime sieve
/// over a range, and Hensel lifting of a polynomial root from a prime to a prime power.
/// </summary>
/// <remarks>
/// These are the arbitrary-width companions to the fixed-width prime-field arithmetic in <see cref="PrimeField64"/>:
/// the character and root-lifting the engine reaches for when a procedural construction outgrows a single
/// machine-word modulus — odd-radix quadratic-residue tests without a full exponentiation, deterministic prime
/// enumeration over a window for sampling-net moduli, and exact modular root refinement past <c>2^64</c>.
/// </remarks>
public static class NumberTheoryFunctions {
    private const ulong MaximumSieveBound = uint.MaxValue;
    /// <summary>The number of odd values one window covers, and therefore the bit count the marker fills.</summary>
    private const int WindowBits = (1 << 16);
    /// <summary>The distance in value space from a window's first odd value to its last.</summary>
    private const ulong WindowSpan = ((2UL * WindowBits) - 2UL);
    /// <summary>The bitmap words one window occupies.</summary>
    private const int WindowWords = (WindowBits >> 6);

    /// <summary>Evaluates a polynomial at a point by nested multiply-and-add.</summary>
    /// <param name="coefficients">The coefficients from the constant term upward.</param>
    /// <param name="point">The evaluation point.</param>
    /// <returns>The polynomial's value.</returns>
    private static BigInteger Evaluate(ReadOnlySpan<BigInteger> coefficients, BigInteger point) {
        var result = BigInteger.Zero;

        for (var index = (coefficients.Length - 1); (index >= 0); --index) {
            result = ((result * point) + coefficients[index]);
        }

        return result;
    }
    /// <summary>Evaluates the formal derivative of a polynomial at a point by nested multiply-and-add.</summary>
    /// <param name="coefficients">The coefficients from the constant term upward.</param>
    /// <param name="point">The evaluation point.</param>
    /// <returns>The derivative's value.</returns>
    private static BigInteger EvaluateDerivative(ReadOnlySpan<BigInteger> coefficients, BigInteger point) {
        var result = BigInteger.Zero;

        for (var index = (coefficients.Length - 1); (index >= 1); --index) {
            result = ((result * point) + (coefficients[index] * index));
        }

        return result;
    }
    /// <summary>Gets whether a value is divisible by a modulus.</summary>
    /// <param name="value">The value to test.</param>
    /// <param name="modulus">The modulus.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is congruent to zero modulo <paramref name="modulus"/>.</returns>
    private static bool IsZeroModulo(this BigInteger value, BigInteger modulus) =>
        (value % modulus).IsZero;

    /// <summary>Enumerates the primes in a closed range in ascending order as a materialized sequence.</summary>
    /// <param name="low">The inclusive lower bound of the range.</param>
    /// <param name="high">The inclusive upper bound of the range.</param>
    /// <returns>The primes in <c>[<paramref name="low"/>, <paramref name="high"/>]</c>, ascending.</returns>
    /// <remarks>A materializing convenience over <see cref="SegmentedPrimeSieve(ulong, ulong, Action{ulong})"/>: the whole range is swept and collected before the first element is observable, so a wide range is paid for in full and in memory up front. The callback form allocates nothing per prime and is preferred on a hot path or over a range whose primes need not all be held at once.</remarks>
    public static IEnumerable<ulong> EnumeratePrimes(ulong low, ulong high) {
        var primes = new List<ulong>();

        SegmentedPrimeSieve(
            high: high,
            low: low,
            onPrime: primes.Add
        );

        return primes;
    }
    /// <summary>Lifts a simple root of an integer polynomial from a base modulus to a power of that modulus.</summary>
    /// <param name="coefficients">The polynomial coefficients from the constant term upward: index <c>i</c> is the coefficient of <c>x^i</c>.</param>
    /// <param name="root">A root of the polynomial modulo <paramref name="baseModulus"/>.</param>
    /// <param name="baseModulus">The modulus the root is known modulo, at least two.</param>
    /// <param name="targetPower">The exponent of the target modulus <c>baseModulus^targetPower</c>, at least one.</param>
    /// <returns>The unique root congruent to <paramref name="root"/> modulo <paramref name="baseModulus"/> that solves the polynomial modulo <c>baseModulus^targetPower</c>.</returns>
    /// <remarks>
    /// <para>
    /// One power is gained per step: a root modulo <c>baseModulus^k</c> is corrected by a multiple of
    /// <c>baseModulus^k</c> chosen so the value vanishes modulo <c>baseModulus^(k+1)</c>. The correction divides by
    /// the derivative, which is why this is the derivative-unit case. The base need not be prime: invertibility of
    /// the derivative modulo the base is the exact precondition used by each correction step.
    /// </para>
    /// <para>
    /// The lift is unique and this routine succeeds exactly when the derivative is a unit modulo <paramref name="baseModulus"/>,
    /// that is, when <paramref name="root"/> is a simple root. When the derivative vanishes modulo <paramref name="baseModulus"/>
    /// or is any other non-unit, the step cannot be inverted: such a root either fails to lift or lifts non-uniquely,
    /// and neither outcome is a single return value, so the method rejects that input rather than guessing a branch.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="coefficients"/> is empty, <paramref name="root"/> is not a root modulo <paramref name="baseModulus"/>, or the derivative is not a unit modulo <paramref name="baseModulus"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baseModulus"/> is below two or <paramref name="targetPower"/> is below one.</exception>
    public static BigInteger HenselLiftRoot(ReadOnlySpan<BigInteger> coefficients, BigInteger root, BigInteger baseModulus, int targetPower) {
        if (coefficients.IsEmpty) {
            throw new ArgumentException(
                message: "The polynomial must have at least one coefficient.",
                paramName: nameof(coefficients)
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: baseModulus,
            other: (BigInteger.One + BigInteger.One)
        );
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: targetPower,
            other: 1
        );

        if (!Evaluate(
            coefficients: coefficients,
            point: root
        ).IsZeroModulo(modulus: baseModulus)) {
            throw new ArgumentException(
                message: "The supplied value is not a root of the polynomial modulo the base modulus.",
                paramName: nameof(root)
            );
        }

        var derivativeAtRoot = EvaluateDerivative(
            coefficients: coefficients,
            point: root
        ).FloorModulo(modulus: baseModulus);

        if (BigInteger.GreatestCommonDivisor(
            left: derivativeAtRoot,
            right: baseModulus
        ) != BigInteger.One) {
            throw new ArgumentException(
                message: "The derivative is not a unit modulo the base modulus, so the unique derivative-unit lift does not apply.",
                paramName: nameof(root)
            );
        }

        var inverseDerivative = BigIntegerFunctions.ModularInverse(
            modulus: baseModulus,
            value: derivativeAtRoot
        );
        var lifted = root.FloorModulo(modulus: baseModulus);
        var modulus = baseModulus;

        for (var power = 1; (power < targetPower); ++power) {
            var nextModulus = (modulus * baseModulus);
            var deficit = Evaluate(
                coefficients: coefficients,
                point: lifted
            ).FloorModulo(modulus: nextModulus);
            // deficit is a multiple of `modulus`; the step solves
            // (deficit/modulus + t * f'(root)) ≡ 0 (mod baseModulus).
            var step = (-(deficit / modulus) * inverseDerivative).FloorModulo(modulus: baseModulus);

            lifted += (step * modulus);
            modulus = nextModulus;
        }

        return lifted;
    }
    /// <summary>Computes the Jacobi symbol by the binary algorithm.</summary>
    /// <param name="numerator">The upper argument.</param>
    /// <param name="denominator">The lower argument, which must be a positive odd integer.</param>
    /// <returns><c>0</c> when the arguments share a factor, otherwise <c>1</c> or <c>-1</c>. When the denominator is an odd prime this is the Legendre symbol.</returns>
    /// <remarks>
    /// The reciprocity recursion driven by repeated halving: factors of two are pulled out using the sign rule keyed on
    /// the denominator modulo eight, and the arguments are swapped using the reciprocity sign keyed on both moduli
    /// modulo four. No factorization and no exponentiation are needed, so the cost is logarithmic in the arguments.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="denominator"/> is not positive or is even.</exception>
    public static int JacobiSymbol(BigInteger numerator, BigInteger denominator) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            value: denominator,
            other: BigInteger.Zero
        );

        if (denominator.IsEven) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(denominator),
                message: "The Jacobi symbol requires an odd positive denominator."
            );
        }

        var upper = numerator.FloorModulo(modulus: denominator);
        var lower = denominator;
        var sign = 1;

        while (!upper.IsZero) {
            while (upper.IsEven) {
                upper >>= 1;

                var residue = ((int)(lower & 7));

                if (
                    (3 == residue) ||
                    (5 == residue)
                ) { sign = -sign; }
            }

            (upper, lower) = (lower, upper);

            if (
                (3 == ((int)(upper & 3))) &&
                (3 == ((int)(lower & 3)))
            ) { sign = -sign; }

            upper %= lower;
        }

        return ((BigInteger.One == lower)
            ? sign
            : 0
        );
    }
    /// <summary>Enumerates the primes in a closed range in ascending order.</summary>
    /// <param name="low">The inclusive lower bound of the range.</param>
    /// <param name="high">The inclusive upper bound of the range.</param>
    /// <param name="onPrime">The callback invoked once for each prime in the range, in ascending order.</param>
    /// <remarks>
    /// A segmented sieve over <see cref="PrimeKernels.MarkWindow(ReadOnlySpan{uint}, Span{ulong}, ulong, ulong)"/> — the
    /// same window marker <see cref="PrimeExtensions.NthPrime(uint)"/> walks its own sieve with, so the two strides are
    /// one body rather than two transcriptions of one idea. The base primes are found once, process-wide and lazily,
    /// then used to strike composites out of fixed-size windows of the range; only that shared table and one rented
    /// bitmap are held, so the working set depends on the window size rather than on the range's length. The enumeration
    /// is deterministic. Even values and values below two are never reported.
    /// The supported upper bound is <see cref="uint.MaxValue"/>; this keeps the complete base-prime table bounded to
    /// the primes through 65,535 instead of accepting a <see cref="ulong"/> input whose square-root sieve cannot be
    /// represented by this in-memory implementation.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onPrime"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="high"/> exceeds <see cref="uint.MaxValue"/>.</exception>
    public static void SegmentedPrimeSieve(ulong low, ulong high, Action<ulong> onPrime) {
        ArgumentNullException.ThrowIfNull(argument: onPrime);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: high,
            other: MaximumSieveBound
        );

        if (high < low) { return; }
        if (2UL >= low) {
            if (high >= 2UL) { onPrime(2UL); }

            low = 3UL;
        }
        if (high < low) { return; }

        // The window marker starts striking at each prime's own square, which is only correct from three upward.
        var windowLow = ((0UL == (low & 1UL))
            ? (low + 1UL)
            : low
        );
        var basePrimes = PrimeKernels.BasePrimes;
        var bitmap = ArrayPool<ulong>.Shared.Rent(minimumLength: WindowWords);

        try {
            while (windowLow <= high) {
                var windowHigh = Math.Min(
                    val1: (windowLow + WindowSpan),
                    val2: high
                );
                var bits = (((windowHigh - windowLow) >> 1) + 1UL);

                PrimeKernels.MarkWindow(
                    basePrimes: basePrimes,
                    bitmap: bitmap,
                    bits: bits,
                    low: windowLow
                );

                // Report by walking the clear bits of each word rather than testing every bit: the cost is
                // proportional to the primes found, not to the window.
                var words = ((int)((bits + 63UL) >> 6));

                for (var word = 0; (word < words); ++word) {
                    var candidates = (~bitmap[word]);

                    if ((word == (words - 1)) && (0UL != (bits & 63UL))) {
                        candidates &= ((1UL << ((int)(bits & 63UL))) - 1UL);
                    }

                    while (0UL != candidates) {
                        var bit = ((((ulong)word) << 6) + ((ulong)BitOperations.TrailingZeroCount(value: candidates)));

                        onPrime((windowLow + (bit << 1)));
                        candidates &= (candidates - 1UL);
                    }
                }

                if (windowHigh == high) { break; }

                windowLow = (windowHigh + 2UL);
            }
        } finally {
            ArrayPool<ulong>.Shared.Return(array: bitmap);
        }
    }
}

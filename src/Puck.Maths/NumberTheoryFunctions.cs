using System.Buffers;
using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Provides exact number-theoretic routines: over arbitrary-width integers, the Jacobi symbol, a segmented prime sieve
/// over a range, and Hensel lifting of a polynomial root from a prime to a prime power; over machine words, modular
/// powers and inverses for any modulus and the extended greatest common divisor.
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
    /// <summary>The binary-GCD steps one inversion round batches: Pornin's <c>k − 1</c> at <c>k = 32</c>.</summary>
    private const int InversionSteps = 31;
    /// <summary>The inversion rounds, <c>⌈(2·64 − 1) / 31⌉</c>, enough steps for any pair of 64-bit operands.</summary>
    private const int InversionRounds = 5;

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
    /// <summary>Multiplies two residues modulo a word modulus through a 128-bit product, which never overflows.</summary>
    /// <param name="left">The first residue.</param>
    /// <param name="right">The second residue.</param>
    /// <param name="modulus">The non-zero modulus.</param>
    /// <returns>The product reduced into <c>[0, <paramref name="modulus"/>)</c>.</returns>
    private static ulong MultiplyModulo(ulong left, ulong right, ulong modulus) =>
        ((ulong)((((UInt128)left) * right) % modulus));
    /// <summary>Divides a signed multiple-precision residue combination by <c>2^<see cref="InversionSteps"/></c> modulo an odd
    /// modulus, the Montgomery way: adds the multiple of the modulus that clears the low bits, then shifts them out.</summary>
    /// <param name="combination">The combination <c>u·f + v·g</c> of two residues below the modulus and two update factors
    /// whose magnitudes sum to at most <c>2^<see cref="InversionSteps"/></c>, so its magnitude is below
    /// <c>modulus · 2^<see cref="InversionSteps"/></c>.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <param name="negatedInverse">The negation of the modulus's inverse modulo <c>2^64</c>.</param>
    /// <returns>The residue in <c>[0, <paramref name="modulus"/>)</c> congruent to <c>combination · 2^−<see cref="InversionSteps"/></c>.</returns>
    private static ulong ShiftOutModulo(Int128 combination, ulong modulus, ulong negatedInverse) {
        var clearing = (((ulong)combination) * negatedInverse) & ((1UL << InversionSteps) - 1UL);
        // The combination lies strictly between −modulus and modulus in units of 2^steps, and the clearing multiple adds
        // less than one more modulus, so the shifted value lies strictly between −modulus and 2·modulus.
        var shifted = ((combination + (((Int128)clearing) * modulus)) >> InversionSteps);

        if (shifted < Int128.Zero) {
            shifted += modulus;
        } else if (shifted >= modulus) {
            shifted -= modulus;
        }

        return ((ulong)shifted);
    }
    /// <summary>Inverts a residue modulo an odd modulus by Pornin's binary extended GCD, the halving steps batched into
    /// update factors applied once per round.</summary>
    /// <param name="value">The residue to invert, below <paramref name="modulus"/>.</param>
    /// <param name="modulus">The odd modulus, at least three.</param>
    /// <param name="inverse">The inverse in <c>[1, <paramref name="modulus"/>)</c> when one exists; otherwise the
    /// residue the descent left, which is meaningless.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is coprime to <paramref name="modulus"/>.</returns>
    /// <remarks>This is Algorithm 2 of Pornin, "Optimized Binary GCD for Modular Inversion" (IACR ePrint 2020/972), with
    /// its word parameter <c>k</c> at 32. A 64-bit operand is its own <c>2k</c>-bit approximation, so the inner steps run
    /// on the exact values and the approximation and the sign fix-ups it would otherwise need never arise. Each inner
    /// step keeps <c>a·2^j = a₀·f₀ + b₀·g₀</c> and <c>b·2^j = a₀·f₁ + b₀·g₁</c>, so a round replaces the residues
    /// <c>(u, v)</c> by the same combinations divided by <c>2^31</c>. Every step lowers the two operands' summed bit
    /// length by at least one until the first reaches zero, so <c>2·64 − 1 = 127</c> steps finish the descent; five
    /// rounds of 31 steps run 155, a fixed count whatever the operands.</remarks>
    private static bool TryInvertOdd(ulong value, ulong modulus, out ulong inverse) {
        var negatedInverse = (0UL - modulus.ModularInverse());
        var a = value;
        var b = modulus;
        var u = 1UL;
        var v = 0UL;

        for (var round = 0; (round < InversionRounds); ++round) {
            var (f0, g0, f1, g1) = (1L, 0L, 0L, 1L);

            for (var step = 0; (step < InversionSteps); ++step) {
                if ((a & 1UL) != 0UL) {
                    if (a < b) {
                        (a, b) = (b, a);
                        (f0, g0, f1, g1) = (f1, g1, f0, g0);
                    }

                    a -= b;
                    f0 -= f1;
                    g0 -= g1;
                }

                a >>= 1;
                f1 <<= 1;
                g1 <<= 1;
            }

            (u, v) = (
                ShiftOutModulo(
                    combination: ((((Int128)u) * f0) + (((Int128)v) * g0)),
                    modulus: modulus,
                    negatedInverse: negatedInverse
                ),
                ShiftOutModulo(
                    combination: ((((Int128)u) * f1) + (((Int128)v) * g1)),
                    modulus: modulus,
                    negatedInverse: negatedInverse
                )
            );
        }

        // The descent ends with the first operand at zero and the second at the greatest common divisor, which v
        // multiplies the value to.
        inverse = v;

        return (1UL == b);
    }

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
    /// <summary>Computes the greatest common divisor of two integers together with the Bézout coefficients that combine
    /// the integers into it.</summary>
    /// <param name="value">The first integer, <c>a</c>; any sign, but not <see cref="long.MinValue"/>.</param>
    /// <param name="other">The second integer, <c>b</c>; any sign, but not <see cref="long.MinValue"/>.</param>
    /// <returns>The divisor <c>g = gcd(|a|, |b|)</c> and coefficients with <c>a·X + b·Y = g</c>. When <c>b</c> is zero they
    /// are <c>(|a|, sign(a), 0)</c>, so <c>(0, 0, 0)</c> for two zeros. Otherwise <c>X</c> is the representative of
    /// <c>(a/g)⁻¹</c> modulo <c>|b|/g</c> in <c>[−|b|/2g, |b|/2g]</c>, the least magnitude that residue class holds;
    /// the only tie, <c>|b|/g = 2</c>, takes <c>sign(a)</c> to minimize <c>|Y|</c>. Then
    /// <c>Y = (g − a·X) / b</c>, whose magnitude is at most <c>|a|/2g + 1</c>, so both always fit a
    /// <see cref="long"/>.</returns>
    /// <remarks>The coefficient comes from <see cref="ModularInverse(ulong, ulong)"/> and the divisor from
    /// <see cref="BinaryIntegerFunctions.GreatestCommonDivisor{T}(T, T)"/>, so the descent is binary throughout; the
    /// one division is the exact one that recovers <c>Y</c>.</remarks>
    /// <exception cref="OverflowException"><paramref name="value"/> or <paramref name="other"/> is
    /// <see cref="long.MinValue"/>, whose magnitude a <see cref="long"/> cannot hold.</exception>
    public static (long Divisor, long X, long Y) ExtendedGreatestCommonDivisor(long value, long other) {
        if (
            (long.MinValue == value) ||
            (long.MinValue == other)
        ) {
            throw new OverflowException(message: "The magnitude of the signed minimum is not representable.");
        }

        var divisor = value.GreatestCommonDivisor(other: other);

        if (0L == other) {
            return (divisor, Math.Sign(value: value), 0L);
        }

        var modulus = ((ulong)Math.Abs(value: (other / divisor)));
        var x = 0L;

        if (modulus > 1UL) {
            var inverse = ModularInverse(
                modulus: modulus,
                value: ((ulong)(value / divisor).FloorModulo(modulus: ((long)modulus)))
            );

            x = (((inverse > (modulus >> 1)) || ((modulus == 2UL) && (value < 0L)))
                ? (((long)inverse) - ((long)modulus))
                : ((long)inverse)
            );
        }

        return (divisor, x, ((long)((((Int128)divisor) - (((Int128)value) * x)) / other)));
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
    /// <summary>Computes the multiplicative inverse of a word modulo a word modulus without dividing.</summary>
    /// <param name="value">The value to invert, any magnitude; it must be coprime to <paramref name="modulus"/>.</param>
    /// <param name="modulus">The modulus, at least two.</param>
    /// <returns>The unique residue in <c>[1, <paramref name="modulus"/>)</c> whose product with <paramref name="value"/>
    /// is congruent to one modulo <paramref name="modulus"/>.</returns>
    /// <remarks>
    /// <para>The modulus splits as <c>2^s · m</c> with <c>m</c> odd. Modulo <c>m</c> the inverse comes from Pornin's
    /// optimized binary extended GCD ("Optimized Binary GCD for Modular Inversion", IACR ePrint 2020/972): five rounds of
    /// 31 shift-and-subtract steps whatever the operands, each round's halvings folded into one Montgomery shift of the
    /// running residues. Modulo <c>2^s</c> it is <see cref="UnsignedNumberFunctions.ModularInverse{T}(T)"/>'s
    /// Newton–Hensel inverse, masked, and the two recombine as <c>x₁ + m·((x₂ − x₁)·m⁻¹ mod 2^s)</c>. Nothing divides:
    /// the reductions are masks, shifts, and 128-bit products.</para>
    /// <para>This is the word-sized companion to <see cref="BigIntegerFunctions.ModularInverse(BigInteger, BigInteger)"/>,
    /// whose Euclidean descent serves any width at the price of a division per step.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> is zero or one.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> shares a factor with <paramref name="modulus"/>, so it
    /// has no inverse there.</exception>
    public static ulong ModularInverse(ulong value, ulong modulus) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            other: 2UL,
            value: modulus
        );

        var twos = BitOperations.TrailingZeroCount(value: modulus);
        var odd = (modulus >> twos);
        var oddInverse = 0UL;

        if (
            (odd > 1UL) &&
            !TryInvertOdd(
                inverse: out oddInverse,
                modulus: odd,
                value: (value % odd)
            )
        ) {
            throw NotInvertible();
        }
        if (0 == twos) {
            return oddInverse;
        }
        if (0UL == (value & 1UL)) {
            throw NotInvertible();
        }

        var mask = ((1UL << twos) - 1UL);
        var evenInverse = value.ModularInverse() & mask;

        return (oddInverse + (odd * (((evenInverse - oddInverse) * odd.ModularInverse()) & mask)));

        static ArgumentException NotInvertible() => new(
            message: "The value shares a factor with the modulus, so it has no multiplicative inverse there.",
            paramName: nameof(value)
        );
    }
    /// <summary>Raises a word to a power modulo a word modulus by square-and-multiply.</summary>
    /// <param name="value">The base, any magnitude.</param>
    /// <param name="exponent">The exponent.</param>
    /// <param name="modulus">The modulus, which must be positive.</param>
    /// <returns><c><paramref name="value"/>^<paramref name="exponent"/> mod <paramref name="modulus"/></c> in
    /// <c>[0, <paramref name="modulus"/>)</c>; zero to the zeroth power is one, reduced, so zero modulo one.</returns>
    /// <remarks>Each product is formed in 128 bits and reduced before the next, so no step overflows whatever the
    /// operands; the loop runs once per bit of <paramref name="exponent"/>, at most 64 times. A prime modulus has the
    /// faster Montgomery chain of <see cref="PrimeField64.Pow(ulong, ulong)"/>; this one serves every modulus.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> is zero.</exception>
    public static ulong ModularPower(ulong value, ulong exponent, ulong modulus) {
        ArgumentOutOfRangeException.ThrowIfZero(value: modulus);

        var power = (1UL % modulus);
        var square = (value % modulus);

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) {
                power = MultiplyModulo(
                    left: power,
                    modulus: modulus,
                    right: square
                );
            }

            exponent >>= 1;

            if (0UL != exponent) {
                square = MultiplyModulo(
                    left: square,
                    modulus: modulus,
                    right: square
                );
            }
        }

        return power;
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

                    if (
                        (word == (words - 1)) &&
                        (0UL != (bits & 63UL))
                    ) {
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

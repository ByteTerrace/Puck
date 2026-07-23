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
    /// <summary>Enumerates the primes in a closed range in ascending order.</summary>
    /// <param name="low">The inclusive lower bound of the range.</param>
    /// <param name="high">The inclusive upper bound of the range.</param>
    /// <param name="onPrime">The callback invoked once for each prime in the range, in ascending order.</param>
    /// <remarks>
    /// A segmented sieve: the primes through the square root of <paramref name="high"/> are found once, then used to
    /// strike composites out of fixed-size windows of the range. Only the base primes and one reusable window buffer
    /// are held, so the working set depends on the square root of the bound and the window size rather than on the
    /// range's length. The enumeration is deterministic. Even values and values below two are never reported.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onPrime"/> is <see langword="null"/>.</exception>
    public static void SegmentedPrimeSieve(ulong low, ulong high, Action<ulong> onPrime) {
        ArgumentNullException.ThrowIfNull(argument: onPrime);

        if (high < low) { return; }
        if (2UL >= low) {
            if (high >= 2UL) { onPrime(2UL); }

            low = 3UL;
        }
        if (high < low) { return; }

        var start = ((0UL == (low & 1UL)) ? (low + 1UL) : low);
        var basePrimes = SmallOddPrimes(bound: IntegerSquareRoot(value: high));

        const int WindowSpan = (1 << 16);
        var window = new bool[WindowSpan];

        for (var windowLow = start; (windowLow <= high); windowLow += (2UL * WindowSpan)) {
            var windowHigh = ((high - windowLow) < ((2UL * WindowSpan) - 2UL)) ? high : (windowLow + ((2UL * WindowSpan) - 2UL));
            var slots = (((int)((windowHigh - windowLow) >> 1)) + 1);

            Array.Clear(array: window, index: 0, length: slots);

            foreach (var prime in basePrimes) {
                var square = (prime * prime);

                if (square > windowHigh) { break; }

                // First odd multiple of `prime` at or above `windowLow`, never below the prime's own square.
                var first = ((square >= windowLow) ? square : (windowLow + ((prime - (windowLow % prime)) % prime)));

                if (0UL == (first & 1UL)) { first += prime; }

                for (var composite = first; (composite <= windowHigh); composite += (2UL * prime)) {
                    window[(int)((composite - windowLow) >> 1)] = true;
                }
            }

            for (var slot = 0; (slot < slots); ++slot) {
                if (!window[slot]) { onPrime(windowLow + (((ulong)slot) << 1)); }
            }
        }
    }
    /// <summary>Enumerates the primes in a closed range in ascending order as a lazy sequence.</summary>
    /// <param name="low">The inclusive lower bound of the range.</param>
    /// <param name="high">The inclusive upper bound of the range.</param>
    /// <returns>The primes in <c>[<paramref name="low"/>, <paramref name="high"/>]</c>, ascending.</returns>
    /// <remarks>A materializing convenience over <see cref="SegmentedPrimeSieve(ulong, ulong, Action{ulong})"/>; the callback form allocates nothing per prime and is preferred on a hot path.</remarks>
    public static IEnumerable<ulong> EnumeratePrimes(ulong low, ulong high) {
        var primes = new List<ulong>();

        SegmentedPrimeSieve(low: low, high: high, onPrime: primes.Add);

        return primes;
    }
    /// <summary>Lifts a simple root of an integer polynomial from a prime to a prime power.</summary>
    /// <param name="coefficients">The polynomial coefficients from the constant term upward: index <c>i</c> is the coefficient of <c>x^i</c>.</param>
    /// <param name="root">A root of the polynomial modulo <paramref name="prime"/>.</param>
    /// <param name="prime">The prime the root is known modulo.</param>
    /// <param name="targetPower">The exponent of the target modulus <c>prime^targetPower</c>, at least one.</param>
    /// <returns>The unique root congruent to <paramref name="root"/> modulo <paramref name="prime"/> that solves the polynomial modulo <c>prime^targetPower</c>.</returns>
    /// <remarks>
    /// <para>
    /// One power is gained per step: a root modulo <c>prime^k</c> is corrected by a multiple of <c>prime^k</c> chosen
    /// so the value vanishes modulo <c>prime^(k+1)</c>. The correction divides by the derivative, which is why this is
    /// the derivative-unit case.
    /// </para>
    /// <para>
    /// The lift is unique and this routine succeeds exactly when the derivative is a unit modulo <paramref name="prime"/>,
    /// that is, when <paramref name="root"/> is a simple root. When the derivative vanishes modulo <paramref name="prime"/>
    /// the step cannot be inverted: such a root either fails to lift or lifts non-uniquely, and neither outcome is a
    /// single return value, so the method rejects that input rather than guessing a branch.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="coefficients"/> is empty, <paramref name="root"/> is not a root modulo <paramref name="prime"/>, or the derivative vanishes modulo <paramref name="prime"/> so the root is not simple.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="prime"/> is below two or <paramref name="targetPower"/> is below one.</exception>
    public static BigInteger HenselLiftRoot(ReadOnlySpan<BigInteger> coefficients, BigInteger root, BigInteger prime, int targetPower) {
        if (coefficients.IsEmpty) {
            throw new ArgumentException(message: "The polynomial must have at least one coefficient.", paramName: nameof(coefficients));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(value: prime, other: BigInteger.One + BigInteger.One);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: targetPower, other: 1);

        if (!Evaluate(coefficients: coefficients, point: root).IsZeroModulo(modulus: prime)) {
            throw new ArgumentException(message: "The supplied value is not a root of the polynomial modulo the prime.", paramName: nameof(root));
        }

        var derivativeAtRoot = FloorModulo(value: EvaluateDerivative(coefficients: coefficients, point: root), modulus: prime);

        if (derivativeAtRoot.IsZero) {
            throw new ArgumentException(message: "The derivative vanishes modulo the prime, so the root is not simple and the derivative-unit lift does not apply.", paramName: nameof(root));
        }

        var inverseDerivative = ModularInverse(value: derivativeAtRoot, modulus: prime);
        var lifted = FloorModulo(value: root, modulus: prime);
        var modulus = prime;

        for (var power = 1; (power < targetPower); ++power) {
            var nextModulus = (modulus * prime);
            var deficit = FloorModulo(value: Evaluate(coefficients: coefficients, point: lifted), modulus: nextModulus);
            // deficit is a multiple of `modulus`; the step solves (deficit/modulus + t * f'(root)) ≡ 0 (mod prime).
            var step = FloorModulo(value: (-(deficit / modulus) * inverseDerivative), modulus: prime);

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
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value: denominator, other: BigInteger.Zero);

        if (denominator.IsEven) {
            throw new ArgumentOutOfRangeException(paramName: nameof(denominator), message: "The Jacobi symbol requires an odd positive denominator.");
        }

        var upper = FloorModulo(value: numerator, modulus: denominator);
        var lower = denominator;
        var sign = 1;

        while (!upper.IsZero) {
            while (upper.IsEven) {
                upper >>= 1;

                var residue = (int)(lower & 7);

                if ((3 == residue) || (5 == residue)) { sign = -sign; }
            }

            (upper, lower) = (lower, upper);

            if ((3 == (int)(upper & 3)) && (3 == (int)(lower & 3))) { sign = -sign; }

            upper %= lower;
        }

        return ((BigInteger.One == lower) ? sign : 0);
    }

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
    /// <summary>Reduces a value into the non-negative residues modulo a positive modulus.</summary>
    /// <param name="value">The value to reduce.</param>
    /// <param name="modulus">The positive modulus.</param>
    /// <returns>The representative in <c>[0, modulus)</c>.</returns>
    private static BigInteger FloorModulo(BigInteger value, BigInteger modulus) {
        var residue = (value % modulus);

        return (residue.Sign < 0) ? (residue + modulus) : residue;
    }
    /// <summary>Gets whether a value is divisible by a modulus.</summary>
    /// <param name="value">The value to test.</param>
    /// <param name="modulus">The modulus.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is congruent to zero modulo <paramref name="modulus"/>.</returns>
    private static bool IsZeroModulo(this BigInteger value, BigInteger modulus) =>
        (value % modulus).IsZero;
    /// <summary>Computes the integer square root of an unsigned value.</summary>
    /// <param name="value">The value whose floor square root is computed.</param>
    /// <returns>The largest value whose square does not exceed <paramref name="value"/>.</returns>
    private static ulong IntegerSquareRoot(ulong value) {
        if (0UL == value) { return 0UL; }

        var estimate = ((ulong)Math.Sqrt(d: value));

        while ((estimate * estimate) > value) { --estimate; }

        while (((estimate + 1UL) <= 4294967295UL) && (((estimate + 1UL) * (estimate + 1UL)) <= value)) { ++estimate; }

        return estimate;
    }
    /// <summary>Computes the modular inverse of a value coprime to the modulus by the extended greatest-common-divisor recursion.</summary>
    /// <param name="value">The value to invert.</param>
    /// <param name="modulus">The modulus.</param>
    /// <returns>The representative in <c>[0, modulus)</c> whose product with <paramref name="value"/> is one modulo <paramref name="modulus"/>.</returns>
    private static BigInteger ModularInverse(BigInteger value, BigInteger modulus) {
        var previousRemainder = modulus;
        var remainder = FloorModulo(value: value, modulus: modulus);
        var previousCoefficient = BigInteger.Zero;
        var coefficient = BigInteger.One;

        while (!remainder.IsZero) {
            var quotient = (previousRemainder / remainder);

            (previousRemainder, remainder) = (remainder, (previousRemainder - (quotient * remainder)));
            (previousCoefficient, coefficient) = (coefficient, (previousCoefficient - (quotient * coefficient)));
        }

        return FloorModulo(value: previousCoefficient, modulus: modulus);
    }
    /// <summary>Sieves the odd primes up to a bound for use as segment base primes.</summary>
    /// <param name="bound">The inclusive upper bound.</param>
    /// <returns>The odd primes through <paramref name="bound"/>, ascending.</returns>
    private static ulong[] SmallOddPrimes(ulong bound) {
        if (3UL > bound) { return []; }

        var span = ((int)bound);
        var composite = new bool[span + 1];
        var primes = new List<ulong>();

        for (var candidate = 3; (candidate <= span); candidate += 2) {
            if (composite[candidate]) { continue; }

            primes.Add((ulong)candidate);

            for (var multiple = ((long)candidate * candidate); (multiple <= span); multiple += (2L * candidate)) {
                composite[(int)multiple] = true;
            }
        }

        return primes.ToArray();
    }
}

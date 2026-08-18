using System.Numerics;

namespace Puck.Maths.Tests;

internal static partial class Oracles {
    // ---- the odd-characteristic references ----
    // Nothing below rounds: every answer is an exact integer, so the module's ties-to-even discipline does not arise
    // here at all. What these references are for is the OTHER failure modes — a wrong modulus fold, a Montgomery
    // representation leaking into an answer, a width or carry edge — and each is written to reach its answer by a
    // different ROUTE from the subject it stands against, not merely by different code.

    /// <summary>The primes below 8192, sieved once — the trial-division screen the primality references open
    /// with.</summary>
    private static readonly uint[] SmallPrimeTable = BuildSmallPrimes();

    /// <summary>The primality flags for every value through <paramref name="inclusiveMaximum"/>, by the sieve of
    /// Eratosthenes.</summary>
    /// <param name="inclusiveMaximum">The largest value to decide.</param>
    /// <returns>One flag per value from zero through <paramref name="inclusiveMaximum"/>.</returns>
    /// <remarks>The one reference in this module that carries no notion of a witness, a base or an exponent: it
    /// crosses out multiples and reads the survivors. That is what makes it the strongest anchor the primality laws
    /// have — it shares with <see cref="PrimeField64.IsPrime(ulong)"/> not merely no code but no IDEA.</remarks>
    public static bool[] PrimeSieve(int inclusiveMaximum) {
        var flags = new bool[(inclusiveMaximum + 1)];

        for (var index = 2; (index <= inclusiveMaximum); ++index) { flags[index] = true; }

        for (var candidate = 2; ((candidate * candidate) <= inclusiveMaximum); ++candidate) {
            if (!flags[candidate]) { continue; }

            for (var multiple = (candidate * candidate); (multiple <= inclusiveMaximum); multiple += candidate) { flags[multiple] = false; }
        }

        return flags;
    }

    /// <summary>The primes below 8192, sieved once by <see cref="PrimeSieve(int)"/>.</summary>
    /// <remarks>Trial division by this list decides every value through <c>8191² = 67092481</c> — just under
    /// <c>2²⁶</c> — outright, with no probable-prime reasoning anywhere in the answer. Deliberately 8192 rather than
    /// 65536: the screen costs one remainder per entry on every candidate that reaches the rounds, and that band is
    /// already well past the region the exhaustive-sieve statements cover directly.</remarks>
    public static ReadOnlySpan<uint> SmallPrimes => SmallPrimeTable;
    /// <summary>The first twenty prime bases, a strict SUPERSET of the twelve
    /// <see cref="PrimeField64.IsPrime(ulong)"/> runs.</summary>
    public static ReadOnlySpan<ulong> StrongPrimeWitnessBases => [2UL, 3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 19UL, 23UL, 29UL, 31UL, 37UL, 41UL, 43UL, 47UL, 53UL, 59UL, 61UL, 67UL, 71UL];

    /// <summary>The Jacobi symbol of <paramref name="numerator"/> over an odd positive
    /// <paramref name="denominator"/>, by the binary quadratic-reciprocity descent in
    /// <see cref="BigInteger"/>.</summary>
    /// <param name="numerator">The upper argument, of either sign; it is normalized into the denominator's range
    /// first.</param>
    /// <param name="denominator">The lower argument, which must be odd and positive.</param>
    /// <returns><c>0</c> when the two share a factor, <c>1</c> when the numerator is a residue, <c>-1</c>
    /// otherwise.</returns>
    /// <remarks>Calls no Puck.Maths member — in particular it is neither <c>NumberTheoryFunctions.JacobiSymbol</c>
    /// nor <c>UnsignedNumberFunctions.JacobiSymbol</c>, both of which are SUBJECTS of core.jacobi-symbol-cross-carrier's jacobi
    /// statement. Written against the shipped kernels in two ways a reader can check: those accumulate the sign in bit
    /// zero of a parity word through the <c>((lower &gt;&gt; 1) ^ lower) &gt;&gt; 1</c> bit identities and strip the
    /// whole trailing-zero run in one <c>TrailingZeroCount</c>, where this carries a signed accumulator, tests
    /// <c>% 8</c> and <c>% 4</c> literally, and strips one factor of two at a time. It is also a wholly different
    /// derivation route from <see cref="PrimeField64.LegendreCharacter(ulong)"/>, which decides the character by
    /// Euler's exponentiation criterion and never touches reciprocity at all.</remarks>
    public static int JacobiSymbolReciprocity(BigInteger numerator, BigInteger denominator) {
        var lower = denominator;
        var upper = (((numerator % lower) + lower) % lower);
        var sign = 1;

        while (!upper.IsZero) {
            // One factor of two at a time, each flipping the sign exactly where the (2/n) rule says it does. Stated
            // as a residue test rather than as a parity bit identity, which is the whole point of this spelling.
            while (upper.IsEven) {
                upper >>= 1;

                var residue = (lower % 8);

                if ((3 == residue) || (5 == residue)) { sign = -sign; }
            }

            if ((3 == (upper % 4)) && (3 == (lower % 4))) { sign = -sign; }

            (lower, upper) = (upper, (lower % upper));
        }

        return (lower.IsOne ? sign : 0);
    }
    /// <summary>One strong-probable-prime round of <paramref name="value"/> to base <paramref name="witness"/>,
    /// evaluated entirely in <see cref="BigInteger"/> plain residues.</summary>
    /// <param name="value">The candidate.</param>
    /// <param name="witness">The witness base, reduced modulo the candidate first.</param>
    /// <returns><see langword="true"/> when the candidate is a strong probable prime to that base.</returns>
    /// <remarks>A base that reduces to zero carries no evidence and PASSES, which is the contract
    /// <see cref="PrimeField64.IsStrongProbablePrime(ulong, ulong)"/> states. Plain residues from first to last —
    /// <see cref="BigInteger.ModPow(BigInteger, BigInteger, BigInteger)"/> and <c>%</c>, never a Montgomery encoding
    /// and never a ring's own one or minus one — where the subject makes its whole acceptance test against the
    /// ring's encoded constants and never leaves Montgomery form. The CRITERION is shared and the legs say so; what
    /// disagreement here catches is the arithmetic, the <c>d·2^s</c> split and a representation leak.</remarks>
    public static bool ModularStrongProbablePrime(ulong value, ulong witness) {
        if (2UL > value) { return false; }
        if (2UL == value) { return true; }
        if (0UL == (value & 1UL)) { return false; }

        var residue = (witness % value);

        if (0UL == residue) { return true; }

        var oddPart = (value - 1UL);
        var twoExponent = 0;

        while (0UL == (oddPart & 1UL)) {
            oddPart >>= 1;
            ++twoExponent;
        }

        var wide = new BigInteger(value: value);
        var last = (wide - BigInteger.One);
        var power = BigInteger.ModPow(value: new BigInteger(value: residue), exponent: new BigInteger(value: oddPart), modulus: wide);

        if (power.IsOne || (power == last)) { return true; }

        for (var round = 1; (round < twoExponent); ++round) {
            power = ((power * power) % wide);

            if (power == last) { return true; }
        }

        return false;
    }
    /// <summary>The EXACT primality decision for every <see cref="ulong"/>, in two layers: trial division by
    /// <see cref="SmallPrimes"/>, then twenty strong-probable-prime rounds against
    /// <see cref="StrongPrimeWitnessBases"/>.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns><see langword="true"/> when the candidate is prime.</returns>
    /// <remarks>
    /// <para>
    /// Trial division alone decides every value through <c>8191² = 67092481</c>, with no probable-prime reasoning in
    /// the answer at all. Above that the rounds decide, and the decision is still exact: the first TWELVE prime bases
    /// are a proven complete witness set for every value below Sorenson and Webster's computed
    /// <c>ψ₁₂ = 318665857834031151167461 ≈ 3.19 × 10²³</c>, four orders of magnitude past
    /// <see cref="ulong.MaxValue"/>, and twenty bases are a strict superset — extra bases can only reject more
    /// composites, and every prime passes every base. That provenance is a third-party exhaustive computation, the
    /// same epistemic class as the Baillie–PSW guarantee, which likewise rests on a third-party enumeration —
    /// Feitsma's and Galway's independent lists of the base-2 Fermat pseudoprimes below <c>2⁶⁴</c> — rather than on a
    /// proof from first principles. Neither is stronger than the other; do not describe one that way.
    /// </para>
    /// <para>
    /// Deliberately OUTSIDE Puck.Maths rather than borrowing <see cref="PrimeField64.IsPrime(ulong)"/>, so that a
    /// future re-pointing of that member at the Baillie–PSW composition turns no tier of this family into a tautology.
    /// </para>
    /// </remarks>
    public static bool ExactPrimality(ulong value) {
        if (2UL > value) { return false; }

        // Fixed-width and exact: the screen's operands are the candidate itself and a prime below 8192, whose square
        // is below 2^26, so nothing here can leave the carrier and BigInteger would buy only wall time.
        foreach (var small in SmallPrimeTable) {
            var prime = ((ulong)small);

            if ((prime * prime) > value) { return true; }
            if (0UL == (value % prime)) { return (value == prime); }
        }

        foreach (var witness in StrongPrimeWitnessBases) {
            if (!ModularStrongProbablePrime(value: value, witness: witness)) { return false; }
        }

        return true;
    }
    /// <summary>The strong Lucas probable-prime test with Selfridge's Method A parameters, computed from
    /// COMPANION-MATRIX powers in <see cref="BigInteger"/>.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns><see langword="true"/> when the candidate is a strong Lucas probable prime.</returns>
    /// <remarks>
    /// <para>
    /// Independent of <see cref="PrimeField64.IsStrongLucasProbablePrime(ulong)"/> in three deliberate ways. The
    /// subject walks the <c>U</c>/<c>V</c> doubling identities most-significant-bit first, HALVING on every
    /// index-incrementing step, entirely inside a Montgomery ring; this multiplies two-by-two matrices in plain
    /// residues, halves nothing anywhere, and carries no <c>V</c> recurrence of its own at all — it derives
    /// <c>V_d</c> from two consecutive <c>U</c> terms through <c>V_n = 2·U_(n+1) − P·U_n</c>. Its Selfridge search
    /// carries the discriminant's sign EXPLICITLY, where the subject reads the sign off <c>magnitude &amp; 3</c>, and
    /// takes its symbol from <see cref="JacobiSymbolReciprocity(BigInteger, BigInteger)"/> rather than from any
    /// shipped Jacobi. A wrong index-incrementing pair, a dropped <c>Q^k</c> squaring, or a halving that leaves the
    /// carrier shows here and nowhere else.
    /// </para>
    /// <para>
    /// It is <c>O(log n)</c>, so a law standing on it reaches the whole carrier rather than the small band an
    /// index-by-index recurrence oracle can afford.
    /// </para>
    /// </remarks>
    public static bool StrongLucasSelfridge(ulong value) {
        if (2UL > value) { return false; }
        if (2UL == value) { return true; }
        if (0UL == (value & 1UL)) { return false; }
        if (PerfectSquareByBisection(value: value)) { return false; }

        var wide = new BigInteger(value: value);
        var discriminant = new BigInteger(value: 5);
        var step = 0;

        // Method A: 5, −7, 9, −11, 13, … with the sign carried as a sign, not derived from a magnitude's low bits.
        while (true) {
            var symbol = JacobiSymbolReciprocity(denominator: wide, numerator: discriminant);

            if (-1 == symbol) { break; }
            // A vanishing symbol means the candidate shares a factor with the discriminant. That factor is a proper
            // divisor unless the value divides the discriminant outright, which leaves the search uninformed.
            if ((0 == symbol) && !(discriminant % wide).IsZero) { return false; }

            ++step;

            var magnitude = (BigInteger.Abs(value: discriminant) + 2);

            discriminant = ((1 == (step & 1)) ? (-magnitude) : magnitude);
        }

        // P = 1 and Q = (1 − D)/4, exact in BigInteger in either sign class: no shift, no ring negation.
        var q = (((((BigInteger.One - discriminant) / 4) % wide) + wide) % wide);
        var order = (wide + BigInteger.One);
        var twoExponent = 0;

        while (order.IsEven) {
            order >>= 1;
            ++twoExponent;
        }

        var (atIndex, atNextIndex) = LucasNumeratorPair(index: ((ulong)order), modulus: wide, q: q);
        var v = (((((2 * atNextIndex) - atIndex) % wide) + wide) % wide);

        if (atIndex.IsZero || v.IsZero) { return true; }

        var qPower = BigInteger.ModPow(exponent: order, modulus: wide, value: q);

        for (var round = 1; (round < twoExponent); ++round) {
            v = (((((v * v) - (2 * qPower)) % wide) + wide) % wide);

            if (v.IsZero) { return true; }

            qPower = ((qPower * qPower) % wide);
        }

        return false;
    }
    /// <summary>The reference modular inverse, by the EXTENDED EUCLIDEAN algorithm.</summary>
    /// <param name="value">The value to invert; must be coprime to <paramref name="modulus"/>.</param>
    /// <param name="modulus">The modulus, above one.</param>
    /// <returns>The representative in <c>[0, modulus)</c> whose product with <paramref name="value"/> is one.</returns>
    /// <remarks>Euclid's algorithm is a DIFFERENT THEOREM from the Fermat exponentiation the subject reaches its base
    /// inverse by — <see cref="PrimeField64.Inverse(ulong)"/> evaluates <c>value^(p − 2)</c> — and it runs in plain
    /// residues with no Montgomery form anywhere. Exact on both sides, so the module's rounding discipline does not
    /// arise.</remarks>
    /// <exception cref="ArgumentException">The greatest common divisor is not one, so no inverse exists.</exception>
    public static BigInteger ModularInverse(BigInteger value, BigInteger modulus) {
        var remainder = (((value % modulus) + modulus) % modulus);
        var previousRemainder = modulus;
        var coefficient = BigInteger.One;
        var previousCoefficient = BigInteger.Zero;

        while (!remainder.IsZero) {
            var quotient = BigInteger.Divide(dividend: previousRemainder, divisor: remainder);

            (previousRemainder, remainder) = (remainder, (previousRemainder - (quotient * remainder)));
            (previousCoefficient, coefficient) = (coefficient, (previousCoefficient - (quotient * coefficient)));
        }

        if (!previousRemainder.IsOne) {
            throw new ArgumentException(message: "The value is not invertible modulo the modulus.", paramName: nameof(value));
        }

        return (((previousCoefficient % modulus) + modulus) % modulus);
    }
    /// <summary>The reference quadratic character by EULER'S CRITERION, evaluated with
    /// <see cref="BigInteger"/>'s own modular exponentiation.</summary>
    /// <param name="value">The value whose character is taken, of either sign.</param>
    /// <param name="modulus">The odd prime.</param>
    /// <returns><c>0</c> at a value congruent to zero, <c>1</c> at a non-zero square, and <c>-1</c> at a
    /// non-square.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="BigInteger.ModPow(BigInteger, BigInteger, BigInteger)"/> in PLAIN residues, where the subject's
    /// <see cref="PrimeField64.LegendreCharacter(ulong)"/> runs its exponentiation in <c>ScaledResidueRing64</c>'s
    /// Montgomery form. The argument is reduced BEFORE the test, which is the difference that matters: this reference
    /// answers <c>0</c> for every value congruent to zero, including the unreduced ones the subject answers <c>-1</c>
    /// for.
    /// </para>
    /// <para>
    /// The last line maps every non-one power to <c>-1</c>. For a prime modulus that power is <c>1</c> or
    /// <c>modulus − 1</c> and nothing else, and every caller passes a prime, so no third case exists;
    /// <c>extension-field.construction-and-refusals</c> additionally asserts the <c>modulus − 1</c> half on the
    /// answers it uses, so a composite slipping in would be caught rather than silently classified.
    /// </para>
    /// </remarks>
    public static int PrimeFieldCharacter(BigInteger value, BigInteger modulus) {
        var residue = (((value % modulus) + modulus) % modulus);

        if (residue.IsZero) { return 0; }

        var power = BigInteger.ModPow(value: residue, exponent: ((modulus - BigInteger.One) / 2), modulus: modulus);

        return (power.IsOne ? 1 : -1);
    }
    /// <summary>The least value at or above two whose quadratic character over the modulus is <c>-1</c>.</summary>
    /// <param name="modulus">The odd prime.</param>
    /// <param name="budget">The maximum number of candidates to test; exhausting it is a FAILURE, not an
    /// answer.</param>
    /// <returns>The smallest quadratic non-residue.</returns>
    /// <remarks>Non-residues are half of the non-zero residues, so the smallest is small for every prime and the budget
    /// is never reached in practice. It is declared and enforced anyway: an unbounded search whose predicate has gone
    /// uniformly false must trip a named failure rather than spin.</remarks>
    /// <exception cref="InvalidOperationException">The budget was exhausted.</exception>
    public static BigInteger SmallestQuadraticNonResidue(BigInteger modulus, int budget) {
        var candidate = new BigInteger(value: 2);

        for (var step = 0; (step < budget); ++step) {
            if (-1 == PrimeFieldCharacter(modulus: modulus, value: candidate)) { return candidate; }

            candidate += BigInteger.One;
        }

        throw new InvalidOperationException(message: $"No quadratic non-residue below {candidate} over the modulus {modulus} within a budget of {budget} candidates.");
    }
    /// <summary>The exact cyclic convolution of two length-<c>N</c> sequences modulo <paramref name="modulus"/>, by
    /// the O(N^2) definition in <see cref="BigInteger"/>.</summary>
    /// <param name="left">The first sequence.</param>
    /// <param name="right">The second sequence, the same length as <paramref name="left"/>.</param>
    /// <param name="modulus">The odd prime <see cref="NumberTheoreticTransform.Convolve"/> reduces against.</param>
    /// <returns>The convolution, the same length as the operands.</returns>
    /// <remarks>Forms every product in full width and reduces once at the end of each output's sum, sharing no code
    /// and no algorithm with the subject's transform-multiply-invert route: <c>NumberTheoreticTransform.Convolve</c>
    /// never runs this double loop at all.</remarks>
    public static ulong[] CyclicConvolutionModulus(ReadOnlySpan<ulong> left, ReadOnlySpan<ulong> right, ulong modulus) {
        var n = left.Length;
        var wideModulus = new BigInteger(value: modulus);
        var result = new ulong[n];

        for (var k = 0; (k < n); ++k) {
            var sum = BigInteger.Zero;

            for (var i = 0; (i < n); ++i) {
                var j = ((((k - i) % n) + n) % n);

                sum += (new BigInteger(value: left[i]) * new BigInteger(value: right[j]));
            }

            result[k] = ((ulong)(sum % wideModulus));
        }

        return result;
    }
    /// <summary>The reference product of an element of <c>F_p(sqrt(d))</c> with its own conjugate, by
    /// <see cref="PrimeFieldPolynomialProduct"/> against the tail <c>[p − d, 0]</c>. The first coordinate is the field
    /// NORM; the second must vanish, which the caller asserts.</summary>
    /// <param name="a">The base-field part of the element.</param>
    /// <param name="b">The coefficient of the adjoined root.</param>
    /// <param name="nonSquare">The reduced quadratic non-square the extension adjoins a root of.</param>
    /// <param name="modulus">The odd prime.</param>
    /// <returns>Both reduced coefficients of the conjugate product.</returns>
    /// <remarks>Reaches the norm as a PRODUCT WITH THE CONJUGATE reduced as a polynomial, where the subject evaluates
    /// the closed form <c>A^2 − d·B^2</c> as three separate reduced base products and one conditional-fold subtraction
    /// (QuadraticExtensionField64.cs:166-170). A different derivation, not a transcription; exact on both sides, so the
    /// module's rounding discipline does not arise. That the second coordinate vanishes is a STATEMENT the caller
    /// checks, not an assumption this reference makes.</remarks>
    public static (ulong A, ulong B) QuadraticExtensionConjugateProduct(ulong a, ulong b, ulong nonSquare, ulong modulus) {
        Span<ulong> left = stackalloc ulong[2];
        Span<ulong> right = stackalloc ulong[2];
        Span<ulong> tail = stackalloc ulong[2];
        Span<ulong> result = stackalloc ulong[2];
        var rootPart = (b % modulus);

        left[0] = (a % modulus);
        left[1] = rootPart;
        right[0] = left[0];
        right[1] = ((modulus - rootPart) % modulus);
        tail[0] = ((modulus - (nonSquare % modulus)) % modulus);
        tail[1] = 0UL;

        PrimeFieldPolynomialProduct(left: left, modulus: modulus, result: result, right: right, tail: tail);

        return (result[0], result[1]);
    }
    /// <summary>The reference power of an element of <c>F_p(sqrt(d))</c>, by MOST-significant-bit-first binary
    /// exponentiation whose multiplication step is <see cref="PrimeFieldPolynomialProduct"/> against the tail
    /// <c>[p − d, 0]</c>.</summary>
    /// <param name="a">The base-field part of the element.</param>
    /// <param name="b">The coefficient of the adjoined root.</param>
    /// <param name="exponent">The power; zero yields <c>(1, 0)</c>.</param>
    /// <param name="nonSquare">The reduced quadratic non-square the extension adjoins a root of.</param>
    /// <param name="modulus">The odd prime.</param>
    /// <returns>The reduced pair.</returns>
    /// <remarks>Two independences at once. The SCHEDULE walks the exponent from its top bit down, squaring the
    /// accumulator; the subject walks it from the bottom up, squaring a running power it multiplies in
    /// (QuadraticExtensionField64.cs:176-189), so the two visit different intermediate values in a different order. The
    /// STEP is schoolbook polynomial reduction rather than the closed pair formula, so no line of the product rule is
    /// shared either. Everything is <see cref="BigInteger"/> inside the shared product; no Puck.Maths kernel is
    /// called.</remarks>
    public static (ulong A, ulong B) QuadraticExtensionPower(ulong a, ulong b, ulong exponent, ulong nonSquare, ulong modulus) {
        Span<ulong> tail = stackalloc ulong[2];
        Span<ulong> accumulator = stackalloc ulong[2];
        Span<ulong> baseValue = stackalloc ulong[2];
        Span<ulong> scratch = stackalloc ulong[2];

        tail[0] = ((modulus - (nonSquare % modulus)) % modulus);
        tail[1] = 0UL;
        baseValue[0] = (a % modulus);
        baseValue[1] = (b % modulus);
        accumulator[0] = (1UL % modulus);
        accumulator[1] = 0UL;

        if (0UL == exponent) { return (accumulator[0], accumulator[1]); }

        for (var bit = BitOperations.Log2(value: exponent); (bit >= 0); --bit) {
            PrimeFieldPolynomialProduct(left: accumulator, modulus: modulus, result: scratch, right: accumulator, tail: tail);
            scratch.CopyTo(destination: accumulator);

            if (0UL != ((exponent >>> bit) & 1UL)) {
                PrimeFieldPolynomialProduct(left: accumulator, modulus: modulus, result: scratch, right: baseValue, tail: tail);
                scratch.CopyTo(destination: accumulator);
            }
        }

        return (accumulator[0], accumulator[1]);
    }

    // Whether the value is a perfect square, by EXACT float-free bisection on [0, 2^32] — thirty-two halvings whose
    // only predicate is one exact squaring. Deliberately NOT the subject's ulong.SquareRoot(), whose first estimate
    // comes from hardware floating point and is the single floating-point touch the whole FiniteFields wing admits
    // to: a gate must not read the estimate it gates. Fixed width and exact rather than BigInteger because the
    // bracket bounds the square — a midpoint is strictly below 2^32, so its square is strictly below 2^64 and cannot
    // leave the carrier.
    private static bool PerfectSquareByBisection(ulong value) {
        var low = 0UL;
        var high = (1UL << 32);

        while ((high - low) > 1UL) {
            var middle = (low + ((high - low) >> 1));

            if ((middle * middle) <= value) { low = middle; } else { high = middle; }
        }

        return ((low * low) == value);
    }
    // The Lucas numerators U_index and U_(index+1) modulo the value, from the companion matrix M = [[P, −Q], [1, 0]]
    // with P = 1, by square-and-multiply over two-by-two BigInteger matrices. M^n carries (U_(n+1), U_n) in its first
    // column, which is what makes ONE matrix power answer both terms and lets the V terms be derived rather than
    // recurred.
    private static (BigInteger AtIndex, BigInteger AtNextIndex) LucasNumeratorPair(BigInteger q, ulong index, BigInteger modulus) {
        var resultA = BigInteger.One;
        var resultB = BigInteger.Zero;
        var resultC = BigInteger.Zero;
        var resultD = BigInteger.One;
        var baseA = BigInteger.One;
        var baseB = ((modulus - q) % modulus);
        var baseC = BigInteger.One;
        var baseD = BigInteger.Zero;
        var exponent = index;

        while (0UL != exponent) {
            if (0UL != (exponent & 1UL)) {
                var a = (((resultA * baseA) + (resultB * baseC)) % modulus);
                var b = (((resultA * baseB) + (resultB * baseD)) % modulus);
                var c = (((resultC * baseA) + (resultD * baseC)) % modulus);
                var d = (((resultC * baseB) + (resultD * baseD)) % modulus);

                resultA = a;
                resultB = b;
                resultC = c;
                resultD = d;
            }

            var squareA = (((baseA * baseA) + (baseB * baseC)) % modulus);
            var squareB = (((baseA * baseB) + (baseB * baseD)) % modulus);
            var squareC = (((baseC * baseA) + (baseD * baseC)) % modulus);
            var squareD = (((baseC * baseB) + (baseD * baseD)) % modulus);

            baseA = squareA;
            baseB = squareB;
            baseC = squareC;
            baseD = squareD;
            exponent >>= 1;
        }

        return (AtIndex: resultC, AtNextIndex: resultA);
    }
    private static uint[] BuildSmallPrimes() {
        var flags = PrimeSieve(inclusiveMaximum: 8191);
        var primes = new List<uint>();

        for (var index = 2; (index < flags.Length); ++index) {
            if (flags[index]) { primes.Add(item: ((uint)index)); }
        }

        return [.. primes];
    }

    /// <summary>One planar diagram: its two boundary widths, its balanced-parenthesis code, and its matching.</summary>
    /// <param name="InputWidth">The number of input wires.</param>
    /// <param name="OutputWidth">The number of output wires.</param>
    /// <param name="Code">The parenthesis word packed most significant point first, a set bit where a point closes an
    /// arc — so ascending codes ARE the lexicographic word order the catalogue keys by.</param>
    /// <param name="Partner">The matching: the point each boundary point is joined to. The inputs occupy the first
    /// <paramref name="InputWidth"/> points in order and the outputs the rest IN REVERSE, which is what makes a planar
    /// diagram a non-crossing matching of one circle of points.</param>
    public sealed record PlanarDiagram(int InputWidth, int OutputWidth, int Code, int[] Partner);

    /// <summary>Every planar diagram of both widths at most <paramref name="maximumWidth"/>, in the catalogue's
    /// canonical order.</summary>
    /// <param name="maximumWidth">The width bound.</param>
    /// <returns>The diagrams, ordered by input width, then output width, then code.</returns>
    /// <remarks>Shares no construction with the subject's enumeration: every mask of the boundary length is generated
    /// and then TESTED for balance, where the subject walks the balanced words directly under two prunings and never
    /// forms an unbalanced one. Only the declared order is common, because that order is the key scheme itself.</remarks>
    public static IReadOnlyList<PlanarDiagram> PlanarDiagrams(int maximumWidth) {
        var diagrams = new List<PlanarDiagram>();

        for (var inputs = 0; (inputs <= maximumWidth); ++inputs) {
            for (var outputs = 0; (outputs <= maximumWidth); ++outputs) {
                var points = (inputs + outputs);

                if (0 != (points & 1)) { continue; }

                for (var code = 0; (code < (1 << points)); ++code) {
                    var depth = 0;
                    var openers = new List<int>();
                    var partner = new int[points];

                    for (var position = 0; (position < points); ++position) {
                        if (0 == ((code >> ((points - 1) - position)) & 1)) {
                            ++depth;

                            openers.Add(item: position);

                            continue;
                        }

                        --depth;

                        if (depth < 0) { break; }

                        var opener = openers[^1];

                        openers.RemoveAt(index: (openers.Count - 1));

                        partner[opener] = position;
                        partner[position] = opener;
                    }

                    if ((depth != 0) || (openers.Count != 0)) { continue; }

                    diagrams.Add(item: new PlanarDiagram(Code: code, InputWidth: inputs, OutputWidth: outputs, Partner: partner));
                }
            }
        }

        return diagrams;
    }
    /// <summary>Indexes a planar basis by boundary shape and code, so a composite diagram can be named.</summary>
    /// <param name="basis">The basis, as <see cref="PlanarDiagrams"/> returns it.</param>
    /// <returns>The map from shape and code to the diagram's index in the basis.</returns>
    public static IReadOnlyDictionary<(int InputWidth, int OutputWidth, int Code), int> PlanarSymbols(IReadOnlyList<PlanarDiagram> basis) {
        var symbols = new Dictionary<(int InputWidth, int OutputWidth, int Code), int>();

        for (var index = 0; (index < basis.Count); ++index) {
            symbols[(basis[index].InputWidth, basis[index].OutputWidth, basis[index].Code)] = index;
        }

        return symbols;
    }
    /// <summary>Composes two planar diagrams by tracing their arcs, and counts the closed loops the composition strands
    /// off.</summary>
    /// <param name="basis">The basis.</param>
    /// <param name="left">The left diagram's index.</param>
    /// <param name="right">The right diagram's index, whose input width must be the left one's output width.</param>
    /// <returns>The composite's shape and code, and the loop count.</returns>
    /// <remarks>An explicit edge list and a union-find, which shares nothing with the subject's walk: the components
    /// are found by merging edges rather than by following one point at a time, the composite's arcs are read off the
    /// component a free point lands in, and the loop count is the components with no free point at all — a difference of
    /// two counts rather than an unvisited-node sweep. The count is a <see cref="BigInteger"/> throughout.</remarks>
    public static (int InputWidth, int OutputWidth, int Code, BigInteger Loops) PlanarCompose(IReadOnlyList<PlanarDiagram> basis, int left, int right) {
        var first = basis[left];
        var second = basis[right];
        var leftPoints = (first.InputWidth + first.OutputWidth);
        var rightPoints = (second.InputWidth + second.OutputWidth);
        var total = (leftPoints + rightPoints);
        var edges = new List<(int First, int Second)>();

        for (var point = 0; (point < leftPoints); ++point) {
            if (point < first.Partner[point]) { edges.Add(item: (point, first.Partner[point])); }
        }

        for (var point = 0; (point < rightPoints); ++point) {
            if (point < second.Partner[point]) { edges.Add(item: ((leftPoints + point), (leftPoints + second.Partner[point]))); }
        }

        // The glue: the left diagram's wire w is its output read backwards from the end, the right diagram's is its
        // input read forwards from the start.
        for (var wire = 0; (wire < first.OutputWidth); ++wire) {
            edges.Add(item: (((leftPoints - 1) - wire), (leftPoints + wire)));
        }

        var parent = new int[total];

        for (var node = 0; (node < total); ++node) { parent[node] = node; }

        int Root(int node) {
            while (parent[node] != node) { node = parent[node] = parent[parent[node]]; }

            return node;
        }

        foreach (var (one, other) in edges) { parent[Root(node: one)] = Root(node: other); }

        var free = new List<int>();

        for (var point = 0; (point < first.InputWidth); ++point) { free.Add(item: point); }
        for (var point = 0; (point < second.OutputWidth); ++point) { free.Add(item: ((leftPoints + second.InputWidth) + point)); }

        var components = new HashSet<int>();
        var openComponents = free.Select(selector: Root).ToHashSet();

        for (var node = 0; (node < total); ++node) { _ = components.Add(item: Root(node: node)); }

        var composite = new int[free.Count];

        for (var position = 0; (position < free.Count); ++position) {
            for (var other = 0; (other < free.Count); ++other) {
                if ((position != other) && (Root(node: free[position]) == Root(node: free[other]))) { composite[position] = other; }
            }
        }

        var code = 0;

        for (var position = 0; (position < composite.Length); ++position) {
            code = (code << 1) | ((composite[position] < position) ? 1 : 0);
        }

        return (first.InputWidth, second.OutputWidth, code, new BigInteger(value: (components.Count - openComponents.Count)));
    }
    /// <summary>Every distinct interleaving of two words, with the number of ways it is reached — the structure
    /// constants of the shuffle at an empty letter product, and of the quasi-shuffle at a non-empty one.</summary>
    /// <param name="left">The left word.</param>
    /// <param name="right">The right word.</param>
    /// <param name="letterProduct">The row-major table of merged letters, one per ordered pair of letters, or empty for
    /// the shuffle, where no two letters collide.</param>
    /// <param name="letterCount">The alphabet size, which indexes that table.</param>
    /// <returns>The interleavings, ascending by length and then lexicographically, each with its multiplicity.</returns>
    /// <remarks>Shares no construction with the subject's recursion on the two heads: every step-kind sequence of every
    /// block count is generated and then TESTED against the two words, so this counts by exhaustive filtering where the
    /// subject counts by reading three shorter cells. The multiplicities are <see cref="BigInteger"/> throughout, and the
    /// enumeration is exponential in the combined length, which is what keeps its callers small.</remarks>
    public static IReadOnlyList<(int[] Word, BigInteger Multiplicity)> Interleavings(int[] left, int[] right, int[] letterProduct, int letterCount) {
        var kinds = ((0 == letterProduct.Length) ? 2L : 3L);
        var multiplicities = new List<BigInteger>();
        var words = new List<int[]>();

        for (var blocks = 0; (blocks <= (left.Length + right.Length)); ++blocks) {
            var sequences = 1L;

            for (var step = 0; (step < blocks); ++step) { sequences *= kinds; }

            for (var sequence = 0L; (sequence < sequences); ++sequence) {
                var scan = sequence;
                var taken = 0;
                var word = new int[blocks];
                var used = 0;

                for (var block = 0; (block < blocks); ++block) {
                    var kind = ((int)(scan % kinds));

                    scan /= kinds;

                    if (((0 == kind) || (2 == kind)) && (taken >= left.Length)) { taken = -1; break; }
                    if (((1 == kind) || (2 == kind)) && (used >= right.Length)) { taken = -1; break; }

                    word[block] = kind switch {
                        0 => left[taken++],
                        1 => right[used++],
                        _ => letterProduct[((left[taken++] * letterCount) + right[used++])],
                    };
                }

                if ((taken != left.Length) || (used != right.Length)) { continue; }

                var low = 0;
                var high = words.Count;

                while (low < high) {
                    var middle = ((low + high) >> 1);
                    var order = CompareWords(left: words[middle], right: word);

                    if (0 == order) {
                        multiplicities[middle] += BigInteger.One;

                        low = -1;

                        break;
                    }

                    if (order < 0) { low = (middle + 1); } else { high = middle; }
                }

                if (low < 0) { continue; }

                multiplicities.Insert(index: low, item: BigInteger.One);
                words.Insert(index: low, item: word);
            }
        }

        var result = new (int[] Word, BigInteger Multiplicity)[words.Count];

        for (var index = 0; (index < result.Length); ++index) { result[index] = (words[index], multiplicities[index]); }

        return result;
    }
    /// <summary>Pascal's triangle down to a given row.</summary>
    /// <param name="rows">The last row.</param>
    /// <returns>The rows, row <c>n</c> carrying <c>n + 1</c> binomial coefficients.</returns>
    /// <remarks>Every entry is the sum of the two above it, so nothing here multiplies, divides or forms a factorial —
    /// which is what makes it an independent reading of a multiplicity some product computed.</remarks>
    public static BigInteger[][] PascalTriangle(int rows) {
        var triangle = new BigInteger[(rows + 1)][];

        for (var row = 0; (row <= rows); ++row) {
            triangle[row] = new BigInteger[(row + 1)];

            for (var column = 0; (column <= row); ++column) {
                triangle[row][column] = (((0 == column) || (row == column))
                    ? BigInteger.One
                    : (triangle[(row - 1)][(column - 1)] + triangle[(row - 1)][column]));
            }
        }

        return triangle;
    }
    /// <summary>The bracket state sum of a plat-closed braid word: every smoothing of every crossing enumerated, each
    /// state's closed curves counted, and the loop charge raised to that count.</summary>
    /// <param name="strandCount">The number of strands, which is even, since the closing cups and caps join adjacent
    /// pairs.</param>
    /// <param name="word">The braid word, one letter per crossing: <c>+i</c> for the crossing at strand <c>i</c> and
    /// <c>-i</c> for its mirror, read bottom to top.</param>
    /// <returns>The bracket as a Laurent polynomial in the crossing charge: the exponent of its first coefficient, and
    /// the coefficients ascending from it.</returns>
    /// <remarks>
    /// Shares no construction with the subject, which composes one planar diagram into another and charges the loops
    /// each composition strands off. Here the WHOLE closed diagram of one state is built at once as a graph over the
    /// boundary points of every layer — the closing arcs, the wire segments and the smoothings are all edges — and its
    /// closed curves are its connected components under a union-find. Nothing is composed, no diagram is named, no key is
    /// formed, and the arithmetic is <see cref="BigInteger"/> throughout. The cost is two to the crossing count, which is
    /// what keeps its callers small.
    /// </remarks>
    public static (int Lowest, BigInteger[] Coefficients) BracketStateSum(int strandCount, ReadOnlySpan<int> word) {
        var levels = (word.Length + 1);
        var nodeCount = (levels * strandCount);
        var states = (1 << word.Length);
        var top = ((levels - 1) * strandCount);
        var total = (0, Array.Empty<BigInteger>());

        for (var state = 0; (state < states); ++state) {
            var parent = new int[nodeCount];

            for (var node = 0; (node < nodeCount); ++node) { parent[node] = node; }

            int Root(int node) {
                while (parent[node] != node) { node = parent[node] = parent[parent[node]]; }

                return node;
            }

            void Join(int left, int right) { parent[Root(node: left)] = Root(node: right); }

            // The closure: the cups below and the caps above, each joining an adjacent pair.
            for (var wire = 0; (wire < strandCount); wire += 2) {
                Join(left: wire, right: (wire + 1));
                Join(left: (top + wire), right: ((top + wire) + 1));
            }

            var exponent = 0;

            for (var layer = 0; (layer < word.Length); ++layer) {
                var letter = word[layer];
                var strand = Math.Abs(value: letter);
                var lower = (layer * strandCount);
                var upper = ((layer + 1) * strandCount);

                // One crossing smooths two ways. The straight-through smoothing carries the crossing's own charge and
                // the joined one carries its inverse, which is what the mirror crossing swaps.
                if (0 == ((state >> layer) & 1)) {
                    exponent += ((letter > 0) ? 1 : -1);

                    for (var wire = 0; (wire < strandCount); ++wire) { Join(left: (lower + wire), right: (upper + wire)); }
                } else {
                    exponent -= ((letter > 0) ? 1 : -1);

                    Join(left: ((lower + strand) - 1), right: (lower + strand));
                    Join(left: ((upper + strand) - 1), right: (upper + strand));

                    for (var wire = 0; (wire < strandCount); ++wire) {
                        if ((wire != (strand - 1)) && (wire != strand)) { Join(left: (lower + wire), right: (upper + wire)); }
                    }
                }
            }

            var curves = new HashSet<int>();

            for (var node = 0; (node < nodeCount); ++node) { _ = curves.Add(item: Root(node: node)); }

            var term = (exponent, new[] { BigInteger.One });

            for (var curve = 0; (curve < curves.Count); ++curve) { term = LaurentMultiply(left: term, right: (-2, LoopCharge)); }

            total = LaurentAdd(left: total, right: term);
        }

        return total;
    }
    /// <summary>The bracket a published reduced bracket and a kink count give: the loop charge times the kink factor
    /// raised to that count times the reduced bracket.</summary>
    /// <param name="kinkExponent">The number of first-move kinks the diagram carries over the standard one, positive or
    /// negative; each multiplies the bracket by the kink factor.</param>
    /// <param name="lowest">The exponent the reduced bracket's first coefficient sits at.</param>
    /// <param name="coefficients">The reduced bracket's coefficients, ascending from that exponent.</param>
    /// <returns>The bracket as a Laurent polynomial in the crossing charge.</returns>
    /// <remarks>The reduced bracket is the published, diagram-independent invariant; the kink count is the declared
    /// diagram's own. Reading them through this is how a table of published numbers becomes a prediction about one
    /// diagram — and the state-sum enumeration, which knows neither, is what says the prediction was right.</remarks>
    public static (int Lowest, BigInteger[] Coefficients) BracketNormalization(int kinkExponent, int lowest, ReadOnlySpan<BigInteger> coefficients) {
        var value = LaurentMultiply(left: (lowest, coefficients.ToArray()), right: (-2, LoopCharge));
        var factor = ((kinkExponent >= 0) ? (3, KinkFactor) : (-3, KinkFactor));

        for (var kink = 0; (kink < Math.Abs(value: kinkExponent)); ++kink) { value = LaurentMultiply(left: value, right: factor); }

        return value;
    }
    /// <summary>Evaluates a Laurent polynomial at one point, exactly, by a Horner fold.</summary>
    /// <param name="lowest">The exponent the first coefficient sits at.</param>
    /// <param name="coefficients">The coefficients, ascending from that exponent.</param>
    /// <param name="point">The point.</param>
    /// <returns>The value as an exact fraction: the fold's numerator, and the power of the point a negative lowest
    /// exponent divides by.</returns>
    /// <remarks>The fold runs over the coefficients alone and never forms a power of the point, so a table of integers
    /// becomes a value in any material that can divide, and the division is the caller's own.</remarks>
    public static (BigInteger Numerator, BigInteger Denominator) BracketHorner(int lowest, ReadOnlySpan<BigInteger> coefficients, BigInteger point) {
        var numerator = BigInteger.Zero;

        for (var index = (coefficients.Length - 1); (index >= 0); --index) { numerator = ((numerator * point) + coefficients[index]); }

        return ((lowest >= 0)
            ? ((numerator * BigInteger.Pow(exponent: lowest, value: point)), BigInteger.One)
            : (numerator, BigInteger.Pow(exponent: -lowest, value: point)));
    }

    // The charge one closed curve carries, as a Laurent polynomial from the exponent minus two: the second move forces
    // it, since a crossing and its mirror compose to the straight-through diagram only at this value.
    //
    // TRANSCRIPTION, labelled (condition (C)). These two constants restate quantities the subject forms for itself —
    // the loop charge the state sum multiplies a closed curve by, and the kink factor the first move charges — so
    // agreement proves the two carriages match and NOT that either value is the one the moves force. The independent
    // witness is `presented.braid-relation-holds`'s loop-charge negative control: the second Reidemeister move holds at
    // this charge and at no other, which is the statement that pins the value from outside both.
    private static readonly BigInteger[] LoopCharge = [BigInteger.MinusOne, BigInteger.Zero, BigInteger.Zero, BigInteger.Zero, BigInteger.MinusOne];
    // The factor one first-move kink multiplies a bracket by, as a Laurent polynomial read at the exponent plus or minus
    // three: minus the crossing charge cubed. Labelled with the loop charge above.
    private static readonly BigInteger[] KinkFactor = [BigInteger.MinusOne];

    private static (int Lowest, BigInteger[] Coefficients) LaurentAdd((int Lowest, BigInteger[] Coefficients) left, (int Lowest, BigInteger[] Coefficients) right) {
        if (0 == left.Coefficients.Length) { return right; }
        if (0 == right.Coefficients.Length) { return left; }

        var lowest = Math.Min(val1: left.Lowest, val2: right.Lowest);
        var highest = Math.Max(val1: ((left.Lowest + left.Coefficients.Length) - 1), val2: ((right.Lowest + right.Coefficients.Length) - 1));
        var coefficients = new BigInteger[((highest - lowest) + 1)];

        for (var index = 0; (index < left.Coefficients.Length); ++index) { coefficients[((left.Lowest + index) - lowest)] += left.Coefficients[index]; }
        for (var index = 0; (index < right.Coefficients.Length); ++index) { coefficients[((right.Lowest + index) - lowest)] += right.Coefficients[index]; }

        return LaurentTrim(coefficients: coefficients, lowest: lowest);
    }
    private static (int Lowest, BigInteger[] Coefficients) LaurentMultiply((int Lowest, BigInteger[] Coefficients) left, (int Lowest, BigInteger[] Coefficients) right) {
        if ((0 == left.Coefficients.Length) || (0 == right.Coefficients.Length)) { return (0, []); }

        var coefficients = new BigInteger[((left.Coefficients.Length + right.Coefficients.Length) - 1)];

        for (var first = 0; (first < left.Coefficients.Length); ++first) {
            for (var second = 0; (second < right.Coefficients.Length); ++second) {
                coefficients[(first + second)] += (left.Coefficients[first] * right.Coefficients[second]);
            }
        }

        return LaurentTrim(coefficients: coefficients, lowest: (left.Lowest + right.Lowest));
    }
    // A Laurent polynomial is canonical when its first and last coefficients are nonzero, so equality is a span
    // comparison and cancellation is visible in the exponent rather than hidden in a zero.
    private static (int Lowest, BigInteger[] Coefficients) LaurentTrim(int lowest, BigInteger[] coefficients) {
        var high = (coefficients.Length - 1);
        var low = 0;

        while ((low <= high) && coefficients[low].IsZero) { ++low; }
        while ((high >= low) && coefficients[high].IsZero) { --high; }

        if (low > high) { return (0, []); }

        var trimmed = new BigInteger[((high - low) + 1)];

        Array.Copy(sourceArray: coefficients, sourceIndex: low, destinationArray: trimmed, destinationIndex: 0, length: trimmed.Length);

        return ((lowest + low), trimmed);
    }
    // The canonical word order the presented algebra keys by: shorter first, then lexicographically. It is shared
    // because it IS the key scheme, exactly as the planar oracle shares the diagram order.
    private static int CompareWords(int[] left, int[] right) {
        if (left.Length != right.Length) { return ((left.Length < right.Length) ? -1 : 1); }

        for (var index = 0; (index < left.Length); ++index) {
            if (left[index] != right[index]) { return ((left[index] < right[index]) ? -1 : 1); }
        }

        return 0;
    }

    /// <summary>The kind of node a <see cref="WordPattern"/> carries.</summary>
    public enum WordPatternKind {
        /// <summary>The empty span, and only it.</summary>
        Empty,
        /// <summary>One letter.</summary>
        Letter,
        /// <summary>Either branch.</summary>
        Union,
        /// <summary>The left branch followed by the right one.</summary>
        Concatenate,
        /// <summary>Any number of repetitions of the branch, the empty one included.</summary>
        Iterate,
    }
    /// <summary>A pattern as a syntax TREE, which is what makes it an oracle: the subject has no tree at all — a pattern
    /// there is an element of a presented algebra and matching is a residual — so counting derivations here shares no
    /// construction with it.</summary>
    /// <param name="Kind">The node kind.</param>
    /// <param name="Symbol">The letter, read only at <see cref="WordPatternKind.Letter"/>.</param>
    /// <param name="Left">The first branch.</param>
    /// <param name="Right">The second branch.</param>
    public sealed record WordPattern(WordPatternKind Kind, int Symbol, WordPattern? Left, WordPattern? Right) {
        /// <summary>The empty-span pattern.</summary>
        public static WordPattern Empty { get; } = new(Kind: WordPatternKind.Empty, Left: null, Right: null, Symbol: -1);

        /// <summary>Builds a one-letter pattern.</summary>
        /// <param name="letter">The letter.</param>
        /// <returns>The pattern.</returns>
        public static WordPattern Letter(int letter) =>
            new(Kind: WordPatternKind.Letter, Left: null, Right: null, Symbol: letter);
        /// <summary>Builds a union.</summary>
        /// <param name="left">The first branch.</param>
        /// <param name="right">The second branch.</param>
        /// <returns>The pattern.</returns>
        public static WordPattern Union(WordPattern left, WordPattern right) =>
            new(Kind: WordPatternKind.Union, Left: left, Right: right, Symbol: -1);
        /// <summary>Builds a concatenation.</summary>
        /// <param name="left">The first branch.</param>
        /// <param name="right">The second branch.</param>
        /// <returns>The pattern.</returns>
        public static WordPattern Concatenate(WordPattern left, WordPattern right) =>
            new(Kind: WordPatternKind.Concatenate, Left: left, Right: right, Symbol: -1);
        /// <summary>Builds an iteration.</summary>
        /// <param name="value">The repeated branch, which must not derive the empty span.</param>
        /// <returns>The pattern.</returns>
        public static WordPattern Iterate(WordPattern value) =>
            new(Kind: WordPatternKind.Iterate, Left: value, Right: null, Symbol: -1);
    }

    /// <summary>Counts the derivations of one word under a pattern tree, by backtracking over every split.</summary>
    /// <param name="pattern">The pattern tree.</param>
    /// <param name="word">The word, as letter numbers.</param>
    /// <returns>The number of distinct derivations — the ambiguity degree, which over a counting material IS the
    /// coefficient, and whose being non-zero IS Boolean membership.</returns>
    /// <remarks>An <see cref="WordPatternKind.Iterate"/> node consumes at least one letter per repetition, so the
    /// recursion terminates; a branch that derives the empty span would make the count infinite and is rejected by the
    /// caller rather than silently truncated.</remarks>
    public static BigInteger WordDerivations(WordPattern pattern, ReadOnlySpan<int> word) =>
        Derivations(pattern: pattern, word: word, start: 0, end: word.Length);

    private static BigInteger Derivations(WordPattern pattern, ReadOnlySpan<int> word, int start, int end) {
        switch (pattern.Kind) {
            case WordPatternKind.Empty:
                return ((start == end) ? BigInteger.One : BigInteger.Zero);
            case WordPatternKind.Letter:
                return ((((start + 1) == end) && (word[start] == pattern.Symbol)) ? BigInteger.One : BigInteger.Zero);
            case WordPatternKind.Union:
                return (Derivations(pattern: pattern.Left!, word: word, start: start, end: end)
                    + Derivations(pattern: pattern.Right!, word: word, start: start, end: end));
            case WordPatternKind.Concatenate: {
                    var total = BigInteger.Zero;

                    for (var split = start; (split <= end); ++split) {
                        var head = Derivations(pattern: pattern.Left!, word: word, start: start, end: split);

                        if (head.IsZero) { continue; }

                        total += (head * Derivations(pattern: pattern.Right!, word: word, start: split, end: end));
                    }

                    return total;
                }
            default: {
                    if (start == end) { return BigInteger.One; }

                    var total = BigInteger.Zero;

                    // Every repetition consumes at least one letter, which is what bounds the recursion.
                    for (var split = (start + 1); (split <= end); ++split) {
                        var head = Derivations(pattern: pattern.Left!, word: word, start: start, end: split);

                        if (head.IsZero) { continue; }

                        total += (head * Derivations(end: end, pattern: pattern, start: split, word: word));
                    }

                    return total;
                }
        }
    }
    // The doubling conjugation's sign on a basis vector: the real unit is fixed, every imaginary unit is negated.
    private static int ConjugationSign(int index) =>
        ((0 == index) ? 1 : -1);

}

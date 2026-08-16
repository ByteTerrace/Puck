using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// Provides exact operations on arbitrary-width signed integers: the floor square root, the modular inverse, and the
/// modular square root over an odd prime.
/// </summary>
/// <remarks>
/// <para>
/// These are the unbounded counterparts to the fixed-width kernels in <see cref="UnsignedNumberFunctions"/> and
/// <see cref="PrimeField64"/> — the routines a procedural construction reaches for once its operands outgrow a machine
/// word. Only operations whose algorithm genuinely changes when the carrier stops being a register live here: a Newton
/// descent that needs unbounded headroom above the root, an extended-Euclid recursion whose Bézout coefficients outgrow
/// their operands, and a residue descent over a modulus past <c>2^64</c>.
/// </para>
/// <para>
/// Nothing a width-agnostic formulation already covers is repeated. The bit, digit and division routines in
/// <see cref="BinaryIntegerFunctions"/> are written against <see cref="IBinaryInteger{TSelf}"/> and therefore serve
/// <see cref="BigInteger"/> directly — <see cref="BinaryIntegerFunctions.FloorModulo{T}(T, T)"/>,
/// <see cref="BinaryIntegerFunctions.GreatestCommonDivisor{T}(T, T)"/> and
/// <see cref="BinaryIntegerFunctions.LeastCommonMultiple{T}(T, T)"/> among them.
/// </para>
/// </remarks>
public static class BigIntegerFunctions {
    /// <summary>The walk steps one refutation attempt above <see cref="PrimeKernels.LeastWitnessFailure"/> may spend
    /// before the operand is reported as a probable prime.</summary>
    /// <remarks>
    /// A cycle walk finds a factor <c>p</c> in about <c>√p</c> steps, so this reaches factors up to roughly <c>2^41</c>
    /// — past the two near-<c>2^39</c> factors of the least strong pseudoprime to the witness set, which is
    /// the case that makes the attempt worth making at all. It is a cap, not a scaling budget: above the boundary the
    /// operand may be genuinely prime, and a budget that scaled with the operand would spend all of it proving nothing.
    /// </remarks>
    private static readonly BigInteger RefutationBudget = (1 << 21);

    /// <summary>Divides every factor of two out of a value and reports how many there were.</summary>
    /// <param name="oddPart">The nonzero value to reduce; on return it holds the odd part of what was passed in.</param>
    /// <returns>The two-adic valuation of the value passed in — the exponent of the largest power of two dividing it.</returns>
    /// <remarks>
    /// <see cref="BigInteger.TrailingZeroCount(BigInteger)"/> reads the exponent straight off the representation, so the
    /// reduction costs one shift rather than a halving loop. Zero is excluded because every power of two divides it.
    /// </remarks>
    private static int ExtractTwoAdicValuation(ref BigInteger oddPart) {
        var shift = ((int)BigInteger.TrailingZeroCount(value: oddPart));

        oddPart >>= shift;

        return shift;
    }
    /// <summary>Splits a value into rational primes, appending each to a flat accumulator.</summary>
    /// <param name="value">The value to split.</param>
    /// <param name="flat">The accumulator the primes are appended to, in no particular order.</param>
    /// <remarks>
    /// <para>
    /// Iterative on both axes, and both were once recursive. Twos are peeled in a loop because a frame per factor of
    /// two made a compact operand control the native stack: <c>BigInteger.One &lt;&lt; 200000</c> is about 25 KiB and
    /// overflowed it, which no catch can recover. Composite splitting walks an explicit stack for the same reason —
    /// heap-bounded and inspectable, with depth no longer a function of the operand's multiplicity.
    /// </para>
    /// <para>
    /// A primality answer is only TERMINAL below <see cref="PrimeKernels.LeastWitnessFailure"/>, where the witness set
    /// is a proof. At or above it a passing value may be the pseudoprime the set cannot see, so the answer is not taken
    /// on its own: a BOUNDED refutation runs first. A value the walk splits was never prime, which is what makes the
    /// least such pseudoprime factor correctly instead of being named a prime factor of itself. A value that RESISTS
    /// the walk is refused, because exhausting a refutation is not a proof and this method's return contract says
    /// every item is prime — reporting an uncertifiable residual would weaken that to probable for every caller.
    /// </para>
    /// <para>
    /// The refutation is capped rather than run to the splitter's full budget, because above the boundary the operand
    /// may be genuinely prime and an uncapped walk would then spend the whole budget proving nothing — minutes, for a
    /// Mersenne prime. So the cap decides how much effort is spent before refusing, not whether a probable answer is
    /// admitted; the answer above the boundary is a factorization or an exception, never a maybe.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">A remaining piece at or above <see cref="PrimeKernels.LeastWitnessFailure"/>
    /// neither split within the bounded refutation nor can be certified prime.</exception>
    private static void Factor(BigInteger value, List<BigInteger> flat) {
        if (value <= BigInteger.One) { return; }

        var two = (BigInteger.One + BigInteger.One);
        var pending = new Stack<BigInteger>();

        pending.Push(item: value);

        while (pending.Count != 0) {
            var current = pending.Pop();

            while (current.IsEven) {
                flat.Add(item: two);
                current >>= 1;
            }

            if (current.IsOne) { continue; }

            BigInteger divisor;

            if (IsPrime(value: current)) {
                // Below the boundary that answer is a proof and the piece is done. At or above it the witness set can
                // be fooled, so one capped walk is spent trying to refute it; only a value that survives is reported.
                if (current < PrimeKernels.LeastWitnessFailure) {
                    flat.Add(item: current);

                    continue;
                }

                if (!TrySplit(
                    budget: RefutationBudget,
                    divisor: out divisor,
                    value: current
                )) {
                    // Exhausting the refutation is not a primality proof. This method's return contract says every
                    // item is prime, so an uncertifiable residual is refused rather than appended: reporting it would
                    // silently weaken the contract from proved primes to probable ones for every caller.
                    throw new InvalidOperationException(message: $"Factorization reached {current}, which passes the twelve witness bases at or above {PrimeKernels.LeastWitnessFailure}, where they prove nothing, and did not split within the bounded refutation. It can be neither factored nor certified prime here, and this method may not report a factor it cannot prove.");
                }
            } else {
                divisor = FindDivisor(value: current);
            }

            pending.Push(item: divisor);
            pending.Push(item: (current / divisor));
        }
    }
    /// <summary>Returns a nontrivial divisor of an odd composite by a deterministic cycle walk over <c>y² + addend</c>.</summary>
    /// <param name="value">The odd composite to split; it must not be a prime power of a value the walk cannot separate.</param>
    /// <returns>A divisor strictly between one and <paramref name="value"/>.</returns>
    /// <remarks>
    /// <para>
    /// Floyd's two-rate walk, restarted at the next offset whenever a walk collapses onto the modulus itself, so the
    /// split is deterministic and depends on nothing but the operand. This is the arbitrary-width counterpart to
    /// <see cref="PrimeKernels.FindFactor(ulong)"/>, which is Brent's walk over a Montgomery ring: that refinement buys
    /// its speed from batching the greatest-common-divisor across a register-sized stride, which is exactly what a
    /// <see cref="BigInteger"/> operand cannot amortize, so the two are genuinely different algorithms rather than one
    /// written twice.
    /// </para>
    /// <para>
    /// The search is BOUNDED, and the bound is a termination guarantee rather than a performance target. The offset
    /// sequence has no natural end, so an operand that is secretly prime — one whose primality gate answered wrongly —
    /// admits no nontrivial divisor for any offset and would otherwise spin here forever. The budget is sixty-four times
    /// the fourth root of the operand, which is the walk's own expected cost with two orders of magnitude of headroom:
    /// it scales with the operand instead of capping it, so no value this method is contracted to accept can reach it.
    /// Being bounded is not the same as being quick — a large operand's bound is itself large — but the loop now ends.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> did not split within the budget, so it was not the odd composite this method requires.</exception>
    private static BigInteger FindDivisor(BigInteger value) =>
        (TrySplit(
            value: value,
            budget: ((64 * SquareRoot(value: SquareRoot(value: value))) + 4096),
            divisor: out var divisor
        )
            ? divisor
            : throw new InvalidOperationException(message: $"The cycle-walk splitter exhausted its step budget on {value}, which is therefore not the odd composite it requires. A prime reaching here means a primality gate upstream answered wrongly.")
        );
    /// <summary>Attempts a split within an explicit step budget, reporting exhaustion instead of throwing.</summary>
    /// <param name="value">The odd value to split.</param>
    /// <param name="budget">The walk steps this attempt may spend.</param>
    /// <param name="divisor">A divisor strictly between one and <paramref name="value"/>, when one was found.</param>
    /// <returns><see langword="true"/> when a divisor was found; otherwise <see langword="false"/>.</returns>
    /// <remarks>Separate from <see cref="FindDivisor(BigInteger)"/> because the two callers want opposite things from
    /// exhaustion. A composite that must split treats it as a broken precondition — something upstream lied. A refutation
    /// attempt above the primality proof boundary treats it as an answer: this value resisted, so it is reported as
    /// probably prime rather than factored.</remarks>
    private static bool TrySplit(BigInteger value, BigInteger budget, out BigInteger divisor) {
        for (var addend = BigInteger.One; ; ++addend) {
            var slow = new BigInteger(value: 2);
            var fast = new BigInteger(value: 2);
            var candidate = BigInteger.One;

            do {
                if (budget.Sign <= 0) {
                    divisor = BigInteger.Zero;

                    return false;
                }

                --budget;
                slow = (((slow * slow) + addend) % value);
                fast = (((fast * fast) + addend) % value);
                fast = (((fast * fast) + addend) % value);
                candidate = BigInteger.GreatestCommonDivisor(
                    left: BigInteger.Abs(value: (slow - fast)),
                    right: value
                );
            } while (candidate.IsOne);

            if (candidate != value) {
                divisor = candidate;

                return true;
            }
        }
    }

    /// <summary>Enumerates the prime factors of a value, with multiplicity, from smallest to largest.</summary>
    /// <param name="value">The value to factor.</param>
    /// <returns>
    /// The prime factors of <paramref name="value"/>, each repeated according to its multiplicity — for example,
    /// <c>360</c> yields <c>2, 2, 2, 3, 3, 5</c>. A prime yields ITSELF, so the length is always Ω, and the sequence is
    /// empty only for a <paramref name="value"/> below two, which has no factorization at all.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Twos are peeled by shifting, an exact primality gate settles each remaining piece, and a composite one is split
    /// by <see cref="FindDivisor(BigInteger)"/> and recursed into. Every step is deterministic, so the same input always
    /// factors identically.
    /// </para>
    /// <para>
    /// <b>The cost is the splitter's, and the splitter is a cycle walk.</b> That is quadratic-ish in the fourth root of
    /// the operand, which is fast for an operand whose factors are small or lopsided and slow for a hard semiprime of
    /// two large primes — a regime this method does not promise to finish in any stated time. Where the operand fits a
    /// machine word, <see cref="UnsignedNumberFunctions.EnumeratePrimeFactors{T}(T)"/> reaches the far faster
    /// <see cref="PrimeKernels"/> path instead, and is what a caller holding a word should use.
    /// </para>
    /// </remarks>
    public static IEnumerable<BigInteger> EnumeratePrimeFactors(BigInteger value) {
        if (value < (BigInteger.One + BigInteger.One)) { return []; }

        var flat = new List<BigInteger>();

        Factor(
            flat: flat,
            value: value
        );
        flat.Sort();

        return flat;
    }
    /// <summary>Decides whether a value is prime.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is prime; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// Below <c>2^64</c> the question is handed to <see cref="PrimeField64.IsPrime(ulong)"/>, which decides it exactly
    /// and without leaving a register. Above it, strong-probable-prime rounds to <see cref="PrimeKernels.WitnessBases"/> — the same
    /// twelve bases, read from the same table — which are a proven complete witness set for every value strictly below
    /// <see cref="PrimeKernels.LeastWitnessFailure"/>, exactly <c>318665857834031151167461</c>.
    /// </para>
    /// <para>
    /// <b>At and past that bound the answer is probable, not exact.</b> Every prime still passes, but the bound is not
    /// hypothetical: it IS a composite this method calls prime, <c>399165290221 * 798330580441</c>, and it is the
    /// smallest one. A caller working at or past it whose decision must be exact needs a proof this method does not
    /// attempt.
    /// </para>
    /// <para>
    /// The threshold is quoted exactly rather than rounded. A rounded <c>3.19 * 10^23</c> stood here once and rounded
    /// the wrong way — up, past <c>3.18665... * 10^23</c> — which placed that one counterexample inside the range this
    /// method promised to decide exactly, and let <see cref="EnumeratePrimeFactors(BigInteger)"/> report it as a prime
    /// factor of itself.
    /// </para>
    /// </remarks>
    public static bool IsPrime(BigInteger value) {
        if (value < (BigInteger.One + BigInteger.One)) { return false; }
        if (value <= ulong.MaxValue) { return PrimeField64.IsPrime(value: ((ulong)value)); }

        var oddPart = (value - BigInteger.One);
        var twoExponent = ExtractTwoAdicValuation(oddPart: ref oddPart);
        var minusOne = (value - BigInteger.One);

        foreach (var witnessBase in PrimeKernels.WitnessBases) {
            var residue = BigInteger.ModPow(
                value: new BigInteger(value: witnessBase),
                exponent: oddPart,
                modulus: value
            );

            if (
                residue.IsOne ||
                (residue == minusOne)
            ) { continue; }

            var composite = true;

            for (var round = 1; (round < twoExponent); ++round) {
                residue = ((residue * residue) % value);

                if (residue == minusOne) {
                    composite = false;

                    break;
                }
            }

            if (composite) { return false; }
        }

        return true;
    }
    /// <summary>Computes the multiplicative inverse of a value modulo a positive modulus by the extended greatest-common-divisor recursion.</summary>
    /// <param name="value">The value to invert. It is reduced modulo <paramref name="modulus"/> on entry, so any sign and magnitude are admitted; it must be coprime to the modulus, and anything else is refused.</param>
    /// <param name="modulus">The modulus, which must be positive.</param>
    /// <returns>The unique representative in <c>[0, <paramref name="modulus"/>)</c> whose product with <paramref name="value"/> is congruent to one modulo <paramref name="modulus"/>.</returns>
    /// <remarks>
    /// <para>
    /// The descent carries one Bézout coefficient alongside the remainders, so the greatest common divisor falls out of
    /// the same loop that produces the inverse: the last non-zero remainder <em>is</em> that divisor, and the coprimality
    /// guard reads it rather than spending a second pass on <see cref="BigInteger.GreatestCommonDivisor"/>. Both the
    /// input and the resulting coefficient are reduced with <see cref="BinaryIntegerFunctions.FloorModulo{T}(T, T)"/>,
    /// so the answer is always the non-negative representative.
    /// </para>
    /// <para>
    /// A non-invertible value is <em>refused</em> rather than answered. When the divisor exceeds one no element
    /// multiplies <paramref name="value"/> to one at all, and the raw Bézout coefficient in hand would multiply it to
    /// that divisor instead — a wrong answer that reads like a right one, which is why this method throws where a
    /// caller might expect a sentinel. The degenerate <paramref name="modulus"/> of one names the zero ring, where every
    /// element is congruent to every other; its divisor is one and the answer is zero, which is correct there because
    /// zero and one are the same residue.
    /// </para>
    /// <para>
    /// This is the arbitrary-modulus companion to <see cref="UnsignedNumberFunctions.ModularInverse{T}(T)"/>, which
    /// inverts an odd value modulo <c>2^width</c> by a Newton–Hensel doubling that presumes that one modulus. The two
    /// share no code and no idea: this is the Euclidean descent, that one is a fixed ladder of squarings.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="value"/> shares a factor with <paramref name="modulus"/>, so it has no inverse there.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="modulus"/> is not positive.</exception>
    public static BigInteger ModularInverse(BigInteger value, BigInteger modulus) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: modulus,
            other: BigInteger.One
        );

        var previousRemainder = modulus;
        var remainder = value.FloorModulo(modulus: modulus);
        var previousCoefficient = BigInteger.Zero;
        var coefficient = BigInteger.One;

        while (!remainder.IsZero) {
            var quotient = (previousRemainder / remainder);

            (previousRemainder, remainder) = (remainder, (previousRemainder - (quotient * remainder)));
            (previousCoefficient, coefficient) = (coefficient, (previousCoefficient - (quotient * coefficient)));
        }

        // The descent halted on the greatest common divisor. Anything but one leaves the congruence unsolvable, and the
        // coefficient in hand solves `value * x ≡ gcd` rather than `≡ 1`.
        if (!previousRemainder.IsOne) {
            throw new ArgumentException(
                message: "The value shares a factor with the modulus, so it has no multiplicative inverse there.",
                paramName: nameof(value)
            );
        }

        return previousCoefficient.FloorModulo(modulus: modulus);
    }
    /// <summary>Returns the floor square root of a non-negative integer.</summary>
    /// <param name="value">The non-negative value to take the square root of.</param>
    /// <returns>The largest integer whose square does not exceed <paramref name="value"/>.</returns>
    /// <remarks>
    /// A Newton descent seeded from a power of two strictly above the root, so every estimate stays at or above the
    /// answer and the first estimate that fails to decrease is the floor. Cost is set by the operand's bit length rather
    /// than by its magnitude. This is the unbounded counterpart to
    /// <see cref="UnsignedNumberFunctions.SquareRoot{T}(T)"/>, which seeds a hardware floating-point root and settles it
    /// with a branchless correction — a route no arbitrary-width operand can take, since the seed would have to be
    /// representable as a <see cref="double"/> first.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is negative.</exception>
    public static BigInteger SquareRoot(BigInteger value) {
        if (value.Sign < 0) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(value),
                message: "The square-root input must be non-negative."
            );
        }
        if (value.IsZero) { return BigInteger.Zero; }

        // This power of two is strictly above √value. Newton descent remains above the root and terminates when the
        // next estimate can no longer decrease, at which point the current integer is floor(√value).
        var estimate = (BigInteger.One << checked((int)((value.GetBitLength() + 1L) / 2L)));

        while (true) {
            var next = ((estimate + (value / estimate)) >> 1);

            if (next >= estimate) { return estimate; }

            estimate = next;
        }
    }
    /// <summary>Attempts to compute a square root of a value modulo an odd prime.</summary>
    /// <param name="value">The value to take the root of. It is reduced modulo <paramref name="oddPrime"/> on entry, so any sign and magnitude are admitted.</param>
    /// <param name="oddPrime">The modulus. It must be an odd prime of at least three: the oddness and the lower bound are enforced, primality is not — see the remarks.</param>
    /// <param name="root">When this method returns <see langword="true"/>, one of the two square roots as the representative in <c>[0, <paramref name="oddPrime"/>)</c> — the other is <c><paramref name="oddPrime"/> − root</c>, and both coincide at zero; when it returns <see langword="false"/>, zero.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is a quadratic residue modulo <paramref name="oddPrime"/> and a root was found; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <para>
    /// The arbitrary-width counterpart of <see cref="PrimeField64.TrySqrt(ulong, out ulong)"/> — the same descent over a
    /// different carrier, for the moduli that outgrow that type's ceiling. Zero roots to zero. Euler's criterion decides
    /// the character before any descent is spent on it; a modulus congruent to three modulo four then answers with a
    /// single power, and otherwise the two-part of the multiplicative order is walked down against an ascending
    /// nonresidue seed, which terminates because half the residues qualify.
    /// </para>
    /// <para>
    /// Primality is a <em>precondition</em>, not a check. Deciding it for an arbitrary-width modulus costs more than the
    /// root does, so this method spends nothing on it and validates only what is decidable at a glance: that the modulus
    /// is odd and at least three. An even modulus and a modulus below three are therefore refused outright — the
    /// two-element case is settled by inspection at the call site, since four candidates cover it. Hand this an odd
    /// <em>composite</em> and every guarantee lapses at once: Euler's criterion no longer decides residuacity, so a
    /// genuine square can be refused and a non-square accepted with a value that does not square back; and the descent
    /// squares a residue until it reaches one, which a residue whose order is not a power of two never does — so the
    /// call may also fail to return. Establish primality first (<see cref="PrimeField64.IsPrime(ulong)"/> within a
    /// machine word, a probable-prime test above it) whenever the modulus is not already known prime by construction.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="oddPrime"/> is below three or is even.</exception>
    public static bool TrySquareRootModuloOddPrime(BigInteger value, BigInteger oddPrime, out BigInteger root) {
        ArgumentOutOfRangeException.ThrowIfLessThan(
            value: oddPrime,
            other: new BigInteger(value: 3)
        );

        if (oddPrime.IsEven) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(oddPrime),
                message: "The modular square root is taken here only over an odd modulus."
            );
        }

        var residue = value.FloorModulo(modulus: oddPrime);

        if (residue.IsZero) {
            root = BigInteger.Zero;

            return true;
        }

        var halfOrder = ((oddPrime - BigInteger.One) >> 1);

        if (!BigInteger.ModPow(
            exponent: halfOrder,
            modulus: oddPrime,
            value: residue
        ).IsOne) {
            root = BigInteger.Zero;

            return false;
        }
        if (3 == (oddPrime & 3)) {
            root = BigInteger.ModPow(
                value: residue,
                exponent: ((oddPrime + BigInteger.One) >> 2),
                modulus: oddPrime
            );

            return true;
        }

        var oddPart = halfOrder;
        // The seed of one accounts for the factor of two the halving into halfOrder already removed.
        var twoExponent = (1 + ExtractTwoAdicValuation(oddPart: ref oddPart));

        // Any nonresidue seeds the descent; the ascending search terminates because half the residues qualify.
        var seed = new BigInteger(value: 2);

        while (BigInteger.ModPow(
            exponent: halfOrder,
            modulus: oddPrime,
            value: seed
        ).IsOne) { ++seed; }

        var scale = BigInteger.ModPow(
            exponent: oddPart,
            modulus: oddPrime,
            value: seed
        );
        var candidate = BigInteger.ModPow(
            value: residue,
            exponent: ((oddPart + BigInteger.One) >> 1),
            modulus: oddPrime
        );
        var square = BigInteger.ModPow(
            exponent: oddPart,
            modulus: oddPrime,
            value: residue
        );
        var order = twoExponent;

        // Each round halves the two-part of the residual's order, so the loop runs at most that many times.
        while (!square.IsOne) {
            var step = 0;
            var power = square;

            while (!power.IsOne) {
                power = ((power * power) % oddPrime);
                ++step;
            }

            var factor = scale;

            for (var lift = ((order - step) - 1); (0 < lift); --lift) { factor = ((factor * factor) % oddPrime); }

            candidate = ((candidate * factor) % oddPrime);
            scale = ((factor * factor) % oddPrime);
            square = ((square * scale) % oddPrime);
            order = step;
        }

        root = candidate;

        return true;
    }
}

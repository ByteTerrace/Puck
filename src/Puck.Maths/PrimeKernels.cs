using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The word-sized prime machinery every public prime entry point in the library runs on: the base-prime table, the
/// window marker both sieves stride with, the exact primality dispatch, and the deterministic cycle-walk splitter.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is a second expression of anything else. Each routine is written once, at the widest carrier a machine
/// word offers, and the narrower public surfaces widen into it rather than carrying their own copy:
/// <see cref="PrimeExtensions.Factorize(uint, Span{uint})"/> and
/// <see cref="UnsignedNumberFunctions.EnumeratePrimeFactors{T}(T)"/> both reach <see cref="Factorize(ulong, Span{ulong})"/>,
/// and <see cref="NumberTheoryFunctions.SegmentedPrimeSieve(ulong, ulong, Action{ulong})"/> and
/// <see cref="PrimeExtensions.NthPrime(uint)"/> both stride <see cref="MarkWindow(ReadOnlySpan{uint}, Span{ulong}, ulong, ulong)"/>.
/// </para>
/// <para>
/// The arbitrary-width counterparts live in <see cref="BigIntegerFunctions"/>. That split is real rather than
/// incidental: the routines here reduce through a Montgomery ring and precomputed reciprocals that exist only because
/// the operands fit a register, and none of that survives the move to <see cref="BigInteger"/>.
/// </para>
/// </remarks>
internal static class PrimeKernels {
    /// <summary>Gets the ascending odd primes below 65,536 — every base prime a 32-bit window sieve can need, since the largest such value's square root is 65,535.</summary>
    internal static ReadOnlySpan<uint> BasePrimes => WindowSieve.BasePrimes;
    /// <summary>Gets the odd primes through fifty-nine, paired index-for-index with <see cref="SmallFactorCeilings"/> and <see cref="SmallFactorInverses"/>.</summary>
    /// <remarks>The trial-division ladder in <see cref="PrimeExtensions.IsPrime(uint)"/> derives its vector tables and its exact-match arm from this same list, so it is the one place the sixteen factors are chosen.</remarks>
    internal static ReadOnlySpan<ulong> SmallFactorPrimes => new ulong[16] {
        3UL, 5UL, 7UL, 11UL, 13UL, 17UL, 19UL, 23UL, 29UL, 31UL, 37UL, 41UL, 43UL, 47UL, 53UL, 59UL,
    };
    /// <summary>Gets the strong-probable-prime witness bases that decide primality exactly for every value strictly
    /// below <see cref="LeastWitnessFailure"/>.</summary>
    /// <remarks>
    /// Sorenson and Webster's computed twelfth strong-pseudoprime threshold is what makes the set complete rather than
    /// merely unrefuted. <see cref="PrimeField64.IsPrime(ulong)"/> and <see cref="BigIntegerFunctions.IsPrime(BigInteger)"/>
    /// read this one table rather than each transcribing it, so a correction to the bound cannot land on one carrier and
    /// miss the other.
    /// </remarks>
    internal static ReadOnlySpan<int> WitnessBases => [2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37];

    /// <summary>Gets the divisibility ceilings paired with <see cref="SmallFactorPrimes"/>: a value is divisible by the paired prime exactly when its inverse-product does not exceed the ceiling, and the product is then the exact quotient.</summary>
    private static ReadOnlySpan<ulong> SmallFactorCeilings => new ulong[16] {
        (ulong.MaxValue / 3UL), (ulong.MaxValue / 5UL), (ulong.MaxValue / 7UL), (ulong.MaxValue / 11UL),
        (ulong.MaxValue / 13UL), (ulong.MaxValue / 17UL), (ulong.MaxValue / 19UL), (ulong.MaxValue / 23UL),
        (ulong.MaxValue / 29UL), (ulong.MaxValue / 31UL), (ulong.MaxValue / 37UL), (ulong.MaxValue / 41UL),
        (ulong.MaxValue / 43UL), (ulong.MaxValue / 47UL), (ulong.MaxValue / 53UL), (ulong.MaxValue / 59UL),
    };

    /// <summary>Gets the least value <see cref="WitnessBases"/> decides incorrectly — the smallest strong pseudoprime to
    /// all twelve of them, <c>318665857834031151167461 = 399165290221 * 798330580441</c>. Every value strictly below
    /// it that survives all twelve rounds is prime; this one survives them and is composite, so it is the exact,
    /// exclusive ceiling of the deterministic range.</summary>
    /// <remarks>
    /// It lives here, beside the bases, because it is a function of them: add or drop a single base and this number
    /// changes. A rounded approximation such as <c>3.19 * 10^23</c> rounds up past the true value and would admit
    /// the counterexample into the range this library promises to decide exactly; round a threshold like this down,
    /// or not at all.
    /// </remarks>
    internal static BigInteger LeastWitnessFailure { get; } = BigInteger.Parse(value: "318665857834031151167461");

    /// <summary>Gets the multiplicative inverses modulo 2⁶⁴ paired with <see cref="SmallFactorPrimes"/>.</summary>
    /// <remarks>Derived rather than transcribed: <see cref="UnsignedNumberFunctions.ModularInverse{T}(T)"/> is a division-free Newton–Hensel ladder, so a static initializer costs sixteen of them once and no table can fall out of step with the primes beside it.</remarks>
    private static readonly ulong[] SmallFactorInverses = CreateSmallFactorInverses();

    /// <summary>The polynomial advances one call to <see cref="FindFactor(ulong)"/> may spend before it refuses.</summary>
    /// <remarks>
    /// A cycle walk finds a factor <c>p</c> in about <c>√p</c> advances, so the worst legal operand — a semiprime of two
    /// primes either side of 2³² — costs on the order of 2¹⁶, and the offset restarts multiply that by a small constant.
    /// 2²⁴ therefore clears the worst legal operand by roughly two hundred times over: no input this kernel is
    /// contracted to accept can reach it, and the ceiling exists only so that every loop in the splitter terminates.
    /// </remarks>
    private const int SplitAdvanceBudget = (1 << 24);

    /// <summary>Defers the base-prime table until a sieve actually strides one.</summary>
    private static class WindowSieve {
        internal static readonly uint[] BasePrimes;

        static WindowSieve() => BasePrimes = CreateBasePrimes();
    }

    /// <summary>Writes the prime factors of <paramref name="value"/>, with multiplicity and in ascending order, into <paramref name="destination"/>.</summary>
    /// <param name="value">The value to factor.</param>
    /// <param name="destination">The destination for the factors; sixty-four entries always suffice.</param>
    /// <returns>The number of factors written: <c>0</c> only when <paramref name="value"/> is below two, and <c>1</c> when it is prime.</returns>
    /// <remarks>A prime reports itself, and the count is therefore Ω — the number of prime factors with multiplicity — for every operand at or above two.</remarks>
    internal static int Factorize(ulong value, Span<ulong> destination) {
        if (2UL > value) { return 0; }
        // Not merely an optimization: it is what spares a large prime the whole trial-division ladder before the pending
        // stack would reach the same answer.
        if (IsPrimeWord(value: value)) {
            destination[0] = value;

            return 1;
        }

        var count = 0;

        while (0UL == (value & 1UL)) {
            destination[count++] = 2UL;
            value >>= 1;
        }

        var primes = SmallFactorPrimes;
        var ceilings = SmallFactorCeilings;
        var inverses = SmallFactorInverses;

        for (var i = 0; (i < primes.Length); ++i) {
            var prime = primes[i];

            // The largest small factor is fifty-nine, so the square never leaves the carrier and needs no widening.
            if ((prime * prime) > value) { break; }

            var ceiling = ceilings[i];
            var inverse = inverses[i];

            while (true) {
                var quotient = unchecked((value * inverse));

                if (quotient > ceiling) { break; }

                destination[count++] = prime;
                value = quotient;
            }
        }

        if (1UL == value) { return count; }

        var sorted = count;
        var depth = 0;
        // Every remaining prime factor exceeds fifty-nine, and 61^11 is past 2^64, so the pending stack never holds more
        // than ten cofactors: each step pops one and pushes two, which is a net gain of one per factor still to find.
        Span<ulong> pending = stackalloc ulong[16];

        pending[depth++] = value;

        while (0 < depth) {
            var cofactor = pending[--depth];

            if (IsPrimeWord(value: cofactor)) {
                destination[count++] = cofactor;

                continue;
            }

            var divisor = FindFactor(value: cofactor);

            pending[depth++] = divisor;
            pending[depth++] = (cofactor / divisor);
        }

        for (var i = (sorted + 1); (i < count); ++i) {
            var current = destination[i];
            var j = (i - 1);

            while (
                (j >= sorted) &&
                (destination[j] > current)
            ) {
                destination[(j + 1)] = destination[j];
                --j;
            }

            destination[(j + 1)] = current;
        }

        return count;
    }
    /// <summary>Returns a nontrivial divisor of the odd composite <paramref name="value"/>, whose prime factors must all exceed fifty-nine.</summary>
    /// <param name="value">The composite to split.</param>
    /// <returns>A divisor strictly between <c>1</c> and <paramref name="value"/>; it is not necessarily prime.</returns>
    /// <remarks>
    /// <para>The polynomial offset advances through a fixed sequence until a walk succeeds, so the split is deterministic.</para>
    /// <para>
    /// The search is bounded, by <see cref="SplitAdvanceBudget"/> advances shared across every offset it tries. The
    /// bound is not a quality-of-service knob but a termination guarantee: the offset sequence has no natural end, so
    /// without it an operand that is secretly prime — one whose primality gate answered wrongly — would spin here
    /// forever instead of failing.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> did not split within the budget, so it was not the odd composite free of small factors this method requires.</exception>
    internal static ulong FindFactor(ulong value) {
        var budget = SplitAdvanceBudget;
        var ring = new ScaledResidueRing64(modulus: value);
        var addend = 1UL;

        while (true) {
            var divisor = FindFactorCycle(
                addend: addend,
                budget: ref budget,
                ring: in ring
            );

            if (
                (1UL < divisor) &&
                (divisor < value)
            ) { return divisor; }

            ++addend;
        }
    }
    /// <summary>Decides primality exactly for a machine word, spending the cheaper of the two exact deciders.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is prime; otherwise <see langword="false"/>.</returns>
    /// <remarks>
    /// <see cref="PrimeExtensions.IsPrime(uint)"/> settles the 32-bit range on one strong-probable-prime round plus a
    /// finite correction table, where <see cref="PrimeField64.IsPrime(ulong)"/> must spend twelve; above that range only
    /// the twelve-base decision applies. Both are exact, so the dispatch is a cost choice and never a correctness one.
    /// </remarks>
    internal static bool IsPrimeWord(ulong value) =>
        ((value <= uint.MaxValue)
            ? ((uint)value).IsPrime()
            : PrimeField64.IsPrime(value: value)
        );
    /// <summary>Sieves the odd values <c>low + 2i</c> for <c>i</c> in <c>[0, bits)</c>, setting bit <c>i</c> of <paramref name="bitmap"/> when the value is composite.</summary>
    /// <param name="basePrimes">The ascending odd primes from <see cref="BasePrimes"/>.</param>
    /// <param name="bitmap">The destination bitmap; its tail word is left unmasked.</param>
    /// <param name="bits">The number of odd values in the window.</param>
    /// <param name="low">The first (odd) value of the window; must be at least <c>3</c>.</param>
    internal static void MarkWindow(ReadOnlySpan<uint> basePrimes, Span<ulong> bitmap, ulong bits, ulong low) {
        var high = (low + ((bits - 1UL) << 1));

        bitmap[..((int)((bits + 63UL) >> 6))].Clear();

        for (var i = 0; (i < basePrimes.Length); ++i) {
            var prime = ((ulong)basePrimes[i]);
            var start = (prime * prime);

            if (start > high) { break; }
            if (start < low) {
                var quotient = ((low + (prime - 1UL)) / prime);

                if (0UL == (quotient & 1UL)) { ++quotient; }

                start = (quotient * prime);
            }

            // Stride the bit index by the prime directly: consecutive odd multiples sit 2·prime apart in value, one
            // prime apart in the odd-only bitmap.
            var lastBit = ((high - low) >> 1);

            for (var bit = ((start - low) >> 1); (bit <= lastBit); bit += prime) {
                bitmap[((int)(bit >> 6))] |= (1UL << ((int)(bit & 63UL)));
            }
        }
    }

    /// <summary>Advances one step of the factoring polynomial <c>y² + addend</c>, spending one unit of the split's budget.</summary>
    /// <param name="addend">The polynomial offset distinguishing one cycle walk from another.</param>
    /// <param name="budget">The remaining advances this split is allowed; decremented, and its exhaustion is the named failure.</param>
    /// <param name="ring">The Montgomery ring over the value being split.</param>
    /// <param name="value">The residue to advance.</param>
    /// <returns>The advanced residue.</returns>
    /// <remarks>
    /// <b>Every loop in the splitter spends the budget through this one door</b>, which is what makes each of them
    /// bounded — the offset restarts, the range-doubling walk, and the backtrack scan alike. That matters most for the
    /// backtrack scan, whose exit condition is a greatest common divisor that a modulus with no nontrivial divisor never
    /// produces: over a prime the anchor may sit in the trajectory's tail, which the scan then never revisits, so without
    /// a budget that loop does not terminate at all rather than merely running long.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Advance(ulong addend, ref int budget, in ScaledResidueRing64 ring, ulong value) {
        if (0 >= budget--) {
            throw new InvalidOperationException(message: $"The cycle-walk splitter exhausted its advance budget on {ring.Modulus}, which is therefore not the odd composite free of factors below sixty-one that it requires. A prime reaching here means a primality gate upstream answered wrongly.");
        }

        return ring.Add(
            left: ring.Multiply(
                left: value,
                right: value
            ),
            right: addend
        );
    }
    /// <summary>Builds the process-wide table of odd primes below 65,536.</summary>
    /// <returns>The 6,541 primes required by every 32-bit window sieve.</returns>
    private static uint[] CreateBasePrimes() {
        var basePrimes = new uint[6541];
        var composite = new bool[65536];
        var count = 0;

        for (var candidate = 3; (candidate < 65536); candidate += 2) {
            if (composite[candidate]) { continue; }

            basePrimes[count++] = ((uint)candidate);

            for (var m = (((long)candidate) * candidate); (m < 65536L); m += (((long)candidate) << 1)) { composite[m] = true; }
        }

        return basePrimes;
    }
    /// <summary>Builds <see cref="SmallFactorInverses"/> from <see cref="SmallFactorPrimes"/>.</summary>
    /// <returns>The inverse of each small factor modulo 2⁶⁴.</returns>
    private static ulong[] CreateSmallFactorInverses() {
        var primes = SmallFactorPrimes;
        var inverses = new ulong[primes.Length];

        for (var i = 0; (i < primes.Length); ++i) { inverses[i] = primes[i].ModularInverse(); }

        return inverses;
    }
    /// <summary>Runs one Brent cycle walk over the polynomial <c>y² + addend</c> modulo <paramref name="ring"/>'s modulus.</summary>
    /// <param name="addend">The polynomial offset for this walk.</param>
    /// <param name="budget">The remaining advances this split is allowed, shared across every offset the caller tries.</param>
    /// <param name="ring">The Montgomery ring over the odd composite to split.</param>
    /// <returns>A divisor of the modulus; the walk failed when the result is <c>1</c> or the modulus itself.</returns>
    /// <remarks>
    /// The offset is added in the ring's own representation rather than converted into it. That is deliberate and costs
    /// nothing: the walk needs a map that mixes, not one that means anything, and every divisor it reports is confirmed
    /// by a greatest-common-divisor against the modulus before it is believed.
    /// </remarks>
    private static ulong FindFactorCycle(ulong addend, ref int budget, in ScaledResidueRing64 ring) {
        var modulus = ring.Modulus;
        var divisor = 1UL;
        var product = ring.One;
        var range = 1UL;
        var anchor = 0UL;
        var backtrack = 0UL;
        var y = Advance(
            addend: addend,
            budget: ref budget,
            ring: in ring,
            value: ring.Encode(value: 2UL)
        );

        do {
            anchor = y;

            for (var i = 0UL; (i < range); ++i) {
                y = Advance(
                    addend: addend,
                    budget: ref budget,
                    ring: in ring,
                    value: y
                );
            }

            var k = 0UL;

            do {
                backtrack = y;

                var limit = Math.Min(
                    val1: 32UL,
                    val2: (range - k)
                );

                for (var i = 0UL; (i < limit); ++i) {
                    y = Advance(
                        addend: addend,
                        budget: ref budget,
                        ring: in ring,
                        value: y
                    );
                    product = ring.Multiply(
                        left: product,
                        right: ring.Subtract(
                            left: ((anchor > y)
                        ? anchor
                        : y),
                            right: ((anchor > y)
                        ? y
                        : anchor)
                        )
                    );
                }

                divisor = product.GreatestCommonDivisor(other: modulus);
                k += 32UL;
            } while ((k < range) && (1UL == divisor));

            range <<= 1;
        } while (1UL == divisor);

        if (divisor == modulus) {
            do {
                backtrack = Advance(
                    addend: addend,
                    budget: ref budget,
                    ring: in ring,
                    value: backtrack
                );
                divisor = ring.Subtract(
                    left: ((anchor > backtrack)
                    ? anchor
                    : backtrack),
                    right: ((anchor > backtrack)
                    ? backtrack
                    : anchor)
                ).GreatestCommonDivisor(other: modulus);
            } while (1UL == divisor);
        }

        return divisor;
    }
}

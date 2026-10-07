using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>
/// The word-sized prime machinery every public prime entry point in the library runs on: the base-prime table, the
/// odd-window marker, the exact primality dispatch, and the deterministic cycle-walk splitter.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is a second expression of anything else. Each routine is written once, at the widest carrier a machine
/// word offers, and the narrower public surfaces widen into it rather than carrying their own copy:
/// <see cref="PrimeExtensions.Factorize(uint, Span{uint})"/> and
/// <see cref="UnsignedNumberFunctions.EnumeratePrimeFactors{T}(T)"/> both reach <see cref="Factorize(ulong, Span{ulong})"/>,
/// <see cref="PrimeExtensions.NthPrime(uint)"/> strides <see cref="MarkWindow(ReadOnlySpan{uint}, Span{ulong}, ulong, ulong)"/>.
/// <see cref="PrimeExploration"/> owns thirty-wheel marking and streams upper base primes through that optimized sieve.
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
    /// merely unrefuted. <see cref="BigIntegerFunctions.IsPrime(BigInteger)"/> uses this arbitrary-width table;
    /// the machine-word decision uses the finite-domain Baillie–PSW result instead.
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

    /// <summary>The number of base-two strong rounds <see cref="PassesBaseTwoBatch(ulong, ulong, ulong)"/> interleaves.</summary>
    /// <remarks>Three independent squaring chains keep the multiplier busy through each other's latency while the
    /// loop's live state still fits the general-purpose registers.</remarks>
    internal const int BaseTwoBatchWidth = 3;
    /// <summary>The largest prime whose multiples <see cref="PassesSelectionPrimeFilter(ulong)"/> rejects.</summary>
    internal const uint SelectionFilterCeiling = 163U;

    /// <summary>The offset of the three moduli in a <see cref="WalkBaseTwoBatch(Span{ulong}, ref BaseTwoBatchPowers)"/> block.</summary>
    private const int BatchModuli = 0;
    /// <summary>The offset of the three inverses modulo <c>2^64</c> in a batch block.</summary>
    private const int BatchInverses = (BatchModuli + BaseTwoBatchWidth);
    /// <summary>The offset of the three odd exponents in a batch block.</summary>
    private const int BatchExponents = (BatchInverses + BaseTwoBatchWidth);
    /// <summary>The offset of the bit count every exponent fits, in a batch block, its last entry.</summary>
    private const int BatchSteps = (BatchExponents + BaseTwoBatchWidth);
    /// <summary>The length of a batch block.</summary>
    private const int BatchBlockLength = (BatchSteps + 1);
    /// <summary>The first discriminant magnitude Selfridge's Method A tries, <c>D = 5</c>, the one whose <c>Q</c> is <c>-1</c>.</summary>
    private const ulong FirstSelfridgeMagnitude = 5UL;
    /// <summary>The bit width of an odd-prime rank word.</summary>
    private const int RankWordShift = 6;
    /// <summary>The exclusive bound on the discriminant magnitudes whose Jacobi symbols <see cref="SelfridgeSymbols"/> tabulates: one residue per bit of a word.</summary>
    private const ulong SelfridgeTableLimit = 64UL;

    /// <summary>Defers the shared base-prime table until a sieve or selection request needs it.</summary>
    private static class WindowSieve {
        internal static readonly uint[] BasePrimes;

        static WindowSieve() => BasePrimes = CreateBasePrimes();
    }
    /// <summary>Defers the odd-prime rank bitmap until a small-interval selection needs it.</summary>
    /// <remarks>
    /// Bit <c>v / 2</c> of <see cref="Bits"/> is set exactly for the odd primes <c>v</c> of <see cref="BasePrimes"/>,
    /// and <see cref="Counts"/> holds, per word, the number of set bits in every earlier word. One trailing zero word
    /// lets a rank at the table's exclusive end read in bounds.
    /// </remarks>
    private static class OddPrimeRanks {
        internal static readonly ulong[] Bits;
        internal static readonly ushort[] Counts;

        static OddPrimeRanks() {
            var primes = WindowSieve.BasePrimes;
            var words = ((((primes[^1] >>> 1) >>> RankWordShift) + 1) + 1);
            var bits = new ulong[words];
            var counts = new ushort[words];

            foreach (var prime in primes) {
                var position = (prime >>> 1);

                bits[(position >>> RankWordShift)] |= (1UL << ((int)position));
            }

            for (var word = 1; (word < words); ++word) {
                counts[word] = ((ushort)(counts[(word - 1)] + BitOperations.PopCount(value: bits[(word - 1)])));
            }

            Bits = bits;
            Counts = counts;
        }
    }
    /// <summary>Holds the Jacobi symbols over every small odd discriminant magnitude as residue masks.</summary>
    /// <remarks>
    /// Entry <c>m / 2</c> describes the odd magnitude <c>m</c> below <see cref="SelfridgeTableLimit"/>: bit <c>r</c> of
    /// <c>Negative</c> is set when the symbol <c>(r / m)</c> is <c>-1</c>, and bit <c>r</c> of <c>Vanishing</c> when it
    /// is <c>0</c>. Both are derived from <see cref="UnsignedNumberFunctions.JacobiSymbol{T}(T, T)"/>, so the table and
    /// the descent beyond it can never disagree.
    /// </remarks>
    private static class SelfridgeSymbols {
        internal static readonly (ulong Negative, ulong Vanishing)[] Masks = CreateMasks();

        private static (ulong Negative, ulong Vanishing)[] CreateMasks() {
            var masks = new (ulong Negative, ulong Vanishing)[(SelfridgeTableLimit >>> 1)];

            for (var magnitude = FirstSelfridgeMagnitude; (magnitude < SelfridgeTableLimit); magnitude += 2UL) {
                var negative = 0UL;
                var vanishing = 0UL;

                for (var residue = 0UL; (residue < magnitude); ++residue) {
                    var symbol = residue.JacobiSymbol(modulus: magnitude);

                    negative |= ((-1 == symbol).As<ulong>() << ((int)residue));
                    vanishing |= ((0 == symbol).As<ulong>() << ((int)residue));
                }

                masks[(magnitude >>> 1)] = (negative, vanishing);
            }

            return masks;
        }
    }
    /// <summary>Defers the selection filter's divisibility table until a wide selection request needs it.</summary>
    /// <remarks>
    /// One inverse and one ceiling per odd prime from seven through <see cref="SelectionFilterCeiling"/>, ascending:
    /// a value is divisible by the prime exactly when its product with the inverse does not exceed the ceiling. Wheel
    /// candidates are coprime to thirty already, so two, three and five are absent.
    /// </remarks>
    private static class SelectionFilter {
        internal static readonly (ulong Inverse, ulong Ceiling)[] Factors = CreateFactors();

        private static (ulong Inverse, ulong Ceiling)[] CreateFactors() {
            var primes = WindowSieve.BasePrimes.AsSpan();
            var first = primes.IndexOf(value: 7U);
            var end = (primes.IndexOf(value: SelectionFilterCeiling) + 1);
            var factors = new (ulong Inverse, ulong Ceiling)[(end - first)];

            for (var i = first; (i < end); ++i) {
                var prime = ((ulong)primes[i]);

                factors[(i - first)] = (prime.ModularInverse(), (ulong.MaxValue / prime));
            }

            return factors;
        }
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
    /// finite correction table. Above that range, wheel rejection precedes Baillie–PSW, whose exactness over this
    /// finite domain rests on the exhaustive result cited by <see cref="PrimeField64.IsBaillieProbablePrime(ulong)"/>.
    /// Both are exact, so the dispatch is a cost choice and never a correctness one.
    /// </remarks>
    internal static bool IsPrimeWord(ulong value) {
        if (value <= uint.MaxValue) { return ((uint)value).IsPrime(); }
        if (((value & 1UL) == 0UL) || ((value % 3UL) == 0UL) || ((value % 5UL) == 0UL)) { return false; }

        return IsPrimeCandidateWord(value: value);
    }
    /// <summary>Decides a word already known to be coprime to thirty, including the nonprime value one.</summary>
    /// <param name="value">The wheel candidate.</param>
    /// <returns>Its exact primality.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsPrimeCandidateWord(ulong value) => ((value <= uint.MaxValue)
        ? ((uint)value).IsPrime()
        : IsBaillieOddWord(value: value));
    /// <summary>Decides Baillie–PSW for an odd value, spending one Montgomery setup on both of its rounds.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <returns>Whether <paramref name="value"/> passes the base-two strong round and the strong Lucas test with Selfridge Method A parameters.</returns>
    /// <remarks>The composition <see cref="PrimeField64.IsBaillieProbablePrime(ulong)"/> states; the ring's inverse and one are derived once and shared by both rounds.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool IsBaillieOddWord(ulong value) {
        var inverse = value.ModularInverse();
        var one = ScaledResidueRing64.RadixResidue(modulus: value);

        return (
            PassesBaseTwo(inverse: inverse, one: one, value: value) &&
            PassesStrongLucas(inverse: inverse, one: one, value: value)
        );
    }
    /// <summary>Returns whether a value has no prime factor from seven through <see cref="SelectionFilterCeiling"/>.</summary>
    /// <param name="value">The value, coprime to thirty and above <see cref="SelectionFilterCeiling"/>, so that a filter prime can only divide it properly.</param>
    /// <returns>Whether no filter prime divides <paramref name="value"/>.</returns>
    /// <remarks>Each test is one truncated multiply and one comparison against the prime's inverse and ceiling; no division runs.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PassesSelectionPrimeFilter(ulong value) {
        foreach (var factor in SelectionFilter.Factors) {
            if (unchecked((value * factor.Inverse)) <= factor.Ceiling) { return false; }
        }

        return true;
    }
    /// <summary>Returns the number of odd primes strictly below a value no greater than 65,536.</summary>
    /// <param name="value">The exclusive bound, at most 65,536.</param>
    /// <returns>The count of odd entries of <see cref="BasePrimes"/> below <paramref name="value"/>, which is also the index of the first entry at or above it.</returns>
    /// <remarks>One masked population count over the odd-prime bitmap plus the cumulative count of the words before it.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int CountOddPrimesBelow(uint value) {
        var position = (value >>> 1);
        var word = ((int)(position >>> RankWordShift));
        var below = OddPrimeRanks.Bits[word] & ((1UL << ((int)position)) - 1UL);

        return (OddPrimeRanks.Counts[word] + BitOperations.PopCount(value: below));
    }
    /// <summary>Runs the base-two strong-probable-prime round on an odd value, inside Montgomery constants the caller already holds.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <param name="one">The radix reduced modulo the value, <see cref="ScaledResidueRing64.RadixResidue(ulong)"/>.</param>
    /// <param name="inverse">The value's inverse modulo <c>2^64</c>.</param>
    /// <returns>Whether the round passes.</returns>
    /// <remarks>
    /// <para>
    /// Two in Montgomery form is <c>one + one</c>, so the round encodes nothing. With <c>value - 1 = d * 2^s</c>, the
    /// power <c>2^d</c> runs least significant bit first; the lowest bit of the odd <c>d</c> is set, so the accumulator
    /// starts at two rather than spending a product on one.
    /// </para>
    /// <para>
    /// The squared power is the left factor of both products, which is the operand <c>MULX</c> reads implicitly, so the
    /// squaring chain — the critical path — stays in one register. The inverse is the last parameter so that it does not
    /// arrive in that register.
    /// </para>
    /// </remarks>
    internal static bool PassesBaseTwo(ulong value, ulong one, ulong inverse) {
        var two = ScaledResidueRing64.Add(
            left: one,
            modulus: value,
            right: one
        );
        var oddPart = (value - 1UL);
        var twoExponent = BitOperations.TrailingZeroCount(value: oddPart);
        var power = two;
        var result = two;

        oddPart >>>= twoExponent;

        while (0UL != (oddPart >>>= 1)) {
            power = ScaledResidueRing64.Reduce(
                inverse: inverse,
                left: power,
                modulus: value,
                right: power
            );

            if (0UL != (oddPart & 1UL)) {
                result = ScaledResidueRing64.Reduce(
                    inverse: inverse,
                    left: power,
                    modulus: value,
                    right: result
                );
            }
        }

        return FinishStrongRound(
            inverse: inverse,
            one: one,
            power: result,
            twoExponent: twoExponent,
            value: value
        );
    }
    /// <summary>Runs the base-two strong-probable-prime round on three odd values at once.</summary>
    /// <param name="first">The first odd value, at least three.</param>
    /// <param name="second">The second odd value, at least three.</param>
    /// <param name="third">The third odd value, at least three.</param>
    /// <returns>A mask whose bit <c>i</c> is set when the <c>i</c>-th value passes; each bit equals <see cref="PassesBaseTwo(ulong, ulong, ulong)"/> on its value.</returns>
    /// <remarks>
    /// The three squaring chains are independent, so interleaving them overlaps each chain's multiply latency with the
    /// other two. Each power <c>2^d</c> runs most significant bit first, which needs no second multiply per bit: a set
    /// bit doubles the accumulator, and doubling is a modular addition. The doubling is masked rather than branched, and
    /// every chain walks the bit length of the longest odd part, starting from one, whose square is one, so a shorter
    /// exponent's leading zeros change nothing. The squarings of the strong round's tail are per value and sequential,
    /// as in the single round.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int PassesBaseTwoBatch(ulong first, ulong second, ulong third) {
        Span<ulong> block = stackalloc ulong[BatchBlockLength];
        var firstExponent = BitOperations.TrailingZeroCount(value: (first - 1UL));
        var secondExponent = BitOperations.TrailingZeroCount(value: (second - 1UL));
        var thirdExponent = BitOperations.TrailingZeroCount(value: (third - 1UL));
        var firstBits = ((first - 1UL) >>> firstExponent);
        var secondBits = ((second - 1UL) >>> secondExponent);
        var thirdBits = ((third - 1UL) >>> thirdExponent);
        var lead = BitOperations.LeadingZeroCount(value: (firstBits | secondBits) | thirdBits);
        var firstOne = ScaledResidueRing64.RadixResidue(modulus: first);
        var secondOne = ScaledResidueRing64.RadixResidue(modulus: second);
        var thirdOne = ScaledResidueRing64.RadixResidue(modulus: third);

        block[BatchModuli] = first;
        block[(BatchModuli + 1)] = second;
        block[(BatchModuli + 2)] = third;
        block[BatchInverses] = first.ModularInverse();
        block[(BatchInverses + 1)] = second.ModularInverse();
        block[(BatchInverses + 2)] = third.ModularInverse();

        block[BatchExponents] = firstBits;
        block[(BatchExponents + 1)] = secondBits;
        block[(BatchExponents + 2)] = thirdBits;
        block[BatchSteps] = ((ulong)(64 - lead));

        var powers = new BaseTwoBatchPowers(
            first: firstOne,
            second: secondOne,
            third: thirdOne
        );

        WalkBaseTwoBatch(block: block, powers: ref powers);

        return FinishStrongRound(inverse: block[BatchInverses], one: firstOne, power: powers.First, twoExponent: firstExponent, value: first).As<int>() |
            (FinishStrongRound(inverse: block[(BatchInverses + 1)], one: secondOne, power: powers.Second, twoExponent: secondExponent, value: second).As<int>() << 1) |
            (FinishStrongRound(inverse: block[(BatchInverses + 2)], one: thirdOne, power: powers.Third, twoExponent: thirdExponent, value: third).As<int>() << 2);
    }

    /// <summary>Squares three Montgomery accumulators once per exponent bit, most significant first, doubling each where its own exponent's bit is set.</summary>
    /// <param name="block">The batch block: moduli, inverses, exponents, and the bit count, at the <c>Batch</c> offsets.</param>
    /// <param name="powers">The three accumulators, raised in place.</param>
    /// <remarks>
    /// A loop of its own. The constants and exponents are read from the block on every step rather than held in
    /// locals, which leaves registers for the accumulators; read from memory they cost loads that issue ahead of the
    /// chain. The accumulators arrive through the second argument, whose reference occupies the operand register
    /// <c>MULX</c> reads implicitly while they load, so none of them is homed there: a term homed in that register is
    /// given a stack slot inside the chain whenever another product needs the register.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WalkBaseTwoBatch(Span<ulong> block, ref BaseTwoBatchPowers powers) {
        // The highest index is checked once, which proves every lower constant index in range.
        var bit = ((int)block[BatchSteps]);
        var first = powers.First;
        var second = powers.Second;
        var third = powers.Third;

        // Each chain's step is written whole; the out-of-order core overlaps the three, and a chain's temporaries die
        // before the next chain's begin.
        while (0 <= --bit) {
            first = SquareAndDouble(
                doubling: (0UL - ((block[BatchExponents] >>> bit) & 1UL)),
                inverse: block[BatchInverses],
                modulus: block[BatchModuli],
                value: first
            );
            second = SquareAndDouble(
                doubling: (0UL - ((block[(BatchExponents + 1)] >>> bit) & 1UL)),
                inverse: block[(BatchInverses + 1)],
                modulus: block[(BatchModuli + 1)],
                value: second
            );
            third = SquareAndDouble(
                doubling: (0UL - ((block[(BatchExponents + 2)] >>> bit) & 1UL)),
                inverse: block[(BatchInverses + 2)],
                modulus: block[(BatchModuli + 2)],
                value: third
            );
        }

        powers = new BaseTwoBatchPowers(
            first: first,
            second: second,
            third: third
        );
    }

    /// <summary>Runs the strong Lucas probable-prime test with Selfridge's Method A parameters on an odd value, inside Montgomery constants the caller already holds.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <param name="one">The radix reduced modulo the value.</param>
    /// <param name="inverse">The value's inverse modulo <c>2^64</c>.</param>
    /// <returns>Whether <paramref name="value"/> is a strong Lucas probable prime; a perfect square is not.</returns>
    /// <remarks>
    /// <para>
    /// Method A takes <c>D</c> to be the first of <c>5, -7, 9, -11, 13, ...</c> whose Jacobi symbol over the value is
    /// <c>-1</c>, then <c>P = 1</c> and <c>Q = (1 - D) / 4</c>. Every candidate is congruent to one modulo four, so
    /// quadratic reciprocity turns <c>(D / value)</c> into <c>(value mod |D| / |D|)</c> with no sign to track, zero
    /// cases included: below <see cref="SelfridgeTableLimit"/> the symbol is one bit of a precomputed residue mask, and
    /// beyond it the shipped descent. A vanishing symbol means the value shares a factor with the candidate, which is a
    /// proper divisor unless the value divides the candidate. A perfect square never reaches a symbol of <c>-1</c>; the
    /// square test that bounds its search runs only once the tabulated candidates are exhausted, which no non-square
    /// on the random-selection path reaches in practice.
    /// </para>
    /// <para>
    /// With <c>value + 1 = d * 2^s</c>, the test accepts when <c>U_d</c> or some <c>V_(d * 2^r)</c> with <c>r</c> below
    /// <c>s</c> vanishes. The ladder carries only <c>V_k</c> and <c>V_(k+1)</c> — <c>V_2k = V_k^2 - 2Q^k</c>,
    /// <c>V_(2k+1) = V_k V_(k+1) - P Q^k</c>, <c>V_(2k+2) = V_(k+1)^2 - 2Q^(k+1)</c> — and reads <c>U_d</c> through
    /// <c>D U_k = 2V_(k+1) - P V_k</c>. A symbol of <c>-1</c> makes <c>D</c> a unit modulo the value, so <c>U_d</c>
    /// vanishes exactly when <c>2V_(d+1) = V_d</c>, and the predicate is the strong Lucas test's own.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool PassesStrongLucas(ulong value, ulong one, ulong inverse) {
        var magnitude = FirstSelfridgeMagnitude;
        var symbol = SelfridgeSymbol(
            magnitude: magnitude,
            residue: (value % FirstSelfridgeMagnitude)
        );

        while (-1 != symbol) {
            if (
                (0 == symbol) &&
                (0UL != (magnitude % value))
            ) { return false; }

            magnitude += 2UL;

            if (SelfridgeTableLimit > magnitude) {
                symbol = SelfridgeSymbol(
                    magnitude: magnitude,
                    residue: (value % magnitude)
                );
            } else {
                if ((SelfridgeTableLimit + 1UL) == magnitude) {
                    var root = value.SquareRoot();

                    if (value == (root * root)) { return false; }
                }

                symbol = value.JacobiSymbol(modulus: magnitude);
            }
        }

        // value + 1 = d * 2^s; the shifted form stays inside the carrier at value = ulong.MaxValue, where d is one.
        var twoExponent = BitOperations.TrailingZeroCount(value: (value + 1UL));
        var oddPart = (((value >>> 1) >>> (twoExponent - 1)) + 1UL);

        if (FirstSelfridgeMagnitude == magnitude) {
            return PassesLucasLadder(
                inverse: inverse,
                oddPart: oddPart,
                one: one,
                powers: new UnitLucasPowers(
                    negativeOne: (value - one),
                    one: one
                ),
                twoExponent: twoExponent,
                value: value
            );
        }

        var isNegativeDiscriminant = (3UL == (magnitude & 3UL));
        // Q = (1 - D) / 4, scaled into the ring by doubling and adding one: no encoding product and no division.
        var q = ScaleSmall(
            modulus: value,
            multiplier: ((isNegativeDiscriminant
                ? (magnitude + 1UL)
                : (magnitude - 1UL)) >>> 2),
            one: one
        );

        if (!isNegativeDiscriminant) {
            q = ScaledResidueRing64.Subtract(
                left: 0UL,
                modulus: value,
                right: q
            );
        }

        return PassesLucasLadder(
            inverse: inverse,
            oddPart: oddPart,
            one: one,
            powers: new ScaledLucasPowers(
                q: q,
                square: ScaledResidueRing64.Reduce(
                    inverse: inverse,
                    left: q,
                    modulus: value,
                    right: q
                )
            ),
            twoExponent: twoExponent,
            value: value
        );
    }
    /// <summary>Runs one strong-probable-prime round in an already-created odd residue ring.</summary>
    /// <param name="ring">The ring over an odd candidate greater than one.</param>
    /// <param name="oddPart">The odd part of the candidate minus one.</param>
    /// <param name="twoExponent">Its exponent of two.</param>
    /// <param name="witness">The unreduced witness.</param>
    /// <returns>Whether the round passes; a zero reduced witness carries no evidence and passes.</returns>
    internal static bool PassesWitness(in ScaledResidueRing64 ring, ulong oddPart, int twoExponent, ulong witness) {
        var residue = (witness % ring.Modulus);

        if (residue == 0UL) { return true; }

        var power = ring.Power(value: ring.Encode(value: residue), exponent: oddPart);
        var negativeOne = ring.NegativeOne;

        if ((power == ring.One) || (power == negativeOne)) { return true; }

        for (var round = 1; (round < twoExponent); ++round) {
            power = ring.Multiply(left: power, right: power);

            if (power == negativeOne) { return true; }
        }

        return false;
    }
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
    /// <summary>Squares a Montgomery residue and doubles the square under a mask.</summary>
    /// <param name="value">The reduced Montgomery-form accumulator.</param>
    /// <param name="doubling">All ones to double the square, zero to keep it.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
    /// <returns>The reduced square, doubled for a set bit.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SquareAndDouble(ulong value, ulong doubling, ulong modulus, ulong inverse) {
        var square = ScaledResidueRing64.Reduce(
            inverse: inverse,
            left: value,
            modulus: modulus,
            right: value
        );

        return ScaledResidueRing64.Add(
            left: square,
            modulus: modulus,
            right: square & doubling
        );
    }

    /// <summary>Returns the Jacobi symbol of a residue over a small odd discriminant magnitude from the precomputed masks.</summary>
    /// <param name="magnitude">The odd magnitude, from <see cref="FirstSelfridgeMagnitude"/> up to <see cref="SelfridgeTableLimit"/>, exclusive.</param>
    /// <param name="residue">The residue, below <paramref name="magnitude"/>.</param>
    /// <returns>The symbol <c>(residue / magnitude)</c>: <c>-1</c>, <c>0</c> or <c>1</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SelfridgeSymbol(ulong magnitude, ulong residue) {
        var masks = SelfridgeSymbols.Masks[((int)(magnitude >>> 1))];
        var bit = ((int)residue);

        return ((1 - (((int)((masks.Negative >>> bit) & 1UL)) << 1)) - ((int)((masks.Vanishing >>> bit) & 1UL)));
    }

    /// <summary>Completes a strong-probable-prime round from the odd-part power.</summary>
    /// <param name="value">The odd modulus.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
    /// <param name="one">The Montgomery form of one.</param>
    /// <param name="power">The Montgomery-form power of the witness to the odd part of <c>value - 1</c>.</param>
    /// <param name="twoExponent">The exponent of two in <c>value - 1</c>.</param>
    /// <returns>Whether the power is one or minus one, or squares to minus one within the remaining exponent.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool FinishStrongRound(ulong value, ulong inverse, ulong one, ulong power, int twoExponent) {
        var negativeOne = (value - one);

        if ((power == one) || (power == negativeOne)) { return true; }

        for (var round = 1; (round < twoExponent); ++round) {
            power = ScaledResidueRing64.Reduce(
                inverse: inverse,
                left: power,
                modulus: value,
                right: power
            );

            if (power == negativeOne) { return true; }
        }

        return false;
    }
    /// <summary>Walks the V-only Lucas ladder and applies the strong Lucas acceptance tests.</summary>
    /// <typeparam name="TPowers">The carrier of <c>Q^k</c> and <c>Q^(k+1)</c>, specialized without boxing.</typeparam>
    /// <param name="value">The odd modulus.</param>
    /// <param name="powers">The powers of <c>Q</c> at <c>k = 1</c>.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
    /// <param name="one">The Montgomery form of one, which is also <c>V_1 = P</c>.</param>
    /// <param name="oddPart">The odd part <c>d</c> of <c>value + 1</c>.</param>
    /// <param name="twoExponent">The exponent of two in <c>value + 1</c>.</param>
    /// <returns>Whether <c>U_d</c> or some <c>V_(d * 2^r)</c> with <c>r</c> below the exponent vanishes.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool PassesLucasLadder<TPowers>(ulong value, TPowers powers, ulong inverse, ulong one, ulong oddPart, int twoExponent)
        where TPowers : struct, ILucasPowers {
        var low = one; // V_k
        var high = ScaledResidueRing64.Subtract(
            left: one,
            modulus: value,
            right: Twice(modulus: value, value: powers.Current)
        ); // V_(k+1) = P^2 - 2Q
        var walked = powers;

        WalkLucasLadder(
            current: ref low,
            inverse: inverse,
            next: ref high,
            oddPart: oddPart,
            powers: ref walked,
            value: value
        );

        // Fresh copies, so the tail's terms live in registers rather than in the walk's by-reference slots.
        var current = low;
        var next = high;
        var tail = walked;

        // D U_d = 2V_(d+1) - P V_d with P = 1 and D a unit, so U_d vanishes exactly when 2V_(d+1) = V_d.
        if ((0UL == current) || (Twice(modulus: value, value: next) == current)) { return true; }

        for (var round = 1; (round < twoExponent); ++round) {
            current = ScaledResidueRing64.Subtract(
                left: ScaledResidueRing64.Reduce(inverse: inverse, left: current, modulus: value, right: current),
                modulus: value,
                right: Twice(modulus: value, value: tail.Current)
            );

            if (0UL == current) { return true; }

            tail.Square(inverse: inverse, modulus: value);
        }

        return false;
    }
    /// <summary>Advances the pair <c>(V_k, V_(k+1))</c> from <c>k = 1</c> to <c>k = d</c> over the bits of <c>d</c> below its leading one.</summary>
    /// <typeparam name="TPowers">The carrier of <c>Q^k</c> and <c>Q^(k+1)</c>.</typeparam>
    /// <param name="value">The odd modulus.</param>
    /// <param name="powers">The powers of <c>Q</c>, advanced alongside the pair.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
    /// <param name="current">The term <c>V_k</c>, replaced by <c>V_d</c>.</param>
    /// <param name="next">The term <c>V_(k+1)</c>, replaced by <c>V_(d+1)</c>.</param>
    /// <param name="oddPart">The odd index <c>d</c>.</param>
    /// <remarks>
    /// A loop of its own, holding only the pair, the powers and the constants: with the acceptance tests beside it the
    /// register allocator gives one term a stack home on the critical path. Each step's cross product takes the term
    /// that is not squared as its left factor, the operand <c>MULX</c> reads implicitly, and the second parameter — the
    /// register that operand arrives in — carries the powers' reference, read once.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WalkLucasLadder<TPowers>(ulong value, ref TPowers powers, ulong inverse, ref ulong current, ref ulong next, ulong oddPart)
        where TPowers : struct, ILucasPowers {
        var state = powers;
        var low = current;
        var high = next;

        for (var bit = (BitOperations.Log2(value: oddPart) - 1); (0 <= bit); --bit) {
            if (0UL != ((oddPart >>> bit) & 1UL)) {
                var cross = ScaledResidueRing64.Subtract(
                    left: ScaledResidueRing64.Reduce(inverse: inverse, left: low, modulus: value, right: high),
                    modulus: value,
                    right: state.Current
                ); // V_(2k+1)

                high = ScaledResidueRing64.Subtract(
                    left: ScaledResidueRing64.Reduce(inverse: inverse, left: high, modulus: value, right: high),
                    modulus: value,
                    right: Twice(modulus: value, value: state.Next)
                ); // V_(2k+2)
                low = cross;
                state.Advance(increment: true, inverse: inverse, modulus: value);
            } else {
                var cross = ScaledResidueRing64.Subtract(
                    left: ScaledResidueRing64.Reduce(inverse: inverse, left: high, modulus: value, right: low),
                    modulus: value,
                    right: state.Current
                ); // V_(2k+1)

                low = ScaledResidueRing64.Subtract(
                    left: ScaledResidueRing64.Reduce(inverse: inverse, left: low, modulus: value, right: low),
                    modulus: value,
                    right: Twice(modulus: value, value: state.Current)
                ); // V_2k
                high = cross;
                state.Advance(increment: false, inverse: inverse, modulus: value);
            }
        }

        powers = state;
        current = low;
        next = high;
    }
    /// <summary>Returns a small multiple of the Montgomery one, by doubling and adding.</summary>
    /// <param name="multiplier">The positive multiplier.</param>
    /// <param name="one">The Montgomery form of one.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <returns>The Montgomery form of <paramref name="multiplier"/>, reduced.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static ulong ScaleSmall(ulong multiplier, ulong one, ulong modulus) {
        var result = one;

        for (var bit = (BitOperations.Log2(value: multiplier) - 1); (0 <= bit); --bit) {
            result = Twice(modulus: modulus, value: result);

            if (0UL != ((multiplier >>> bit) & 1UL)) {
                result = ScaledResidueRing64.Add(
                    left: result,
                    modulus: modulus,
                    right: one
                );
            }
        }

        return result;
    }
    /// <summary>Doubles a reduced residue.</summary>
    /// <param name="value">The reduced residue.</param>
    /// <param name="modulus">The odd modulus.</param>
    /// <returns>The reduced double.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Twice(ulong value, ulong modulus) =>
        ScaledResidueRing64.Add(
            left: value,
            modulus: modulus,
            right: value
        );

    /// <summary>The three Montgomery accumulators of <see cref="WalkBaseTwoBatch(Span{ulong}, ref BaseTwoBatchPowers)"/>.</summary>
    /// <param name="first">The first accumulator.</param>
    /// <param name="second">The second accumulator.</param>
    /// <param name="third">The third accumulator.</param>
    private readonly struct BaseTwoBatchPowers(ulong first, ulong second, ulong third) {
        public ulong First { get; } = first;
        public ulong Second { get; } = second;
        public ulong Third { get; } = third;
    }
    /// <summary>Carries <c>Q^k</c> and <c>Q^(k+1)</c> through the Lucas ladder's index doublings.</summary>
    /// <remarks>The modulus and inverse arrive with each step rather than living in the carrier, so the ladder holds one copy of each.</remarks>
    private interface ILucasPowers {
        /// <summary>Gets <c>Q^k</c> in Montgomery form.</summary>
        ulong Current { get; }
        /// <summary>Gets <c>Q^(k+1)</c> in Montgomery form.</summary>
        ulong Next { get; }

        /// <summary>Moves the index from <c>k</c> to <c>2k</c>, or to <c>2k + 1</c> when <paramref name="increment"/> is set.</summary>
        /// <param name="increment">Whether the ladder's current exponent bit is set.</param>
        /// <param name="modulus">The odd modulus.</param>
        /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
        void Advance(bool increment, ulong modulus, ulong inverse);
        /// <summary>Moves the index from <c>k</c> to <c>2k</c>, keeping only <c>Q^k</c> current.</summary>
        /// <param name="modulus">The odd modulus.</param>
        /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
        void Square(ulong modulus, ulong inverse);
    }
    /// <summary>The powers of <c>Q = -1</c>, Method A's value at <c>D = 5</c>: a sign set by the index's parity, so the ladder spends no product on them.</summary>
    /// <param name="one">The Montgomery form of one.</param>
    /// <param name="negativeOne">The Montgomery form of minus one.</param>
    private struct UnitLucasPowers(ulong one, ulong negativeOne) : ILucasPowers {
        private readonly ulong m_one = one;
        private readonly ulong m_signs = one ^ negativeOne;
        // k = 1 is odd, so Q^k starts at minus one.
        private ulong m_oddMask = ulong.MaxValue;

        public readonly ulong Current => m_one ^ (m_signs & m_oddMask);
        public readonly ulong Next => m_one ^ (m_signs & ~m_oddMask);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(bool increment, ulong modulus, ulong inverse) => m_oddMask = unchecked((0UL - increment.As<ulong>()));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Square(ulong modulus, ulong inverse) => m_oddMask = 0UL;
    }
    /// <summary>The powers of a general <c>Q</c>, both carried so that every index doubling is one product deep.</summary>
    /// <param name="q">The Montgomery form of <c>Q</c>.</param>
    /// <param name="square">The Montgomery form of <c>Q^2</c>.</param>
    private struct ScaledLucasPowers(ulong q, ulong square) : ILucasPowers {
        private ulong m_current = q;
        private ulong m_next = square;

        public readonly ulong Current => m_current;
        public readonly ulong Next => m_next;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Advance(bool increment, ulong modulus, ulong inverse) {
            var cross = ScaledResidueRing64.Reduce(inverse: inverse, left: m_current, modulus: modulus, right: m_next); // Q^(2k+1)

            if (increment) {
                m_next = ScaledResidueRing64.Reduce(inverse: inverse, left: m_next, modulus: modulus, right: m_next);
                m_current = cross;
            } else {
                m_current = ScaledResidueRing64.Reduce(inverse: inverse, left: m_current, modulus: modulus, right: m_current);
                m_next = cross;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Square(ulong modulus, ulong inverse) =>
            m_current = ScaledResidueRing64.Reduce(inverse: inverse, left: m_current, modulus: modulus, right: m_current);
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

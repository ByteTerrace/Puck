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
/// </para>
/// <para>
/// The arbitrary-width counterparts live in <see cref="BigIntegerFunctions"/>. That split is real rather than
/// incidental: the routines here reduce through a Montgomery ring and precomputed reciprocals that exist only because
/// the operands fit a register, and none of that survives the move to <see cref="BigInteger"/>.
/// </para>
/// </remarks>
public static partial class PrimeKernels {
    /// <summary>Gets the ascending odd primes below 65,536 — every base prime a 32-bit window sieve can need, since the largest such value's square root is 65,535.</summary>
    internal static ReadOnlySpan<uint> BasePrimes => WindowSieve.BasePrimes;
    /// <summary>Gets the odd primes through fifty-nine, the first sixteen entries of the trial-division table <see cref="TrialDivisor(int)"/> reads, which <see cref="Factorize(ulong, Span{ulong})"/> strips before it splits.</summary>
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
    public static BigInteger LeastWitnessFailure { get; } = BigInteger.Parse(value: "318665857834031151167461");

    /// <summary>The polynomial advances one call to <see cref="FindFactor(ulong)"/> may spend before it refuses.</summary>
    /// <remarks>
    /// A cycle walk finds a factor <c>p</c> in about <c>√p</c> advances, so the worst legal operand — a semiprime of two
    /// primes either side of 2³² — costs on the order of 2¹⁶, and the offset restarts multiply that by a small constant.
    /// 2²⁴ therefore clears the worst legal operand by roughly two hundred times over: no input this kernel is
    /// contracted to accept can reach it, and the ceiling exists only so that every loop in the splitter terminates.
    /// </remarks>
    private const int SplitAdvanceBudget = (1 << 24);
    /// <summary>The leading exponent bits the single base-two round absorbs below <see cref="ScaledResidueRing64.SignedModulusCeiling"/>, so that every chain walks that many fewer steps.</summary>
    /// <remarks>
    /// <para>
    /// Seven: every seed is then <c>2^-a</c> with a prefix <c>a</c> of at most <c>2^7</c>, and
    /// <see cref="InverseTwoPower(ulong, ulong, ulong, ulong)"/> reaches every such prefix with two multiplies per
    /// chain, a fraction of the step each absorbed bit saves. An eighth bit would take prefixes past <c>2^7</c>,
    /// whose seeds need a second, dependent product to save that one step.
    /// </para>
    /// </remarks>
    private const int BaseTwoSeedWindow = 7;
    /// <summary>The first discriminant magnitude Selfridge's Method A tries, <c>D = 5</c>, the one whose <c>Q</c> is <c>-1</c>.</summary>
    private const ulong FirstSelfridgeMagnitude = 5UL;
    /// <summary>The product <c>3 * 5 * 7 * 11 * 13 = 15015</c>: one remainder of a value by it, a multiply by the JIT's reciprocal, yields the value's residues modulo three and every candidate magnitude of <see cref="SelfridgeMagnitudeCore(ulong)"/> below 17 as constant-divisor remainders of a 14-bit value.</summary>
    private const ulong SelfridgeSearchModulus = 15015UL;
    /// <summary>The bit width of an odd-prime bitmap word.</summary>
    private const int RankWordShift = 6;
    /// <summary>The exclusive bound on the discriminant magnitudes whose Jacobi symbols <see cref="SelfridgeSymbols"/> tabulates: one residue per bit of a word.</summary>
    private const ulong SelfridgeTableLimit = 64UL;
    /// <summary>The exclusive bound on the odd parts whose reciprocal digits <see cref="ReciprocalDigits"/> tabulates: every odd part of a parameter <c>Q = (1 - D) / 4</c> with <c>|D|</c> below <see cref="SelfridgeTableLimit"/>, whose magnitude is at most sixteen.</summary>
    private const ulong ReciprocalDigitLimit = 16UL;
    /// <summary>The exclusive bound on the magnitude of a parameter <see cref="PassesStrongLucasRound(ulong, long)"/> accepts, which keeps <c>4|Q| + 1</c> below <c>2^63</c>.</summary>
    private const long ParameterMagnitudeLimit = (1L << 61);

    /// <summary>Defers the shared base-prime table until a prime kernel needs it.</summary>
    private static class WindowSieve {
        internal static readonly uint[] BasePrimes;

        static WindowSieve() => BasePrimes = CreateBasePrimes();
    }
    /// <summary>Defers the odd-prime bitmap until a narrow primality decision needs it.</summary>
    /// <remarks>
    /// Bit <c>v / 2</c> of <see cref="Bits"/> is set exactly for the odd primes <c>v</c> of <see cref="BasePrimes"/>,
    /// with one bit for each odd candidate below 65,536.
    /// </remarks>
    private static class OddPrimeBitmap {
        internal static readonly ulong[] Bits;

        static OddPrimeBitmap() {
            var primes = WindowSieve.BasePrimes;
            var words = (((primes[^1] >>> 1) >>> RankWordShift) + 1);
            var bits = new ulong[words];

            foreach (var prime in primes) {
                var position = (prime >>> 1);

                bits[(position >>> RankWordShift)] |= (1UL << ((int)position));
            }

            Bits = bits;
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
    /// <summary>Holds the digits <see cref="TryScaledReciprocal(ulong, ulong, ulong, out ulong)"/> lifts the radix residue by, for every odd part below <see cref="ReciprocalDigitLimit"/>.</summary>
    /// <remarks>
    /// Entry <see cref="Index(ulong, ulong, ulong)"/> of <see cref="Entries"/> is the digit <c>k</c> for the odd part
    /// <c>q</c>, the radix residue reduced modulo <c>q</c> and the value reduced modulo <c>q</c>, or <see cref="None"/>
    /// where the value's residue is not a unit modulo <c>q</c>. <see cref="Inverses"/> holds each odd part's inverse
    /// modulo <c>2^64</c> at <c>q / 2</c>. Both are derived from <see cref="TryReciprocalDigit(ulong, ulong, ulong, out ulong)"/>
    /// and <see cref="UnsignedNumberFunctions.ModularInverse{T}(T)"/>, so the table and the derivation beyond it can never
    /// disagree.
    /// </remarks>
    private static class ReciprocalDigits {
        /// <summary>The entry marking a value residue that is not a unit, so that no digit exists.</summary>
        internal const byte None = byte.MaxValue;

        internal static readonly byte[] Entries = CreateEntries();
        internal static readonly ulong[] Inverses = CreateInverses();

        /// <summary>Returns the position of a digit in <see cref="Entries"/>.</summary>
        /// <param name="odd">The odd part, from three up to <see cref="ReciprocalDigitLimit"/>, exclusive.</param>
        /// <param name="radixResidue">The radix residue reduced modulo <paramref name="odd"/>.</param>
        /// <param name="modulusResidue">The value reduced modulo <paramref name="odd"/>.</param>
        /// <returns>The position: the odd part's half above eight bits, then four bits for each residue.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int Index(ulong odd, ulong radixResidue, ulong modulusResidue) =>
            ((int)((((odd >>> 1) << 8) | (radixResidue << 4)) | modulusResidue));

        private static byte[] CreateEntries() {
            var entries = new byte[(((int)(ReciprocalDigitLimit >>> 1)) << 8)];

            for (var odd = 3UL; (odd < ReciprocalDigitLimit); odd += 2UL) {
                for (var radixResidue = 0UL; (radixResidue < odd); ++radixResidue) {
                    for (var modulusResidue = 0UL; (modulusResidue < odd); ++modulusResidue) {
                        entries[Index(modulusResidue: modulusResidue, odd: odd, radixResidue: radixResidue)] = (TryReciprocalDigit(
                            digit: out var digit,
                            modulusResidue: modulusResidue,
                            odd: odd,
                            radixResidue: radixResidue
                        ) ? ((byte)digit) : None);
                    }
                }
            }

            return entries;
        }
        private static ulong[] CreateInverses() {
            var inverses = new ulong[(ReciprocalDigitLimit >>> 1)];

            for (var odd = 3UL; (odd < ReciprocalDigitLimit); odd += 2UL) { inverses[(odd >>> 1)] = odd.ModularInverse(); }

            return inverses;
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

        for (var i = 0; (i < primes.Length); ++i) {
            var prime = primes[i];

            // The largest small factor is fifty-nine, so the square never leaves the carrier and needs no widening.
            if ((prime * prime) > value) { break; }

            var (inverse, ceiling) = TrialDivisor(index: i);

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
    /// <see cref="PrimeExtensions.IsPrime(uint)"/> settles the 32-bit range on a bitmap, the narrow trial division and one
    /// strong-probable-prime round plus a finite correction table. Above that range, trial division by the odd primes through
    /// <see cref="TrialDivisionCeiling"/>, <see cref="HasTrialFactorCore(ulong)"/>, rejects about five in six odd
    /// values before Baillie–PSW, whose exactness over this finite domain rests on the exhaustive result cited by
    /// <see cref="PrimeField64.IsBaillieProbablePrime(ulong)"/>. Every step is exact, so the dispatch is a cost choice
    /// and never a correctness one.
    /// </remarks>
    internal static bool IsPrimeWord(ulong value) {
        if (value <= uint.MaxValue) { return ((uint)value).IsPrime(); }
        if ((0UL == (value & 1UL)) || HasTrialFactorCore(value: value)) { return false; }

        return IsBaillieOddWord(value: value);
    }
    /// <summary>Decides Baillie–PSW for an odd value, spending one Montgomery setup on both of its rounds.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <returns>Whether <paramref name="value"/> passes the base-two strong round and the strong Lucas test with Selfridge Method A parameters.</returns>
    /// <remarks>The composition <see cref="PrimeField64.IsBaillieProbablePrime(ulong)"/> states; the ring's inverse and one are derived once and shared by both rounds. Each round runs in the widest arithmetic the value admits: the base-two round as <see cref="PassesBaseTwoCore(ulong, ulong, ulong)"/> chooses and the Lucas round as <see cref="PassesStrongLucas(ulong, ulong, ulong)"/> chooses, both in <see cref="SignedArithmetic"/> below <see cref="ScaledResidueRing64.SignedModulusCeiling"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool IsBaillieOddWord(ulong value) {
        var inverse = value.ModularInverse();
        var one = ScaledResidueRing64.RadixResidue(modulus: value);
        var baseTwo = PassesBaseTwoCore(inverse: inverse, one: one, value: value);

        return (baseTwo && PassesStrongLucas(inverse: inverse, one: one, value: value));
    }

    /// <summary>Returns whether an odd value below 65,536 is prime, from the odd-prime bitmap.</summary>
    /// <param name="value">The odd value, below 65,536.</param>
    /// <returns>Whether <paramref name="value"/> is an entry of <see cref="BasePrimes"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsOddTablePrime(uint value) =>
        (0UL != ((OddPrimeBitmap.Bits[((int)(value >>> (RankWordShift + 1)))] >>> ((int)(value >>> 1))) & 1UL));

    /// <summary>Runs the base-two strong-probable-prime round on one odd value, the round the word decision runs before its Lucas test.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <returns>Whether <paramref name="value"/> is a strong probable prime to base two: <c>2^d</c> is one, or <c>2^(d * 2^r)</c> is minus one for some <c>r</c> below <c>s</c>, where <c>value - 1 = d * 2^s</c> with <c>d</c> odd.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is even or below three.</exception>
    /// <remarks>The checked shell over <see cref="PassesBaseTwoCore(ulong, ulong, ulong)"/>; the law <c>prime-field.base-two-round-vs-modular-power</c> compares it with plain modular exponentiation.</remarks>
    public static bool PassesBaseTwoRound(ulong value) {
        if ((3UL > value) || (0UL == (value & 1UL))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(value), actualValue: value, message: "The value must be odd and at least three.");
        }

        return PassesBaseTwoCore(
            inverse: value.ModularInverse(),
            one: ScaledResidueRing64.RadixResidue(modulus: value),
            value: value
        );
    }

    /// <summary>Runs the base-two strong-probable-prime round on one odd value in the arithmetic and walk its size admits, inside Montgomery constants the caller already holds.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <param name="one">The radix reduced modulo the value, <see cref="ScaledResidueRing64.RadixResidue(ulong)"/>.</param>
    /// <param name="inverse">The value's inverse modulo <c>2^64</c>.</param>
    /// <returns>Whether the round passes.</returns>
    /// <remarks>
    /// Below <see cref="ScaledResidueRing64.SignedModulusCeiling"/> the seeded walk
    /// <see cref="PassesBaseTwoSeeded(ulong, ulong, ulong)"/>, one signed chain of the batch's walk; from there the
    /// least-significant-bit-first walk <see cref="PassesBaseTwo(ulong, ulong, ulong)"/>, whose squares carry a borrow
    /// instead of folding. Every walk decides the same round, so the dispatch is a cost choice. The signed chain serves
    /// the whole region below its ceiling: below <see cref="ScaledResidueRing64.LazyModulusCeiling"/>, where the lazy
    /// step is admissible as well, the signed one is no slower.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PassesBaseTwoCore(ulong value, ulong one, ulong inverse) =>
        ((value < ScaledResidueRing64.SignedModulusCeiling)
            ? PassesBaseTwoSeeded(inverse: inverse, one: one, value: value)
            : PassesBaseTwo(inverse: inverse, one: one, value: value));

    /// <summary>Runs the base-two strong-probable-prime round on an odd value below <see cref="ScaledResidueRing64.SignedModulusCeiling"/> as one signed seeded chain of the batch's walk.</summary>
    /// <param name="value">The odd value, at least three and below <see cref="ScaledResidueRing64.SignedModulusCeiling"/>.</param>
    /// <param name="one">The radix reduced modulo the value, <see cref="ScaledResidueRing64.RadixResidue(ulong)"/>.</param>
    /// <param name="inverse">The value's inverse modulo <c>2^64</c>.</param>
    /// <returns>Whether the round passes.</returns>
    /// <remarks>
    /// <para>
    /// Two raised to the negated
    /// odd part <c>-d</c>, which accepts at the same round as <c>2^d</c>, from the seed <c>2^-a</c> of the prefix
    /// <c>a = ceil(d / 2^k)</c>, over the low <c>k</c> bits of <c>0 - d</c>, where <c>k</c> is this odd part's bit
    /// length less <see cref="BaseTwoSeedWindow"/>. The walk is most significant bit first, so a step is one square
    /// and an optional doubling rather than a square and an optional product, and there are six steps fewer than
    /// the least-significant-bit-first walk's squarings. A step's doubling follows a bit of the exponent, on which
    /// that walk branches and mispredicts about half the time; here it is a shift count.
    /// </para>
    /// <para>
    /// The step is <see cref="SignedArithmetic"/>'s <see cref="SignedArithmetic.SquareDouble(ulong, ulong, ulong, ulong)"/>,
    /// the signed batch's, which keeps the accumulator within the value of zero from the reduced seed on;
    /// <see cref="IResidueArithmetic.Canonical(ulong, ulong)"/> folds the power once, before the strong round's exact
    /// squarings.
    /// </para>
    /// </remarks>
    private static bool PassesBaseTwoSeeded(ulong value, ulong one, ulong inverse) {
        var oddPart = (value - 1UL);
        var twoExponent = BitOperations.TrailingZeroCount(value: oddPart);

        oddPart >>>= twoExponent;

        var steps = Math.Max(val1: ((64 - BitOperations.LeadingZeroCount(value: oddPart)) - BaseTwoSeedWindow), val2: 0);
        var power = InverseTwoPower(
            inverse: inverse,
            modulus: value,
            one: one,
            prefix: SignedPrefix(oddPart: oddPart, steps: steps)
        );
        var exponent = unchecked((0UL - oddPart));

        for (var bit = (steps - 1); (0 <= bit); --bit) {
            power = SignedArithmetic.SquareDouble(
                doubling: (exponent >>> bit) & 1UL,
                inverse: inverse,
                modulus: value,
                value: power
            );
        }

        return FinishStrongRound(
            inverse: inverse,
            one: one,
            power: SignedArithmetic.Canonical(modulus: value, value: power),
            twoExponent: twoExponent,
            value: value
        );
    }

    /// <summary>Runs the base-two strong-probable-prime round on an odd value least significant bit first, inside Montgomery constants the caller already holds, carrying the squared power with a borrow instead of folding it.</summary>
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
    /// The squaring chain, the critical path, is <see cref="ScaledResidueRing64.ReduceBorrowedSquare(ulong, ulong, ulong, ulong)"/>:
    /// its borrow is read only off the critical path, so no square is folded at any modulus. Above <c>2^63</c> no
    /// signed word holds a representative within the modulus of zero, and a doubling fused into the reduction would
    /// carry the square's high word past the carrier, so the most-significant-bit-first walk would fold every step;
    /// here the folds fall on the accumulator's products, a chain of their own beside the squares, on the bits that are
    /// set. A branch on each bit selects them: it reads the exponent alone and resolves ahead of the chain, so its
    /// mispredicts cost less than a product on every bit.
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
        var borrow = 0UL;
        var result = two;

        oddPart >>>= twoExponent;

        while (0UL != (oddPart >>>= 1)) {
            (power, borrow) = ScaledResidueRing64.ReduceBorrowedSquare(
                borrow: borrow,
                inverse: inverse,
                modulus: value,
                value: power
            );

            if (0UL != (oddPart & 1UL)) {
                result = ScaledResidueRing64.Reduce(
                    inverse: inverse,
                    left: (power + (value & borrow)),
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

    /// <summary>Returns the prefix a batch seed absorbs: the odd part over two to the step count, rounded up.</summary>
    /// <param name="oddPart">The odd part <c>d</c>, at least one.</param>
    /// <param name="steps">The walk's step count <c>k</c>, from zero through 63.</param>
    /// <returns><c>ceil(d / 2^k)</c>, at least one; it leaves <c>a 2^k - d</c> below <c>2^k</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SignedPrefix(ulong oddPart, int steps) => (((oddPart - 1UL) >>> steps) + 1UL);
    /// <summary>Returns the reduced Montgomery form of <c>2^-a</c> modulo an odd value, for a prefix <c>a</c> from 1 through 128.</summary>
    /// <param name="prefix">The prefix <c>a</c>, from 1 through 128.</param>
    /// <param name="one">The radix reduced modulo the value, <see cref="ScaledResidueRing64.RadixResidue(ulong)"/>.</param>
    /// <param name="modulus">The odd value, at least three.</param>
    /// <param name="inverse">The value's inverse modulo <c>2^64</c>.</param>
    /// <returns><c>2^64 2^-a</c> modulo <paramref name="modulus"/>, in <c>[0, modulus)</c>.</returns>
    /// <remarks>
    /// <para>
    /// One reduction by <c>2^m</c> rather than by the radix: for a reduced <c>x</c> and <c>c = -x n^-1</c> modulo
    /// <c>2^m</c>, the sum <c>x + c n</c> is a multiple of <c>2^m</c> below <c>2^m n</c>, so its quotient by
    /// <c>2^m</c> is <c>x 2^-m</c> modulo <c>n</c> and already below <c>n</c>. The Montgomery form <c>2^64 2^-a</c> is
    /// <c>one</c> times <c>2^-a</c> for <c>a</c> through 64, and <c>2^-(a - 64)</c> itself beyond, so <c>x</c> is
    /// <c>one</c> or one and <c>m</c> is <c>a</c> or <c>a - 64</c>, from 1 through 64 either way.
    /// </para>
    /// <para>
    /// The sum is below <c>2^128</c>, so it is the product's two words plus a carry into the high one; the low
    /// <c>m</c> bits are zero, so the shifted halves join with no lost bit, and at <c>m = 64</c> the low word is zero
    /// and the quotient is the high word, which the masked shift counts reach as well. The mask
    /// <c>2^64 - 1 >>> (64 - m)</c> is the one spelling that is also right at <c>m = 64</c>.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong InverseTwoPower(ulong prefix, ulong one, ulong modulus, ulong inverse) {
        // Masks rather than selects: the JIT branches on a select, and which prefixes pass 64 depends on the lanes.
        var wide = unchecked((0UL - (64UL < prefix).As<ulong>()));
        var residue = (one & ~wide) | (wide & 1UL);
        var shift = ((int)(prefix - (wide & 64UL)));
        var factor = unchecked((0UL - (residue * inverse))) & (ulong.MaxValue >>> (64 - shift));
        var high = Math.BigMul(a: factor, b: modulus, low: out var low);

        low = unchecked((low + residue));
        high += (low < residue).As<ulong>();

        return (high << (64 - shift)) | (low >>> shift);
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
    /// <c>s</c> vanishes. A symbol of <c>-1</c> makes <c>D</c> a unit modulo the value, which is the precondition
    /// <see cref="PassesLucasTraceLadder(ulong, ulong, ulong, ulong, bool)"/> decides that predicate under, for every
    /// <c>Q</c> the search selects, <c>Q = -1</c> at <c>D = 5</c> included.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool PassesStrongLucas(ulong value, ulong one, ulong inverse) {
        var magnitude = SelfridgeMagnitudeCore(value: value);

        if (0UL == magnitude) { return false; }

        // Q = (1 - D) / 4 is positive exactly when D is negative, which is when its magnitude is three modulo four.
        var isNegativeDiscriminant = (3UL == (magnitude & 3UL));

        return PassesLucasTraceLadder(
            inverse: inverse,
            isNegativeParameter: !isNegativeDiscriminant,
            one: one,
            parameterMagnitude: ((isNegativeDiscriminant
                ? (magnitude + 1UL)
                : (magnitude - 1UL)) >>> 2),
            value: value
        );
    }

    /// <summary>Runs Selfridge's Method A search on an odd value: the first discriminant of <c>5, -7, 9, -11, 13, ...</c> whose Jacobi symbol over the value is <c>-1</c>.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <returns>The signed discriminant <c>D</c>, of magnitude at least five, or zero when the search rejects the value: a perfect square, or a value sharing a factor with a candidate it does not divide outright.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is even or below three.</exception>
    /// <remarks>The search <see cref="PrimeField64.IsStrongLucasProbablePrime(ulong)"/> runs before its ladder, exposed so that the law <c>prime-field.lucas-trace-ladder-vs-recurrence</c> can pin the chosen discriminant, which the test's verdict on a prime cannot see: a prime accepts at every discriminant of symbol <c>-1</c>.</remarks>
    public static long SelfridgeDiscriminant(ulong value) {
        if ((3UL > value) || (0UL == (value & 1UL))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(value), actualValue: value, message: "The value must be odd and at least three.");
        }

        var magnitude = SelfridgeMagnitudeCore(value: value);

        return ((3UL == (magnitude & 3UL))
            ? -((long)magnitude)
            : ((long)magnitude));
    }

    /// <summary>Runs Selfridge's Method A search on an odd value and returns the magnitude of the discriminant it chooses, or zero when it rejects the value.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <returns>The magnitude of the first discriminant <c>D</c> of <c>5, -7, 9, -11, 13, ...</c> whose Jacobi symbol over the value is <c>-1</c>; the sign is negative exactly where the magnitude is three modulo four. Zero when the value is a perfect square or shares a factor with a candidate it does not divide outright, where the strong Lucas test is decided against it.</returns>
    /// <remarks>
    /// <para>
    /// Every candidate is congruent to one modulo four, so quadratic reciprocity turns <c>(D / value)</c> into
    /// <c>(value mod |D| / |D|)</c> with no sign to track, zero cases included: below <see cref="SelfridgeTableLimit"/>
    /// the symbol is one bit of a precomputed residue mask, and beyond it the shipped descent. A vanishing symbol means
    /// the value shares a factor with the candidate, which is a proper divisor unless the value divides the candidate.
    /// A perfect square never reaches a symbol of <c>-1</c>; the square test that bounds its search runs only once the
    /// tabulated candidates are exhausted, which no non-square on the random-selection path reaches in practice.
    /// </para>
    /// <para>
    /// The residues the first candidates need come from one remainder by the constant
    /// <see cref="SelfridgeSearchModulus"/>, a multiply, and constant-divisor remainders of that 14-bit value: no
    /// hardware division runs before the candidate 17, which one search in sixteen reaches. The square discriminant
    /// 9 is skipped: <c>(9 / value)</c> is <c>(3 / value)</c> squared, never <c>-1</c>, and it vanishes exactly when
    /// three divides the value, which rejects the value unless it divides nine. At each candidate below 17 the value
    /// divides the candidate exactly when it is one of the candidate's divisors above one, which is the equality test
    /// spelled in place of the remainder.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong SelfridgeMagnitudeCore(ulong value) {
        var residues = ((uint)(value % SelfridgeSearchModulus));
        var magnitude = FirstSelfridgeMagnitude;
        var symbol = SelfridgeSymbolCore(magnitude: 5UL, residue: (residues % 5U));

        if (-1 != symbol) {
            if ((0 == symbol) && (5UL != value)) { return 0UL; }

            magnitude = 7UL;
            symbol = SelfridgeSymbolCore(magnitude: 7UL, residue: (residues % 7U));
        }
        if (-1 != symbol) {
            if ((0 == symbol) && (7UL != value)) { return 0UL; }
            if ((0U == (residues % 3U)) && (3UL != value) && (9UL != value)) { return 0UL; }

            magnitude = 11UL;
            symbol = SelfridgeSymbolCore(magnitude: 11UL, residue: (residues % 11U));
        }
        if (-1 != symbol) {
            if ((0 == symbol) && (11UL != value)) { return 0UL; }

            magnitude = 13UL;
            symbol = SelfridgeSymbolCore(magnitude: 13UL, residue: (residues % 13U));
        }
        if (-1 != symbol) {
            if ((0 == symbol) && (13UL != value)) { return 0UL; }

            magnitude = 15UL;
            symbol = SelfridgeSymbolCore(magnitude: 15UL, residue: (residues % 15U));
        }
        if (-1 != symbol) {
            if ((0 == symbol) && (3UL != value) && (5UL != value) && (15UL != value)) { return 0UL; }

            magnitude = 17UL;
            symbol = SelfridgeSymbolCore(magnitude: 17UL, residue: (value % 17UL));
        }

        while (-1 != symbol) {
            if (
                (0 == symbol) &&
                (0UL != (magnitude % value))
            ) { return 0UL; }

            magnitude += 2UL;

            if (SelfridgeTableLimit > magnitude) {
                symbol = SelfridgeSymbolCore(
                    magnitude: magnitude,
                    residue: (value % magnitude)
                );
            } else {
                if ((SelfridgeTableLimit + 1UL) == magnitude) {
                    var root = value.SquareRoot();

                    if (value == (root * root)) { return 0UL; }
                }

                symbol = value.JacobiSymbol(modulus: magnitude);
            }
        }

        return magnitude;
    }

    /// <summary>Runs the strong Lucas probable-prime test with <c>P = 1</c> and a given <c>Q</c> on an odd value: the test <see cref="PrimeField64.IsStrongLucasProbablePrime(ulong)"/> runs once Selfridge's search has chosen <c>Q</c>.</summary>
    /// <param name="value">The odd value, at least three.</param>
    /// <param name="parameter">The parameter <c>Q</c>, of magnitude below <c>2^61</c>, whose discriminant <c>D = 1 - 4Q</c> is coprime to <paramref name="value"/>.</param>
    /// <returns>
    /// Whether <c>U_d</c>, or <c>V_(d * 2^r)</c> for some <c>r</c> below <c>s</c>, vanishes modulo <paramref name="value"/>,
    /// where <c>value + 1 = d * 2^s</c> with <c>d</c> odd and <c>U</c> and <c>V</c> are the Lucas sequences of <c>P = 1</c>
    /// and <c>Q</c>; always <see langword="false"/> when <paramref name="parameter"/> shares a factor with
    /// <paramref name="value"/>, zero included.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is even or below three, or the magnitude of
    /// <paramref name="parameter"/> is <c>2^61</c> or above.</exception>
    /// <exception cref="ArgumentException">The discriminant <c>1 - 4 * parameter</c> shares a factor with <paramref name="value"/>.</exception>
    /// <remarks>
    /// A parameter that shares a prime <c>p</c> with the value is rejected outright, and that is the predicate's own
    /// answer: modulo <c>p</c> the recurrence <c>X_(k+2) = X_(k+1) - Q X_k</c> becomes <c>X_(k+2) = X_(k+1)</c>, so every
    /// <c>U_k</c> and <c>V_k</c> past the first is one and none can vanish modulo the value. Selfridge's search never
    /// selects such a parameter; this shell reaches it directly. The law
    /// <c>prime-field.lucas-trace-ladder-vs-recurrence</c> compares the answer with an independent <c>U</c> and <c>V</c>
    /// recurrence for every parameter of magnitude up to forty, non-units included.
    /// </remarks>
    public static bool PassesStrongLucasRound(ulong value, long parameter) {
        if ((3UL > value) || (0UL == (value & 1UL))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(value), actualValue: value, message: "The value must be odd and at least three.");
        }
        if ((parameter <= -ParameterMagnitudeLimit) || (parameter >= ParameterMagnitudeLimit)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(parameter), actualValue: parameter, message: "The parameter's magnitude must be below 2^61.");
        }

        var magnitude = ((ulong)long.Abs(value: parameter));
        // |1 - 4Q|: four times a magnitude below 2^61 stays below 2^63.
        var discriminant = ((0L < parameter)
            ? ((magnitude << 2) - 1UL)
            : ((magnitude << 2) + 1UL));

        if (1UL != (discriminant % value).GreatestCommonDivisor(other: value)) {
            throw new ArgumentException(message: "The discriminant 1 - 4Q must be coprime to the value.", paramName: nameof(parameter));
        }
        // Zero shares every prime with the value.
        if (0L == parameter) { return false; }

        return PassesLucasTraceLadder(
            inverse: value.ModularInverse(),
            isNegativeParameter: (0L > parameter),
            one: ScaledResidueRing64.RadixResidue(modulus: value),
            parameterMagnitude: magnitude,
            value: value
        );
    }

    /// <summary>Decides the strong Lucas predicate of <c>P = 1</c> and <c>Q = ±<paramref name="parameterMagnitude"/></c> on an odd value by the norm-one trace ladder.</summary>
    /// <param name="value">The odd value, at least three, over which <c>D = 1 - 4Q</c> is a unit.</param>
    /// <param name="one">The radix reduced modulo the value.</param>
    /// <param name="inverse">The value's inverse modulo <c>2^64</c>.</param>
    /// <param name="parameterMagnitude">The magnitude of <c>Q</c>, at least one.</param>
    /// <param name="isNegativeParameter">Whether <c>Q</c> is negative.</param>
    /// <returns>Whether <c>U_d</c> or some <c>V_(d * 2^r)</c> with <c>r</c> below <c>s</c> vanishes, where <c>value + 1 = d * 2^s</c>; <see langword="false"/> when <c>Q</c> is not a unit.</returns>
    /// <remarks>
    /// <para>
    /// The ladder works in <c>A = (Z/nZ)[a] / (a^2 - a + Q)</c>, a free rank-two algebra over any odd modulus <c>n</c>,
    /// prime or not. With <c>b = 1 - a</c>, <c>a + b = 1</c> and <c>ab = Q</c>, so when <c>Q</c> is a unit both roots
    /// are, and <c>z = a / b</c> has norm one: its conjugate is <c>1 / z</c>. Its traces <c>C_k = z^k + z^-k</c> satisfy
    /// <c>C_0 = 2</c>, <c>C_1 = t = Q^-1 - 2</c>, <c>C_2k = C_k^2 - 2</c> and <c>C_(2k+1) = C_k C_(k+1) - t</c>, so a
    /// ladder over the bits of its index spends one square and one cross product per bit and carries no power of
    /// <c>Q</c> at all.
    /// </para>
    /// <para>
    /// The decision is the strong Lucas predicate's own on composite moduli as well as prime ones. From
    /// <c>a^d - b^d = (a - b) U_d</c> and <c>a^d + b^d = V_d</c>, with <c>(a - b)^2 = D</c> a unit, <c>U_d</c> vanishes
    /// exactly when <c>z^d = 1</c> and <c>V_d</c> exactly when <c>z^d = -1</c>. The trace <c>C_d</c> alone cannot tell
    /// those apart where the ring has zero divisors, but the pair at half the index can: with <c>d = 2h + 1</c>,
    /// <c>C_(h+1) - e C_h</c> is <c>z^d - e</c> times a unit for <c>e = 1</c> or <c>-1</c>, as
    /// <see cref="PassesTraceLadder{TArithmetic}(ulong, ulong, ulong, ulong, ulong, int)"/> shows, so <c>U_d</c>
    /// vanishes exactly when <c>C_h = C_(h+1)</c> and <c>V_d</c> exactly when <c>C_h = -C_(h+1)</c>. Beyond that,
    /// <c>C_k = V_2k / Q^k</c>, so <c>V_(d * 2^r)</c> vanishes for <c>r</c> of one or more exactly when
    /// <c>C_(d * 2^(r-1))</c> does: the tail is zero tests and scalar doublings. A non-unit <c>Q</c> is rejected, which
    /// is the predicate's answer there, as <see cref="PassesStrongLucasRound(ulong, long)"/> explains.
    /// </para>
    /// <para>
    /// The ladder runs in <see cref="SignedArithmetic"/> below <see cref="ScaledResidueRing64.SignedModulusCeiling"/>
    /// and in <see cref="ExactArithmetic"/> above; every step is one
    /// <see cref="IResidueArithmetic.ReduceSubtract(ulong, ulong, ulong, ulong, ulong)"/> and one
    /// <see cref="IResidueArithmetic.SquareSubtract(ulong, ulong, ulong, ulong)"/>.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool PassesLucasTraceLadder(ulong value, ulong one, ulong inverse, ulong parameterMagnitude, bool isNegativeParameter) {
        if (!TryScaledReciprocal(
            magnitude: parameterMagnitude,
            one: one,
            reciprocal: out var reciprocal,
            value: value
        )) { return false; }
        if (isNegativeParameter) {
            reciprocal = ScaledResidueRing64.Subtract(
                left: 0UL,
                modulus: value,
                right: reciprocal
            );
        }

        var two = ScaledResidueRing64.Add(
            left: one,
            modulus: value,
            right: one
        );
        var trace = ScaledResidueRing64.Subtract(
            left: reciprocal,
            modulus: value,
            right: two
        );
        // value + 1 = d * 2^s and d = 2h + 1; (value >> 1) >> (s - 1) is d - 1, which stays inside the carrier and
        // keeps every shift below 64 at value = ulong.MaxValue, where s is 64 and h is zero.
        var twoExponent = BitOperations.TrailingZeroCount(value: (value + 1UL));
        var half = (((value >>> 1) >>> (twoExponent - 1)) >>> 1);

        return ((value < ScaledResidueRing64.SignedModulusCeiling)
            ? PassesTraceLadder<SignedArithmetic>(half: half, inverse: inverse, trace: trace, two: two, twoExponent: twoExponent, value: value)
            : PassesTraceLadder<ExactArithmetic>(half: half, inverse: inverse, trace: trace, two: two, twoExponent: twoExponent, value: value));
    }

    /// <summary>Returns the Montgomery form of the reciprocal of a positive integer modulo an odd value, <c>2^64 / magnitude</c> reduced, when the integer is a unit there.</summary>
    /// <param name="magnitude">The positive integer.</param>
    /// <param name="one">The radix reduced modulo the value.</param>
    /// <param name="value">The odd value, at least three.</param>
    /// <param name="reciprocal">The reduced Montgomery form of the reciprocal, when it exists.</param>
    /// <returns>Whether <paramref name="magnitude"/> is coprime to <paramref name="value"/>.</returns>
    /// <remarks>
    /// <para>
    /// Write <c>magnitude = 2^j q</c> with <c>q</c> odd and <c>u</c> for the radix reduced. For <c>q</c> above one the
    /// digit <c>k = -u (value mod q)^-1 mod q</c> makes <c>u + k * value</c> a multiple of <c>q</c> below
    /// <c>q * value</c>, so its quotient <c>w</c> by <c>q</c> is below the value and <c>q w = u</c> modulo it: <c>w</c>
    /// is the Montgomery form of <c>q^-1</c>. The division is exact, so <c>w</c> is the numerator's low word times
    /// <c>q</c>'s inverse modulo <c>2^64</c>, and nothing wider than a word is formed. The digit exists exactly when
    /// <c>q</c> is coprime to the value. Below <see cref="ReciprocalDigitLimit"/> it is one lookup in
    /// <see cref="ReciprocalDigits"/>, which every odd part of Selfridge's tabulated parameters reaches; above it,
    /// <see cref="NumberTheoryFunctions.TryInvertOdd(ulong, ulong, out ulong)"/> derives it, the rule the table is built by.
    /// </para>
    /// <para>
    /// Each factor of two then halves <c>w</c> modulo the value, which needs no inverse: an even residue halves, and an
    /// odd one is lifted by the odd value first, spelled <c>(w &gt;&gt; 1) + (value &gt;&gt; 1) + 1</c> so the sum never
    /// leaves the carrier.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryScaledReciprocal(ulong magnitude, ulong one, ulong value, out ulong reciprocal) {
        var halvings = BitOperations.TrailingZeroCount(value: magnitude);
        var odd = (magnitude >>> halvings);
        var result = one;

        if (1UL != odd) {
            var radixResidue = (one % odd);
            var modulusResidue = (value % odd);
            ulong digit;
            ulong oddInverse;

            if (ReciprocalDigitLimit > odd) {
                var entry = ReciprocalDigits.Entries[ReciprocalDigits.Index(
                    modulusResidue: modulusResidue,
                    odd: odd,
                    radixResidue: radixResidue
                )];

                if (ReciprocalDigits.None == entry) {
                    reciprocal = 0UL;

                    return false;
                }

                digit = entry;
                oddInverse = ReciprocalDigits.Inverses[((int)(odd >>> 1))];
            } else if (TryReciprocalDigit(
                digit: out digit,
                modulusResidue: modulusResidue,
                odd: odd,
                radixResidue: radixResidue
            )) {
                oddInverse = odd.ModularInverse();
            } else {
                reciprocal = 0UL;

                return false;
            }

            result = unchecked(((result + (digit * value)) * oddInverse));
        }

        var halfUp = ((value >>> 1) + 1UL);

        for (; (0 < halvings); --halvings) {
            result = ((result >>> 1) + (halfUp & unchecked((0UL - (result & 1UL)))));
        }

        reciprocal = result;

        return true;
    }
    /// <summary>Derives the digit that lifts the radix residue to a multiple of an odd integer: <c>-radixResidue * modulusResidue^-1</c> modulo it.</summary>
    /// <param name="odd">The odd integer, at least three.</param>
    /// <param name="radixResidue">The radix reduced modulo the value, reduced again modulo <paramref name="odd"/>.</param>
    /// <param name="modulusResidue">The value reduced modulo <paramref name="odd"/>.</param>
    /// <param name="digit">The digit in <c>[0, odd)</c>, when it exists.</param>
    /// <returns>Whether <paramref name="modulusResidue"/> is a unit modulo <paramref name="odd"/>.</returns>
    private static bool TryReciprocalDigit(ulong odd, ulong radixResidue, ulong modulusResidue, out ulong digit) {
        if (!NumberTheoryFunctions.TryInvertOdd(
            inverse: out var inverse,
            modulus: odd,
            value: modulusResidue
        )) {
            digit = 0UL;

            return false;
        }

        digit = ((ulong)(((((UInt128)((odd - radixResidue) % odd)) * inverse)) % odd));

        return true;
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

    /// <summary>Returns the Jacobi symbol of a residue over a small odd discriminant magnitude from the precomputed masks.</summary>
    /// <param name="magnitude">The odd magnitude, from 5 up to 64, exclusive.</param>
    /// <param name="residue">The residue, below <paramref name="magnitude"/>.</param>
    /// <returns>The symbol <c>(residue / magnitude)</c>: <c>-1</c>, <c>0</c> or <c>1</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="magnitude"/> is below 5, 64 or above, or even, or
    /// <paramref name="residue"/> is not below <paramref name="magnitude"/>.</exception>
    /// <remarks>The law <c>prime-field.selfridge-symbols-vs-reciprocity</c> compares the table with the Jacobi symbol.</remarks>
    public static int SelfridgeSymbol(ulong magnitude, ulong residue) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: magnitude, other: FirstSelfridgeMagnitude, paramName: nameof(magnitude));
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: magnitude, other: SelfridgeTableLimit, paramName: nameof(magnitude));
        if ((magnitude & 1UL) == 0UL) { throw new ArgumentOutOfRangeException(paramName: nameof(magnitude), actualValue: magnitude, message: "The magnitude must be odd."); }
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: residue, other: magnitude, paramName: nameof(residue));
        return SelfridgeSymbolCore(magnitude: magnitude, residue: residue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int SelfridgeSymbolCore(ulong magnitude, ulong residue) {
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
    /// <summary>Walks the norm-one trace ladder to half the odd part and applies the strong Lucas acceptance tests.</summary>
    /// <typeparam name="TArithmetic">The ladder's arithmetic: <see cref="SignedArithmetic"/> only below <see cref="ScaledResidueRing64.SignedModulusCeiling"/>.</typeparam>
    /// <param name="value">The odd modulus.</param>
    /// <param name="trace">The Montgomery form of <c>t = Q^-1 - 2</c>, reduced: the trace <c>C_1</c>.</param>
    /// <param name="two">The Montgomery form of two, reduced: the trace <c>C_0</c>.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
    /// <param name="half">The index <c>h</c> with <c>d = 2h + 1</c>, where <c>d</c> is the odd part of <c>value + 1</c>.</param>
    /// <param name="twoExponent">The exponent of two in <c>value + 1</c>.</param>
    /// <returns>Whether <c>C_h</c> is <c>C_(h+1)</c> or <c>-C_(h+1)</c>, or some <c>C_(d * 2^r)</c> with <c>r</c> below the exponent less one vanishes.</returns>
    /// <remarks>
    /// <para>
    /// The pair stops at <c>(C_h, C_(h+1))</c>, one doubling short of <c>C_d</c>, because it already decides both of
    /// the first acceptance tests. The walk leaves it in either order, and every use below is symmetric in its two
    /// terms, the product included, so it is never swapped back. For <c>e = 1</c> or <c>-1</c>,
    /// <c>C_(h+1) - e C_h = z^(-h-1) (z - e) (z^d - e)</c>, and the first two factors are units: <c>z</c> has norm
    /// one, <c>z - 1</c> norm <c>2 - t = -D / Q</c> and <c>z + 1</c> norm <c>2 + t = 1 / Q</c>. So <c>z^d = 1</c>, which
    /// is <c>U_d = 0</c>, exactly when <c>C_h = C_(h+1)</c>, and <c>z^d = -1</c>, which is <c>V_d = 0</c>, exactly when
    /// <c>C_h = -C_(h+1)</c>, on every odd modulus. The tail's first zero test reads <c>C_d = C_h C_(h+1) - t</c>, one
    /// product spent only when <c>s</c> is two or more; at <c>h = 0</c>, where the pair is <c>(C_0, C_1) = (2, t)</c>
    /// and no walk runs, <c>C_d</c> is <c>t</c> itself.
    /// </para>
    /// <para>
    /// Every trace a signed ladder carries is a value
    /// <see cref="IResidueArithmetic.ReduceSubtract(ulong, ulong, ulong, ulong, ulong)"/> or
    /// <see cref="IResidueArithmetic.SquareSubtract(ulong, ulong, ulong, ulong)"/> returned, or a reduced constant, so
    /// it stays within the modulus of zero by induction. Each acceptance test reads the terms it compares through
    /// <see cref="IResidueArithmetic.Canonical(ulong, ulong)"/>, so the decision is the exact ladder's.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool PassesTraceLadder<TArithmetic>(ulong value, ulong trace, ulong two, ulong inverse, ulong half, int twoExponent) where TArithmetic : struct, IResidueArithmetic {
        var traceSubtrahend = TArithmetic.Subtrahend(
            constant: trace,
            modulus: value
        );
        var twoSubtrahend = TArithmetic.Subtrahend(
            constant: two,
            modulus: value
        );
        var low = two; // C_h, from h = 0
        var high = trace; // C_(h+1)

        if (0UL != half) {
            low = trace; // C_k, from k = 1
            high = TArithmetic.SquareSubtract(
                inverse: inverse,
                modulus: value,
                subtrahend: twoSubtrahend,
                value: trace
            ); // C_(k+1) = t^2 - 2

            WalkTraceLadder<TArithmetic>(
                crossed: ref low,
                selections: LadderSelections(index: half),
                inverse: inverse,
                squared: ref high,
                traceSubtrahend: traceSubtrahend,
                twoSubtrahend: twoSubtrahend,
                value: value
            );
        }

        var lowResidue = TArithmetic.Canonical(
            modulus: value,
            value: low
        );
        var highResidue = TArithmetic.Canonical(
            modulus: value,
            value: high
        );

        // U_d vanishes exactly when C_h = C_(h+1), and V_d exactly when C_h = -C_(h+1).
        if (
            (lowResidue == highResidue) ||
            (lowResidue == ScaledResidueRing64.Subtract(left: 0UL, modulus: value, right: highResidue))
        ) { return true; }
        if (2 > twoExponent) { return false; }

        // C_d = C_h C_(h+1) - t, which is t itself at h = 0.
        var current = ((0UL == half)
            ? trace
            : TArithmetic.ReduceSubtract(
                inverse: inverse,
                left: low,
                modulus: value,
                right: high,
                subtrahend: traceSubtrahend
            ));

        // V_(d * 2^r) vanishes exactly when C_(d * 2^(r - 1)) does, for r from one up to s, exclusive.
        for (var round = 1; (round < twoExponent); ++round) {
            if (0UL == TArithmetic.Canonical(modulus: value, value: current)) { return true; }
            if ((round + 1) < twoExponent) {
                current = TArithmetic.SquareSubtract(
                    inverse: inverse,
                    modulus: value,
                    subtrahend: twoSubtrahend,
                    value: current
                ); // C_2k = C_k^2 - 2
            }
        }

        return false;
    }
    /// <summary>Returns the word that selects a trace ladder's terms: the bits of the index's Gray code below its leading one, most significant first from bit 63, then a sentinel one.</summary>
    /// <param name="index">The index the ladder walks to, at least one and below <c>2^63</c>.</param>
    /// <returns>The selection word; the sentinel reaches bit 63 after one shift per step, with nothing left below it.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong LadderSelections(ulong index) =>
        ((((index ^ (index >>> 1)) << 1) | 1UL) << (63 - BitOperations.Log2(value: index)));
    /// <summary>Advances the pair <c>(C_k, C_(k+1))</c> from <c>k = 1</c> to <c>k = index</c> over the bits of the index below its leading one, selecting by mask rather than branching.</summary>
    /// <typeparam name="TArithmetic">The ladder's arithmetic.</typeparam>
    /// <param name="value">The odd modulus.</param>
    /// <param name="crossed">The trace <c>C_1</c>, replaced by the pair's crossed term: <c>C_index</c> when the index is odd, <c>C_(index+1)</c> when it is even.</param>
    /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
    /// <param name="twoSubtrahend">Two in the form <see cref="IResidueArithmetic.Subtrahend(ulong, ulong)"/> returns.</param>
    /// <param name="traceSubtrahend">The trace <c>t</c> in the form <see cref="IResidueArithmetic.Subtrahend(ulong, ulong)"/> returns.</param>
    /// <param name="squared">The trace <c>C_2</c>, replaced by the pair's squared term: <c>C_(index+1)</c> when the index is odd, <c>C_index</c> when it is even.</param>
    /// <param name="selections">The selection word <see cref="LadderSelections(ulong)"/> returns for the index the pair is walked to, at least one.</param>
    /// <remarks>
    /// <para>
    /// A set bit takes <c>(C_(2k+1), C_(2k+2))</c>, a clear one <c>(C_2k, C_(2k+1))</c>; each is one cross product
    /// less <c>t</c> and one square less two, and the constant rides on the product's high word, so a step's critical
    /// path is the reduction's own. The pair is carried unswapped, as <c>(squared, crossed)</c>, which is
    /// <c>(C_(k+1), C_k)</c> after a set bit and <c>(C_k, C_(k+1))</c> after a clear one. The cross product is the same
    /// either way, and the term to square is <c>crossed</c> exactly where the bit differs from the one above it, a bit
    /// of the index's Gray code <c>index ^ (index >>> 1)</c>, chosen by a mask; the leading bit is set, so the pair
    /// starts as <c>(C_2, C_1)</c>. The walk leaves it unswapped, since its caller reads the pair symmetrically. The
    /// select adds its latency to every step, which costs less than a branch on each bit, which mispredicts about half
    /// the time; that holds for the signed step at every size, the shortest included.
    /// </para>
    /// <para>
    /// The exact step calls <see cref="ScaledResidueRing64.SubtractBySelect(ulong, ulong, ulong)"/> twice, and a call
    /// clobbers every volatile register, so what the loop carries across it has to fit the eight callee-saved ones: the
    /// modulus, the inverse, both subtrahends, both references and the term in flight. One word carries the bits and
    /// the step count together, so the term stays in a register; the word's next value, off the critical path, and the
    /// square's correction factor, held across its <c>MULX</c>, are what pass through memory.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WalkTraceLadder<TArithmetic>(ulong value, ref ulong crossed, ulong inverse, ulong twoSubtrahend, ulong traceSubtrahend, ref ulong squared, ulong selections)
        where TArithmetic : struct, IResidueArithmetic {
        var squaredTerm = squared;
        var crossedTerm = crossed;
        var bits = selections;

        for (; (0UL != (bits << 1)); bits <<= 1) {
            var swap = unchecked(((ulong)(((long)bits) >> 63)));
            var term = (squaredTerm & ~swap) | (crossedTerm & swap);
            var cross = TArithmetic.ReduceSubtract(
                inverse: inverse,
                left: crossedTerm,
                modulus: value,
                right: squaredTerm,
                subtrahend: traceSubtrahend
            ); // C_(2k+1)

            squaredTerm = TArithmetic.SquareSubtract(
                inverse: inverse,
                modulus: value,
                subtrahend: twoSubtrahend,
                value: term
            ); // C_(2k+2) for a set bit, C_2k for a clear one
            crossedTerm = cross;
        }

        crossed = crossedTerm;
        squared = squaredTerm;
    }

    /// <summary>The modular arithmetic the trace ladder runs in: folded after every operation, or carried signed below <see cref="ScaledResidueRing64.SignedModulusCeiling"/>.</summary>
    /// <remarks><see cref="SignedArithmetic"/> keeps every trace within the modulus of zero; <see cref="ExactArithmetic"/> is admissible everywhere.</remarks>
    internal interface IResidueArithmetic {
        /// <summary>Returns the unique residue a carried value stands for.</summary>
        /// <param name="value">The carried value, under the arithmetic's operand bound.</param>
        /// <param name="modulus">The odd modulus.</param>
        /// <returns>The residue in <c>[0, modulus)</c>.</returns>
        static abstract ulong Canonical(ulong value, ulong modulus);

        /// <summary>Multiplies two carried residues, reduces the product and subtracts a constant inside the reduction.</summary>
        /// <param name="left">The first factor.</param>
        /// <param name="right">The second factor.</param>
        /// <param name="subtrahend">The constant, in the form <see cref="Subtrahend(ulong, ulong)"/> returns.</param>
        /// <param name="modulus">The odd modulus.</param>
        /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
        /// <returns>The product over the radix less the constant, under the arithmetic's operand bound.</returns>
        /// <remarks>
        /// The constant is applied to the product's high half, which is ready before the critical multiply of the
        /// reduction, so it adds nothing to a chain's critical path.
        /// </remarks>
        static abstract ulong ReduceSubtract(ulong left, ulong right, ulong subtrahend, ulong modulus, ulong inverse);
        /// <summary>Squares a carried residue, reduces the square and subtracts a constant inside the reduction: <see cref="ReduceSubtract(ulong, ulong, ulong, ulong, ulong)"/> of a value with itself.</summary>
        /// <param name="value">The value.</param>
        /// <param name="subtrahend">The constant, in the form <see cref="Subtrahend(ulong, ulong)"/> returns.</param>
        /// <param name="modulus">The odd modulus.</param>
        /// <param name="inverse">The modulus's inverse modulo <c>2^64</c>.</param>
        /// <returns>The square over the radix less the constant, under the arithmetic's operand bound.</returns>
        static abstract ulong SquareSubtract(ulong value, ulong subtrahend, ulong modulus, ulong inverse);
        /// <summary>Returns the form in which <see cref="ReduceSubtract(ulong, ulong, ulong, ulong, ulong)"/> takes a reduced constant to subtract.</summary>
        /// <param name="constant">The constant, reduced.</param>
        /// <param name="modulus">The odd modulus.</param>
        /// <returns>The constant itself, or its representative of least magnitude in the signed form.</returns>
        static abstract ulong Subtrahend(ulong constant, ulong modulus);
    }
    /// <summary>The arithmetic that folds after every operation, so every value is the residue itself.</summary>
    internal readonly struct ExactArithmetic : IResidueArithmetic {
        /// <remarks>
        /// <see cref="ScaledResidueRing64.Reduce(ulong, ulong, ulong, ulong)"/>'s subtractive form with the constant taken
        /// off the high half first: two reduced operands keep both high halves below the modulus, so the high half less
        /// the constant, folded, and then less the correction, folded, is the reduced result, and the first fold runs
        /// while the correction's multiply does. One widening multiply gives both halves; the factor's truncated
        /// multiply follows its low half on the critical path while the first fold runs on its high half. The second fold
        /// is <see cref="ScaledResidueRing64.SubtractBySelect(ulong, ulong, ulong)"/>, a call, so that it is a conditional
        /// move on the ladder's critical path; the first stays the inlined mask, off it.
        /// </remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReduceSubtract(ulong left, ulong right, ulong subtrahend, ulong modulus, ulong inverse) {
            var high = ScaledResidueRing64.Subtract(
                left: Math.BigMul(a: left, b: right, low: out var low),
                modulus: modulus,
                right: subtrahend
            );
            var factor = unchecked((low * inverse));

            return ScaledResidueRing64.SubtractBySelect(
                left: high,
                modulus: modulus,
                right: ScaledResidueRing64.MultiplyHigh(left: factor, right: modulus)
            );
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong SquareSubtract(ulong value, ulong subtrahend, ulong modulus, ulong inverse) =>
            ReduceSubtract(inverse: inverse, left: value, modulus: modulus, right: value, subtrahend: subtrahend);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Subtrahend(ulong constant, ulong modulus) => constant;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Canonical(ulong value, ulong modulus) => value;
    }
    /// <summary>The arithmetic below <see cref="ScaledResidueRing64.SignedModulusCeiling"/> that carries signed representatives within the modulus of zero and reads each correction factor unsigned, so that no product is folded after its reduction.</summary>
    /// <remarks>
    /// Each operation is a <see cref="ScaledResidueRing64"/> kernel on the word read signed, whose documentation carries
    /// the interval proof: a square doubled or not, <see cref="ScaledResidueRing64.ReduceSignedSquare(long, int, ulong, ulong)"/>,
    /// and a product less a centered constant, <see cref="ScaledResidueRing64.ReduceSigned(long, long, long, ulong, ulong)"/>,
    /// whose one fold runs on the high word off the critical path. A reduced residue is already inside the bound.
    /// </remarks>
    internal readonly struct SignedArithmetic : IResidueArithmetic {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong SquareDouble(ulong value, ulong doubling, ulong modulus, ulong inverse) =>
            unchecked(((ulong)ScaledResidueRing64.ReduceSignedSquare(doubling: ((int)doubling), inverse: inverse, modulus: modulus, value: ((long)value))));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong ReduceSubtract(ulong left, ulong right, ulong subtrahend, ulong modulus, ulong inverse) =>
            unchecked(((ulong)ScaledResidueRing64.ReduceSigned(inverse: inverse, left: ((long)left), modulus: modulus, right: ((long)right), subtrahend: ((long)subtrahend))));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong SquareSubtract(ulong value, ulong subtrahend, ulong modulus, ulong inverse) =>
            unchecked(((ulong)ScaledResidueRing64.ReduceSignedSquareSubtract(inverse: inverse, modulus: modulus, subtrahend: ((long)subtrahend), value: ((long)value))));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Subtrahend(ulong constant, ulong modulus) =>
            unchecked(((ulong)ScaledResidueRing64.Center(modulus: modulus, value: constant)));
        /// <remarks>A representative in <c>(-n, n)</c> plus the modulus under its sign mask.</remarks>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Canonical(ulong value, ulong modulus) =>
            unchecked((value + (modulus & ((ulong)(((long)value) >> 63)))));
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

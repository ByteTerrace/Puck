using System.Numerics;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

/// <summary>Supplies the final exact primality decision for filtered unsigned-64-bit selection candidates.</summary>
/// <remarks>The candidate is greater than <see cref="uint.MaxValue"/> and has no prime factor through 163.
/// Implementations must return its exact primality. A false positive can return a composite; a false negative
/// changes the selected distribution. The sampler supplies filtering, uniform candidate mapping and draw budgets.</remarks>
internal interface IPrimeCandidateDecision {
    /// <summary>Decides primality of a filtered candidate above the unsigned-32-bit domain.</summary>
    /// <param name="value">The candidate, with no prime divisor through 163.</param>
    /// <returns>Whether <paramref name="value"/> is prime.</returns>
    static abstract bool IsPrimeCandidate(ulong value);
}

public static partial class PrimeExploration {
    /// <summary>The largest upper bound the shared base-prime table selects from directly.</summary>
    private const ulong TableSelectionCeiling = 65535UL;

    /// <summary>Attempts to select a uniformly distributed prime from an inclusive unsigned-64-bit interval.</summary>
    /// <typeparam name="TGenerator">The caller-owned draw generator, specialized without boxing.</typeparam>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound, at least <paramref name="low"/>.</param>
    /// <param name="generator">The generator state, advanced by reference.</param>
    /// <param name="prime">The selected prime on success; zero on failure.</param>
    /// <param name="maxAttempts">The positive budget of raw 64-bit draws, each consuming two 32-bit draws.</param>
    /// <returns>Whether a prime was selected before the draw budget was exhausted. Failure does not prove the interval contains no primes.</returns>
    /// <remarks>
    /// <para>
    /// With independent uniform input words, every interval prime has equal probability conditional on success.
    /// Small intervals ending below 65,536 sample the shared prime table directly. Other intervals sample the
    /// wheel candidates together with two, three and five, rejecting composites rather than walking to a neighbor.
    /// Range-reduction rejections consume the same finite budget as composite candidates. Empty candidate sets
    /// consume no draws. A singleton candidate is decided once without drawing. The uint primality test handles
    /// small words; larger candidates use the Baillie–PSW test, exhaustively counterexample-free below
    /// 2^64 as described by <see cref="PrimeField64.IsBaillieProbablePrime(ulong)"/>.
    /// The generator determines reproducibility and random quality; this method supplies no entropy.
    /// </para>
    /// <para>
    /// When <typeparamref name="TGenerator"/> holds no references, the generator is treated as a pure value: its
    /// draws are a function of its fields alone, so a copy taken after a draw restores the stream exactly. Wide
    /// candidates are then decided speculatively, three filter survivors at a time, with their base-two rounds
    /// interleaved; the generator is restored to the copy taken after the winning draw. The selected prime, the
    /// final generator state and success or failure are those of deciding each candidate in draw order, and no
    /// draw is made past the budget. A generator whose draws depend on anything outside its own fields must hold a
    /// reference, which selects the sequential order instead.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The interval is reversed or the attempt budget is not positive.</exception>
    public static bool TryRandomPrime<TGenerator>(ulong low, ulong high, ref TGenerator generator, out ulong prime, int maxAttempts = 256)
        where TGenerator : struct, IDrawGenerator =>
        TryRandomPrime<TGenerator, BaillieSelectionDecision>(generator: ref generator, high: high, low: low, maxAttempts: maxAttempts, prime: out prime);

    /// <summary>Attempts uniform prime selection with a supplied exact decision for filtered wide candidates.</summary>
    /// <typeparam name="TGenerator">The caller-owned draw generator, specialized without boxing.</typeparam>
    /// <typeparam name="TDecision">The exact wide-candidate decision; see <see cref="IPrimeCandidateDecision"/>.</typeparam>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound, at least <paramref name="low"/>.</param>
    /// <param name="generator">The generator state, advanced by reference.</param>
    /// <param name="prime">The selected prime on success; zero on failure.</param>
    /// <param name="maxAttempts">The positive budget of raw 64-bit words, each consuming two 32-bit draws.</param>
    /// <returns>Whether a prime was selected before exhausting the draw budget.</returns>
    /// <remarks>Uses exactly the default sampler's table, wheel, range reduction and filters through 163.
    /// The custom decision runs only above <see cref="uint.MaxValue"/> after those filters, once per candidate in
    /// draw order until one is accepted; all smaller candidates use the narrow production decision. Uniform-prime
    /// semantics require an exact supplied decision.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The interval is reversed or the attempt budget is not positive.</exception>
    internal static bool TryRandomPrime<TGenerator, TDecision>(ulong low, ulong high, ref TGenerator generator, out ulong prime, int maxAttempts = 256)
        where TGenerator : struct, IDrawGenerator where TDecision : struct, IPrimeCandidateDecision {
        ArgumentOutOfRangeException.ThrowIfLessThan(high, low);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        prime = 0;
        if (high < 2) { return false; }
        if (high <= TableSelectionCeiling) {
            return TryTableRandomPrime(
                generator: ref generator,
                high: high,
                low: low,
                maxAttempts: maxAttempts,
                prime: out prime
            );
        }

        return TryWheelRandomPrime<TGenerator, TDecision>(
            generator: ref generator,
            high: high,
            low: low,
            maxAttempts: maxAttempts,
            prime: out prime
        );
    }

    /// <summary>Gets two, three and five, the primes the thirty-wheel's units omit.</summary>
    private static ReadOnlySpan<byte> ExceptionalPrimes => [2, 3, 5];

    /// <summary>Selects from the exceptional primes and wheel values of an interval ending above <see cref="TableSelectionCeiling"/>.</summary>
    /// <typeparam name="TGenerator">The caller-owned draw generator.</typeparam>
    /// <typeparam name="TDecision">The wide-candidate decision.</typeparam>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound, above <see cref="TableSelectionCeiling"/> and at least <paramref name="low"/>.</param>
    /// <param name="generator">The generator state, advanced by reference.</param>
    /// <param name="prime">The selected prime on success; zero on failure.</param>
    /// <param name="maxAttempts">The positive raw-draw budget.</param>
    /// <returns>Whether a prime was selected.</returns>
    /// <remarks>Every request above the table runs its wheel mapping here, so the method is compiled fully optimized on
    /// first use rather than tiered: tiering would run that per-request setup unoptimized until a promotion that
    /// single-processor hosts delay for a second or more.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryWheelRandomPrime<TGenerator, TDecision>(ulong low, ulong high, ref TGenerator generator, out ulong prime, int maxAttempts)
        where TGenerator : struct, IDrawGenerator where TDecision : struct, IPrimeCandidateDecision {
        prime = 0;

        var exceptionalStart = ((low <= 2) ? 0 : ((low <= 3) ? 1 : ((low <= 5) ? 2 : 3)));
        var sampler = new WheelSampler(
            exceptionalCount: ((ulong)(ExceptionalPrimes.Length - exceptionalStart)),
            exceptionalStart: exceptionalStart,
            first: WheelPrefix(high: (Math.Max(val1: low, val2: 7UL) - 1)),
            high: high
        );

        if (sampler.Count == 0) { return false; }
        if (sampler.Count == 1) {
            var value = sampler.Value(index: 0);

            if (!DecideSelectionCandidate<TDecision>(value: value)) { return false; }
            prime = value;
            return true;
        }
        if (high <= uint.MaxValue) {
            return TryNarrowWheelPrime(
                attempts: maxAttempts,
                generator: ref generator,
                prime: out prime,
                sampler: in sampler
            );
        }
        if ((typeof(TDecision) == typeof(BaillieSelectionDecision)) && !RuntimeHelpers.IsReferenceOrContainsReferences<TGenerator>()) {
            return TrySpeculativeWheelPrime(
                attempts: maxAttempts,
                generator: ref generator,
                prime: out prime,
                sampler: in sampler
            );
        }

        var candidates = new BoundedRandomSampling.Range64(exclusiveHigh: sampler.Count);

        while (candidates.TrySample(attempts: ref maxAttempts, generator: ref generator, value: out var index)) {
            var value = sampler.Value(index: index);

            if (DecideSelectionCandidate<TDecision>(value: value)) {
                prime = value;
                return true;
            }
        }
        return false;
    }
    /// <summary>Decides one selection candidate: the narrow exact decision at or below <see cref="uint.MaxValue"/>, the filter and <typeparamref name="TDecision"/> above.</summary>
    /// <typeparam name="TDecision">The wide-candidate decision.</typeparam>
    /// <param name="value">The candidate: an exceptional prime or a wheel value.</param>
    /// <returns>Whether the candidate is selected.</returns>
    private static bool DecideSelectionCandidate<TDecision>(ulong value) where TDecision : struct, IPrimeCandidateDecision =>
        ((value <= uint.MaxValue) ? ((uint)value).IsPrime()
            : (PrimeKernels.PassesSelectionPrimeFilter(value: value) && TDecision.IsPrimeCandidate(value: value)));
    /// <summary>Selects from the shared base-prime table, for an interval ending at or below <see cref="TableSelectionCeiling"/>.</summary>
    /// <typeparam name="TGenerator">The caller-owned draw generator.</typeparam>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound, from two through <see cref="TableSelectionCeiling"/>.</param>
    /// <param name="generator">The generator state, advanced by reference.</param>
    /// <param name="prime">The selected prime on success; zero on failure.</param>
    /// <param name="maxAttempts">The positive raw-draw budget.</param>
    /// <returns>Whether a prime was selected.</returns>
    /// <remarks>The interval's first and last table indices are odd-prime ranks read off a bitmap with one population count each.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryTableRandomPrime<TGenerator>(ulong low, ulong high, ref TGenerator generator, out ulong prime, int maxAttempts)
        where TGenerator : struct, IDrawGenerator {
        var bases = PrimeKernels.BasePrimes;
        var start = PrimeKernels.CountOddPrimesBelow(value: ((uint)Math.Max(val1: low, val2: 3UL)));
        var end = PrimeKernels.CountOddPrimesBelow(value: (((uint)high) + 1U));
        var includesTwo = (low <= 2);
        var count = ((ulong)((end - start) + (includesTwo ? 1 : 0)));

        prime = 0;
        if (count == 0) { return false; }
        if (count == 1) { prime = (includesTwo ? 2UL : bases[start]); return true; }
        var range = new BoundedRandomSampling.Range64(exclusiveHigh: count);

        if (!range.TrySample(attempts: ref maxAttempts, generator: ref generator, value: out var index)) { return false; }
        prime = ((includesTwo && (index == 0)) ? 2UL : bases[((start + ((int)index)) - (includesTwo ? 1 : 0))]);
        return true;
    }
    /// <summary>Selects from the wheel candidates of an interval ending at or below <see cref="uint.MaxValue"/>, deciding each draw as it arrives.</summary>
    /// <typeparam name="TGenerator">The caller-owned draw generator.</typeparam>
    /// <param name="sampler">The interval's wheel mapping, every value of which is a narrow word.</param>
    /// <param name="prime">The selected prime on success; zero on failure.</param>
    /// <param name="generator">The generator state, advanced by reference.</param>
    /// <param name="attempts">The positive raw-draw budget.</param>
    /// <returns>Whether a prime was selected.</returns>
    /// <remarks>Every candidate takes the narrow exact decision, which no wide decision replaces, so the sequential order is
    /// the only order and no survivor waits for a batch. The generator lives in a local for the whole request and is
    /// written back on every exit.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryNarrowWheelPrime<TGenerator>(in WheelSampler sampler, out ulong prime, ref TGenerator generator, int attempts)
        where TGenerator : struct, IDrawGenerator {
        var candidates = new BoundedRandomSampling.Range64(exclusiveHigh: sampler.Count);
        var state = generator;

        while (candidates.TrySample(attempts: ref attempts, generator: ref state, value: out var index)) {
            var value = sampler.Value(index: index);

            if (((uint)value).IsPrime()) {
                generator = state;
                prime = value;
                return true;
            }
        }

        generator = state;
        prime = 0;
        return false;
    }
    /// <summary>Selects from the wheel candidates of a pure-value generator, deciding wide filter survivors in interleaved batches.</summary>
    /// <typeparam name="TGenerator">The generator, holding no references, so a copy of it is a complete snapshot.</typeparam>
    /// <param name="sampler">The interval's wheel mapping.</param>
    /// <param name="prime">The selected prime on success; zero on failure.</param>
    /// <param name="generator">The generator state, advanced by reference.</param>
    /// <param name="attempts">The positive raw-draw budget.</param>
    /// <returns>Whether a prime was selected.</returns>
    /// <remarks>
    /// <para>
    /// Draws continue past an undecided survivor until <see cref="PrimeKernels.BaseTwoBatchWidth"/> survivors are
    /// pending, a narrow or exceptional candidate arrives, or the budget runs out; each survivor keeps a copy of the
    /// generator taken right after its draw. The pending survivors are then decided in draw order — a full batch with
    /// one interleaved base-two kernel, a partial one survivor by survivor — and the first prime wins with its copy
    /// restored, discarding every later draw. Only when none is prime is the narrow candidate decided, with the
    /// generator where its own draw left it, and only then does the budget's exhaustion end the request. That is
    /// exactly the sequential order's outcome, prime, final state and failure alike.
    /// </para>
    /// <para>
    /// The generator lives in a local for the whole request, so its state stays in registers between draws instead of
    /// round-tripping through the caller's storage, and is written back on every exit.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TrySpeculativeWheelPrime<TGenerator>(in WheelSampler sampler, out ulong prime, ref TGenerator generator, int attempts)
        where TGenerator : struct, IDrawGenerator {
        var candidates = new BoundedRandomSampling.Range64(exclusiveHigh: sampler.Count);
        var state = generator;
        var snapshots = new SpeculativeSnapshots<TGenerator>();
        Span<ulong> pending = stackalloc ulong[PrimeKernels.BaseTwoBatchWidth];

        while (true) {
            var count = 0;
            var narrow = 0UL;
            var exhausted = false;

            while (count < PrimeKernels.BaseTwoBatchWidth) {
                if (!candidates.TrySample(attempts: ref attempts, generator: ref state, value: out var index)) {
                    exhausted = true;
                    break;
                }

                var value = sampler.Value(index: index);

                if (value <= uint.MaxValue) {
                    narrow = value;
                    break;
                }
                if (!PrimeKernels.PassesSelectionPrimeFilter(value: value)) { continue; }

                pending[count] = value;
                snapshots[count] = state;
                ++count;
            }

            var passes = ((PrimeKernels.BaseTwoBatchWidth == count)
                ? PrimeKernels.PassesBaseTwoBatch(first: pending[0], second: pending[1], third: pending[2])
                : -1);

            for (var i = 0; (i < count); ++i) {
                if (
                    (0 != ((passes >>> i) & 1)) &&
                    ((PrimeKernels.BaseTwoBatchWidth == count)
                        ? PrimeField64.IsStrongLucasProbablePrime(value: pending[i])
                        : PrimeKernels.IsBaillieOddWord(value: pending[i]))
                ) {
                    generator = snapshots[i];
                    prime = pending[i];
                    return true;
                }
            }

            if ((0UL != narrow) && ((uint)narrow).IsPrime()) {
                generator = state;
                prime = narrow;
                return true;
            }
            if (exhausted) {
                generator = state;
                prime = 0;
                return false;
            }
        }
    }
    /// <summary>Counts the wheel candidates at or below a value: the units of thirty, one of which is the nonprime one.</summary>
    /// <param name="high">The inclusive bound.</param>
    /// <returns>The number of values at or below <paramref name="high"/> that are coprime to thirty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong WheelPrefix(ulong high) =>
        (((high / PrimeWheel30.Modulus) * PrimeWheel30.ChannelCount) + WheelUnitsThrough.Counts[((int)(high % PrimeWheel30.Modulus))]);

    /// <summary>The decision the default selector specializes: the shared exact word decision, which recognizes the interleaved path.</summary>
    private readonly struct BaillieSelectionDecision : IPrimeCandidateDecision {
        public static bool IsPrimeCandidate(ulong value) => PrimeKernels.IsPrimeCandidateWord(value: value);
    }
    /// <summary>One generator copy per pending survivor of an interleaved batch.</summary>
    /// <typeparam name="TGenerator">The generator type.</typeparam>
    [InlineArray(PrimeKernels.BaseTwoBatchWidth)]
    private struct SpeculativeSnapshots<TGenerator> {
        private TGenerator m_element;
    }
    /// <summary>Maps a uniform candidate index onto an interval's exceptional primes and wheel values.</summary>
    /// <param name="high">The inclusive upper bound.</param>
    /// <param name="first">The wheel-prefix count below the interval's first wheel value.</param>
    /// <param name="exceptionalStart">The index in <see cref="ExceptionalPrimes"/> of the interval's first exceptional prime.</param>
    /// <param name="exceptionalCount">The number of exceptional primes the interval holds.</param>
    private readonly struct WheelSampler(ulong high, ulong first, int exceptionalStart, ulong exceptionalCount) {
        private readonly ulong m_exceptionalCount = exceptionalCount;
        private readonly int m_exceptionalStart = exceptionalStart;
        private readonly ulong m_first = first;

        /// <summary>Gets the number of candidates: the exceptional primes followed by the wheel values.</summary>
        public ulong Count { get; } = ((WheelPrefix(high: high) - first) + exceptionalCount);

        /// <summary>Returns the candidate at an index.</summary>
        /// <param name="index">The index, below <see cref="Count"/>.</param>
        /// <returns>The exceptional prime or the wheel value at <paramref name="index"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong Value(ulong index) {
            if (index < m_exceptionalCount) { return ExceptionalPrimes[(m_exceptionalStart + ((int)index))]; }

            var ordinal = ((m_first + index) - m_exceptionalCount);

            return ((PrimeWheel30.Modulus * (ordinal >>> 3)) + PrimeWheel30.NumericResidues[((int)(ordinal & (PrimeWheel30.ChannelCount - 1)))]);
        }
    }

    /// <summary>Holds, for each residue modulo thirty, the number of wheel units at or below it.</summary>
    internal static class WheelUnitsThrough {
        internal static readonly byte[] Counts = CreateCounts();

        private static byte[] CreateCounts() {
            var counts = new byte[PrimeWheel30.Modulus];

            for (var residue = 0; (residue < counts.Length); ++residue) { counts[residue] = ((byte)BitOperations.PopCount(value: ((uint)PrimeWheel30.PrefixMask(remainder: residue)))); }

            return counts;
        }
    }
}

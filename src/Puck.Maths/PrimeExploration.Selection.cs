namespace Puck.Maths;

/// <summary>Supplies the final exact primality decision for filtered unsigned-64-bit selection candidates.</summary>
/// <remarks>The candidate is greater than <see cref="uint.MaxValue"/> and has no prime factor through 163.
/// Implementations must return its exact primality. A false positive can return a composite; a false negative
/// changes the selected distribution. The sampler supplies filtering, uniform candidate mapping and draw budgets.</remarks>
public interface IPrimeCandidateDecision {
    /// <summary>Decides primality of a filtered candidate above the unsigned-32-bit domain.</summary>
    /// <param name="value">The candidate, with no prime divisor through 163.</param>
    /// <returns>Whether <paramref name="value"/> is prime.</returns>
    static abstract bool IsPrimeCandidate(ulong value);
}
public static partial class PrimeExploration {
    /// <summary>Attempts to select a uniformly distributed prime from an inclusive unsigned-64-bit interval.</summary>
    /// <typeparam name="TGenerator">The caller-owned draw generator, specialized without boxing.</typeparam>
    /// <param name="low">The inclusive lower bound.</param>
    /// <param name="high">The inclusive upper bound, at least <paramref name="low"/>.</param>
    /// <param name="generator">The generator state, advanced by reference.</param>
    /// <param name="prime">The selected prime on success; zero on failure.</param>
    /// <param name="maxAttempts">The positive budget of raw 64-bit draws, each consuming two 32-bit draws.</param>
    /// <returns>Whether a prime was selected before the draw budget was exhausted. Failure does not prove the interval contains no primes.</returns>
    /// <remarks>
    /// With independent uniform input words, every interval prime has equal probability conditional on success.
    /// Small intervals ending below 65,536 sample the shared prime table directly. Other intervals sample the
    /// wheel candidates together with two, three and five, rejecting composites rather than walking to a neighbor.
    /// Range-reduction rejections consume the same finite budget as composite candidates. Empty candidate sets
    /// consume no draws. A singleton candidate is decided once without drawing. The uint primality test handles
    /// small words; larger candidates use the existing Baillie–PSW test, exhaustively counterexample-free below
    /// 2^64 as described by <see cref="PrimeField64.IsBaillieProbablePrime(ulong)"/>.
    /// The generator determines reproducibility and random quality; this method supplies no entropy.
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
    /// The custom decision runs only above <see cref="uint.MaxValue"/> after those filters; all smaller
    /// candidates use the narrow production decision. Uniform-prime semantics require an exact supplied decision.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The interval is reversed or the attempt budget is not positive.</exception>
    public static bool TryRandomPrime<TGenerator, TDecision>(ulong low, ulong high, ref TGenerator generator, out ulong prime, int maxAttempts = 256)
        where TGenerator : struct, IDrawGenerator where TDecision : struct, IPrimeCandidateDecision {
        ArgumentOutOfRangeException.ThrowIfLessThan(high, low);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        prime = 0;
        if (high < 2) { return false; }

        if (high <= 65535) {
            var bases = PrimeKernels.BasePrimes;
            var start = bases.BinarySearch(comparable: ((uint)Math.Max(val1: low, val2: 3UL)));
            var end = bases.BinarySearch(comparable: ((uint)high));

            start = ((start < 0) ? ~start : start);
            end = ((end < 0) ? ~end : (end + 1));
            var includesTwo = (low <= 2);
            var count = ((ulong)((end - start) + (includesTwo ? 1 : 0)));

            if (count == 0) { return false; }
            if (count == 1) { prime = (includesTwo ? 2UL : bases[start]); return true; }
            var range = new BoundedRandomSampling.Range64(exclusiveHigh: count);

            if (!range.TrySample(attempts: ref maxAttempts, generator: ref generator, value: out var index)) { return false; }
            prime = ((includesTwo && (index == 0)) ? 2UL : bases[((start + ((int)index)) - (includesTwo ? 1 : 0))]);
            return true;
        }

        var exceptionalStart = ((low <= 2) ? 0 : ((low <= 3) ? 1 : ((low <= 5) ? 2 : 3)));
        var exceptionalCount = (3 - exceptionalStart);
        var first = WheelPrefix(high: (Math.Max(val1: low, val2: 7UL) - 1));
        var countCandidates = ((WheelPrefix(high: high) - first) + ((ulong)exceptionalCount));

        if (countCandidates == 0) { return false; }
        if (countCandidates == 1) {
            var value = ((30 * (first >> 3)) + PrimeWheel30.NumericResidues[((int)(first & 7))]);

            if (!DecideSelectionCandidate<TDecision>(value: value)) { return false; }
            prime = value;
            return true;
        }
        var candidates = new BoundedRandomSampling.Range64(exclusiveHigh: countCandidates);

        while (candidates.TrySample(attempts: ref maxAttempts, generator: ref generator, value: out var index)) {
            if (index < ((ulong)exceptionalCount)) {
                ReadOnlySpan<byte> exceptions = [2, 3, 5];

                prime = exceptions[(exceptionalStart + ((int)index))];
                return true;
            }
            var ordinal = ((first + index) - ((ulong)exceptionalCount));
            var value = ((30 * (ordinal >> 3)) + PrimeWheel30.NumericResidues[((int)(ordinal & 7))]);

            if (DecideSelectionCandidate<TDecision>(value: value)) {
                prime = value;
                return true;
            }
        }
        return false;
    }

    private static bool DecideSelectionCandidate<TDecision>(ulong value) where TDecision : struct, IPrimeCandidateDecision =>
        ((value <= uint.MaxValue) ? ((uint)value).IsPrime()
            : (PrimeKernels.PassesSelectionPrimeFilter(value: value) && TDecision.IsPrimeCandidate(value: value)));

    private readonly struct BaillieSelectionDecision : IPrimeCandidateDecision {
        public static bool IsPrimeCandidate(ulong value) => PrimeKernels.IsPrimeCandidateWord(value: value);
    }

    private static ulong WheelPrefix(ulong high) {
        var count = ((high / 30) * 8);
        var residue = (high % 30);

        foreach (var candidate in PrimeWheel30.NumericResidues) {
            if (candidate > residue) { break; }
            ++count;
        }
        return count;
    }
}

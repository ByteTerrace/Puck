using System.Buffers;
using System.Runtime.CompilerServices;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    private const ulong PrimeCount64 = 425_656_284_035_217_743;
    private const uint CountingRootLimit = ((1U << 22) - 1U);
    private const ulong CountingValueLimit = (((ulong)CountingRootLimit) * CountingRootLimit);
    private const ulong SelectionWindow64 = 1_048_576;

    // pi(2^k), k = 32..64, from Tomas Oliveira e Silva's independently computed table:
    // https://sweet.ua.pt/tos/primes.html (the pi(2^k) dataset). The last bound is represented by ulong.MaxValue;
    // 2^64 is composite, so replacing it by 2^64-1 leaves its prime count unchanged.
    private static ReadOnlySpan<ulong> PrimeCountPowers64 => [
        203_280_221, 393_615_806, 762_939_111, 1_480_206_279, 2_874_398_515,
        5_586_502_348, 10_866_266_172, 21_151_907_950, 41_203_088_796, 80_316_571_436,
        156_661_034_233, 305_761_713_237, 597_116_381_732, 1_166_746_786_182,
        2_280_998_753_949, 4_461_632_979_717, 8_731_188_863_470, 17_094_432_576_778,
        33_483_379_603_407, 65_612_899_915_304, 128_625_503_610_475, 252_252_704_148_404,
        494_890_204_904_784, 971_269_945_245_201, 1_906_879_381_028_850,
        3_745_011_184_713_964, 7_357_400_267_843_990, 14_458_792_895_301_660,
        28_423_094_496_953_330, 55_890_484_045_084_135, 109_932_807_585_469_973,
        216_289_611_853_439_384, PrimeCount64,
    ];

    /// <summary>Returns the prime at a zero-based unsigned sixty-four-bit index.</summary>
    /// <param name="value">The zero-based index: zero selects two.</param>
    /// <param name="cancellationToken">Cancels a potentially long exact count or interval walk.</param>
    /// <returns>The selected prime, or zero when the index is at least 425,656,284,035,217,743,
    /// the number of primes representable by <see cref="ulong"/>.</returns>
    /// <remarks>
    /// <para>Ranks whose result fits in <see cref="uint"/> use the existing narrow implementation. Larger
    /// practical ranks start from an asymptotic estimate, align its rank with exact combinatorial counting,
    /// and sieve the remaining interval. Floating-point estimates affect work only, never the selected prime.</para>
    /// <para>Combinatorial counting rents at most approximately 52 MiB of array payload. Above its bounded
    /// working range, selection walks from the nearest published power-of-two prime-count checkpoint with
    /// bounded presieving. Ranks close to a checkpoint, including the final ranks of the ulong domain, are
    /// inexpensive; ranks far from every checkpoint may require impractical time. The full domain is exact,
    /// but this API does not promise fast arbitrary sixty-four-bit rank selection.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static ulong NthPrime(this ulong value, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        if (value >= PrimeCount64) { return 0; }
        if (value <= 203_280_220) { return ((uint)value).NthPrime(); }

        var ordinal = (value + 1);
        var checkpoint = NearestRankCheckpoint64(ordinal: ordinal);
        var anchorCount = PrimeCountPowers64[checkpoint];
        var distance = ((anchorCount >= ordinal) ? (anchorCount - ordinal) : (ordinal - anchorCount));
        var anchor = CheckpointBound64(index: checkpoint);

        if (distance > 4096) {
            var logarithm = Math.Log(d: ordinal);
            var logLogarithm = Math.Log(d: logarithm);
            var estimate = (ordinal * ((((logarithm + logLogarithm) - 1.0d) + ((logLogarithm - 2.0d) / logarithm))
                - ((((logLogarithm * logLogarithm) - (6.0d * logLogarithm)) + 11.0d) / ((2.0d * logarithm) * logarithm))));

            if (estimate <= CountingValueLimit) {
                anchor = ((ulong)estimate);
                anchorCount = anchor.PrimeCountingFunction(cancellationToken: cancellationToken);
            }
        }
        return ((anchorCount < ordinal)
            ? WalkRank64(cancellationToken: cancellationToken, forward: true, remaining: (ordinal - anchorCount), start: (anchor + 1))
            : WalkRank64(cancellationToken: cancellationToken, forward: false, remaining: ((anchorCount - ordinal) + 1), start: anchor));
    }
    /// <summary>Returns the number of primes less than or equal to an unsigned sixty-four-bit bound.</summary>
    /// <param name="value">The inclusive upper bound.</param>
    /// <param name="cancellationToken">Cancels a potentially long exact count.</param>
    /// <returns>The exact prime count through <paramref name="value"/>.</returns>
    /// <remarks>
    /// Bounds through <see cref="uint.MaxValue"/> retain the narrow combinatorial implementation. Larger
    /// bounds whose square root is at most 4,194,303 use its widened quotient-counting recurrence, with at most
    /// approximately 52 MiB of pooled array payload. Larger bounds count a difference from the nearest published
    /// power-of-two checkpoint using bounded presieving; that path has bounded memory but can take impractical
    /// time far from a checkpoint. Exact checkpoint values and their short neighborhoods avoid a full count.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static ulong PrimeCountingFunction(this ulong value, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        if (value <= uint.MaxValue) { return ((uint)value).PrimeCountingFunction(); }

        var checkpoint = NearestValueCheckpoint64(value: value);
        var anchor = CheckpointBound64(index: checkpoint);
        var distance = ((anchor >= value) ? (anchor - value) : (value - anchor));

        if (distance == 0) { return PrimeCountPowers64[checkpoint]; }
        if ((value <= CountingValueLimit) && (distance > SelectionWindow64)) {
            return CountQuotients64(cancellationToken: cancellationToken, value: value);
        }

        var delta = CountInterval64(low: (Math.Min(val1: value, val2: anchor) + 1), high: Math.Max(val1: value, val2: anchor), cancellationToken: cancellationToken);

        return ((value < anchor) ? (PrimeCountPowers64[checkpoint] - delta) : (PrimeCountPowers64[checkpoint] + delta));
    }

    private static ulong CountQuotients64(ulong value, CancellationToken cancellationToken) {
        var squareRoot = ((uint)value.SquareRoot());
        var roughCount = ((squareRoot + 1U) >> 1);
        var ignore = ArrayPool<bool>.Shared.Rent(minimumLength: ((int)(squareRoot + 1U)));
        var larges = ArrayPool<ulong>.Shared.Rent(minimumLength: ((int)roughCount));
        var multipliers = ArrayPool<ulong>.Shared.Rent(minimumLength: ((int)roughCount));
        var roughs = ArrayPool<uint>.Shared.Rent(minimumLength: ((int)roughCount));
        var smalls = ArrayPool<uint>.Shared.Rent(minimumLength: ((int)roughCount));

        try {
            for (var index = 0U; (index < roughCount); ++index) {
                larges[index] = (((value / ((index << 1) + 1U)) - 1UL) >> 1);
                roughs[index] = ((index << 1) + 1U);
                smalls[index] = index;
            }
            Array.Clear(array: ignore, index: 0, length: ((int)(squareRoot + 1U)));
            var counter = 0U;

            for (var factor = 3U; (factor <= squareRoot); factor += 2U) {
                if (ignore[factor]) { continue; }
                var factorSquared = (((ulong)factor) * factor);

                if ((factorSquared * factorSquared) > value) { break; }
                cancellationToken.ThrowIfCancellationRequested();
                ignore[factor] = true;
                for (var multiple = ((uint)factorSquared); (multiple <= squareRoot); multiple += (factor << 1)) { ignore[multiple] = true; }
                var nextCount = 0U;

                for (var index = 0U; (index < roughCount); ++index) {
                    var rough = roughs[index];

                    if (ignore[rough]) { continue; }
                    var product = (((ulong)rough) * factor);
                    var removed = ((product > squareRoot)
                        ? smalls[((uint)(((value / product) - 1UL) >> 1))]
                        : larges[(smalls[((uint)(product >> 1))] - counter)]);

                    larges[nextCount] = ((larges[index] - removed) + counter);
                    roughs[nextCount++] = rough;
                }
                roughCount = nextCount;
                var high = ((squareRoot - 1U) >> 1);
                var divisor = ((squareRoot / factor) - 1U) | 1U;

                while (divisor >= factor) {
                    var removed = (smalls[(divisor >> 1)] - counter);
                    var low = ((divisor * factor) >> 1);

                    if (low <= high) {
                        SubtractInPlace(subtrahend: removed, values: smalls.AsSpan(length: ((int)((high - low) + 1U)), start: ((int)low)));
                        high = (low - 1U);
                    }
                    divisor -= 2U;
                }
                ++counter;
            }

            larges[0] += (((((ulong)roughCount) + (2UL * (counter - 1U))) * (roughCount - 1U)) >> 1);
            for (var index = 1U; (index < roughCount); ++index) { larges[0] -= larges[index]; }
            for (var index = 1U; (index < roughCount); ++index) { multipliers[index] = (ulong.MaxValue / roughs[index]); }
            for (var index = 1U; (index < roughCount); ++index) {
                cancellationToken.ThrowIfCancellationRequested();
                var quotient = DivideCountingWord64(value: value, divisor: roughs[index], reciprocal: multipliers[index]);
                var bound = (smalls[((uint)((DivideCountingWord64(value: quotient, divisor: roughs[index], reciprocal: multipliers[index]) - 1UL) >> 1))] - counter);

                if (bound < (index + 1U)) { break; }
                var sum = 0UL;

                for (var next = (index + 1U); (next <= bound); ++next) {
                    sum += smalls[((uint)((DivideCountingWord64(value: quotient, divisor: roughs[next], reciprocal: multipliers[next]) - 1UL) >> 1))];
                }
                larges[0] += (sum - (((ulong)(bound - index)) * ((counter + index) - 1U)));
            }
            return (larges[0] + 1UL);
        } finally {
            ArrayPool<uint>.Shared.Return(array: smalls);
            ArrayPool<uint>.Shared.Return(array: roughs);
            ArrayPool<ulong>.Shared.Return(array: multipliers);
            ArrayPool<ulong>.Shared.Return(array: larges);
            ArrayPool<bool>.Shared.Return(array: ignore);
        }
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong DivideCountingWord64(ulong value, uint divisor, ulong reciprocal) {
        // The reciprocal is floor(2^64/divisor) for an odd divisor > 1. Its high product underestimates
        // the quotient by at most one; the exact remainder comparison supplies that possible missing unit.
        var quotient = ((ulong)((((UInt128)value) * reciprocal) >> 64));

        return (quotient + (((value - (quotient * divisor)) >= divisor) ? 1UL : 0UL));
    }
    private static ulong CountInterval64(ulong low, ulong high, CancellationToken cancellationToken) {
        var count = 0UL;

        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var end = (low + Math.Min(val1: (high - low), val2: (SelectionWindow64 - 1)));

            count += PrimeExploration.Count(low: low, high: end, mode: PrimeSieveMode.Presieve);
            if (end == high) { return count; }
            low = (end + 1);
        }
    }
    private static ulong WalkRank64(ulong start, ulong remaining, bool forward, CancellationToken cancellationToken) {
        if (remaining <= 16) {
            var candidate = (forward ? start | 1UL : (start - 1UL) | 1UL);

            while (true) {
                cancellationToken.ThrowIfCancellationRequested();
                if (PrimeExploration.IsPrime(value: candidate) && (--remaining == 0)) { return candidate; }
                candidate = (forward ? (candidate + 2) : (candidate - 2));
            }
        }
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var desired = Math.Min(val1: ((double)SelectionWindow64), val2: ((remaining * (Math.Log(d: start) + 2)) + 1024));
            var span = Math.Max(val1: 4096UL, val2: ((ulong)desired));
            var low = (forward ? start : (start - Math.Min(val1: start, val2: (span - 1))));
            var high = (forward ? (start + Math.Min(val1: (ulong.MaxValue - start), val2: (span - 1))) : start);
            var mode = ((high <= CountingValueLimit) ? PrimeSieveMode.Automatic : PrimeSieveMode.Presieve);
            var count = PrimeExploration.Count(low: low, high: high, mode: mode);

            if (count >= remaining) {
                var skip = (forward ? remaining : ((count - remaining) + 1));
                var selected = 0UL;

                PrimeExploration.Enumerate(low: low, high: high, mode: mode, onPrime: prime => {
                    if ((skip != 0) && (--skip == 0)) { selected = prime; }
                });
                return selected;
            }
            remaining -= count;
            start = (forward ? (high + 1) : (low - 1));
        }
    }
    private static ulong CheckpointBound64(int index) => ((index == 32) ? ulong.MaxValue : (1UL << (index + 32)));
    private static int NearestValueCheckpoint64(ulong value) {
        var nearest = 0;
        var best = ulong.MaxValue;

        for (var index = 0; (index < PrimeCountPowers64.Length); ++index) {
            var bound = CheckpointBound64(index: index);
            var distance = ((bound >= value) ? (bound - value) : (value - bound));

            if (distance < best) { best = distance; nearest = index; }
        }
        return nearest;
    }
    private static int NearestRankCheckpoint64(ulong ordinal) {
        var nearest = 0;
        var best = ulong.MaxValue;

        for (var index = 0; (index < PrimeCountPowers64.Length); ++index) {
            var count = PrimeCountPowers64[index];
            var distance = ((count >= ordinal) ? (count - ordinal) : (ordinal - count));

            if (distance < best) { best = distance; nearest = index; }
        }
        return nearest;
    }
}

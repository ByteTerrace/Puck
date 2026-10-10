using System.Numerics;

namespace Puck.Maths;

public static partial class PrimeExtensions {
    private const ulong PrimeCount64 = 425_656_284_035_217_743;
    private const uint CountingRootLimit = ((1U << 22) - 1U);
    private const ulong CountingValueLimit = (((ulong)CountingRootLimit) * CountingRootLimit);

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

    /// <summary>Decides primality exactly throughout the unsigned 64-bit domain.</summary>
    /// <param name="value">The integer to test.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is prime; zero and one are not prime.</returns>
    /// <remarks>
    /// Words within <see cref="uint"/> take <see cref="IsPrime(uint)"/>. Larger words reject factors two, three and five and
    /// then run Baillie–PSW with Selfridge Method A, whose exactness on this finite domain rests on the exhaustive
    /// computation cited by <see cref="PrimeField64.IsBaillieProbablePrime(ulong)"/>.
    /// </remarks>
    public static bool IsPrime(this ulong value) => PrimeKernels.IsPrimeWord(value: value);
    /// <summary>Returns the prime at a zero-based unsigned sixty-four-bit index.</summary>
    /// <param name="value">The zero-based index: zero selects two.</param>
    /// <param name="cancellationToken">Cancels a potentially long exact count or interval walk.</param>
    /// <returns>The selected prime, or zero when the index is at least 425,656,284,035,217,743,
    /// the number of primes representable by <see cref="ulong"/>.</returns>
    /// <remarks>
    /// <para>Ranks whose result fits in <see cref="uint"/> use the narrow implementation. Larger ranks use
    /// an inverse Riemann R estimate, exact combinatorial counts and count-guided corrections before selecting
    /// from a short interval. Counting setup is reused within the request, and local selection sieves each
    /// window once. Floating-point estimates affect work only, never the selected prime.</para>
    /// <para>After an exact count, a local sieve replaces another count whenever its integer cost bound is below
    /// that count's work floor. Local windows span up to 502,333,410 integers and repeat one upper-base pass each;
    /// where carried sieve states would exceed the automatic workspace bound, windowed sieving keeps local bitmaps
    /// within two 16-MiB pooled arrays, so ranks near 10^15 and 10^16 need one global count.</para>
    /// <para>Published power-of-two counts bound the search and make nearby ranks, including the last ranks,
    /// inexpensive. Other ranks use the combinatorial counter across the unsigned domain, rather than sieving
    /// the distance from a checkpoint. Cancellation is observed during counting and between sieve segments.
    /// A count that contradicts its own exact bracket raises <see cref="InvalidOperationException"/> instead of
    /// returning the out-of-range zero.</para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static ulong NthPrime(this ulong value, CancellationToken cancellationToken = default) =>
        NthPrime64(cancellationToken: cancellationToken, profile: null, value: value);

    private static ulong NthPrime64(ulong value, CancellationToken cancellationToken, PrimeRequestWorkBuilder? profile) {
        cancellationToken.ThrowIfCancellationRequested();
        if (value >= PrimeCount64) {
            if (profile is not null) { profile.Route = PrimeRequestRoute.OutOfRange; }
            return 0;
        }
        if (value <= 203_280_220) {
            if (profile is not null) { profile.Route = PrimeRequestRoute.Narrow32; }
            return ((uint)value).NthPrime();
        }

        var ordinal = (value + 1);
        var checkpoint = NearestRankCheckpoint64(ordinal: ordinal);
        var anchorCount = PrimeCountPowers64[checkpoint];
        var distance = ((anchorCount >= ordinal) ? (anchorCount - ordinal) : (ordinal - anchorCount));
        var anchor = CheckpointBound64(index: checkpoint);

        var lowerCheckpoint = ((anchorCount < ordinal) ? checkpoint : (checkpoint - 1));
        var lower = CheckpointBound64(index: lowerCheckpoint);
        var lowerCount = PrimeCountPowers64[lowerCheckpoint];
        var upper = CheckpointBound64(index: (lowerCheckpoint + 1));
        var checkpointBudget = LocalSieveBudget64(high: anchor);
        var checkpointRemaining = ((anchorCount < ordinal) ? distance : (distance + 1));

        if (RankProbeFits64(budget: checkpointBudget, remaining: checkpointRemaining, value: anchor)) {
            if (profile is not null) { profile.Route = PrimeRequestRoute.CheckpointSelection; }
            var forward = (anchorCount < ordinal);
            var probe = ProbeRank64(cancellationToken: cancellationToken, forward: forward, integerBudget: checkpointBudget,
                profile: profile, remaining: checkpointRemaining, start: (forward ? (anchor + 1) : anchor));

            if (probe.Selected != 0) { return probe.Selected; }
            if (forward) { lower = probe.Boundary; lowerCount = (anchorCount + probe.Count); } else { upper = (probe.Boundary - 1); }
        }
        if (profile is not null) { profile.Route = PrimeRequestRoute.CountedSelection; }

        var estimate = Math.Clamp(value: EstimateNthPrime64(ordinal: ordinal), min: (lower + 1), max: (upper - 1));
        var previousError = ulong.MaxValue;
        var slowCorrections = 0;
        var workspace = new CombinatorialCountingWorkspace();

        // The exact invariant is pi(lower) < ordinal <= pi(upper). Estimates propose the next count;
        // only its integer result moves the bracket or supplies the final one-based interval ordinal.
        while (true) {
            cancellationToken.ThrowIfCancellationRequested();
            var count = CountPrimeBound64(cancellationToken: cancellationToken, profile: profile, value: estimate, workspace: workspace);
            var below = (count < ordinal);
            var error = (below ? (ordinal - count) : (count - ordinal));

            if (below) { lower = estimate; lowerCount = count; } else { upper = estimate; }
            var localBudget = LocalSieveBudget64(high: estimate);
            var remaining = (below ? error : (error + 1));

            if (RankProbeFits64(budget: localBudget, remaining: remaining, value: estimate)) {
                var probe = ProbeRank64(cancellationToken: cancellationToken, forward: below, integerBudget: localBudget,
                    profile: profile, remaining: remaining, start: (below ? (estimate + 1) : estimate));

                if (probe.Selected != 0) { return probe.Selected; }
                // Exhaustion is an exact local count. Keep it in the bracket instead of counting the
                // examined interval again, and never extend this probe beyond its cumulative work budget.
                estimate = (below ? probe.Boundary : (probe.Boundary - 1));
                count = (below ? (count + probe.Count) : (count - probe.Count));
                error = (below ? (ordinal - count) : (count - ordinal));
                if (below) { lower = estimate; lowerCount = count; } else { upper = estimate; }
            }
            if ((upper - lower) <= Math.Min(val1: PrimeExploration.SelectionSpanIntegers, val2: localBudget)) {
                var selected = PrimeExploration.SelectPrime(cancellationToken: cancellationToken, high: upper, low: (lower + 1), ordinal: (ordinal - lowerCount), profile: profile);

                // Zero is the documented out-of-range answer, so a broken bracket must not masquerade as one.
                return ((selected != 0) ? selected
                    : throw new InvalidOperationException(message: $"Exact counts broke the rank bracket pi({lower}) < {ordinal} <= pi({upper})."));
            }

            // Correct from the exact count, using the local prime density only to choose work. A poor
            // correction cannot escape the bracket; repeated poor corrections force a halving step.
            slowCorrections = ((error >= (previousError >> 1)) ? (slowCorrections + 1) : 0);
            previousError = error;
            var available = (below ? ((upper - estimate) - 1) : ((estimate - lower) - 1));
            var desired = Math.Max(val1: 1D, val2: (error * Math.Log(d: estimate)));

            if ((slowCorrections >= 2) || (desired >= available)) {
                estimate = (lower + ((upper - lower) >> 1));
                slowCorrections = 0;
            } else {
                var correction = ((ulong)desired);

                estimate = (below ? (estimate + correction) : (estimate - correction));
            }
        }
    }

    /// <summary>Returns the number of primes less than or equal to an unsigned sixty-four-bit bound.</summary>
    /// <param name="value">The inclusive upper bound.</param>
    /// <param name="cancellationToken">Cancels a potentially long exact count.</param>
    /// <returns>The exact prime count through <paramref name="value"/>.</returns>
    /// <remarks>
    /// Bounds through <see cref="uint.MaxValue"/> use the narrow combinatorial implementation. Larger
    /// bounds whose square root is at most 4,194,303 use its widened quotient-counting recurrence, with at most
    /// approximately 52 MiB of pooled array payload. Larger distant bounds use Gourdon's combinatorial counter.
    /// Published power-of-two counts admit a local interval count instead of a global one when the interval's
    /// integer cost bound, its width plus one upper-base pass per 502,333,410-integer window, stays below the
    /// global count's work floor: its initialization divisions for the quotient recurrence, or the Gourdon
    /// hard-leaf frontier.
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    public static ulong PrimeCountingFunction(this ulong value, CancellationToken cancellationToken = default) =>
        CountPrimeBound64(cancellationToken: cancellationToken, value: value, workspace: null);

    private static ulong CountPrimeBound64(ulong value, CancellationToken cancellationToken, CombinatorialCountingWorkspace? workspace, PrimeRequestWorkBuilder? profile = null) {
        cancellationToken.ThrowIfCancellationRequested();
        var work = profile?.BeginCount(bound: value);

        if (value <= uint.MaxValue) {
            if (work is not null) { work.Route = PrimeCountRoute.Narrow32; }
            return ((uint)value).PrimeCountingFunction();
        }

        var checkpoint = NearestValueCheckpoint64(value: value);
        var anchor = CheckpointBound64(index: checkpoint);
        var distance = ((anchor >= value) ? (anchor - value) : (value - anchor));

        if (distance == 0) {
            if (work is not null) { work.Route = PrimeCountRoute.Checkpoint; }
            return PrimeCountPowers64[checkpoint];
        }
        if (distance > LocalSieveBudget64(high: Math.Max(val1: value, val2: anchor))) {
            if (work is not null) { work.Route = ((value <= CountingValueLimit) ? PrimeCountRoute.Quotient64 : PrimeCountRoute.Gourdon); }
            return ((value <= CountingValueLimit)
                ? CountQuotients64(cancellationToken: cancellationToken, value: value)
                : ((workspace is null)
                    ? CountCombinatorial64(cancellationToken: cancellationToken, profile: work, value: value)
                    : workspace.Count(cancellationToken: cancellationToken, profile: work, value: value)));
        }
        if (work is not null) { work.Route = PrimeCountRoute.CheckpointInterval; }
        var delta = PrimeExploration.CountWithWork(low: (Math.Min(val1: value, val2: anchor) + 1), high: Math.Max(val1: value, val2: anchor),
            cancellationToken: cancellationToken, work: work?.CheckpointBitmap);

        return ((value < anchor) ? (PrimeCountPowers64[checkpoint] - delta) : (PrimeCountPowers64[checkpoint] + delta));
    }
    private static ulong CountQuotients64(ulong value, CancellationToken cancellationToken) =>
        CountQuotients<ulong, QuotientCountWord64>(cancellationToken: cancellationToken, squareRoot: ((uint)value.SquareRoot()), value: value);

    // The cumulative integer width a local sieve may cover instead of one more global count at the bound: the
    // widest W with ceil(W/span)*pass + W <= the count's work floor, where span is the selection window and pass
    // is one upper-base pass. Whole windows come first, then a partial window if it still pays. These are work
    // estimates in integer units; they assert neither prime gaps nor cycle costs.
    public static ulong LocalSieveBudget64(ulong high) {
        var pass = PrimeExploration.UpperBasePassCost(root: ((uint)high.SquareRoot()));
        var countFloor = GlobalCountFloor64(value: high);
        var window = (PrimeExploration.SelectionSpanIntegers + pass);
        var windows = (countFloor / window);
        var remainder = (countFloor - (windows * window));

        return Math.Max(val1: 1UL, val2: ((windows * PrimeExploration.SelectionSpanIntegers) + ((remainder > pass) ? (remainder - pass) : 0UL)));
    }

    // A lower bound, in the same integer units, on the work of the global count CountPrimeBound64 dispatches
    // for a bound away from its checkpoint. The quotient recurrence divides the bound by every odd integer
    // below its square root while initializing; Gourdon's hard-leaf loop sieves every integer below
    // floor(x/(y+1))+1.
    internal static ulong GlobalCountFloor64(ulong value) =>
        ((value <= CountingValueLimit)
            ? PrimeExploration.DivisionCost(divisions: ((value.SquareRoot() + 1UL) >> 1))
            : (value / (((ulong)ResolveGourdonCutoff(value: value)) + 1UL)));

    private static bool RankProbeFits64(ulong remaining, ulong value, ulong budget) =>
        ((remaining <= 16) || (((((UInt128)remaining) * ((uint)(BitOperations.Log2(value: value) + 3))) + 1024) <= budget));
    private static (ulong Selected, ulong Boundary, ulong Count) ProbeRank64(ulong start, ulong remaining, bool forward, ulong integerBudget,
        CancellationToken cancellationToken, PrimeRequestWorkBuilder? profile) {
        var consumed = 0UL;
        var boundary = start;

        if (remaining <= 16) {
            var candidate = (forward ? start | 1UL : (start - 1UL) | 1UL);
            var available = (forward ? (ulong.MaxValue - start) : start);
            var distance = Math.Min(val1: (integerBudget - 1), val2: available);
            var low = (forward ? start : (start - distance));
            var high = (forward ? (start + distance) : start);

            while ((candidate >= low) && (candidate <= high)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (profile is not null) { ++profile.DirectPrimalityRequests; }
                if (PrimeExtensions.IsPrime(value: candidate)) {
                    ++consumed;
                    if (--remaining == 0) {
                        cancellationToken.ThrowIfCancellationRequested();
                        return (candidate, candidate, consumed);
                    }
                }
                if ((forward ? (high - candidate) : (candidate - low)) < 2) { break; }
                candidate = (forward ? (candidate + 2) : (candidate - 2));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return (0, (forward ? high : low), consumed);
        }
        while (integerBudget != 0) {
            cancellationToken.ThrowIfCancellationRequested();
            var available = (forward ? (ulong.MaxValue - start) : start);
            var desired = Math.Min(val1: (Math.Min(val1: PrimeExploration.SelectionSpanIntegers, val2: integerBudget) - 1), val2: Math.Max(val1: 4095D, val2: ((remaining * (Math.Log(d: start) + 2)) + 1023)));
            // Clamp the proposed window to the remaining integer domain before narrowing its size.
            var distance = ((desired >= available) ? available : Math.Min(val1: available, val2: ((ulong)desired)));
            var low = (forward ? start : (start - distance));
            var high = (forward ? (start + distance) : start);

            var (selected, count) = PrimeExploration.SelectPrimeAndCount(cancellationToken: cancellationToken, forward: forward, high: high,
                low: low, ordinal: remaining, profile: profile);

            consumed += count;
            if (selected != 0) { return (selected, selected, consumed); }
            remaining -= count;
            boundary = (forward ? high : low);
            integerBudget -= (distance + 1);
            if (distance == available) { break; }
            start = (forward ? (high + 1) : (low - 1));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (0, boundary, consumed);
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

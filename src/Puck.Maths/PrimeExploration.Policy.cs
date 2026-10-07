using System.Numerics;

namespace Puck.Maths;

public static partial class PrimeExploration {
    private const ulong ProvenPresieveLimit = (65537UL * 65537UL);
    private const ulong AutomaticWorkspaceBudget = ((128UL * 1024UL) * 1024UL);

    /// <summary>Resolves the automatic policy for an inclusive interval into a complete sieve or a bounded presieve.</summary>
    /// <param name="low">The inclusive lower bound; a reversed interval is empty.</param>
    /// <param name="high">The inclusive upper bound.</param>
    /// <param name="mode">The requested policy. Explicit policies are preserved.</param>
    /// <returns>The selected <see cref="PrimeSieveMode.Eratosthenes"/> or <see cref="PrimeSieveMode.Presieve"/> policy.</returns>
    /// <remarks>
    /// Automatic mode completely sieves the uint domain. Above it, complete sieving requires an interval width
    /// of at least one sixty-fourth of the square root of the upper bound and a conservative upper-base workspace
    /// bound of 128 MiB. A Rosser-Schoenfeld prime-count bound budgets eight-byte states, partially filled pages,
    /// slab growth and the bucket-head window, plus three MiB for medium states and the streamed base generator.
    /// Shared tables, the requested bitmap, small-prime states, allocator bookkeeping and pool retention are additional.
    /// This crossover is a cost heuristic, not an accuracy condition; both policies return the same primes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is undefined.</exception>
    public static PrimeSieveMode ResolveMode(ulong low, ulong high, PrimeSieveMode mode = PrimeSieveMode.Automatic) {
        if (((uint)mode) > ((uint)PrimeSieveMode.Presieve)) { throw new ArgumentOutOfRangeException(paramName: nameof(mode)); }
        if (mode != PrimeSieveMode.Automatic) { return mode; }
        if ((high < low) || (high < ProvenPresieveLimit)) { return PrimeSieveMode.Eratosthenes; }
        var root = high.SquareRoot();
        var requiredWidth = ((root + 63UL) / 64UL);

        return (((AutomaticWorkspaceBytes(root: ((uint)root)) <= AutomaticWorkspaceBudget) && ((high - low) >= (requiredWidth - 1UL)))
            ? PrimeSieveMode.Eratosthenes : PrimeSieveMode.Presieve);
    }

    private static int WideBucketHorizon(uint limit) {
        // A 210-wheel gap is at most ten; its byte advance is at most 10*(p/30)+10.
        return ((int)((((10UL * (limit / 30U)) + 10) >> WideBucketBits) + 3));
    }
    private static unsafe ulong AutomaticWorkspaceBytes(uint root) {
        // Rosser and Schoenfeld, Corollary 1 (3.6): pi(x) < 1.25506*x/ln(x), x>1.
        // https://doi.org/10.1215/ijm/1255631807
        // Here root>=65537. For k=floor(log2(root)), ln(root)>=k*ln(2)>k*693147/10^6.
        // The first six positive terms of 2*sum(1/((2j+1)*3^(2j+1))) already exceed 693147/10^6.
        // Rounding the resulting rational upward is conservative without floating-point assumptions.
        var denominator = (693147UL * ((uint)BitOperations.Log2(value: root)));
        var primes = ((((1255060UL * root) + denominator) - 1) / denominator);
        var horizon = ((ulong)WideBucketHorizon(limit: root));
        var capacity = ((ulong)((WidePageBytes - sizeof(WideNativePage)) / sizeof(WideState)));
        // One partial page per bucket plus detached input and its current copy; all other pages are full.
        var pages = (((((primes + capacity) - 1) / capacity) + horizon) + 3);
        // Geometric slabs retain <2*peak+16 pages; capped growth retains <peak+2048 pages.
        var retained = Math.Min(val1: ((2 * pages) + WideInitialSlabPages), val2: (pages + WideMaximumSlabPages));

        return (((retained * WidePageBytes) + ((2 * horizon) * ((ulong)sizeof(nuint)))) + ((3UL * 1024UL) * 1024UL));
    }

    /// <summary>Resolves a requested bitmap size using the marking strategy's cache policy.</summary>
    /// <param name="high">The inclusive interval upper bound.</param>
    /// <param name="segmentBytes">The positive requested bitmap size.</param>
    /// <param name="strategy">The marking strategy.</param>
    /// <returns>The maximum segment size used before clamping to the actual interval length.</returns>
    /// <remarks>Bucket packets round twice the upper bound's square root to cache chunks, clamped to the request.
    /// Other strategies use the request unchanged. This is bitmap storage, not total sieve workspace.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The size is outside <c>[1, Array.MaxLength]</c> or the strategy is undefined.</exception>
    public static int ResolveSegmentBytes(ulong high, int segmentBytes = 32768, PrimeSieveStrategy strategy = PrimeSieveStrategy.BucketPackets) {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(segmentBytes, Array.MaxLength);
        if (((uint)strategy) > ((uint)PrimeSieveStrategy.BucketPackets)) { throw new ArgumentOutOfRangeException(paramName: nameof(strategy)); }
        if (strategy != PrimeSieveStrategy.BucketPackets) { return segmentBytes; }
        var cacheBytes = Math.Min(val1: segmentBytes, val2: 32768);
        var adaptiveBytes = Math.Clamp(max: ((ulong)segmentBytes), min: ((ulong)cacheBytes), value: (2UL * high.SquareRoot()));

        return ((int)(adaptiveBytes - (adaptiveBytes % ((ulong)cacheBytes))));
    }
}

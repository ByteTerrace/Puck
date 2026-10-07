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
    /// bound of 128 MiB. The bound reserves forty-eight bytes for every potential base prime coprime to thirty,
    /// including transfers and recycled state pages, plus three MiB for bucket metadata and minimum rentals; actual primes
    /// form a subset. Shared tables, the requested bitmap, small-prime states and pool retention are additional.
    /// This crossover is a cost heuristic, not an accuracy condition; both policies return the same primes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is undefined.</exception>
    public static PrimeSieveMode ResolveMode(ulong low, ulong high, PrimeSieveMode mode = PrimeSieveMode.Automatic) {
        if (((uint)mode) > ((uint)PrimeSieveMode.Presieve)) { throw new ArgumentOutOfRangeException(paramName: nameof(mode)); }
        if (mode != PrimeSieveMode.Automatic) { return mode; }
        if ((high < low) || (high < ProvenPresieveLimit)) { return PrimeSieveMode.Eratosthenes; }
        var root = high.SquareRoot();
        var candidateBound = (((root / 30UL) * 8UL) + 8UL);
        var requiredWidth = ((root + 63UL) / 64UL);

        return (((((candidateBound * 48UL) + ((3UL * 1024UL) * 1024UL)) <= AutomaticWorkspaceBudget) && ((high - low) >= (requiredWidth - 1UL)))
            ? PrimeSieveMode.Eratosthenes : PrimeSieveMode.Presieve);
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

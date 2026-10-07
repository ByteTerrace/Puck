using System.Numerics;

namespace Puck.Maths;

/// <summary>Selects complete sieving or a bounded sieve followed by exact primality decisions.</summary>
internal enum PrimeSieveMode {
    /// <summary>Chooses complete, windowed or bounded presieving from interval width and upper-base workspace.</summary>
    Automatic,
    /// <summary>Generates every required base prime and carries useful upper-prime cursors in segment buckets.</summary>
    Eratosthenes,
    /// <summary>Sieves with primes through 65,535 and decides surviving candidates exactly.</summary>
    Presieve,
    /// <summary>Completely sieves each segment by streaming every upper base prime through it once, carrying no per-prime state.</summary>
    Windowed,
}

public static partial class PrimeExploration {
    /// <summary>The bitmap bytes of a segment, and of a small-prime marking chunk inside a larger one: one 32-KiB
    /// level-one data cache.</summary>
    internal const int CacheSegmentBytes = 32768;

    // 65521 is the largest entry of the shared base-prime table; the next prime starts the upper bases.
    private const uint FirstUpperPrime = 65537;
    private const ulong ProvenPresieveLimit = (((ulong)FirstUpperPrime) * FirstUpperPrime);
    private const ulong AutomaticWorkspaceBudget = ((128UL * 1024UL) * 1024UL);

    /// <summary>The bitmap bytes of an automatic windowed segment.</summary>
    /// <remarks>Sixteen MiB less one 32-KiB chunk: a whole number of cache chunks whose 63-byte alignment pad
    /// still fits one sixteen-MiB pooled array. This bounds windowed workspace independently of the base primes.</remarks>
    internal const int WindowedSegmentBytes = (((16 * 1024) * 1024) - WideBucketBytes);
    /// <summary>The widest local selection window: any interval this wide touches at most <see cref="WindowedSegmentBytes"/>
    /// wheel bytes, so one window makes one upper-base pass and a reverse window copies at most one such bitmap.</summary>
    internal const ulong SelectionSpanIntegers = ((WindowedSegmentBytes - 1UL) * PrimeWheel30.Modulus);

    /// <summary>Resolves the automatic policy for an inclusive interval into complete, windowed or bounded presieving.</summary>
    /// <param name="low">The inclusive lower bound; a reversed interval is empty.</param>
    /// <param name="high">The inclusive upper bound.</param>
    /// <param name="mode">The requested policy. Explicit policies are preserved.</param>
    /// <returns>The selected <see cref="PrimeSieveMode.Eratosthenes"/>, <see cref="PrimeSieveMode.Windowed"/> or
    /// <see cref="PrimeSieveMode.Presieve"/> policy.</returns>
    /// <remarks>
    /// Automatic mode completely sieves every interval ending below 65,537², the square of the first prime past the
    /// shared base-prime table, so every composite there has a prime factor in that table. Above it, an interval narrower
    /// than one sixty-fourth of the square root of its upper bound uses bounded presieving. Wider intervals use
    /// carried-state complete sieving when a conservative upper-base workspace bound fits 128 MiB, and windowed
    /// complete sieving otherwise. A Rosser-Schoenfeld prime-count bound budgets eight-byte states, partially filled
    /// pages, slab growth and the bucket-head window, plus three MiB for medium states and the streamed base generator.
    /// Shared tables, the requested bitmap, small-prime states, allocator bookkeeping and pool retention are additional.
    /// Automatic windowed sieving uses segments of 16 MiB less one 32-KiB chunk. These crossovers are cost heuristics,
    /// not accuracy conditions; every policy returns the same primes.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is undefined.</exception>
    internal static PrimeSieveMode ResolveMode(ulong low, ulong high, PrimeSieveMode mode = PrimeSieveMode.Automatic) {
        if (((uint)mode) > ((uint)PrimeSieveMode.Windowed)) { throw new ArgumentOutOfRangeException(paramName: nameof(mode)); }
        if (mode != PrimeSieveMode.Automatic) { return mode; }
        if ((high < low) || (high < ProvenPresieveLimit)) { return PrimeSieveMode.Eratosthenes; }
        var root = high.SquareRoot();
        var requiredWidth = ((root + 63UL) / 64UL);

        if ((high - low) < (requiredWidth - 1UL)) { return PrimeSieveMode.Presieve; }
        return ((AutomaticWorkspaceBytes(root: ((uint)root)) <= AutomaticWorkspaceBudget) ? PrimeSieveMode.Eratosthenes : PrimeSieveMode.Windowed);
    }
    // Integer-equivalent work of one upper-base pass through root >= 2, made once by each carried sieve call and
    // once by each windowed segment. Regenerating the bases sieves root integers; each base start, a division
    // and a residue search, is charged one complete wheel byte. The charge is a cost heuristic used only to
    // choose work, like the automatic crossovers.
    internal static ulong UpperBasePassCost(uint root) => (root + (PrimeWheel30.Modulus * UpperBasePrimeBound(root: root)));
    // Charges a division one complete wheel byte, the same unit as a base-prime start.
    internal static ulong DivisionCost(ulong divisions) => (PrimeWheel30.Modulus * divisions);

    private static int WideBucketHorizon(uint limit) {
        // A 210-wheel gap is at most ten; its byte advance is at most 10*(p/30)+10.
        return ((int)((((10UL * (limit / PrimeWheel30.Modulus)) + 10) >> WideBucketBits) + 3));
    }
    private static ulong UpperBasePrimeBound(uint root) {
        // Rosser and Schoenfeld, Corollary 1 (3.6): pi(x) < 1.25506*x/ln(x), x>1.
        // https://doi.org/10.1215/ijm/1255631807
        // For root>=2 and k=floor(log2(root))>=1, ln(root)>=k*ln(2)>k*693147/10^6.
        // The first six positive terms of 2*sum(1/((2j+1)*3^(2j+1))) already exceed 693147/10^6.
        // Rounding the resulting rational upward is conservative without floating-point assumptions.
        var denominator = (693147UL * ((uint)BitOperations.Log2(value: root)));

        return ((((1255060UL * root) + denominator) - 1) / denominator);
    }
    private static unsafe ulong AutomaticWorkspaceBytes(uint root) {
        var primes = UpperBasePrimeBound(root: root);
        var horizon = ((ulong)WideBucketHorizon(limit: root));
        var capacity = ((ulong)((WidePageBytes - sizeof(WideNativePage)) / sizeof(WideState)));
        // One partial page per bucket plus detached input and its current copy; all other pages are full.
        var pages = (((((primes + capacity) - 1) / capacity) + horizon) + 3);
        // Geometric slabs retain <2*peak+16 pages; capped growth retains <peak+2048 pages.
        var retained = Math.Min(val1: ((2 * pages) + WideInitialSlabPages), val2: (pages + WideMaximumSlabPages));

        return (((retained * WidePageBytes) + ((2 * horizon) * ((ulong)sizeof(nuint)))) + ((3UL * 1024UL) * 1024UL));
    }

    /// <summary>Resolves the bitmap size enumeration and counting use for an interval and requested policy.</summary>
    /// <param name="low">The inclusive interval lower bound.</param>
    /// <param name="high">The inclusive interval upper bound.</param>
    /// <param name="segmentBytes">The positive requested bitmap size.</param>
    /// <param name="mode">The requested policy.</param>
    /// <returns>The maximum segment size used before clamping to the actual interval length.</returns>
    /// <remarks>When <see cref="PrimeSieveMode.Automatic"/> resolves to <see cref="PrimeSieveMode.Windowed"/>, the request
    /// is first raised to 16 MiB less one 32-KiB chunk, because every windowed segment repeats the upper-base pass;
    /// explicit policies keep the request. Twice the upper bound's square root is then clamped between
    /// <c>min(request, 32768)</c> and the request and rounded down to a multiple of that minimum. This is bitmap
    /// storage, not total sieve workspace.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The size is outside <c>[1, Array.MaxLength]</c>, or the mode is undefined.</exception>
    internal static int ResolveSegmentBytes(ulong low, ulong high, int segmentBytes, PrimeSieveMode mode) {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(segmentBytes, Array.MaxLength);
        // Enumeration resolves its policy after reporting 2, 3 and 5, from a lower bound of at least seven.
        var resolved = ResolveMode(high: high, low: Math.Max(val1: low, val2: 7UL), mode: mode);

        if ((mode == PrimeSieveMode.Automatic) && (resolved == PrimeSieveMode.Windowed)) { segmentBytes = Math.Max(val1: segmentBytes, val2: WindowedSegmentBytes); }
        var cacheBytes = Math.Min(val1: segmentBytes, val2: CacheSegmentBytes);
        var adaptiveBytes = Math.Clamp(max: ((ulong)segmentBytes), min: ((ulong)cacheBytes), value: (2UL * high.SquareRoot()));

        return ((int)(adaptiveBytes - (adaptiveBytes % ((ulong)cacheBytes))));
    }
}

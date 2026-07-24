namespace Puck.Maths;

/// <summary>
/// A low-discrepancy Kronecker–Weyl sequence certified by the continued fraction of its generator. The one-dimensional
/// sequence <c>{n·α}</c> (<c>n = 1, 2, 3, …</c>) has a provably bounded star discrepancy exactly when <c>α</c> is badly
/// approximable — when the partial quotients of its continued fraction are bounded — so the largest partial quotient over
/// the eventually periodic expansion of a quadratic irrational (<see cref="ContinuedFraction"/>) is an exact integer
/// certificate of how evenly the points cover the unit interval. By Hurwitz's theorem the golden ratio, whose all-ones
/// expansion <c>[1; 1, 1, …]</c> gives the smallest possible certificate one, is the most irrational number and the optimal
/// generator; the silver ratio <c>[2; 2, 2, …]</c> certifies at two, and every metallic mean <c>δₙ = [n; n, n, …]</c> at
/// <c>n</c> — the same badly-approximable units that drive <see cref="MetallicQuasicrystal"/>.
/// </summary>
/// <remarks>
/// <see cref="FromQuadraticIrrational"/> reads the certificate straight from <see cref="ContinuedFraction.Expand"/> — the
/// maximum partial quotient, in pure integer arithmetic with no approximate seam — and precomputes the generator's
/// fractional part as a UQ0.64 multiplier. <see cref="Point"/> then maps an index to its point by the additive recurrence
/// <c>frac(index · frac(α))</c>, the sixty-four-bit wrap performing the mod-one exactly, in the stateless one-multiply style
/// of <see cref="LowDiscrepancy.R1"/> — the fractional multiplier is the one approximate value, the certificate above it
/// exact. <see cref="DiscrepancyBound"/> turns the certificate into the guarantee it certifies: the star discrepancy of the
/// first <c>N</c> points is at most <c>(3 + K·m) / N</c>, where <c>K</c> is the certificate and <c>m</c> bounds the count of
/// continued-fraction convergents with denominator at most <c>N</c> (the Fibonacci growth floor) — a closed-form
/// <c>O(K·log N / N)</c> consequence of the certificate, deliberately conservative in its constant.
/// </remarks>
public readonly record struct CertifiedLowDiscrepancy {
    // The additive slack of the equidistribution theorem N·D*_N ≤ C₀ + Σ aᵢ; kept safely conservative.
    private const long DiscrepancyConstant = 3L;

    private CertifiedLowDiscrepancy(ulong multiplier, long certificate) {
        Multiplier = multiplier;
        Certificate = certificate;
    }

    // The generator's fractional part frac(α) as a UQ0.64 value — the additive-recurrence increment of the sequence.
    private ulong Multiplier { get; }

    /// <summary>Gets the badly-approximable certificate: the largest continued-fraction partial quotient of the generator, excluding its integer part.</summary>
    /// <remarks>Smaller is more equidistributed. One — the golden ratio, the all-ones expansion — is the optimal minimum, and the n-th metallic mean certifies at <c>n</c>.</remarks>
    public long Certificate { get; }

    /// <summary>Builds the certified sequence for the quadratic irrational <c>α = (p + q·√d) / r</c>, reading its certificate from the continued-fraction expansion.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <returns>The certified sequence, carrying the exact certificate and the precomputed fractional-part multiplier.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="q"/> is not positive, <paramref name="d"/> is below two or a perfect square, or <paramref name="r"/> is zero.</exception>
    public static CertifiedLowDiscrepancy FromQuadraticIrrational(long p, long q, long d, long r) {
        Span<long> terms = stackalloc long[128];
        var written = ContinuedFraction.Expand(
            p: p,
            q: q,
            d: d,
            r: r,
            terms: terms,
            periodStart: out var periodStart,
            periodLength: out _
        );

        // The star discrepancy of {n·α} sees only frac(α), whose partial quotients are a₁, a₂, … — the expansion with its
        // integer part a₀ = terms[0] dropped (a large integer part does not clump the fractional points). The certificate is
        // their maximum: the pre-period tail terms[1..periodStart) together with the period block terms[periodStart..], which
        // repeats forever and so contributes every one of its terms to the supremum.
        var certificate = 1L;

        for (var index = 1; (index < periodStart); ++index) {
            certificate = Math.Max(val1: certificate, val2: terms[index]);
        }

        for (var index = periodStart; (index < written); ++index) {
            certificate = Math.Max(val1: certificate, val2: terms[index]);
        }

        return new CertifiedLowDiscrepancy(
            multiplier: FractionalMultiplier(p: p, q: q, d: d, r: r),
            certificate: certificate
        );
    }
    /// <summary>Builds the certified sequence for the n-th metallic mean <c>δₙ = (n + √(n² + 4)) / 2</c> — the noble, badly-approximable generator whose certificate is exactly <paramref name="n"/>.</summary>
    /// <param name="n">The metallic index; it must be positive. One is the golden ratio (the optimal certificate one), two the silver ratio.</param>
    /// <returns>The certified sequence of <c>δₙ</c>, whose <see cref="Certificate"/> equals <paramref name="n"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="n"/> is not positive.</exception>
    public static CertifiedLowDiscrepancy MetallicMean(int n) {
        if (0 >= n) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(n),
                message: "the metallic index must be positive"
            );
        }

        // δₙ is the all-n continued fraction (n + √(n² + 4)) / 2, so its every partial quotient — and its certificate — is n.
        return FromQuadraticIrrational(p: n, q: 1L, d: (((long)n * n) + 4L), r: 2L);
    }

    /// <summary>Returns the <paramref name="index"/>-th point of the sequence, <c>frac(index · α)</c> in <c>[0, 1)</c>.</summary>
    /// <param name="index">The point index; consecutive indices land far apart, the more so the smaller the <see cref="Certificate"/>.</param>
    /// <returns>The fraction <c>frac(index · α)</c> in <c>[0, 1)</c>, identical on every machine.</returns>
    public UFixedQ0032 Point(ulong index) =>
        new(Value: ((uint)(unchecked((index * Multiplier)) >> 32)));
    /// <summary>Returns the certified star-discrepancy bound for the first <paramref name="pointCount"/> points: <c>D*_N ≤ (3 + K·m) / N</c>.</summary>
    /// <param name="pointCount">The number of leading points the bound covers; it must be positive.</param>
    /// <returns>An upper bound on the star discrepancy of points one through <paramref name="pointCount"/>, a closed-form consequence of the <see cref="Certificate"/>; it can exceed one (a vacuous bound) for very few points.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pointCount"/> is not positive.</exception>
    public FixedQ4816 DiscrepancyBound(long pointCount) {
        if (0L >= pointCount) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(pointCount),
                message: "the point count must be positive"
            );
        }

        // N·D*_N ≤ C₀ + Σ aᵢ over the convergents with denominator ≤ N (Kuipers–Niederreiter); bounding each aᵢ by the
        // certificate K and the count by the Fibonacci floor m gives the closed form (C₀ + K·m) / N.
        var numerator = checked(DiscrepancyConstant + (Certificate * ConvergentCountBound(pointCount: pointCount)));

        return (FixedQ4816.FromInteger(value: numerator) / FixedQ4816.FromInteger(value: pointCount));
    }

    // 2^64·frac(α) ≡ round(2^64·α) (mod 2^64): the integer part of α scales to a multiple of 2^64 and drops under the wrap,
    // so only the surd's fractional bits survive. Compute q·√d at as many fractional bits as fit a 128-bit root, place the
    // numerator 2^64·(p + q√d) over r, and reduce mod 2^64 — the sole approximate step, in deterministic integer arithmetic.
    private static ulong FractionalMultiplier(long p, long q, long d, long r) {
        var radicand = (((UInt128)(ulong)q * (ulong)q) * (ulong)d);      // q²·d, positive since q > 0 and d ≥ 2
        var headroom = (127 - (int)UInt128.Log2(value: radicand));       // bits below 2^127 the scaled radicand may occupy
        var scale = Math.Max(val1: 0, val2: Math.Min(val1: 64, val2: (headroom / 2)));
        var root = (radicand << (2 * scale)).SquareRoot();               // floor(q·√d · 2^scale)
        var rootQ64 = (((Int128)root) << (64 - scale));                  // q·√d at 2^64, its low (64 − scale) bits zero
        var numerator = (((Int128)p << 64) + rootQ64);                   // 2^64·(p + q√d), to the multiplier's precision
        var scaled = (numerator / r);                                    // 2^64·α, truncating within a unit in the last place

        return unchecked((ulong)scaled);                                 // the low 64 bits are frac(α) as a UQ0.64 value
    }
    // Continued-fraction denominators grow at least as fast as the Fibonacci sequence — every partial quotient is at least
    // one — so no more convergents have denominator ≤ N than there are Fibonacci numbers ≤ N: an α-independent count bound.
    private static long ConvergentCountBound(long pointCount) {
        var count = 0L;
        var previous = 1UL;
        var current = 1UL;
        var limit = ((ulong)pointCount);

        while (current <= limit) {
            ++count;
            (previous, current) = (current, (previous + current));
        }

        return count;
    }
}

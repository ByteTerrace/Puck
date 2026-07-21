using Puck.Maths.Research;

namespace Puck.Maths;

/// <summary>
/// The inflation lens of a quadratic irrational: the exact substitution matrix beneath its aperiodic order, read straight
/// from its continued-fraction period. By Lagrange's theorem the expansion of a quadratic irrational is eventually periodic
/// (<see cref="ContinuedFraction"/>), and the product of the period's partial quotients as the integer matrices
/// <c>[[aᵢ, 1], [1, 0]]</c> gives one matrix representative of the closed geodesic the period codes; an odd-length
/// product has determinant minus one, so <see cref="Axis"/> squares it to obtain the orientation-preserving modular
/// element. Its trace and Perron eigenvalue are the two invariants that drive the cut-and-project chains: the golden
/// period <c>[1]</c> gives the matrix <c>[[1, 1], [1, 0]]</c> and the inflation factor <c>φ</c> of
/// the golden case of <see cref="MetallicQuasicrystal"/>, and the silver period <c>[2]</c> gives <c>[[2, 1], [1, 0]]</c> and
/// the factor <c>1 + √2</c> of its silver case — the two smallest-trace members of one infinite family, now addressed
/// by name rather than hand-coded.
/// </summary>
/// <remarks>
/// The four entries <see cref="A"/>, <see cref="B"/>, <see cref="C"/>, <see cref="D"/> are the non-negative substitution
/// matrix <c>M = ∏ [[aᵢ, 1], [1, 0]]</c> over the period block; its <see cref="Determinant"/> is <c>(−1)^period</c>.
/// Its Perron eigenvalue exceeds one, while the orientation-preserving motion exposed by <see cref="Axis"/> is always
/// <see cref="ModularClass.Hyperbolic"/>. The period is canonical
/// only up to cyclic rotation, so the specific entries are one representative — but the trace, the determinant, the
/// discriminant, and hence the inflation factor are conjugacy invariants, identical for every rotation. <see cref="Axis"/>
/// lifts the representative to the orientation-preserving <see cref="ModularTransform"/> whose axis is the geodesic (the
/// matrix itself when the period is even, its square when odd), so <see cref="ModularTransform.Classify"/> reads the same
/// hyperbolic motion the trace announces. Everything above is exact integer arithmetic; <see cref="InflationFactor"/> is
/// the one approximate seam — the square root that realizes the factor as a fixed-point value — matching the convention of
/// <see cref="MetallicQuasicrystal.Position(int, long, long)"/>.
/// </remarks>
public readonly record struct QuadraticInflation {
    private QuadraticInflation(long a, long b, long c, long d, int periodLength) {
        A = a;
        B = b;
        C = c;
        D = d;
        PeriodLength = periodLength;
    }

    /// <summary>Gets the top-left entry of the substitution matrix.</summary>
    public long A { get; }
    /// <summary>Gets the top-right entry of the substitution matrix.</summary>
    public long B { get; }
    /// <summary>Gets the bottom-left entry of the substitution matrix.</summary>
    public long C { get; }
    /// <summary>Gets the bottom-right entry of the substitution matrix.</summary>
    public long D { get; }
    /// <summary>Gets the length of the continued-fraction period — the number of factors composed into the matrix.</summary>
    public int PeriodLength { get; }

    /// <summary>Gets the trace <c>A + D</c> — the conjugacy invariant that fixes the inflation factor and, above two, marks the hyperbolic motion.</summary>
    /// <exception cref="OverflowException">The sum of the diagonal exceeds <see cref="long"/>.</exception>
    public long Trace =>
        checked(A + D);
    /// <summary>Gets the determinant <c>A·D − B·C</c>, which is <c>(−1)</c> raised to <see cref="PeriodLength"/>: one for an even period, minus one for an odd period.</summary>
    /// <exception cref="OverflowException">A determinant intermediate exceeds <see cref="long"/>.</exception>
    public long Determinant =>
        checked((A * D) - (B * C));
    /// <summary>Gets the radicand <c>Trace² − 4·Determinant</c> under the inflation factor's surd — <c>5</c> for the golden period and <c>8</c> for the silver period.</summary>
    /// <exception cref="OverflowException">A radicand intermediate exceeds <see cref="long"/>.</exception>
    public long Discriminant =>
        checked((Trace * Trace) - (4L * Determinant));
    /// <summary>Gets the orientation-preserving modular element whose axis is the closed geodesic: the matrix itself for an even period, its square for an odd one, so the determinant is one either way.</summary>
    /// <exception cref="OverflowException">A squared entry exceeds <see cref="long"/>.</exception>
    public ModularTransform Axis =>
        ((Determinant == 1L)
            ? ModularTransform.Create(a: A, b: B, c: C, d: D)
            : ModularTransform.Create(
                a: checked((A * A) + (B * C)),
                b: checked((A * B) + (B * D)),
                c: checked((C * A) + (D * C)),
                d: checked((C * B) + (D * D))
            ));
    /// <summary>Gets the geodesic's conjugacy class, read from <see cref="Axis"/> — always <see cref="ModularClass.Hyperbolic"/> for a genuine quadratic irrational, whose period drives a translation along its axis.</summary>
    public ModularClass GeodesicClass =>
        Axis.Classify();

    /// <summary>Builds the inflation lens of the quadratic irrational <c>(p + q·√d) / r</c> from its continued-fraction period.</summary>
    /// <param name="p">The rational part of the numerator.</param>
    /// <param name="q">The coefficient of the surd; it must be positive.</param>
    /// <param name="d">The radicand; it must be at least two and not a perfect square.</param>
    /// <param name="r">The denominator; it must be non-zero.</param>
    /// <returns>The substitution matrix over the period, with the invariants — trace, determinant, discriminant, geodesic axis, and inflation factor — read off it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="q"/> is not positive, <paramref name="d"/> is below two or a perfect square, or <paramref name="r"/> is zero.</exception>
    /// <exception cref="OverflowException">A partial-quotient product exceeds <see cref="long"/> — the period is too long for the width.</exception>
    public static QuadraticInflation FromQuadraticIrrational(long p, long q, long d, long r) {
        Span<long> terms = stackalloc long[128];
        int periodStart;
        int periodLength;

        while (true) {
            try {
                _ = ContinuedFraction.Expand(
                    p: p,
                    q: q,
                    d: d,
                    r: r,
                    terms: terms,
                    periodStart: out periodStart,
                    periodLength: out periodLength
                );

                break;
            } catch (ArgumentException exception) when ((exception.ParamName == nameof(terms)) && (terms.Length < int.MaxValue)) {
                var nextLength = ((terms.Length <= (int.MaxValue / 2)) ? (terms.Length * 2) : int.MaxValue);

                terms = new long[nextLength];
            }
        }

        // The pre-period is only how the trajectory enters the geodesic; the closed geodesic — and so the inflation — is the
        // product of the period block alone. Compose the partial quotients as convergent matrices [[aᵢ, 1], [1, 0]].
        var matrixA = 1L;
        var matrixB = 0L;
        var matrixC = 0L;
        var matrixD = 1L;

        for (var offset = 0; (offset < periodLength); ++offset) {
            var term = terms[periodStart + offset];

            (matrixA, matrixB) = (checked((matrixA * term) + matrixB), matrixA);
            (matrixC, matrixD) = (checked((matrixC * term) + matrixD), matrixC);
        }

        return new QuadraticInflation(
            a: matrixA,
            b: matrixB,
            c: matrixC,
            d: matrixD,
            periodLength: periodLength
        );
    }

    /// <summary>Returns the inflation factor <c>(Trace + √Discriminant) / 2</c> — the Perron eigenvalue and self-similarity scale of the substitution matrix. For a multi-term period, it is generally not the length ratio of the chain's two tiles.</summary>
    /// <returns>The factor as a fixed-point value; this is the one approximate operation — the trace, determinant, and discriminant above it are exact.</returns>
    public FixedQ4816 InflationFactor() {
        // λ differs from the integral trace by |det(M)|/λ = 1/λ. Above this threshold that correction is below half of
        // one Q48.16 ULP, so the correctly rounded value is the trace itself. At the boundary, the determinant-minus-one
        // root lies just above the trace and rounds back to it; the determinant-plus-one root lies just below by slightly
        // more than half an ULP and still needs the exact path. This also keeps a representable λ available when its
        // squared discriminant exceeds Q48.16's range.
        var trace = Trace;
        if ((trace > (1L << 17)) || ((trace == (1L << 17)) && (Determinant == -1L))) {
            return FixedQ4816.FromInteger(value: trace);
        }

        var root = FixedQ4816.Sqrt(value: FixedQ4816.FromInteger(value: Discriminant));

        return ((FixedQ4816.FromInteger(value: trace) + root) / FixedQ4816.FromInteger(value: 2L));
    }
}

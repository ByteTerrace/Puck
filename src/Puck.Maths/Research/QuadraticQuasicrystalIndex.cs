using System.Numerics;

namespace Puck.Maths;

/// <summary>
/// A compiled exact random-access index for the periodic-tail representative of a quadratic quasicrystal.
/// </summary>
/// <remarks>
/// The index stores the continued-fraction period as a straight-line substitution grammar. Queries descend powers of
/// that grammar using <see cref="BigInteger"/> block lengths; they do not materialize the prefix preceding the requested
/// tile. Consequently a tile or prefix count at index <c>N</c> takes time proportional to the continued-fraction period
/// times the logarithm of <c>N</c> in the inflation scale.
/// </remarks>
public sealed class QuadraticQuasicrystalIndex {
    private readonly long[] m_period;

    private readonly List<SubstitutionLevel> m_levels = [new(
            LongCount: BigInteger.One,
            LongLength: BigInteger.One,
            ShortCount: BigInteger.Zero,
            ShortLength: BigInteger.One
        )];
    private readonly Lock m_levelLock = new();

    internal QuadraticQuasicrystalIndex(long p, long q, long d, long r) {
        m_period = PeriodicBlock(
            d: d,
            p: p,
            q: q,
            r: r
        );
        if (m_period.Any(predicate: term => (term <= 0))) {
            throw new InvalidOperationException(message: "the periodic continued-fraction block must contain positive terms");
        }

        var matrixA = BigInteger.One;
        var matrixB = BigInteger.Zero;
        var matrixC = BigInteger.Zero;
        var matrixD = BigInteger.One;

        foreach (var term in m_period) {
            (matrixA, matrixB) = (((matrixA * term) + matrixB), matrixA);
            (matrixC, matrixD) = (((matrixC * term) + matrixD), matrixC);
        }
        A = matrixA;
        B = matrixB;
        C = matrixC;
        D = matrixD;
        var trace = (A + D);
        var determinant = ((A * D) - (B * C));

        ExactLongTileLength = QuadraticSurd.Create(
            rationalNumerator: (trace - (2 * D)),
            surdNumerator: BigInteger.One,
            radicand: ((trace * trace) - (4 * determinant)),
            denominator: (2 * B)
        );
    }

    /// <summary>Gets the top-left entry of the exact substitution matrix.</summary>
    public BigInteger A { get; }
    /// <summary>Gets the top-right entry of the exact substitution matrix.</summary>
    public BigInteger B { get; }
    /// <summary>Gets the bottom-left entry of the exact substitution matrix.</summary>
    public BigInteger C { get; }
    /// <summary>Gets the bottom-right entry of the exact substitution matrix.</summary>
    public BigInteger D { get; }
    /// <summary>Gets the exact long-tile length when the short tile has length one.</summary>
    public QuadraticSurd ExactLongTileLength { get; }
    /// <summary>Gets the length of the eventually periodic continued-fraction block.</summary>
    public int PeriodLength => m_period.Length;

    private int EnsureDepth(BigInteger index) {
        while (m_levels[^1].LongLength <= index) {
            var previous = m_levels[^1];

            m_levels.Add(item: new SubstitutionLevel(
                LongLength: ((A * previous.LongLength) + (C * previous.ShortLength)),
                ShortLength: ((B * previous.LongLength) + (D * previous.ShortLength)),
                LongCount: ((A * previous.LongCount) + (C * previous.ShortCount)),
                ShortCount: ((B * previous.LongCount) + (D * previous.ShortCount))
            ));
        }
        return (m_levels.Count - 1);
    }
    private LocatedTile Locate(BigInteger index) {
        var depth = EnsureDepth(index: index);
        var letterIsLong = true;
        var offset = index;
        var longBefore = BigInteger.Zero;

        while (depth > 0) {
            var childLevel = m_levels[(depth - 1)];
            var selected = SelectPeriodImage(
                factorIndex: 0,
                seedIsLong: letterIsLong,
                longWeight: childLevel.LongLength,
                shortWeight: childLevel.ShortLength,
                index: offset
            );

            longBefore +=
                ((selected.LongBefore * childLevel.LongCount) +
                (selected.ShortBefore * childLevel.ShortCount));

            letterIsLong = selected.LetterIsLong;
            offset = selected.Offset;
            --depth;
        }

        return new LocatedTile(
            LetterIsLong: letterIsLong,
            LongBefore: longBefore
        );
    }
    private static long[] PeriodicBlock(long p, long q, long d, long r) {
        Span<long> terms = stackalloc long[128];

        while (true) {
            try {
                _ = ContinuedFraction.Expand(
                    d: d,
                    p: p,
                    periodLength: out var periodLength,
                    periodStart: out var periodStart,
                    q: q,
                    r: r,
                    terms: terms
                );
                return terms.Slice(
                    length: periodLength,
                    start: periodStart
                ).ToArray();
            } catch (ArgumentException exception) when (((exception.ParamName == nameof(terms)) && (terms.Length < int.MaxValue))) {
                var nextLength = ((terms.Length <= (int.MaxValue / 2))
                    ? (terms.Length * 2)
                    : int.MaxValue
                );

                terms = new long[nextLength];
            }
        }
    }
    private ImageSelection SelectPeriodImage(
        int factorIndex,
        bool seedIsLong,
        BigInteger longWeight,
        BigInteger shortWeight,
        BigInteger index) {
        if (factorIndex == m_period.Length) {
            var weight = (seedIsLong
                ? longWeight
                : shortWeight
            );

            if (
                (index < BigInteger.Zero) ||
                (index >= weight)
            ) {
                throw new InvalidOperationException(message: "the substitution selector received an out-of-range block index");
            }
            return new ImageSelection(
                LetterIsLong: seedIsLong,
                LongBefore: BigInteger.Zero,
                Offset: index,
                ShortBefore: BigInteger.Zero
            );
        }

        var factor = m_period[factorIndex];
        var inner = SelectPeriodImage(
            factorIndex: (factorIndex + 1),
            index: index,
            longWeight: ((factor * longWeight) + shortWeight),
            seedIsLong: seedIsLong,
            shortWeight: longWeight
        );
        var longBefore = ((factor * inner.LongBefore) + inner.ShortBefore);
        var shortBefore = inner.LongBefore;

        if (!inner.LetterIsLong) {
            return new ImageSelection(
                LetterIsLong: true,
                LongBefore: longBefore,
                Offset: inner.Offset,
                ShortBefore: shortBefore
            );
        }

        var longRunWeight = (factor * longWeight);

        if (inner.Offset < longRunWeight) {
            var longOffset = BigInteger.DivRem(
                dividend: inner.Offset,
                divisor: longWeight,
                remainder: out var remainder
            );

            return new ImageSelection(
                LetterIsLong: true,
                LongBefore: (longBefore + longOffset),
                Offset: remainder,
                ShortBefore: shortBefore
            );
        }

        return new ImageSelection(
            LetterIsLong: false,
            LongBefore: (longBefore + factor),
            Offset: (inner.Offset - longRunWeight),
            ShortBefore: shortBefore
        );
    }

    /// <summary>Counts long tiles in the prefix <c>[0, exclusiveEnd)</c> without enumerating it.</summary>
    public BigInteger CountLongTiles(BigInteger exclusiveEnd) {
        ArgumentOutOfRangeException.ThrowIfNegative(exclusiveEnd);
        if (exclusiveEnd.IsZero) { return BigInteger.Zero; }

        lock (m_levelLock) {
            var location = Locate(index: (exclusiveEnd - BigInteger.One));

            return (location.LongBefore + (location.LetterIsLong
                ? BigInteger.One
                : BigInteger.Zero));
        }
    }
    /// <summary>Returns the exact physical coordinate of the tile at <paramref name="index"/>.</summary>
    public QuadraticSurd PositionAt(BigInteger index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var longCount = CountLongTiles(exclusiveEnd: index);
        var shortCount = (index - longCount);

        return (QuadraticSurd.Rational(value: shortCount) + (QuadraticSurd.Rational(value: longCount) * ExactLongTileLength));
    }
    /// <summary>Returns the tile at a non-negative zero-based index; <see langword="true"/> denotes long.</summary>
    public bool TileAt(BigInteger index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        lock (m_levelLock) {
            var location = Locate(index: index);

            return location.LetterIsLong;
        }
    }

    private readonly record struct SubstitutionLevel(
        BigInteger LongLength,
        BigInteger ShortLength,
        BigInteger LongCount,
        BigInteger ShortCount
    );
    private readonly record struct ImageSelection(
        bool LetterIsLong,
        BigInteger Offset,
        BigInteger LongBefore,
        BigInteger ShortBefore
    );
    private readonly record struct LocatedTile(bool LetterIsLong, BigInteger LongBefore);
}

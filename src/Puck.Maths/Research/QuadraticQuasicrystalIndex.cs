using System.Numerics;
using Puck.Maths.Research;

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
    private readonly long[] period;
    private readonly List<SubstitutionLevel> levels = [new(BigInteger.One, BigInteger.One, BigInteger.One, BigInteger.Zero)];
    private readonly object levelLock = new();

    internal QuadraticQuasicrystalIndex(long p, long q, long d, long r) {
        period = PeriodicBlock(p, q, d, r);
        if (period.Any(term => term <= 0)) {
            throw new InvalidOperationException("the periodic continued-fraction block must contain positive terms");
        }

        var matrixA = BigInteger.One;
        var matrixB = BigInteger.Zero;
        var matrixC = BigInteger.Zero;
        var matrixD = BigInteger.One;
        foreach (var term in period) {
            (matrixA, matrixB) = ((matrixA * term) + matrixB, matrixA);
            (matrixC, matrixD) = ((matrixC * term) + matrixD, matrixC);
        }
        A = matrixA;
        B = matrixB;
        C = matrixC;
        D = matrixD;
        var trace = (A + D);
        var determinant = ((A * D) - (B * C));
        ExactLongTileLength = QuadraticSurd.Create(
            rationalNumerator: trace - (2 * D),
            surdNumerator: BigInteger.One,
            radicand: (trace * trace) - (4 * determinant),
            denominator: 2 * B
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
    /// <summary>Gets the length of the eventually periodic continued-fraction block.</summary>
    public int PeriodLength => period.Length;

    /// <summary>Gets the exact long-tile length when the short tile has length one.</summary>
    public QuadraticSurd ExactLongTileLength { get; }

    /// <summary>Returns the tile at a non-negative zero-based index; <see langword="true"/> denotes long.</summary>
    public bool TileAt(BigInteger index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        lock (levelLock) {
            var location = Locate(index);
            return location.LetterIsLong;
        }
    }

    /// <summary>Counts long tiles in the prefix <c>[0, exclusiveEnd)</c> without enumerating it.</summary>
    public BigInteger CountLongTiles(BigInteger exclusiveEnd) {
        ArgumentOutOfRangeException.ThrowIfNegative(exclusiveEnd);
        if (exclusiveEnd.IsZero) { return BigInteger.Zero; }

        lock (levelLock) {
            var location = Locate(exclusiveEnd - BigInteger.One);
            return location.LongBefore + (location.LetterIsLong ? BigInteger.One : BigInteger.Zero);
        }
    }

    /// <summary>Returns the exact physical coordinate of the tile at <paramref name="index"/>.</summary>
    public QuadraticSurd PositionAt(BigInteger index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        var longCount = CountLongTiles(index);
        var shortCount = (index - longCount);
        return QuadraticSurd.Rational(shortCount) + (QuadraticSurd.Rational(longCount) * ExactLongTileLength);
    }

    private LocatedTile Locate(BigInteger index) {
        var depth = EnsureDepth(index);
        var letterIsLong = true;
        var offset = index;
        var longBefore = BigInteger.Zero;

        while (depth > 0) {
            var childLevel = levels[depth - 1];
            var selected = SelectPeriodImage(
                factorIndex: 0,
                seedIsLong: letterIsLong,
                longWeight: childLevel.LongLength,
                shortWeight: childLevel.ShortLength,
                index: offset
            );
            longBefore +=
                (selected.LongBefore * childLevel.LongCount) +
                (selected.ShortBefore * childLevel.ShortCount);

            letterIsLong = selected.LetterIsLong;
            offset = selected.Offset;
            --depth;
        }

        return new LocatedTile(letterIsLong, longBefore);
    }

    private int EnsureDepth(BigInteger index) {
        while (levels[^1].LongLength <= index) {
            var previous = levels[^1];
            levels.Add(new SubstitutionLevel(
                LongLength: ((A * previous.LongLength) + (C * previous.ShortLength)),
                ShortLength: ((B * previous.LongLength) + (D * previous.ShortLength)),
                LongCount: ((A * previous.LongCount) + (C * previous.ShortCount)),
                ShortCount: ((B * previous.LongCount) + (D * previous.ShortCount))
            ));
        }
        return (levels.Count - 1);
    }

    private ImageSelection SelectPeriodImage(
        int factorIndex,
        bool seedIsLong,
        BigInteger longWeight,
        BigInteger shortWeight,
        BigInteger index) {
        if (factorIndex == period.Length) {
            var weight = seedIsLong ? longWeight : shortWeight;
            if ((index < BigInteger.Zero) || (index >= weight)) {
                throw new InvalidOperationException("the substitution selector received an out-of-range block index");
            }
            return new ImageSelection(seedIsLong, index, BigInteger.Zero, BigInteger.Zero);
        }

        var factor = period[factorIndex];
        var inner = SelectPeriodImage(
            factorIndex + 1,
            seedIsLong,
            ((factor * longWeight) + shortWeight),
            longWeight,
            index
        );
        var longBefore = ((factor * inner.LongBefore) + inner.ShortBefore);
        var shortBefore = inner.LongBefore;

        if (!inner.LetterIsLong) {
            return new ImageSelection(true, inner.Offset, longBefore, shortBefore);
        }

        var longRunWeight = (factor * longWeight);
        if (inner.Offset < longRunWeight) {
            var longOffset = BigInteger.DivRem(inner.Offset, longWeight, out var remainder);
            return new ImageSelection(true, remainder, longBefore + longOffset, shortBefore);
        }

        return new ImageSelection(false, inner.Offset - longRunWeight, longBefore + factor, shortBefore);
    }

    private static long[] PeriodicBlock(long p, long q, long d, long r) {
        Span<long> terms = stackalloc long[128];
        while (true) {
            try {
                _ = ContinuedFraction.Expand(p, q, d, r, terms, out var periodStart, out var periodLength);
                return terms.Slice(periodStart, periodLength).ToArray();
            } catch (ArgumentException exception) when ((exception.ParamName == nameof(terms)) && (terms.Length < int.MaxValue)) {
                var nextLength = (terms.Length <= (int.MaxValue / 2)) ? (terms.Length * 2) : int.MaxValue;
                terms = new long[nextLength];
            }
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

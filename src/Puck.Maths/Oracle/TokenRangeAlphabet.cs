namespace Puck.Maths;

/// <summary>One inclusive run of tokens.</summary>
/// <param name="First">The lowest token the run accepts.</param>
/// <param name="Last">The highest token the run accepts; never below <paramref name="First"/>.</param>
public readonly record struct TokenRange(ulong First, ulong Last);
/// <summary>
/// A canonical set of tokens, packed as ascending disjoint non-adjacent runs. The nontrivial end of the refinement
/// axis: the label space is the whole of <see cref="ulong"/>, so a predicate can never be enumerated and every
/// operation is run arithmetic.
/// </summary>
/// <remarks>The canonical form is forced at construction — sorted, merged, adjacency collapsed — so two sets that
/// accept the same tokens carry the same runs, and a partition built from them is reproduced exactly on any machine.</remarks>
public readonly struct TokenRangeSet {
    /// <summary>The largest number of runs a set may be authored with.</summary>
    public const int MaximumRangeCount = 256;

    private readonly TokenRange[]? m_ranges;

    internal TokenRangeSet(TokenRange[] ranges) =>
        m_ranges = ranges;

    /// <summary>Indicates whether the set accepts no token.</summary>
    public bool IsEmpty =>
        ((m_ranges is null) || (0 == m_ranges.Length));
    /// <summary>Gets the runs, ascending, disjoint and non-adjacent.</summary>
    public ReadOnlySpan<TokenRange> Ranges =>
        m_ranges;

    // Sorts by start, then merges every overlapping or adjacent pair. Adjacency is collapsed because two runs that
    // touch accept the same tokens as one, and a canonical form is what makes the partition reproducible.
    internal static TokenRange[] Canonicalize(TokenRange[] ranges, int count) {
        if (0 == count) { return []; }

        Array.Sort(
            array: ranges,
            comparer: TokenRangeOrder.Instance,
            index: 0,
            length: count
        );

        var written = 0;

        for (var index = 0; (index < count); ++index) {
            var candidate = ranges[index];

            if (0 != written) {
                var previous = ranges[(written - 1)];

                if (
                    (ulong.MaxValue == previous.Last) ||
                    (candidate.First <= (previous.Last + 1UL))
                ) {
                    ranges[(written - 1)] = new(
                        First: previous.First,
                        Last: ((previous.Last < candidate.Last)
                        ? candidate.Last
                        : previous.Last)
                    );

                    continue;
                }
            }

            ranges[written++] = candidate;
        }

        return ranges.AsSpan(
            length: written,
            start: 0
        ).ToArray();
    }

    /// <summary>Creates the canonical set of a run list.</summary>
    /// <param name="ranges">The runs, in any order, allowed to overlap.</param>
    /// <returns>The canonical set.</returns>
    /// <exception cref="ArgumentException">A run ends below where it starts.</exception>
    /// <exception cref="ArgumentOutOfRangeException">More than <see cref="MaximumRangeCount"/> runs were listed.</exception>
    public static TokenRangeSet Create(ReadOnlySpan<TokenRange> ranges) {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            value: ranges.Length,
            other: MaximumRangeCount,
            paramName: nameof(ranges)
        );

        for (var index = 0; (index < ranges.Length); ++index) {
            if (ranges[index].Last < ranges[index].First) {
                throw new ArgumentException(
                    message: "A token run ends below where it starts.",
                    paramName: nameof(ranges)
                );
            }
        }

        return new(ranges: Canonicalize(
            ranges: ranges.ToArray(),
            count: ranges.Length
        ));
    }

    private sealed class TokenRangeOrder : IComparer<TokenRange> {
        internal static readonly TokenRangeOrder Instance = new();

        public int Compare(TokenRange x, TokenRange y) =>
            ((x.First != y.First)
                ? x.First.CompareTo(value: y.First)
                : x.Last.CompareTo(value: y.Last)
            );
    }
}
/// <summary>
/// The packed-run predicate algebra over <see cref="ulong"/> tokens: conjunction is run intersection, complement is
/// the gap walk, and satisfiability is emptiness. An unbounded label space served without enumerating it.
/// </summary>
/// <remarks>Stateless, so one value serves every alphabet. Membership is a binary search over the runs, which is what
/// keeps a per-token classification allocation-free.</remarks>
public readonly struct TokenRangeAlphabet : IAlphabetRefinement<TokenRangeSet> {
    /// <summary>Gets the set of every token.</summary>
    public TokenRangeSet Full =>
        new(ranges: [new(
                First: ulong.MinValue,
                Last: ulong.MaxValue
            )]);

    /// <summary>Returns the set of exactly the tokens this one rejects.</summary>
    /// <param name="predicate">The set to complement.</param>
    /// <returns>The complement over the whole label space.</returns>
    public TokenRangeSet Complement(TokenRangeSet predicate) {
        var ranges = predicate.Ranges;

        if (0 == ranges.Length) { return Full; }

        var gaps = new TokenRange[(ranges.Length + 1)];
        var cursor = ulong.MinValue;
        var open = true;
        var written = 0;

        for (var index = 0; (index < ranges.Length); ++index) {
            var range = ranges[index];

            if (range.First > cursor) {
                gaps[written++] = new(
                    First: cursor,
                    Last: (range.First - 1UL)
                );
            }

            if (ulong.MaxValue == range.Last) {
                open = false;

                break;
            }

            cursor = (range.Last + 1UL);
        }

        if (open) {
            gaps[written++] = new(
                First: cursor,
                Last: ulong.MaxValue
            );
        }

        return new(ranges: gaps.AsSpan(
            length: written,
            start: 0
        ).ToArray());
    }
    /// <summary>Returns the set of exactly the tokens both accept.</summary>
    /// <param name="left">The first set.</param>
    /// <param name="right">The second set.</param>
    /// <returns>The intersection.</returns>
    public TokenRangeSet Conjoin(TokenRangeSet left, TokenRangeSet right) {
        var leftRanges = left.Ranges;
        var rightRanges = right.Ranges;

        if (
            (0 == leftRanges.Length) ||
            (0 == rightRanges.Length)
        ) { return default; }

        var meets = new TokenRange[(leftRanges.Length + rightRanges.Length)];
        var leftIndex = 0;
        var rightIndex = 0;
        var written = 0;

        while (
            (leftIndex < leftRanges.Length) &&
            (rightIndex < rightRanges.Length)
        ) {
            var leftRange = leftRanges[leftIndex];
            var rightRange = rightRanges[rightIndex];
            var first = ((leftRange.First < rightRange.First)
                ? rightRange.First
                : leftRange.First
            );
            var last = ((leftRange.Last < rightRange.Last)
                ? leftRange.Last
                : rightRange.Last
            );

            if (first <= last) {
                meets[written++] = new(
                    First: first,
                    Last: last
                );
            }

            if (leftRange.Last < rightRange.Last) { ++leftIndex; } else { ++rightIndex; }
        }

        return new(ranges: TokenRangeSet.Canonicalize(
            count: written,
            ranges: meets
        ));
    }
    /// <summary>Indicates whether a token falls in a set.</summary>
    /// <param name="predicate">The set.</param>
    /// <param name="token">The token.</param>
    /// <returns><see langword="true"/> when some run covers the token.</returns>
    public bool Contains(TokenRangeSet predicate, ulong token) {
        var ranges = predicate.Ranges;
        var low = 0;
        var high = ranges.Length;

        while (low < high) {
            var middle = ((low + high) >> 1);
            var range = ranges[middle];

            if (token < range.First) {
                high = middle;
            } else if (token > range.Last) {
                low = (middle + 1);
            } else {
                return true;
            }
        }

        return false;
    }
    /// <summary>Indicates whether any token falls in a set.</summary>
    /// <param name="predicate">The set to test.</param>
    /// <returns><see langword="true"/> when the set carries a run.</returns>
    public bool IsSatisfiable(TokenRangeSet predicate) =>
        !predicate.IsEmpty;
    /// <summary>Cuts a set list into the coarsest partition every member is inside or disjoint from.</summary>
    /// <param name="predicates">The sets to refine against.</param>
    /// <param name="minterms">Receives the partition's blocks.</param>
    /// <returns>The number of blocks written, or <c>-1</c> when the partition exceeds the cap or the destination.</returns>
    public int Minterms(ReadOnlySpan<TokenRangeSet> predicates, Span<TokenRangeSet> minterms) =>
        AlphabetRefinement.Refine<TokenRangeSet, TokenRangeAlphabet>(
            minterms: minterms,
            predicates: predicates,
            refinement: this
        );
}

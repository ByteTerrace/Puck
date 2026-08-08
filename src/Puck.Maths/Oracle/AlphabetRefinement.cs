namespace Puck.Maths;

/// <summary>
/// The refinement axis: a predicate algebra over <see cref="ulong"/> tokens that can decide satisfiability and cut
/// itself into a minterm partition. It is the ONE thing the presented kernel cannot express, and it is carried as a
/// second, orthogonal axis rather than smuggled into a presentation.
/// </summary>
/// <typeparam name="TPredicate">The predicate form: a token-index mask, a packed range set, or any other Boolean
/// algebra over tokens.</typeparam>
/// <remarks>
/// <para>
/// <b>Why it is separate.</b> Deciding whether a label predicate is satisfiable, and partitioning an unbounded label
/// space into blocks that every predicate is either inside or disjoint from, is a refinement structure with no
/// convolution shadow — there is no product whose associativity states it. So it is not a presentation and it is not a
/// material; it is an axis whose ONLY output into the algebra is a letter count and a letter mask.
/// </para>
/// <para>
/// <b>The kernel never learns about predicates.</b> A minterm partition of size <c>k</c> becomes
/// <see cref="Presentations.FreeMonoid"/> on <c>k</c> letters, and everything after that is ordinary presented
/// arithmetic. Nothing below <see cref="MintermAlphabet{TPredicate, TRefinement}"/> is generic in
/// <typeparamref name="TPredicate"/>, which is the whole point of declaring the axis rather than widening the kernel.
/// </para>
/// </remarks>
public interface IAlphabetRefinement<TPredicate> {
    /// <summary>Gets the predicate every token of the label space satisfies.</summary>
    TPredicate Full { get; }

    /// <summary>Returns the predicate satisfied by exactly the tokens this one rejects.</summary>
    /// <param name="predicate">The predicate to complement.</param>
    /// <returns>The complement, taken against <see cref="Full"/>.</returns>
    TPredicate Complement(TPredicate predicate);
    /// <summary>Returns the predicate satisfied by exactly the tokens both satisfy.</summary>
    /// <param name="left">The first predicate.</param>
    /// <param name="right">The second predicate.</param>
    /// <returns>The conjunction.</returns>
    TPredicate Conjoin(TPredicate left, TPredicate right);
    /// <summary>Indicates whether a token satisfies a predicate.</summary>
    /// <param name="predicate">The predicate.</param>
    /// <param name="token">The token.</param>
    /// <returns><see langword="true"/> when the token satisfies the predicate; otherwise <see langword="false"/>.</returns>
    /// <remarks>Allocation-free, because a membership run calls it once per token.</remarks>
    bool Contains(TPredicate predicate, ulong token);
    /// <summary>Indicates whether any token satisfies a predicate.</summary>
    /// <param name="predicate">The predicate to test.</param>
    /// <returns><see langword="true"/> when some token satisfies it; otherwise <see langword="false"/>.</returns>
    bool IsSatisfiable(TPredicate predicate);
    /// <summary>Cuts a predicate set into the coarsest partition every member is inside or disjoint from.</summary>
    /// <param name="predicates">The predicates to refine against.</param>
    /// <param name="minterms">Receives the partition's blocks.</param>
    /// <returns>The number of blocks written, or <c>-1</c> when the partition would exceed
    /// <see cref="AlphabetRefinement.MaximumMintermCount"/> or the destination.</returns>
    int Minterms(ReadOnlySpan<TPredicate> predicates, Span<TPredicate> minterms);
}

/// <summary>The shared minterm refinement every predicate algebra runs, and the caps it runs under.</summary>
/// <remarks>The refinement is one loop over the predicate set; a predicate algebra supplies only conjunction,
/// complement and satisfiability. Two algebras that agree on those three agree on their partitions, which is what makes
/// the axis a contract rather than a family of unrelated implementations.</remarks>
public static class AlphabetRefinement {
    /// <summary>The largest partition a refinement may produce, which is the letter cap of the free monoid the
    /// partition becomes.</summary>
    /// <remarks>
    /// <b>This ceiling is a wall, not an envelope.</b> Sixty-four letters is a property of the packing — a letter is a
    /// bit position in the mask the kernel carries — so it bounds the refinement axis outright: at most sixty-four
    /// minterms, and therefore at most sixty-three pairwise-disjoint named predicates, because the tokens satisfying
    /// none of them always survive as a block of their own. Unlike a limit that says only how far something has been
    /// audited, no amount of further verification widens it; only a wider carrier for the mask would.
    /// </remarks>
    public const int MaximumMintermCount = 64;

    /// <summary>Cuts a predicate set into the coarsest partition every member is inside or disjoint from.</summary>
    /// <typeparam name="TPredicate">The predicate form.</typeparam>
    /// <typeparam name="TRefinement">The predicate algebra.</typeparam>
    /// <param name="refinement">The predicate algebra.</param>
    /// <param name="predicates">The predicates to refine against.</param>
    /// <param name="minterms">Receives the partition's blocks.</param>
    /// <returns>The number of blocks written, or <c>-1</c> when the partition would exceed
    /// <see cref="MaximumMintermCount"/> or the destination.</returns>
    /// <remarks>
    /// <para>
    /// It starts from <see cref="IAlphabetRefinement{TPredicate}.Full"/> and splits every surviving block by each
    /// predicate in turn, keeping the satisfiable halves. The block a token satisfying NO predicate falls in survives
    /// as a block of its own, which is what makes a complemented pattern able to match outside every named predicate.
    /// </para>
    /// <para>
    /// <b>The order is a function of the input alone.</b> Blocks are emitted inside-then-outside, in the order the
    /// blocks they were split from were held, so the partition — and therefore the letter numbering, and therefore
    /// every key of every presentation built on it — is reproduced exactly by the same predicate list on any machine.
    /// Nothing here is ordered by a hash.
    /// </para>
    /// </remarks>
    public static int Refine<TPredicate, TRefinement>(TRefinement refinement, ReadOnlySpan<TPredicate> predicates, Span<TPredicate> minterms)
        where TRefinement : struct, IAlphabetRefinement<TPredicate> {
        var blocks = new List<TPredicate> { refinement.Full };
        var split = new List<TPredicate>();

        if (!refinement.IsSatisfiable(predicate: refinement.Full)) { blocks.Clear(); }

        for (var index = 0; (index < predicates.Length); ++index) {
            var predicate = predicates[index];
            var rejected = refinement.Complement(predicate: predicate);

            split.Clear();

            for (var block = 0; (block < blocks.Count); ++block) {
                var inside = refinement.Conjoin(left: blocks[block], right: predicate);

                if (refinement.IsSatisfiable(predicate: inside)) { split.Add(item: inside); }

                var outside = refinement.Conjoin(left: blocks[block], right: rejected);

                if (refinement.IsSatisfiable(predicate: outside)) { split.Add(item: outside); }
            }

            if (split.Count > MaximumMintermCount) { return -1; }

            blocks.Clear();
            blocks.AddRange(collection: split);
        }

        if (blocks.Count > minterms.Length) { return -1; }

        for (var block = 0; (block < blocks.Count); ++block) { minterms[block] = blocks[block]; }

        return blocks.Count;
    }
}

/// <summary>
/// A finite token set as a predicate algebra: the alphabet is a listed set of tokens and a predicate is the subset
/// mask over it. The trivial end of the axis, served day one and needing none of the refinement machinery to be
/// interesting.
/// </summary>
/// <remarks>Tokens are held ascending and distinct, so membership is a binary search and the mask bit of a token is a
/// function of the token alone. At most sixty-four tokens, which is the same cap the partition itself carries.</remarks>
public readonly struct FiniteTokenAlphabet : IAlphabetRefinement<ulong> {
    private readonly ulong[] m_tokens;

    private FiniteTokenAlphabet(ulong[] tokens) =>
        m_tokens = tokens;

    /// <summary>Gets the predicate every listed token satisfies.</summary>
    public ulong Full =>
        MaskOf(count: TokenCount);
    /// <summary>Gets the number of listed tokens.</summary>
    public int TokenCount =>
        ((m_tokens is null) ? 0 : m_tokens.Length);

    /// <summary>Creates the predicate algebra of a listed token set.</summary>
    /// <param name="tokens">The tokens, in any order; repeats are collapsed.</param>
    /// <returns>The described algebra.</returns>
    /// <exception cref="ArgumentOutOfRangeException">More than sixty-four distinct tokens were listed.</exception>
    public static FiniteTokenAlphabet Create(ReadOnlySpan<ulong> tokens) {
        var sorted = tokens.ToArray();

        Array.Sort(array: sorted);

        var distinct = 0;

        for (var index = 0; (index < sorted.Length); ++index) {
            if ((0 != distinct) && (sorted[(distinct - 1)] == sorted[index])) { continue; }

            sorted[distinct++] = sorted[index];
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: distinct, other: AlphabetRefinement.MaximumMintermCount, paramName: nameof(tokens));

        return new(tokens: sorted.AsSpan(start: 0, length: distinct).ToArray());
    }

    /// <summary>Returns the predicate satisfied by exactly the listed tokens this one rejects.</summary>
    /// <param name="predicate">The subset mask to complement.</param>
    /// <returns>The complement within the alphabet.</returns>
    public ulong Complement(ulong predicate) =>
        ~predicate & Full;
    /// <summary>Returns the predicate satisfied by exactly the tokens both satisfy.</summary>
    /// <param name="left">The first subset mask.</param>
    /// <param name="right">The second subset mask.</param>
    /// <returns>The conjunction.</returns>
    public ulong Conjoin(ulong left, ulong right) =>
        left & right;
    /// <summary>Indicates whether a token satisfies a predicate.</summary>
    /// <param name="predicate">The subset mask.</param>
    /// <param name="token">The token.</param>
    /// <returns><see langword="true"/> when the token is listed and its bit is set.</returns>
    public bool Contains(ulong predicate, ulong token) =>
        (TryIndexOf(token: token, index: out var index) && (0UL != (predicate & (1UL << index))));
    /// <summary>Indicates whether any token satisfies a predicate.</summary>
    /// <param name="predicate">The subset mask.</param>
    /// <returns><see langword="true"/> when the mask is nonempty.</returns>
    public bool IsSatisfiable(ulong predicate) =>
        (0UL != (predicate & Full));
    /// <summary>Cuts a predicate set into the coarsest partition every member is inside or disjoint from.</summary>
    /// <param name="predicates">The subset masks to refine against.</param>
    /// <param name="minterms">Receives the partition's blocks.</param>
    /// <returns>The number of blocks written, or <c>-1</c> when the partition exceeds the cap or the destination.</returns>
    public int Minterms(ReadOnlySpan<ulong> predicates, Span<ulong> minterms) =>
        AlphabetRefinement.Refine<ulong, FiniteTokenAlphabet>(refinement: this, predicates: predicates, minterms: minterms);
    /// <summary>Returns the predicate satisfied by exactly a listed token set.</summary>
    /// <param name="tokens">The tokens the predicate accepts; each must be listed.</param>
    /// <returns>The subset mask.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A token is not one this alphabet lists.</exception>
    public ulong Predicate(ReadOnlySpan<ulong> tokens) {
        var mask = 0UL;

        for (var index = 0; (index < tokens.Length); ++index) {
            if (!TryIndexOf(token: tokens[index], index: out var slot)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(tokens), message: "This alphabet does not list that token.");
            }

            mask |= (1UL << slot);
        }

        return mask;
    }
    /// <summary>Returns one listed token.</summary>
    /// <param name="index">The token's index in ascending order.</param>
    /// <returns>The token.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside <see cref="TokenCount"/>.</exception>
    public ulong Token(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: TokenCount);

        return m_tokens[index];
    }

    private static ulong MaskOf(int count) =>
        ((64 == count) ? ulong.MaxValue : ((1UL << count) - 1UL));
    private bool TryIndexOf(ulong token, out int index) {
        var tokens = m_tokens;
        var low = 0;
        var high = ((tokens is null) ? 0 : tokens.Length);

        index = -1;

        while (low < high) {
            var middle = ((low + high) >> 1);
            var probe = tokens![middle];

            if (probe == token) {
                index = middle;

                return true;
            }

            if (probe < token) { low = (middle + 1); } else { high = middle; }
        }

        return false;
    }
}

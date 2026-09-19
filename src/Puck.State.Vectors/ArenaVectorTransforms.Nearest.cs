namespace Puck.State;

/// <summary>A <c>nearest</c>: the top-<c>k</c> keys of a vector table ranked against a query vector.</summary>
/// <param name="IntoRowOrdinal">The destination row's catalog ordinal — a keyed <c>Int</c> or <c>Fixed</c> table
/// taking key and score, or a <c>Text</c> slot taking the winning key's name.</param>
/// <param name="FromRowOrdinal">The source keyed vector table's catalog ordinal.</param>
/// <param name="Query">Where the query vector comes from.</param>
/// <param name="K">How many matches to keep, in <c>1..min(capacity, StateCapacity.MaxNearestResults)</c>.</param>
/// <param name="Threshold">The score a match must reach (or, when <paramref name="Farthest"/>, must not exceed),
/// or <see langword="null"/> to rank every candidate. An <c>Int</c> destination scores by exact dot product; a
/// <c>Fixed</c> or <c>Text</c> destination scores by Q48.16 cosine.</param>
/// <param name="WhereRowOrdinal">A keyed <c>Bool</c> row admitting the candidates to rank, or <c>-1</c> to rank
/// them all.</param>
/// <param name="Exclude">A candidate key to skip, or the invalid default to skip none.</param>
/// <param name="Farthest">Whether to rank by ascending score instead of descending.</param>
public readonly record struct VectorNearestRequest(
    int IntoRowOrdinal,
    int FromRowOrdinal,
    VectorSource Query,
    int K,
    long? Threshold = null,
    int WhereRowOrdinal = -1,
    CellKey Exclude = default,
    bool Farthest = false
);
public static partial class ArenaVectorTransforms {
    /// <summary>Writes the top-<c>k</c> keys of a vector table, ranked against a query vector, into the
    /// destination row.</summary>
    /// <param name="arena">The arena holding the rows.</param>
    /// <param name="request">The pre-resolved operands.</param>
    /// <param name="refusal">Why the transform refused, or the default on success.</param>
    /// <returns><see langword="true"/> when the ranking was written.</returns>
    /// <remarks>A keyed destination is replaced whole: every cell it held is removed before the matches are
    /// minted, so the row names exactly the matches this ranking found and nothing older. A match's score decides
    /// through the destination row's own admission, so a row whose envelope cannot hold a score refuses by
    /// name.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryNearest(StateArena arena, in VectorNearestRequest request, out VectorTransformRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (
            !VectorColumn.TryOpen(
            arena: arena,
            column: out var from,
            refusal: out refusal,
            rowOrdinal: request.FromRowOrdinal
        ) ||
            !TryOpenFilter(
            arena: arena,
            refusal: out refusal,
            whereRowOrdinal: request.WhereRowOrdinal
        )
        ) {
            return false;
        }
        if (from.Shape != RowShape.Keyed) {
            refusal = Refused(
                code: RuleRefusal.VectorNearestShape,
                reason: $"row '{from.RowName()}' must be a keyed Vector table to rank"
            );

            return false;
        }
        if (!TryOpenRanking(
            arena: arena,
            k: request.K,
            layout: out var into,
            refusal: out refusal,
            rowOrdinal: request.IntoRowOrdinal
        )) {
            return false;
        }
        if (!TryResolveSource(
            arena: arena,
            components: out var query,
            dimensions: from.Dimensions,
            refusal: out refusal,
            role: "query",
            source: request.Query
        )) {
            return false;
        }

        var candidateCount = from.Count;

        using var candidatesLease = arena.Scratch.Rent<NearestCandidate>(length: candidateCount);
        using var matchesLease = arena.Scratch.Rent<VectorTransforms.NearestMatch>(length: request.K);

        var candidates = candidatesLease.Span;
        var gathered = 0;

        for (var position = 0; (position < candidateCount); position++) {
            if (
                from.TryKeyAt(
                key: out var key,
                position: position
            ) &&
                from.TryReadMemory(
                components: out var components,
                key: key
            )
            ) {
                candidates[gathered++] = new NearestCandidate(
                    Admitted: Admits(
                        arena: arena,
                        key: key,
                        whereRowOrdinal: request.WhereRowOrdinal
                    ),
                    Components: components,
                    Key: arena.Keys[key: key]
                );
            }
        }

        var matches = matchesLease.Span;
        var matched = VectorTransforms.SelectNearest(
            candidates: candidates[..gathered],
            excludeKey: (arena.Keys.TryGetName(
                key: request.Exclude,
                name: out var excluded
            )
                ? excluded
                : null
            ),
            farthest: request.Farthest,
            isFixedScore: (into.Kind is (CellKind.Fixed or CellKind.Text)),
            k: request.K,
            query: query.Span,
            results: matches,
            threshold: request.Threshold
        );
        var mark = arena.BeginScope();

        if (!TryWriteRanking(
            arena: arena,
            into: into,
            matched: matched,
            matches: matches,
            refusal: out refusal
        )) {
            arena.Rewind(mark: mark);

            return false;
        }

        arena.Commit(mark: mark);

        return true;
    }

    private static bool TryOpenRanking(StateArena arena, int rowOrdinal, int k, out ArenaRowLayout layout, out VectorTransformRefusal refusal) {
        layout = default;

        if (((uint)rowOrdinal) >= ((uint)arena.Layout.RowCount)) {
            refusal = Refused(
                code: RuleRefusal.StateRowUnknown,
                reason: $"row ordinal {rowOrdinal} names no row of this arena"
            );

            return false;
        }

        layout = arena.Layout[rowOrdinal];

        var name = arena.Catalog.Descriptors[rowOrdinal].Name;

        if (layout.HostOwned || !layout.IsStored) {
            refusal = Refused(
                code: RuleRefusal.StateRowUnknown,
                reason: $"row '{name}' stores no cells of its own"
            );

            return false;
        }
        if (layout.Kind is not (CellKind.Int or CellKind.Fixed or CellKind.Text)) {
            refusal = Refused(
                code: RuleRefusal.VectorNearestShape,
                reason: $"row '{name}' stores {layout.Kind}, and a ranking lands in an Int, Fixed, or Text row"
            );

            return false;
        }

        // A Text destination names one winner and has one slot to name it in; an Int or Fixed destination is the
        // keyed table the k matches are minted into, so its declared capacity is what bounds k.
        var capacity = ((layout.Kind == CellKind.Text)
            ? 1
            : layout.CellCapacity
        );

        if (layout.Shape != ((layout.Kind == CellKind.Text)
            ? RowShape.Slot
            : RowShape.Keyed
        )) {
            refusal = Refused(
                code: RuleRefusal.VectorNearestShape,
                reason: $"row '{name}' must be a {((layout.Kind == CellKind.Text) ? "Text slot" : "keyed table")} to take a ranking, not a {layout.Shape} row"
            );

            return false;
        }

        var ceiling = Math.Min(
            val1: capacity,
            val2: StateCapacity.MaxNearestResults
        );

        if ((k < 1) || (k > ceiling)) {
            refusal = Refused(
                code: RuleRefusal.VectorNearestShape,
                reason: $"row '{name}' takes 1 to {ceiling} matches, not {k}"
            );

            return false;
        }

        refusal = default;

        return true;
    }
    private static bool TryWriteRanking(StateArena arena, in ArenaRowLayout into, ReadOnlySpan<VectorTransforms.NearestMatch> matches, int matched, out VectorTransformRefusal refusal) {
        var name = arena.Catalog.Descriptors[into.Ordinal].Name;

        if (into.Kind == CellKind.Text) {
            if (!arena.TryKeyAt(
                key: out var slot,
                position: 0,
                rowOrdinal: into.Ordinal
            )) {
                refusal = Refused(
                    code: RuleRefusal.StateCellUnaddressable,
                    reason: $"row '{name}' holds no slot to name a winner in"
                );

                return false;
            }
            if (!arena.TryWriteText(
                key: slot,
                reason: out var textReason,
                rowOrdinal: into.Ordinal,
                text: ((matched > 0)
                    ? matches[0].Key.Value
                    : string.Empty
                )
            )) {
                refusal = Refused(
                    code: RuleEffectRefusal.MutationRejected,
                    reason: textReason
                );

                return false;
            }

            refusal = default;

            return true;
        }

        while (arena.CellCount(rowOrdinal: into.Ordinal) > 0) {
            if (!arena.TryKeyAt(
                key: out var stale,
                position: 0,
                rowOrdinal: into.Ordinal
            )) {
                refusal = Refused(
                    code: RuleRefusal.StateCellUnaddressable,
                    reason: $"row '{name}' counts a cell at position 0 that carries no key"
                );

                return false;
            }
            if (!arena.TryRemove(
                key: stale,
                reason: out var removeReason,
                rowOrdinal: into.Ordinal
            )) {
                refusal = Refused(
                    code: RuleEffectRefusal.MutationRejected,
                    reason: removeReason
                );

                return false;
            }
        }

        for (var rank = 0; (rank < matched); rank++) {
            var match = matches[rank];

            if (!arena.TryMint(
                key: out _,
                name: match.Key,
                reason: out var mintReason,
                rowOrdinal: into.Ordinal,
                value: ((into.Kind == CellKind.Fixed)
                    ? CellValue.Fixed(rawBits: match.Score)
                    : CellValue.Int(value: match.Score)
                )
            )) {
                refusal = Refused(
                    code: RuleEffectRefusal.MutationRejected,
                    reason: mintReason
                );

                return false;
            }
        }

        refusal = default;

        return true;
    }
}

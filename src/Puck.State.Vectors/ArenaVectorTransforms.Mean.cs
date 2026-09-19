namespace Puck.State;

/// <summary>A <c>mean</c>: the normalized centroid of a vector table, written into one cell.</summary>
/// <param name="IntoRowOrdinal">The destination row's catalog ordinal.</param>
/// <param name="IntoKey">The destination cell key, interned by the arena's catalog.</param>
/// <param name="FromRowOrdinal">The source vector table's catalog ordinal.</param>
/// <param name="WhereRowOrdinal">A keyed <c>Bool</c> row admitting the source cells to average, or <c>-1</c> to
/// average them all.</param>
public readonly record struct VectorMeanRequest(int IntoRowOrdinal, CellKey IntoKey, int FromRowOrdinal, int WhereRowOrdinal = -1);
public static partial class ArenaVectorTransforms {
    /// <summary>Writes the normalized centroid of a vector table's admitted cells into one cell.</summary>
    /// <param name="arena">The arena holding the rows.</param>
    /// <param name="request">The pre-resolved operands.</param>
    /// <param name="refusal">Why the transform refused, or the default on success.</param>
    /// <returns><see langword="true"/> when the mean was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arena"/> is <see langword="null"/>.</exception>
    public static bool TryMean(StateArena arena, in VectorMeanRequest request, out VectorTransformRefusal refusal) {
        ArgumentNullException.ThrowIfNull(argument: arena);

        if (
            !VectorColumn.TryOpen(
            arena: arena,
            column: out var into,
            refusal: out refusal,
            rowOrdinal: request.IntoRowOrdinal
        ) ||
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
        if (from.Dimensions != into.Dimensions) {
            refusal = Refused(
                code: RuleRefusal.VectorSpaceMismatch,
                reason: $"row '{from.RowName()}' stores {from.Dimensions}-dimensional vectors where row '{into.RowName()}' lays out {into.Dimensions}"
            );

            return false;
        }

        var count = from.Count;

        using var gatheredLease = arena.Scratch.Rent<ReadOnlyMemory<sbyte>>(length: count);

        var gathered = gatheredLease.Span;
        var admitted = 0;

        for (var position = 0; (position < count); position++) {
            if (
                from.TryKeyAt(
                key: out var key,
                position: position
            ) &&
                Admits(
                arena: arena,
                key: key,
                whereRowOrdinal: request.WhereRowOrdinal
            ) &&
                from.TryReadMemory(
                components: out var components,
                key: key
            )
            ) {
                gathered[admitted++] = components;
            }
        }

        var candidates = gathered[..admitted];

        if (candidates.Length == 0) {
            refusal = Refused(
                code: RuleRefusal.VectorMeanEmpty,
                reason: $"row '{from.RowName()}' admits no cell to average"
            );

            return false;
        }

        using var destinationLease = arena.Scratch.Rent<sbyte>(length: into.Dimensions);
        using var sumLease = arena.Scratch.Rent<long>(length: into.Dimensions);

        var destination = destinationLease.Span;

        if (!VectorTransforms.TryMean(
            candidates: candidates,
            destination: destination,
            refusal: out var code,
            sum: sumLease.Span
        )) {
            refusal = Refused(
                code: code.Value,
                reason: $"row '{from.RowName()}' averages its {candidates.Length} admitted cells to no direction"
            );

            return false;
        }

        var mark = arena.BeginScope();

        if (!TryAdmitWrite(
            components: destination,
            into: into,
            key: request.IntoKey,
            refusal: out refusal
        )) {
            arena.Rewind(mark: mark);

            return false;
        }

        arena.Commit(mark: mark);

        return true;
    }
}

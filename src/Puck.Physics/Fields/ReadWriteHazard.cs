namespace Puck.Physics.Fields;

/// <summary>The read/write hazard test every field-program scheduler orders its nodes by.</summary>
/// <remarks>Both the compiled lattice plan and the document-side field-program compiler resolve node ordering
/// through this one door, so a dependency edge means the same thing on either side. The sets are compared, never
/// mutated, and the handle types differ per caller — a lattice indexes fields by ordinal, a compiled document by
/// its own handle — so both sides are spans of whatever they already hold.</remarks>
public static class ReadWriteHazard {
    private static bool Intersects<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right) where T : IEquatable<T> {
        for (var leftIndex = 0; (leftIndex < left.Length); leftIndex++) {
            for (var rightIndex = 0; (rightIndex < right.Length); rightIndex++) {
                if (left[leftIndex].Equals(other: right[rightIndex])) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Determines whether a later node must be ordered after an earlier one.</summary>
    /// <typeparam name="TField">The caller's field handle type.</typeparam>
    /// <typeparam name="TState">The caller's state row handle type.</typeparam>
    /// <param name="earlierFieldReads">The earlier node's canonical field-read set.</param>
    /// <param name="earlierFieldWrites">The earlier node's canonical field-write set.</param>
    /// <param name="earlierStateReads">The earlier node's canonical state-read set.</param>
    /// <param name="earlierStateWrites">The earlier node's canonical state-write set.</param>
    /// <param name="laterFieldReads">The later node's canonical field-read set.</param>
    /// <param name="laterFieldWrites">The later node's canonical field-write set.</param>
    /// <param name="laterStateReads">The later node's canonical state-read set.</param>
    /// <param name="laterStateWrites">The later node's canonical state-write set.</param>
    /// <returns><see langword="true"/> when the two nodes carry a read-after-write, write-after-write, or
    /// write-after-read hazard over either the field sets or the state sets.</returns>
    /// <remarks>Simulation-state critical: the six clauses and their order are the dependency contract both
    /// schedulers hash their plans against.</remarks>
    public static bool Conflicts<TField, TState>(
        ReadOnlySpan<TField> earlierFieldReads,
        ReadOnlySpan<TField> earlierFieldWrites,
        ReadOnlySpan<TState> earlierStateReads,
        ReadOnlySpan<TState> earlierStateWrites,
        ReadOnlySpan<TField> laterFieldReads,
        ReadOnlySpan<TField> laterFieldWrites,
        ReadOnlySpan<TState> laterStateReads,
        ReadOnlySpan<TState> laterStateWrites
    )
        where TField : IEquatable<TField>
        where TState : IEquatable<TState> => (
        Intersects(left: earlierFieldWrites, right: laterFieldReads) ||
        Intersects(left: earlierFieldWrites, right: laterFieldWrites) ||
        Intersects(left: earlierFieldReads, right: laterFieldWrites) ||
        Intersects(left: earlierStateWrites, right: laterStateReads) ||
        Intersects(left: earlierStateWrites, right: laterStateWrites) ||
        Intersects(left: earlierStateReads, right: laterStateWrites)
    );
}

namespace Puck.State;

/// <summary>One state cell a compiled rule reads or writes, addressed by ordinal: the row's catalog ordinal and the
/// interned cell key, never a name. An invalid <paramref name="Key"/> means "any cell of the row" — what a read that
/// resolves its key live, a reduction, a push, or a transform touches.</summary>
/// <param name="RowOrdinal">The row's catalog ordinal, which is also its arena row ordinal.</param>
/// <param name="Key">The interned cell key, or the invalid default for any cell of the row.</param>
/// <param name="IsSet">For a write, whether it replaces the cell rather than accumulating into it.</param>
public readonly record struct CellAccess(int RowOrdinal, CellKey Key, bool IsSet = false) {
    /// <summary>Gets a value indicating whether two accesses can touch the same cell.</summary>
    /// <param name="other">The other access.</param>
    /// <returns><see langword="true"/> when both name the same row and neither names a different cell of it.</returns>
    public bool Overlaps(CellAccess other) => ((RowOrdinal == other.RowOrdinal) && (!Key.IsValid || !other.Key.IsValid || (Key == other.Key)));
}
/// <summary>What a compiled fact prices one read or firing against. The rule compiler's own context implements it;
/// a fact needs the capacities and nothing else, so a fact base never names the compiler's whole context.</summary>
public interface IRuleCostContext {
    /// <summary>Returns a row's cell capacity for pricing: its authored capacity, its domain's ceiling, or the
    /// section-wide ceiling for a row the context cannot resolve.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The capacity.</returns>
    long RowCapacity(int rowOrdinal);
}
/// <summary>The root of the compiled-fact hierarchy the rule compiler emits — an operand, a key, or an effect.
/// <see cref="RequiredFacet"/> is a reading of the fact's own declaration: the typed bases answer it from their type
/// argument, and a fact that reads only the arena answers <see langword="null"/>.</summary>
/// <remarks>This is the type <see cref="RuleNeedsBuilder.AddFact"/> takes, so a rule's needs are folded from the
/// facts the walk actually produced rather than from an untyped object.</remarks>
public interface ICompiledFact {
    /// <summary>Gets the facet this fact's type declares, or <see langword="null"/> when it needs none.</summary>
    FacetRef? RequiredFacet { get; }
}

namespace Puck.State;

/// <summary>How a <see cref="CellAccess"/> reaches its row.</summary>
public enum CellReach : byte {
    /// <summary>The cell <see cref="CellAccess.Key"/> names, or any cell of the row when the key is the invalid
    /// default.</summary>
    Key,

    /// <summary>The cell under the key the evaluation binds to <c>$each</c>: one binding reaches one cell.</summary>
    Each,

    /// <summary>The cell a cell under the <c>$each</c> key names (<c>row[index[$each]]</c>): each binding reaches one
    /// cell of the row, and the bindings share its cells.</summary>
    Indexed,

    /// <summary>The row's key set, which a sweep binds, and no cell's value: only a change to the row's members or
    /// their order reaches the access.</summary>
    Members,
}
/// <summary>One state cell a compiled rule reads or writes, addressed by ordinal: the row's catalog ordinal and the
/// interned cell key, never a name. An invalid <paramref name="Key"/> means "any cell of the row" — what a read that
/// resolves its key live, a reduction, a push, or a transform touches.</summary>
/// <param name="RowOrdinal">The row's catalog ordinal, which is also its arena row ordinal.</param>
/// <param name="Key">The interned cell key, or the invalid default for any cell of the row.</param>
/// <param name="IsSet">For a write, whether it replaces the cell rather than accumulating into it.</param>
/// <param name="Reach">How the access reaches the row when no one key names its cell: which of the row's cells
/// moving can change what the access answers.</param>
public readonly record struct CellAccess(int RowOrdinal, CellKey Key, bool IsSet = false, CellReach Reach = CellReach.Key) {
    /// <summary>Gets a value indicating whether the cell is the one under the key bound to <c>$each</c>.</summary>
    public bool Each => (Reach == CellReach.Each);
    /// <summary>Gets a value indicating whether the cell is one a cell under the <c>$each</c> key names.</summary>
    public bool Indexed => (Reach == CellReach.Indexed);
    /// <summary>Gets a value indicating whether the access reads the row's key set and no cell's value.</summary>
    public bool Members => (Reach == CellReach.Members);

    /// <summary>Gets a value indicating whether two accesses can touch the same cell.</summary>
    /// <param name="other">The other access.</param>
    /// <returns><see langword="true"/> when both name the same row and neither names a different cell of it.</returns>
    public bool Overlaps(CellAccess other) => ((RowOrdinal == other.RowOrdinal) && (!Key.IsValid || !other.Key.IsValid || (Key == other.Key)));
}
/// <summary>What a compiled fact prices one read or firing against. The rule compiler's own context implements it;
/// a fact needs the capacities and nothing else, so a fact base never names the compiler's whole context.</summary>
public interface IRuleCostContext {
    /// <summary>Returns the most cells a row can hold, for pricing, read from declarations only (never its cells or
    /// its reserved room), or the section-wide ceiling for a row the context cannot resolve.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal.</param>
    /// <returns>The capacity.</returns>
    long RowCapacity(int rowOrdinal);

    /// <summary>Gets how many bodies a <see cref="RuleFacts.ForEachBodies"/> sweep can visit: the document's body
    /// capacity, or zero for a section no host with bodies runs.</summary>
    long BodyCapacity { get; }
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

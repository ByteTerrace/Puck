using Puck.State.Rules;

namespace Puck.World;

/// <summary>A named cell served by the world's storage through its declared host facet.</summary>
public sealed class WorldHostOwnedCellOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, CellKind valueKind) : WorldFactOperand(valueKind: valueKind), IStateAddressedOperand {
    /// <inheritdoc/>
    public int RowOrdinal { get; } = rowOrdinal;
    /// <summary>Gets the literal key, or invalid for an indirect read.</summary>
    public CellKey Key { get; } = key;
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; } = keyFrom;

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(into);
        into.Add(item: new CellAccess(RowOrdinal: RowOrdinal, Key: ((KeyFrom is null) ? Key : default)));
        CompiledCellRef.CollectReference(into: into, reference: KeyFrom);
    }
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader, IWorldFacts facet) {
        var resolved = RuleReads.ResolveKey(reader: reader, literal: Key, keyFrom: KeyFrom, named: out var named);

        if (!named) { return RuleFact.Absent(kind: ValueKind); }
        var value = ((resolved.IsValid && RuleReads.TryKeyIndex(keys: reader.Arena.Keys, key: resolved, index: out var cell) &&
            (cell >= 0) && (cell <= int.MaxValue) && facet.TryReadHostOwnedCell(RowOrdinal, ((int)cell), out var raw)) ? raw : 0L);

        return RuleFact.Finite(kind: ValueKind, value: value);
    }
}

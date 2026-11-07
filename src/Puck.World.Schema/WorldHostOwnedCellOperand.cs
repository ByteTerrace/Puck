using Puck.State.Rules;

namespace Puck.World;

/// <summary>A named cell served by the world's storage through its declared host facet.</summary>
public sealed class WorldHostOwnedCellOperand : WorldFactOperand, IStateAddressedOperand {
    private readonly OperandAddress[] m_addresses;

    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The host-owned row's catalog ordinal.</param>
    /// <param name="key">The literal key, or invalid for an indirect read.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal key.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    public WorldHostOwnedCellOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, CellKind valueKind) : base(valueKind: valueKind) {
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
        m_addresses = OperandAddress.List(
            first: keyFrom,
            use: AddressUse.Key
        );
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <inheritdoc/>
    public int RowOrdinal { get; }
    /// <summary>Gets the literal key, or invalid for an indirect read.</summary>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(into);
        into.Add(item: new CellAccess(RowOrdinal: RowOrdinal, Key: ((KeyFrom is null) ? Key : default)));
        CompiledCellRef.CollectReference(into: into, reference: KeyFrom);
    }
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet) => ((KeyFrom is null)
        ? Read(
            facet: facet,
            keys: default,
            reader: reader
        )
        : ((IRuleOperand)this).Read(reader: reader));
    /// <inheritdoc/>
    public override Fact Read(IStateReader reader, IWorldFacts facet, ReadOnlySpan<ResolvedKey> keys) {
        var resolved = ((KeyFrom is null)
            ? RuleReads.Literal(literal: Key)
            : RuleReads.Named(key: in keys[0]));

        if (!resolved.TryNamed(value: out var key)) { return Fact.Absent(kind: ValueKind); }
        var value = ((key.IsValid && RuleReads.TryKeyIndex(keys: reader.Arena.Keys, key: key, index: out var cell) &&
            (cell >= 0) && (cell <= int.MaxValue) && facet.TryReadHostOwnedCell(RowOrdinal, ((int)cell), out var raw)) ? raw : 0L);

        return Fact.Finite(kind: ValueKind, value: value);
    }
}

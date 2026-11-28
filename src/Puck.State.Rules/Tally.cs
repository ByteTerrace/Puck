namespace Puck.State.Rules;

/// <summary>The work units of a tally's reads and writes, priced from its declared capacity alone.</summary>
public static class TallyWork {
    /// <summary>The units one write of a tally cell costs: what any state write costs.</summary>
    public const long CellWrite = RuleWorkBudget.DoorUnits;

    /// <summary>Returns the units one probe walk costs at its bound: the two header reads, then a stamp and a key read
    /// for every bucket the walk may visit, then the found count.</summary>
    /// <param name="capacity">The tally's capacity.</param>
    /// <returns>The work units.</returns>
    public static long Walk(int capacity) => ((2L + (2L * StateTally.Probes(capacity: capacity))) + 1L);
}
/// <summary>A count read from a <see cref="StateDomain.Tally"/> row: <c>row[key]</c>, zero for a key the row has not met
/// since its last reset.</summary>
public sealed class TallyOperand : RuleOperand {
    private readonly OperandAddress[] m_addresses;

    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The tally row's catalog ordinal.</param>
    /// <param name="capacity">The row's tally capacity.</param>
    /// <param name="key">The literal key, read when <paramref name="keyFrom"/> is <see langword="null"/>.</param>
    /// <param name="keyFrom">The live key, whose integer the interpreter resolves, or <see langword="null"/>.</param>
    public TallyOperand(int rowOrdinal, int capacity, long key, CompiledCellRef? keyFrom) : base(valueKind: CellKind.Int) {
        Capacity = capacity;
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
        m_addresses = OperandAddress.List(first: keyFrom, use: AddressUse.Index);
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the row's tally capacity.</summary>
    public int Capacity { get; }
    /// <summary>Gets the literal key, read when <see cref="KeyFrom"/> is <see langword="null"/>.</summary>
    public long Key { get; }
    /// <summary>Gets the live key, or <see langword="null"/> for a literal one.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the tally row's catalog ordinal.</summary>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        // A walk reads whichever buckets the key's probe sequence crosses: the whole row.
        into.Add(item: new CellAccess(Key: default, RowOrdinal: RowOrdinal));
        CompiledCellRef.CollectReference(into: into, reference: KeyFrom);
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => TallyWork.Walk(capacity: Capacity);
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) {
        var key = Key;

        if (KeyFrom is not null) {
            if (!keys[0].Named) {
                return Fact.Absent(kind: CellKind.Int);
            }
            key = keys[0].Index;
        }

        var time = frame.Time;
        var probe = StateTally.Find(arena: frame.Arena, capacity: Capacity, key: key, rowOrdinal: RowOrdinal, time: in time);

        return (probe.Case switch {
            TallyProbeCase.Found => Fact.Finite(kind: CellKind.Int, value: probe.Count),
            TallyProbeCase.Free => Fact.Finite(kind: CellKind.Int, value: 0L),
            _ => Fact.Faulted(kind: CellKind.Int),
        });
    }
}
/// <summary>Adds to one key's count in a <see cref="StateDomain.Tally"/> row (<c>row[key] += n</c>), claiming a bucket for
/// a key the row has not met since its last reset; a key past the row's capacity refuses the firing by name.</summary>
public sealed class TallyAddEffect : RuleEffect, IStateAddressedEffect, IValueSourcedEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The tally row's catalog ordinal.</param>
    /// <param name="capacity">The row's tally capacity.</param>
    /// <param name="literalKey">The literal key, read when <paramref name="keyFrom"/> is <see langword="null"/>.</param>
    /// <param name="keyFrom">The live key, or <see langword="null"/>.</param>
    /// <param name="source">The amount added, an Int source.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public TallyAddEffect(int rowOrdinal, int capacity, long literalKey, CompiledCellRef? keyFrom, in CompiledValueSource source, string describe) : base(describe: describe) {
        Capacity = capacity;
        KeyFrom = keyFrom;
        LiteralKey = literalKey;
        RowOrdinal = rowOrdinal;
        Source = source;
    }

    /// <summary>Gets the row's tally capacity.</summary>
    public int Capacity { get; }
    /// <summary>Gets the invalid default: a tally key is an integer, never an interned cell key.</summary>
    public CellKey Key => default;
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the literal key, read when <see cref="KeyFrom"/> is <see langword="null"/>.</summary>
    public long LiteralKey { get; }
    /// <inheritdoc/>
    public int RowOrdinal { get; }
    /// <inheritdoc/>
    public CompiledValueSource Source { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        Source.CollectReads(into: into);
        CompiledCellRef.CollectReference(into: into, reference: KeyFrom);
        into.Add(item: new CellAccess(Key: default, RowOrdinal: RowOrdinal));
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        // Which bucket a new key claims depends on every key claimed before it, so two adds do not commute as cells.
        into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: RowOrdinal));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) =>
        ((TallyWork.Walk(capacity: Capacity) + (4L * TallyWork.CellWrite)) + Source.Cost(context: context));
    /// <inheritdoc/>
    public override long Doors(IRuleCostContext context) => 4L;
}
/// <summary>Empties a <see cref="StateDomain.Tally"/> row (<c>row = 0</c>): one epoch write frees every bucket, however
/// many keys the row held.</summary>
public sealed class TallyResetEffect : RuleEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The tally row's catalog ordinal.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public TallyResetEffect(int rowOrdinal, string describe) : base(describe: describe) => RowOrdinal = rowOrdinal;

    /// <summary>Gets the tally row's catalog ordinal.</summary>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(Key: default, RowOrdinal: RowOrdinal));
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(IsSet: true, Key: default, RowOrdinal: RowOrdinal));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (2L + (2L * TallyWork.CellWrite));
    /// <inheritdoc/>
    public override long Doors(IRuleCostContext context) => 2L;
}

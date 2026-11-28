using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>A declared row's named cell — or, through a live row, the named cell of whichever row the index selects
/// this evaluation (the absent fact when it selects none).</summary>
public sealed class StateCellOperand : RuleOperand, IStateAddressedOperand {
    private readonly OperandAddress[] m_addresses;
    // The arena's cached slot for a literal key of a fixed row; only the arena's planned read makes or reads it.
    private CellReadPlan? m_plan;

    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal, or <c>-1</c> when <paramref name="rowFrom"/> applies.</param>
    /// <param name="key">The literal cell key, or the invalid default when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal key.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    /// <param name="rowFrom">The live row, or <see langword="null"/> for a fixed row.</param>
    /// <param name="positional">Whether the fixed row is one the arena stores by position, a lattice or a ring whose
    /// every in-range position's decimal spelling the catalog interns, so an integer key reads the cell at that
    /// position.</param>
    public StateCellOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, CellKind valueKind, LiveRow? rowFrom = null, bool positional = false) : base(valueKind: valueKind) {
        Key = key;
        KeyFrom = keyFrom;
        RowFrom = rowFrom;
        RowOrdinal = rowOrdinal;
        ReadsByPosition = (positional && (rowFrom is null) && (keyFrom is { } reference) && SpellsPosition(reference: in reference));
        m_addresses = OperandAddress.List(
            first: rowFrom?.Index,
            second: keyFrom,
            secondUse: (ReadsByPosition ? AddressUse.Index : AddressUse.Key),
            use: AddressUse.Index
        );
    }

    // An address whose key is an integer it computes or a pointer cell carries: resolved as that integer, it names
    // nothing exactly when resolved as a key it does, so reading the integer's position answers what reading the key
    // its spelling interns would, without the key table.
    private static bool SpellsPosition(in CompiledCellRef reference) => ((reference.Custom is null) && ((reference.Program is not null) ||
        ((reference.Binding == BoundKey.None) && (reference.RowOrdinal >= 0) && (reference.Kind != CellKind.Text))));

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the literal cell key, or the invalid default when <see cref="KeyFrom"/> applies.</summary>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets a value indicating whether the key address is read as the position of a lattice or ring cell
    /// (<see cref="StateArena.TryReadLiveNumberAt"/>) rather than resolved through the key table.</summary>
    public bool ReadsByPosition { get; }
    /// <summary>Gets the live row, or <see langword="null"/> for a fixed row.</summary>
    public LiveRow? RowFrom { get; }
    /// <inheritdoc/>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (RowFrom is { } live) {
            live.CollectCellReads(into: into, key: KeyFrom);
        } else {
            into.Add(item: new CellAccess(
                Key: ((KeyFrom is null)
                ? Key
                : default),
                Reach: (KeyFrom?.KeyReach ?? CellReach.Key),
                RowOrdinal: RowOrdinal
            ));
        }

        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) {
        if (ReadsByPosition) {
            return RuleReads.ReadPosition(
                frame: ref frame,
                kind: ValueKind,
                position: in keys[0],
                rowOrdinal: RowOrdinal
            );
        }

        var next = 0;
        var ordinal = RowOrdinal;

        if (RowFrom is { } live) {
            if (!live.Select(each: (live.SelectsByBinding ? RuleReads.BoundOwnIndex(reader: frame.Reader) : null), index: in keys[next++]).TryNamed(value: out ordinal)) {
                return Fact.Absent(kind: ValueKind);
            }
        } else if ((KeyFrom is null) && Key.IsValid && (ordinal >= 0)) {
            var time = frame.Time;

            return Fact.Finite(
                kind: ValueKind,
                value: (frame.Arena.TryReadPlannedNumber(
                    key: Key,
                    plan: ref m_plan,
                    rowOrdinal: ordinal,
                    time: in time,
                    value: out var value
                )
                    ? value
                    : 0L)
            );
        }

        return RuleReads.ReadCell(
            frame: ref frame,
            kind: ValueKind,
            resolved: ((KeyFrom is null)
                ? RuleReads.Literal(literal: Key)
                : RuleReads.Named(key: in keys[next])),
            rowOrdinal: ordinal
        );
    }
}
/// <summary>A value the enclosing rule bound for this evaluation (<see cref="RuleFacts.LocalPrefix"/>).</summary>
public sealed class LocalOperand : RuleOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="ordinal">The binding's slot in the evaluation's bound-value scratch.</param>
    /// <param name="name">The authored binding name, for the read-back.</param>
    /// <param name="valueKind">The kind of the binding's result.</param>
    /// <param name="source">The referenced binding's own compiled form, or <see langword="null"/>.</param>
    public LocalOperand(int ordinal, string name, CellKind valueKind, CompiledRuleLocal? source = null) : base(valueKind: valueKind) {
        Name = name;
        Ordinal = ordinal;
        Source = source;
    }

    /// <summary>Gets the authored binding name.</summary>
    public string Name { get; }
    /// <summary>Gets the binding's slot in the evaluation's bound-value scratch.</summary>
    public int Ordinal { get; }
    /// <summary>Gets the referenced binding's own compiled form, so a read chained through <c>$local:</c> folds the
    /// whole chain it rests on.</summary>
    public CompiledRuleLocal? Source { get; }

    /// <summary>Returns whether the binding is a compile-time constant: the constant folder reduced its program to
    /// one constant, so every evaluation binds that value and a read of it is that literal.</summary>
    /// <param name="raw">The constant, in this operand's <see cref="RuleOperand.ValueKind"/> encoding.</param>
    /// <returns><see langword="true"/> for a numeric binding whose program is one constant. A Bool binding never
    /// answers one, since its raw value is read through a conversion a literal skips.</returns>
    public bool TryConstant(out long raw) {
        if ((ValueKind is CellKind.Int or CellKind.Fixed) && (Source?.Expression is [{ Operation: ExpressionOp.Constant } constant])) {
            raw = constant.Constant;

            return true;
        }

        raw = 0L;

        return false;
    }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        if (Source is { } source) {
            into.AddRange(collection: source.Reads);
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) => Fact.Finite(
        kind: ValueKind,
        value: frame.Reader.LocalValue(ordinal: Ordinal)
    );
}
/// <summary>A static table entry (<see cref="RuleFacts.TablePrefix"/>). A key the table does not carry names no
/// entry and reads as the absent fact: an expression over it refuses and a gate over it never holds.</summary>
public sealed class TableOperand : RuleOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="table">The compiled table.</param>
    /// <param name="name">The table's authored name, for the read-back.</param>
    /// <param name="key">The literal key, or 0 when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal key.</param>
    /// <param name="column">The column index; 0 for a single-value table.</param>
    /// <param name="valueKind">The table's value kind.</param>
    public TableOperand(CompiledTable table, string name, long key, CompiledCellRef? keyFrom, int column, CellKind valueKind) : base(valueKind: valueKind) {
        ArgumentNullException.ThrowIfNull(argument: table);

        Column = column;
        Key = key;
        KeyFrom = keyFrom;
        Name = name;
        Table = table;
        m_addresses = OperandAddress.List(
            first: keyFrom,
            use: AddressUse.Index
        );
    }

    private readonly OperandAddress[] m_addresses;

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the column index.</summary>
    public int Column { get; }
    /// <summary>Gets the literal key.</summary>
    public long Key { get; }
    /// <summary>Gets the live key indirection, or <see langword="null"/> for a literal key.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the table's authored name.</summary>
    public string Name { get; }
    /// <summary>Gets the compiled table.</summary>
    public CompiledTable Table { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: KeyFrom
    );
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (2L + System.Numerics.BitOperations.Log2(value: ((uint)Math.Max(
        val1: Table.Count,
        val2: 1
    ))));
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) {
        var key = Key;

        if (KeyFrom is not null) {
            if (!keys[0].Named) {
                return Fact.Absent(kind: ValueKind);
            }

            key = keys[0].Index;
        }

        return (Table.TryLookup(
            column: Column,
            key: key,
            raw: out var raw
        )
            ? Fact.Finite(
                kind: ValueKind,
                value: raw
            )
            : Fact.Absent(kind: ValueKind)
        );
    }
}
/// <summary>The completed-tick counter (<see cref="RuleFacts.Tick"/>). Stateless: every read shares
/// <see cref="Instance"/>.</summary>
public sealed class TickOperand : RuleOperand {
    /// <summary>The shared instance.</summary>
    public static readonly TickOperand Instance = new();

    private TickOperand() : base(valueKind: CellKind.Int) { }

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) => Fact.Finite(
        kind: CellKind.Int,
        value: unchecked((long)frame.Reader.Tick)
    );
}
/// <summary>A numeric aggregate over a row's cells (<see cref="RuleFacts.ReducePrefix"/>). Count is always integer
/// regardless of the row's declared kind; Max/Min/Sum preserve the row's kind. An empty row reads as zero for every
/// op.</summary>
public sealed class ReductionOperand : RuleOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The aggregated row's catalog ordinal, or <c>-1</c> when <paramref name="rowFrom"/>
    /// applies.</param>
    /// <param name="reduce">The aggregate.</param>
    /// <param name="filterOrdinal">The filter row's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="domainOrdinal">The token domain row's catalog ordinal, for an arrangement rank; <c>-1</c>
    /// otherwise.</param>
    /// <param name="valueKind">Int for <see cref="StateReduceOp.Count"/>, else the aggregated row's own kind.</param>
    /// <param name="range">Optional inclusive bounds in the source row's raw numeric encoding.</param>
    /// <param name="rowFrom">The live row aggregated, or <see langword="null"/> for a fixed row.</param>
    public ReductionOperand(int rowOrdinal, StateReduceOp reduce, int filterOrdinal, int domainOrdinal, CellKind valueKind, (long Lower, long Upper)? range = null, LiveRow? rowFrom = null) : base(valueKind: valueKind) {
        DomainOrdinal = domainOrdinal;
        FilterOrdinal = filterOrdinal;
        Range = range;
        Reduce = reduce;
        RowFrom = rowFrom;
        RowOrdinal = rowOrdinal;
        m_addresses = OperandAddress.List(
            first: rowFrom?.Index,
            use: AddressUse.Index
        );
    }

    private readonly OperandAddress[] m_addresses;

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the token domain row's catalog ordinal, for an arrangement rank; <c>-1</c> otherwise.</summary>
    public int DomainOrdinal { get; }
    /// <summary>Gets the filter row's catalog ordinal, or <c>-1</c>.</summary>
    public int FilterOrdinal { get; }
    /// <summary>Gets the optional inclusive bounds applied to each candidate's live raw value.</summary>
    public (long Lower, long Upper)? Range { get; }
    /// <summary>Gets the aggregate.</summary>
    public StateReduceOp Reduce { get; }
    /// <summary>Gets the live row aggregated, or <see langword="null"/> for a fixed row.</summary>
    public LiveRow? RowFrom { get; }
    /// <summary>Gets the aggregated row's catalog ordinal, or <c>-1</c> for a live row.</summary>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        LiveRow.CollectWholeRowReads(
            into: into,
            live: RowFrom,
            rowOrdinal: RowOrdinal
        );
        if (FilterOrdinal >= 0) {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: FilterOrdinal
            ));
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (((long)((RowFrom is { } live)
        ? live.SelectionCapacity(context: context)
        : context.RowCapacity(rowOrdinal: RowOrdinal))) * RuleWork.Known(units: ((Range is null)
            ? 1L
            : 3L
        )));
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) {
        var ordinal = RowOrdinal;

        if (RowFrom is { } live) {
            if (!live.Select(each: (live.SelectsByBinding ? RuleReads.BoundOwnIndex(reader: frame.Reader) : null), index: in keys[0]).TryNamed(value: out ordinal)) {
                return Fact.Absent(kind: ValueKind);
            }
        }
        if (ordinal < 0) {
            return Fact.Finite(
                kind: ValueKind,
                value: 0L
            );
        }
        if (Reduce == StateReduceOp.ArrangementRank) {
            return Fact.Finite(
                kind: ValueKind,
                value: RuleReads.ArrangementRank(
                    domainOrdinal: DomainOrdinal,
                    reader: frame.Reader,
                    rowOrdinal: ordinal
                )
            );
        }

        return Fact.Finite(
            kind: ValueKind,
            value: RuleReads.Reduce(
                filterOrdinal: FilterOrdinal,
                frame: ref frame,
                op: Reduce,
                range: Range,
                rowOrdinal: ordinal
            )
        );
    }
}
/// <summary>A key's zero-based position among a row's cells in their order (<see cref="StateReduceOp.Position"/>), read
/// from the arena's position view (<see cref="StateArena.TryPositionOf"/>): one lookup per read, the view itself
/// derived once per row generation. A key the row does not hold reads <c>-1</c>; an address that names no cell reads
/// absent, and one whose computation faulted reads faulted.</summary>
public sealed class PositionOperand : RuleOperand, IStateAddressedOperand {
    private readonly OperandAddress[] m_addresses;

    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal, or <c>-1</c> when <paramref name="rowFrom"/> applies.</param>
    /// <param name="key">The literal key whose position is read, or the invalid default when
    /// <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal key.</param>
    /// <param name="rowFrom">The live row, or <see langword="null"/> for a fixed row.</param>
    public PositionOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, LiveRow? rowFrom = null) : base(valueKind: CellKind.Int) {
        Key = key;
        KeyFrom = keyFrom;
        RowFrom = rowFrom;
        RowOrdinal = rowOrdinal;
        m_addresses = OperandAddress.List(
            first: rowFrom?.Index,
            second: keyFrom,
            secondUse: AddressUse.Key,
            use: AddressUse.Index
        );
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the literal key, or the invalid default when <see cref="KeyFrom"/> applies.</summary>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the live row, or <see langword="null"/> for a fixed row.</summary>
    public LiveRow? RowFrom { get; }
    /// <inheritdoc/>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        // A position moves when any cell of the row is added, removed or reordered, so the read spans the row.
        LiveRow.CollectWholeRowReads(
            into: into,
            live: RowFrom,
            rowOrdinal: RowOrdinal
        );

        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) {
        var next = 0;
        var ordinal = RowOrdinal;

        if (RowFrom is { } live) {
            if (!live.Select(each: (live.SelectsByBinding ? RuleReads.BoundOwnIndex(reader: frame.Reader) : null), index: in keys[next++]).TryNamed(value: out ordinal)) {
                return Fact.Absent(kind: ValueKind);
            }
        }

        var resolved = ((KeyFrom is null)
            ? RuleReads.Literal(literal: Key)
            : RuleReads.Named(key: in keys[next]));

        if (!resolved.TryNamed(value: out var key)) {
            return ((resolved.Case == FaultCase.Faulted)
                ? Fact.Faulted(kind: ValueKind)
                : Fact.Absent(kind: ValueKind));
        }

        return Fact.Finite(
            kind: ValueKind,
            value: (((ordinal >= 0) && key.IsValid && frame.Arena.TryPositionOf(
                key: key,
                position: out var position,
                rowOrdinal: ordinal
            ))
                ? position
                : -1L)
        );
    }
}
/// <summary>One value of a history ring by age (<see cref="RuleFacts.HistoryPrefix"/>).</summary>
public sealed class HistoryOperand : RuleOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The history row's catalog ordinal.</param>
    /// <param name="age">0 is the latest push; an age the ring no longer holds reads the trait's empty value. Read
    /// only when <paramref name="ageProgram"/> is <see langword="null"/>.</param>
    /// <param name="ring">The row's ring domain.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    /// <param name="ageProgram">The int program the age is computed by per evaluation, or
    /// <see langword="null"/> for a constant age. It is lowered into the enclosing program, and the age arrives as
    /// this operand's one address.</param>
    public HistoryOperand(int rowOrdinal, long age, StateDomain.Ring ring, CellKind valueKind, CompiledProgram? ageProgram = null) : base(valueKind: valueKind) {
        ArgumentNullException.ThrowIfNull(argument: ring);

        Age = age;
        AgeFrom = ((ageProgram is null)
            ? null
            : new CompiledCellRef(
                Key: default,
                Program: ageProgram,
                RowOrdinal: -1
            ));
        Ring = ring;
        RowOrdinal = rowOrdinal;
        m_addresses = OperandAddress.List(
            first: AgeFrom,
            use: AddressUse.Index
        );
    }

    private readonly OperandAddress[] m_addresses;

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the age: 0 is the latest push.</summary>
    public long Age { get; }
    /// <summary>Gets the address the age is computed through per evaluation, or <see langword="null"/> for a
    /// constant age.</summary>
    public CompiledCellRef? AgeFrom { get; }
    /// <summary>Gets the row's ring domain.</summary>
    public StateDomain.Ring Ring { get; }
    /// <summary>Gets the history row's catalog ordinal.</summary>
    public int RowOrdinal { get; }

    /// <summary>Reads the slot pushed <paramref name="age"/> pushes ago: (cursor - 1 - age) mod capacity, since the
    /// ring's cells are its slots in order; a slot never written, and an age outside 0..capacity-1, read the empty
    /// value.</summary>
    /// <param name="arena">The arena.</param>
    /// <param name="rowOrdinal">The history row's catalog ordinal.</param>
    /// <param name="ring">The row's ring domain.</param>
    /// <param name="age">The age.</param>
    /// <returns>The slot's raw value.</returns>
    public static long ReadSlot(StateArena arena, int rowOrdinal, StateDomain.Ring ring, long age) {
        ArgumentNullException.ThrowIfNull(argument: arena);
        ArgumentNullException.ThrowIfNull(argument: ring);

        var cursor = arena.HistoryCursor(rowOrdinal: rowOrdinal);

        if (
            (age < 0L) ||
            (age >= Math.Min(
            val1: cursor,
            val2: ring.Capacity
        ))
        ) {
            return ring.Empty;
        }

        return (arena.TryReadRawAt(
            position: ((int)(((cursor - 1L) - age) % ring.Capacity)),
            raw: out var value,
            rowOrdinal: rowOrdinal
        )
            ? value
            : ring.Empty
        );
    }
    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: RowOrdinal
        ));
        CompiledCellRef.CollectReference(
            into: into,
            reference: AgeFrom
        );
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) {
        var age = Age;

        // An age that names nothing selects no slot, which the ring answers with its empty value the same way an age
        // past what it holds does.
        if (AgeFrom is not null) {
            age = (keys[0].Named
                ? keys[0].Index
                : -1L);
        }

        return Fact.Finite(
            kind: ValueKind,
            value: ReadSlot(
                age: age,
                arena: frame.Arena,
                ring: Ring,
                rowOrdinal: RowOrdinal
            )
        );
    }
}
/// <summary>A phase protocol progression value (the <c>$phase:</c> channel) — the row's own generation, the same
/// value a <see cref="PhaseGuard"/> checks against it.</summary>
public sealed class PhaseOperand : RuleOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The phase row's catalog ordinal.</param>
    public PhaseOperand(int rowOrdinal) : base(valueKind: CellKind.Int) => RowOrdinal = rowOrdinal;

    /// <summary>Gets the phase row's catalog ordinal.</summary>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: RowOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) => Fact.Finite(
        kind: CellKind.Int,
        value: frame.Arena.PhaseSequence(rowOrdinal: RowOrdinal)
    );
}
/// <summary>A reserved channel read whose argument names a family member under the binding's own key
/// (<c>board(mask, board[$member], 1, 6)</c>, <c>physics(quiescent, pieces[$member])</c>): the channel compiled once per
/// member with that member's row in the argument, and the binding's member's read answered. A family index the sweep
/// binds that selects no member reads absent.</summary>
/// <remarks>Every member's operand is compiled from one spelling that differs only in the row, so they declare the same
/// addresses and are read with the same resolved keys. A read one member makes and the others do not reaches only the
/// bindings of that member (<see cref="CellReach.Member"/>).</remarks>
public sealed class MemberOperand : RuleOperand, ICompositeFact {
    private readonly IRuleOperand?[] m_members;
    private readonly OperandAddress[] m_addresses;

    /// <summary>Initializes the operand.</summary>
    /// <param name="members">Each family index's operand, <see langword="null"/> at a gap.</param>
    /// <param name="valueKind">The kind every member answers in.</param>
    public MemberOperand(IRuleOperand?[] members, CellKind valueKind) : base(valueKind: valueKind) {
        ArgumentNullException.ThrowIfNull(argument: members);

        m_members = members;
        m_addresses = (Array.Find(array: members, match: static member => (member is not null))?.Addresses.ToArray() ?? []);
    }

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets each family index's operand, <see langword="null"/> at a gap.</summary>
    public IReadOnlyList<IRuleOperand?> Members => m_members;
    /// <inheritdoc/>
    public IReadOnlyList<ICompiledFact?> Parts => m_members;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        var each = new List<CellAccess>[m_members.Length];
        List<CellAccess>? shared = null;

        for (var index = 0; (index < m_members.Length); index++) {
            if (m_members[index] is not { } member) {
                continue;
            }

            member.CollectReads(into: (each[index] = []));
            shared = ((shared is null) ? [.. each[index]] : shared.FindAll(match: each[index].Contains));
        }
        foreach (var access in (shared ?? [])) {
            into.Add(item: access);
        }
        for (var index = 0; (index < each.Length); index++) {
            foreach (var access in (each[index] ?? [])) {
                if (!shared!.Contains(item: access)) {
                    into.Add(item: (access.Key.IsValid ? access : (access with { Member = index, Reach = CellReach.Member })));
                }
            }
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        var cost = RuleWork.Zero;

        foreach (var member in m_members) {
            if (member is not null) {
                cost = RuleWork.Max(left: cost, right: member.Cost(context: context));
            }
        }

        return cost;
    }
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) => ((RuleReads.BoundOwnIndex(reader: frame.Reader) is { } index) &&
        (((ulong)index) < ((ulong)m_members.Length)) && (m_members[index] is { } member)
        ? member.Read(frame: ref frame, keys: keys)
        : Fact.Absent(kind: ValueKind));
}
/// <summary>A bounded discrete topology query over a board row (the <c>$board:</c> channel).</summary>
public sealed class BoardOperand : RuleOperand, IStateAddressedOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The board row's catalog ordinal.</param>
    /// <param name="key">The literal source cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="board">The compiled query.</param>
    /// <param name="targetFrom">The live destination indirection a <c>pathCost</c> or <c>jumpDistance</c> query
    /// reads every evaluation. A jump destination is an in-range integer ordinal; <see langword="null"/> selects a
    /// compile-time target.</param>
    public BoardOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, BoardQuery board, CompiledCellRef? targetFrom = null) : base(valueKind: CellKind.Int) {
        ArgumentNullException.ThrowIfNull(argument: board);

        Board = board;
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
        TargetFrom = targetFrom;
        m_addresses = OperandAddress.List(
            first: keyFrom,
            use: AddressUse.Key
        );
    }

    private readonly OperandAddress[] m_addresses;

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the compiled query.</summary>
    public BoardQuery Board { get; }
    /// <summary>Gets the live destination indirection a <c>pathCost</c> or <c>jumpDistance</c> query reads. A jump
    /// destination must be an in-range integer ordinal, or this is <see langword="null"/> for a compile-time target.</summary>
    public CompiledCellRef? TargetFrom { get; }
    /// <summary>Gets the literal source cell key, or the invalid default.</summary>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: RowOrdinal
        ));
        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
        CompiledCellRef.CollectReference(
            into: into,
            reference: TargetFrom
        );
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) =>
        Board.Visits + (Board.ReadsWholeBoard ? Board.Topology.CellCount : 0L);
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) {
        var reader = frame.Reader;
        var arena = frame.Arena;
        var key = ((KeyFrom is null)
            ? Key
            : keys[0].Key);
        var origin = -1;

        if (
            key.IsValid &&
            arena.Keys.TryGetName(
            key: key,
            name: out var name
        )
        ) {
            _ = Board.Topology.TryCell(
                cell: out origin,
                key: name.Value
            );
        }

        if (Board is BoardOffsetQuery offset) {
            return Fact.Finite(
                kind: CellKind.Int,
                value: (((origin >= 0) && Board.Topology.TryOffset(
                    origin,
                    offset.Dx,
                    offset.Dz,
                    out var moved
                ))
                ? moved
                : -1L)
            );
        }

        var dynamicTarget = 0;

        if (TargetFrom is { } targetFrom) {
            if (Board is BoardJumpDistanceQuery) {
                var time = frame.Time;

                if (!arena.TryReadLiveNumber(
                    key: targetFrom.Key,
                    rowOrdinal: targetFrom.RowOrdinal,
                    time: in time,
                    value: out var target
                ) || (target < 0L) || (target >= Board.Topology.CellCount)) {
                    return Fact.Finite(
                        kind: CellKind.Int,
                        value: -1L
                    );
                }
                dynamicTarget = ((int)target);
            } else {
                dynamicTarget = ((int)(RuleReads.ReadFixed(
                    key: targetFrom.Key,
                    reader: reader,
                    rowOrdinal: targetFrom.RowOrdinal
                ).Value >> FixedQ4816.FractionBitCount));
            }
        }

        using var values = reader.Scratch.Rent<long>(length: Board.ReadsWholeBoard ? Board.Topology.CellCount : 0);

        return Fact.Finite(
            kind: CellKind.Int,
            value: (ArenaBoards.TryQuery(
                arena: arena,
                dynamicTarget: dynamicTarget,
                query: Board,
                reason: out _,
                result: out var result,
                rowOrdinal: RowOrdinal,
                source: origin,
                values: values.Span
            )
            ? result
            : -1L)
        );
    }
}
/// <summary>A <see cref="RuleFacts.SymmetryPrefix"/> read: a cell's node through one symmetry-lattice map. The
/// source cell's whole part is the node; a cell holding no node reads the neutral value — <c>-1</c> for the
/// node-valued maps, 0 for orthogonal, the inner product, and the projections.</summary>
public sealed class SymmetryOperand : RuleOperand, IStateAddressedOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The source row's catalog ordinal.</param>
    /// <param name="key">The literal source cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="symmetry">The lattice map applied to the source node.</param>
    /// <param name="symmetryArgument">The literal argument, when <paramref name="symmetryOtherCell"/> is
    /// <see langword="null"/>.</param>
    /// <param name="symmetryOtherCell">The cell the other node is read from live, or <see langword="null"/>.</param>
    /// <param name="valueKind">Fixed for the two projection functions, else Int.</param>
    public SymmetryOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, SymmetryFunction symmetry, long symmetryArgument, CompiledCellRef? symmetryOtherCell, CellKind valueKind) : base(valueKind: valueKind) {
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
        Symmetry = symmetry;
        SymmetryArgument = symmetryArgument;
        SymmetryOtherCell = symmetryOtherCell;
        m_addresses = OperandAddress.List(
            first: keyFrom,
            use: AddressUse.Key
        );
    }

    private readonly OperandAddress[] m_addresses;

    /// <inheritdoc/>
    public override ReadOnlySpan<OperandAddress> Addresses => m_addresses;
    /// <summary>Gets the literal source cell key, or the invalid default.</summary>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public int RowOrdinal { get; }
    /// <summary>Gets the lattice map applied to the source node.</summary>
    public SymmetryFunction Symmetry { get; }
    /// <summary>Gets the literal argument, when <see cref="SymmetryOtherCell"/> is <see langword="null"/>.</summary>
    public long SymmetryArgument { get; }
    /// <summary>Gets the cell the other node is read from live, or <see langword="null"/>.</summary>
    public CompiledCellRef? SymmetryOtherCell { get; }

    // A fact's whole part as a lattice node, or -1 when it names none.
    private static int NodeOf(FixedQ4816 value) {
        var whole = (value.Value >> FixedQ4816.FractionBitCount);

        return (((whole < 0L) || (whole >= SymmetryLattice.NodeCount))
            ? -1
            : ((int)whole)
        );
    }
    private FixedQ4816 ReadFixed(IStateReader reader, CellKey key) {
        var node = NodeOf(value: RuleReads.ReadFixed(
            key: key,
            reader: reader,
            rowOrdinal: RowOrdinal
        ));
        var other = ((SymmetryOtherCell is { } otherCell)
            ? NodeOf(value: RuleReads.ReadFixed(
                key: otherCell.Key,
                reader: reader,
                rowOrdinal: otherCell.RowOrdinal
            ))
            : ((int)Math.Clamp(
                max: (SymmetryLattice.NodeCount - 1L),
                min: -1L,
                value: SymmetryArgument
            ))
        );
        var neutral = ((Symmetry is SymmetryFunction.Orthogonal or SymmetryFunction.InnerProduct or SymmetryFunction.ProjectionX or SymmetryFunction.ProjectionY)
            ? 0L
            : -1L
        );

        if (
            (node < 0) ||
            ((Symmetry is SymmetryFunction.Reflect or SymmetryFunction.Orthogonal or SymmetryFunction.InnerProduct) && (other < 0))
        ) {
            return FixedQ4816.FromInteger(value: neutral);
        }

        return Symmetry switch {
            SymmetryFunction.Ring => FixedQ4816.FromInteger(value: SymmetryLattice.Ring(node: node)),
            SymmetryFunction.Antipode => FixedQ4816.FromInteger(value: SymmetryLattice.Antipode(node: node)),
            SymmetryFunction.CanonicalRay => FixedQ4816.FromInteger(value: SymmetryLattice.CanonicalRay(node: node)),
            SymmetryFunction.Cycle => FixedQ4816.FromInteger(value: SymmetryLattice.Cycle(
            node: node,
            steps: SymmetryArgument
        )),
            SymmetryFunction.Reflect => FixedQ4816.FromInteger(value: SymmetryLattice.Reflect(
            mirror: other,
            node: node
        )),
            SymmetryFunction.Orthogonal => FixedQ4816.FromInteger(value: (SymmetryLattice.AreOrthogonal(
            first: node,
            second: other
        )
            ? 1L
            : 0L)),
            SymmetryFunction.InnerProduct => FixedQ4816.FromInteger(value: SymmetryLattice.InnerProduct(
            first: node,
            second: other
        )),
            SymmetryFunction.ProjectionX => SymmetryLattice.Project(node: node).X,
            _ => SymmetryLattice.Project(node: node).Y,
        };
    }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: RowOrdinal
        ));
        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
        CompiledCellRef.CollectReference(into: into, reference: SymmetryOtherCell);
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) => Fact.Finite(
        kind: ValueKind,
        value: Fact.RawOf(
            kind: ValueKind,
            value: ReadFixed(
                key: ((KeyFrom is null)
                    ? Key
                    : keys[0].Key),
                reader: frame.Reader
            )
        )
    );
}
/// <summary>The candidate depth supplied by a search host, or zero on a live host.</summary>
public sealed class SearchPlyOperand : RuleOperand {
    /// <summary>The shared instance.</summary>
    public static readonly SearchPlyOperand Instance = new();

    private SearchPlyOperand() : base(valueKind: CellKind.Int) { }

    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override Fact Read(ref EvaluationFrame frame, scoped ReadOnlySpan<ResolvedKey> keys) => Fact.Finite(kind: CellKind.Int, value: frame.Reader.SearchPly);
}

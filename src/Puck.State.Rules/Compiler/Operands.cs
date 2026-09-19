using Puck.Maths;

namespace Puck.State.Rules;

/// <summary>A declared row's named cell — or, through a live row, the named cell of whichever row the index selects
/// this evaluation (the absent fact when it selects none).</summary>
public sealed class StateCellOperand : RuleOperand, IStateAddressedOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The row's catalog ordinal, or <c>-1</c> when <paramref name="rowFrom"/> applies.</param>
    /// <param name="key">The literal cell key, or the invalid default when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal key.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    /// <param name="rowFrom">The live row, or <see langword="null"/> for a fixed row.</param>
    public StateCellOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, CellKind valueKind, LiveRow? rowFrom = null) : base(valueKind: valueKind) {
        Key = key;
        KeyFrom = keyFrom;
        RowFrom = rowFrom;
        RowOrdinal = rowOrdinal;
    }

    /// <summary>Gets the literal cell key, or the invalid default when <see cref="KeyFrom"/> applies.</summary>
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

        if (RowFrom is { } live) {
            live.CollectReads(into: into);
        } else {
            into.Add(item: new CellAccess(
                Key: ((KeyFrom is null)
                ? Key
                : default),
                RowOrdinal: RowOrdinal
            ));
        }

        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
    }
    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var ordinal = RowOrdinal;

        if (
            (RowFrom is { } live) &&
            !live.TryResolve(
            reader: reader,
            rowOrdinal: out ordinal
        )
        ) {
            return RuleFact.Absent(kind: ValueKind);
        }

        return RuleReads.ReadCell(
            key: RuleReads.ResolveKey(
                keyFrom: KeyFrom,
                literal: Key,
                named: out var named,
                reader: reader
            ),
            kind: ValueKind,
            named: named,
            reader: reader,
            rowOrdinal: ordinal
        );
    }
}
/// <summary>A value the enclosing rule bound for this evaluation (<see cref="RuleFacts.LocalPrefix"/>).</summary>
public sealed class LocalOperand : RuleOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="ordinal">The binding's slot in the evaluation's bound-value scratch.</param>
    /// <param name="name">The authored binding name, for the read-back.</param>
    /// <param name="valueKind">The kind the binding was compiled in.</param>
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

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        if (Source is { } source) {
            RuleDataflow.CollectExpression(
                into: into,
                tokens: source.Expression
            );
        }
    }
    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return RuleFact.Finite(
            kind: ValueKind,
            value: reader.LocalValue(ordinal: Ordinal)
        );
    }
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
    }

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
    public override long Cost(IRuleCostContext context) => (2L + System.Numerics.BitOperations.Log2(value: ((uint)Math.Max(
        val1: Table.Count,
        val2: 1
    ))));
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var key = Key;

        if (
            (KeyFrom is { } indirection) &&
            !RuleReads.TryResolveIndex(
            index: out key,
            reader: reader,
            reference: in indirection
        )
        ) {
            return RuleFact.Absent(kind: ValueKind);
        }

        return (Table.TryLookup(
            column: Column,
            key: key,
            raw: out var raw
        )
            ? RuleFact.Finite(
                kind: ValueKind,
                value: raw
            )
            : RuleFact.Absent(kind: ValueKind)
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
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: unchecked((long)reader.Tick)
        );
    }
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
    /// <param name="capacity">The aggregated row's cell capacity, for pricing the scan.</param>
    /// <param name="range">Optional inclusive bounds in the source row's raw numeric encoding.</param>
    /// <param name="rowFrom">The live row aggregated, or <see langword="null"/> for a fixed row.</param>
    public ReductionOperand(int rowOrdinal, StateReduceOp reduce, int filterOrdinal, int domainOrdinal, CellKind valueKind, long capacity, (long Lower, long Upper)? range = null, LiveRow? rowFrom = null) : base(valueKind: valueKind) {
        Capacity = capacity;
        DomainOrdinal = domainOrdinal;
        FilterOrdinal = filterOrdinal;
        Range = range;
        Reduce = reduce;
        RowFrom = rowFrom;
        RowOrdinal = rowOrdinal;
    }

    /// <summary>Gets the aggregated row's cell capacity, for pricing the scan.</summary>
    public long Capacity { get; }
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

        if (RowFrom is { } live) {
            live.CollectReads(into: into);
        } else {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: RowOrdinal
            ));
        }
        if (FilterOrdinal >= 0) {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: FilterOrdinal
            ));
        }
    }
    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => RuleWorkBudget.SaturatingMultiply(
        left: ((RowFrom is { } live)
        ? live.SelectionCapacity
        : Capacity),
        right: ((Range is null)
        ? 1L
        : 3L)
    );
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var ordinal = RowOrdinal;

        if (
            (RowFrom is { } live) &&
            !live.TryResolve(
            reader: reader,
            rowOrdinal: out ordinal
        )
        ) {
            return RuleFact.Absent(kind: ValueKind);
        }
        if (ordinal < 0) {
            return RuleFact.Finite(
                kind: ValueKind,
                value: 0L
            );
        }
        if (Reduce == StateReduceOp.ArrangementRank) {
            return RuleFact.Finite(
                kind: ValueKind,
                value: RuleReads.ArrangementRank(
                    domainOrdinal: DomainOrdinal,
                    reader: reader,
                    rowOrdinal: ordinal
                )
            );
        }

        return RuleFact.Finite(
            kind: ValueKind,
            value: RuleReads.Reduce(
                filterOrdinal: FilterOrdinal,
                op: Reduce,
                range: Range,
                reader: reader,
                rowOrdinal: ordinal
            )
        );
    }
}
/// <summary>One value of a history ring by age (<see cref="RuleFacts.HistoryPrefix"/>).</summary>
public sealed class HistoryOperand : RuleOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The history row's catalog ordinal.</param>
    /// <param name="age">0 is the latest push; an age the ring no longer holds reads the trait's empty value. Read
    /// only when <paramref name="ageExpression"/> is <see langword="null"/>.</param>
    /// <param name="ring">The row's ring domain.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    /// <param name="ageExpression">The int expression the age is read from per evaluation, or
    /// <see langword="null"/> for a constant age.</param>
    public HistoryOperand(int rowOrdinal, long age, StateDomain.Ring ring, CellKind valueKind, CompiledExpressionToken[]? ageExpression = null) : base(valueKind: valueKind) {
        ArgumentNullException.ThrowIfNull(argument: ring);

        Age = age;
        AgeExpression = ageExpression;
        Ring = ring;
        RowOrdinal = rowOrdinal;
    }

    /// <summary>Gets the age: 0 is the latest push.</summary>
    public long Age { get; }
    /// <summary>Gets the int expression the age is read from per evaluation, or <see langword="null"/>.</summary>
    public CompiledExpressionToken[]? AgeExpression { get; }
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
        RuleDataflow.CollectExpression(
            into: into,
            tokens: AgeExpression
        );
    }
    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => RuleWorkBudget.SaturatingAdd(
        left: 1L,
        right: ((AgeExpression is { } expression)
        ? RuleWorkBudget.ExpressionCost(
            context: context,
            kind: CellKind.Int,
            tokens: expression
        )
        : 0L)
    );
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var age = Age;

        // An age the expression cannot compute names no slot, which the ring answers with its empty value the same
        // way an age past what it holds does.
        if (
            (AgeExpression is { } expression) &&
            !RuleExpressions.TryEvaluate(
            fault: out _,
            kind: CellKind.Int,
            program: expression,
            reader: reader,
            value: out age
        )
        ) {
            age = -1L;
        }

        return RuleFact.Finite(
            kind: ValueKind,
            value: ReadSlot(
                age: age,
                arena: reader.Arena,
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
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: reader.Arena.PhaseSequence(rowOrdinal: RowOrdinal)
        );
    }
}
/// <summary>A bounded discrete topology query over a board row (the <c>$board:</c> channel).</summary>
public sealed class BoardOperand : RuleOperand, IStateAddressedOperand {
    /// <summary>Initializes the operand.</summary>
    /// <param name="rowOrdinal">The board row's catalog ordinal.</param>
    /// <param name="key">The literal source cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="board">The compiled query.</param>
    /// <param name="targetFrom">The live destination indirection a <c>pathCost</c> query reads its target ordinal
    /// from every evaluation, or <see langword="null"/> for a compile-time target.</param>
    public BoardOperand(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, BoardQuery board, CompiledCellRef? targetFrom = null) : base(valueKind: CellKind.Int) {
        ArgumentNullException.ThrowIfNull(argument: board);

        Board = board;
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
        TargetFrom = targetFrom;
    }

    /// <summary>Gets the compiled query.</summary>
    public BoardQuery Board { get; }
    /// <summary>Gets the live destination indirection a <c>pathCost</c> query reads its target ordinal from, or
    /// <see langword="null"/>.</summary>
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
    public override long Cost(IRuleCostContext context) => (Board.Topology.CellCount + Board.Visits);
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        var key = RuleReads.ResolveKey(
            keyFrom: KeyFrom,
            literal: Key,
            reader: reader
        );
        var origin = -1;

        if (
            key.IsValid &&
            reader.Catalog.Keys.TryGetName(
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
            return RuleFact.Finite(
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
            dynamicTarget = ((int)(RuleReads.ReadFixed(
                key: targetFrom.Key,
                reader: reader,
                rowOrdinal: targetFrom.RowOrdinal
            ).Value >> FixedQ4816.FractionBitCount));
        }

        return RuleFact.Finite(
            kind: CellKind.Int,
            value: (ArenaBoards.TryEvaluate(
                arena: reader.Arena,
                dynamicTarget: dynamicTarget,
                query: Board,
                reason: out _,
                result: out var result,
                rowOrdinal: RowOrdinal,
                source: origin,
                values: reader.BoardScratch(cells: Board.Topology.CellCount)
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
    }

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
    private FixedQ4816 ReadFixed(IStateReader reader) {
        var node = NodeOf(value: RuleReads.ReadFixed(
            key: RuleReads.ResolveKey(
                keyFrom: KeyFrom,
                literal: Key,
                reader: reader
            ),
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
    }
    /// <inheritdoc/>
    public override long Cost(IRuleCostContext context) => 1L;
    /// <inheritdoc/>
    public override RuleFact Read(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        return RuleFact.Finite(
            kind: ValueKind,
            value: RuleFact.RawOf(
                kind: ValueKind,
                value: ReadFixed(reader: reader)
            )
        );
    }
}

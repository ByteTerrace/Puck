using Puck.Maths;

namespace Puck.State;

/// <summary>A declared <see cref="StateRow"/>'s named cell — or, through a live zone, the named cell of whichever
/// zone the index selects this evaluation (the absent fact when it selects none).</summary>
public sealed class StateCellOperand : OperandFact, IStateAddressedOperand {
    /// <summary>Addresses a state cell, literally or by indirection.</summary>
    /// <param name="row">The state row name, or the live zone's spelling.</param>
    /// <param name="key">The literal cell key, or <see langword="null"/> when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="stateHandle">The compiled row handle, for a fixed row.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    /// <param name="rowFrom">The live zone, or <see langword="null"/> for a fixed row.</param>
    public StateCellOperand(string row, string? key, CompiledCellRef? keyFrom, StateHandle stateHandle, CellKind valueKind, LiveZone? rowFrom = null)
        : base(valueKind) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        StateHandle = stateHandle;
        RowFrom = rowFrom;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>Gets the literal cell key, or <see langword="null"/> when <see cref="KeyFrom"/> applies.</summary>
    public string? Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the compiled row handle, for a fixed row.</summary>
    public StateHandle StateHandle { get; }
    /// <summary>Gets the live zone, or <see langword="null"/> for a fixed row.</summary>
    public LiveZone? RowFrom { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) =>
        (RuleEvaluation.TryResolveRow(reader: reader, handle: StateHandle, rowFrom: RowFrom, resolved: out var handle)
            ? RuleEvaluation.ReadStateFact(reader: reader, handle: handle, key: RuleEvaluation.ResolveKey(reader: reader, key: Key, keyFrom: KeyFrom))
            : RuleFact.Absent(kind: ValueKind));
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 1L;
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        if (RowFrom is { } live) {
            live.CollectReads(into: into);
        } else {
            into.Add(item: new RuleAccess(Row: Row, Key: ((KeyFrom is null) ? Key : null)));
        }
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }
    /// <inheritdoc/>
    public override bool HostOnly => (RowFrom is { HostOnly: true }) || (KeyFrom is { Custom.HostOnly: true });
}

/// <summary>A value the enclosing rule bound for this evaluation (<see cref="RuleFacts.BindPrefix"/>).</summary>
public sealed class BindingOperand : OperandFact {
    /// <summary>Reads a value the enclosing rule bound for this evaluation.</summary>
    /// <param name="ordinal">The binding's slot in the evaluation's bound-value scratch.</param>
    /// <param name="name">The authored binding name.</param>
    /// <param name="valueKind">The kind the binding was compiled in.</param>
    public BindingOperand(int ordinal, string name, CellKind valueKind) : base(valueKind) {
        Ordinal = ordinal;
        Name = name;
    }
    /// <summary>Gets the binding's slot in the evaluation's bound-value scratch.</summary>
    public int Ordinal { get; }
    /// <summary>Gets the authored binding name.</summary>
    public string Name { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) => RuleFact.Finite(value: reader.BindingValue(ordinal: Ordinal), kind: ValueKind);
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>A static table entry (<see cref="RuleFacts.TablePrefix"/>). A key the table does not carry reads
/// as a forever fact: an expression over it refuses and a gate over it never holds.</summary>
public sealed class TableOperand : OperandFact {
    /// <summary>Reads one entry of a static table.</summary>
    /// <param name="tableOrdinal">The table's index in the section's <c>tables</c> rows.</param>
    /// <param name="table">The table's authored name.</param>
    /// <param name="key">The literal key, or 0 when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection — a <c>$cell:</c> read, a bound token, a <c>$bind:</c> binding,
    /// an expression — or <see langword="null"/> for a literal key.</param>
    /// <param name="column">The column index; 0 for a single-value table.</param>
    /// <param name="entryCount">The table's entry count, for pricing the lookup.</param>
    /// <param name="valueKind">The table's value kind.</param>
    public TableOperand(int tableOrdinal, string table, long key, CompiledCellRef? keyFrom, int column, int entryCount, CellKind valueKind)
        : base(valueKind) {
        TableOrdinal = tableOrdinal;
        Table = table;
        Key = key;
        KeyFrom = keyFrom;
        Column = column;
        EntryCount = entryCount;
    }
    /// <summary>Gets the column index.</summary>
    public int Column { get; }
    /// <summary>Gets the table's index in the section's <c>tables</c> rows.</summary>
    public int TableOrdinal { get; }
    /// <summary>Gets the table's authored name.</summary>
    public string Table { get; }
    /// <summary>Gets the literal key.</summary>
    public long Key { get; }
    /// <summary>Gets the live key indirection, or <see langword="null"/> for a literal key.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the table's entry count.</summary>
    public int EntryCount { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) {
        var key = Key;
        // A live key that spells no integer (an empty zone's endpoint) names no entry: the absent fact, not a
        // missing-key report, since no key was ever looked up.
        if ((KeyFrom is { } indirection) && !RuleEvaluation.TryResolveIndex(reader: reader, reference: in indirection, index: out key)) {
            return RuleFact.Absent(kind: ValueKind);
        }
        if (reader.Table(ordinal: TableOrdinal).TryLookup(key: key, column: Column, raw: out var raw)) {
            return RuleFact.Finite(value: raw, kind: ValueKind);
        }
        reader.ReportTableKeyMissing(table: Table, key: key);
        return RuleFact.Forever(kind: ValueKind);
    }
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => (2L + System.Numerics.BitOperations.Log2((uint)Math.Max(EntryCount, 1)));
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => RuleAccess.CollectReference(reference: KeyFrom, into: into);
    /// <inheritdoc/>
    public override bool HostOnly => (KeyFrom is { Custom.HostOnly: true });
}

/// <summary>The completed-tick counter (<see cref="RuleFacts.Tick"/>). Stateless: every read shares
/// <see cref="Instance"/>.</summary>
public sealed class TickOperand : OperandFact {
    /// <summary>The shared instance.</summary>
    public static readonly TickOperand Instance = new();
    private TickOperand() : base(CellKind.Int) { }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) => RuleFact.Finite(value: unchecked((long)reader.Tick), kind: CellKind.Int);
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 1L;
}

/// <summary>A numeric aggregate over a row's cells (<see cref="RuleFacts.ReducePrefix"/>). Count is always integer
/// regardless of the row's declared kind; Max/Min/Sum preserve the row's kind. An empty row reads as zero for every
/// op.</summary>
public sealed class ReductionOperand : OperandFact {
    /// <param name="row">The aggregated row.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="reduce">The aggregate.</param>
    /// <param name="filterRow">The optional keyed row whose nonzero cells admit candidates.</param>
    /// <param name="filterHandle">The compiled handle for <paramref name="filterRow"/>.</param>
    /// <param name="valueKind">Int for <see cref="StateReduceOp.Count"/>, else the aggregated row's own kind.</param>
    /// <param name="range">Optional inclusive bounds in the source row's raw numeric encoding.</param>
    /// <param name="rowFrom">The live zone aggregated, or <see langword="null"/> for a fixed row.</param>
    public ReductionOperand(string row, StateHandle stateHandle, StateReduceOp reduce, string? filterRow, StateHandle filterHandle, CellKind valueKind, (long Lower, long Upper)? range = null, LiveZone? rowFrom = null)
        : base(valueKind) {
        Row = row;
        StateHandle = stateHandle;
        Reduce = reduce;
        FilterRow = filterRow;
        FilterHandle = filterHandle;
        Range = range;
        RowFrom = rowFrom;
    }

    /// <summary>Gets the aggregated row, or the live zone's spelling.</summary>
    public string Row { get; }
    /// <summary>Gets the compiled row handle, for a fixed row.</summary>
    public StateHandle StateHandle { get; }
    /// <summary>Gets the live zone aggregated, or <see langword="null"/> for a fixed row.</summary>
    public LiveZone? RowFrom { get; }
    /// <summary>Gets the aggregate.</summary>
    public StateReduceOp Reduce { get; }
    /// <summary>Gets the optional keyed row whose nonzero cells admit candidates.</summary>
    public string? FilterRow { get; }
    /// <summary>Gets the compiled handle for <see cref="FilterRow"/>.</summary>
    public StateHandle FilterHandle { get; }
    /// <summary>Gets the optional inclusive bounds applied to each candidate's live raw value.</summary>
    public (long Lower, long Upper)? Range { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) {
        if (!RuleEvaluation.TryResolveRow(reader: reader, handle: StateHandle, rowFrom: RowFrom, resolved: out var handle)) {
            return RuleFact.Absent(kind: ValueKind);
        }
        if (!StateReader.TryReadHandle(store: reader.Store, catalog: reader.Catalog, handle: handle, key: null, tick: reader.Tick, row: out var declared, rawValue: out _, text: out _)) {
            return RuleFact.Finite(value: 0L, kind: ValueKind);
        }
        if (Reduce == StateReduceOp.ArrangementRank) {
            return RuleFact.Finite(value: StateReader.ArrangementRank(store: reader.Store, zone: declared), kind: ValueKind);
        }
        StateRow? filter = null;
        if (FilterRow is not null && !StateReader.TryReadHandle(reader.Store, reader.Catalog, FilterHandle, null, reader.Tick, out filter, out _, out _)) {
            return RuleFact.Finite(0L, ValueKind);
        }
        return RuleFact.Finite(StateReader.ReduceRaw(reader.Store, declared, Reduce, reader.Tick, filter, Range), ValueKind);
    }
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => (RowFrom?.Table.Capacity ?? context.RowCapacity(name: Row)) * (Range is null ? 1L : 3L);
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        if (RowFrom is { } live) {
            live.CollectReads(into: into);
        } else {
            into.Add(item: new RuleAccess(Row: Row, Key: null));
        }
        if (FilterRow is not null) { into.Add(item: new RuleAccess(Row: FilterRow, Key: null)); }
    }
    /// <inheritdoc/>
    public override bool HostOnly => (RowFrom is { HostOnly: true });
}

/// <summary>A <see cref="RuleFacts.SymmetryPrefix"/> read: a cell's node through one symmetry-lattice map. The source
/// cell's whole part is the node; a cell holding no node reads the neutral value — -1 for the node-valued maps, 0 for
/// orthogonal, the inner product, and the projections.</summary>
public sealed class SymmetryOperand : OperandFact, IStateAddressedOperand {
    /// <param name="row">The source row.</param>
    /// <param name="key">The literal source cell key, or <see langword="null"/> when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="symmetry">The lattice map applied to the source node.</param>
    /// <param name="symmetryArgument">The literal argument — the step count of <see cref="SymmetryFunction.Cycle"/>,
    /// or the other node of <see cref="SymmetryFunction.Reflect"/>/<see cref="SymmetryFunction.Orthogonal"/>/
    /// <see cref="SymmetryFunction.InnerProduct"/> when <paramref name="symmetryOtherCell"/> is <see langword="null"/>.</param>
    /// <param name="symmetryOtherCell">The cell the other node is read from live, or <see langword="null"/> for the
    /// literal <paramref name="symmetryArgument"/>.</param>
    /// <param name="valueKind">Fixed for the two projection functions, else Int.</param>
    public SymmetryOperand(string row, string? key, CompiledCellRef? keyFrom, StateHandle stateHandle, SymmetryFunction symmetry, long symmetryArgument, CompiledCellRef? symmetryOtherCell, CellKind valueKind)
        : base(valueKind) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        StateHandle = stateHandle;
        Symmetry = symmetry;
        SymmetryArgument = symmetryArgument;
        SymmetryOtherCell = symmetryOtherCell;
    }

    /// <summary>Reshapes an already-resolved <see cref="StateCellOperand"/> source into the symmetry read over it:
    /// the address (row/key/keyFrom/handle) carries over unchanged, and only the symmetry-specific fields are new.</summary>
    /// <param name="source">The resolved state-cell source.</param>
    /// <param name="symmetry">The lattice map applied to the source node.</param>
    /// <param name="symmetryArgument">The literal argument.</param>
    /// <param name="symmetryOtherCell">The cell the other node is read from live, or <see langword="null"/>.</param>
    /// <param name="valueKind">Fixed for the two projection functions, else Int.</param>
    public static SymmetryOperand FromStateCell(StateCellOperand source, SymmetryFunction symmetry, long symmetryArgument, CompiledCellRef? symmetryOtherCell, CellKind valueKind) =>
        new(source.Row, source.Key, source.KeyFrom, source.StateHandle, symmetry, symmetryArgument, symmetryOtherCell, valueKind);

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>Gets the literal source cell key, or <see langword="null"/> when <see cref="KeyFrom"/> applies.</summary>
    public string? Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the compiled row handle.</summary>
    public StateHandle StateHandle { get; }
    /// <summary>Gets the lattice map applied to the source node.</summary>
    public SymmetryFunction Symmetry { get; }
    /// <summary>Gets the literal argument, when <see cref="SymmetryOtherCell"/> is <see langword="null"/>.</summary>
    public long SymmetryArgument { get; }
    /// <summary>Gets the cell the other node is read from live, or <see langword="null"/> for the literal
    /// <see cref="SymmetryArgument"/>.</summary>
    public CompiledCellRef? SymmetryOtherCell { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) => RuleFact.Finite(value: RuleFact.RawOf(value: ReadFixed(reader: reader), kind: ValueKind), kind: ValueKind);
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 1L;
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        into.Add(item: new RuleAccess(Row: Row, Key: null));
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }

    private FixedQ4816 ReadFixed(IRuleReader reader) {
        var node = NodeOf(value: RuleEvaluation.ReadFixed(
            reader: reader,
            handle: StateHandle,
            key: RuleEvaluation.ResolveKey(reader: reader, key: Key, keyFrom: KeyFrom)
        ));
        var other = ((SymmetryOtherCell is { } otherCell)
            ? NodeOf(value: RuleEvaluation.ReadFixed(
                reader: reader,
                handle: otherCell.Handle,
                key: ((otherCell.Key.Length == 0) ? StateRow.SlotKey.Value : otherCell.Key)
            ))
            : (int)Math.Clamp(value: SymmetryArgument, min: -1L, max: (SymmetryLattice.NodeCount - 1L))
        );
        var neutral = ((Symmetry is SymmetryFunction.Orthogonal or SymmetryFunction.InnerProduct or SymmetryFunction.ProjectionX or SymmetryFunction.ProjectionY) ? 0L : -1L);

        if ((node < 0) || ((Symmetry is SymmetryFunction.Reflect or SymmetryFunction.Orthogonal or SymmetryFunction.InnerProduct) && (other < 0))) {
            return FixedQ4816.FromInteger(value: neutral);
        }

        return Symmetry switch {
            SymmetryFunction.Ring => FixedQ4816.FromInteger(value: SymmetryLattice.Ring(node: node)),
            SymmetryFunction.Antipode => FixedQ4816.FromInteger(value: SymmetryLattice.Antipode(node: node)),
            SymmetryFunction.CanonicalRay => FixedQ4816.FromInteger(value: SymmetryLattice.CanonicalRay(node: node)),
            SymmetryFunction.Cycle => FixedQ4816.FromInteger(value: SymmetryLattice.Cycle(node: node, steps: SymmetryArgument)),
            SymmetryFunction.Reflect => FixedQ4816.FromInteger(value: SymmetryLattice.Reflect(mirror: other, node: node)),
            SymmetryFunction.Orthogonal => FixedQ4816.FromInteger(value: (SymmetryLattice.AreOrthogonal(first: node, second: other) ? 1L : 0L)),
            SymmetryFunction.InnerProduct => FixedQ4816.FromInteger(value: SymmetryLattice.InnerProduct(first: node, second: other)),
            SymmetryFunction.ProjectionX => SymmetryLattice.Project(node: node).X,
            _ => SymmetryLattice.Project(node: node).Y,
        };
    }

    // A fact's whole part as a lattice node, or -1 when it names none.
    private static int NodeOf(FixedQ4816 value) {
        var whole = (value.Value >> FixedQ4816.FractionBitCount);

        return (((whole < 0L) || (whole >= SymmetryLattice.NodeCount)) ? -1 : (int)whole);
    }
}

/// <summary>A bounded discrete topology query over a board row (the <c>$board:</c> channel).</summary>
public sealed class BoardOperand : OperandFact, IStateAddressedOperand {
    /// <param name="row">The board row.</param>
    /// <param name="key">The literal source cell key, or <see langword="null"/> when <paramref name="keyFrom"/> applies
    /// or the query needs no source cell (<c>mask</c>, <c>canonical</c>).</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="board">The compiled query.</param>
    public BoardOperand(string row, string? key, CompiledCellRef? keyFrom, StateHandle stateHandle, BoardQuery board)
        : base(CellKind.Int) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        StateHandle = stateHandle;
        Board = board;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>Gets the literal source cell key, or <see langword="null"/> when <see cref="KeyFrom"/> applies or the
    /// query needs no source cell.</summary>
    public string? Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the compiled row handle.</summary>
    public StateHandle StateHandle { get; }
    /// <summary>Gets the compiled query.</summary>
    public BoardQuery Board { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) => RuleFact.Finite(value: ReadBoard(reader: reader), kind: CellKind.Int);
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => (Board.Topology.CellCount + Board.Visits);
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        into.Add(item: new RuleAccess(Row: Row, Key: null));
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }

    private long ReadBoard(IRuleReader reader) {
        var query = Board;
        if (
            !StateReader.TryReadHandle(store: reader.Store, catalog: reader.Catalog, handle: StateHandle, key: null, tick: reader.Tick, row: out var row, rawValue: out _, text: out _) ||
            (row.EffectiveDomain is not StateDomain.CellsOf rowBoard)
        ) {
            return -1;
        }
        if (query is BoardOffsetQuery offsetQuery) {
            var originKey = RuleEvaluation.ResolveKey(reader: reader, key: Key, keyFrom: KeyFrom);
            var origin = ((originKey is not null) && query.Topology.TryCell(originKey, out var originCell)) ? originCell : -1;
            return ((origin >= 0) && query.Topology.TryOffset(origin, offsetQuery.Dx, offsetQuery.Dz, out var offset)) ? offset : -1;
        }
        var values = reader.BoardScratch(cells: query.Topology.CellCount);
        reader.Store.ReadBoard(row: row, topology: query.Topology, values: values);
        var key = RuleEvaluation.ResolveKey(reader: reader, key: Key, keyFrom: KeyFrom);
        var source = ((key is not null) && query.Topology.TryCell(key, out var sourceCell)) ? sourceCell : -1;
        // A pathCost query's live target resolves on the same terms as a '$cell:' key indirection — the same
        // (row, key) cell read, answered as the destination ordinal rather than formatted as a key string.
        var dynamicTarget = ((query is BoardPathCostQuery { TargetFrom: { } targetFrom })
            ? ((int)RuleEvaluation.IntegerOf(value: RuleEvaluation.ReadFixed(reader: reader, handle: targetFrom.Handle, key: targetFrom.Key)))
            : 0
        );
        return BoardQueries.Evaluate(query, values, rowBoard.Empty, source, dynamicTarget);
    }
}

/// <summary>A phase protocol progression value (the <c>$phase:</c> channel) — the row's own generation, the same
/// value a <see cref="PhaseGuard"/> checks against it.</summary>
public sealed class PhaseOperand : OperandFact {
    /// <param name="row">The phase row.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    public PhaseOperand(string row, StateHandle stateHandle) : base(CellKind.Int) {
        Row = row;
        StateHandle = stateHandle;
    }

    /// <summary>Gets the phase row.</summary>
    public string Row { get; }
    /// <summary>Gets the compiled row handle.</summary>
    public StateHandle StateHandle { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) {
        if (!StateReader.TryReadHandle(store: reader.Store, catalog: reader.Catalog, handle: StateHandle, key: null, tick: reader.Tick, row: out var declared, rawValue: out _, text: out _)) {
            return RuleFact.Finite(value: -1L, kind: CellKind.Int);
        }
        return RuleFact.Finite(value: (declared.Phase?.Sequence ?? -1L), kind: CellKind.Int);
    }
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 1L;
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: null));
}

/// <summary>A pattern-language match over a row's word (<see cref="RuleFacts.MatchPrefix"/>). The word is read at
/// this tick through compiled row handles and the same per-cell read every other state read uses, so an advancing
/// attribute cell reads its live value. Every source fits the word buffer, and a board origin that names no cell
/// reads the empty word, which the pattern decides like any other.</summary>
public sealed class PatternOperand : OperandFact, IStateAddressedOperand {
    /// <param name="row">The source row.</param>
    /// <param name="key">The literal board-origin cell key, or the token a zone or keyed word starts at; <see langword="null"/>
    /// when <paramref name="keyFrom"/> applies or the word reads whole.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="stateHandle">The compiled handle for <paramref name="row"/>.</param>
    /// <param name="pattern">The pattern name.</param>
    /// <param name="board">The board ray descriptor, for a board source; <see langword="null"/> otherwise.</param>
    /// <param name="filterRow">The zone's attribute row name, for a zone source; <see langword="null"/> otherwise.</param>
    /// <param name="filterHandle">The compiled handle for <paramref name="filterRow"/>.</param>
    /// <param name="matchFacet">What this operand answers about its word.</param>
    /// <param name="tokenExpression">The zone's per-token value expression, when the pattern carries one.</param>
    /// <param name="rowFrom">The live zone read, or <see langword="null"/> for a fixed row.</param>
    public PatternOperand(string row, string? key, CompiledCellRef? keyFrom, StateHandle stateHandle, string pattern, BoardNeighbourQuery? board, string? filterRow, StateHandle filterHandle, MatchFacet matchFacet, CompiledExpressionToken[]? tokenExpression, LiveZone? rowFrom = null)
        : base(CellKind.Int) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        StateHandle = stateHandle;
        Pattern = pattern;
        Board = board;
        FilterRow = filterRow;
        FilterHandle = filterHandle;
        MatchFacet = matchFacet;
        TokenExpression = tokenExpression;
        RowFrom = rowFrom;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>Gets the live zone read, or <see langword="null"/> for a fixed row.</summary>
    public LiveZone? RowFrom { get; }
    /// <summary>Gets the literal board-origin cell key, or the token a zone or keyed word starts at; <see langword="null"/>
    /// when <see cref="KeyFrom"/> applies or the word reads whole.</summary>
    public string? Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the compiled handle for <see cref="Row"/>.</summary>
    public StateHandle StateHandle { get; }
    /// <summary>Gets the pattern name.</summary>
    public string Pattern { get; }
    /// <summary>Gets the board ray descriptor, for a board source; <see langword="null"/> otherwise. Only its
    /// direction is read (-1 meaning every direction); the query is never evaluated by its kind.</summary>
    public BoardNeighbourQuery? Board { get; }
    /// <summary>Gets the zone's attribute row name, for a zone source; <see langword="null"/> otherwise.</summary>
    public string? FilterRow { get; }
    /// <summary>Gets the compiled handle for <see cref="FilterRow"/>.</summary>
    public StateHandle FilterHandle { get; }
    /// <summary>Gets what this operand answers about its word.</summary>
    public MatchFacet MatchFacet { get; }
    /// <summary>Gets the zone's per-token value expression, when the pattern carries one.</summary>
    public CompiledExpressionToken[]? TokenExpression { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) =>
        (RuleEvaluation.TryResolveRow(reader: reader, handle: StateHandle, rowFrom: RowFrom, resolved: out var handle)
            ? RuleFact.Finite(value: ReadMatch(reader: reader, handle: handle), kind: CellKind.Int)
            : RuleFact.Absent(kind: CellKind.Int));
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => ((Board is { } board)
        ? (board.Topology.CellCount + board.Visits)
        : RuleWorkBudget.SaturatingMultiply((RowFrom?.Table.Capacity ?? context.RowCapacity(Row)),
            RuleWorkBudget.SaturatingAdd(1L, RuleWorkBudget.ExpressionCost(TokenExpression ?? [], context))));
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        if (RowFrom is { } live) {
            live.CollectReads(into: into);
        } else {
            into.Add(item: new RuleAccess(Row: Row, Key: null));
        }
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
        if (FilterRow is not null) { into.Add(new RuleAccess(FilterRow, null)); }
        if (TokenExpression is { } expression) { RuleDataflow.CollectExpression(expression, into); }
    }
    /// <inheritdoc/>
    public override bool HostOnly => (RowFrom is { HostOnly: true }) || (KeyFrom is { Custom.HostOnly: true }) || RuleDataflow.ExpressionReadsHost(tokens: TokenExpression);

    private long ReadMatch(IRuleReader reader, StateHandle handle) {
        if (!reader.Patterns.TryGet(name: Pattern, pattern: out var pattern)) {
            throw new InvalidOperationException($"pattern operand '{Pattern}' outlived the compiled rules");
        }
        if (!StateReader.TryReadHandle(store: reader.Store, catalog: reader.Catalog, handle: handle, key: null, tick: reader.Tick, row: out var row, rawValue: out _, text: out _)) {
            throw new InvalidOperationException($"pattern operand over '{Row}' outlived its compiled row handle");
        }

        var word = reader.PatternWord;

        if (Board is { } query) {
            if (row.EffectiveDomain is not StateDomain.CellsOf) {
                return 0L;
            }

            var values = reader.BoardScratch(cells: query.Topology.CellCount);
            reader.Store.ReadBoard(row: row, topology: query.Topology, values: values);
            var key = RuleEvaluation.ResolveKey(reader: reader, key: Key, keyFrom: KeyFrom);
            var origin = (((key is not null) && query.Topology.TryCell(key, out var cell)) ? cell : -1);

            if (query.Direction >= 0) {
                var length = BoardQueries.ReadRay(query.Topology, values, origin, query.Direction, word);

                if (MatchFacet is MatchFacet.Cell or MatchFacet.Distance) {
                    var prefixLength = pattern.LongestAcceptedPrefix(values: word[..length]);

                    if (prefixLength == length) {
                        return -1L;
                    }
                    if (MatchFacet == MatchFacet.Distance) {
                        return prefixLength + 1;
                    }

                    var blocker = origin;

                    for (var step = 0; step <= prefixLength; step++) {
                        blocker = query.Topology.Neighbour(blocker, query.Direction);
                    }

                    return blocker;
                }

                return (MatchFacet == MatchFacet.Prefix)
                    ? pattern.LongestAcceptedPrefix(values: word[..length])
                    : pattern.Match(values: word[..length]);
            }

            var mask = 0L;
            var count = 0L;

            for (var direction = 0; direction < query.Topology.DirectionCount; direction++) {
                var length = BoardQueries.ReadRay(query.Topology, values, origin, direction, word);

                if (pattern.Match(values: word[..length]) == 1L) {
                    mask |= 1L << direction;
                    count++;
                }
            }

            return (MatchFacet == MatchFacet.DirectionCount) ? count : mask;
        }

        var source = row;
        int wordLength;
        var start = (((Key is not null) || (KeyFrom is not null)) ? RuleEvaluation.ResolveKey(reader: reader, key: Key, keyFrom: KeyFrom) : null);

        if (TokenExpression is { } tokenExpression) {
            wordLength = ReadTupleWord(reader: reader, row: row, expression: tokenExpression, kind: pattern.Source.Kind, word: word, start: start);
        } else {
            if ((row.EffectiveDomain is StateDomain.KeysOf { Ordered: true }) && !StateReader.TryReadHandle(store: reader.Store, catalog: reader.Catalog, handle: FilterHandle, key: null, tick: reader.Tick, row: out source, rawValue: out _, text: out _)) {
                throw new InvalidOperationException($"pattern attribute '{FilterRow}' outlived its compiled row handle");
            }

            wordLength = ReadWord(store: reader.Store, row: row, source: source, tick: reader.Tick, word: word, start: start);
        }

        return (MatchFacet == MatchFacet.Prefix)
            ? pattern.LongestAcceptedPrefix(values: word[..wordLength])
            : pattern.Match(values: word[..wordLength]);
    }

    /// <summary>Reads a zone's cells in pile order through its attribute row, a history ring oldest push first, or a
    /// keyed row's own cells in cell order.</summary>
    /// <param name="store">Where each value is read.</param>
    /// <param name="row">The word's row.</param>
    /// <param name="source">The row the values are read from — the attribute row for a zone, else <paramref name="row"/>.</param>
    /// <param name="tick">The tick the read answers as of.</param>
    /// <param name="word">The word buffer.</param>
    /// <returns>The word's length.</returns>
    /// <param name="start">The token the word starts at, or <see langword="null"/> to read the whole row.</param>
    public static int ReadWord(StateStore store, StateRow row, StateRow source, ulong tick, Span<long> word, string? start = null) {
        ArgumentNullException.ThrowIfNull(argument: store);
        var length = 0;

        if (row.EffectiveDomain is StateDomain.Ring history) {
            var count = (int)Math.Min(store.HistoryCursor(row: row), history.Capacity);

            for (var age = count - 1; age >= 0; age--) {
                word[length++] = HistoryOperand.ReadSlot(store: store, row: row, history: history, age: age);
            }

            return length;
        }

        var members = store.CellCount(row: row);
        for (var index = StartIndex(store: store, row: row, start: start); index < members; index++) {
            if (!store.TryKeyAt(row: row, index: index, key: out var key)) {
                continue;
            }
            StateReader.ReadCell(store: store, row: source, key: key.Value, tick: tick, rawValue: out var raw, text: out _);
            word[length++] = raw ?? 0L;
        }

        return length;
    }

    /// <summary>Returns the index the word starts at through a store — a frame's zone enumerates its live members —
    /// on <see cref="StartIndex(IReadOnlyList{StateCell}, string?)"/>'s terms.</summary>
    /// <param name="store">Where the row's members are read.</param>
    /// <param name="row">The row.</param>
    /// <param name="start">The start token's key, or <see langword="null"/>.</param>
    public static int StartIndex(StateStore store, StateRow row, string? start) {
        ArgumentNullException.ThrowIfNull(argument: store);
        var count = store.CellCount(row: row);

        if (start is null) {
            return 0;
        }

        for (var index = 0; index < count; index++) {
            if (store.TryKeyAt(row: row, index: index, key: out var key) && string.Equals(a: key.Value, b: start, comparisonType: StringComparison.Ordinal)) {
                return index;
            }
        }

        return count;
    }

    /// <summary>Returns the index the word starts at: 0 for a whole word, the named token's position, or the row's
    /// count (an empty word) when the named token is not in the row.</summary>
    /// <param name="cells">The row's cells.</param>
    /// <param name="start">The start token's key, or <see langword="null"/>.</param>
    public static int StartIndex(IReadOnlyList<StateCell> cells, string? start) {
        if (start is null) {
            return 0;
        }

        for (var index = 0; index < cells.Count; index++) {
            if (string.Equals(a: cells[index].Key.Value, b: start, comparisonType: StringComparison.Ordinal)) {
                return index;
            }
        }

        return cells.Count;
    }

    /// <summary>Reads a zone's tokens in pile order, each through the pattern's value expression with <c>$token</c>
    /// bound to it; an expression that fails on a token reads that letter as zero.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <param name="row">The zone row.</param>
    /// <param name="expression">The compiled value expression.</param>
    /// <param name="kind">The pattern's kind.</param>
    /// <param name="word">The word buffer.</param>
    /// <returns>The word's length.</returns>
    /// <param name="start">The token the word starts at, or <see langword="null"/> to read the whole row.</param>
    public static int ReadTupleWord(IRuleReader reader, StateRow row, CompiledExpressionToken[] expression, CellKind kind, Span<long> word, string? start = null) {
        var length = 0;
        var store = reader.Store;
        var count = store.CellCount(row);

        try {
            for (var index = StartIndex(store: store, row: row, start: start); index < count; index++) {
                if (!store.TryKeyAt(row, index, out var key)) { continue; }
                reader.BoundTokenKey = key.Value;
                reader.BoundPreviousKey = index > 0 && store.TryKeyAt(row, index - 1, out var previous) ? previous.Value : null;
                word[length++] = RuleEvaluation.TryEvaluateExpression(reader: reader, program: expression, kind: kind, value: out var raw) ? raw : 0L;
            }
        } finally {
            reader.BoundTokenKey = null;
            reader.BoundPreviousKey = null;
        }

        return length;
    }
}

/// <summary>One value of a history ring by age (<see cref="RuleFacts.HistoryPrefix"/>).</summary>
public sealed class HistoryOperand : OperandFact {
    /// <param name="row">The history row.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="age">0 is the latest push; an age the ring no longer holds reads the trait's empty value.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    public HistoryOperand(string row, StateHandle stateHandle, long age, CellKind valueKind) : base(valueKind) {
        Row = row;
        StateHandle = stateHandle;
        Age = age;
    }

    /// <summary>Gets the history row.</summary>
    public string Row { get; }
    /// <summary>Gets the compiled row handle.</summary>
    public StateHandle StateHandle { get; }
    /// <summary>Gets the age: 0 is the latest push; an age the ring no longer holds reads the trait's empty value.</summary>
    public long Age { get; }

    /// <inheritdoc/>
    public override RuleFact Read(IRuleReader reader) {
        if (
            !StateReader.TryReadHandle(store: reader.Store, catalog: reader.Catalog, handle: StateHandle, key: null, tick: reader.Tick, row: out var row, rawValue: out _, text: out _) ||
            (row.EffectiveDomain is not StateDomain.Ring history)
        ) {
            throw new InvalidOperationException($"history operand over '{Row}' outlived its compiled row handle");
        }

        return RuleFact.Finite(value: ReadSlot(store: reader.Store, row: row, history: history, age: Age), kind: ValueKind);
    }
    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 1L;
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: null));

    /// <summary>Reads the slot pushed <paramref name="age"/> pushes ago: (cursor - 1 - age) mod capacity, since the
    /// ring's cells are its slots in order; a slot never written reads the empty value. Ring slots carry no time
    /// trait, so the stored raw is the live value.</summary>
    /// <param name="store">Where the slots and the cursor are read.</param>
    /// <param name="row">The history row.</param>
    /// <param name="history">The row's ring domain.</param>
    /// <param name="age">The age.</param>
    public static long ReadSlot(StateStore store, StateRow row, StateDomain.Ring history, long age) {
        ArgumentNullException.ThrowIfNull(argument: store);
        var cursor = store.HistoryCursor(row: row);

        if (age >= Math.Min(cursor, history.Capacity)) {
            return history.Empty;
        }

        var slot = (int)((cursor - 1L - age) % history.Capacity);

        return (store.TryStoredAt(row: row, index: slot, value: out var value) ? value : history.Empty);
    }
}

namespace Puck.World;

// One sealed class per WorldRuleFactKind — the case types of CompiledWorldOperand's union (WorldOperandUnion.cs).
// Every case's constructor computes ValueKind itself where the original resolver derived it in ResolvedOperand
// rather than on the operand; there is exactly one place each case's ValueKind is decided.

/// <summary>A declared <see cref="WorldStateRow"/>'s named cell (<see cref="WorldRuleFactKind.StateCell"/>).</summary>
public sealed class StateCellOperand : WorldOperandFact, IStateAddressedOperand {
    /// <summary>Addresses a state cell, literally or by indirection.</summary>
    /// <param name="row">The state row name.</param>
    /// <param name="key">The literal cell key, or <see langword="null"/> when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    public StateCellOperand(string row, string? key, CompiledCellRef? keyFrom, WorldStateHandle stateHandle, CellKind valueKind)
        : base(WorldRuleFactKind.StateCell, valueKind) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        StateHandle = stateHandle;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>The literal cell key, or <see langword="null"/> when <see cref="KeyFrom"/> applies.</summary>
    public string? Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>The compiled row handle.</summary>
    public WorldStateHandle StateHandle { get; }
}

/// <summary>A value the enclosing rule bound for this evaluation (<see cref="RuleFacts.BindPrefix"/>).</summary>
public sealed class BindingOperand : WorldOperandFact {
    /// <summary>Reads a value the enclosing rule bound for this evaluation.</summary>
    /// <param name="ordinal">The binding's slot in the evaluation's bound-value scratch.</param>
    /// <param name="name">The authored binding name.</param>
    /// <param name="valueKind">The kind the binding was compiled in.</param>
    public BindingOperand(int ordinal, string name, CellKind valueKind) : base(WorldRuleFactKind.Binding, valueKind) {
        Ordinal = ordinal;
        Name = name;
    }
    /// <summary>Gets the binding's slot in the evaluation's bound-value scratch.</summary>
    public int Ordinal { get; }
    /// <summary>Gets the authored binding name.</summary>
    public string Name { get; }
}

/// <summary>A static table entry (<see cref="RuleFacts.TablePrefix"/>). A key the table does not carry reads
/// as a forever fact: an expression over it refuses and a gate over it never holds.</summary>
public sealed class TableOperand : WorldOperandFact {
    /// <summary>Reads one entry of a static table.</summary>
    /// <param name="tableOrdinal">The table's index in the document's <c>tables</c> rows.</param>
    /// <param name="table">The table's authored name.</param>
    /// <param name="key">The literal key, or 0 when <paramref name="keyFrom"/> or <paramref name="keyBinding"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal or bound key.</param>
    /// <param name="keyBinding">The ordinal of the enclosing rule's binding the key reads, or -1.</param>
    /// <param name="column">The column index; 0 for a single-value table.</param>
    /// <param name="entryCount">The table's entry count, for pricing the lookup.</param>
    /// <param name="valueKind">The table's value kind.</param>
    public TableOperand(int tableOrdinal, string table, long key, CompiledCellRef? keyFrom, int keyBinding, int column, int entryCount, CellKind valueKind)
        : base(WorldRuleFactKind.Table, valueKind) {
        TableOrdinal = tableOrdinal;
        Table = table;
        Key = key;
        KeyFrom = keyFrom;
        KeyBinding = keyBinding;
        Column = column;
        EntryCount = entryCount;
    }
    /// <summary>Gets the ordinal of the enclosing rule's binding the key reads, or -1.</summary>
    public int KeyBinding { get; }
    /// <summary>Gets the column index.</summary>
    public int Column { get; }
    /// <summary>Gets the table's index in the document's <c>tables</c> rows.</summary>
    public int TableOrdinal { get; }
    /// <summary>Gets the table's authored name.</summary>
    public string Table { get; }
    /// <summary>Gets the literal key.</summary>
    public long Key { get; }
    /// <summary>Gets the live key indirection, or <see langword="null"/> for a literal key.</summary>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the table's entry count.</summary>
    public int EntryCount { get; }
}

/// <summary>The server's completed-tick counter (<see cref="RuleFacts.Tick"/>). Stateless: every read shares
/// <see cref="Instance"/>.</summary>
public sealed class TickOperand : WorldOperandFact {
    /// <summary>The shared instance.</summary>
    public static readonly TickOperand Instance = new();
    private TickOperand() : base(WorldRuleFactKind.Tick, CellKind.Int) { }
}

/// <summary>The live active-population count (<see cref="WorldRuleFacts.Population"/>). Stateless: every read shares
/// <see cref="Instance"/>.</summary>
public sealed class PopulationOperand : WorldOperandFact {
    /// <summary>The shared instance.</summary>
    public static readonly PopulationOperand Instance = new();
    private PopulationOperand() : base(WorldRuleFactKind.Population, CellKind.Int) { }
}

/// <summary>Whether every active rigid body is at rest (<see cref="WorldRuleFacts.PhysicsQuiescent"/>). Stateless:
/// every read shares <see cref="Instance"/>.</summary>
public sealed class PhysicsQuiescentOperand : WorldOperandFact {
    /// <summary>The shared instance.</summary>
    public static readonly PhysicsQuiescentOperand Instance = new();
    private PhysicsQuiescentOperand() : base(WorldRuleFactKind.PhysicsQuiescent, CellKind.Bool) { }
}

/// <summary>The world's musical clock's signed phase error against the nearest beat (<see cref="WorldRuleFacts.ClockPrefix"/>).
/// Stateless: every read shares <see cref="Instance"/>.</summary>
public sealed class ClockOperand : WorldOperandFact {
    /// <summary>The shared instance.</summary>
    public static readonly ClockOperand Instance = new();
    private ClockOperand() : base(WorldRuleFactKind.Clock, CellKind.Int) { }
}

/// <summary>A named placement region's live occupant count (<see cref="WorldRuleFacts.RegionPrefix"/>).</summary>
public sealed class RegionOccupancyOperand : WorldOperandFact {
    /// <param name="row">The placement id.</param>
    public RegionOccupancyOperand(string row) : base(WorldRuleFactKind.RegionOccupancy, CellKind.Int) => Row = row;
    /// <summary>The placement id.</summary>
    public string Row { get; }
}

/// <summary>One live byte off a declared screen's booted machine (<see cref="WorldRuleFacts.MachinePrefix"/>).</summary>
public sealed class MachineMemoryOperand : WorldOperandFact {
    /// <param name="screen">The declared screen index.</param>
    /// <param name="address">The machine-defined memory address.</param>
    public MachineMemoryOperand(int screen, int address) : base(WorldRuleFactKind.MachineMemory, CellKind.Int) {
        Screen = screen;
        Address = address;
    }

    /// <summary>The declared screen index.</summary>
    public int Screen { get; }
    /// <summary>The machine-defined memory address.</summary>
    public int Address { get; }
}

/// <summary>A numeric aggregate over a row's cells (<see cref="RuleFacts.ReducePrefix"/>).</summary>
public sealed class ReductionOperand : WorldOperandFact {
    /// <param name="row">The aggregated row.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="reduce">The aggregate.</param>
    /// <param name="filterRow">The optional keyed row whose nonzero cells admit candidates.</param>
    /// <param name="filterHandle">The compiled handle for <paramref name="filterRow"/>.</param>
    /// <param name="valueKind">Int for <see cref="WorldStateReduceOp.Count"/>, else the aggregated row's own kind.</param>
    public ReductionOperand(string row, WorldStateHandle stateHandle, WorldStateReduceOp reduce, string? filterRow, WorldStateHandle filterHandle, CellKind valueKind)
        : base(WorldRuleFactKind.Reduction, valueKind) {
        Row = row;
        StateHandle = stateHandle;
        Reduce = reduce;
        FilterRow = filterRow;
        FilterHandle = filterHandle;
    }

    /// <summary>The aggregated row.</summary>
    public string Row { get; }
    /// <summary>The compiled row handle.</summary>
    public WorldStateHandle StateHandle { get; }
    /// <summary>The aggregate.</summary>
    public WorldStateReduceOp Reduce { get; }
    /// <summary>The optional keyed row whose nonzero cells admit candidates.</summary>
    public string? FilterRow { get; }
    /// <summary>The compiled handle for <see cref="FilterRow"/>.</summary>
    public WorldStateHandle FilterHandle { get; }
}

/// <summary>The body naming a row's extremal cell (<see cref="WorldRuleFacts.ArgMaxPrefix"/>/<see cref="WorldRuleFacts.ArgMinPrefix"/>).</summary>
public sealed class ArgBodyOperand : WorldOperandFact {
    /// <param name="row">The keyed row searched.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="reduce"><see cref="WorldStateReduceOp.Max"/> or <see cref="WorldStateReduceOp.Min"/>.</param>
    /// <param name="filterRow">The optional keyed row whose nonzero cells admit candidates.</param>
    /// <param name="filterHandle">The compiled handle for <paramref name="filterRow"/>.</param>
    public ArgBodyOperand(string row, WorldStateHandle stateHandle, WorldStateReduceOp reduce, string? filterRow, WorldStateHandle filterHandle)
        : base(WorldRuleFactKind.ArgBody, CellKind.Int) {
        Row = row;
        StateHandle = stateHandle;
        Reduce = reduce;
        FilterRow = filterRow;
        FilterHandle = filterHandle;
    }

    /// <summary>The keyed row searched.</summary>
    public string Row { get; }
    /// <summary>The compiled row handle.</summary>
    public WorldStateHandle StateHandle { get; }
    /// <summary><see cref="WorldStateReduceOp.Max"/> or <see cref="WorldStateReduceOp.Min"/>.</summary>
    public WorldStateReduceOp Reduce { get; }
    /// <summary>The optional keyed row whose nonzero cells admit candidates.</summary>
    public string? FilterRow { get; }
    /// <summary>The compiled handle for <see cref="FilterRow"/>.</summary>
    public WorldStateHandle FilterHandle { get; }
}

/// <summary>The live distance between two named bodies (<see cref="WorldRuleFacts.DistancePrefix"/>).</summary>
public sealed class BodyDistanceOperand : WorldOperandFact {
    /// <param name="bodyA">The first named body.</param>
    /// <param name="bodyB">The second named body.</param>
    public BodyDistanceOperand(CompiledBodyRef bodyA, CompiledBodyRef bodyB) : base(WorldRuleFactKind.BodyDistance, CellKind.Fixed) {
        BodyA = bodyA;
        BodyB = bodyB;
    }

    /// <summary>The first named body.</summary>
    public CompiledBodyRef BodyA { get; }
    /// <summary>The second named body.</summary>
    public CompiledBodyRef BodyB { get; }
}

/// <summary>Whether two named bodies have line of sight (<see cref="WorldRuleFacts.LineOfSightPrefix"/>).</summary>
public sealed class LineOfSightOperand : WorldOperandFact {
    /// <param name="bodyA">The first named body.</param>
    /// <param name="bodyB">The second named body.</param>
    public LineOfSightOperand(CompiledBodyRef bodyA, CompiledBodyRef bodyB) : base(WorldRuleFactKind.LineOfSight, CellKind.Bool) {
        BodyA = bodyA;
        BodyB = bodyB;
    }

    /// <summary>The first named body.</summary>
    public CompiledBodyRef BodyA { get; }
    /// <summary>The second named body.</summary>
    public CompiledBodyRef BodyB { get; }
}

/// <summary>One named body's remaining reconnect-park ticks (<see cref="WorldRuleFacts.ParkedPrefix"/>).</summary>
public sealed class ParkedOperand : WorldOperandFact {
    /// <param name="bodyA">The named body.</param>
    public ParkedOperand(CompiledBodyRef bodyA) : base(WorldRuleFactKind.Parked, CellKind.Int) => BodyA = bodyA;
    /// <summary>The named body.</summary>
    public CompiledBodyRef BodyA { get; }
}

/// <summary>One named body's own up axis dotted against world up (<see cref="WorldRuleFacts.UprightPrefix"/>).</summary>
public sealed class UprightOperand : WorldOperandFact {
    /// <param name="bodyA">The named body.</param>
    public UprightOperand(CompiledBodyRef bodyA) : base(WorldRuleFactKind.Upright, CellKind.Fixed) => BodyA = bodyA;
    /// <summary>The named body.</summary>
    public CompiledBodyRef BodyA { get; }
}

/// <summary>Simulation ticks since one named adjacency row last received a delivered neighbour refresh
/// (<see cref="WorldRuleFacts.LinkPrefix"/>).</summary>
public sealed class LinkStalenessOperand : WorldOperandFact {
    /// <param name="row">The adjacency row name.</param>
    public LinkStalenessOperand(string row) : base(WorldRuleFactKind.LinkStaleness, CellKind.Int) => Row = row;
    /// <summary>The adjacency row name.</summary>
    public string Row { get; }
}

/// <summary>One local seat's own folded channel value (<see cref="WorldRuleFacts.ChannelPrefix"/>).</summary>
public sealed class ChannelOperand : WorldOperandFact {
    /// <param name="seat">The 0-based local-seat body index.</param>
    /// <param name="channelOrdinal">The declared channel's document-order ordinal.</param>
    public ChannelOperand(int seat, int channelOrdinal) : base(WorldRuleFactKind.Channel, CellKind.Fixed) {
        Seat = seat;
        ChannelOrdinal = channelOrdinal;
    }

    /// <summary>The 0-based local-seat body index.</summary>
    public int Seat { get; }
    /// <summary>The declared channel's document-order ordinal.</summary>
    public int ChannelOrdinal { get; }
}

/// <summary>The nearest tagged body's index (<see cref="WorldRuleFacts.NearestPrefix"/>).</summary>
public sealed class NearestOperand : WorldOperandFact {
    /// <param name="bodyA">The reference body.</param>
    /// <param name="row">The keyed tag row.</param>
    /// <param name="stateHandle">The compiled handle for <paramref name="row"/>.</param>
    public NearestOperand(CompiledBodyRef bodyA, string row, WorldStateHandle stateHandle) : base(WorldRuleFactKind.Nearest, CellKind.Int) {
        BodyA = bodyA;
        Row = row;
        StateHandle = stateHandle;
    }

    /// <summary>The reference body.</summary>
    public CompiledBodyRef BodyA { get; }
    /// <summary>The keyed tag row.</summary>
    public string Row { get; }
    /// <summary>The compiled handle for <see cref="Row"/>.</summary>
    public WorldStateHandle StateHandle { get; }
}

/// <summary>One body's navigation status or remaining waypoint count (<see cref="WorldRuleFacts.NavigationPrefix"/>).</summary>
public sealed class NavigationOperand : WorldOperandFact {
    /// <param name="bodyA">The routed body.</param>
    /// <param name="row">The facet name (<c>hasPath</c>, <c>active</c>, <c>arrived</c>, <c>unreachable</c>,
    /// <c>pending</c>, <c>capacity</c>, or <c>remaining</c>).</param>
    public NavigationOperand(CompiledBodyRef bodyA, string row) : base(WorldRuleFactKind.Navigation, CellKind.Int) {
        BodyA = bodyA;
        Row = row;
    }

    /// <summary>The routed body.</summary>
    public CompiledBodyRef BodyA { get; }
    /// <summary>The facet name.</summary>
    public string Row { get; }
}

/// <summary>A <see cref="RuleFacts.SymmetryPrefix"/> read: a cell's node through one symmetry-lattice map.</summary>
public sealed class SymmetryOperand : WorldOperandFact, IStateAddressedOperand {
    /// <param name="row">The source row.</param>
    /// <param name="key">The literal source cell key, or <see langword="null"/> when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="symmetry">The lattice map applied to the source node.</param>
    /// <param name="symmetryArgument">The literal argument — the step count of <see cref="WorldSymmetryFunction.Cycle"/>,
    /// or the other node of <see cref="WorldSymmetryFunction.Reflect"/>/<see cref="WorldSymmetryFunction.Orthogonal"/>/
    /// <see cref="WorldSymmetryFunction.InnerProduct"/> when <paramref name="symmetryOtherCell"/> is <see langword="null"/>.</param>
    /// <param name="symmetryOtherCell">The cell the other node is read from live, or <see langword="null"/> for the
    /// literal <paramref name="symmetryArgument"/>.</param>
    /// <param name="valueKind">Fixed for the two projection functions, else Int.</param>
    public SymmetryOperand(string row, string? key, CompiledCellRef? keyFrom, WorldStateHandle stateHandle, WorldSymmetryFunction symmetry, long symmetryArgument, CompiledCellRef? symmetryOtherCell, CellKind valueKind)
        : base(WorldRuleFactKind.Symmetry, valueKind) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        StateHandle = stateHandle;
        Symmetry = symmetry;
        SymmetryArgument = symmetryArgument;
        SymmetryOtherCell = symmetryOtherCell;
    }

    /// <summary>Reshapes an already-resolved <see cref="StateCellOperand"/> source into the symmetry read over it —
    /// the union-safe replacement for the record struct's <c>source.Operand with { Kind = Symmetry, ... }</c>: the
    /// address (row/key/keyFrom/handle) carries over unchanged, and only the symmetry-specific fields are new.</summary>
    /// <param name="source">The resolved state-cell source.</param>
    /// <param name="symmetry">The lattice map applied to the source node.</param>
    /// <param name="symmetryArgument">The literal argument.</param>
    /// <param name="symmetryOtherCell">The cell the other node is read from live, or <see langword="null"/>.</param>
    /// <param name="valueKind">Fixed for the two projection functions, else Int.</param>
    public static SymmetryOperand FromStateCell(StateCellOperand source, WorldSymmetryFunction symmetry, long symmetryArgument, CompiledCellRef? symmetryOtherCell, CellKind valueKind) =>
        new(source.Row, source.Key, source.KeyFrom, source.StateHandle, symmetry, symmetryArgument, symmetryOtherCell, valueKind);

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>The literal source cell key, or <see langword="null"/> when <see cref="KeyFrom"/> applies.</summary>
    public string? Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>The compiled row handle.</summary>
    public WorldStateHandle StateHandle { get; }
    /// <summary>The lattice map applied to the source node.</summary>
    public WorldSymmetryFunction Symmetry { get; }
    /// <summary>The literal argument, when <see cref="SymmetryOtherCell"/> is <see langword="null"/>.</summary>
    public long SymmetryArgument { get; }
    /// <summary>The cell the other node is read from live, or <see langword="null"/> for the literal
    /// <see cref="SymmetryArgument"/>.</summary>
    public CompiledCellRef? SymmetryOtherCell { get; }
}

/// <summary>A bounded discrete topology query (<see cref="RuleFacts.MatchPrefix"/>'s board-adjacent sibling —
/// see <c>WorldRuleCompiler.Board.cs</c>'s <c>$board:</c> resolver).</summary>
public sealed class BoardOperand : WorldOperandFact, IStateAddressedOperand {
    /// <param name="row">The board row.</param>
    /// <param name="key">The literal source cell key, or <see langword="null"/> when <paramref name="keyFrom"/> applies
    /// or the query needs no source cell (<c>mask</c>, <c>canonical</c>, <c>cellOf</c>).</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="board">The compiled query.</param>
    /// <param name="bodyA">The referenced body for <see cref="WorldBoardQueryKind.CellOf"/>; <see langword="null"/> otherwise.</param>
    public BoardOperand(string row, string? key, CompiledCellRef? keyFrom, WorldStateHandle stateHandle, CompiledWorldBoardQuery board, CompiledBodyRef? bodyA)
        : base(WorldRuleFactKind.Board, CellKind.Int) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        StateHandle = stateHandle;
        Board = board;
        BodyA = bodyA;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>The literal source cell key, or <see langword="null"/> when <see cref="KeyFrom"/> applies or the
    /// query needs no source cell.</summary>
    public string? Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>The compiled row handle.</summary>
    public WorldStateHandle StateHandle { get; }
    /// <summary>The compiled query.</summary>
    public CompiledWorldBoardQuery Board { get; }
    /// <summary>The referenced body for <see cref="WorldBoardQueryKind.CellOf"/>; <see langword="null"/> otherwise.</summary>
    public CompiledBodyRef? BodyA { get; }
}

/// <summary>A phase protocol progression value (<see cref="RuleFacts.SymmetryPrefix"/>'s <c>$phase:</c> sibling —
/// see <c>WorldRuleCompiler.Phase.cs</c>).</summary>
public sealed class PhaseOperand : WorldOperandFact {
    /// <param name="row">The phase row.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    public PhaseOperand(string row, WorldStateHandle stateHandle) : base(WorldRuleFactKind.Phase, CellKind.Int) {
        Row = row;
        StateHandle = stateHandle;
    }

    /// <summary>The phase row.</summary>
    public string Row { get; }
    /// <summary>The compiled row handle.</summary>
    public WorldStateHandle StateHandle { get; }
}

/// <summary>A pattern-language match over a row's word (<see cref="RuleFacts.MatchPrefix"/>).</summary>
public sealed class PatternOperand : WorldOperandFact, IStateAddressedOperand {
    /// <param name="row">The source row.</param>
    /// <param name="key">The literal board-origin cell key, or <see langword="null"/> when <paramref name="keyFrom"/>
    /// applies or the source is a zone/keyed/history word (no origin cell).</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="stateHandle">The compiled handle for <paramref name="row"/>.</param>
    /// <param name="pattern">The pattern name.</param>
    /// <param name="board">The board ray descriptor, for a board source; <see langword="null"/> otherwise.</param>
    /// <param name="filterRow">The zone's attribute row name, for a zone source; <see langword="null"/> otherwise.</param>
    /// <param name="filterHandle">The compiled handle for <paramref name="filterRow"/>.</param>
    /// <param name="matchFacet">What this operand answers about its word.</param>
    /// <param name="tokenExpression">The zone's per-token value expression, when the pattern carries one.</param>
    public PatternOperand(string row, string? key, CompiledCellRef? keyFrom, WorldStateHandle stateHandle, string pattern, CompiledWorldBoardQuery? board, string? filterRow, WorldStateHandle filterHandle, WorldMatchFacet matchFacet, CompiledWorldExpressionToken[]? tokenExpression)
        : base(WorldRuleFactKind.Pattern, CellKind.Int) {
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
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>The literal board-origin cell key, or <see langword="null"/> when <see cref="KeyFrom"/> applies or
    /// the source needs no origin cell.</summary>
    public string? Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>The compiled handle for <see cref="Row"/>.</summary>
    public WorldStateHandle StateHandle { get; }
    /// <summary>The pattern name.</summary>
    public string Pattern { get; }
    /// <summary>The board ray descriptor, for a board source; <see langword="null"/> otherwise.</summary>
    public CompiledWorldBoardQuery? Board { get; }
    /// <summary>The zone's attribute row name, for a zone source; <see langword="null"/> otherwise.</summary>
    public string? FilterRow { get; }
    /// <summary>The compiled handle for <see cref="FilterRow"/>.</summary>
    public WorldStateHandle FilterHandle { get; }
    /// <summary>What this operand answers about its word.</summary>
    public WorldMatchFacet MatchFacet { get; }
    /// <summary>The zone's per-token value expression, when the pattern carries one.</summary>
    public CompiledWorldExpressionToken[]? TokenExpression { get; }
}

/// <summary>One value of a history ring by age (<see cref="RuleFacts.HistoryPrefix"/>).</summary>
public sealed class HistoryOperand : WorldOperandFact {
    /// <param name="row">The history row.</param>
    /// <param name="stateHandle">The compiled row handle.</param>
    /// <param name="age">0 is the latest push; an age the ring no longer holds reads the trait's empty value.</param>
    /// <param name="valueKind">The row's own cell kind.</param>
    public HistoryOperand(string row, WorldStateHandle stateHandle, long age, CellKind valueKind) : base(WorldRuleFactKind.History, valueKind) {
        Row = row;
        StateHandle = stateHandle;
        Age = age;
    }

    /// <summary>The history row.</summary>
    public string Row { get; }
    /// <summary>The compiled row handle.</summary>
    public WorldStateHandle StateHandle { get; }
    /// <summary>0 is the latest push; an age the ring no longer holds reads the trait's empty value.</summary>
    public long Age { get; }
}

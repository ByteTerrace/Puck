using System.Numerics;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Tests;

/// <summary>A refused preflight restores a <see cref="StateFrame"/> through its undo journal rather than a whole-frame
/// copy: every write kind a frame applies records what it overwrote, a rewind reverses exactly those cells (most
/// recent first, so a cell touched twice returns to what it held before the scope began), and a commit keeps them —
/// remaining part of an ancestor scope's own undo range until that scope, in turn, closes.</summary>
public sealed class PreflightJournalLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static StateCell[] Members(string keys) => [.. keys.Select(static key => new StateCell(Key: CellName.Parse(candidate: key.ToString()), Value: 1L))];

    private static (FrameLayout Layout, StateFrame Frame, IReadOnlyList<StateRow> Rows) Build() {
        var map = TopologyCompilation.Compile(
            topology: new LatticeTopology.Grid(Name: "map", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 2, Depth: 2),
            anchorOffset: Vector3.Zero
        );
        var line = TopologyCompilation.Compile(
            topology: new LatticeTopology.Grid(Name: "line", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 3, Depth: 1),
            anchorOffset: Vector3.Zero
        );
        var zone = new StateDomain.KeysOf(Row: Name("cards"), Ordered: true);
        StateRow[] rows = [
            new(Name: Name("counter"), Kind: CellKind.Int, Capacity: 2, Cells: [new(Name("x"), 5), new(Name("y"), 9)]),
            new(Name: Name("history"), Kind: CellKind.Int, Domain: new StateDomain.Ring(Capacity: 3, Empty: -1)),
            new(Name: Name("cards"), Kind: CellKind.Bool, Capacity: 4, Cells: Members("abcd")),
            new(Name: Name("deck"), Kind: CellKind.Bool, Capacity: 4, Domain: zone, Cells: Members("abc")),
            new(Name: Name("hand"), Kind: CellKind.Bool, Capacity: 4, Domain: zone, Cells: []),
            new(Name: Name("board"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "map")),
            new(Name: Name("legal"), Kind: CellKind.Int, Capacity: 1, Cells: [new(Name("0"), 0b0110L)]),
            new(Name: Name("combined"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "map", Empty: 0)),
            new(Name: Name("line3"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "line", Empty: -1), Cells: [new(Name("0"), 5), new(Name("1"), 1), new(Name("2"), 5)]),
            new(Name: Name("tokens"), Kind: CellKind.Int, Capacity: 2, Cells: [new(Name("a"), 0), new(Name("b"), 1)]),
            new(Name: Name("codes"), Kind: CellKind.Int, Capacity: 2, Cells: [new(Name("a"), 5), new(Name("b"), 9)]),
            new(Name: Name("derived"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "map", Empty: -1), Inverse: new StateInverse(Name("tokens"), Name("codes")), Cells: [new(Name("0"), 5), new(Name("1"), 9)]),
        ];
        var layout = new FrameLayout(rows: rows, topology: candidate => (candidate switch { "map" => map, "line" => line, _ => null }));
        var frame = new StateFrame(layout: layout, rows: rows);

        frame.Load(source: new RowStore(rows: rows));

        return (layout, frame, rows);
    }

    private static StateRow Row(IReadOnlyList<StateRow> rows, string name) => rows.Single(row => row.Name.Value == name);

    [Fact]
    public void ARefusedPreflightRestoresAPlainCellSetExactly() {
        var (_, frame, rows) = Build();
        var counter = Row(rows: rows, name: "counter");
        var before = frame.Values.ToArray();
        var mark = frame.BeginJournalScope();

        Assert.True(frame.TryWrite(row: counter, key: Name("x"), value: 3L, write: StateWriteKind.Set, reason: out var reason), reason);

        frame.RewindJournalScope(mark: mark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void ARefusedPreflightRestoresAPlainCellAddExactly() {
        var (_, frame, rows) = Build();
        var counter = Row(rows: rows, name: "counter");
        var before = frame.Values.ToArray();
        var mark = frame.BeginJournalScope();

        Assert.True(frame.TryWrite(row: counter, key: Name("y"), value: 4L, write: StateWriteKind.Add, reason: out var reason), reason);

        frame.RewindJournalScope(mark: mark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void ARefusedPreflightRestoresATransferExactly() {
        var (_, frame, rows) = Build();
        var deck = Row(rows: rows, name: "deck");
        var hand = Row(rows: rows, name: "hand");
        var before = frame.Values.ToArray();
        var mark = frame.BeginJournalScope();

        Assert.True(frame.TryTransferToken(from: deck, to: hand, key: Name("b"), insertFirst: false, reason: out var reason), reason);

        frame.RewindJournalScope(mark: mark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void ARefusedPreflightRestoresAPushExactly() {
        var (_, frame, rows) = Build();
        var history = Row(rows: rows, name: "history");
        var before = frame.Values.ToArray();
        var mark = frame.BeginJournalScope();

        Assert.True(frame.TryPush(row: history, value: 7L, reason: out var reason), reason);
        Assert.True(frame.TryPush(row: history, value: 8L, reason: out reason), reason);

        frame.RewindJournalScope(mark: mark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void ARefusedPreflightRestoresAWriteSetExactly() {
        var (_, frame, rows) = Build();
        var before = frame.Values.ToArray();
        var mark = frame.BeginJournalScope();

        Assert.True(frame.TryWriteSet(writeSet: new StateTransform.WriteSet(Row: "board", Set: "legal", SetKey: "0", Value: 5L), reason: out var reason), reason);

        frame.RewindJournalScope(mark: mark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void ARefusedPreflightRestoresABoardCombineExactly() {
        var (_, frame, _) = Build();
        var before = frame.Values.ToArray();
        var mark = frame.BeginJournalScope();

        Assert.True(frame.TryBoardCombine(combine: new StateTransform.BoardCombine(Row: "combined", Operation: BoardCombineOp.Fill, Value: 9L), reason: out var reason), reason);

        frame.RewindJournalScope(mark: mark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void ARefusedPreflightRestoresAClearEnclosedExactly() {
        var (_, frame, _) = Build();
        var before = frame.Values.ToArray();
        var mark = frame.BeginJournalScope();

        // Cell 1 sits between two members of the placed value with no empty neighbour beside it — enclosed, and
        // cleared by the call this scope then undoes.
        Assert.True(frame.TryClearEnclosed(enclosed: new StateTransform.ClearEnclosed(Row: "line3", From: "2", Lower: 1, Upper: 1), reason: out var reason), reason);

        frame.RewindJournalScope(mark: mark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void ARefusedPreflightRestoresADerivedBoardRecomputeExactly() {
        var (_, frame, rows) = Build();
        var tokens = Row(rows: rows, name: "tokens");
        var before = frame.Values.ToArray();
        var mark = frame.BeginJournalScope();

        Assert.True(frame.TryWrite(row: tokens, key: Name("a"), value: 2L, write: StateWriteKind.Set, reason: out var reason), reason);

        frame.RewindJournalScope(mark: mark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void NestedScopesRewindOnlyTheInnerWrite() {
        var (_, frame, rows) = Build();
        var counter = Row(rows: rows, name: "counter");
        var before = frame.Values.ToArray();

        var outerMark = frame.BeginJournalScope();
        Assert.True(frame.TryWrite(row: counter, key: Name("x"), value: 1L, write: StateWriteKind.Set, reason: out var reason), reason);
        var innerMark = frame.BeginJournalScope();
        Assert.True(frame.TryWrite(row: counter, key: Name("y"), value: 2L, write: StateWriteKind.Set, reason: out reason), reason);

        frame.RewindJournalScope(mark: innerMark);

        Assert.True(frame.TryStored(row: counter, key: Name("x"), value: out var x, text: out _));
        Assert.Equal(expected: 1L, actual: x);
        Assert.True(frame.TryStored(row: counter, key: Name("y"), value: out var y, text: out _));
        Assert.Equal(expected: 9L, actual: y);

        frame.RewindJournalScope(mark: outerMark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
    }

    [Fact]
    public void ACommittedInnerScopeStaysInTheOutersUndoRange() {
        var (_, frame, rows) = Build();
        var counter = Row(rows: rows, name: "counter");
        var before = frame.Values.ToArray();

        var outerMark = frame.BeginJournalScope();
        var innerMark = frame.BeginJournalScope();
        Assert.True(frame.TryWrite(row: counter, key: Name("y"), value: 2L, write: StateWriteKind.Set, reason: out var reason), reason);

        frame.CommitJournalScope();
        Assert.True(frame.TryStored(row: counter, key: Name("y"), value: out var y, text: out _));
        Assert.Equal(expected: 2L, actual: y);

        frame.RewindJournalScope(mark: outerMark);

        Assert.Equal(expected: before, actual: frame.Values.ToArray());
        _ = innerMark;
    }

    private sealed class Section(IReadOnlyList<StateRow> rows) : IStateSection {
        public IReadOnlyList<StateRow> Rows => rows;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
    }

    private const int BulkCellCount = 2_000;
    private const int BulkEffectCount = 10;

    private static (FrameHost Host, StateRow Row) BuildBulkFixture() {
        StateCell[] cells = [.. Enumerable.Range(0, BulkCellCount).Select(static index => new StateCell(Key: Name($"c{index}"), Value: 0L))];
        var big = new StateRow(Name: Name("big"), Kind: CellKind.Int, Capacity: BulkCellCount, Cells: cells);
        StateRow[] rows = [big];
        var catalog = StateCatalog.Compile(section: new Section(rows: rows));
        var host = new FrameHost(new FrameLayout(rows: rows, topology: static _ => null), rows, catalog, CompiledPatterns.Empty, []);

        host.Frame.Load(source: new RowStore(rows: rows));

        return (host, big);
    }

    // Ten top-level write effects, each its own preflight scope: the exact shape RuleEvaluator.Effects.cs drives
    // per effect (BeginPreflight, TryApply under preflight, EndPreflight, TryApply again for real) — driven directly
    // so this law measures the preflight itself rather than the effect-firing pipeline's own per-firing allocation
    // (a fresh mutation record every application, unrelated to and unchanged by this journal).
    private static void FireTenPreflightedWrites(FrameHost host, StateMutation.UpsertCell[] mutations) {
        foreach (var mutation in mutations) {
            host.BeginPreflight();
            Assert.True(host.TryApply(mutation: mutation, tick: 1, preflight: true, reason: out var reason), reason);
            host.EndPreflight();
            Assert.True(host.TryApply(mutation: mutation, tick: 1, preflight: false, reason: out reason), reason);
        }
    }

    [Fact]
    public void APreflightOfTenWritesTouchesOnlyTheCellsItWroteAndAllocatesNothing() {
        var (host, row) = BuildBulkFixture();
        StateMutation.UpsertCell[] mutations = [.. Enumerable.Range(0, BulkEffectCount).Select(static index => new StateMutation.UpsertCell(Row: "big", Key: $"c{index}", Value: 1, Write: StateWriteKind.Add))];

        // Warms the journal buffer and the JIT; an add never no-ops, so the measured round exercises the same
        // preflight-and-apply path as the warm-up one.
        FireTenPreflightedWrites(host: host, mutations: mutations);

        var touchesBefore = host.Frame.JournalTouches;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        FireTenPreflightedWrites(host: host, mutations: mutations);

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        var touched = (host.Frame.JournalTouches - touchesBefore);

        Assert.Equal(expected: 0L, actual: allocated);
        Assert.True(condition: (touched <= BulkEffectCount), userMessage: $"expected at most {BulkEffectCount} cells touched by the preflight, saw {touched}");

        for (var index = 0; index < BulkEffectCount; index++) {
            Assert.True(host.Frame.TryStored(row: row, key: Name($"c{index}"), value: out var value, text: out _));
            Assert.Equal(expected: 2L, actual: value);
        }
    }
}

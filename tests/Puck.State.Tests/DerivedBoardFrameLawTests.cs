using System.Numerics;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Tests;

/// <summary>A derived board's frame counterpart: a token write inside a hypothetical evaluation rewrites the
/// board's dense values in the SAME frame, never the section the frame was loaded from.</summary>
public sealed class DerivedBoardFrameLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);

    private static (FrameLayout Layout, StateFrame Frame, IReadOnlyList<StateRow> Rows) Build() {
        var map = new LatticeTopology.Grid(Name: "map", Origin: new DocumentVector3(0, 0, 0), CellSize: 1, Width: 4, Depth: 4);
        var topology = TopologyCompilation.Compile(topology: map, anchorOffset: Vector3.Zero);
        var tokens = new StateRow(Name: Name("tokens"), Kind: CellKind.Int, Capacity: 2, Cells: [
            new StateCell(Name("a"), Value: 0),
            new StateCell(Name("b"), Value: 1),
        ]);
        var codes = new StateRow(Name: Name("codes"), Kind: CellKind.Int, Capacity: 2, Cells: [
            new StateCell(Name("a"), Value: 5),
            new StateCell(Name("b"), Value: 9),
        ]);
        // Pre-derived, as a compose/install pass would already have left it — a frame load reads the row's own
        // cells; deriving them is the section host's job, not the frame's.
        var board = new StateRow(Name: Name("board"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf("map", Empty: -1), Inverse: new StateInverse(Name("tokens"), Name("codes")), Cells: [
            new StateCell(Name("0"), Value: 5),
            new StateCell(Name("1"), Value: 9),
        ]);
        var rows = new List<StateRow> { tokens, codes, board };
        var layout = new FrameLayout(rows: rows, topology: name => (name == "map" ? topology : null));
        var frame = new StateFrame(layout: layout, rows: rows);

        frame.Load(source: new RowStore(rows: rows));

        return (layout, frame, rows);
    }

    [Fact]
    public void AFilterReadsDenseCellsCreatedAfterFrameLoad() {
        var topology = TopologyCompilation.Compile(new LatticeTopology.Grid("map", new DocumentVector3(0, 0, 0), 1, 4, 4), Vector3.Zero);
        var source = new StateRow(Name("values"), CellKind.Int, Capacity: 2, Cells: [new(Name("2"), 7), new(Name("3"), 11)]);
        var filter = new StateRow(Name("filter"), CellKind.Int, Domain: new StateDomain.CellsOf("map", Empty: -1), Cells: []);
        StateRow[] rows = [source, filter];
        var frame = new StateFrame(new FrameLayout(rows, _ => topology), rows);
        frame.Load(new RowStore(rows));
        Assert.Equal(0, StateReader.ReduceRaw(frame, source, StateReduceOp.Sum, 0, filter, null));
        Assert.True(frame.TryWrite(filter, Name("2"), 1, StateWriteKind.Set, out _));
        Assert.Equal(7, StateReader.ReduceRaw(frame, source, StateReduceOp.Sum, 0, filter, null));
        Assert.True(frame.TryWrite(filter, Name("2"), 0, StateWriteKind.Set, out _));
        Assert.Equal(0, StateReader.ReduceRaw(frame, source, StateReduceOp.Sum, 0, filter, null));
        Assert.Empty(filter.Cells!);
    }

    [Fact]
    public void RelocatingATokenRewritesTheDerivedBoardInTheFrameAndLeavesTheBaseRowsUntouched() {
        var (layout, frame, rows) = Build();
        var tokensOrdinal = layout.TryOrdinal(name: "tokens", ordinal: out var ordinal) ? ordinal : -1;
        var boardOrdinal = layout.TryOrdinal(name: "board", ordinal: out var board) ? board : -1;

        Assert.NotEqual(-1, tokensOrdinal);
        Assert.NotEqual(-1, boardOrdinal);
        Assert.Equal(new[] { boardOrdinal }, layout.DependentBoards(tokensOrdinal: tokensOrdinal));

        var boardLayout = layout[boardOrdinal];

        // Before the move: cell 0 carries token "a"'s code, cell 2 is still empty.
        Assert.Equal(5, frame.Values[boardLayout.Offset + 0]);
        Assert.Equal(-1, frame.Values[boardLayout.Offset + 2]);

        Assert.True(frame.TryWrite(row: rows[tokensOrdinal], key: Name("a"), value: 2, write: StateWriteKind.Set, reason: out var reason), reason);

        // After: the vacated cell reads empty, the entered cell reads the relocated token's code, and the untouched
        // token's cell is unaffected.
        Assert.Equal(-1, frame.Values[boardLayout.Offset + 0]);
        Assert.Equal(5, frame.Values[boardLayout.Offset + 2]);
        Assert.Equal(9, frame.Values[boardLayout.Offset + 1]);

        // The base rows the frame was built from never move — only the frame's own dense array does.
        var baseBoard = rows[boardOrdinal];
        Assert.Equal(5, baseBoard.Cells!.Single(c => c.Key.Value == "0").Value);
        Assert.DoesNotContain(baseBoard.Cells!, c => c.Key.Value == "2");
        var baseTokens = rows[tokensOrdinal];
        Assert.Equal(0, baseTokens.Cells!.Single(c => c.Key.Value == "a").Value);
    }

    [Fact]
    public void ADerivedBoardRefusesADirectFrameWrite() {
        var (_, frame, rows) = Build();
        var board = rows.Single(r => r.Name.Value == "board");

        Assert.False(frame.TryWrite(row: board, key: Name("0"), value: 3, write: StateWriteKind.Set, reason: out var reason));
        Assert.Contains("derived board", reason);
    }

    [Fact]
    public void TwoTokensOnOneCellLetsTheLaterTokenInRowOrderWin() {
        var (layout, frame, rows) = Build();
        var boardOrdinal = layout.TryOrdinal(name: "board", ordinal: out var board) ? board : -1;
        var boardLayout = layout[boardOrdinal];
        var tokens = rows.Single(r => r.Name.Value == "tokens");

        // "b" (row order index 1, after "a") relocates onto "a"'s cell — the later token wins the cell.
        Assert.True(frame.TryWrite(row: tokens, key: Name("b"), value: 0, write: StateWriteKind.Set, reason: out var reason), reason);
        Assert.Equal(9, frame.Values[boardLayout.Offset + 0]);
        Assert.Equal(-1, frame.Values[boardLayout.Offset + 1]);
    }
}

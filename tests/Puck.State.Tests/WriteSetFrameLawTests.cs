using System.Numerics;
using Xunit;
using Puck.Assets.Documents;

namespace Puck.State.Tests;

/// <summary>The one board-writing transform, evaluated over a <see cref="StateFrame"/> rather than an installed
/// section: a mask read through the frame's own store lands on the frame's dense board without moving a base row.</summary>
public sealed class WriteSetFrameLawTests {
    private static CompiledTopology Board() => TopologyCompilation.Compile(
        topology: new LatticeTopology.Grid(Name: "map", Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f), CellSize: 1f, Width: 2, Depth: 2),
        anchorOffset: Vector3.Zero
    );

    private static CellName Name(string value) => CellName.Parse(candidate: value);

    [Fact]
    public void WriteSetPaintsTheMaskFromTheFramesOwnStoreAndNeverTouchesTheBaseRows() {
        var topology = Board();
        var board = new StateRow(Name: Name(value: "board"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "map"));
        var legal = new StateRow(Name: Name(value: "legal"), Kind: CellKind.Int, Cells: [
            new StateCell(Key: Name(value: "0"), Value: 0b0110L),
            new StateCell(Key: Name(value: "1"), Value: 0b1001L),
        ]);
        StateRow[] rows = [board, legal];
        var layout = new FrameLayout(rows: rows, topology: candidate => ((candidate == "map") ? topology : null));
        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(source: new RowStore(rows: rows));

        Assert.True(frame.TryWriteSet(writeSet: new StateTransform.WriteSet(Row: "board", Set: "legal", SetKey: "0", Value: 5L), reason: out var reason), reason);

        Assert.True(frame.TryStored(row: board, key: Name(value: "1"), value: out var one, text: out _));
        Assert.Equal(5L, one);
        Assert.True(frame.TryStored(row: board, key: Name(value: "2"), value: out var two, text: out _));
        Assert.Equal(5L, two);
        Assert.False(frame.TryStored(row: board, key: Name(value: "0"), value: out _, text: out _));
        Assert.False(frame.TryStored(row: board, key: Name(value: "3"), value: out _, text: out _));

        // The base rows carry no cells of their own — the frame's array is the only thing that moved.
        Assert.Null(board.Cells);
        Assert.Equal(2, legal.Cells!.Count);

        // Painting from the other set cell touches only the cells its own mask names.
        Assert.True(frame.TryWriteSet(writeSet: new StateTransform.WriteSet(Row: "board", Set: "legal", SetKey: "1", Value: 7L), reason: out reason), reason);
        Assert.True(frame.TryStored(row: board, key: Name(value: "0"), value: out var zero, text: out _));
        Assert.Equal(7L, zero);
        Assert.True(frame.TryStored(row: board, key: Name(value: "3"), value: out var three, text: out _));
        Assert.Equal(7L, three);
        // Cells 1 and 2, painted by the first call and outside the second mask, are untouched: writeSet only ever
        // writes the cells its own mask names.
        Assert.True(frame.TryStored(row: board, key: Name(value: "1"), value: out var stillOne, text: out _));
        Assert.Equal(5L, stillOne);
    }

    [Fact]
    public void WriteSetRefusesByNameWhenTheSetCellIsMissing() {
        var topology = Board();
        var board = new StateRow(Name: Name(value: "board"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "map"));
        var legal = new StateRow(Name: Name(value: "legal"), Kind: CellKind.Int, Cells: [new StateCell(Key: Name(value: "0"), Value: 0b0001L)]);
        StateRow[] rows = [board, legal];
        var layout = new FrameLayout(rows: rows, topology: candidate => ((candidate == "map") ? topology : null));
        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(source: new RowStore(rows: rows));

        Assert.False(frame.TryWriteSet(writeSet: new StateTransform.WriteSet(Row: "board", Set: "legal", SetKey: "missing", Value: 1L), reason: out var reason));
        Assert.Contains("legal", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteSetRefusesByNameWhenTheRowIsNotABoardInTheFrame() {
        var topology = Board();
        var board = new StateRow(Name: Name(value: "board"), Kind: CellKind.Int, Domain: new StateDomain.CellsOf(Topology: "map"));
        var legal = new StateRow(Name: Name(value: "legal"), Kind: CellKind.Int, Cells: [new StateCell(Key: Name(value: "0"), Value: 0b0001L)]);
        StateRow[] rows = [board, legal];
        var layout = new FrameLayout(rows: rows, topology: candidate => ((candidate == "map") ? topology : null));
        var frame = new StateFrame(layout: layout, rows: rows);

        Assert.False(frame.TryWriteSet(writeSet: new StateTransform.WriteSet(Row: "legal", Set: "legal", SetKey: "0", Value: 1L), reason: out var reason));
        Assert.Contains("legal", reason, StringComparison.Ordinal);
    }
}

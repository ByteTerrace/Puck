using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: every <see cref="StateFrame"/> write door — <see cref="StateFrame.TryWrite(StateRow,CellName,long,StateWriteKind,out string)"/>
/// (slot and keyed cells), <see cref="StateFrame.TryPush(StateRow,long,out string)"/> (a ring), and
/// <see cref="StateFrame.TryBoardCombine"/>/<see cref="StateFrame.TryWriteSet"/> (a board) — decides admission
/// through the row's own <see cref="StateRow.TryAdmitWrite"/>, never a private clamp. Expected admitted/stored values
/// are computed independently from the row's Min/Max/Overflow, never by calling the frame.</summary>
public sealed class StateFrameEnvelopeLawTests {
    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static (FrameLayout Layout, StateFrame Frame) Over(params StateRow[] rows) {
        var layout = new FrameLayout(
            rows: rows,
            topology: static _ => null
        );
        var frame = new StateFrame(
            layout: layout,
            rows: rows
        );

        frame.Load(source: new RowStore(rows: rows));

        return (layout, frame);
    }
    private static long Saturated(long current, long operand, StateWriteKind write, long? min, long? max) {
        var exact = ((write == StateWriteKind.Add)
            ? (((Int128)current) + operand)
            : ((Int128)operand)
        );
        var lower = ((Int128)(min ?? long.MinValue));
        var upper = ((Int128)(max ?? long.MaxValue));

        return (long)((exact < lower)
            ? lower
            : ((exact > upper)
                ? upper
                : exact
        ));
    }
    private static StateRow Slot(string name, long value, long? min, long? max, StateOverflow overflow = StateOverflow.Refuse) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Min: min,
        Max: max,
        Overflow: overflow,
        Cells: [new StateCell(
                Key: StateRow.SlotKey,
                Value: value
            )]
    );

    // The frame's own no-wrap rule for a keyed cell, exercised through TryWrite: a Set past a declared bound
    // refuses under Refuse and clamps under Saturate, on both the declared side and the undeclared (64-bit) side.
    [InlineData(50L, 200L, StateWriteKind.Set, 0L, 100L, StateOverflow.Refuse, false)]
    [InlineData(50L, 200L, StateWriteKind.Set, 0L, 100L, StateOverflow.Saturate, true)]
    [InlineData(90L, 50L, StateWriteKind.Add, 0L, 100L, StateOverflow.Saturate, true)]
    [InlineData(10L, -50L, StateWriteKind.Add, 0L, 100L, StateOverflow.Refuse, false)]
    [InlineData(long.MaxValue, 1L, StateWriteKind.Add, null, null, StateOverflow.Saturate, true)]
    [InlineData(long.MaxValue, 1L, StateWriteKind.Add, null, null, StateOverflow.Refuse, false)]
    [Theory]
    public void TryWriteDecidesAKeyedCellThroughTheRowsEnvelope(long current, long operand, StateWriteKind write, long? min, long? max, StateOverflow overflow, bool expectAdmitted) {
        var row = new StateRow(
            Name: Name(value: "meter"),
            Kind: CellKind.Int,
            Min: min,
            Max: max,
            Overflow: overflow,
            Capacity: 2,
            Cells: [new StateCell(
                    Key: Name(value: "a"),
                    Value: current
                ), new StateCell(
                    Key: Name(value: "b"),
                    Value: 0L
                )]
        );
        var (_, frame) = Over(row);

        var accepted = frame.TryWrite(
            row: row,
            key: Name(value: "a"),
            value: operand,
            write: write,
            reason: out var reason
        );

        Assert.Equal(
            expected: expectAdmitted,
            actual: accepted
        );

        Assert.True(condition: frame.TryStored(
            row: row,
            key: Name(value: "a"),
            value: out var stored,
            text: out _
        ));

        if (expectAdmitted) {
            Assert.Empty(collection: reason);
            Assert.Equal(
                expected: Saturated(
                    current: current,
                    operand: operand,
                    write: write,
                    min: min,
                    max: max
                ),
                actual: stored
            );
        } else {
            Assert.NotEmpty(collection: reason);
            // A refused write never touches the cell.
            Assert.Equal(
                expected: current,
                actual: stored
            );
        }
    }
    // The same door for a slot cell, including a one-sided range on either side.
    [InlineData(5L, -100L, StateWriteKind.Add, 0L, null, StateOverflow.Saturate, 0L)]
    [InlineData(5L, -100L, StateWriteKind.Add, 0L, null, StateOverflow.Refuse, 5L)]
    [InlineData(-5L, 100L, StateWriteKind.Set, null, 10L, StateOverflow.Saturate, 10L)]
    [Theory]
    public void TryWriteDecidesASlotCellThroughTheRowsEnvelopeOnAOneSidedRange(long current, long operand, StateWriteKind write, long? min, long? max, StateOverflow overflow, long expectedFinal) {
        var row = Slot(
            name: "gauge",
            value: current,
            min: min,
            max: max,
            overflow: overflow
        );
        var (_, frame) = Over(row);

        _ = frame.TryWrite(
            row: row,
            key: StateRow.SlotKey,
            value: operand,
            write: write,
            reason: out _
        );

        Assert.True(condition: frame.TryStored(
            row: row,
            key: StateRow.SlotKey,
            value: out var stored,
            text: out _
        ));
        Assert.Equal(
            expected: expectedFinal,
            actual: stored
        );
    }
    // A ring push admits through the same door: a value outside the row's declared envelope refuses the push
    // outright rather than landing an unclamped value in the ring.
    [Fact]
    public void TryPushRefusesAValueOutsideTheRowsEnvelope() {
        var row = new StateRow(
            Name: Name(value: "history"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 10L,
            Domain: new StateDomain.Ring(
                Capacity: 3,
                Empty: -1L
            )
        );
        var (_, frame) = Over(row);

        Assert.False(condition: frame.TryPush(
            reason: out var reason,
            row: row,
            value: 99L
        ));
        Assert.NotEmpty(collection: reason);
        Assert.True(condition: frame.TryPush(
            reason: out reason,
            row: row,
            value: 7L
        ));
        Assert.Empty(collection: reason);
    }
    // A ring push saturates the same way any other write does when the row opts in.
    [Fact]
    public void TryPushSaturatesAValueOutsideTheRowsEnvelopeWhenTheRowSaturates() {
        var row = new StateRow(
            Name: Name(value: "history"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 10L,
            Overflow: StateOverflow.Saturate,
            Domain: new StateDomain.Ring(
                Capacity: 3,
                Empty: -1L
            )
        );
        var (_, frame) = Over(row);

        Assert.True(condition: frame.TryPush(
            reason: out var reason,
            row: row,
            value: 99L
        ));
        Assert.Empty(collection: reason);
        Assert.True(condition: frame.TryStoredAt(
            row: row,
            index: 0,
            value: out var pushed
        ));
        Assert.Equal(
            expected: 10L,
            actual: pushed
        );
    }
    // A board's fill member value goes through TryValidate -> TryAdmitWrite, exercised via TryBoardCombine: a
    // literal outside the board row's declared envelope either refuses or lands its clamped value on every cell.
    [InlineData(5L, StateOverflow.Refuse, false, 0L)]
    [InlineData(5L, StateOverflow.Saturate, true, 3L)]
    [Theory]
    public void TryBoardCombineDecidesTheFillValueThroughTheRowsEnvelope(long fillValue, StateOverflow overflow, bool expectAdmitted, long expectedStored) {
        var topology = TopologyCompilation.Compile(
            topology: new LatticeTopology.Grid(
                Name: "map",
                Origin: new Puck.Assets.Documents.DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                CellSize: 1f,
                Width: 2,
                Depth: 2
            ),
            anchorOffset: System.Numerics.Vector3.Zero
        );
        var board = new StateRow(
            Name: Name(value: "board"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 3L,
            Overflow: overflow,
            Domain: new StateDomain.CellsOf(
                Empty: -1L,
                Topology: "map"
            )
        );
        var layout = new FrameLayout(
            rows: [board],
            topology: candidate => ((candidate == "map")
                ? topology
                : null
            )
        );
        var frame = new StateFrame(
            layout: layout,
            rows: [board]
        );

        frame.Load(source: new RowStore(rows: [board]));

        var accepted = frame.TryBoardCombine(
            combine: new StateTransform.BoardCombine(
                Row: "board",
                Operation: BoardCombineOp.Fill,
                Value: fillValue
            ),
            reason: out var reason
        );

        Assert.Equal(
            expected: expectAdmitted,
            actual: accepted
        );

        if (expectAdmitted) {
            Assert.Empty(collection: reason);
            for (var cell = 0; (cell < 4); cell++) {
                Assert.True(condition: frame.TryStored(
                    row: board,
                    key: Name(value: cell.ToString()),
                    value: out var value,
                    text: out _
                ));
                Assert.Equal(
                    expected: expectedStored,
                    actual: value
                );
            }
        } else {
            Assert.NotEmpty(collection: reason);
            for (var cell = 0; (cell < 4); cell++) {
                // The board's cells still sit at its declared empty value; TryStored answers false for a cell
                // never authored, exactly as an unwritten board cell always reads.
                Assert.False(condition: frame.TryStored(
                    row: board,
                    key: Name(value: cell.ToString()),
                    value: out _,
                    text: out _
                ));
            }
        }
    }
    // A writeSet's painted value is admitted through the same door as any other board write.
    [Fact]
    public void TryWriteSetRefusesAPaintedValueOutsideTheBoardsEnvelope() {
        var topology = TopologyCompilation.Compile(
            topology: new LatticeTopology.Grid(
                Name: "map",
                Origin: new Puck.Assets.Documents.DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                CellSize: 1f,
                Width: 2,
                Depth: 2
            ),
            anchorOffset: System.Numerics.Vector3.Zero
        );
        var board = new StateRow(
            Name: Name(value: "board"),
            Kind: CellKind.Int,
            Min: 0L,
            Max: 3L,
            Domain: new StateDomain.CellsOf(Topology: "map")
        );
        var legal = new StateRow(
            Name: Name(value: "legal"),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                    Key: Name(value: "0"),
                    Value: 0b0001L
                )]
        );
        StateRow[] rows = [board, legal];
        var layout = new FrameLayout(
            rows: rows,
            topology: candidate => ((candidate == "map")
                ? topology
                : null
            )
        );
        var frame = new StateFrame(
            layout: layout,
            rows: rows
        );

        frame.Load(source: new RowStore(rows: rows));

        Assert.False(condition: frame.TryWriteSet(
            writeSet: new StateTransform.WriteSet(
                Row: "board",
                Set: "legal",
                SetKey: "0",
                Value: 99L
            ),
            reason: out var reason
        ));
        Assert.NotEmpty(collection: reason);
    }
}

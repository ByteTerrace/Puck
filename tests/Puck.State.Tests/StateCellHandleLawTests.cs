using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Tests;

/// <summary>Guards static cell handle interning, cell table lookup, and direct frame read/write through
/// <see cref="StateCellHandle"/>.</summary>
public sealed class StateCellHandleLawTests {
    private sealed class Section(params StateRow[] rows) : IStateSection {
        public IReadOnlyList<StateRow> Rows { get; } = rows;
        public IReadOnlyList<IStateSlot>? IdentitySlots => null;
        public IReadOnlyList<LatticeTopology>? Lattices => null;
        public IReadOnlyList<IStateSlot>? ParticipantSlots => null;
    }

    [Fact]
    public void Builder_InternsRowAndKey_Idempotently() {
        var rowA = new StateRow(
            Name: CellName.Parse(candidate: "rowA"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 1L)]
        );
        var rowB = new StateRow(
            Name: CellName.Parse(candidate: "rowB"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 2L)]
        );

        var catalog = StateCatalog.Compile(section: new Section(rowA, rowB));
        Assert.True(catalog.TryResolve(StateLane.Document, "rowA", out var rowHandle1));
        Assert.True(catalog.TryResolve(StateLane.Document, "rowB", out var rowHandle2));

        var builder = new StateCellTableBuilder();
        var keyA = CellName.Parse(candidate: "hp");
        var keyB = CellName.Parse(candidate: "mana");

        var handle1A = builder.Intern(rowHandle: rowHandle1, key: keyA);
        var handle1B = builder.Intern(rowHandle: rowHandle1, key: keyB);
        var handle2A = builder.Intern(rowHandle: rowHandle2, key: keyA);

        Assert.True(handle1A.IsValid);
        Assert.True(handle1B.IsValid);
        Assert.True(handle2A.IsValid);

        Assert.Equal(0, handle1A.Ordinal);
        Assert.Equal(1, handle1B.Ordinal);
        Assert.Equal(2, handle2A.Ordinal);

        // Re-interning same pair returns identical handle
        var reInterned = builder.Intern(rowHandle: rowHandle1, key: keyA);
        Assert.Equal(handle1A, reInterned);

        // Invalid row handle returns invalid cell handle
        Assert.False(builder.Intern(rowHandle: default, key: keyA).IsValid);

        var table = builder.Build();
        Assert.Equal(3, table.Count);
        Assert.Equal(keyA, table[handle1A].Key);
        Assert.Equal(rowHandle1, table[handle1A].RowHandle);

        Assert.True(table.TryResolve(rowHandle: rowHandle1, key: keyA, out var resolved));
        Assert.Equal(handle1A, resolved);
    }

    [Fact]
    public void StateFrame_TryReadAndWriteCellHandle_OperatesDirectly() {
        var slotRow = new StateRow(
            Name: CellName.Parse(candidate: "gauge"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 42L)]
        );
        var keyedRow = new StateRow(
            Name: CellName.Parse(candidate: "stats"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "str"), Value: 18L),
                new StateCell(Key: CellName.Parse(candidate: "dex"), Value: 14L),
            ]
        );

        var rows = new StateRow[] { slotRow, keyedRow };
        var catalog = StateCatalog.Compile(section: new Section(slotRow, keyedRow));
        Assert.True(catalog.TryResolve(StateLane.Document, "gauge", out var slotHandle));
        Assert.True(catalog.TryResolve(StateLane.Document, "stats", out var keyedHandle));

        var builder = new StateCellTableBuilder();
        var cellSlot = builder.Intern(rowHandle: slotHandle, key: StateRow.SlotKey);
        var cellStr = builder.Intern(rowHandle: keyedHandle, key: CellName.Parse(candidate: "str"));
        var cellDex = builder.Intern(rowHandle: keyedHandle, key: CellName.Parse(candidate: "dex"));
        var cellMissing = builder.Intern(rowHandle: keyedHandle, key: CellName.Parse(candidate: "int"));

        var cellTable = builder.Build();
        var layout = new FrameLayout(
            rows: rows,
            topology: _ => null,
            cellTable: cellTable
        );

        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(new RowStore(rows));

        // Read slot
        Assert.True(frame.TryReadCellHandle(handle: cellSlot, out var slotVal));
        Assert.Equal(42L, slotVal);

        // Read keyed cells
        Assert.True(frame.TryReadCellHandle(handle: cellStr, out var strVal));
        Assert.Equal(18L, strVal);
        Assert.True(frame.TryReadCellHandle(handle: cellDex, out var dexVal));
        Assert.Equal(14L, dexVal);

        // Missing cell maps to -1 offset
        Assert.False(frame.TryReadCellHandle(handle: cellMissing, out _));

        // Direct write through handle
        Assert.True(frame.TryWriteCellHandle(
            handle: cellStr,
            value: 20L,
            write: StateWriteKind.Set,
            reason: out var reason
        ));
        Assert.Empty(reason);

        Assert.True(frame.TryReadCellHandle(handle: cellStr, out var updatedStr));
        Assert.Equal(20L, updatedStr);
    }

    [Fact]
    public void TryResolve_WithMismatchedCatalogHandle_ReturnsFalse() {
        var rowA1 = new StateRow(
            Name: CellName.Parse(candidate: "rowA"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 1L)]
        );
        var rowA2 = new StateRow(
            Name: CellName.Parse(candidate: "rowA"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 1L)]
        );

        var catalog1 = StateCatalog.Compile(section: new Section(rowA1));
        var catalog2 = StateCatalog.Compile(section: new Section(rowA2));

        Assert.True(catalog1.TryResolve(StateLane.Document, "rowA", out var handle1));
        Assert.True(catalog2.TryResolve(StateLane.Document, "rowA", out var handle2));

        var builder = new StateCellTableBuilder();
        var cellHandle = builder.Intern(rowHandle: handle1, key: StateRow.SlotKey);
        var table = builder.Build();

        // Handle from matching catalog succeeds
        Assert.True(table.TryResolve(rowHandle: handle1, key: StateRow.SlotKey, out var res1));
        Assert.Equal(cellHandle, res1);

        // Handle from different catalog instance fails even if same ordinal and name
        Assert.False(table.TryResolve(rowHandle: handle2, key: StateRow.SlotKey, out var res2));
        Assert.Equal(StateCellHandle.Invalid, res2);
    }

    [Fact]
    public void BoardCellHandle_RespectsAbsenceRule() {
        // Topology with 2 cells (0 and 1)
        var topology = TopologyCompilation.Compile(
            topology: new LatticeTopology.Grid(
                Name: "line",
                Origin: new DocumentVector3(0f, 0f, 0f),
                CellSize: 1f,
                Width: 2,
                Depth: 1
            ),
            anchorOffset: System.Numerics.Vector3.Zero
        );

        // Authored board row only has cell "0" with value 0; cell "1" is unauthored
        var boardRow = new StateRow(
            Name: CellName.Parse(candidate: "board"),
            Kind: CellKind.Int,
            Domain: new StateDomain.CellsOf(Topology: "line"),
            Cells: [new StateCell(Key: CellName.Parse(candidate: "0"), Value: 0L)]
        );

        var catalog = StateCatalog.Compile(section: new Section(boardRow));
        Assert.True(catalog.TryResolve(StateLane.Document, "board", out var boardHandle));

        var builder = new StateCellTableBuilder();
        var handleCell0 = builder.Intern(rowHandle: boardHandle, key: CellName.Parse(candidate: "0"));
        var handleCell1 = builder.Intern(rowHandle: boardHandle, key: CellName.Parse(candidate: "1"));
        var cellTable = builder.Build();

        var rows = new StateRow[] { boardRow };
        var layout = new FrameLayout(
            rows: rows,
            topology: name => (name == "line" ? topology : null),
            cellTable: cellTable
        );

        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(new RowStore(rows));

        // Cell 0 is authored at 0 (the empty value) -> reads present (true, 0)
        Assert.True(frame.TryReadCellHandle(handle: handleCell0, out var val0));
        Assert.Equal(0L, val0);

        // Cell 1 is unauthored and at empty value 0 -> reads absent (false) per absence contract
        Assert.False(frame.TryReadCellHandle(handle: handleCell1, out _));
    }

    [Fact]
    public void DerivedBoard_RefusesDirectCellHandleWrite() {
        var topology = TopologyCompilation.Compile(
            topology: new LatticeTopology.Grid(
                Name: "single",
                Origin: new DocumentVector3(0f, 0f, 0f),
                CellSize: 1f,
                Width: 1,
                Depth: 1
            ),
            anchorOffset: System.Numerics.Vector3.Zero
        );

        var tokensRow = new StateRow(
            Name: CellName.Parse(candidate: "tokens"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 0L)]
        );
        var codesRow = new StateRow(
            Name: CellName.Parse(candidate: "codes"),
            Kind: CellKind.Int,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 1L)]
        );
        var derivedBoard = new StateRow(
            Name: CellName.Parse(candidate: "board"),
            Kind: CellKind.Int,
            Domain: new StateDomain.CellsOf(Topology: "single"),
            Inverse: new StateInverse(Tokens: CellName.Parse(candidate: "tokens"), Codes: CellName.Parse(candidate: "codes")),
            Cells: [new StateCell(Key: CellName.Parse(candidate: "0"), Value: 1L)]
        );

        var rows = new StateRow[] { tokensRow, codesRow, derivedBoard };
        var catalog = StateCatalog.Compile(section: new Section(tokensRow, codesRow, derivedBoard));
        Assert.True(catalog.TryResolve(StateLane.Document, "board", out var boardHandle));

        var builder = new StateCellTableBuilder();
        var cellHandle = builder.Intern(rowHandle: boardHandle, key: CellName.Parse(candidate: "0"));
        var cellTable = builder.Build();

        var layout = new FrameLayout(
            rows: rows,
            topology: name => (name == "single" ? topology : null),
            cellTable: cellTable
        );

        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(new RowStore(rows));

        Assert.False(frame.TryWriteCellHandle(
            handle: cellHandle,
            value: 5L,
            write: StateWriteKind.Set,
            reason: out var reason
        ));
        Assert.Contains("derived board", reason);
    }

    [Fact]
    public void BoolRow_RefusesValuesOutsideEnvelope() {
        var boolRow = new StateRow(
            Name: CellName.Parse(candidate: "flag"),
            Kind: CellKind.Bool,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 0L)]
        );

        var catalog = StateCatalog.Compile(section: new Section(boolRow));
        Assert.True(catalog.TryResolve(StateLane.Document, "flag", out var handle));

        var builder = new StateCellTableBuilder();
        var cellHandle = builder.Intern(rowHandle: handle, key: StateRow.SlotKey);
        var cellTable = builder.Build();

        var rows = new StateRow[] { boolRow };
        var layout = new FrameLayout(
            rows: rows,
            topology: _ => null,
            cellTable: cellTable
        );

        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(new RowStore(rows));

        Assert.False(frame.TryWriteCellHandle(
            handle: cellHandle,
            value: 2L,
            write: StateWriteKind.Set,
            reason: out var reason
        ));
        Assert.Contains("would leave the row's envelope", reason);
    }

    [Fact]
    public void UnframedRow_ReturnsNegativeOffset() {
        var textRow = new StateRow(
            Name: CellName.Parse(candidate: "title"),
            Kind: CellKind.Text,
            Cells: [new StateCell(Key: StateRow.SlotKey, Value: 0L, Text: "hello")]
        );

        var catalog = StateCatalog.Compile(section: new Section(textRow));
        Assert.True(catalog.TryResolve(StateLane.Document, "title", out var handle));

        var builder = new StateCellTableBuilder();
        var cellHandle = builder.Intern(rowHandle: handle, key: StateRow.SlotKey);
        var cellTable = builder.Build();

        var rows = new StateRow[] { textRow };
        var layout = new FrameLayout(
            rows: rows,
            topology: _ => null,
            cellTable: cellTable
        );

        Assert.Equal(-1, layout.CellOffset(cellHandle));

        var frame = new StateFrame(layout: layout, rows: rows);
        Assert.False(frame.TryReadCellHandle(handle: cellHandle, out _));
        Assert.False(frame.TryWriteCellHandle(handle: cellHandle, value: 1L, write: StateWriteKind.Set, reason: out var reason));
        Assert.Equal("cell handle is not framed", reason);
    }
}

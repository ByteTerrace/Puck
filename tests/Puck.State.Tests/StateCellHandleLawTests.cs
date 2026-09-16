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
}

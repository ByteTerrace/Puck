using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="ArenaLayout.Bytes"/> is an upper bound on what an arena over the layout
/// allocates, with every lazily allocated column forced; a section that lays out past
/// <see cref="ArenaCapacity.MaxBytes"/> is refused at the row that crossed, before anything is allocated for it; and
/// the journal reads over its ceiling exactly while the record it holds is past it.</summary>
public sealed class ArenaByteCeilingLawTests {
    private static StateSection Rings(int count) => new(Rows: [.. Enumerable.Range(
            count: count,
            start: 0
        ).Select(selector: static index => new StateRow(
            Name: ArenaFixture.Name(value: $"r{index}"),
            Kind: CellKind.Int,
            Domain: new StateDomain.Ring(
                Capacity: StateCapacity.MaxCellsPerRow,
                Empty: -1L
            )
        ))]);
    private static StateSection Pool(int capacity) => new(Rows: [
        new StateRow(
            Name: ArenaFixture.Name(value: "pool"),
            Kind: CellKind.Int,
            Capacity: capacity,
            Cells: [new StateCell(
                    Key: ArenaFixture.Name(value: "a"),
                    Value: CellValue.Int(value: 1L)
                )]
        ),
        new StateRow(
            Name: ArenaFixture.Name(value: "label"),
            Kind: CellKind.Text,
            Capacity: capacity,
            Cells: [new StateCell(
                    Key: ArenaFixture.Name(value: "a"),
                    Value: CellValue.Text(value: "hi")
                )]
        ),
    ]);
    // Builds the arena, writes every column that is allocated on first use, and settles one scope so the change
    // stamps exist, returning what all of it allocated and what the layout measured.
    private static (long Allocated, long Measured) Footprint(int capacity) {
        var section = Pool(capacity: capacity);
        var catalog = StateCatalog.Compile(section: section);
        var a = ArenaFixture.Key(
            catalog: catalog,
            value: "a"
        );
        var before = GC.GetAllocatedBytesForCurrentThread();
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var mark = arena.BeginScope();

        Assert.True(condition: arena.TryWriteClock(
            epochEngineTick: 4L,
            epochTick: 3L,
            key: a,
            reason: out _,
            rowOrdinal: 0,
            substepTicks: 7L,
            v0: 6L,
            y0: 5L
        ));
        Assert.True(condition: arena.TryWriteProvenance(
            key: a,
            provenance: "issuer",
            rowOrdinal: 0
        ));
        Assert.True(condition: arena.TryWriteBehavior(
            behavior: StateCellBehavior.None,
            key: a,
            rowOrdinal: 0
        ));
        Assert.True(condition: arena.TryWriteVisibility(
            key: a,
            rowOrdinal: 0,
            visibility: new StateVisibility(Readers: ["p1"])
        ));
        Assert.True(condition: arena.TryWriteObservation(
            key: a,
            observation: new StateObservation(
                Tick: 12L,
                Visible: true
            ),
            rowOrdinal: 0
        ));
        Assert.True(condition: arena.TryWriteText(
            key: a,
            reason: out _,
            rowOrdinal: 1,
            text: "bye"
        ));
        arena.Commit(mark: mark);

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        GC.KeepAlive(obj: arena);

        return (allocated, arena.Layout.Bytes);
    }

    [Fact]
    public void AnArenaAllocatesNoMoreThanItsLayoutMeasures() {
        // The difference between two capacities cancels what an arena allocates whatever its size, leaving what it
        // allocates per cell slot, which is what the measure has to cover.
        _ = Footprint(capacity: 16);

        var small = Footprint(capacity: 256);
        var large = Footprint(capacity: StateCapacity.MaxCellsPerRow);

        Assert.True(
            condition: ((large.Allocated - small.Allocated) <= (large.Measured - small.Measured)),
            userMessage: $"allocated {(large.Allocated - small.Allocated)} bytes for cells the layout measured at {(large.Measured - small.Measured)}"
        );
        Assert.True(condition: (small.Measured < large.Measured));
    }
    [Fact]
    public void ASectionPastTheByteCeilingIsRefusedAtTheRowThatCrossed() {
        var fits = 0L;
        var crossed = -1;

        for (var count = 1; (count <= StateCapacity.MaxRows); count++) {
            var section = Rings(count: count);

            if (ArenaLayout.TryBuild(
                catalog: StateCatalog.Compile(section: section),
                layout: out var layout,
                options: null,
                reason: out var reason,
                section: section
            )) {
                Assert.True(condition: (layout.Bytes > fits));
                Assert.True(condition: (layout.Bytes <= ArenaCapacity.MaxBytes));

                fits = layout.Bytes;

                continue;
            }

            crossed = count;

            Assert.Contains(
                actualString: reason,
                expectedSubstring: $"'r{(count - 1)}'"
            );
            Assert.Contains(
                actualString: reason,
                expectedSubstring: $"{ArenaCapacity.MaxBytes}-byte ceiling"
            );

            break;
        }

        // Full-width rows reach the ceiling well inside the row count, so the bytes are what bound a document.
        Assert.InRange(
            actual: crossed,
            high: StateCapacity.MaxRows,
            low: 2
        );
    }
    [Fact]
    public void ARefusedSectionThrowsFromTheArenaThatWouldHaveHeldIt() {
        var section = Rings(count: StateCapacity.MaxRows);
        var catalog = StateCatalog.Compile(section: section);

        var thrown = Assert.Throws<InvalidOperationException>(testCode: () => new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        ));

        Assert.Contains(
            actualString: thrown.Message,
            expectedSubstring: "byte ceiling"
        );
    }
    [Fact]
    public void TheJournalReadsOverItsCeilingExactlyWhileItsRecordIsPastIt() {
        var journal = new ArenaJournal();
        var outer = journal.BeginScope();
        var entries = (ArenaCapacity.MaxJournalBytes / ArenaJournal.EntryBytes);

        for (var index = 0; (index < entries); index++) {
            journal.Record(
                column: ArenaColumn.Number,
                index: 0,
                previous: index
            );
        }

        Assert.False(condition: journal.OverCeiling);

        var inner = journal.BeginScope();

        journal.Record(
            column: ArenaColumn.Number,
            index: 0,
            previous: 0L
        );

        Assert.True(condition: journal.OverCeiling);
        Assert.Equal(
            actual: journal.Bytes,
            expected: ((entries + 1L) * ArenaJournal.EntryBytes)
        );

        // A rewound savepoint hands its record back, and the reading follows the record.
        _ = journal.RewindScope(mark: inner);

        Assert.False(condition: journal.OverCeiling);

        // Snapshotted components count beside the entries.
        journal.RecordVector(
            index: 0,
            previous: new sbyte[ArenaJournal.EntryBytes]
        );

        Assert.True(condition: journal.OverCeiling);

        _ = journal.RewindScope(mark: outer);

        Assert.Equal(
            actual: journal.Bytes,
            expected: 0L
        );
    }
}

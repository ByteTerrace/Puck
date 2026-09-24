using Puck.Abstractions.Counting;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="StateArena.Bytes"/> is an upper bound on what an arena
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
        var measured = 0L;
        var allocated = AllocationWindow.Measure(window: () => {
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
            measured = arena.Bytes;
        });

        return (allocated, measured);
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

    private static (long Allocated, long Measured) PoolFootprint(int capacity) {
        var record = new StateRecord(Name: ArenaFixture.Name(value: "item"));
        var section = new StateSection(
            Records: [record],
            Pools: [new StatePool(Name: ArenaFixture.Name(value: "items"), Record: record.Name, Capacity: capacity)]
        );
        var catalog = StateCatalog.Compile(section: section);
        var layout = ArenaLayout.Build(catalog: catalog, options: null, section: section);
        var allocated = AllocationWindow.Measure(window: () => _ = new StateArena(catalog: catalog, layout: layout, section: section, time: ArenaTime.Origin));

        return (allocated, layout.Bytes);
    }

    [Fact]
    public void PoolIndexesAreInsideTheLayoutMeasure() {
        _ = PoolFootprint(capacity: 16);

        var small = PoolFootprint(capacity: 256);
        var large = PoolFootprint(capacity: StateCapacity.MaxCellsPerRow);

        Assert.True(
            condition: ((large.Allocated - small.Allocated) <= (large.Measured - small.Measured)),
            userMessage: $"allocated {(large.Allocated - small.Allocated)} bytes for pool storage the layout measured at {(large.Measured - small.Measured)}"
        );
        Assert.True(condition: (small.Measured < large.Measured));
    }

    // A text row with every cell present, so every slot can be written to its ceiling.
    private static StateSection Labels(int capacity) => new(Rows: [new StateRow(
            Name: ArenaFixture.Name(value: "labels"),
            Kind: CellKind.Text,
            Capacity: capacity,
            Cells: [.. Enumerable.Range(
                count: capacity,
                start: 0
            ).Select(selector: static index => new StateCell(
                Key: ArenaFixture.Name(value: $"k{index}"),
                Value: CellValue.Text(value: string.Empty)
            ))]
        )]);
    // What an arena allocates once every cell holds a text and a provenance at their length ceilings, the strings
    // themselves included, against what the layout measured.
    private static (long Allocated, long Measured) TextFootprint(int capacity) {
        var section = Labels(capacity: capacity);
        var catalog = StateCatalog.Compile(section: section);
        var keys = new CellKey[capacity];

        for (var index = 0; (index < capacity); index++) {
            keys[index] = ArenaFixture.Key(
                catalog: catalog,
                value: $"k{index}"
            );
        }

        var measured = 0L;
        var allocated = AllocationWindow.Measure(window: () => {
            var arena = new StateArena(
                catalog: catalog,
                options: null,
                section: section,
                time: ArenaTime.Origin
            );

            for (var index = 0; (index < capacity); index++) {
                Assert.True(condition: arena.TryWriteText(
                    key: keys[index],
                    reason: out _,
                    rowOrdinal: 0,
                    text: new string(
                        c: 't',
                        count: StateCapacity.MaxTextValueLength
                    )
                ));
                Assert.True(condition: arena.TryWriteProvenance(
                    key: keys[index],
                    provenance: new string(
                        c: 'p',
                        count: StateCapacity.MaxProvenanceLength
                    ),
                    rowOrdinal: 0
                ));
            }

            measured = arena.Bytes;
        });

        return (allocated, measured);
    }

    [Fact]
    public void TheStringsAnArenaRefersToAreInsideItsMeasure() {
        _ = TextFootprint(capacity: 16);

        var small = TextFootprint(capacity: 64);
        var large = TextFootprint(capacity: 1024);

        Assert.True(
            condition: ((large.Allocated - small.Allocated) <= (large.Measured - small.Measured)),
            userMessage: $"allocated {(large.Allocated - small.Allocated)} bytes for cells the layout measured at {(large.Measured - small.Measured)}"
        );
    }
    [Fact]
    public void AProvenancePastItsLengthCeilingIsRefusedAtBothDoors() {
        var section = Labels(capacity: 4);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var overlong = new string(
            c: 'p',
            count: (StateCapacity.MaxProvenanceLength + 1)
        );

        Assert.False(condition: arena.TryWriteProvenance(
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "k0"
            ),
            provenance: overlong,
            rowOrdinal: 0
        ));
        Assert.Null(@object: arena.Provenance(
            key: ArenaFixture.Key(
                catalog: catalog,
                value: "k0"
            ),
            rowOrdinal: 0
        ));

        var row = section.Rows![0];

        Assert.False(condition: (arena.TryLoad(
            reason: out var reason,
            rows: [(row with { Cells = [(row.Cells![0] with { Provenance = overlong })] })],
            time: ArenaTime.Origin
        ) && (reason.Length == 0)));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"past the {StateCapacity.MaxProvenanceLength}-character limit"
        );
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
    // Lane widths come from whoever builds the arena, not from the document, so the layout admits them itself: a
    // negative width is refused, widths whose roster alone passes the ceiling are refused though no row is declared,
    // and nothing is summed in a width that wraps.
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(int.MaxValue, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    [Theory]
    public void LaneWidthsTheArenaCannotHoldAreRefusedWithNoRowDeclared(int participants, int identities) {
        var section = new StateSection(Rows: []);

        Assert.False(condition: ArenaLayout.TryBuild(
            catalog: StateCatalog.Compile(section: section),
            layout: out _,
            options: new ArenaOptions(
                Identities: identities,
                Participants: participants
            ),
            reason: out var reason,
            section: section
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "lane"
        );
    }
    [InlineData(0, 0)]
    [InlineData(0, 4)]
    [InlineData(4096, 4096)]
    [Theory]
    public void AZeroWideLaneLaysOutAndAdmitsNoOrdinal(int participants, int identities) {
        var section = new StateSection(Rows: []);

        Assert.True(
            condition: ArenaLayout.TryBuild(
                catalog: StateCatalog.Compile(section: section),
                layout: out var layout,
                options: new ArenaOptions(
                    Identities: identities,
                    Participants: participants
                ),
                reason: out var reason,
                section: section
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: layout.LaneRosterCount,
            expected: (participants + identities)
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
    // A replaced text is kept alive by the record alone, so the record is charged for it: a scope that replaces
    // texts at their length ceiling reaches the journal's ceiling by their payload, long before its entries would.
    [Fact]
    public void TheRecordIsChargedForTheReferencesItRetains() {
        var journal = new ArenaJournal();
        var outer = journal.BeginScope();
        var text = new string(
            c: 't',
            count: StateCapacity.MaxTextValueLength
        );

        journal.RecordReference(
            column: ArenaColumn.Text,
            index: 0,
            previous: text
        );

        Assert.True(
            condition: (journal.Bytes >= (ArenaJournal.EntryBytes + (2L * text.Length))),
            userMessage: $"one retained text of {text.Length} characters is charged {journal.Bytes} bytes"
        );

        var one = journal.Bytes;
        var inner = journal.BeginScope();
        var replacements = ((ArenaCapacity.MaxJournalBytes / one) + 1L);

        for (var index = 0L; (index < replacements); index++) {
            journal.RecordReference(
                column: ArenaColumn.Text,
                index: 0,
                previous: text
            );
        }

        Assert.True(condition: journal.OverCeiling);
        Assert.True(condition: ((journal.Length * ((long)ArenaJournal.EntryBytes)) < (ArenaCapacity.MaxJournalBytes / 8)));

        // A rewound savepoint hands the payload back with the entries, and a null reference retains nothing.
        _ = journal.RewindScope(mark: inner);

        Assert.Equal(
            actual: journal.Bytes,
            expected: one
        );

        journal.RecordReference(
            column: ArenaColumn.Text,
            index: 0,
            previous: null
        );

        Assert.Equal(
            actual: journal.Bytes,
            expected: (one + ArenaJournal.EntryBytes)
        );

        _ = journal.CommitScope(mark: outer);

        Assert.Equal(
            actual: journal.Bytes,
            expected: 0L
        );
    }
}

using Puck.Testing;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: the arena bounds and owns every visibility payload it retains, and the undo
/// journal charges that normalized payload through nested scopes.</summary>
public sealed class ArenaVisibilityBudgetLawTests {
    [Fact]
    public void AVisibilityIsBoundedAndCopiedAtTheArenaBoundary() {
        var (catalog, arena) = ArenaFixture.Build();
        var key = ArenaFixture.Key(catalog: catalog, value: "a");
        var readers = new List<string> { "p1" };

        Assert.True(condition: arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(
                Readers: readers,
                ReadersFrom: "tokens"
            )
        ));

        readers[0] = "changed-after-write";

        var stored = arena.Visibility(key: key, rowOrdinal: ArenaFixture.Tokens)!;

        Assert.Equal(
            actual: stored.Readers![0],
            expected: "p1"
        );
        Assert.True(condition: arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: stored
        ));
        Assert.Same(
            actual: arena.Visibility(key: key, rowOrdinal: ArenaFixture.Tokens),
            expected: stored
        );
        Assert.False(condition: arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(Readers: Enumerable.Repeat(
                count: (StateCapacity.MaxVisibilityReaders + 1),
                element: "p1"
            ).ToArray())
        ));
        Assert.False(condition: arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(Readers: [new string(
                    c: 'r',
                    count: (StateCapacity.MaxVisibilityReaderLength + 1)
                )])
        ));
        Assert.False(condition: arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(ReadersFrom: new string(
                c: 's',
                count: (SafeName.MaxLength + 1)
            ))
        ));
    }
    [Fact]
    public void AnImportedVisibilityIsRefusedBeforeTheRowMoves() {
        var section = ArenaFixture.Section();
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var before = arena.ComputeHash();
        var row = section.Rows![ArenaFixture.Tokens];
        var cell = row.Cells![0];

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [row with { Cells = [cell with { Visibility = new StateVisibility(
                        ReadersFrom: new string(c: 's', count: (SafeName.MaxLength + 1))
                    ) }] }],
            time: ArenaTime.Origin
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "readersFrom");
        Assert.Equal(actual: arena.ComputeHash(), expected: before);

        Assert.False(condition: arena.TryLoad(
            reason: out reason,
            rows: [row with { Visibility = new StateVisibility(ReadersFrom: new string(
                    c: 's',
                    count: (SafeName.MaxLength + 1)
                )) }],
            time: ArenaTime.Origin
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "readersFrom");
        Assert.Equal(actual: arena.ComputeHash(), expected: before);
    }
    [Fact]
    public void AuthoredVisibilityIsNormalizedBeforeTheArenaRetainsItsRows() {
        var source = ArenaFixture.Section();
        var cellReaders = new List<string> { "p1" };
        var rowReaders = new List<string> { "p2" };
        var row = source.Rows![ArenaFixture.Tokens];
        var cell = row.Cells![0];
        var authoredCells = new[] { cell with {
            Visibility = new StateVisibility(
                Readers: cellReaders,
                ReadersFrom: "tokens"
            ),
        } };
        var rows = source.Rows.ToArray();

        rows[ArenaFixture.Tokens] = row with {
            Cells = authoredCells,
            Visibility = new StateVisibility(Readers: rowReaders),
        };

        var section = source with { Rows = rows };
        var arena = new StateArena(
            catalog: StateCatalog.Compile(section: section),
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        Assert.True(condition: StateArena.TryMeasureVisibility(
            bytes: out var visibilityBytes,
            reason: out var reason,
            section: section
        ), userMessage: reason);

        cellReaders[0] = "changed-after-construction";
        rowReaders[0] = "changed-after-construction";
        authoredCells[0] = cell with {
            Visibility = new StateVisibility(
                ReadersFrom: new string(c: 's', count: (SafeName.MaxLength + 1))
            ),
        };

        Assert.Equal(
            actual: arena.Rows[ArenaFixture.Tokens].Cells![0].Visibility!.Readers![0],
            expected: "p1"
        );
        Assert.Equal(
            actual: arena.Rows[ArenaFixture.Tokens].Cells![0].Visibility!.ReadersFrom,
            expected: "tokens"
        );
        Assert.Equal(
            actual: arena.Rows[ArenaFixture.Tokens].Visibility!.Readers![0],
            expected: "p2"
        );
        Assert.Equal(
            actual: arena.Bytes,
            expected: ((arena.Layout.Bytes + visibilityBytes) + arena.Keys.Bytes)
        );
    }
    [Fact]
    public void NestedRewindReturnsTheVisibilityPayloadCharge() {
        var journal = new ArenaJournal();
        var outer = journal.BeginScope();
        var readersFrom = new string(c: 's', count: SafeName.MaxLength);
        var visibility = new StateVisibility(
            Readers: [new string(c: 'r', count: StateCapacity.MaxVisibilityReaderLength)],
            ReadersFrom: readersFrom
        );

        var withoutReadersFrom = new ArenaJournal();
        var comparison = withoutReadersFrom.BeginScope();

        withoutReadersFrom.RecordReference(
            column: ArenaColumn.Visibility,
            index: 0,
            previous: visibility with { ReadersFrom = null }
        );

        journal.RecordReference(column: ArenaColumn.Visibility, index: 0, previous: visibility);

        var one = journal.Bytes;

        Assert.Equal(
            actual: (journal.Bytes - withoutReadersFrom.Bytes),
            expected: (32L + (2L * readersFrom.Length))
        );

        _ = withoutReadersFrom.CommitScope(mark: comparison);

        var inner = journal.BeginScope();

        journal.RecordReference(column: ArenaColumn.Visibility, index: 0, previous: visibility);
        Assert.True(condition: (journal.Bytes > (one + ArenaJournal.EntryBytes)));

        _ = journal.RewindScope(mark: inner);

        Assert.Equal(actual: journal.Bytes, expected: one);

        _ = journal.CommitScope(mark: outer);

        Assert.Equal(actual: journal.Bytes, expected: 0L);
    }
    [Fact]
    public void NestedArenaScopesRestoreTheLiveVisibilityCharge() {
        var (catalog, arena) = ArenaFixture.Build();
        var key = ArenaFixture.Key(catalog: catalog, value: "a");
        var outer = arena.BeginScope();

        Assert.True(condition: arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(Readers: ["p1"])
        ));

        var outerBytes = arena.Bytes;
        var inner = arena.BeginScope();

        Assert.True(condition: arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(Readers: [new string(
                    c: 'r',
                    count: StateCapacity.MaxVisibilityReaderLength
                )])
        ));
        Assert.True(condition: (arena.Bytes > outerBytes));

        arena.Rewind(mark: inner);

        Assert.Equal(actual: arena.Bytes, expected: outerBytes);

        arena.Commit(mark: outer);

        Assert.Equal(actual: arena.Bytes, expected: outerBytes);
    }
    [Fact]
    public void AReadersFromOnlyDeclarationDoesNotRetainTheCallersCellArray() {
        var source = ArenaFixture.Section();
        var row = source.Rows![ArenaFixture.Tokens];
        var cell = row.Cells![0];
        var authored = new[] { cell with {
            Visibility = new StateVisibility(ReadersFrom: "tokens"),
        } };
        var rows = source.Rows.ToArray();

        rows[ArenaFixture.Tokens] = row with { Cells = authored };

        var section = source with { Rows = rows };
        var arena = new StateArena(
            catalog: StateCatalog.Compile(section: section),
            options: null,
            section: section,
            time: ArenaTime.Origin
        );

        authored[0] = cell with {
            Visibility = new StateVisibility(
                ReadersFrom: new string(c: 's', count: (SafeName.MaxLength + 1))
            ),
        };

        Assert.Equal(
            actual: arena.Rows[ArenaFixture.Tokens].Cells![0].Visibility!.ReadersFrom,
            expected: "tokens"
        );
    }
    [Fact]
    public void VisibilityPayloadCannotPushAnArenaPastItsByteCeiling() {
        var capacity = StateCapacity.MaxCellsPerRow;
        var cells = Enumerable.Range(count: capacity, start: 0).Select(selector: index => new StateCell(
            Key: ArenaFixture.Name(value: $"k{index}"),
            Value: CellValue.Int(value: index)
        )).ToArray();
        var section = new StateSection(Rows: [new StateRow(
                Name: ArenaFixture.Name(value: "pool"),
                Kind: CellKind.Int,
                Capacity: capacity,
                Cells: cells
            )]);
        var catalog = StateCatalog.Compile(section: section);
        var arena = new StateArena(
            catalog: catalog,
            options: null,
            section: section,
            time: ArenaTime.Origin
        );
        var reader = new string(c: 'r', count: StateCapacity.MaxVisibilityReaderLength);
        var maximum = new StateVisibility(Readers: Enumerable.Repeat(
            count: StateCapacity.MaxVisibilityReaders,
            element: reader
        ).ToArray());
        var refused = false;

        for (var index = 0; (index < capacity); index++) {
            if (!arena.TryWriteVisibility(
                key: ArenaFixture.Key(catalog: catalog, value: $"k{index}"),
                rowOrdinal: 0,
                visibility: maximum
            )) {
                refused = true;
                break;
            }
        }

        Assert.True(condition: refused);
        Assert.True(condition: (arena.Bytes <= ArenaCapacity.MaxBytes));

        var imported = cells.Select(selector: cell => cell with { Visibility = maximum }).ToArray();
        var beforeImport = arena.Bytes;

        Assert.False(condition: arena.TryLoad(
            reason: out var reason,
            rows: [section.Rows![0] with { Cells = imported }],
            time: ArenaTime.Origin
        ));
        Assert.Contains(actualString: reason, expectedSubstring: "byte ceiling");
        Assert.Equal(actual: arena.Bytes, expected: beforeImport);
    }
    [Fact]
    public void RewritingAnAdmittedVisibilityAllocatesNothing() {
        var (catalog, arena) = ArenaFixture.Build();
        var key = ArenaFixture.Key(catalog: catalog, value: "a");

        Assert.True(condition: arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: new StateVisibility(Readers: ["p1"])
        ));

        var admitted = arena.Visibility(key: key, rowOrdinal: ArenaFixture.Tokens);

        _ = arena.TryWriteVisibility(
            key: key,
            rowOrdinal: ArenaFixture.Tokens,
            visibility: admitted
        );

        var before = AllocationWindow.Least(window: () => {
            for (var iteration = 0; (iteration < 128); iteration++) {
                _ = arena.TryWriteVisibility(
                    key: key,
                    rowOrdinal: ArenaFixture.Tokens,
                    visibility: admitted
                );
            }
        });

        Assert.Equal(
            actual: before,
            expected: 0L
        );
    }
}

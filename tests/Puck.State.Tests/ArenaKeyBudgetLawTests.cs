using Xunit;

namespace Puck.State.Tests;

/// <summary>THE LAW: retained key names consume the arena byte budget through every admission door. A refusal
/// leaves the row, ledger, and byte count unchanged; rewinding a speculative scope releases the retained charge.</summary>
public sealed class ArenaKeyBudgetLawTests {
    private static StateSection Rings(int count) => new(Rows: [
        .. Enumerable.Range(
            count: count,
            start: 0
        ).Select(selector: static index => new StateRow(
            Name: ArenaFixture.Name(value: $"budget-ring-{index}"),
            Kind: CellKind.Int,
            Domain: new StateDomain.Ring(
                Capacity: StateCapacity.MaxCellsPerRow,
                Empty: -1L
            )
        )),
        new StateRow(
            Name: ArenaFixture.Name(value: "tokens"),
            Kind: CellKind.Int,
            Capacity: 4
        ),
        new StateRow(
            Name: ArenaFixture.Name(value: "evicting"),
            Kind: CellKind.Int,
            Capacity: 1,
            Evicts: true
        )
    ]);
    private static StateArena NearByteCeiling() {
        StateCatalog? selectedCatalog = null;
        StateSection? selectedSection = null;

        for (var count = 1; (count <= StateCapacity.MaxRows); count++) {
            var section = Rings(count: count);
            var catalog = StateCatalog.Compile(section: section);

            if (!ArenaLayout.TryBuild(
                catalog: catalog,
                layout: out _,
                options: null,
                reason: out _,
                section: section
            )) {
                break;
            }

            selectedCatalog = catalog;
            selectedSection = section;
        }

        Assert.NotNull(@object: selectedCatalog);
        Assert.NotNull(@object: selectedSection);
        return new StateArena(
            catalog: selectedCatalog!,
            options: null,
            section: selectedSection!,
            time: ArenaTime.Origin
        );
    }
    private static void FillRetainedKeyBudget(StateArena arena) {
        for (var index = 0; (arena.Keys.Count < StateCapacity.MaxCellKeys); index++) {
            var name = ArenaFixture.Name(value: $"budget-key-{index}-{new string(c: 'x', count: 220)}");

            if (!arena.Keys.TryIntern(key: out _, name: name, reason: out _)) {
                break;
            }
        }
    }
    private static void FillToAggregateWindow(StateArena arena) {
        // Leave enough room for one fixed-length entry but less than two. This makes the aggregate laws
        // distinguish admission of each name from admission of the complete ledger/import.
        while ((ArenaCapacity.MaxBytes - arena.Bytes) >= 1_212L) {
            var remaining = (ArenaCapacity.MaxBytes - arena.Bytes);
            var length = Math.Clamp(
                max: 230,
                min: 1,
                value: ((int)(((remaining - 900L) - 96L) / 2L))
            );
            var name = ArenaFixture.Name(value: $"aggregate-{arena.Keys.Count}-{new string(c: 'q', count: length)}");

            if (!arena.Keys.TryIntern(key: out _, name: name, reason: out _)) {
                break;
            }
        }
    }

    [Fact]
    public void OversizedMintRefusesBeforeChangingAnEvictingRow() {
        var arena = NearByteCeiling();
        var rowOrdinal = (arena.Catalog.Descriptors.Count - 1);

        Assert.True(condition: arena.TryMint(
            rowOrdinal: rowOrdinal,
            name: ArenaFixture.Name(value: "victim"),
            value: CellValue.Int(value: 1L),
            key: out var victim,
            reason: out var victimReason
        ), userMessage: victimReason);
        FillRetainedKeyBudget(arena: arena);
        var beforeBytes = arena.Bytes;
        var beforeKeys = arena.Keys.Count;
        var beforeNames = arena.Keys.Names.ToArray();
        var count = arena.CellCount(rowOrdinal: rowOrdinal);

        Assert.False(condition: arena.TryMint(
            rowOrdinal: rowOrdinal,
            name: ArenaFixture.Name(value: new string(c: 'm', count: 255)),
            value: CellValue.Int(value: 1L),
            key: out _,
            reason: out var reason
        ));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: "byte budget");
        Assert.Equal(count, arena.CellCount(rowOrdinal: rowOrdinal));
        Assert.True(condition: arena.TryRead(key: victim, rowOrdinal: rowOrdinal, value: out var retained));
        Assert.Equal(CellValue.Int(value: 1L), retained);
        Assert.Equal(beforeKeys, arena.Keys.Count);
        Assert.Equal(beforeBytes, arena.Bytes);
        Assert.Equal(beforeNames, arena.Keys.Names);
    }
    [Fact]
    public void RefusedImportAndRestoreAggregateKeyBytesAtomically() {
        var arena = NearByteCeiling();

        FillToAggregateWindow(arena: arena);
        var beforeCount = arena.Keys.Count;
        var beforeBytes = arena.Bytes;
        var names = new[] {
            ArenaFixture.Name(value: new string(c: 'a', count: 255)),
            ArenaFixture.Name(value: new string(c: 'b', count: 255)),
        };

        Assert.False(condition: arena.TryRestoreKeys(names: names, reason: out var restoreReason));
        Assert.Contains(actualString: restoreReason, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: "budget");
        Assert.Equal(beforeCount, arena.Keys.Count);
        Assert.Equal(beforeBytes, arena.Bytes);

        var refused = new StateRow(
            Name: ArenaFixture.Name(value: "tokens"),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: [
                new StateCell(
                    Key: ArenaFixture.Name(value: new string(c: 'i', count: 255)),
                    Value: CellValue.Int(value: 1L)
                ),
                new StateCell(
                    Key: ArenaFixture.Name(value: new string(c: 'j', count: 255)),
                    Value: CellValue.Int(value: 1L)
                )
            ]
        );

        Assert.False(condition: arena.TryLoad([refused], ArenaTime.Origin, out var importReason));
        Assert.Contains(actualString: importReason, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: "byte ceiling");
        Assert.Equal(beforeCount, arena.Keys.Count);
        Assert.Equal(beforeBytes, arena.Bytes);
    }
    [Fact]
    public void RewindReleasesRetainedKeyBytesAndNames() {
        var (_, arena) = ArenaFixture.Build();
        var beforeBytes = arena.Bytes;
        var beforeCount = arena.Keys.Count;
        var mark = arena.BeginScope();
        var name = ArenaFixture.Name(value: $"rewind-{new string(c: 'r', count: 220)}");

        Assert.True(condition: arena.Keys.TryIntern(key: out _, name: name, reason: out var reason), userMessage: reason);
        Assert.True(condition: (arena.Bytes > beforeBytes));

        arena.Rewind(mark: mark);
        Assert.Equal(beforeBytes, arena.Bytes);
        Assert.Equal(beforeCount, arena.Keys.Count);
        Assert.False(condition: arena.Keys.TryResolve(key: out _, name: name));
    }
}

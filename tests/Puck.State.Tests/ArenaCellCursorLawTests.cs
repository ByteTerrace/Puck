using Puck.Testing;
using Puck.Assets.Documents;
using Xunit;

namespace Puck.State.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="StateArena.TryNextCell"/> is the sole held-cell traversal for every
/// row shape. It scans physical slots in ascending order, skips holes, never mints keys, and leaves positional APIs
/// unavailable for generated pool storage.</summary>
public sealed class ArenaCellCursorLawTests {
    [Fact]
    public void ExportingAndRebuildingAStandaloneRingPreservesItsLedgerAndHash() {
        var section = new StateSection(Rows: [new StateRow(Name: CellName.Parse(candidate: "history"), Kind: CellKind.Int, Domain: new StateDomain.Ring(Capacity: 3))]);
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);

        Assert.True(condition: arena.TryPush(reason: out var reason, rowOrdinal: 0, value: 42L), userMessage: reason);
        var exported = section with { Rows = arena.ToRows() };
        var restored = new StateArena(catalog: StateCatalog.Compile(section: exported), section: exported, time: ArenaTime.Origin);

        Assert.Equal(expected: arena.Keys.Count, actual: restored.Keys.Count);
        Assert.Equal(expected: arena.ComputeHash(), actual: restored.ComputeHash());
        Assert.Equal(expected: new long[] { 42L }, actual: Values(arena: restored, rowOrdinal: 0));
    }
    [Fact]
    public void AStandaloneRingCanBeReadWithoutRetainingNumericKeyNames() {
        var section = new StateSection(Rows: [new StateRow(Name: CellName.Parse(candidate: "history"), Kind: CellKind.Int, Domain: new StateDomain.Ring(Capacity: 3))]);
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);
        var beforeKeys = arena.Keys.Count;

        Assert.True(condition: arena.TryPush(reason: out var reason, rowOrdinal: 0, value: 42L), userMessage: reason);
        var beforeHash = arena.ComputeHash();
        var cursor = 0;

        Assert.True(condition: arena.TryNextCell(rowOrdinal: 0, cursor: ref cursor, key: out var key));
        Assert.Equal(expected: "0", actual: arena.Keys[key].Value);
        Assert.True(condition: arena.TryReadRaw(rowOrdinal: 0, key: key, raw: out var raw));
        Assert.Equal(actual: raw, expected: 42L);
        Assert.Equal(expected: beforeKeys, actual: arena.Keys.Count);
        Assert.Equal(expected: beforeHash, actual: arena.ComputeHash());
    }
    [Fact]
    public void ACursorVisitsExactlyTheHeldCellsOfEveryOrdinaryShapeInPhysicalOrder() {
        var (_, arena) = ArenaFixture.Build();
        var beforeKeys = arena.Keys.Count;

        Assert.True(condition: arena.TryPush(reason: out var reason, rowOrdinal: ArenaFixture.History, value: 10L), userMessage: reason);
        Assert.True(condition: arena.TryPush(reason: out reason, rowOrdinal: ArenaFixture.History, value: 20L), userMessage: reason);
        Assert.True(condition: arena.TryPush(reason: out reason, rowOrdinal: ArenaFixture.History, value: 30L), userMessage: reason);
        Assert.True(condition: arena.TryPush(reason: out reason, rowOrdinal: ArenaFixture.History, value: 40L), userMessage: reason);

        Assert.Equal(expected: new[] { "$value" }, actual: Names(arena: arena, rowOrdinal: ArenaFixture.Score));
        Assert.Equal(expected: new[] { "a", "b" }, actual: Names(arena: arena, rowOrdinal: ArenaFixture.Tokens));
        Assert.Equal(expected: new[] { "a", "b" }, actual: Names(arena: arena, rowOrdinal: ArenaFixture.Deck));
        Assert.Equal(expected: new[] { "0", "2" }, actual: Names(arena: arena, rowOrdinal: ArenaFixture.Board));

        var ring = Values(arena: arena, rowOrdinal: ArenaFixture.History);

        // The ring's cursor is a physical-slot walk: the fourth push overwrote slot zero. It is deliberately not
        // ReadWord's oldest-to-newest chronology.
        Assert.Equal(actual: ring, expected: new long[] { 40L, 20L, 30L });
        Assert.Equal(expected: arena.CellCount(rowOrdinal: ArenaFixture.History), actual: ring.Count);
        Assert.Equal(expected: beforeKeys, actual: arena.Keys.Count);
    }
    [Fact]
    public void ACursorSkipsSparsePoolHolesAcrossAWordBoundaryAndEndsWithTheDefaultKey() {
        var arena = PoolArena();
        var row = arena.Catalog.Pools[0].Fields[0].RowOrdinal;
        var cursor = 0;
        var visited = new List<string>();

        while (arena.TryNextCell(rowOrdinal: row, cursor: ref cursor, key: out var key)) {
            visited.Add(item: arena.Keys[key].Value);
        }

        Assert.Equal(actual: visited, expected: new[] { "64", "129" });
        Assert.Equal(expected: arena.CellCount(rowOrdinal: row), actual: visited.Count);
        Assert.False(condition: arena.TryNextCell(rowOrdinal: row, cursor: ref cursor, key: out var end));
        Assert.Equal(expected: default, actual: end);
        Assert.False(condition: arena.TryNextCell(rowOrdinal: -1, cursor: ref cursor, key: out var invalid));
        Assert.Equal(expected: default, actual: invalid);
    }
    [Fact]
    public void ACursorCountsAnImportedSparseRingByPresenceRatherThanItsHistoryCursor() {
        var (_, arena) = ArenaFixture.Build();

        Assert.True(condition: arena.TryLoad(
            reason: out var reason,
            rows: [new StateRow(
                Name: ArenaFixture.Name(value: "history"),
                Kind: CellKind.Int,
                Cells: [new StateCell(Key: ArenaFixture.Name(value: "1"), Value: CellValue.Int(value: 8L))],
                Domain: new StateDomain.Ring(Capacity: 3, Empty: -1L),
                HistoryCursor: 5L
            )],
            time: ArenaTime.Origin
        ), userMessage: reason);

        Assert.Equal(expected: 1, actual: arena.CellCount(rowOrdinal: ArenaFixture.History));
        Assert.Equal(expected: new[] { "1" }, actual: Names(arena: arena, rowOrdinal: ArenaFixture.History));
        Assert.Equal(expected: new long[] { 8L }, actual: Values(arena: arena, rowOrdinal: ArenaFixture.History));
    }
    [MemberData(nameof(LatticeTopologies))]
    [Theory]
    public void ACursorResolvesAndExportsSparseLatticeCellsByTheirCanonicalKeys(LatticeTopology topology) {
        var section = new StateSection(
            Lattices: [topology],
            Rows: [new StateRow(
                Name: CellName.Parse(candidate: "board"),
                Kind: CellKind.Int,
                Domain: new StateDomain.CellsOf(Empty: -1L, Topology: topology.Name)
            )]
        );
        var arena = new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);

        // No cell was authored: every address must have been seeded from the topology itself.
        for (var position = 0; (position < arena.Layout[0].CellCapacity); position++) {
            Assert.True(condition: arena.Keys.TryResolve(name: arena.Layout[0].Topology!.NameOf(cell: position), key: out _));
        }
        Assert.True(condition: arena.Keys.TryResolve(name: CellName.Parse(candidate: "1"), key: out var one));
        Assert.True(condition: arena.Keys.TryResolve(name: CellName.Parse(candidate: "5"), key: out var five));
        Assert.True(condition: arena.TryWrite(rowOrdinal: 0, key: one, value: CellValue.Int(value: 11L), reason: out var reason), userMessage: reason);
        Assert.True(condition: arena.TryWrite(rowOrdinal: 0, key: five, value: CellValue.Int(value: 55L), reason: out reason), userMessage: reason);

        Assert.Equal(expected: new[] { "1", "5" }, actual: Names(arena: arena, rowOrdinal: 0));
        Assert.Equal(expected: new long[] { 11L, 55L }, actual: Values(arena: arena, rowOrdinal: 0));
        Assert.True(condition: arena.TryCellSlot(key: five, rowOrdinal: 0, slot: out var slot));
        Assert.Equal(expected: (arena.Layout[0].CellStart + 5), actual: slot);

        var exported = section with { Rows = arena.ToRows() };
        var restored = new StateArena(catalog: StateCatalog.Compile(section: exported), section: exported, time: ArenaTime.Origin);

        Assert.Equal(expected: arena.ComputeHash(), actual: restored.ComputeHash());
        Assert.Equal(expected: new[] { "1", "5" }, actual: Names(arena: restored, rowOrdinal: 0));
        Assert.Equal(expected: new long[] { 11L, 55L }, actual: Values(arena: restored, rowOrdinal: 0));
    }
    [Fact]
    public void APoolOwnedRowRefusesPositionalAccessEvenForItsGenerationAndMembershipRows() {
        var arena = PoolArena();
        var pool = Assert.Single(collection: arena.Catalog.Pools);
        var rows = pool.Fields.Select(selector: static field => field.RowOrdinal)
            .Append(element: pool.DomainRowOrdinal)
            .Append(element: pool.GenerationRowOrdinal);

        foreach (var row in rows) {
            Assert.Throws<InvalidOperationException>(testCode: () => arena.TryKeyAt(key: out _, position: 0, rowOrdinal: row));
            Assert.Throws<InvalidOperationException>(testCode: () => arena.TryReadAt(position: 0, rowOrdinal: row, value: out _));
            Assert.Throws<InvalidOperationException>(testCode: () => arena.TryReadRawAt(position: 0, raw: out _, rowOrdinal: row));
        }
    }
    [Fact]
    public void AWarmedCursorMintsNoKeysChangesNoHashAndAllocatesNothing() {
        var arena = PoolArena();
        var row = arena.Catalog.Pools[0].Fields[0].RowOrdinal;

        for (var pass = 0; (pass < 64); pass++) {
            Drain(arena: arena, rowOrdinal: row);
        }

        var beforeHash = arena.ComputeHash();
        var beforeKeys = arena.Keys.Count;
        var count = 0;

        var before = AllocationWindow.Least(window: () => {
            count = 0;
            for (var pass = 0; (pass < 512); pass++) {
                count += Drain(arena: arena, rowOrdinal: row);
            }
        });

        Assert.Equal(expected: 0L, actual: before);
        Assert.Equal(actual: count, expected: 1024);
        Assert.Equal(expected: beforeKeys, actual: arena.Keys.Count);
        Assert.Equal(expected: beforeHash, actual: arena.ComputeHash());
    }

    private static int Drain(StateArena arena, int rowOrdinal) {
        var cursor = 0;
        var count = 0;

        while (arena.TryNextCell(rowOrdinal: rowOrdinal, cursor: ref cursor, key: out _)) {
            count++;
        }

        return count;
    }
    private static List<string> Names(StateArena arena, int rowOrdinal) {
        var cursor = 0;
        var names = new List<string>();

        while (arena.TryNextCell(rowOrdinal: rowOrdinal, cursor: ref cursor, key: out var key)) {
            names.Add(item: arena.Keys[key].Value);
        }

        Assert.Equal(expected: arena.CellCount(rowOrdinal: rowOrdinal), actual: names.Count);

        return names;
    }
    private static List<long> Values(StateArena arena, int rowOrdinal) {
        var cursor = 0;
        var values = new List<long>();

        while (arena.TryNextCell(rowOrdinal: rowOrdinal, cursor: ref cursor, key: out var key)) {
            Assert.True(condition: arena.TryReadRaw(rowOrdinal: rowOrdinal, key: key, raw: out var raw));
            values.Add(item: raw);
        }

        return values;
    }
    private static StateArena PoolArena() {
        var record = new StateRecord(Name: CellName.Parse(candidate: "item"), Fields: [
            new StatePoolField(Name: CellName.Parse(candidate: "value"), Default: CellValue.Int(value: 7L)),
        ]);
        var section = new StateSection(
            Records: [record],
            Pools: [new StatePool(
                Name: CellName.Parse(candidate: "items"),
                Record: record.Name,
                Capacity: 130,
                Initial: [new StatePoolSeed(Slot: 64), new StatePoolSeed(Slot: 129)]
            )]
        );

        return new StateArena(catalog: StateCatalog.Compile(section: section), section: section, time: ArenaTime.Origin);
    }

    public static TheoryData<LatticeTopology> LatticeTopologies => new() {
        new LatticeTopology.Grid(
            Name: "grid",
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: 4,
            Depth: 4
        ),
        new LatticeTopology.Hex(
            Name: "hex",
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Radius: 2
        ),
        new LatticeTopology.Tiling(
            Name: "tiles",
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Family: TilingFamily.Kagome,
            Radius: 2
        ),
    };
}

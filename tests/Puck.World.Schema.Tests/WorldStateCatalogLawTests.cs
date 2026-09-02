using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Proves the typed state catalog's stable ordinal, ownership, storage-shape, value-kind, and handle
/// resolution contracts.</summary>
public sealed class WorldStateCatalogLawTests {
    [InlineData(CellKind.Int, WorldStateValueKind.Int)]
    [InlineData(CellKind.Fixed, WorldStateValueKind.Fixed)]
    [InlineData(CellKind.Bool, WorldStateValueKind.Bool)]
    [InlineData(CellKind.Text, WorldStateValueKind.Text)]
    [Theory]
    public void WorldRowValueKinds_PreserveTheCellKindDiscriminant(CellKind cellKind, WorldStateValueKind valueKind) {
        Assert.Equal(actual: ((byte)valueKind), expected: ((byte)cellKind));
    }
    [Fact]
    public void Compile_AssignsStableGlobalAndLaneOrdinals_InLaneThenDocumentOrder() {
        var section = BuildSection();

        var catalog = WorldStateCatalog.Compile(section: section);

        Assert.Equal(expected: 6, actual: catalog.Count);
        Assert.Collection(
            catalog.Descriptors,
            descriptor => AssertDescriptor(descriptor, ordinal: 0, laneOrdinal: 0, name: "score", ownership: WorldStateOwnershipLane.World, storage: WorldStateStorageShape.Slot, valueKind: WorldStateValueKind.Int),
            descriptor => AssertDescriptor(descriptor, ordinal: 1, laneOrdinal: 1, name: "labels", ownership: WorldStateOwnershipLane.World, storage: WorldStateStorageShape.Keyed, valueKind: WorldStateValueKind.Text),
            descriptor => AssertDescriptor(descriptor, ordinal: 2, laneOrdinal: 2, name: "heat", ownership: WorldStateOwnershipLane.World, storage: WorldStateStorageShape.Lattice, valueKind: WorldStateValueKind.Fixed),
            descriptor => AssertDescriptor(descriptor, ordinal: 3, laneOrdinal: 3, name: "open", ownership: WorldStateOwnershipLane.World, storage: WorldStateStorageShape.Slot, valueKind: WorldStateValueKind.Bool),
            descriptor => AssertDescriptor(descriptor, ordinal: 4, laneOrdinal: 0, name: "jumpUses", ownership: WorldStateOwnershipLane.Body, storage: WorldStateStorageShape.Slot, valueKind: WorldStateValueKind.Counter),
            descriptor => AssertDescriptor(descriptor, ordinal: 5, laneOrdinal: 0, name: "cooldown", ownership: WorldStateOwnershipLane.Identity, storage: WorldStateStorageShape.Slot, valueKind: WorldStateValueKind.Timer)
        );
    }
    [Fact]
    public void TryResolve_UsesOwnershipAndName_ThenDescriptorAccessNeedsNoName() {
        var catalog = WorldStateCatalog.Compile(section: BuildSection());

        Assert.True(condition: catalog.TryResolve(lane: WorldStateOwnershipLane.World, name: WorldCellName.Parse(candidate: "score"), handle: out var score));
        Assert.True(condition: catalog.TryResolve(handle: out var jumpUses, lane: WorldStateOwnershipLane.Body, name: "jumpUses"));
        Assert.Equal(expected: "score", actual: catalog[score].Name);
        Assert.Equal(expected: 0, actual: catalog[score].LaneOrdinal);
        Assert.Equal(expected: "jumpUses", actual: catalog[jumpUses].Name);
        Assert.Equal(expected: WorldStateValueKind.Counter, actual: catalog[jumpUses].ValueKind);

        Assert.False(condition: catalog.TryResolve(handle: out var wrongLane, lane: WorldStateOwnershipLane.Identity, name: "jumpUses"));
        Assert.False(condition: wrongLane.IsValid);
        Assert.False(condition: catalog.TryResolve(handle: out var missing, lane: WorldStateOwnershipLane.World, name: "missing"));
        Assert.False(condition: catalog.TryGetDescriptor(descriptor: out _, handle: missing));
    }
    [Fact]
    public void AHandleFromAnotherCatalogIsRejectedEvenWhenItsOrdinalFits() {
        var first = WorldStateCatalog.Compile(section: BuildSection());
        var second = WorldStateCatalog.Compile(section: BuildSection());

        Assert.True(condition: first.TryResolve(handle: out var firstScore, lane: WorldStateOwnershipLane.World, name: "score"));
        Assert.True(condition: second.TryResolve(handle: out var secondScore, lane: WorldStateOwnershipLane.World, name: "score"));
        Assert.Equal(expected: firstScore.Ordinal, actual: secondScore.Ordinal);
        Assert.False(condition: second.TryGetDescriptor(descriptor: out _, handle: firstScore));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => second[firstScore]);
    }
    [Fact]
    public void TypedReaderUsesTheLaneOrdinalAndRefusesAStaleCatalogOrHandle() {
        var section = BuildSection();
        var rows = section.World!.ToArray();

        rows[0] = rows[0] with { Cells = [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 7L)] };
        var definition = new WorldDefinition(StateRaw: section with { World = rows });
        var catalog = definition.StateCatalog;

        Assert.True(condition: catalog.TryResolve(handle: out var score, lane: WorldStateOwnershipLane.World, name: "score"));
        Assert.True(condition: WorldStateReader.TryReadHandle(
            catalog: catalog,
            definition: definition,
            handle: score,
            key: null,
            rawValue: out var raw,
            row: out var row,
            text: out _,
            tick: 0UL
        ));
        Assert.Equal(expected: "score", actual: row.Name);
        Assert.Equal(actual: raw, expected: 7L);

        var foreign = WorldStateCatalog.Compile(section: definition.StateRaw);

        Assert.Throws<ArgumentException>(testCode: () => WorldStateReader.TryReadHandle(
            catalog: foreign,
            definition: definition,
            handle: score,
            key: null,
            rawValue: out _,
            row: out _,
            text: out _,
            tick: 0UL
        ));
    }
    [Fact]
    public void WorldDefinition_StateCatalog_RecompilesWhenAWithExpressionReplacesState() {
        var original = new WorldDefinition(StateRaw: BuildSection());
        var originalCatalog = original.StateCatalog;

        Assert.True(condition: originalCatalog.TryResolve(handle: out var score, lane: WorldStateOwnershipLane.World, name: "score"));

        var replaced = original with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "round"), Kind: CellKind.Int)]),
        };

        Assert.False(condition: replaced.StateCatalog.TryResolve(handle: out _, lane: WorldStateOwnershipLane.World, name: "score"));
        Assert.True(condition: replaced.StateCatalog.TryResolve(handle: out var round, lane: WorldStateOwnershipLane.World, name: "round"));
        Assert.Equal(expected: 0, actual: round.Ordinal);
        Assert.NotSame(expected: originalCatalog, actual: replaced.StateCatalog);
        Assert.False(condition: replaced.StateCatalog.TryGetDescriptor(descriptor: out _, handle: score));
    }
    [Fact]
    public void WorldDefinition_ValueOnlyUpdatePreservesCatalogAndResolvedHandles() {
        var section = BuildSection();
        var original = new WorldDefinition(StateRaw: section);
        var catalog = original.StateCatalog;

        Assert.True(condition: catalog.TryResolve(handle: out var score, lane: WorldStateOwnershipLane.World, name: "score"));

        var rows = section.World!.Select(selector: row => (
            string.Equals(a: row.Name, b: "score", comparisonType: StringComparison.Ordinal)
                ? row with { Cells = [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 7L)] }
                : row
        )).ToArray();
        var updated = original.WithWorldState(rows: rows);

        Assert.Same(expected: catalog, actual: updated.StateCatalog);
        Assert.Equal(expected: "score", actual: updated.StateCatalog[score].Name);
    }
    [Fact]
    public void WorldDefinition_InPlaceCallerArrayMutationCannotLeaveAStaleCatalog() {
        WorldStateRow[] rows = [new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "score"),
            Kind: CellKind.Int
        )];
        var definition = new WorldDefinition(StateRaw: new WorldStateSection(World: rows));
        var original = definition.StateCatalog;

        Assert.True(condition: original.TryResolve(handle: out var score, lane: WorldStateOwnershipLane.World, name: "score"));

        rows[0] = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "round"),
            Kind: CellKind.Int
        );
        var refreshed = definition.StateCatalog;

        Assert.NotSame(actual: refreshed, expected: original);
        Assert.False(condition: refreshed.TryGetDescriptor(descriptor: out _, handle: score));
        Assert.True(condition: refreshed.TryResolve(handle: out _, lane: WorldStateOwnershipLane.World, name: "round"));
    }
    [Fact]
    public void Compile_NullSection_ProducesAnEmptyCatalog() {
        var catalog = WorldStateCatalog.Compile(section: null);

        Assert.Empty(collection: catalog.Descriptors);
        Assert.Equal(expected: 0, actual: catalog.Count);
        Assert.False(condition: catalog.TryResolve(handle: out var handle, lane: WorldStateOwnershipLane.World, name: "anything"));
        Assert.False(condition: handle.IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => catalog[handle]);
    }
    [Fact]
    public void Compile_RefusesANameSharedByBodyAndIdentityLanes() {
        var section = new WorldStateSection(
            Body: [new ActionStateSlot(Name: "shared", Kind: ActionStateKind.Counter)],
            Identity: [new ActionStateSlot(Name: "shared", Kind: ActionStateKind.Timer)]
        );

        var exception = Assert.Throws<InvalidOperationException>(testCode: () => WorldStateCatalog.Compile(section: section));

        Assert.Contains(expectedSubstring: "duplicate name 'shared'", actualString: exception.Message, comparisonType: StringComparison.Ordinal);
    }

    private static WorldStateSection BuildSection() => new(
        World: [
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "score"), Kind: CellKind.Int),
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: "labels"),
                Kind: CellKind.Text,
                Capacity: 4,
                Cells: [new WorldStateCell(Key: WorldCellName.Parse(candidate: "primary"), Text: "ready")]
            ),
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: "heat"),
                Kind: CellKind.Fixed,
                Lattice: new WorldStateLatticeTrait(Topology: "ground")
            ),
            new WorldStateRow(Name: WorldCellName.Parse(candidate: "open"), Kind: CellKind.Bool),
        ],
        Body: [new ActionStateSlot(Name: "jumpUses", Kind: ActionStateKind.Counter)],
        Identity: [new ActionStateSlot(Name: "cooldown", Kind: ActionStateKind.Timer)]
    );
    private static void AssertDescriptor(WorldStateDescriptor descriptor, int ordinal, int laneOrdinal, string name, WorldStateOwnershipLane ownership, WorldStateStorageShape storage, WorldStateValueKind valueKind) {
        Assert.Equal(expected: ordinal, actual: descriptor.Handle.Ordinal);
        Assert.Equal(expected: laneOrdinal, actual: descriptor.LaneOrdinal);
        Assert.Equal(expected: name, actual: descriptor.Name);
        Assert.Equal(expected: ownership, actual: descriptor.Ownership);
        Assert.Equal(expected: storage, actual: descriptor.Storage);
        Assert.Equal(expected: valueKind, actual: descriptor.ValueKind);
    }
}

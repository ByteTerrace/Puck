using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Proves the typed state catalog's stable ordinal, ownership, storage-shape, value-kind, and handle
/// resolution contracts.</summary>
public sealed class WorldStateCatalogLawTests {
    [Theory]
    [InlineData(CellKind.Int, WorldStateValueKind.Int)]
    [InlineData(CellKind.Fixed, WorldStateValueKind.Fixed)]
    [InlineData(CellKind.Bool, WorldStateValueKind.Bool)]
    [InlineData(CellKind.Text, WorldStateValueKind.Text)]
    public void WorldRowValueKinds_PreserveTheCellKindDiscriminant(CellKind cellKind, WorldStateValueKind valueKind) {
        Assert.Equal(expected: (byte)cellKind, actual: (byte)valueKind);
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
        Assert.True(condition: catalog.TryResolve(lane: WorldStateOwnershipLane.Body, name: "jumpUses", handle: out var jumpUses));
        Assert.Equal(expected: "score", actual: catalog[score].Name);
        Assert.Equal(expected: 0, actual: catalog[score].LaneOrdinal);
        Assert.Equal(expected: "jumpUses", actual: catalog[jumpUses].Name);
        Assert.Equal(expected: WorldStateValueKind.Counter, actual: catalog[jumpUses].ValueKind);

        Assert.False(condition: catalog.TryResolve(lane: WorldStateOwnershipLane.Identity, name: "jumpUses", handle: out var wrongLane));
        Assert.False(condition: wrongLane.IsValid);
        Assert.False(condition: catalog.TryResolve(lane: WorldStateOwnershipLane.World, name: "missing", handle: out var missing));
        Assert.False(condition: catalog.TryGetDescriptor(handle: missing, descriptor: out _));
    }

    [Fact]
    public void AHandleFromAnotherCatalogIsRejectedEvenWhenItsOrdinalFits() {
        var first = WorldStateCatalog.Compile(section: BuildSection());
        var second = WorldStateCatalog.Compile(section: BuildSection());

        Assert.True(condition: first.TryResolve(lane: WorldStateOwnershipLane.World, name: "score", handle: out var firstScore));
        Assert.True(condition: second.TryResolve(lane: WorldStateOwnershipLane.World, name: "score", handle: out var secondScore));
        Assert.Equal(expected: firstScore.Ordinal, actual: secondScore.Ordinal);
        Assert.False(condition: second.TryGetDescriptor(handle: firstScore, descriptor: out _));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => second[firstScore]);
    }

    [Fact]
    public void TypedReaderUsesTheLaneOrdinalAndRefusesAStaleCatalogOrHandle() {
        var section = BuildSection();
        var rows = section.World!.ToArray();
        rows[0] = rows[0] with { Cells = [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 7L)] };
        var definition = new WorldDefinition(StateRaw: section with { World = rows });
        var catalog = definition.StateCatalog;

        Assert.True(condition: catalog.TryResolve(lane: WorldStateOwnershipLane.World, name: "score", handle: out var score));
        Assert.True(condition: WorldStateReader.TryReadHandle(
            definition: definition,
            catalog: catalog,
            handle: score,
            key: null,
            tick: 0UL,
            row: out var row,
            rawValue: out var raw,
            text: out _
        ));
        Assert.Equal(expected: "score", actual: row.Name);
        Assert.Equal(expected: 7L, actual: raw);

        var foreign = WorldStateCatalog.Compile(section: definition.StateRaw);

        Assert.Throws<ArgumentException>(testCode: () => WorldStateReader.TryReadHandle(
            definition: definition,
            catalog: foreign,
            handle: score,
            key: null,
            tick: 0UL,
            row: out _,
            rawValue: out _,
            text: out _
        ));
    }

    [Fact]
    public void WorldDefinition_StateCatalog_RecompilesWhenAWithExpressionReplacesState() {
        var original = new WorldDefinition(StateRaw: BuildSection());
        var originalCatalog = original.StateCatalog;

        Assert.True(condition: originalCatalog.TryResolve(lane: WorldStateOwnershipLane.World, name: "score", handle: out var score));

        var replaced = original with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "round"), Kind: CellKind.Int)]),
        };

        Assert.False(condition: replaced.StateCatalog.TryResolve(lane: WorldStateOwnershipLane.World, name: "score", handle: out _));
        Assert.True(condition: replaced.StateCatalog.TryResolve(lane: WorldStateOwnershipLane.World, name: "round", handle: out var round));
        Assert.Equal(expected: 0, actual: round.Ordinal);
        Assert.NotSame(expected: originalCatalog, actual: replaced.StateCatalog);
        Assert.False(condition: replaced.StateCatalog.TryGetDescriptor(handle: score, descriptor: out _));
    }

    [Fact]
    public void WorldDefinition_ValueOnlyUpdatePreservesCatalogAndResolvedHandles() {
        var section = BuildSection();
        var original = new WorldDefinition(StateRaw: section);
        var catalog = original.StateCatalog;

        Assert.True(condition: catalog.TryResolve(lane: WorldStateOwnershipLane.World, name: "score", handle: out var score));

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

        Assert.True(condition: original.TryResolve(lane: WorldStateOwnershipLane.World, name: "score", handle: out var score));

        rows[0] = new WorldStateRow(
            Name: WorldCellName.Parse(candidate: "round"),
            Kind: CellKind.Int
        );
        var refreshed = definition.StateCatalog;

        Assert.NotSame(expected: original, actual: refreshed);
        Assert.False(condition: refreshed.TryGetDescriptor(handle: score, descriptor: out _));
        Assert.True(condition: refreshed.TryResolve(lane: WorldStateOwnershipLane.World, name: "round", handle: out _));
    }

    [Fact]
    public void Compile_NullSection_ProducesAnEmptyCatalog() {
        var catalog = WorldStateCatalog.Compile(section: null);

        Assert.Empty(collection: catalog.Descriptors);
        Assert.Equal(expected: 0, actual: catalog.Count);
        Assert.False(condition: catalog.TryResolve(lane: WorldStateOwnershipLane.World, name: "anything", handle: out var handle));
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

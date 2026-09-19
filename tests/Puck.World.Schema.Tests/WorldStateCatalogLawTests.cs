using Puck.Physics.Motion;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>
/// The serialized home for every law that reads <see cref="StateCatalog.ShapeWalkCount"/>. The count belongs to
/// the process, not to a test, so any class composing a document beside one of these laws moves the number it is
/// asserting on. Runs one class at a time, apart from every parallel collection.
/// </summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class ShapeWalkCollection {
    /// <summary>The collection name test classes reference via <c>[Collection(ShapeWalkCollection.Name)]</c>.</summary>
    public const string Name = "state-catalog-shape-walk";
}
/// <summary>Proves the typed state catalog's stable ordinal, lane, row-shape, cell-kind, role, and handle
/// resolution contracts.</summary>
[Collection(name: ShapeWalkCollection.Name)]
public sealed class WorldStateCatalogLawTests {
    [Fact]
    public void Compile_AssignsStableGlobalAndLaneOrdinals_InLaneThenDocumentOrder() {
        var section = BuildSection();

        var catalog = StateCatalog.Compile(section: section);

        Assert.Equal(
            expected: 6,
            actual: catalog.Count
        );
        Assert.Collection(
            catalog.Descriptors,
            descriptor => AssertDescriptor(
                descriptor,
                ordinal: 0,
                laneOrdinal: 0,
                name: "score",
                lane: StateLane.Document,
                shape: RowShape.Slot,
                kind: CellKind.Int,
                role: StateParticipantRole.None
            ),
            descriptor => AssertDescriptor(
                descriptor,
                ordinal: 1,
                laneOrdinal: 1,
                name: "labels",
                lane: StateLane.Document,
                shape: RowShape.Keyed,
                kind: CellKind.Text,
                role: StateParticipantRole.None
            ),
            descriptor => AssertDescriptor(
                descriptor,
                ordinal: 2,
                laneOrdinal: 2,
                name: "heat",
                lane: StateLane.Document,
                shape: RowShape.Lattice,
                kind: CellKind.Fixed,
                role: StateParticipantRole.None,
                hostOwned: true
            ),
            descriptor => AssertDescriptor(
                descriptor,
                ordinal: 3,
                laneOrdinal: 3,
                name: "open",
                lane: StateLane.Document,
                shape: RowShape.Slot,
                kind: CellKind.Bool,
                role: StateParticipantRole.None
            ),
            descriptor => AssertDescriptor(
                descriptor,
                ordinal: 4,
                laneOrdinal: 0,
                name: "jumpUses",
                lane: StateLane.Participant,
                shape: RowShape.Slot,
                kind: CellKind.Fixed,
                role: StateParticipantRole.Counter
            ),
            descriptor => AssertDescriptor(
                descriptor,
                ordinal: 5,
                laneOrdinal: 0,
                name: "cooldown",
                lane: StateLane.Identity,
                shape: RowShape.Slot,
                kind: CellKind.Int,
                role: StateParticipantRole.Timer
            )
        );
    }
    [Fact]
    public void TryResolve_UsesOwnershipAndName_ThenDescriptorAccessNeedsNoName() {
        var catalog = StateCatalog.Compile(section: BuildSection());

        Assert.True(condition: catalog.TryResolve(
            lane: StateLane.Document,
            name: CellName.Parse(candidate: "score"),
            handle: out var score
        ));
        Assert.True(condition: catalog.TryResolve(
            handle: out var jumpUses,
            lane: StateLane.Participant,
            name: "jumpUses"
        ));
        Assert.Equal(
            expected: "score",
            actual: catalog[score].Name
        );
        Assert.Equal(
            expected: 0,
            actual: catalog[score].LaneOrdinal
        );
        Assert.Equal(
            expected: "jumpUses",
            actual: catalog[jumpUses].Name
        );
        Assert.Equal(
            expected: StateParticipantRole.Counter,
            actual: catalog[jumpUses].Role
        );
        Assert.Equal(
            expected: CellKind.Fixed,
            actual: catalog[jumpUses].Kind
        );

        Assert.False(condition: catalog.TryResolve(
            handle: out var wrongLane,
            lane: StateLane.Identity,
            name: "jumpUses"
        ));
        Assert.False(condition: wrongLane.IsValid);
        Assert.False(condition: catalog.TryResolve(
            handle: out var missing,
            lane: StateLane.Document,
            name: "missing"
        ));
        Assert.False(condition: catalog.TryGetDescriptor(
            descriptor: out _,
            handle: missing
        ));
    }
    [Fact]
    public void AHandleFromAnotherCatalogIsRejectedEvenWhenItsOrdinalFits() {
        var first = StateCatalog.Compile(section: BuildSection());
        var second = StateCatalog.Compile(section: BuildSection());

        Assert.True(condition: first.TryResolve(
            handle: out var firstScore,
            lane: StateLane.Document,
            name: "score"
        ));
        Assert.True(condition: second.TryResolve(
            handle: out var secondScore,
            lane: StateLane.Document,
            name: "score"
        ));
        Assert.Equal(
            expected: firstScore.Ordinal,
            actual: secondScore.Ordinal
        );
        Assert.False(condition: second.TryGetDescriptor(
            descriptor: out _,
            handle: firstScore
        ));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => second[firstScore]);
    }
    [Fact]
    public void TypedReaderUsesTheLaneOrdinalAndRefusesAStaleCatalogOrHandle() {
        var section = BuildSection();
        var rows = section.World!.ToArray();

        rows[0] = rows[0] with {
            Cells = [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: 7L)
            )],
        };
        var definition = new WorldDefinition(StateRaw: section with { World = rows });
        var catalog = definition.StateCatalog;

        Assert.True(condition: catalog.TryResolve(
            handle: out var score,
            lane: StateLane.Document,
            name: "score"
        ));
        Assert.True(condition: WorldStateReader.TryReadHandle(
            catalog: catalog,
            definition: definition,
            engineTick: 0UL,
            handle: score,
            key: null,
            rawValue: out var raw,
            row: out var row,
            text: out _,
            tick: 0UL
        ));
        Assert.Equal(
            expected: "score",
            actual: row.Name
        );
        Assert.Equal(
            actual: raw,
            expected: 7L
        );

        var foreign = StateCatalog.Compile(section: definition.StateRaw);

        Assert.Throws<ArgumentException>(testCode: () => WorldStateReader.TryReadHandle(
            catalog: foreign,
            definition: definition,
            engineTick: 0UL,
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

        Assert.True(condition: originalCatalog.TryResolve(
            handle: out var score,
            lane: StateLane.Document,
            name: "score"
        ));

        var replaced = original with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: CellName.Parse(candidate: "round"),
                Kind: CellKind.Int
            )]),
        };

        Assert.False(condition: replaced.StateCatalog.TryResolve(
            handle: out _,
            lane: StateLane.Document,
            name: "score"
        ));
        Assert.True(condition: replaced.StateCatalog.TryResolve(
            handle: out var round,
            lane: StateLane.Document,
            name: "round"
        ));
        Assert.Equal(
            expected: 0,
            actual: round.Ordinal
        );
        Assert.NotSame(
            expected: originalCatalog,
            actual: replaced.StateCatalog
        );
        Assert.False(condition: replaced.StateCatalog.TryGetDescriptor(
            descriptor: out _,
            handle: score
        ));
    }
    [Fact]
    public void WorldDefinition_ValueOnlyUpdatePreservesCatalogAndResolvedHandles() {
        var section = BuildSection();
        var original = new WorldDefinition(StateRaw: section);
        var catalog = original.StateCatalog;

        Assert.True(condition: catalog.TryResolve(
            handle: out var score,
            lane: StateLane.Document,
            name: "score"
        ));

        var rows = section.World!.Select(selector: row => (
            string.Equals(
            a: row.Name,
            b: "score",
            comparisonType: StringComparison.Ordinal
        )
            ? row with {
                Cells = [new StateCell(
                    Key: WorldStateRow.SlotKey,
                    Value: CellValue.Int(value: 7L)
                )],
            }
            : row)).ToArray();
        var updated = original.WithWorldState(rows: rows);

        Assert.Same(
            expected: catalog,
            actual: updated.StateCatalog
        );
        Assert.Equal(
            expected: "score",
            actual: updated.StateCatalog[score].Name
        );
    }
    [Fact]
    public void WorldDefinition_InPlaceCallerArrayMutationCannotReachTheSectionOrItsCatalog() {
        WorldStateRow[] rows = [new WorldStateRow(
                Name: CellName.Parse(candidate: "score"),
                Kind: CellKind.Int
            )];
        var section = new WorldStateSection(World: rows);
        var definition = new WorldDefinition(StateRaw: section);
        var original = definition.StateCatalog;

        Assert.True(condition: original.TryResolve(
            handle: out var score,
            lane: StateLane.Document,
            name: "score"
        ));

        rows[0] = new WorldStateRow(
            Name: CellName.Parse(candidate: "round"),
            Kind: CellKind.Int
        );

        Assert.Equal(
            expected: "score",
            actual: section.World![0].Name.Value
        );

        var refreshed = definition.StateCatalog;

        Assert.Same(
            actual: refreshed,
            expected: original
        );
        Assert.True(condition: refreshed.TryGetDescriptor(
            descriptor: out var descriptor,
            handle: score
        ));
        Assert.Equal(
            expected: "score",
            actual: descriptor.Name
        );
        Assert.False(condition: refreshed.TryResolve(
            handle: out _,
            lane: StateLane.Document,
            name: "round"
        ));
    }
    [Fact]
    public void GetStateCatalog_NeverReWalksAnAlreadyKeyedSectionsShape() {
        var definition = new WorldDefinition(StateRaw: BuildSection());
        var warm = definition.StateCatalog;
        var before = StateCatalog.ShapeWalkCount;

        for (var read = 0; (read < 64); read++) {
            Assert.Same(
                expected: warm,
                actual: definition.StateCatalog
            );
        }

        Assert.Equal(
            expected: before,
            actual: StateCatalog.ShapeWalkCount
        );
    }
    [Fact]
    public void MatchesShape_WithVectorRow_ReturnsTrue() {
        var section = new WorldStateSection(
            World: [
                new WorldStateRow(
                    Name: CellName.Parse(candidate: "embeddings"),
                    Kind: CellKind.Vector,
                    Space: "lore"
                )
            ],
            Spaces: [
                new StateSpace(
                    Name: CellName.Parse(candidate: "lore"),
                    Model: "puck-fixture",
                    Revision: "1",
                    Dimensions: 16
                )
            ]
        );
        var catalog = StateCatalog.Compile(section: section);

        Assert.True(condition: catalog.MatchesShape(section: section));
    }
    [Fact]
    public void Compile_NullSection_ProducesAnEmptyCatalog() {
        var catalog = StateCatalog.Compile(section: null);

        Assert.Empty(collection: catalog.Descriptors);
        Assert.Equal(
            expected: 0,
            actual: catalog.Count
        );
        Assert.False(condition: catalog.TryResolve(
            handle: out var handle,
            lane: StateLane.Document,
            name: "anything"
        ));
        Assert.False(condition: handle.IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => catalog[handle]);
    }
    [Fact]
    public void Compile_RefusesANameSharedByBodyAndIdentityLanes() {
        var section = new WorldStateSection(
            Body: [new ActionStateSlot(
                    Name: "shared",
                    Kind: ActionStateKind.Counter
                )],
            Identity: [new ActionStateSlot(
                    Name: "shared",
                    Kind: ActionStateKind.Timer
                )]
        );

        var exception = Assert.Throws<InvalidOperationException>(testCode: () => StateCatalog.Compile(section: section));

        Assert.Contains(
            expectedSubstring: "duplicate name 'shared'",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }

    private static WorldStateSection BuildSection() => new(
        World: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: "score"),
                Kind: CellKind.Int
            ),
            new WorldStateRow(
                Name: CellName.Parse(candidate: "labels"),
                Kind: CellKind.Text,
                Capacity: 4,
                Cells: [new StateCell(
                        Key: CellName.Parse(candidate: "primary"),
                        Value: CellValue.Text(value: "ready")
                    )]
            ),
            new WorldStateRow(
                Name: CellName.Parse(candidate: "heat"),
                Kind: CellKind.Fixed,
                Domain: new StateDomain.CellsOf(Topology: "ground"),
                Field: new WorldStateFieldTrait()
            ),
            new WorldStateRow(
                Name: CellName.Parse(candidate: "open"),
                Kind: CellKind.Bool
            ),
        ],
        Body: [new ActionStateSlot(
                Name: "jumpUses",
                Kind: ActionStateKind.Counter
            )],
        Identity: [new ActionStateSlot(
                Name: "cooldown",
                Kind: ActionStateKind.Timer
            )],
        Lattices: [new WorldFieldTopology(
                Name: "ground",
                Origin: new Puck.Assets.Documents.DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                CellSize: 1f,
                Width: 2,
                Depth: 2
            )]
    );
    private static void AssertDescriptor(StateDescriptor descriptor, int ordinal, int laneOrdinal, string name, StateLane lane, RowShape shape, CellKind kind, StateParticipantRole role, bool hostOwned = false) {
        Assert.Equal(
            expected: ordinal,
            actual: descriptor.Handle.Ordinal
        );
        Assert.Equal(
            expected: ordinal,
            actual: descriptor.Ordinal
        );
        Assert.Equal(
            expected: laneOrdinal,
            actual: descriptor.LaneOrdinal
        );
        Assert.Equal(
            expected: name,
            actual: descriptor.Name
        );
        Assert.Equal(
            expected: lane,
            actual: descriptor.Lane
        );
        Assert.Equal(
            expected: shape,
            actual: descriptor.Shape
        );
        Assert.Equal(
            expected: kind,
            actual: descriptor.Kind
        );
        Assert.Equal(
            expected: role,
            actual: descriptor.Role
        );
        Assert.False(condition: descriptor.Generated);
        Assert.Equal(
            actual: descriptor.HostOwned,
            expected: hostOwned
        );
    }
}

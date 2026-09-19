using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>CONTRACT UNDER TEST: a document the validator passes always loads into the arena. Every shape the
/// arena's one import door refuses — kind, shape, addressable position, capacity, duplicate key, vector dimensions,
/// envelope, symbolic range, and the runtime-state fields a row does not carry — is refused at document ingress
/// first, so <c>ArenaImport</c> never decides a value the author could have reached.</summary>
public sealed class ArenaIngressValidationLawTests {
    public static TheoryData<string> Fixtures => [
        "cellsPastCapacity",
        "drawCursorOnANonDrawRow",
        "duplicateKey",
        "hostOwnedRowCarryingCells",
        "historyCursorOnANonRingRow",
        "keyNoLatticePositionAddresses",
        "keyNoRingPositionAddresses",
        "keyNoSlotPositionAddresses",
        "negativeDrawCursor",
        "negativeHistoryCursor",
        "negativePhaseSequence",
        "valueOutsideTheEnvelope",
        "valueOutsideTheSymbolicRange",
        "vectorDimensionDisagreement",
    ];

    private static CellName Name(string value) => CellName.Parse(candidate: value);
    private static WorldDefinition Document(string fixture) {
        var section = (fixture switch {
            "cellsPastCapacity" => new WorldStateSection(World: [new WorldStateRow(
                    Name: Name(value: "pile"),
                    Kind: CellKind.Int,
                    Domain: new StateDomain.Keys(),
                    Capacity: 1,
                    Cells: [Cell(key: "a"), Cell(key: "b")]
                )]),
            "drawCursorOnANonDrawRow" => new WorldStateSection(World: [Slot(name: "score") with { DrawCursor = 3L }]),
            "duplicateKey" => new WorldStateSection(World: [new WorldStateRow(
                    Name: Name(value: "pile"),
                    Kind: CellKind.Int,
                    Domain: new StateDomain.Keys(),
                    Capacity: 4,
                    Cells: [Cell(key: "a"), Cell(key: "a")]
                )]),
            "hostOwnedRowCarryingCells" => Lattice(cells: [Cell(key: "0")], field: new WorldStateFieldTrait(
                Initial: 0f,
                Max: 1f,
                Min: 0f
            )),
            "historyCursorOnANonRingRow" => new WorldStateSection(World: [Slot(name: "score") with { HistoryCursor = 2L }]),
            "keyNoLatticePositionAddresses" => Lattice(cells: [Cell(key: "99")]),
            "keyNoRingPositionAddresses" => new WorldStateSection(World: [new WorldStateRow(
                    Name: Name(value: "history"),
                    Kind: CellKind.Int,
                    Domain: new StateDomain.Ring(Capacity: 2),
                    Cells: [Cell(key: "7")]
                )]),
            "keyNoSlotPositionAddresses" => new WorldStateSection(World: [new WorldStateRow(
                    Name: Name(value: "score"),
                    Kind: CellKind.Int,
                    Domain: StateDomain.Slot.Instance,
                    Cells: [Cell(key: "elsewhere")]
                )]),
            "negativeDrawCursor" => new WorldStateSection(World: [Slot(name: "score") with { DrawCursor = -1L }]),
            "negativeHistoryCursor" => new WorldStateSection(World: [new WorldStateRow(
                    Name: Name(value: "history"),
                    Kind: CellKind.Int,
                    Domain: new StateDomain.Ring(Capacity: 2),
                    HistoryCursor: -1L
                )]),
            "negativePhaseSequence" => new WorldStateSection(World: [Slot(name: "turn") with { Phase = new StatePhase(Sequence: -1L), PhaseOf = "turn" }]),
            "valueOutsideTheEnvelope" => new WorldStateSection(World: [Slot(
                    name: "gauge",
                    value: 40L
                ) with { Max = 10L, Min = 0L }]),
            "valueOutsideTheSymbolicRange" => new WorldStateSection(
                Enums: [new StateEnum(
                        Members: [Name(value: "hearts"), Name(value: "spades")],
                        Name: Name(value: "suit")
                    )],
                World: [Slot(
                        name: "card",
                        value: 9L
                    ) with { Enum = Name(value: "suit") }]
            ),
            _ => new WorldStateSection(
                Spaces: [new StateSpace(
                        Dimensions: 16,
                        Model: "test",
                        Name: Name(value: "emb"),
                        Revision: "1"
                    )],
                World: [new WorldStateRow(
                        Name: Name(value: "point"),
                        Kind: CellKind.Vector,
                        Space: "emb",
                        Cells: [new StateCell(
                                Key: WorldStateRow.SlotKey,
                                Value: CellValue.Vector(components: Vector(dimensions: 8).Memory)
                            )]
                    )]
            ),
        });

        return new WorldDefinition(
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: section
        );
    }
    private static StateCell Cell(string key, long value = 0L) => new(
        Key: Name(value: key),
        Value: CellValue.Int(value: value)
    );
    private static WorldStateSection Lattice(IReadOnlyList<StateCell> cells, WorldStateFieldTrait? field = null) => new(
        Lattices: [new LatticeTopology.Grid(
                CellSize: 1f,
                Depth: 2,
                Name: "board",
                Origin: new DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                Width: 2
            )],
        World: [new WorldStateRow(
                Name: Name(value: "board"),
                Kind: CellKind.Int,
                Domain: new StateDomain.CellsOf(Topology: "board"),
                Cells: cells,
                Field: field
            )]
    );
    private static StateVector Vector(int dimensions) {
        var components = new sbyte[dimensions];

        components[0] = 127;
        Assert.True(condition: StateVector.TryCreate(
            components: components,
            error: out var error,
            vector: out var vector
        ), userMessage: error);

        return vector!;
    }
    private static WorldStateRow Slot(string name, long value = 0L) => new(
        Name: Name(value: name),
        Kind: CellKind.Int,
        Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: value)
            )]
    );
    // The arena's own answer for the same document, so a fixture that stopped being arena-refusable fails here
    // rather than passing as a validator that refuses something nothing else would.
    private static string ArenaRefusal(WorldDefinition definition) {
        try {
            return (StateArena.TryCreate(
                arena: out _,
                catalog: definition.StateCatalog,
                options: null,
                reason: out var reason,
                section: definition.StateRaw,
                time: ArenaTime.Origin
            )
                ? string.Empty
                : reason
            );
        } catch (Exception error) when ((error is ArgumentException or InvalidOperationException)) {
            return error.Message;
        }
    }

    [MemberData(memberName: nameof(Fixtures))]
    [Theory]
    public void EveryArenaImportRefusalIsRefusedAtDocumentIngress(string fixture) {
        var definition = Document(fixture: fixture);

        Assert.False(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: $"the validator admitted '{fixture}', which the arena refuses with: {ArenaRefusal(definition: definition)}"
        );
        Assert.NotEqual(
            actual: reason,
            expected: string.Empty
        );
        Assert.NotEqual(
            actual: ArenaRefusal(definition: definition),
            expected: string.Empty
        );
    }
}

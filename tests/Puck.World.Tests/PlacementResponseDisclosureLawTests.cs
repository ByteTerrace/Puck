using Puck.Commands;
using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Laws for a responsive placement read through a seat's disclosure: a placement whose response facet reads
/// a cell the reader may not read loses that entry's holding bit, so it shows the reader the first entry that holds over
/// what the reader may read (its own cells, and the lattice), or its authored prototype, on the wire and in the
/// placement census alike.</summary>
public sealed class PlacementResponseDisclosureLawTests {
    private const string Ember = "gateEmber";
    private const string FieldName = "heat";
    private const string Open = "gateOpen";
    private const string Shut = "gateShut";

    private static readonly Principal SeatOne = Principal.Seat(slot: 0);
    private static readonly Principal SeatTwo = Principal.Seat(slot: 1);

    private static WorldStateRow Slot(string name, StateVisibility? visibility = null) => new(
        Cells: [new StateCell(Key: StateRow.SlotKey, Value: CellValue.Int(value: 0L))],
        Kind: CellKind.Int,
        Name: CellName.Parse(candidate: name),
        Visibility: visibility
    );
    private static WorldPlacementResponse OnSlot(string row, string prototype) => new(
        When: new WorldPlacementResponseCondition.StateCondition(
            Comparison: ExpressionOp.GreaterOrEqual,
            State: row,
            Value: 1f
        ),
        PrototypeId: prototype
    );
    private static WorldPlacement Gate(string id, params WorldPlacementResponse[] respond) => new(
        Id: id,
        PrototypeId: Shut,
        Position: new DocumentVector3(value: Vector3.Zero),
        YawDegrees: 0f,
        Scale: 1f,
        Respond: respond
    );
    // Two gates over the same pair of prototypes: one swaps on a slot only seat 1 reads, the other on a slot every
    // reader reads.
    private static WorldDefinition Document() => (Fixtures.BuildDocument() with {
        CreationsRaw = [CreationFixtures.UnitSphere(id: Shut), CreationFixtures.UnitSphere(id: Open), CreationFixtures.UnitSphere(id: Ember)],
        PlacementRowsRaw = [Gate(id: "vaultGate", OnSlot(prototype: Open, row: "vault")), Gate(id: "scoreGate", OnSlot(prototype: Open, row: "score"))],
        StateRaw = new WorldStateSection(World: [Slot(name: "vault", visibility: new StateVisibility(Readers: ["seat1"])), Slot(name: "score")]),
    });
    private static void Set(WorldFixture fixture, string row, long value) {
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: row,
            Key: StateRow.SlotKey.Value,
            Value: value,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();
        fixture.Step();
    }
    private static WorldPlacement Find(IReadOnlyList<WorldPlacement> placements, string id) =>
        WorldDefinitionRows.FindPlacement(id: id, placements: placements)!;
    // What a presentation-tier peer serving one seat is handed on the wire, decoded as the peer decodes it.
    private static IReadOnlyList<WorldPlacement> Served(WorldFixture fixture, Principal recipient) {
        var bytes = WorldFederationCodec.EncodeDocument(
            authority: "boot",
            definition: fixture.Server.Definition,
            recipient: recipient,
            revision: 1,
            tier: WorldDisclosureTier.Presentation
        );

        Assert.True(condition: WorldFederationCodec.TryDecodeDocument(
            body: bytes,
            definition: out var served,
            failure: out var failure,
            tier: out _
        ), userMessage: $"the served document did not decode: {failure}");

        return served!.Placements;
    }

    // Seat 1's hidden slot opens its gate. A peer serving seat 2 is handed that gate shut, with no entry marked as
    // holding; seat 1's peer is handed it open. The public gate reaches seat 2 exactly as the server holds it, each
    // seat's placement census answers the same way, and no reading overwrites the authored prototype.
    [Fact]
    public void APeerServingSeatTwoNeverReceivesAPrototypeSwappedOnSeatOnesHiddenCell() {
        using var fixture = Fixtures.FreshServer(definition: Document());

        fixture.Step();
        Set(fixture: fixture, row: "vault", value: 1L);
        Set(fixture: fixture, row: "score", value: 1L);

        var live = fixture.Server.Definition.Placements;
        var forSeatTwo = Served(fixture: fixture, recipient: SeatTwo);
        var forSeatOne = Served(fixture: fixture, recipient: SeatOne);

        // The control: the live gate shows the entry seat 1's hidden slot holds.
        Assert.Equal(expected: Open, actual: Find(id: "vaultGate", placements: live).ShownPrototypeId);
        Assert.Equal(expected: 1, actual: Find(id: "vaultGate", placements: live).Holding);
        Assert.Equal(expected: Shut, actual: Find(id: "vaultGate", placements: forSeatTwo).ShownPrototypeId);
        Assert.Equal(expected: 0, actual: Find(id: "vaultGate", placements: forSeatTwo).Holding);
        Assert.Equal(expected: Open, actual: Find(id: "vaultGate", placements: forSeatOne).ShownPrototypeId);
        Assert.Equal(expected: Open, actual: Find(id: "scoreGate", placements: forSeatTwo).ShownPrototypeId);

        foreach (var placements in ((IReadOnlyList<WorldPlacement>[])[live, forSeatOne, forSeatTwo])) {
            Assert.All(collection: placements, action: static placement => Assert.Equal(expected: Shut, actual: placement.PrototypeId));
        }

        var census = fixture.Server.DescribePlacements(view: WorldStateReadView.Of(reader: SeatTwo, server: fixture.Server));

        Assert.Contains(actualString: census, expectedSubstring: $"'vaultGate' prototype={Shut}");
        Assert.Contains(actualString: census, expectedSubstring: $"'scoreGate' prototype={Open}");
        Assert.Contains(
            actualString: fixture.Server.DescribePlacements(view: WorldStateReadView.Of(reader: SeatOne, server: fixture.Server)),
            expectedSubstring: $"'vaultGate' prototype={Open}"
        );
    }
    // A facet reading a row that withholds some other cell from the reader keeps every entry over a cell the reader
    // may read: seat 2 reads the ledger's open cell but not its sealed one, and its peer is handed the gate the open
    // cell swapped.
    [Fact]
    public void AnEntryOverACellTheReaderMayReadStillHolds() {
        var document = Document();
        var ledger = new WorldStateRow(
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "open"), Value: CellValue.Int(value: 0L)),
                new StateCell(Key: CellName.Parse(candidate: "sealed"), Value: CellValue.Int(value: 0L), Visibility: new StateVisibility(Readers: ["seat1"])),
            ],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "ledger")
        );
        var gate = Gate(id: "ledgerGate", new WorldPlacementResponse(
            When: new WorldPlacementResponseCondition.StateCondition(
                Comparison: ExpressionOp.GreaterOrEqual,
                Key: "open",
                State: "ledger",
                Value: 1f
            ),
            PrototypeId: Open
        ));
        using var fixture = Fixtures.FreshServer(definition: document with {
            PlacementRowsRaw = [gate],
            StateRaw = document.StateRaw! with { World = [.. document.AuthoredState, ledger] },
        });

        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: "ledger",
            Key: "open",
            Value: 1L,
            Kind: WorldDocumentWriteKind.Set
        ));
        fixture.Step();
        fixture.Step();

        var served = Find(id: "ledgerGate", placements: Served(fixture: fixture, recipient: SeatTwo));

        Assert.Equal(expected: 1, actual: Find(id: "ledgerGate", placements: fixture.Server.Definition.Placements).Holding);
        Assert.Equal(expected: Open, actual: served.ShownPrototypeId);
        Assert.Equal(expected: 1, actual: served.Holding);
    }
    // A gate opens on seat 1's hidden slot and embers on the lattice's heat, and both hold. The lattice is not hidden
    // state, so seat 2's peer is handed the ember the heat holds, never the open gate the hidden slot holds and never
    // the authored one; seat 1's peer is handed the open gate, the first entry.
    [Fact]
    public void AFieldEntryHoldsForAReaderAHiddenCellEntryIsWithheldFrom() {
        var document = Document();
        var gate = Gate(
            id: "forgeGate",
            OnSlot(prototype: Open, row: "vault"),
            new WorldPlacementResponse(
                When: new WorldPlacementResponseCondition.FieldCondition(
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Field: FieldName,
                    Value: 0.2f
                ),
                PrototypeId: Ember
            )
        );
        using var fixture = Fixtures.FreshServer(definition: document with {
            PlacementRowsRaw = [gate],
            StateRaw = document.StateRaw! with {
                Lattices = [new WorldFieldTopology(
                    CellSize: 1f,
                    Depth: 1,
                    Layers: 1,
                    Name: "world",
                    Origin: new DocumentVector3(value: Vector3.Zero),
                    Reactions: [new WorldReaction.Transform(
                        Then: [new WorldFieldWrite(Field: FieldName, Op: WorldFieldWriteOp.Add, Value: 0.1f)],
                        When: []
                    )],
                    StepEveryTicks: 1,
                    Width: 1
                )],
                World = [
                    .. document.AuthoredState,
                    new WorldStateRow(
                        Domain: new StateDomain.CellsOf(Topology: "world"),
                        Field: new WorldStateFieldTrait(Initial: 0f, Max: 1f, Min: 0f),
                        Kind: CellKind.Fixed,
                        Name: CellName.Parse(candidate: FieldName)
                    ),
                ],
            },
        });

        fixture.Step();
        Set(fixture: fixture, row: "vault", value: 1L);
        fixture.Step();

        var live = Find(id: "forgeGate", placements: fixture.Server.Definition.Placements);
        var forSeatTwo = Find(id: "forgeGate", placements: Served(fixture: fixture, recipient: SeatTwo));

        // The control: both entries hold on the server, which shows the first.
        Assert.Equal(expected: 3, actual: live.Holding);
        Assert.Equal(expected: Open, actual: live.ShownPrototypeId);
        Assert.Equal(expected: Ember, actual: forSeatTwo.ShownPrototypeId);
        Assert.Equal(expected: 2, actual: forSeatTwo.Holding);
        Assert.Equal(expected: Open, actual: Find(id: "forgeGate", placements: Served(fixture: fixture, recipient: SeatOne)).ShownPrototypeId);
        Assert.Contains(
            actualString: fixture.Server.DescribePlacements(view: WorldStateReadView.Of(reader: SeatTwo, server: fixture.Server)),
            expectedSubstring: $"'forgeGate' prototype={Ember}"
        );
    }
}

using Puck.Testing;
using Puck.Commands;
using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>
/// THE LAW: a placement carrying a deal facet is a template whose children follow its keyed row — one child per
/// cell at the region's offsets, dealt through the ordinary mutation pipeline: a cell that arrives adds a child at
/// the lowest free offset, a cell that leaves removes its child and moves no sibling, a variant row selects the
/// prototype, an undo stays undone until the row moves, a replay reproduces the children on the same tick, and only
/// the sweep may mint a child's id.
/// </summary>
[Collection(AllocationCollection.Name)]
public sealed partial class PlacementDealLawTests {
    private const string AnchorCreation = "anchor";
    private const string RowName = "accounts";
    private const string StoreCreation = "store";
    private const string TemplateId = "stores";
    private const string VariantRowName = "levels";

    private static StateCell TextCell(string key) => new(
        Key: CellName.Parse(candidate: key),
        Value: CellValue.Text(value: key)
    );
    private static WorldStateRow AccountsRow(params string[] keys) => new(
        Name: CellName.Parse(candidate: RowName),
        Kind: CellKind.Text,
        Capacity: 4,
        Cells: [.. keys.Select(selector: TextCell)]
    );
    // A four-offset lattice along +X: offsets (0,0,0), (2,0,0), (4,0,0), (6,0,0) in deal order.
    private static WorldDistribution Lattice(int count = 4) => new(
        Region: new WorldDistributionRegion.Lattice(
            StepA: new DocumentVector3(value: new Vector3(
                x: 2f,
                y: 0f,
                z: 0f
            )),
            CountA: count,
            StepB: new DocumentVector3(value: new Vector3(
                x: 0f,
                y: 0f,
                z: 1f
            )),
            CountB: 1
        ),
        Fill: new WorldSequence(
            Name: WorldSequence.None,
            Offset: 0,
            Step: 0f
        )
    );
    private static WorldPlacement Template(WorldPlacementDeal? deal, WorldDistribution? distribution = null) => new(
        Id: TemplateId,
        PrototypeId: StoreCreation,
        Position: new DocumentVector3(value: new Vector3(
            x: 10f,
            y: 0f,
            z: 10f
        )),
        YawDegrees: 0f,
        Scale: 1f,
        Distribution: (distribution ?? Lattice()),
        Solid: new WorldSolid(Margin: 0f),
        Deal: deal
    );
    private static WorldDefinition Document(WorldPlacement template, WorldStateRow row, WorldStateRow? variantRow = null) {
        var document = Fixtures.BuildDocument();

        return (document with {
            StateRaw = new WorldStateSection(World: ((variantRow is null)
            ? [row]
            : [row, variantRow])),
            CreationsRaw = [CreationFixtures.UnitSphere(id: StoreCreation), CreationFixtures.UnitSphere(id: AnchorCreation)],
            PlacementRowsRaw = [template],
        });
    }
    private static WorldDefinition Dealt(params string[] keys) => Document(
        row: AccountsRow(keys),
        template: Template(deal: new WorldPlacementDeal(Row: RowName))
    );
    private static List<WorldPlacement> Children(WorldServer server) {
        var template = WorldDefinitionRows.FindPlacement(
            id: TemplateId,
            placements: server.Definition.Placements
        );

        return [.. server.Definition.Placements.Where(predicate: placement => WorldPlacementDeal.IsChild(
                parent: template,
                placement: placement
            ))];
    }
    private static WorldPlacement Child(WorldServer server, string key) =>
        Assert.Single(
            collection: Children(server: server),
            predicate: child => (child.Id == WorldPlacementDeal.ChildId(
                key: key,
                template: TemplateId
            ))
        );
    private static Vector3 Offset(int slot) => new(
        x: (2f * slot),
        y: 0f,
        z: 0f
    );
    private static WorldMutation.UpsertStateCell Upsert(string key) => new(
        Principal: Principal.Console,
        Row: RowName,
        Key: key,
        Value: 0L,
        Kind: WorldDocumentWriteKind.Set,
        Text: key
    );
    private static string Describe(WorldServer server) => string.Join(
        separator: "|",
        values: Children(server: server).OrderBy(
            keySelector: child => child.Id,
            comparer: StringComparer.Ordinal
        ).Select(selector: child => $"{child.Id}@{child.Position.Value}:{child.PrototypeId}")
    );

    [Fact]
    public void InstanceOwnedTransformAndFacetsSurviveInventoryAndVariantChanges() {
        var template = Template(new WorldPlacementDeal(
            RowName,
            new WorldPlacementDealVariants(
                VariantRowName,
                new Dictionary<string, string> { ["1"] = AnchorCreation }
            ),
            new WorldPlacementDealPreserve(
                Transform: true,
                Facets: true
            )
        ));
        var variants = new WorldStateRow(
            Name: CellName.Parse(candidate: VariantRowName),
            Kind: CellKind.Int,
            Capacity: 4,
            Cells: []
        );
        using var fixture = Fixtures.FreshServer(definition: Document(
            template,
            AccountsRow(
                "a",
                "b"
            ),
            variants
        ));

        fixture.Step();
        var moved = Child(
            fixture.Server,
            "a"
        ) with {
            Position = new Vector3(
            x: 17,
            y: 0,
            z: 9
        ),
            YawDegrees = 45,
            Region = new WorldPlacementRegion(Radius: 3),
        };

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertPlacement(
            Principal.Console,
            moved
        ));
        fixture.Step();
        fixture.Server.EnqueueMutation(Upsert(key: "c"));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(
            Principal.Console,
            VariantRowName,
            "a",
            1,
            WorldDocumentWriteKind.Set
        ));
        fixture.Step();
        var actual = Child(
            fixture.Server,
            "a"
        );

        Assert.Equal(
            moved.Position,
            actual.Position
        );
        Assert.Equal(
            moved.YawDegrees,
            actual.YawDegrees
        );
        Assert.Equal(
            moved.Region,
            actual.Region
        );
        Assert.Equal(
            AnchorCreation,
            actual.PrototypeId
        );
        Assert.Equal(
            moved.DealSlot,
            actual.DealSlot
        );
        Assert.Equal(
            2,
            Child(
                fixture.Server,
                "c"
            ).DealSlot
        );
    }
    /// <summary>Three cells deal three children on the first sweep: <c>stores/a</c>, <c>stores/b</c>, <c>stores/c</c>,
    /// each parented to the template at the region's first three offsets, yaw 0, scale 1, carrying the template's
    /// prototype and solid facet. The template itself stays a single row. CONTROL: the same row with no deal facet
    /// deals nothing.</summary>
    [Fact]
    public void ThreeCellsDealThreeChildrenAtTheFirstThreeOffsets() {
        using var fixture = Fixtures.FreshServer(definition: Dealt(
            "a",
            "b",
            "c"
        ));

        fixture.Step();

        var children = Children(server: fixture.Server);

        Assert.Equal(
            expected: 3,
            actual: children.Count
        );

        foreach (var (key, slot) in new[] { ("a", 0), ("b", 1), ("c", 2) }) {
            var child = Child(
                server: fixture.Server,
                key: key
            );

            Assert.Equal(
                expected: Offset(slot: slot),
                actual: child.Position.Value
            );
            Assert.Equal(
                expected: TemplateId,
                actual: child.Parent
            );
            Assert.Equal(
                expected: StoreCreation,
                actual: child.PrototypeId
            );
            Assert.Equal(
                expected: 0f,
                actual: child.YawDegrees
            );
            Assert.Equal(
                expected: 1f,
                actual: child.Scale
            );
            Assert.Equal(
                expected: new WorldSolid(Margin: 0f),
                actual: child.Solid
            );
            Assert.Null(@object: child.Deal);
            Assert.Null(@object: child.Distribution);
            // The child's world frame composes over the template's: the template stands at (10, 0, 10).
            Assert.Equal(
                expected: (new Vector3(
                    x: 10f,
                    y: 0f,
                    z: 10f
                ) + Offset(slot: slot)),
                actual: WorldDefinitionRows.ResolvedPosition(
                    definition: fixture.Server.ExportDefinition(),
                    placement: child
                )
            );
        }

        using var control = Fixtures.FreshServer(definition: Document(
            row: AccountsRow(
                "a",
                "b",
                "c"
            ),
            template: Template(deal: null)
        ));

        control.Step();

        Assert.Single(collection: control.Server.ExportDefinition().Placements);
    }
    /// <summary>A fourth cell upserted after the first deal adds exactly the fourth child, at the fourth offset, on
    /// the next sweep; the three already dealt stay where they were.</summary>
    [Fact]
    public void UpsertingAFourthCellAddsTheFourthChildOnTheNextSweep() {
        using var fixture = Fixtures.FreshServer(definition: Dealt(
            "a",
            "b",
            "c"
        ));

        fixture.Step();

        var before = Describe(server: fixture.Server);

        fixture.Server.EnqueueMutation(mutation: Upsert(key: "d"));
        fixture.Step();

        Assert.Equal(
            expected: 4,
            actual: Children(server: fixture.Server).Count
        );
        Assert.Equal(
            expected: Offset(slot: 3),
            actual: Child(
                server: fixture.Server,
                key: "d"
            ).Position.Value
        );
        Assert.StartsWith(
            expectedStartString: before,
            actualString: Describe(server: fixture.Server)
        );
    }
    /// <summary>Removing the second cell removes its child and leaves the others at their own offsets — a child never
    /// moves when a sibling leaves. The next cell dealt takes the offset the departure freed, the lowest free one,
    /// so the region never runs out while the row is within its capacity. THE DISCRIMINATOR: under a
    /// present-cells-ordinal rule, <c>c</c> would have slid from offset 2 to offset 1 and <c>e</c> would have taken
    /// offset 2.</summary>
    [Fact]
    public void RemovingTheSecondCellRemovesItsChildAndLeavesTheOthersInPlace() {
        using var fixture = Fixtures.FreshServer(definition: Dealt(
            "a",
            "b",
            "c"
        ));

        fixture.Step();
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemoveStateCell(
            Principal: Principal.Console,
            Row: RowName,
            Key: "b"
        ));
        fixture.Step();

        Assert.Equal(
            expected: 2,
            actual: Children(server: fixture.Server).Count
        );
        Assert.Equal(
            expected: Offset(slot: 0),
            actual: Child(
                server: fixture.Server,
                key: "a"
            ).Position.Value
        );
        Assert.Equal(
            expected: Offset(slot: 2),
            actual: Child(
                server: fixture.Server,
                key: "c"
            ).Position.Value
        );

        fixture.Server.EnqueueMutation(mutation: Upsert(key: "e"));
        fixture.Step();

        Assert.Equal(
            expected: 3,
            actual: Children(server: fixture.Server).Count
        );
        Assert.Equal(
            expected: Offset(slot: 1),
            actual: Child(
                server: fixture.Server,
                key: "e"
            ).Position.Value
        );
        Assert.Equal(
            expected: Offset(slot: 2),
            actual: Child(
                server: fixture.Server,
                key: "c"
            ).Position.Value
        );
    }
    /// <summary>A variant row selects the prototype: a text cell whose text maps, and an integer cell whose value
    /// spelled as text maps, deal the mapped creation; every other cell deals the template's own. Changing the
    /// variant cell re-deals that one child with the new prototype at the same offset.</summary>
    [Fact]
    public void AVariantRowSelectsThePrototype() {
        var byText = Document(
            row: AccountsRow(
                "a",
                "b",
                "c"
            ),
            template: Template(deal: new WorldPlacementDeal(
                Row: RowName,
                Variants: new WorldPlacementDealVariants(
                    Row: RowName,
                    Map: new Dictionary<string, string>(comparer: StringComparer.Ordinal) { ["b"] = AnchorCreation }
                )
            ))
        );

        using (var fixture = Fixtures.FreshServer(definition: byText)) {
            fixture.Step();

            Assert.Equal(
                expected: StoreCreation,
                actual: Child(
                    server: fixture.Server,
                    key: "a"
                ).PrototypeId
            );
            Assert.Equal(
                expected: AnchorCreation,
                actual: Child(
                    server: fixture.Server,
                    key: "b"
                ).PrototypeId
            );
            Assert.Equal(
                expected: StoreCreation,
                actual: Child(
                    server: fixture.Server,
                    key: "c"
                ).PrototypeId
            );
        }

        var byInteger = Document(
            row: AccountsRow(
                "a",
                "b",
                "c"
            ),
            variantRow: new WorldStateRow(
                Name: CellName.Parse(candidate: VariantRowName),
                Kind: CellKind.Int,
                Capacity: 4,
                Cells: [new StateCell(
                        Key: CellName.Parse(candidate: "c"),
                        Value: CellValue.Int(value: 2L)
                    )]
            ),
            template: Template(deal: new WorldPlacementDeal(
                Row: RowName,
                Variants: new WorldPlacementDealVariants(
                    Row: VariantRowName,
                    Map: new Dictionary<string, string>(comparer: StringComparer.Ordinal) { ["2"] = AnchorCreation }
                )
            ))
        );

        using (var fixture = Fixtures.FreshServer(definition: byInteger)) {
            fixture.Step();

            Assert.Equal(
                expected: StoreCreation,
                actual: Child(
                    server: fixture.Server,
                    key: "b"
                ).PrototypeId
            );
            Assert.Equal(
                expected: AnchorCreation,
                actual: Child(
                    server: fixture.Server,
                    key: "c"
                ).PrototypeId
            );

            fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Principal: Principal.Console,
                Row: VariantRowName,
                Key: "c",
                Value: 1L,
                Kind: WorldDocumentWriteKind.Set
            ));
            fixture.Step();

            var redealt = Child(
                server: fixture.Server,
                key: "c"
            );

            Assert.Equal(
                expected: StoreCreation,
                actual: redealt.PrototypeId
            );
            Assert.Equal(
                expected: Offset(slot: 2),
                actual: redealt.Position.Value
            );
        }
    }
    /// <summary>Undoing a membership write re-derives the children without journalling their projection.</summary>
    [Fact]
    public void UndoOfMembershipReprojectsChildren() {
        using var fixture = Fixtures.FreshServer(definition: Dealt("a", "b", "c"));
        fixture.Step();
        Assert.Equal(3, Children(fixture.Server).Count);
        Assert.Equal(0, fixture.Server.JournalLength);
        fixture.Server.EnqueueMutation(Upsert("d"));
        fixture.Step();
        Assert.Equal(4, Children(fixture.Server).Count);
        fixture.Server.EnqueueUndo(1, Principal.Console);
        fixture.Step();
        Assert.Equal(3, Children(fixture.Server).Count);
    }
    /// <summary>A recorded session whose cells arrive and leave on given ticks replays to the same children on the
    /// same ticks: the tape carries the cell writes, and the sweep re-derives the children from them.</summary>
    [Fact]
    public void AReplayOfTheTapeReproducesTheChildrenOnTheSameTick() {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-");

        using var fixture = Fixtures.FreshServer(definition: Dealt("a"));
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var name = $"deal-replay-{Guid.NewGuid():N}";

        Assert.True(
            condition: tape.TryBeginRecording(
                name: name,
                refusal: out var refusal
            ),
            userMessage: $"refused to arm: {refusal}"
        );

        var recorded = new List<string>();

        for (var tick = 0; (tick < 5); tick++) {
            switch (tick) {
                case 1:
                    _ = transport.SubmitWorldMutation(mutation: Upsert(key: "b"));

                    break;
                case 2:
                    _ = transport.SubmitWorldMutation(mutation: Upsert(key: "c"));

                    break;
                case 3:
                    _ = transport.SubmitWorldMutation(mutation: new WorldMutation.RemoveStateCell(
                        Principal: Principal.Console,
                        Row: RowName,
                        Key: "b"
                    ));

                    break;
            }

            fixture.Step();
            tape.NoteTick();
            recorded.Add(item: Describe(server: fixture.Server));
        }

        _ = tape.StopRecording();

        // The recorded trajectory is not trivial: children arrive and leave across the ticks.
        Assert.Equal(
            expected: 4,
            actual: recorded.Distinct(comparer: StringComparer.Ordinal).Count()
        );
        Assert.Contains(
            expected: "stores/b",
            collection: recorded[1].Split(separator: '|').Select(selector: static entry => entry.Split(separator: '@')[0])
        );
        Assert.DoesNotContain(
            expected: "stores/b",
            collection: recorded[3].Split(separator: '|').Select(selector: static entry => entry.Split(separator: '@')[0])
        );

        Assert.True(
            condition: tape.TryBeginDrive(
                documentPath: null,
                forkName: $"{name}-fork",
                name: name,
                refusal: out refusal,
                toTick: null
            ),
            userMessage: $"refused to drive: {refusal}"
        );

        var replayed = new List<string>();

        while (tape.Mode == WorldReplayMode.Replaying) {
            tape.InjectDriveTick();
            fixture.Step();
            tape.NoteTick();
            replayed.Add(item: Describe(server: fixture.Server));
        }

        _ = tape.StopRecording();

        Assert.Equal(
            actual: replayed,
            expected: recorded
        );
    }
    /// <summary>An authored placement id spelling the child separator refuses by name; the identical row parented
    /// to a dealt template, spelled as that template's child, validates — the one shape the sweep mints.</summary>
    [Fact]
    public void AnAuthoredChildIdRefusesByName() {
        var stray = (Dealt("a") with {
            PlacementRowsRaw = [
                Template(deal: new WorldPlacementDeal(Row: RowName)),
                new WorldPlacement(
                Id: "shed/a",
                PrototypeId: StoreCreation,
                Position: new DocumentVector3(value: Vector3.Zero),
                YawDegrees: 0f,
                Scale: 1f
            ),
            ],
        });
        var dealt = (Dealt("a") with {
            PlacementRowsRaw = [
                Template(deal: new WorldPlacementDeal(Row: RowName)),
                new WorldPlacement(
                Id: WorldPlacementDeal.ChildId(
                    key: "a",
                    template: TemplateId
                ),
                PrototypeId: StoreCreation,
                Position: new DocumentVector3(value: Vector3.Zero),
                YawDegrees: 0f,
                Scale: 1f,
                Parent: TemplateId,
                DealSlot: 0
            ),
            ],
        });

        Laws.RefusalWithControl(
            lawId: "placement-deal.authored-child-id",
            deniedOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: stray,
                reason: out _
            ),
            controlOutcome: () => WorldDefinitionValidator.TryValidateLocally(
                definition: dealt,
                reason: out _
            )
        );

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: stray,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "'shed/a' spells the dealt-child separator"
        );
    }
    /// <summary>The deal facet's own refusals, each by name: a row the document does not declare, a slot row, a
    /// capacity past the region's offsets (quoting both), a disc region, a missing distribution, a variant prototype
    /// that resolves to nothing, and the facets a template cannot carry. CONTROL: the well-formed template validates.</summary>
    [Fact]
    public void TheDealFacetRefusesByName() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Dealt("a"),
                reason: out var accepted
            ),
            userMessage: accepted
        );

        AssertRefusedNaming(
            definition: Document(
                row: AccountsRow("a"),
                template: Template(deal: new WorldPlacementDeal(Row: "ledger"))
            ),
            needle: "names state row 'ledger', which state.world does not declare"
        );
        AssertRefusedNaming(
            definition: Document(
                row: new WorldStateRow(
                    Name: CellName.Parse(candidate: RowName),
                    Kind: CellKind.Int
                ),
                template: Template(deal: new WorldPlacementDeal(Row: RowName))
            ),
            needle: "which is a slot"
        );
        AssertRefusedNaming(
            definition: Document(
                row: AccountsRow("a"),
                template: Template(
                    deal: new WorldPlacementDeal(Row: RowName),
                    distribution: Lattice(count: 3)
                )
            ),
            needle: "has capacity 4, but placements[0].distribution materializes 3 instance(s)"
        );
        AssertRefusedNaming(
            definition: Document(
                row: AccountsRow("a"),
                template: Template(
                    deal: new WorldPlacementDeal(Row: RowName),
                    distribution: new WorldDistribution(
                        Region: new WorldDistributionRegion.Disc(Radius: 2f),
                        Fill: WorldSequence.AdditiveDefault
                    )
                )
            ),
            needle: "'disc' does not"
        );
        AssertRefusedNaming(
            definition: Document(
                row: AccountsRow("a"),
                template: (Template(deal: new WorldPlacementDeal(Row: RowName)) with { Distribution = null })
            ),
            needle: "requires placements[0].distribution"
        );
        AssertRefusedNaming(
            definition: Document(
                row: AccountsRow("a"),
                template: Template(deal: new WorldPlacementDeal(
                    Row: RowName,
                    Variants: new WorldPlacementDealVariants(
                        Row: RowName,
                        Map: new Dictionary<string, string>(comparer: StringComparer.Ordinal) { ["a"] = "silo" }
                    )
                ))
            ),
            needle: "deal.variants.map['a'] 'silo' names no creation row"
        );
        AssertRefusedNaming(
            definition: Document(
                row: AccountsRow("a"),
                template: (Template(deal: new WorldPlacementDeal(Row: RowName)) with {
                    Region = new WorldPlacementRegion(Radius: 1f),
                    Mirror = new WorldPlacementMirror(
                    Normal: new DocumentVector3(value: Vector3.UnitX),
                    Offset: 0f
                ),
                })
            ),
            needle: "refused alongside inhabit/attach/respond/mirror/faceSources"
        );
    }
    private static void AssertRefusedNaming(WorldDefinition definition, string needle) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: needle
        );
    }
}

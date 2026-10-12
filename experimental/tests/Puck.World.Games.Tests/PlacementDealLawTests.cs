using Puck.Testing;
using Puck.Commands;
using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Assets.Documents;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.Hosting;
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
public sealed partial class PlacementDealLawTests(ITestOutputHelper output) {
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
    /// <summary>A quiet tick — no row change, no other mutation — allocates nothing in the sweep: the per-tick
    /// allocation of a dealt fixture at steady state is the same as the identical fixture without the deal facet,
    /// on the code-built document and on the shipped granary court isolated from unrelated island simulation.</summary>
    [Fact]
    public void AQuietTickAllocatesNothingInTheSweep() {
        var small = Measure(
            definition: Dealt(
                "a",
                "b",
                "c"
            ),
            seed: null
        );
        var smallControl = Measure(
            definition: Document(
                row: AccountsRow(
                    "a",
                    "b",
                    "c"
                ),
                template: Template(deal: null)
            ),
            seed: null
        );

        output.WriteLine(message: $"fixture: dealt median {small:N0} bytes/tick, control median {smallControl:N0} bytes/tick");
        Assert.True(
            condition: (small <= smallControl),
            userMessage: $"a dealt fixture's quiet tick allocated {small:N0} bytes against the control's {smallControl:N0}"
        );

        var source = AuthoredGameFixtures.Nexus;
        var ids = new HashSet<string>(comparer: StringComparer.Ordinal) { "granaries$granaryCourt", "granaries$granaryStore", "granaries$granaryAnchor" };
        var island = Fixtures.BuildDocument() with {
            Text = source.Text,
            CreationsRaw = [.. source.Creations.Where(predicate: row => ids.Contains(item: row.Id))],
            PlacementRowsRaw = [.. source.Placements.Where(predicate: row => (row.Id is "granaries$granaryCourt" or "granaries$granaryStores"))],
            StateRaw = new WorldStateSection(World: [.. source.State.Where(predicate: row => row.Name.Value.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "granaries$"
            ))]),
        };
        var template = Assert.Single(
            collection: island.Placements,
            predicate: static placement => (placement.Deal is not null)
        );
        var seed = new WorldMutation.Batch(
            Principal: Principal.Console,
            Mutations: [
            new WorldMutation.UpsertStateCell(
                    Principal: Principal.Console,
                    Row: template.Deal!.Row,
                    Key: "bytrcstp001",
                    Value: 0L,
                    Kind: WorldDocumentWriteKind.Set,
                    Text: "bytrcstp001"
                ),
            new WorldMutation.UpsertStateCell(
                    Principal: Principal.Console,
                    Row: template.Deal!.Row,
                    Key: "bytrcstp002",
                    Value: 0L,
                    Kind: WorldDocumentWriteKind.Set,
                    Text: "bytrcstp002"
                ),
            new WorldMutation.UpsertStateCell(
                    Principal: Principal.Console,
                    Row: template.Deal!.Row,
                    Key: "bytrcstp003",
                    Value: 0L,
                    Kind: WorldDocumentWriteKind.Set,
                    Text: "bytrcstp003"
                ),
        ]
        );
        var granaries = Measure(
            definition: island,
            seed: seed
        );
        var granariesControl = Measure(
            definition: (island with {
                PlacementRowsRaw = [.. island.Placements.Select(selector: placement => (ReferenceEquals(
                    objA: placement,
                    objB: template
                )
            ? (placement with { Deal = null })
            : placement))],
            }),
            seed: seed
        );

        output.WriteLine(message: $"granaries: dealt median {granaries:N0} bytes/tick, control median {granariesControl:N0} bytes/tick");
        Assert.True(
            condition: (granaries <= granariesControl),
            userMessage: $"the granaries' quiet tick allocated {granaries:N0} bytes against the control's {granariesControl:N0}"
        );
    }

    private static long Measure(WorldDefinition definition, WorldMutation? seed) {
        using var fixture = Fixtures.FreshServer(definition: definition);
        var width = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));

        if (seed is not null) {
            fixture.Server.EnqueueMutation(mutation: seed);
        }

        for (var tick = 0; (tick < 8); tick++) {
            fixture.Step(stepTicks: width);
        }

        var samples = new long[120];

        for (var tick = 0; (tick < samples.Length); tick++) {
            samples[tick] = AllocationWindow.Total(window: () => fixture.Step(stepTicks: width));
        }

        Array.Sort(array: samples);

        return samples[(samples.Length / 2)];
    }
}

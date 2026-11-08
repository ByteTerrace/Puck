using System.Numerics;
using Puck.Commands;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary><see cref="Server.WorldPopulation.ReconcileKeeps"/> answers from the reconcile an install runs: a body the
/// install retires or replaces is one the forecast does not keep, and the forecast changes nothing.</summary>
/// <remarks>The census re-clamp needs census bodies beside inhabitants, which only the population API or the
/// <c>world.population</c> verb sets, and a <c>.puck</c> test block can carry neither, so this is a law rather than a
/// witness in <c>worlds/debug/rule-faults-tests.puck</c>.</remarks>
public sealed class ReconcileForecastLawTests {
    private const string Prototype = "stand-in";
    private const string CountRow = "count";

    private static WorldPlacement Inhabited(string id, float x, WorldPlacementInhabitCount? count = null) => new(
        Id: id,
        PrototypeId: Prototype,
        Position: new Puck.Assets.Documents.DocumentVector3(value: new Vector3(
            x: x,
            y: 0f,
            z: 0f
        )),
        YawDegrees: 0f,
        Scale: 1f,
        Inhabit: new WorldPlacementInhabit(
            Kit: Fixtures.SeatKitName,
            Look: null,
            Source: IntentSource.Idle,
            Count: (count ?? new WorldPlacementInhabitCount(Literal: 1)),
            Distribution: new WorldDistribution(
                Region: new WorldDistributionRegion.Disc(
                    Radius: 0.5f,
                    SampleCount: 1
                ),
                Fill: new WorldSequence(
                    Name: WorldSequence.Additive,
                    Offset: 0,
                    Step: 0.618034f
                )
            )
        )
    );

    // Four peer slots. The inhabitants take them from the top down: "first" the highest, "low" the lowest.
    private static WorldDefinition Document() {
        var document = Fixtures.BuildDocument();

        return (document with {
            CreationsRaw = [CreationFixtures.UnitSphere(id: Prototype)],
            PlacementRowsRaw = [
                Inhabited(id: "first", x: 0f),
                Inhabited(id: "second", x: 2f),
                Inhabited(id: "third", x: 4f),
                Inhabited(id: "low", x: 6f),
            ],
            PopulationRaw = (document.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 4),
                ReconnectGraceSeconds = 0f,
            }),
        });
    }
    // Three literal inhabitants over one whose count reads the Int cell `count` (seed `seed`). They take the four peer
    // slots from the top down, so "low", the cell-driven one, holds the lowest.
    private static WorldDefinition CountedDocument(int seed, params string[] removed) {
        var document = Fixtures.BuildDocument();
        WorldPlacement[] placements = [
            Inhabited(id: "observer", x: 0f),
            Inhabited(id: "temporary", x: 2f),
            Inhabited(id: "middle", x: 4f),
            Inhabited(id: "low", x: 6f, count: new WorldPlacementInhabitCount(Row: CountRow)),
        ];

        return (document with {
            CreationsRaw = [CreationFixtures.UnitSphere(id: Prototype)],
            PlacementRowsRaw = [.. placements.Where(predicate: placement => !removed.Contains(value: placement.Id))],
            PopulationRaw = (document.Population with {
                CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 4),
                ReconnectGraceSeconds = 0f,
            }),
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                    Name: CellName.Parse(candidate: CountRow),
                    Kind: CellKind.Int,
                    Min: 0,
                    Max: 4,
                    Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: seed)
                    )]
                ),
            ]),
        });
    }
    private static void Remove(WorldFixture fixture, string id) {
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.RemovePlacement(
            Id: id,
            Principal: Principal.Console
        ));
        fixture.Step();
    }

    [Fact]
    public void ACensusBodyTheReClampRetiresIsOneTheForecastDoesNotKeep() {
        using var fixture = Fixtures.FreshServer(definition: Document());
        var population = fixture.Server.Population;
        var seats = population.LocalSeatCount;
        var low = seats;
        var lowerCensus = (seats + 1);
        var upperCensus = (seats + 2);
        var first = (seats + 3);

        Assert.Equal(
            expected: "low",
            actual: population.InhabitantPlacementId(index: low)
        );
        // Free the two middle slots, then seat two census bodies in them.
        Remove(fixture: fixture, id: "second");
        Remove(fixture: fixture, id: "third");
        Assert.Equal(
            expected: 2,
            actual: population.SetSimulatedCount(count: 2)
        );
        Assert.True(condition: population.IsActive(index: lowerCensus) && (population.InhabitantPlacementId(index: lowerCensus) is null));
        Assert.True(condition: population.IsActive(index: upperCensus) && (population.InhabitantPlacementId(index: upperCensus) is null));

        var current = fixture.Server.Definition;
        var proposed = (current with {
            PlacementRowsRaw = [.. current.Placements.Where(predicate: static placement => (placement.Id != "low"))],
        });
        var generations = new int[population.Capacity];

        for (var index = 0; (index < population.Capacity); index++) {
            generations[index] = population.Generation(index: index);
        }

        // Removing "low" frees the slot below the census, so the re-clamp takes it and retires the upper census body.
        Assert.False(condition: Forecast(fixture: fixture, definition: proposed, structural: true, body: upperCensus));
        Assert.False(condition: Forecast(fixture: fixture, definition: proposed, structural: true, body: low));
        Assert.True(condition: Forecast(fixture: fixture, definition: proposed, structural: true, body: lowerCensus));
        Assert.True(condition: Forecast(fixture: fixture, definition: proposed, structural: true, body: first));
        // Without the structural pass nothing moves.
        Assert.True(condition: Forecast(fixture: fixture, definition: current, structural: false, body: upperCensus));

        // The forecast changed nothing.
        for (var index = 0; (index < population.Capacity); index++) {
            Assert.Equal(
                expected: generations[index],
                actual: population.Generation(index: index)
            );
        }
        Assert.True(condition: population.IsActive(index: upperCensus));
        Assert.Equal(
            expected: "low",
            actual: population.InhabitantPlacementId(index: low)
        );

        // The install does what the forecast said.
        Remove(fixture: fixture, id: "low");
        Assert.False(condition: population.IsActive(index: upperCensus));
        Assert.True(condition: population.IsActive(index: low) && (population.InhabitantPlacementId(index: low) is null));
        Assert.Equal(
            expected: generations[lowerCensus],
            actual: population.Generation(index: lowerCensus)
        );
        Assert.Equal(
            expected: generations[first],
            actual: population.Generation(index: first)
        );
    }

    [Fact]
    public void ACensusBodyOneInstallAdmitsAndRetiresStaysRetired() {
        using var fixture = Fixtures.FreshServer(definition: CountedDocument(seed: 1));
        var population = fixture.Server.Population;
        var seats = population.LocalSeatCount;
        var low = seats;
        var middle = (seats + 1);
        var census = (seats + 2);
        var observer = (seats + 3);

        Assert.Equal(
            expected: "low",
            actual: population.InhabitantPlacementId(index: low)
        );
        Assert.Equal(
            expected: "middle",
            actual: population.InhabitantPlacementId(index: middle)
        );
        // Free the slot "temporary" held, then seat one census body in it.
        Remove(fixture: fixture, id: "temporary");
        Assert.Equal(
            expected: 1,
            actual: population.SetSimulatedCount(count: 1)
        );
        Assert.True(condition: population.IsActive(index: census) && (population.InhabitantPlacementId(index: census) is null));

        var generations = new int[population.Capacity];

        for (var index = 0; (index < population.Capacity); index++) {
            generations[index] = population.Generation(index: index);
        }

        // One install that zeroes the count and removes "middle": the structural re-clamp moves the census body from
        // its slot down into the one "middle" frees, and the count re-clamp moves it again, into the one "low" frees.
        // The cell reads the proposed document's seed, as the install reads its arena.
        var proposed = CountedDocument(seed: 0, removed: ["temporary", "middle"]);

        Assert.True(condition: Forecast(fixture: fixture, definition: proposed, arena: null, structural: true, body: observer));
        Assert.False(condition: Forecast(fixture: fixture, definition: proposed, arena: null, structural: true, body: low));
        Assert.False(condition: Forecast(fixture: fixture, definition: proposed, arena: null, structural: true, body: middle));
        Assert.False(condition: Forecast(fixture: fixture, definition: proposed, arena: null, structural: true, body: census));

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.Batch(
            Principal: Principal.Console,
            Mutations: [
                new WorldMutation.UpsertStateCell(
                    Principal: Principal.Console,
                    Row: CountRow,
                    Key: WorldStateRow.SlotKey.Value,
                    Value: 0L,
                    Kind: WorldDocumentWriteKind.Set
                ),
                new WorldMutation.RemovePlacement(
                    Id: "middle",
                    Principal: Principal.Console
                ),
            ]
        ));
        fixture.Step();

        // One census body, at the lowest slot, and the census count the install began with.
        Assert.Null(@object: population.InhabitantPlacementId(index: low));
        Assert.True(condition: population.IsActive(index: low));
        Assert.False(condition: population.IsActive(index: middle));
        Assert.False(condition: population.IsActive(index: census));
        Assert.Equal(
            expected: 1,
            actual: population.SimulatedCount
        );
        // The install kept what the forecast kept and replaced what it did not.
        Assert.True(condition: population.IsActive(index: observer));
        Assert.Equal(
            expected: generations[observer],
            actual: population.Generation(index: observer)
        );
        Assert.NotEqual(
            expected: generations[low],
            actual: population.Generation(index: low)
        );
    }

    private static bool Forecast(WorldFixture fixture, WorldDefinition definition, bool structural, int body) => Forecast(
        arena: fixture.Server.Arena,
        body: body,
        definition: definition,
        fixture: fixture,
        structural: structural
    );
    private static bool Forecast(WorldFixture fixture, WorldDefinition definition, StateArena? arena, bool structural, int body) => fixture.Server.Population.ReconcileKeeps(
        arena: arena,
        bodies: [body],
        definition: definition,
        structural: structural,
        tick: fixture.Server.NextInputTick
    );
}

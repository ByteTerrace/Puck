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

    private static WorldPlacement Inhabited(string id, float x) => new(
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
            Count: new WorldPlacementInhabitCount(Literal: 1),
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

    private static bool Forecast(WorldFixture fixture, WorldDefinition definition, bool structural, int body) => fixture.Server.Population.ReconcileKeeps(
        arena: fixture.Server.Arena,
        bodies: [body],
        definition: definition,
        structural: structural,
        tick: fixture.Server.NextInputTick
    );
}

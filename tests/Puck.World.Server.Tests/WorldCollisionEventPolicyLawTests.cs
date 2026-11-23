using Puck.Maths;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Acceptance evidence for bounded body-overlap events in dense crowds.</summary>
public sealed class WorldCollisionEventPolicyLawTests(ITestOutputHelper output) {
    private static void Coincide(WorldFixture fixture) {
        for (var index = fixture.Server.Population.LocalSeatCount; (index < fixture.Server.Population.Capacity); index++) {
            fixture.Server.Body(index: index)!.Pose(
                FixedVector3.Zero,
                FixedQ4816.Zero,
                FixedQ4816.Zero,
                FixedQ4816.Zero
            );
        }
    }
    private static WorldDefinition Document(WorldCollisionEvents events) {
        var definition = Fixtures.BuildDocument();

        return definition with {
            CollisionRaw = definition.Collision with { EventsRaw = events },
            PopulationRaw = definition.Population with { CapacityRaw = 68, NetworkPlayers = 64 },
            KitRowsRaw = [definition.Kits[0] with { Collider = new WorldCollider.Sphere(Radius: 0.5f) }],
        };
    }

    [Fact]
    public void DenseCrowdHonorsBeginAndDegreeBudgetsAndDisableEmitsEnds() {
        var policy = new WorldCollisionEvents(
            BeginBudget: 11,
            CandidateBudget: 8,
            MaxPairsPerBody: 2
        );
        var document = Document(events: policy);
        using var fixture = Fixtures.FreshServer(document);

        Assert.Equal(
            64,
            fixture.Server.Population.SetSimulatedCount(count: 64)
        );
        Coincide(fixture: fixture);
        var feed = new WorldEventFeed(capacity: fixture.Server.Population.Capacity);

        feed.Collect(
            definition: document,
            listeners: WorldEventListeners.Everyone,
            population: fixture.Server.Population
        );
        var begins = feed.Edges.Where(predicate: edge => (edge.Family == WorldEventFamily.CollisionBegin)).ToArray();

        Assert.InRange(
            begins.Length,
            1,
            policy.BeginBudget
        );
        Assert.All(
            begins.SelectMany(selector: edge => new[] { ((int)edge.A), ((int)edge.B) }).GroupBy(keySelector: index => index),
            group => Assert.InRange(
                group.Count(),
                1,
                policy.MaxPairsPerBody
            )
        );

        var disabled = document with {
            CollisionRaw = document.Collision with {
                EventsRaw = new WorldCollisionEvents(
            BeginBudget: 0,
            CandidateBudget: 0,
            MaxPairsPerBody: 0
        ),
            },
        };

        feed.Collect(
            definition: disabled,
            listeners: WorldEventListeners.Everyone,
            population: fixture.Server.Population
        );
        var ends = feed.Edges.Where(predicate: edge => (edge.Family == WorldEventFamily.CollisionEnd)).ToArray();

        Assert.Equal(
            expected: begins.Length,
            actual: ends.Length
        );
        Assert.Equal(
            expected: 0,
            actual: feed.CollisionTrackedPairs
        );
    }
    // Forty solid spheres share one X and stand in a Z column, each touching only its two Z neighbours. Their
    // population order is folded against their height (slot 0 -> body 0, slot 1 -> body 39, slot 2 -> body 1, ...),
    // so a body's touching neighbour sits up to 39 sweep ordinals away, past the default candidate budget of 32.
    // Collision events and body contacts share one broadphase, so both must find every touching pair: an event
    // feed that charged its budget before culling on Y/Z spent it on far bodies and never began those pairs.
    [Fact]
    public void EventsAndContactsAgreeOnAnXAlignedColumnPastTheCandidateBudget() {
        const int Count = 40;
        var source = Fixtures.BuildDocument();
        var definition = source with {
            PopulationRaw = source.Population with {
                CapacityRaw = (source.Population.LocalSeats + Count),
                NetworkPlayers = Count,
            },
            KitRowsRaw = [
                source.Kits[0] with {
                    BodyContact = WorldBodyContactMode.Solid,
                    Collider = new WorldCollider.Sphere(Radius: 0.5f),
                },
            ],
        };
        using var fixture = Fixtures.FreshServer(definition);
        var population = fixture.Server.Population;

        Assert.Equal(expected: Count, actual: population.SetSimulatedCount(count: Count));
        var firstBody = population.LocalSeatCount;

        for (var slot = 0; (slot < Count); slot++) {
            var ordinal = (((slot & 1) == 0) ? (slot / 2) : ((Count - 1) - (slot / 2)));

            fixture.Server.Body(index: (firstBody + ordinal))!.Pose(
                pitchRadians: 0f, rollRadians: 0f, x: 10f, y: 5f, yawRadians: 0f, z: (slot * 0.75f)
            );
        }
        var feed = new WorldEventFeed(capacity: population.Capacity);

        feed.Collect(definition: definition, listeners: WorldEventListeners.Everyone, population: population);
        var begins = feed.Edges.Count(predicate: edge => (edge.Family == WorldEventFamily.CollisionBegin));

        fixture.Step();
        output.WriteLine(message: $"begins={begins} limited={feed.CollisionLimitedBodies} candidates={feed.CollisionCandidates} contactPairs={population.DynamicContactResolvedPairs} contactLimited={population.DynamicContactLimitedBodies}");
        Assert.Equal(expected: 0, actual: feed.CollisionLimitedBodies);
        Assert.Equal(expected: (Count - 1), actual: begins);
        Assert.Equal(expected: 0, actual: population.DynamicContactLimitedBodies);
        Assert.Equal(expected: begins, actual: population.DynamicContactResolvedPairs);
    }
    [Fact]
    public void ValidatorRefusesEveryCollisionEventBoundByName() {
        foreach (var (events, name) in new[] {
            (new WorldCollisionEvents(CandidateBudget: -1), "candidateBudget"),
            (new WorldCollisionEvents(MaxPairsPerBody: -1), "maxPairsPerBody"),
            (new WorldCollisionEvents(BeginBudget: -1), "beginBudget"),
            (new WorldCollisionEvents(
            CandidateBudget: 1,
            MaxPairsPerBody: 2
        ), "must be >= maxPairsPerBody"),
        }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(events: events),
                reason: out var reason
            ));
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: name
            );
        }
    }
}

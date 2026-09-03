using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Acceptance evidence for bounded body-overlap events in dense crowds.</summary>
public sealed class WorldCollisionEventPolicyLawTests {
    private static WorldDefinition Document(WorldCollisionEvents events) {
        var definition = Fixtures.BuildDocument();
        return definition with {
            CollisionRaw = definition.Collision with { EventsRaw = events },
            PopulationRaw = definition.Population with { CapacityRaw = 68, NetworkPlayers = 64 },
            KitRowsRaw = [definition.Kits[0] with { Collider = new WorldCollider.Sphere(Radius: 0.5f) }],
        };
    }

    private static void Coincide(WorldFixture fixture) {
        for (var index = fixture.Server.Population.LocalSeatCount; index < fixture.Server.Population.Capacity; index++) {
            fixture.Server.Body(index)!.Pose(FixedVector3.Zero, FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        }
    }

    [Fact]
    public void DenseCrowdHonorsBeginAndDegreeBudgetsAndDisableEmitsEnds() {
        var policy = new WorldCollisionEvents(CandidateBudget: 8, MaxPairsPerBody: 2, BeginBudget: 11);
        var document = Document(events: policy);
        using var fixture = Fixtures.FreshServer(document);
        Assert.Equal(64, fixture.Server.Population.SetSimulatedCount(count: 64));
        Coincide(fixture);
        var feed = new WorldEventFeed(capacity: fixture.Server.Population.Capacity);

        feed.Collect(definition: document, population: fixture.Server.Population);
        var begins = feed.Edges.Where(edge => edge.Family == WorldEventFamily.CollisionBegin).ToArray();
        Assert.InRange(begins.Length, 1, policy.BeginBudget);
        Assert.All(begins.SelectMany(edge => new[] { (int)edge.A, (int)edge.B }).GroupBy(index => index),
            group => Assert.InRange(group.Count(), 1, policy.MaxPairsPerBody));

        var disabled = document with { CollisionRaw = document.Collision with {
            EventsRaw = new WorldCollisionEvents(CandidateBudget: 0, MaxPairsPerBody: 0, BeginBudget: 0),
        } };
        feed.Collect(definition: disabled, population: fixture.Server.Population);
        var ends = feed.Edges.Where(edge => edge.Family == WorldEventFamily.CollisionEnd).ToArray();
        Assert.Equal(expected: begins.Length, actual: ends.Length);
        Assert.Equal(expected: 0, actual: feed.CollisionTrackedPairs);
    }

    [Fact]
    public void ValidatorRefusesEveryCollisionEventBoundByName() {
        foreach (var (events, name) in new[] {
            (new WorldCollisionEvents(CandidateBudget: -1), "candidateBudget"),
            (new WorldCollisionEvents(MaxPairsPerBody: -1), "maxPairsPerBody"),
            (new WorldCollisionEvents(BeginBudget: -1), "beginBudget"),
            (new WorldCollisionEvents(CandidateBudget: 1, MaxPairsPerBody: 2), "must be >= maxPairsPerBody"),
        }) {
            Assert.False(WorldDefinitionValidator.TryValidateLocally(Document(events), out var reason));
            Assert.Contains(name, reason, StringComparison.Ordinal);
        }
    }
}

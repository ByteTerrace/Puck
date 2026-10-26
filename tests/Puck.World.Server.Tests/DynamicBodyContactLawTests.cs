using Puck.Commands;
using System.Numerics;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Executable law for the local half of cross-seam melee contact: two dynamic bodies are not allowed to
/// occupy one volume, and the stable pair order produces the same authoritative result on every run.</summary>
public sealed class DynamicBodyContactLawTests(ITestOutputHelper output) {
    private static (Vector3 Left, Vector3 Right) ResolveCoincidentPair() {
        using var fixture = TwoBodies(mode: WorldBodyContactMode.Solid);
        var coincident = new Vector3(
            x: 10f,
            y: 5f,
            z: 10f
        );

        fixture.Server.Body(index: 0)!.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: coincident.X,
            y: coincident.Y,
            yawRadians: 0f,
            z: coincident.Z
        );
        fixture.Server.Body(index: 1)!.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: coincident.X,
            y: coincident.Y,
            yawRadians: 0f,
            z: coincident.Z
        );

        fixture.Step();
        return (fixture.Server.Body(index: 0)!.Position, fixture.Server.Body(index: 1)!.Position);
    }
    private static WorldFixture TwoBodies(WorldBodyContactMode mode) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var definition = source with { KitRowsRaw = source.Kits.Select(selector: kit => kit with { BodyContact = mode }).ToArray() };
        var fixture = Fixtures.FreshServer(definition: definition);
        var left = Principal.Seat(slot: 0);
        var right = Principal.Seat(slot: 1);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            left,
            left.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            right,
            right.Index,
            null,
            WorldProtocol.WireProtocolKey
        )).Accepted);
        return fixture;
    }

    [Fact]
    public void CoincidentBodiesSeparateDeterministically() {
        var first = ResolveCoincidentPair();
        var second = ResolveCoincidentPair();

        Assert.Equal(
            actual: second,
            expected: first
        );
        Assert.True(
            (Vector3.Distance(
                value1: first.Left,
                value2: first.Right
            ) >= 0.69f),
            userMessage: $"capsule pair remained overlapped at {first.Left} / {first.Right}"
        );
    }
    [Fact]
    public void OverlapIsTheDefaultAndDoesNotIntroduceCrowdShoving() {
        using var fixture = TwoBodies(mode: WorldBodyContactMode.Overlap);
        var coincident = new Vector3(
            x: 10f,
            y: 5f,
            z: 10f
        );

        fixture.Server.Body(index: 0)!.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: coincident.X,
            y: coincident.Y,
            yawRadians: 0f,
            z: coincident.Z
        );
        fixture.Server.Body(index: 1)!.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: coincident.X,
            y: coincident.Y,
            yawRadians: 0f,
            z: coincident.Z
        );

        fixture.Step();

        Assert.Equal(
            expected: fixture.Server.Body(index: 0)!.Position,
            actual: fixture.Server.Body(index: 1)!.Position
        );
        Assert.Equal(
            expected: 0,
            actual: fixture.Server.Population.DynamicContactPotentialPairs
        );
    }
    [Fact]
    public void PhysicalContactBudgetsAreValidatedIndependentlyOfOverlapEvents() {
        var source = Fixtures.BuildDocument();

        foreach (var (policy, expected) in new[] {
            (new WorldBodyContactPolicy(CandidateBudget: 0), "candidateBudget"),
            (new WorldBodyContactPolicy(MaxPairsPerBody: 0), "maxPairsPerBody"),
            (new WorldBodyContactPolicy(
            CandidateBudget: 2,
            MaxPairsPerBody: 3
        ), "must be >= maxPairsPerBody"),
            (new WorldBodyContactPolicy(CandidateBudget: (WorldBodyContactPolicy.MaximumCandidateBudget + 1)), "candidateBudget"),
        }) {
            var definition = source with {
                CollisionRaw = source.Collision with { BodyContactsRaw = policy },
            };

            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ));
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.Ordinal,
                expectedSubstring: expected
            );
        }
    }
    [Fact]
    public void SolidBodiesUseTheSpatialBroadphaseBeforeNarrowPhase() {
        using var fixture = TwoBodies(mode: WorldBodyContactMode.Solid);

        fixture.Server.Body(index: 0)!.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: -20f,
            y: 5f,
            yawRadians: 0f,
            z: 10f
        );
        fixture.Server.Body(index: 1)!.Pose(
            pitchRadians: 0f,
            rollRadians: 0f,
            x: 20f,
            y: 5f,
            yawRadians: 0f,
            z: 10f
        );

        fixture.Step();

        Assert.Equal(
            expected: 1,
            actual: fixture.Server.Population.DynamicContactPotentialPairs
        );
        Assert.Equal(
            expected: 0,
            actual: fixture.Server.Population.DynamicContactNarrowPairs
        );
        Assert.Equal(
            expected: 0,
            actual: fixture.Server.Population.DynamicContactResolvedPairs
        );
    }
    [Fact]
    public void ThirtyTwoBodiesAlignedOnOneXIntervalAllCollideWithTheirNeighbours() {
        const int Count = 32;
        var source = Fixtures.BuildDocument();
        var definition = source with {
            PopulationRaw = source.Population with {
                CapacityRaw = (source.Population.LocalSeats + Count),
                NetworkPlayers = Count,
            },
            CollisionRaw = source.Collision with {
                BodyContactsRaw = new WorldBodyContactPolicy(
                    CandidateBudget: 4,
                    MaxPairsPerBody: 2
                ),
            },
            KitRowsRaw = [
                source.Kits[0] with {
                    BodyContact = WorldBodyContactMode.Solid,
                    Collider = new WorldCollider.Sphere(Radius: 0.5f),
                },
            ],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var population = fixture.Server.Population;

        Assert.Equal(
            actual: population.SetSimulatedCount(count: Count),
            expected: Count
        );

        var firstBody = population.LocalSeatCount;
        const float SpacingZ = 0.75f;

        // Fold Z order across body indices (slot 0 -> body 0, slot 1 -> body 31, slot 2 -> body 1, ...) so early
        // bodies in X-sweep order must scan past up to 30 non-overlapping bodies sharing the same X interval to
        // reach their immediate Z neighbours.
        for (var slot = 0; (slot < Count); slot++) {
            var ordinal = (((slot & 1) == 0)
                ? (slot / 2)
                : ((Count - 1) - (slot / 2)));

            fixture.Server.Body(index: (firstBody + ordinal))!.Pose(
                pitchRadians: 0f,
                rollRadians: 0f,
                x: 10f,
                y: 5f,
                yawRadians: 0f,
                z: (slot * SpacingZ)
            );
        }

        fixture.Step();

        Assert.Equal(
            actual: population.DynamicContactLimitedBodies,
            expected: 0
        );
        Assert.Equal(
            actual: population.DynamicContactResolvedPairs,
            expected: (Count - 1)
        );
        Assert.InRange(
            actual: population.DynamicContactCandidates,
            low: (Count - 1),
            high: (Count * 4)
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void XAlignedDisjointBodiesPrunePairVisitsOnBothOtherAxes(bool separateY) {
        const int Count = 4096;
        var source = Fixtures.BuildDocument();
        var definition = source with {
            PopulationRaw = source.Population with {
                CapacityRaw = Count,
                NetworkPlayers = (Count - source.Population.LocalSeats),
            },
            KitRowsRaw = [
                source.Kits[0] with {
                    BodyContact = WorldBodyContactMode.Solid,
                    Collider = new WorldCollider.Sphere(Radius: 0.5f),
                },
            ],
        };
        using var fixture = Fixtures.FreshServer(definition: definition);
        var population = fixture.Server.Population;

        Assert.Equal((Count - population.LocalSeatCount), population.SetSimulatedCount(count: Count));
        for (var index = 0; (index < population.LocalSeatCount); index++) {
            fixture.JoinSeat(slot: index);
        }
        for (var index = 0; (index < Count); index++) {
            // Negative and positive cells exercise floor division across the origin. Reverse spatial order to
            // ensure pruning does not depend on the body's population ordinal matching its coordinate order.
            var offset = (((Count / 2) - index) * 3f);

            fixture.Server.Body(index: index)!.Pose(
                pitchRadians: 0f, rollRadians: 0f, x: 0f,
                y: (separateY ? offset : 5f), yawRadians: 0f, z: (separateY ? 0f : offset)
            );
        }

        fixture.Step();

        output.WriteLine(message: $"{Count} X-aligned {(separateY ? "Y" : "Z")}-disjoint bodies: {population.DynamicContactPairVisits} visits / {population.DynamicContactPotentialPairs} potential pairs");
        Assert.Equal(((((long)Count) * (Count - 1)) / 2), population.DynamicContactPotentialPairs);
        Assert.InRange(population.DynamicContactPairVisits, 1, (Count * 4L));
        Assert.Equal(0, population.DynamicContactCandidates);
        Assert.Equal(0, population.DynamicContactLimitedBodies);
        Assert.Equal(0, population.DynamicContactResolvedPairs);
    }
}

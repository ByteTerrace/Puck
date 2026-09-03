using System.Numerics;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Executable law for the local half of cross-seam melee contact: two dynamic bodies are not allowed to
/// occupy one volume, and the stable pair order produces the same authoritative result on every run.</summary>
public sealed class DynamicBodyContactLawTests {
    [Fact]
    public void CoincidentBodiesSeparateDeterministically() {
        var first = ResolveCoincidentPair();
        var second = ResolveCoincidentPair();

        Assert.Equal(actual: second, expected: first);
        Assert.True((Vector3.Distance(value1: first.Left, value2: first.Right) >= 0.69f),
            userMessage: $"capsule pair remained overlapped at {first.Left} / {first.Right}");
    }
    [Fact]
    public void OverlapIsTheDefaultAndDoesNotIntroduceCrowdShoving() {
        using var fixture = TwoBodies(mode: WorldBodyContactMode.Overlap);
        var coincident = new Vector3(x: 10f, y: 5f, z: 10f);

        fixture.Server.Body(index: 0)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: coincident.X, y: coincident.Y, yawRadians: 0f, z: coincident.Z);
        fixture.Server.Body(index: 1)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: coincident.X, y: coincident.Y, yawRadians: 0f, z: coincident.Z);

        fixture.Step();

        Assert.Equal(expected: fixture.Server.Body(index: 0)!.Position, actual: fixture.Server.Body(index: 1)!.Position);
        Assert.Equal(expected: 0, actual: fixture.Server.Population.DynamicContactPotentialPairs);
    }
    [Fact]
    public void SolidBodiesUseTheSpatialBroadphaseBeforeNarrowPhase() {
        using var fixture = TwoBodies(mode: WorldBodyContactMode.Solid);

        fixture.Server.Body(index: 0)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: -20f, y: 5f, yawRadians: 0f, z: 10f);
        fixture.Server.Body(index: 1)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: 20f, y: 5f, yawRadians: 0f, z: 10f);

        fixture.Step();

        Assert.Equal(expected: 1, actual: fixture.Server.Population.DynamicContactPotentialPairs);
        Assert.Equal(expected: 0, actual: fixture.Server.Population.DynamicContactNarrowPairs);
        Assert.Equal(expected: 0, actual: fixture.Server.Population.DynamicContactResolvedPairs);
    }
    [Fact]
    public void PhysicalContactBudgetsAreValidatedIndependentlyOfOverlapEvents() {
        var source = Fixtures.BuildDocument();
        foreach (var (policy, expected) in new[] {
            (new WorldBodyContactPolicy(CandidateBudget: 0), "candidateBudget"),
            (new WorldBodyContactPolicy(MaxPairsPerBody: 0), "maxPairsPerBody"),
            (new WorldBodyContactPolicy(CandidateBudget: 2, MaxPairsPerBody: 3), "must be >= maxPairsPerBody"),
            (new WorldBodyContactPolicy(CandidateBudget: WorldBodyContactPolicy.MaximumCandidateBudget + 1), "candidateBudget"),
        }) {
            var definition = source with {
                CollisionRaw = source.Collision with { BodyContactsRaw = policy },
            };
            Assert.False(WorldDefinitionValidator.TryValidateLocally(definition, out var reason));
            Assert.Contains(expected, reason, StringComparison.Ordinal);
        }
    }

    private static (Vector3 Left, Vector3 Right) ResolveCoincidentPair() {
        using var fixture = TwoBodies(mode: WorldBodyContactMode.Solid);
        var coincident = new Vector3(x: 10f, y: 5f, z: 10f);

        fixture.Server.Body(index: 0)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: coincident.X, y: coincident.Y, yawRadians: 0f, z: coincident.Z);
        fixture.Server.Body(index: 1)!.Pose(pitchRadians: 0f, rollRadians: 0f, x: coincident.X, y: coincident.Y, yawRadians: 0f, z: coincident.Z);

        fixture.Step();
        return (fixture.Server.Body(index: 0)!.Position, fixture.Server.Body(index: 1)!.Position);
    }
    private static WorldFixture TwoBodies(WorldBodyContactMode mode) {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var definition = source with { KitRowsRaw = source.Kits.Select(selector: kit => kit with { BodyContact = mode }).ToArray() };
        var fixture = Fixtures.FreshServer(definition: definition);
        var left = WorldPrincipal.Seat(slot: 0);
        var right = WorldPrincipal.Seat(slot: 1);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(left, left.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(right, right.Index, null, WorldProtocol.WireProtocolKey)).Accepted);
        return fixture;
    }
}

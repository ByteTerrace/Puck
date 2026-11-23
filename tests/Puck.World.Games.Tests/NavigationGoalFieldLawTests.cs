using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Games.Tests;

public sealed partial class NavigationLawTests {
    // A shared volume domain of 20 by 10 by 6 one-metre cells, and a crowd of peers steering through it.
    private static WorldDefinition GoalFieldDocument(int bodies) {
        var document = NavigationDocument(domain: VolumeDomain() with { Width = 20, Depth = 10, Layers = 6, MaxExpandedNodes = 1200, MaxPathNodes = 1024, Shared = true });

        return document with {
            PopulationRaw = new WorldBodiesDefaults(
                LocalSeatsRaw: 0,
                CapacityRaw: bodies,
                NetworkPlayers: bodies,
                DefaultPeerSourceRaw: IntentSource.Producer(name: ProducerName)
            ),
        };
    }
    // The goal a body heads to: one of 200 cell centres spread through the domain.
    private static FixedVector3 GoalFieldGoal(int body) {
        var goal = (body % 200);

        return Fixtures.FixedPoint(x: (goal % 20), y: ((goal / 20) % 6), z: ((goal / 120) * 9));
    }
    private static WorldFixture GoalFieldCrowd(int bodies) {
        var fixture = Fixtures.FreshServer(GoalFieldDocument(bodies: bodies));

        Assert.Equal(expected: bodies, actual: fixture.Server.Population.SetSimulatedCount(bodies));
        for (var body = 0; (body < bodies); body++) {
            fixture.Server.Body(index: body)!.Pose(Fixtures.FixedPoint(x: ((body * 7) % 20), y: ((body * 3) % 6), z: ((body * 11) % 10)), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
            Assert.True(condition: fixture.Server.ApplyDesignation(new WorldDesignation(body, RegisterName, default, GoalFieldGoal(body: body)), Principal.Console));
        }

        return fixture;
    }

    [Theory]
    [InlineData(128)]
    [InlineData(2_000)]
    public void SharedFollowersDeriveOneFieldPerGoalAndThenOnlyReadSteps(int bodies) {
        using var fixture = GoalFieldCrowd(bodies: bodies);
        var ticks = new (int Builds, long Cells, long Steps, int Resident)[4];

        for (var tick = 0; (tick < ticks.Length); tick++) {
            fixture.Step();
            ticks[tick] = fixture.Server.Population.NavigationGoalFieldWork;
        }

        var goals = Math.Min(val1: bodies, val2: 200);

        TestContext.Current.TestOutputHelper!.WriteLine(message: $"bodies={bodies} goals={goals}: {string.Join(separator: "; ", values: ticks.Select(selector: static (work, tick) => $"tick{tick} builds={work.Builds} cells={work.Cells} steps={work.Steps} resident={work.Resident}"))}");
        Assert.Equal(expected: goals, actual: ticks[0].Builds);
        for (var tick = 1; (tick < ticks.Length); tick++) {
            Assert.Equal(expected: 0, actual: ticks[tick].Builds);
            Assert.Equal(expected: goals, actual: ticks[tick].Resident);
            Assert.InRange(actual: ticks[tick].Steps, low: bodies, high: (8L * bodies));
        }
        Assert.Equal(expected: 1, actual: fixture.Server.Population.NavigationFact(facet: "hasPath", index: (bodies - 1)));
    }

    [Fact]
    public void AFollowerRestoredWithNoResidentFieldMovesAsOneThatKeptItsFields() {
        using var fixture = GoalFieldCrowd(bodies: 64);

        for (var tick = 0; (tick < 5); tick++) { fixture.Step(); }
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var captured, hostRow: WorldAuthorityHostRowCheckpoint.Empty, reason: out var reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!), checkpoint: out var decoded, reason: out reason), userMessage: reason);

        using var cold = Fixtures.FreshServer(GoalFieldDocument(bodies: 64));

        cold.Server.RestoreCheckpoint(checkpoint: decoded!);
        Assert.Equal(expected: 0, actual: cold.Server.Population.NavigationGoalFieldWork.Resident);
        for (var tick = 0; (tick < 60); tick++) {
            fixture.Step();
            cold.Step();
            Assert.Equal(
                expected: WorldStateHashComposition.HashAuthoritative(server: fixture.Server, tick: ((ulong)tick)),
                actual: WorldStateHashComposition.HashAuthoritative(server: cold.Server, tick: ((ulong)tick))
            );
        }
    }
}

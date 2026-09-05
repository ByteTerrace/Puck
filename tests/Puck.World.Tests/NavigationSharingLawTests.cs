using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class NavigationLawTests {
    private static WorldDefinition SharedNavigationDocument(int goals = 2, int budget = 3, bool medium = false) {
        var domain = VolumeDomain(kind: medium ? WorldNavigationKind.Medium : WorldNavigationKind.Volume,
            medium: medium ? "water" : null) with { Shared = new(goals, budget) };
        return NavigationDocument(domain, withMedium: medium);
    }

    private static FixedVector3 SharedGoal(int x = 4, int y = 4, int z = 0) =>
        new(FixedQ4816.FromInteger(x), FixedQ4816.FromInteger(y), FixedQ4816.FromInteger(z));

    private static WorldNavigationTreeCheckpoint[] SharedTrees(WorldFixture fixture) =>
        fixture.Server.Population.Capture().SharedNavigation![0].Trees;

    [Fact]
    public void SharedNavigationAlsoRunsForDensePeerFlocksWithoutLocalSeats() {
        var document = SharedNavigationDocument(budget: 8, medium: true);
        var kit = document.Kits[0];
        document = document with {
            PopulationRaw = new WorldBodiesDefaults(LocalSeatsRaw: 0, CapacityRaw: 128, NetworkPlayers: 128, DefaultPeerSourceRaw: IntentSource.Producer(ProducerName)),
            BodyMotionProgramsRaw = document.BodyMotionPrograms.Select(program => program.Name == ProducerName ? program with {
                Operations = [Puck.Physics.Motion.BodyMotionOp.SenseNearestInCone, Puck.Physics.Motion.BodyMotionOp.ProduceFlockIntent],
            } : program).ToArray(),
            KitRowsRaw = [kit with { ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                [ProducerName] = new(new Dictionary<string, float>(), new Dictionary<string, string>(),
                    new WorldFlockProfile(10, 1, 16, 8, .1f, WorldFlockSpace.Volume, 0, 0, 0, 1, 0, .1f, 180, false, DomainName)),
            } }],
        };
        using var fixture = Fixtures.FreshServer(document);
        Assert.Equal(128, fixture.Server.Population.SetSimulatedCount(128));
        for (var index = 0; index < 128; index++) {
            Assert.True(fixture.Server.ApplyDesignation(new WorldDesignation(index, RegisterName, default, SharedGoal()), WorldPrincipal.Console));
        }
        for (var tick = 0; tick < 120; tick++) {
            fixture.Step();
            Assert.InRange(fixture.Server.Population.NavigationWork().LastExpanded, 0, 8);
        }
        Assert.Equal(1, fixture.Server.Population.NavigationFact(4, "hasPath"));
        Assert.True(fixture.Server.Body(4)!.FixedPosition.Y > FixedQ4816.Zero, fixture.Server.Population.DescribeTargets(4));
    }

    [Fact]
    public void SharedNavigationCoalescesSameGoalWorkWithoutSharingBodyCursors() {
        long Run(int count) {
            using var fixture = Fixtures.FreshServer(SharedNavigationDocument(goals: 1));
            var bodies = new WorldBody[count];
            for (var slot = 0; slot < count; slot++) {
                bodies[slot] = JoinNavigator(fixture, SharedGoal(), slot);
                bodies[slot].Pose(FixedVector3.Zero, FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
            }
            fixture.Step();
            for (var slot = 0; slot < count; slot++) { Assert.Equal(1, fixture.Server.Population.NavigationFact(slot, "pending")); }
            long expansions = 0;
            for (var tick = 0; tick < 120; tick++) {
                fixture.Step();
                var work = fixture.Server.Population.NavigationWork();
                Assert.Equal(3, work.WorstExpanded);
                Assert.InRange(work.LastExpanded, 0, 3);
                expansions += work.LastExpanded;
                for (var slot = 1; slot < count; slot++) { Assert.Equal(bodies[0].FixedPosition, bodies[slot].FixedPosition); }
            }
            Assert.InRange(expansions, 1, 108);
            Assert.True(bodies[0].FixedPosition.Y > FixedQ4816.Zero);
            Assert.Single(SharedTrees(fixture), tree => tree.Goal >= 0);
            if (count > 1) {
                var before = fixture.Server.Population.Capture().Entries.Single(entry => entry.Index == 1).Navigation!.Value;
                bodies[0].SetIntentSource(IntentSource.Idle);
                bodies[0].Pose(SharedGoal(2, 0, 2), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
                fixture.Step();
                Assert.Equal(0, fixture.Server.Population.NavigationFact(0, "hasPath"));
                var after = fixture.Server.Population.Capture().Entries.Single(entry => entry.Index == 1).Navigation!.Value;
                Assert.Equal(before.Path, after.Path);
                Assert.True(after.Waypoint >= before.Waypoint);
            }
            return expansions;
        }
        Assert.Equal(Run(1), Run(4));
    }

    [Fact]
    public void SharedNavigationRoundRobinsGoalsAndReportsCapacityWithoutAnUnbudgetedSearch() {
        foreach (var capacity in new[] { 1, 2 }) {
            using var fixture = Fixtures.FreshServer(SharedNavigationDocument(capacity, budget: 1));
            _ = JoinNavigator(fixture, SharedGoal());
            _ = JoinNavigator(fixture, SharedGoal(4, 4, 2), slot: 1);
            fixture.Step();
            Assert.Equal(capacity == 1 ? 1 : 0, fixture.Server.Population.NavigationFact(1, "capacity"));
            for (var tick = 0; tick < 8; tick++) {
                fixture.Step();
                Assert.InRange(fixture.Server.Population.NavigationWork().LastExpanded, 0, 1);
                if (capacity == 2) {
                    var trees = SharedTrees(fixture);
                    Assert.InRange(Math.Abs(trees[0].Nodes.Count(node => node.Settled) - trees[1].Nodes.Count(node => node.Settled)), 0, 1);
                }
            }
            // A completed request releases its pending cache pin. Both destinations eventually get service even
            // with only one resident tree, and the first body's copied route survives eviction of that tree.
            var served = new bool[2];
            for (var tick = 0; tick < 250; tick++) {
                fixture.Step();
                for (var body = 0; body < served.Length; body++) { served[body] |= fixture.Server.Population.NavigationFact(body, "hasPath") == 1; }
            }
            Assert.All(served, value => Assert.True(value));
        }
    }

    [Fact]
    public void SharedNavigationPinsACompletedAnswerUntilItsRequesterCanReadIt() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(goals: 1, budget: 108));
        var first = JoinNavigator(fixture, SharedGoal(5, 4, 2));
        first.SetIntentSource(IntentSource.Idle);
        _ = JoinNavigator(fixture, SharedGoal(), slot: 1);
        fixture.Step();
        Assert.Equal(1, fixture.Server.Population.NavigationFact(1, "pending"));
        first.SetIntentSource(IntentSource.Producer(ProducerName));
        fixture.Step();
        Assert.Equal(1, fixture.Server.Population.NavigationFact(0, "capacity"));
        Assert.Equal(1, fixture.Server.Population.NavigationFact(1, "hasPath"));
        fixture.Step(); fixture.Step();
        Assert.Equal(1, fixture.Server.Population.NavigationFact(0, "hasPath"));
    }

    [Fact]
    public void SharedNavigationEvictsTheOldestGoalAfterRepeatedTouchesOfTheNewest() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(goals: 3, budget: 324));
        var first = JoinNavigator(fixture, SharedGoal());
        _ = JoinNavigator(fixture, SharedGoal(4, 4, 1), slot: 1);
        _ = JoinNavigator(fixture, SharedGoal(4, 4, 2), slot: 2);
        fixture.Step(); fixture.Step();
        Assert.Equal(3, SharedTrees(fixture).Count(tree => tree.Goal >= 0));
        for (var touch = 0; touch < 5; touch++) {
            first.SetIntentSource(IntentSource.Idle);
            fixture.Step();
            first.SetIntentSource(IntentSource.Producer(ProducerName));
            fixture.Step();
        }
        var newest = SharedTrees(fixture).Single(tree => tree.Goal == 76);
        Assert.Equal(0, newest.Age);
        Assert.True(fixture.Server.ApplyDesignation(new(0, RegisterName, default, SharedGoal(5, 4, 2)), WorldPrincipal.Seat(0)));
        fixture.Step();
        var trees = SharedTrees(fixture);
        Assert.DoesNotContain(trees, tree => tree.Goal == 82);
        Assert.Contains(trees, tree => tree.Goal == 88);
        Assert.Equal(new[] { 0, 1, 2 }, trees.Select(tree => tree.Age).Order().ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedNavigationResumesAnUnfinishedSearchThroughCheckpointCodec(bool medium) {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 2, medium: medium));
        _ = JoinNavigator(fixture, SharedGoal());
        _ = JoinNavigator(fixture, SharedGoal(4, 3, 2), slot: 1);
        for (var tick = 0; tick < 7; tick++) { fixture.Step(); }
        Assert.Equal(1, fixture.Server.Population.NavigationFact(0, "pending"));
        Assert.True(fixture.Server.TryCaptureCheckpoint(checkpoint: out var captured, hostRow: EmptyHostRow(), reason: out var reason), reason);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(captured!), out var decoded, out reason), reason);
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 7);
        var expected = new ulong[130];
        for (var tick = 0; tick < expected.Length; tick++) {
            fixture.Step();
            expected[tick] = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, (ulong)tick);
        }
        fixture.Server.RestoreCheckpoint(decoded!);
        Assert.Equal(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 7));
        for (var tick = 0; tick < expected.Length; tick++) {
            fixture.Step();
            Assert.Equal(expected[tick], WorldRuntimeStateHash.HashAuthoritative(fixture.Server, (ulong)tick));
        }
    }

    [Fact]
    public void SharedNavigationCanDetachRetargetAndRejoinWithoutInvalidatingAnotherCreature() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 108));
        var first = JoinNavigator(fixture, SharedGoal());
        _ = JoinNavigator(fixture, SharedGoal(), slot: 1);
        fixture.Step(); fixture.Step();
        var initial = fixture.Server.Population.Capture().Entries.Single(entry => entry.Index == 1).Navigation!.Value;
        Assert.True(fixture.Server.ApplyDesignation(new WorldDesignation(0, RegisterName, default, SharedGoal(5, 0, 2)), WorldPrincipal.Seat(0)));
        fixture.Step(); fixture.Step();
        Assert.Equal(initial.GoalCell, fixture.Server.Population.Capture().Entries.Single(entry => entry.Index == 1).Navigation!.Value.GoalCell);
        Assert.Equal(2, SharedTrees(fixture).Count(tree => tree.Goal >= 0));
        Assert.True(fixture.Server.ApplyDesignation(new WorldDesignation(0, RegisterName, default, SharedGoal()), WorldPrincipal.Seat(0)));
        first.Pose(SharedGoal(3, 2, 1), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        fixture.Step(); fixture.Step();
        Assert.Equal(1, fixture.Server.Population.NavigationFact(0, "hasPath"));
        Assert.Equal(initial.GoalCell, fixture.Server.Population.Capture().Entries.Single(entry => entry.Index == 0).Navigation!.Value.GoalCell);
    }

    [Fact]
    public void SharedNavigationRejectsMalformedSuccessorsBeforeChangingAnyLiveState() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 3));
        _ = JoinNavigator(fixture, SharedGoal());
        fixture.Step(); fixture.Step();
        Assert.True(fixture.Server.TryCaptureCheckpoint(checkpoint: out var captured, hostRow: EmptyHostRow(), reason: out var reason), reason);
        var shared = captured!.Population.SharedNavigation!;
        var treeSlot = Array.FindIndex(shared[0].Trees, tree => tree.Goal >= 0);
        var tree = shared[0].Trees[treeSlot];
        var malformedTree = tree with { Nodes = tree.Nodes.Select(node => node.Node == tree.Goal ? node : node with { Next = node.Node }).ToArray() };
        var malformedDomain = shared[0] with { Trees = shared[0].Trees.Select((row, index) => index == treeSlot ? malformedTree : row).ToArray() };
        var malformed = captured with { Population = captured.Population with { SharedNavigation = [malformedDomain] } };
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 2);
        Assert.Contains("successor", Assert.Throws<InvalidOperationException>(() => fixture.Server.RestoreCheckpoint(malformed)).Message);
        Assert.Equal(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 2));
    }

    [Fact]
    public void SharedNavigationCanonicalizesTreesInvalidatedByWaterBeforeCheckpointing() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 2, medium: true));
        _ = JoinNavigator(fixture, SharedGoal());
        fixture.Step(); fixture.Step();
        var fields = fixture.Server.Population.Fields!;
        fields.Restore(new WorldFieldLattice.WorldFieldCheckpoint([new long[fields.CellCount]]));
        Assert.All(SharedTrees(fixture), tree => Assert.Equal(-1, tree.Goal));
        Assert.True(fixture.Server.TryCaptureCheckpoint(checkpoint: out var captured, hostRow: EmptyHostRow(), reason: out var reason), reason);
        var before = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 2);
        fixture.Step();
        var after = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 3);
        fixture.Server.RestoreCheckpoint(captured!);
        Assert.Equal(before, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 2));
        fixture.Step();
        Assert.Equal(after, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 3));
        Assert.Equal(1, fixture.Server.Population.NavigationFact(0, "unreachable"));
    }

    [Fact]
    public void SharedNavigationAuthoringBoundsMemoryAndTotalWork() {
        var document = SharedNavigationDocument();
        var domain = document.Navigation.Rows[0];
        foreach (var shared in new[] { new WorldNavigationSharing(0, 2), new(17, 2), new(1, 0), new(1, 65_537) }) {
            Assert.False(WorldDefinitionValidator.TryValidateLocally(document with { NavigationRaw = new([domain with { Shared = shared }]) }, out _));
        }
        var large = domain with { Width = 256, Depth = 256, Layers = 1, Shared = new(16, 40_000) };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(document with { NavigationRaw = new([large, large with { Name = "other" }]) }, out var reason));
        Assert.Contains("shared trees require", reason);
        Assert.Contains("expansions per tick", reason);
    }

    [Fact]
    public void SharedNavigationDoesNotRestartForAnUnrelatedFieldWrite() {
        var document = SharedNavigationDocument(budget: 3, medium: true);
        document = document with { StateRaw = document.StateRaw! with { World = [.. document.State,
            new WorldStateRow(WorldCellName.Parse("heat"), CellKind.Fixed,
                Domain: new WorldStateDomain.CellsOf("water-space"), Field: new WorldStateFieldTrait(Initial: 0, Min: 0, Max: 1))] } };
        using var fixture = Fixtures.FreshServer(document);
        _ = JoinNavigator(fixture, SharedGoal());
        fixture.Step(); fixture.Step(); fixture.Step();
        var before = SharedTrees(fixture).Sum(tree => tree.Nodes.Count(node => node.Settled));
        Assert.Equal(6, before);
        Assert.Equal(1, fixture.Server.Population.Fields!.PaintSphere("heat", 0, 0, 0, 0, WorldFieldWriteOp.Set, FixedQ4816.One));
        fixture.Step();
        Assert.Equal(before + 3, SharedTrees(fixture).Sum(tree => tree.Nodes.Count(node => node.Settled)));
    }

    [Fact]
    public void SharedNavigationSettledCostsMatchTheUnobstructedGridOracle() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 108));
        _ = JoinNavigator(fixture, SharedGoal());
        fixture.Step(); fixture.Step();
        var tree = Assert.Single(SharedTrees(fixture), row => row.Goal >= 0);
        foreach (var node in tree.Nodes.Where(node => node.Settled)) {
            // Independent closed-form shortest cost in an unobstructed 26-neighbor grid.
            var distances = new[] { Math.Abs(node.Node % 6 - 4), Math.Abs(node.Node / 18 - 4), node.Node / 6 % 3 };
            Array.Sort(distances);
            var expected = distances[0] * 1732 + (distances[1] - distances[0]) * 1414 + (distances[2] - distances[1]) * 1000;
            Assert.Equal(expected, node.Cost);
        }
    }

    [Fact]
    public void SharedSurfaceNavigationUsesTheSweptClearanceDetour() {
        using var fixture = Fixtures.FreshServer(WithFloor(NavigationDocument(SurfaceDomain() with { Shared = new(1, 16) }), withBarrier: true));
        _ = JoinNavigator(fixture, SharedGoal(3, 0, 0));
        fixture.Step(); fixture.Step();
        var route = fixture.Server.Population.Capture().Entries.Single(entry => entry.Index == 0).Navigation!.Value;
        Assert.True(route.Path.Length > 4, string.Join(',', route.Path));
    }

    [Fact]
    public void NavigationRefusesFullWidthOutOfDomainPositionsWithoutNarrowingOverflow() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument());
        var body = JoinNavigator(fixture, SharedGoal());
        body.Pose(new(FixedQ4816.MaxValue, FixedQ4816.MinValue, FixedQ4816.MaxValue), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        fixture.Step();
        Assert.Equal(1, fixture.Server.Population.NavigationFact(0, "unreachable"));
    }
}

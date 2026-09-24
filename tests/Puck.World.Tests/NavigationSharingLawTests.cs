using Puck.Commands;
using Puck.Maths;
using Puck.Physics.Fields;
using Puck.Physics.Navigation;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class NavigationLawTests {
    private static WorldDefinition SharedNavigationDocument(int goals = 2, int budget = 3, bool medium = false) {
        var domain = VolumeDomain(
            kind: (medium
            ? WorldNavigationKind.Medium
            : WorldNavigationKind.Volume),
            medium: (medium
            ? "water"
            : null)
        ) with {
            Shared = new(
            ExpandedNodesPerTick: budget,
            GoalCapacity: goals
        ),
        };

        return NavigationDocument(
            domain,
            withMedium: medium
        );
    }
    private static FixedVector3 SharedGoal(int x = 4, int y = 4, int z = 0) => Fixtures.FixedPoint(x: x, y: y, z: z);
    private static NavigationTreeCheckpoint[] SharedTrees(WorldFixture fixture) =>
        fixture.Server.Population.Capture().SharedNavigation![0].Trees;

    [Fact]
    public void SharedNavigationAlsoRunsForDensePeerFlocksWithoutLocalSeats() {
        var document = SharedNavigationDocument(
            budget: 8,
            medium: true
        );
        var kit = document.Kits[0];

        document = document with {
            PopulationRaw = new WorldBodiesDefaults(
            LocalSeatsRaw: 0,
            CapacityRaw: 128,
            NetworkPlayers: 128,
            DefaultPeerSourceRaw: IntentSource.Producer(name: ProducerName)
        ),
            BodyMotionProgramsRaw = document.BodyMotionPrograms.Select(selector: program => ((program.Name == ProducerName)
            ? program with {
                Operations = [Puck.Physics.Motion.BodyMotionOp.SenseNearestInCone, Puck.Physics.Motion.BodyMotionOp.ProduceFlockIntent],
            }
            : program)).ToArray(),
            KitRowsRaw = [kit with { ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                [ProducerName] = new(
                new Dictionary<string, float>(),
                new Dictionary<string, string>(),
                new WorldFlockProfile(
                    10,
                    1,
                    16,
                    8,
                    .1f,
                    WorldFlockSpace.Volume,
                    0,
                    0,
                    0,
                    1,
                    0,
                    .1f,
                    180,
                    false,
                    DomainName
                )
            ),
            } }],
        };
        using var fixture = Fixtures.FreshServer(document);

        Assert.Equal(
            128,
            fixture.Server.Population.SetSimulatedCount(128)
        );
        for (var index = 0; (index < 128); index++) {
            Assert.True(condition: fixture.Server.ApplyDesignation(
                new WorldDesignation(
                    index,
                    RegisterName,
                    default,
                    SharedGoal()
                ),
                Principal.Console
            ));
        }
        for (var tick = 0; (tick < 120); tick++) {
            fixture.Step();
            Assert.InRange(
                fixture.Server.Population.NavigationWork().LastExpanded,
                0,
                8
            );
        }
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "hasPath",
                index: 4
            )
        );
        Assert.True(
            condition: (fixture.Server.Body(index: 4)!.FixedPosition.Y > FixedQ4816.Zero),
            userMessage: fixture.Server.Population.DescribeTargets(bodyIndex: 4)
        );
    }
    [Fact]
    public void SharedNavigationCoalescesSameGoalWorkWithoutSharingBodyCursors() {
        long Run(int count) {
            using var fixture = Fixtures.FreshServer(SharedNavigationDocument(goals: 1));
            var bodies = new WorldBody[count];

            for (var slot = 0; (slot < count); slot++) {
                bodies[slot] = JoinNavigator(
                    fixture: fixture,
                    goal: SharedGoal(),
                    slot: slot
                );
                bodies[slot].Pose(
                    FixedVector3.Zero,
                    FixedQ4816.Zero,
                    FixedQ4816.Zero,
                    FixedQ4816.Zero
                );
            }
            fixture.Step();
            for (var slot = 0; (slot < count); slot++) {
                Assert.Equal(
                1,
                fixture.Server.Population.NavigationFact(
                    facet: "pending",
                    index: slot
                )
            );
            }
            var expansions = 0L;

            for (var tick = 0; (tick < 120); tick++) {
                fixture.Step();
                var work = fixture.Server.Population.NavigationWork();

                Assert.Equal(
                    actual: work.WorstExpanded,
                    expected: 3
                );
                Assert.InRange(
                    actual: work.LastExpanded,
                    high: 3,
                    low: 0
                );
                expansions += work.LastExpanded;
                for (var slot = 1; (slot < count); slot++) {
                    Assert.Equal(
                    bodies[0].FixedPosition,
                    bodies[slot].FixedPosition
                );
                }
            }
            Assert.InRange(
                actual: expansions,
                high: 108,
                low: 1
            );
            Assert.True(condition: (bodies[0].FixedPosition.Y > FixedQ4816.Zero));
            Assert.Single(
                collection: SharedTrees(fixture: fixture),
                predicate: tree => (tree.Goal >= 0)
            );
            if (count > 1) {
                var before = fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 1)).Navigation!.Value;

                bodies[0].SetIntentSource(source: IntentSource.Idle);
                bodies[0].Pose(
                    SharedGoal(
                        x: 2,
                        y: 0,
                        z: 2
                    ),
                    FixedQ4816.Zero,
                    FixedQ4816.Zero,
                    FixedQ4816.Zero
                );
                fixture.Step();
                Assert.Equal(
                    0,
                    fixture.Server.Population.NavigationFact(
                        facet: "hasPath",
                        index: 0
                    )
                );
                var after = fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 1)).Navigation!.Value;

                Assert.Equal(
                    before.Path,
                    after.Path
                );
                Assert.True(condition: (after.Waypoint >= before.Waypoint));
            }
            return expansions;
        }
        Assert.Equal(
            Run(count: 1),
            Run(count: 4)
        );
    }
    [Fact]
    public void SharedNavigationRoundRobinsGoalsAndReportsCapacityWithoutAnUnbudgetedSearch() {
        foreach (var capacity in new[] { 1, 2 }) {
            using var fixture = Fixtures.FreshServer(SharedNavigationDocument(
                capacity,
                budget: 1
            ));

            _ = JoinNavigator(
                fixture,
                SharedGoal()
            );
            _ = JoinNavigator(
                fixture,
                SharedGoal(
                    x: 4,
                    y: 4,
                    z: 2
                ),
                slot: 1
            );
            fixture.Step();
            Assert.Equal(
                ((capacity == 1)
                ? 1
                : 0),
                fixture.Server.Population.NavigationFact(
                    facet: "capacity",
                    index: 1
                )
            );
            for (var tick = 0; (tick < 8); tick++) {
                fixture.Step();
                Assert.InRange(
                    fixture.Server.Population.NavigationWork().LastExpanded,
                    0,
                    1
                );
                if (capacity == 2) {
                    var trees = SharedTrees(fixture: fixture);

                    Assert.InRange(
                        Math.Abs(value: (trees[0].Nodes.Count(predicate: node => node.Settled) - trees[1].Nodes.Count(predicate: node => node.Settled))),
                        0,
                        1
                    );
                }
            }
            // A completed request releases its pending cache pin. Both destinations eventually get service even
            // with only one resident tree, and the first body's copied route survives eviction of that tree.
            var served = new bool[2];

            for (var tick = 0; (tick < 250); tick++) {
                fixture.Step();
                for (var body = 0; (body < served.Length); body++) {
                    served[body] |= (fixture.Server.Population.NavigationFact(
                    facet: "hasPath",
                    index: body
                ) == 1);
                }
            }
            Assert.All(
                served,
                value => Assert.True(condition: value)
            );
        }
    }
    [Fact]
    public void SharedNavigationPinsACompletedAnswerUntilItsRequesterCanReadIt() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(
            goals: 1,
            budget: 108
        ));
        var first = JoinNavigator(
            fixture,
            SharedGoal(
                x: 5,
                y: 4,
                z: 2
            )
        );

        first.SetIntentSource(source: IntentSource.Idle);
        _ = JoinNavigator(
            fixture,
            SharedGoal(),
            slot: 1
        );
        fixture.Step();
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "pending",
                index: 1
            )
        );
        first.SetIntentSource(source: IntentSource.Producer(name: ProducerName));
        fixture.Step();
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "capacity",
                index: 0
            )
        );
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "hasPath",
                index: 1
            )
        );
        fixture.Step(); fixture.Step();
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "hasPath",
                index: 0
            )
        );
    }
    [Fact]
    public void SharedNavigationEvictsTheOldestGoalAfterRepeatedTouchesOfTheNewest() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(
            goals: 3,
            budget: 324
        ));
        var first = JoinNavigator(
            fixture,
            SharedGoal()
        );

        _ = JoinNavigator(
            fixture,
            SharedGoal(
                x: 4,
                y: 4,
                z: 1
            ),
            slot: 1
        );
        _ = JoinNavigator(
            fixture,
            SharedGoal(
                x: 4,
                y: 4,
                z: 2
            ),
            slot: 2
        );
        fixture.Step(); fixture.Step();
        Assert.Equal(
            3,
            SharedTrees(fixture: fixture).Count(predicate: tree => (tree.Goal >= 0))
        );
        for (var touch = 0; (touch < 5); touch++) {
            first.SetIntentSource(source: IntentSource.Idle);
            fixture.Step();
            first.SetIntentSource(source: IntentSource.Producer(name: ProducerName));
            fixture.Step();
        }
        var newest = SharedTrees(fixture: fixture).Single(predicate: tree => (tree.Goal == 76));

        Assert.Equal(
            0,
            newest.Age
        );
        Assert.True(condition: fixture.Server.ApplyDesignation(
            new(
                0,
                RegisterName,
                default,
                SharedGoal(
                    x: 5,
                    y: 4,
                    z: 2
                )
            ),
            Principal.Seat(slot: 0)
        ));
        fixture.Step();
        var trees = SharedTrees(fixture: fixture);

        Assert.DoesNotContain(
            collection: trees,
            filter: tree => (tree.Goal == 82)
        );
        Assert.Contains(
            collection: trees,
            filter: tree => (tree.Goal == 88)
        );
        Assert.Equal(
            new[] { 0, 1, 2 },
            trees.Select(selector: tree => tree.Age).Order().ToArray()
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void SharedNavigationResumesAnUnfinishedSearchThroughCheckpointCodec(bool medium) {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(
            budget: 2,
            medium: medium
        ));

        _ = JoinNavigator(
            fixture,
            SharedGoal()
        );
        _ = JoinNavigator(
            fixture,
            SharedGoal(
                x: 4,
                y: 3,
                z: 2
            ),
            slot: 1
        );
        for (var tick = 0; (tick < 7); tick++) { fixture.Step(); }
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "pending",
                index: 0
            )
        );
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var captured,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!),
                checkpoint: out var decoded,
                reason: out reason
            ),
            userMessage: reason
        );
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 7
        );
        var expected = new ulong[130];

        for (var tick = 0; (tick < expected.Length); tick++) {
            fixture.Step();
            expected[tick] = WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: ((ulong)tick)
            );
        }
        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        Assert.Equal(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 7
            )
        );
        for (var tick = 0; (tick < expected.Length); tick++) {
            fixture.Step();
            Assert.Equal(
                expected[tick],
                WorldStateHashComposition.HashAuthoritative(
                    server: fixture.Server,
                    tick: ((ulong)tick)
                )
            );
        }
    }
    [Fact]
    public void SharedNavigationCanDetachRetargetAndRejoinWithoutInvalidatingAnotherCreature() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 108));
        var first = JoinNavigator(
            fixture,
            SharedGoal()
        );

        _ = JoinNavigator(
            fixture,
            SharedGoal(),
            slot: 1
        );
        fixture.Step(); fixture.Step();
        var initial = fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 1)).Navigation!.Value;

        Assert.True(condition: fixture.Server.ApplyDesignation(
            new WorldDesignation(
                0,
                RegisterName,
                default,
                SharedGoal(
                    x: 5,
                    y: 0,
                    z: 2
                )
            ),
            Principal.Seat(slot: 0)
        ));
        fixture.Step(); fixture.Step();
        Assert.Equal(
            initial.GoalCell,
            fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 1)).Navigation!.Value.GoalCell
        );
        Assert.Equal(
            2,
            SharedTrees(fixture: fixture).Count(predicate: tree => (tree.Goal >= 0))
        );
        Assert.True(condition: fixture.Server.ApplyDesignation(
            new WorldDesignation(
                0,
                RegisterName,
                default,
                SharedGoal()
            ),
            Principal.Seat(slot: 0)
        ));
        first.Pose(
            SharedGoal(
                x: 3,
                y: 2,
                z: 1
            ),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Step(); fixture.Step();
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "hasPath",
                index: 0
            )
        );
        Assert.Equal(
            initial.GoalCell,
            fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 0)).Navigation!.Value.GoalCell
        );
    }
    [Fact]
    public void SharedNavigationRejectsMalformedSuccessorsBeforeChangingAnyLiveState() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 3));

        _ = JoinNavigator(
            fixture,
            SharedGoal()
        );
        fixture.Step(); fixture.Step();
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var captured,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ),
            userMessage: reason
        );
        var shared = captured!.Population.SharedNavigation!;
        var treeSlot = Array.FindIndex(
            array: shared[0].Trees,
            match: tree => (tree.Goal >= 0)
        );
        var tree = shared[0].Trees[treeSlot];
        var malformedTree = tree with {
            Nodes = tree.Nodes.Select(selector: node => ((node.Node == tree.Goal)
            ? node
            : node with { Next = node.Node })).ToArray(),
        };
        var malformedDomain = shared[0] with {
            Trees = shared[0].Trees.Select(selector: (row, index) => ((index == treeSlot)
            ? malformedTree
            : row)).ToArray(),
        };
        var malformed = captured with { Population = captured.Population with { SharedNavigation = [malformedDomain] } };
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 2
        );

        Assert.Contains(
            "successor",
            Assert.Throws<InvalidOperationException>(testCode: () => fixture.Server.RestoreCheckpoint(checkpoint: malformed)).Message
        );
        Assert.Equal(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 2
            )
        );
    }
    [Fact]
    public void SharedNavigationCanonicalizesTreesInvalidatedByWaterBeforeCheckpointing() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(
            budget: 2,
            medium: true
        ));

        _ = JoinNavigator(
            fixture,
            SharedGoal()
        );
        fixture.Step(); fixture.Step();
        var fields = fixture.Server.Population.Fields!;

        fields.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [new long[fields.CellCount]]));
        Assert.All(
            SharedTrees(fixture: fixture),
            tree => Assert.Equal(
                -1,
                tree.Goal
            )
        );
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var captured,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var reason
            ),
            userMessage: reason
        );
        var before = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 2
        );

        fixture.Step();
        var after = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 3
        );

        fixture.Server.RestoreCheckpoint(checkpoint: captured!);
        Assert.Equal(
            before,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 2
            )
        );
        fixture.Step();
        Assert.Equal(
            after,
            WorldStateHashComposition.HashAuthoritative(
                server: fixture.Server,
                tick: 3
            )
        );
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "unreachable",
                index: 0
            )
        );
    }
    [Fact]
    public void SharedNavigationAuthoringBoundsMemoryAndTotalWork() {
        var document = SharedNavigationDocument();
        var domain = document.Navigation.Rows[0];

        foreach (var shared in new[] { new WorldNavigationSharing(
            ExpandedNodesPerTick: 2,
            GoalCapacity: 0
        ), new(
            ExpandedNodesPerTick: 2,
            GoalCapacity: 17
        ), new(
            ExpandedNodesPerTick: 0,
            GoalCapacity: 1
        ), new(
            ExpandedNodesPerTick: 65_537,
            GoalCapacity: 1
        ) }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
                definition: document with { NavigationRaw = new(Domains: [domain with { Shared = shared }]) },
                reason: out _
            ));
        }
        var large = domain with {
            Width = 256,
            Depth = 256,
            Layers = 1,
            Shared = new(
            ExpandedNodesPerTick: 40_000,
            GoalCapacity: 16
        ),
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: document with { NavigationRaw = new(Domains: [large, large with { Name = "other" }]) },
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "shared trees require"
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "expansions per tick"
        );
    }
    [Fact]
    public void SharedNavigationDoesNotRestartForAnUnrelatedFieldWrite() {
        var document = SharedNavigationDocument(
            budget: 3,
            medium: true
        );

        document = document with {
            StateRaw = document.StateRaw! with {
                World = [.. document.State,
            new WorldStateRow(
                CellName.Parse(candidate: "heat"),
                CellKind.Fixed,
                Domain: new StateDomain.CellsOf("water-space"),
                Field: new WorldStateFieldTrait(
                    Initial: 0,
                    Min: 0,
                    Max: 1
                )
            )],
            },
        };
        using var fixture = Fixtures.FreshServer(document);

        _ = JoinNavigator(
            fixture,
            SharedGoal()
        );
        fixture.Step(); fixture.Step(); fixture.Step();
        var before = SharedTrees(fixture: fixture).Sum(selector: tree => tree.Nodes.Count(predicate: node => node.Settled));

        Assert.Equal(
            actual: before,
            expected: 6
        );
        Assert.Equal(
            1,
            fixture.Server.Population.Fields!.PaintSphere(
                "heat",
                0,
                0,
                0,
                0,
                FieldWriteOp.Set,
                FixedQ4816.One
            )
        );
        fixture.Step();
        Assert.Equal(
            (before + 3),
            SharedTrees(fixture: fixture).Sum(selector: tree => tree.Nodes.Count(predicate: node => node.Settled))
        );
    }
    [Fact]
    public void SharedNavigationSettledCostsMatchTheUnobstructedGridOracle() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument(budget: 108));

        _ = JoinNavigator(
            fixture,
            SharedGoal()
        );
        fixture.Step(); fixture.Step();
        var tree = Assert.Single(
            collection: SharedTrees(fixture: fixture),
            predicate: row => (row.Goal >= 0)
        );

        foreach (var node in tree.Nodes.Where(predicate: node => node.Settled)) {
            // Independent closed-form shortest cost in an unobstructed 26-neighbor grid.
            var distances = new[] { Math.Abs(value: ((node.Node % 6) - 4)), Math.Abs(value: ((node.Node / 18) - 4)), ((node.Node / 6) % 3) };

            Array.Sort(array: distances);
            var expected = (((distances[0] * 1732) + ((distances[1] - distances[0]) * 1414)) + ((distances[2] - distances[1]) * 1000));

            Assert.Equal(
                expected,
                node.Cost
            );
        }
    }
    [Fact]
    public void SharedSurfaceNavigationUsesTheSweptClearanceDetour() {
        using var fixture = Fixtures.FreshServer(WithFloor(
            NavigationDocument(SurfaceDomain() with {
                Shared = new(
                ExpandedNodesPerTick: 16,
                GoalCapacity: 1
            ),
            }),
            withBarrier: true
        ));

        _ = JoinNavigator(
            fixture,
            SharedGoal(
                x: 3,
                y: 0,
                z: 0
            )
        );
        fixture.Step(); fixture.Step();
        var route = fixture.Server.Population.Capture().Entries.Single(predicate: entry => (entry.Index == 0)).Navigation!.Value;

        Assert.True(
            condition: (route.Path.Length > 4),
            userMessage: string.Join(
                separator: ',',
                values: route.Path
            )
        );
    }
    [Fact]
    public void NavigationRefusesFullWidthOutOfDomainPositionsWithoutNarrowingOverflow() {
        using var fixture = Fixtures.FreshServer(SharedNavigationDocument());
        var body = JoinNavigator(
            fixture,
            SharedGoal()
        );

        body.Pose(
            new(
                X: FixedQ4816.MaxValue,
                Y: FixedQ4816.MinValue,
                Z: FixedQ4816.MaxValue
            ),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Step();
        Assert.Equal(
            1,
            fixture.Server.Population.NavigationFact(
                facet: "unreachable",
                index: 0
            )
        );
    }
}

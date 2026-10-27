using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Server.Tests;

public sealed class WorldRigidSleepContinuationLawTests {
    [Fact]
    public void CollisionAdmissionContinuesIdenticallyAfterRestore() {
        var source = Document();
        var definition = source with {
            KitRowsRaw = [source.Kits[0], source.Kits[0] with { Name = "overlap", Rigid = null, BodyContact = WorldBodyContactMode.Overlap }],
            AssignmentRaw = new WorldRowAssignment(WorldSequence.IndexDefault, ["ball", "ball", "overlap"]),
            CollisionRaw = source.Collision with { EventsRaw = new WorldCollisionEvents(BeginBudget: 1024, CandidateBudget: 1, MaxPairsPerBody: 1) },
        };
        var population = new WorldPopulation(definition: definition);

        population.SetSimulatedCount(3);
        Pose(index: 0, population: population, x: 0, y: .5f, z: 0);
        Pose(index: 1, population: population, x: .2f, y: .5f, z: .79f);
        Pose(index: 2, population: population, x: 3, y: .1f, z: 0);
        Settle(count: 2, population: population);
        var feed = new WorldEventFeed(capacity: population.Capacity);

        feed.Collect(definition: definition, population: population);
        feed.Collect(definition: definition, population: population);
        var captured = RoundTrip(definition: definition, feed: feed, population: population);
        var live = Arrive();
        var delivered = Assert.Single(collection: live);

        Assert.Equal(expected: (WorldEventFamily.CollisionBegin, 0L, 2L), actual: (delivered.Family, delivered.A, delivered.B));

        population.Restore(checkpoint: captured.Population, defaults: definition.PlayerDefaults, tick: 600);
        feed.Restore(checkpoint: captured.EventFeed);
        Assert.Equal(live, Arrive());

        WorldEventEdge[] Arrive() {
            Pose(population, 2, .5f, population.EntryBody(index: 0)!.Position.Y, 0);
            Step(population: population, tick: 601);
            feed.Collect(definition: definition, population: population);
            return [.. feed.Edges];
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void MovingANonRigidSupportWakesItsSleepingLoad(bool restore) {
        var definition = SupportedDocument();
        var population = SupportedPopulation(definition);
        var height = population.EntryBody(index: 1)!.FixedPosition.Y;

        if (restore) { population.Restore(checkpoint: RoundTrip(definition, population).Population, defaults: definition.PlayerDefaults, tick: 1200); }
        Step(population: population, tick: 1200);
        Assert.Equal(height, population.EntryBody(index: 1)!.FixedPosition.Y);
        Assert.Equal(1, population.SleepingRigidBodies);
        population.EntryBody(index: 0)!.Glide(position: (population.EntryBody(index: 0)!.FixedPosition + (FixedVector3.UnitX * FixedQ4816.FromInteger(value: 3))));
        for (var tick = 1200; (tick < 1320); tick++) { Step(population: population, tick: tick); }
        Assert.True(condition: (population.EntryBody(index: 1)!.FixedPosition.Y < (height - FixedQ4816.One)));
    }
    [Fact]
    public void MovingAGravityAreaWakesASleepingLoad() {
        var source = SupportedDocument();
        var definition = source with {
            KitRowsRaw = [.. source.Kits, source.Kits[0] with { Name = "marker", Collider = null }],
            AssignmentRaw = new WorldRowAssignment(WorldSequence.IndexDefault, ["support", "ball", "marker"]),
            PlacementRowsRaw = [.. source.Placements, new WorldPlacement("zone", "floor", Vector3.Zero, 0, 1,
                Attach: new WorldPlacementAttach(2, new DocumentVector3(value: Vector3.Zero)))],
            GravityRaw = source.Gravity with {
                Areas = [new WorldGravityArea("zone", 0, WorldGravityAreaMode.Replace,
                new WorldGravityAreaBounds.SphereBounds(Radius: 4),
                new WorldGravityAreaAcceleration.Directional(Value: new DocumentVector3(value: new Vector3(x: 0, y: 10, z: 0))))],
            },
        };
        var population = SupportedPopulation(count: 3, definition: definition);
        var height = population.EntryBody(index: 1)!.FixedPosition.Y;
        var version = population.ContactFieldVersion;

        population.EntryBody(index: 2)!.Glide(position: new FixedVector3(X: FixedQ4816.Zero, Y: FixedQ4816.FromInteger(value: 2), Z: FixedQ4816.Zero));
        Step(population: population, tick: 1200);
        Assert.Equal(version, population.ContactFieldVersion);
        Assert.True(condition: (population.EntryBody(index: 1)!.FixedPosition.Y > height));
        Assert.False(condition: population.EntryBody(index: 1)!.Resting);
    }
    [Fact]
    public void ASelectedRigidKitRestoresItsSleepingContinuation() {
        var source = Document();
        var definition = source with {
            DefaultSeatKitRaw = "locomotion",
            KitRowsRaw = [source.Kits[0] with { Name = "locomotion", Rigid = null }, source.Kits[0]],
            AssignmentRaw = new WorldRowAssignment(WorldSequence.IndexDefault, ["locomotion"],
                Select: new WorldRowSelector(BodyState: "mode", Cases: [new WorldRowSelectorCase(Row: "ball", Value: 1)])),
            StateRaw = new WorldStateSection(Body: [new ActionStateSlot("mode", ActionStateKind.Counter, Initial: 0,
                Envelope: new ActionStateEnvelope.Range(Maximum: 1, Minimum: 0))]),
        };
        var population = BoundPopulation(definition: definition);

        Assert.True(condition: WorldBodyStateReads.TryResolveSlot(definition: definition, kind: out _, name: "mode", slot: out var slot));
        population.ActionStateLane.Write(slot: slot, ordinal: 0, raw: FixedQ4816.One.Value);
        population.SyncBodyCells(definition: definition);
        Pose(index: 0, population: population, x: 0, y: .5f, z: 0);
        Settle(count: 1, population: population);
        var captured = RoundTrip(definition, population).Population;
        var hash = WorldReplaySnapshot.HashState(population: population);

        population.Restore(checkpoint: captured, defaults: definition.PlayerDefaults, tick: 600);
        population.SyncBodyCells(definition: definition);
        Assert.True(condition: population.EntryBody(index: 0)!.IsRigid);
        Assert.Equal(hash, WorldReplaySnapshot.HashState(population: population));
        Assert.Equal(1, population.SleepingRigidBodies);
    }
    [Fact]
    public void ScaleCatchUpAfterRestorePreservesTheSleepingContinuation() {
        var source = Document();
        var definition = Fixtures.WithBodyScale(initial: FixedQ4816.FromDouble(value: .5), source: source);
        var population = BoundPopulation(definition: definition);

        Pose(index: 0, population: population, x: 0, y: .5f, z: 0);
        Settle(count: 1, population: population);
        var captured = RoundTrip(definition, population).Population;
        var hash = WorldReplaySnapshot.HashState(population: population);

        population.BindActionStateLane(arena: new StateArena(definition.StateCatalog, definition.StateRaw, ArenaTime.Origin), definition: definition);
        population.Restore(checkpoint: captured, defaults: definition.PlayerDefaults, tick: 600);
        population.SyncBodyCells(definition: definition);
        Assert.Equal(hash, WorldReplaySnapshot.HashState(population: population));
        Assert.True(condition: population.EntryBody(index: 0)!.Resting);
        Assert.Equal(1, population.SleepingRigidBodies);
    }

    private static WorldAuthorityCheckpoint RoundTrip(WorldDefinition definition, WorldPopulation population, WorldEventFeed? feed = null) {
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(hostRow: WorldAuthorityHostRowCheckpoint.Empty, checkpoint: out var captured, reason: out var reason), userMessage: reason);
        captured = captured! with { Population = population.Capture(), EventFeed = (feed ?? fixture.Server.Events).Capture() };
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: captured);

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes, out var decoded, out reason), userMessage: reason);
        Assert.Equal(bytes, WorldAuthorityCheckpointCodec.Encode(checkpoint: decoded!));
        return decoded!;
    }
    private static WorldPopulation BoundPopulation(WorldDefinition definition) {
        var population = new WorldPopulation(definition: definition);

        population.BindActionStateLane(arena: new StateArena(definition.StateCatalog, definition.StateRaw, ArenaTime.Origin), definition: definition);
        population.SetSimulatedCount(1);
        population.SyncBodyCells(definition: definition);
        return population;
    }
    private static WorldPopulation SupportedPopulation(WorldDefinition definition, int count = 2) {
        var population = new WorldPopulation(definition: definition);

        population.SetSimulatedCount(count);
        Pose(index: 0, population: population, x: 0, y: 2, z: 0);
        Pose(index: 1, population: population, x: 0, y: 3, z: 0);
        if (count > 2) { Pose(index: 2, population: population, x: 20, y: 2, z: 0); }
        Settle(count: 1, population: population);
        return population;
    }
    private static void Pose(WorldPopulation population, int index, float x, float y, float z) =>
        population.EntryBody(index: index)!.Pose(pitchRadians: 0, rollRadians: 0, x: x, y: y, yawRadians: 0, z: z);
    private static void Step(WorldPopulation population, int tick) {
        population.AdvanceSimulated(stepStartEngineTick: (((ulong)tick) * 1680), stepTicks: 1680, tick: ((ulong)tick));
        population.ResolveDynamicContacts();
    }
    private static void Settle(WorldPopulation population, int count) {
        for (var tick = 0; ((tick < 1200) && (population.SleepingRigidBodies != count)); tick++) { Step(population: population, tick: tick); }
        Assert.Equal(count, population.SleepingRigidBodies);
    }
    private static WorldDefinition SupportedDocument() {
        var source = Document();

        return source with {
            DefaultSeatKitRaw = "support",
            KitRowsRaw = [source.Kits[0] with { Name = "support", Rigid = null }, source.Kits[0] with { Rigid = source.Kits[0].Rigid! with { Restitution = 0 } }],
            AssignmentRaw = new WorldRowAssignment(WorldSequence.IndexDefault, ["support", "ball"]),
        };
    }
    private static WorldDefinition Document() {
        var source = Fixtures.BuildGradientUpDocument(gradientUp: false);
        var creation = CreationCanonicalizer.Canonicalize(document: new CreationDocument(CreationDocument.CurrentSchema, "floor", null,
            [new ShapeDocument(0, "floor", SdfSolidPrimitive.Box, Vector3.Zero, Quaternion.Identity, new Vector3(x: 24, y: .1f, z: 24),
                0, SdfBlendOp.Union, 0, 0)], null), source: "floor");
        var kit = source.Kits[0] with {
            Name = "ball",
            BodyMotionProgram = "idle",
            Collider = new WorldCollider.Sphere(Radius: .4f),
            BodyContact = WorldBodyContactMode.Solid,
            Rigid = new WorldRigid(AngularDamping: .02f, Friction: .3f, LinearDamping: .01f, Mass: 1, Restitution: .4f, RollingFriction: .05f),
        };

        return source with {
            PopulationRaw = new WorldBodiesDefaults(LocalSeatsRaw: 0, CapacityRaw: 4, NetworkPlayers: 4),
            DefaultSeatKitRaw = "ball",
            AssignmentRaw = new WorldRowAssignment(WorldSequence.IndexDefault, ["ball"]),
            BodyMotionProgramsRaw = [.. source.BodyMotionPrograms.Where(predicate: static row => (row.Name != "idle")), new BodyMotionProgram("idle", BodyMotionProgram.CurrentVersion, BodyProgramKind.Motion, [BodyMotionOp.CommitPose])],
            CreationsRaw = [new WorldPrototype("floor", creation.Document, creation.Hash)],
            GravityRaw = source.Gravity with { Uniform = new DocumentVector3(value: new Vector3(x: 0, y: -9.8f, z: 0)) },
            KitRowsRaw = [kit],
            PlacementRowsRaw = [new WorldPlacement("floor", "floor", Vector3.Zero, 0, 1, Solid: new WorldSolid(Margin: 0))],
        };
    }
}

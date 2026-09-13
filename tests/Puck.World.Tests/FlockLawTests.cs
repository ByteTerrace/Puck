using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Authoring, frozen-image steering, cadence, lifecycle, and checkpoint evidence for local flocks.</summary>
public sealed class FlockLawTests {
    private static WorldDefinition Document(WorldFlockProfile profile, bool targeted = false, BodyTargetSource? target = null) {
        var definition = Fixtures.BuildDocument();

        targeted |= (target is not null);
        var producer = new BodyMotionProgram(
            Name: "flock",
            Version: BodyMotionProgram.CurrentVersion,
            Kind: BodyProgramKind.Producer,
            Operations: (targeted
            ? [BodyMotionOp.SenseNearestInCone, BodyMotionOp.ProduceFlockIntent]
            : [BodyMotionOp.ProduceFlockIntent]),
            Target: (target ?? (targeted
            ? new BodyTargetSource.Designated(Register: "goal")
            : null))
        );

        return definition with {
            BodyMotionProgramsRaw = [.. definition.BodyMotionPrograms, producer],
            TargetRegistersRaw = (targeted
            ? [new WorldTargetRegister(
                    Name: "goal",
                    MaximumRange: 100,
                    MaximumHalfAngleDegrees: 180,
                    RequiresLineOfSight: false
                )]
            : definition.TargetRegistersRaw),
            KitRowsRaw = [definition.Kits[0] with {
                // Deliberately no roam producer: spawn/color must not depend on choosing that behavior.
                ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                    ["flock"] = new(
                Scalars: new Dictionary<string, float>(),
                Channels: new Dictionary<string, string>(),
                Flock: profile
            ),
                },
            }],
        };
    }
    private static WorldAuthorityHostRowCheckpoint HostRow() => new(
        AnnouncedCrossingHolds: [],
        AppliedTransferHighWater: null,
        AppliedTransferIds: [],
        ElapsedEngineTicks: 0,
        ForwardedBodies: [],
        FreshCounter: 0,
        InDoubtTransfers: [],
        IsPaused: false,
        NextTransferId: 1,
        PortalOccupancy: [],
        Retained: false,
        ScheduleAccumulatorTicks: 0,
        SeededArrivals: []
    );
    private static WorldBody Join(WorldFixture fixture, int slot, FixedVector3 position, bool flock = true) {
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: WorldPrincipal.Seat(slot: slot),
            Slot: slot,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        var body = fixture.Server.Body(index: slot)!;

        body.Pose(
            position,
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        body.SetIntentSource(source: (flock
            ? IntentSource.Producer(name: "flock")
            : IntentSource.Idle));
        return body;
    }
    private static FixedVector3 Position(int x, int y = 0, int z = 0) =>
        new(
            X: FixedQ4816.FromInteger(value: x),
            Y: FixedQ4816.FromInteger(value: y),
            Z: FixedQ4816.FromInteger(value: z)
        );
    private static WorldFlockProfile Profile(float cadence = 0) => new(
        Range: 20,
        SeparationRadius: 1,
        CandidateBudget: 4,
        MaxNeighbors: 3,
        UpdateSeconds: cadence,
        Space: WorldFlockSpace.Tangent,
        Separation: 0,
        Alignment: 0,
        Cohesion: 1,
        Goal: 0,
        Inertia: 0,
        ArrivalDistance: 0,
        HalfAngleDegrees: 180,
        RequiresLineOfSight: false
    );

    [Fact]
    public void AuthoringRejectsUnboundedOrUnconsumedFlocks() {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(Profile()),
                reason: out var reason
            ),
            userMessage: reason
        );
        foreach (var profile in new[] { Profile() with { CandidateBudget = 0 }, Profile() with { MaxNeighbors = 5 },
            Profile() with { Range = float.NaN }, Profile() with { Cohesion = -1 }, Profile() with { Space = WorldFlockSpace.Volume } }) {
            Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(profile),
                reason: out reason
            ));
            Assert.Contains(
                actualString: reason,
                comparisonType: StringComparison.OrdinalIgnoreCase,
                expectedSubstring: "flock"
            );
        }
    }
    [Fact]
    public void CheckpointPreservesCadenceAndFrozenTravelAcrossManyUpdates() {
        using var fixture = Fixtures.FreshServer(Document(Profile(cadence: 0.1f) with { Alignment = 0.25f }));

        _ = Join(
            fixture,
            0,
            Position(-2)
        );
        _ = Join(
            fixture,
            1,
            Position(2)
        );
        for (var step = 0; (step < 7); step++) { fixture.Step(); }
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var captured,
                hostRow: HostRow(),
                reason: out var reason
            ),
            userMessage: reason
        );
        var saved = captured!.Population.Entries[0].Flock;

        Assert.True(condition: saved.Seeded);
        Assert.True(condition: (saved.RemainingTicks > 0));
        Assert.Equal(
            1UL,
            saved.SampleOrdinal
        );
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured),
                checkpoint: out var decoded,
                reason: out reason
            ),
            userMessage: reason
        );
        var expected = new ulong[180];

        for (var step = 0; (step < expected.Length); step++) {
            fixture.Step();
            expected[step] = WorldRuntimeStateHash.HashAuthoritative(
                server: fixture.Server,
                tick: ((ulong)step)
            );
        }
        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        for (var step = 0; (step < expected.Length); step++) {
            fixture.Step();
            Assert.Equal(
                expected[step],
                WorldRuntimeStateHash.HashAuthoritative(
                    server: fixture.Server,
                    tick: ((ulong)step)
                )
            );
        }
    }
    [Fact]
    public void CohesionUsesOneFrozenImageAndZeroWeightIsDiscriminatingControl() {
        foreach (var enabled in new[] { true, false }) {
            using var fixture = Fixtures.FreshServer(Document(Profile() with { Cohesion = (enabled
                ? 1
                : 0) }));
            var left = Join(
                fixture,
                0,
                Position(-2)
            );
            var right = Join(
                fixture,
                1,
                Position(2)
            );

            fixture.Step();
            Assert.Equal(
                -left.FixedPosition.X,
                right.FixedPosition.X
            );
            Assert.Equal(
                enabled,
                (left.FixedPosition.X > FixedQ4816.FromInteger(value: -2))
            );
            Assert.Equal(
                2,
                fixture.Server.Population.FlockStatistics.Updates
            );
            Assert.InRange(
                fixture.Server.Population.FlockStatistics.Candidates,
                2,
                8
            );
        }
    }
    [Fact]
    public void GoalChangesImmediatelyWhileNeighborCadenceRemainsCached() {
        using var fixture = Fixtures.FreshServer(Document(
            Profile(cadence: 10) with { Cohesion = 0, Goal = 1 },
            targeted: true
        ));
        var body = Join(
            fixture,
            0,
            Position(0)
        );
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplyDesignation(
            new WorldDesignation(
                EntityIndex: 0,
                Register: "goal",
                Subject: default,
                Point: Position(10)
            ),
            actor
        ));
        fixture.Step();
        var first = body.FixedPosition.X;

        Assert.True(condition: (first > FixedQ4816.Zero));
        Assert.True(condition: fixture.Server.ApplyDesignation(
            new WorldDesignation(
                EntityIndex: 0,
                Register: "goal",
                Subject: default,
                Point: Position(-10)
            ),
            actor
        ));
        fixture.Step();
        Assert.True(condition: (body.FixedPosition.X < first));
        Assert.Equal(
            0,
            fixture.Server.Population.FlockStatistics.Updates
        );
    }
    [Fact]
    public void LeavingAndReselectingProducerRefreshesNeighborsImmediately() {
        using var fixture = Fixtures.FreshServer(Document(Profile(cadence: 60)));
        var body = Join(
            fixture,
            0,
            Position(0)
        );
        var other = Join(
            fixture,
            1,
            Position(2),
            flock: false
        );

        fixture.Step();
        body.SetIntentSource(source: IntentSource.Idle);
        fixture.Step();
        other.Pose(
            Position(-2),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        body.SetIntentSource(source: IntentSource.Producer(name: "flock"));
        var before = body.FixedPosition.X;

        fixture.Step();
        Assert.True(condition: (body.FixedPosition.X < before));
        Assert.Equal(
            1,
            fixture.Server.Population.FlockStatistics.Updates
        );
    }
    [Fact]
    public void RebuildPreservesPerceptionUnlessItsBindingChanges() {
        var definition = Document(Profile(cadence: 60));
        using var fixture = Fixtures.FreshServer(definition);

        _ = Join(
            fixture,
            0,
            Position(0)
        );
        _ = Join(
            fixture,
            1,
            Position(2),
            flock: false
        );
        fixture.Step();
        var saved = fixture.Server.Population.Capture().Entries[0].Flock;

        fixture.Server.Population.Rebuild(
            definition,
            solids: null
        );
        fixture.Step();
        Assert.Equal(
            0,
            fixture.Server.Population.FlockStatistics.Updates
        );
        var kept = fixture.Server.Population.Capture().Entries[0].Flock;

        Assert.Equal(
            saved.Desired,
            kept.Desired
        );
        Assert.Equal(
            saved.SampleOrdinal,
            kept.SampleOrdinal
        );
        var retuned = Document(Profile(cadence: 60) with { Cohesion = 0 });

        fixture.Server.Population.Rebuild(
            retuned,
            solids: null
        );
        fixture.Step();
        Assert.Equal(
            1,
            fixture.Server.Population.FlockStatistics.Updates
        );
        Assert.Equal(
            FixedVector3.Zero,
            fixture.Server.Population.Capture().Entries[0].Flock.Desired
        );
    }
    [Fact]
    public void SensedObservationDoesNotAttachToAReusedSlot() {
        var document = Document(
            Profile(cadence: 60) with { Cohesion = 0, Goal = 1 },
            target: new BodyTargetSource.Sensed(
                HalfAngleDegrees: 180,
                Range: 20,
                RequiresLineOfSight: false,
                Scope: BodyTargetScope.Bodies
            )
        );
        using var fixture = Fixtures.FreshServer(document with { PopulationRaw = document.Population with { CapacityRaw = 5, NetworkPlayers = 1 } });
        var body = Join(
            fixture,
            0,
            Position(0)
        );

        Assert.Equal(
            1,
            fixture.Server.Population.SetSimulatedCount(1)
        );
        fixture.Server.Body(index: 4)!.Pose(
            Position(10),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Server.Body(index: 4)!.SetIntentSource(source: IntentSource.Idle);
        fixture.Step();
        var observed = fixture.Server.Population.Capture().Entries[0].Flock.Target;

        Assert.NotNull(value: observed);
        Assert.Equal(
            0,
            fixture.Server.Population.SetSimulatedCount(0)
        );
        Assert.Equal(
            1,
            fixture.Server.Population.SetSimulatedCount(1)
        );
        fixture.Server.Body(index: 4)!.Pose(
            Position(-10),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Server.Body(index: 4)!.SetIntentSource(source: IntentSource.Idle);
        body.Pose(
            Position(0),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        fixture.Step();
        Assert.Equal(
            FixedQ4816.Zero,
            body.FixedPosition.X
        );
        Assert.Equal(
            0,
            fixture.Server.Population.FlockStatistics.Updates
        );
    }
    [Fact]
    public void SensedTargetAndNeighborsShareOneBudgetIncludingRejectedCandidates() {
        using var fixture = Fixtures.FreshServer(Document(
            Profile(cadence: 60) with {
            CandidateBudget = 2,
            MaxNeighbors = 1,
            HalfAngleDegrees = 1,
            Goal = 1,
        },
            target: new BodyTargetSource.Sensed(
                HalfAngleDegrees: 1,
                Range: 20,
                RequiresLineOfSight: false,
                Scope: BodyTargetScope.Bodies
            )
        ));

        _ = Join(
            fixture,
            0,
            Position(0)
        );
        for (var index = 1; (index < 4); index++) { _ = Join(
            fixture,
            index,
            Position(
                0,
                z: index
            ),
            flock: false
        ); }
        fixture.Step();
        Assert.Equal(
            2,
            fixture.Server.Population.FlockStatistics.Candidates
        );
        Assert.Equal(
            0,
            fixture.Server.Population.FlockStatistics.RetainedNeighbors
        );
        Assert.Null(value: fixture.Server.Population.Capture().Entries[0].Flock.Target);
        fixture.Step();
        Assert.Equal(
            0,
            fixture.Server.Population.FlockStatistics.Candidates
        );
        Assert.Equal(
            0,
            fixture.Server.Population.FlockStatistics.Updates
        );
    }
    [Fact]
    public void SensedTargetKeepsLastObservedPoseAndSurvivesCheckpoint() {
        using var fixture = Fixtures.FreshServer(Document(
            Profile(cadence: 10) with { Range = 1, Cohesion = 0, Goal = 1 },
            target: new BodyTargetSource.Sensed(
                HalfAngleDegrees: 180,
                Range: 20,
                RequiresLineOfSight: false,
                Scope: BodyTargetScope.Bodies
            )
        ));
        var body = Join(
            fixture,
            0,
            Position(0)
        );
        var other = Join(
            fixture,
            1,
            Position(10),
            flock: false
        );

        fixture.Step();
        var seen = fixture.Server.Population.Capture().Entries[0].Flock.Target;

        Assert.NotNull(value: seen);
        Assert.Equal(
            Position(10),
            seen.Value.Position
        );
        Assert.Equal(
            0,
            fixture.Server.Population.FlockStatistics.RetainedNeighbors
        );
        other.Pose(
            Position(-10),
            FixedQ4816.Zero,
            FixedQ4816.Zero,
            FixedQ4816.Zero
        );
        var before = body.FixedPosition.X;

        fixture.Step();
        Assert.True(condition: (body.FixedPosition.X > before));
        Assert.Equal(
            seen,
            fixture.Server.Population.Capture().Entries[0].Flock.Target
        );
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: HostRow(),
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!),
                checkpoint: out var decoded,
                reason: out reason
            ),
            userMessage: reason
        );
        var expected = new ulong[20];

        for (var tick = 0; (tick < expected.Length); tick++) {
            fixture.Step();
            expected[tick] = WorldRuntimeStateHash.HashAuthoritative(
                server: fixture.Server,
                tick: ((ulong)tick)
            );
        }
        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        for (var tick = 0; (tick < expected.Length); tick++) {
            fixture.Step();
            Assert.Equal(
                expected[tick],
                WorldRuntimeStateHash.HashAuthoritative(
                    server: fixture.Server,
                    tick: ((ulong)tick)
                )
            );
        }
    }
    [Fact]
    public void UnusedWideRangeProfileDoesNotChangeSmallRangeAttention() {
        var small = Profile() with { Range = 2, CandidateBudget = 2, MaxNeighbors = 1 };
        var definition = Document(small);
        var kit = definition.Kits[0];
        var wider = definition with {
            BodyMotionProgramsRaw = [.. definition.BodyMotionPrograms, new BodyMotionProgram(
                "wide",
                BodyMotionProgram.CurrentVersion,
                BodyProgramKind.Producer,
                [BodyMotionOp.ProduceFlockIntent]
            )],
            KitRowsRaw = [kit with { ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                ["wide"] = kit.Producers["flock"] with { Flock = small with { Range = 1000 } },
            } }],
        };
        using var ordinary = Fixtures.FreshServer(definition);
        using var withWide = Fixtures.FreshServer(wider);

        foreach (var fixture in new[] { ordinary, withWide }) {
            _ = Join(
                fixture,
                0,
                Position(0)
            );
            _ = Join(
                fixture,
                1,
                Position(1),
                flock: false
            );
            _ = Join(
                fixture,
                2,
                Position(100),
                flock: false
            );
            _ = Join(
                fixture,
                3,
                Position(101),
                flock: false
            );
        }
        for (var tick = 0; (tick < 120); tick++) {
            ordinary.Step();
            withWide.Step();
            Assert.Equal(
                ordinary.Server.Body(index: 0)!.FixedPosition,
                withWide.Server.Body(index: 0)!.FixedPosition
            );
            Assert.Equal(
                ordinary.Server.Population.FlockStatistics,
                withWide.Server.Population.FlockStatistics
            );
        }
    }
}

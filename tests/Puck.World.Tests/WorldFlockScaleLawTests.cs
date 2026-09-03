using System.Diagnostics;
using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Whole-population acceptance evidence for the dense few-thousand-creature representation.</summary>
public sealed class WorldFlockScaleLawTests(ITestOutputHelper output) {
    private static WorldCellName Name(string text) => WorldCellName.Parse(text);
    private static WorldValueExpression SocialAffinity(string dimension) => new([
        new WorldValueToken.Social(new(new(new(Body: "left"), new(Body: "right"), dimension)))
    ]);
    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
        AnnouncedCrossingHolds: [], AppliedTransferHighWater: null, AppliedTransferIds: [], ElapsedEngineTicks: 0,
        ForwardedBodies: [], FreshCounter: 0, InDoubtTransfers: [], IsPaused: false, NextTransferId: 1,
        PortalOccupancy: [], Retained: false, ScheduleAccumulatorTicks: 0, SeededArrivals: []);
    private static void Coincide(WorldFixture fixture) {
        for (var index = fixture.Server.Population.LocalSeatCount; index < fixture.Server.Population.Capacity; index++) {
            fixture.Server.Body(index)!.Pose(FixedVector3.Zero, FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.Zero);
        }
    }
    private static WorldDefinition DenseDocument(float updateSeconds = 0.1f, bool flocking = true, bool colliders = false,
        float motionSeconds = (1f / 60f), float steeringSeconds = 0.1f, bool socialAffinities = false) {
        var definition = Fixtures.BuildDocument();
        var profile = new WorldFlockProfile(
            Range: 20, SeparationRadius: 1, CandidateBudget: 32, MaxNeighbors: 16,
            UpdateSeconds: updateSeconds, Space: WorldFlockSpace.Tangent,
            Separation: 1, Alignment: 0.5f, Cohesion: 0.5f, Goal: 0, Inertia: 0.25f,
            ArrivalDistance: 0, HalfAngleDegrees: 180, RequiresLineOfSight: false);
        if (socialAffinities) {
            profile = profile with {
                AlignmentAffinity = SocialAffinity("competence"),
                CohesionAffinity = SocialAffinity("affection"),
            };
        }
        return definition with {
            PopulationRaw = definition.Population with {
                CapacityRaw = WorldBodiesLimits.CapacityCeiling,
                NetworkPlayers = WorldBodiesLimits.CapacityCeiling - definition.Population.LocalSeats,
                DefaultPeerSourceRaw = flocking ? IntentSource.Producer("flock") : IntentSource.Idle,
            },
            BodyMotionProgramsRaw = [.. definition.BodyMotionPrograms, new(
                Name: "flock", Version: BodyMotionProgram.CurrentVersion, Kind: BodyProgramKind.Producer,
                Operations: [BodyMotionOp.ProduceFlockIntent])],
            KitRowsRaw = [definition.Kits[0] with {
                AutonomyRaw = new WorldAutonomyCadence(MotionSeconds: motionSeconds, SteeringSeconds: steeringSeconds),
                Collider = colliders ? new WorldCollider.Sphere(0.5f) : null,
                ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                    ["flock"] = new(Scalars: new Dictionary<string, float>(), Channels: new Dictionary<string, string>(), Flock: profile),
                },
            }],
            StateRaw = socialAffinities
                ? new WorldStateSection(Social: new WorldSocialPolicy(
                    Dimensions: [new(Name("affection")), new(Name("competence"))],
                    ImpressionCapacity: 65536,
                    ImpressionsPerObserver: 256,
                    ReceiptCapacity: 65536,
                    EvidenceAttemptsPerTick: 1024,
                    ExpiredReceiptsPerTick: 1024))
                : definition.StateRaw,
        };
    }

    [Fact]
    public void FourThousandCoincidentCreaturesStayBoundedDeterministicAndAllocationStable() {
        using var fixture = Fixtures.FreshServer(DenseDocument());
        var expected = WorldBodiesLimits.CapacityCeiling - fixture.Server.Population.LocalSeatCount;
        Assert.Equal(expected, fixture.Server.Population.SetSimulatedCount(expected));
        Coincide(fixture);

        fixture.Step();
        var burst = fixture.Server.Population.FlockStatistics;
        Assert.InRange(burst.Followers, 1, 172);
        Assert.InRange(burst.Updates, 1, 172);
        Assert.InRange(burst.Candidates, 0, expected * 32);

        for (var tick = 0; tick < 30; tick++) { fixture.Step(); }
        var candidates = 0;
        var retained = 0;
        var updates = 0;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        for (var tick = 0; tick < 120; tick++) {
            fixture.Step();
            var work = fixture.Server.Population.FlockStatistics;
            candidates = Math.Max(candidates, work.Candidates);
            retained = Math.Max(retained, work.RetainedNeighbors);
            updates = Math.Max(updates, work.Updates);
        }
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        output.WriteLine($"dense flock: {expected} coincident creatures, 120 ticks in {watch.Elapsed.TotalMilliseconds:F1} ms ({watch.Elapsed.TotalMilliseconds / 120:F2} ms/tick), {allocated} thread bytes");
        Assert.InRange(candidates, 0, updates * 32);
        Assert.InRange(retained, 0, updates * 16);
        Assert.InRange(allocated, 0, 512 * 120);
        Assert.InRange(fixture.Server.Population.AutonomyStatistics.MotionUpdates, 1023, 1024);
        Assert.InRange(fixture.Server.Population.AutonomyStatistics.SteeringUpdates, 170, 172);

        var first = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, 120);
        using var replay = Fixtures.FreshServer(DenseDocument());
        Assert.Equal(expected, replay.Server.Population.SetSimulatedCount(expected));
        Coincide(replay);
        for (var tick = 0; tick < 151; tick++) { replay.Step(); }
        Assert.Equal(first, WorldRuntimeStateHash.HashAuthoritative(replay.Server, 120));
    }

    [Fact]
    public void DenseIntegrationAndPerceptionCostsRemainVisible() {
        foreach (var sample in new[] {
            (Perception: 60f, Flocking: false, Motion: 0f, Steering: 0f),
            (Perception: 60f, Flocking: true, Motion: 0f, Steering: 0f),
            (Perception: 0.1f, Flocking: true, Motion: (1f / 60f), Steering: 0.1f),
        }) {
            using var fixture = Fixtures.FreshServer(DenseDocument(sample.Perception, sample.Flocking,
                motionSeconds: sample.Motion, steeringSeconds: sample.Steering));
            var count = WorldBodiesLimits.CapacityCeiling - fixture.Server.Population.LocalSeatCount;
            Assert.Equal(count, fixture.Server.Population.SetSimulatedCount(count));
            Coincide(fixture);
            fixture.Step();
            for (var tick = 0; tick < 20; tick++) { fixture.Step(); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (var tick = 0; tick < 20; tick++) { fixture.Step(); }
            watch.Stop();
            output.WriteLine($"dense flock={sample.Flocking} perception={sample.Perception}s motion={sample.Motion}s steering={sample.Steering}s: {watch.Elapsed.TotalMilliseconds / 20:F2} ms/tick, {GC.GetAllocatedBytesForCurrentThread() - before} bytes");
        }
    }

    [Fact]
    public void FourThousandSociallyFilteredCreaturesStayInsideAuthoredAttentionAndAllocationBudgets() {
        using var fixture = Fixtures.FreshServer(DenseDocument(socialAffinities: true));
        var count = WorldBodiesLimits.CapacityCeiling - fixture.Server.Population.LocalSeatCount;
        Assert.Equal(count, fixture.Server.Population.SetSimulatedCount(count));
        Coincide(fixture);
        for (var tick = 0; tick < 30; tick++) { fixture.Step(); }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var maximumEvaluations = 0;
        var maximumWork = 0L;
        for (var tick = 0; tick < 120; tick++) {
            fixture.Step();
            maximumEvaluations = Math.Max(maximumEvaluations, fixture.Server.Population.FlockStatistics.AffinityEvaluations);
            maximumWork = Math.Max(maximumWork, fixture.Server.Population.FlockStatistics.AffinityWorkUnits);
        }
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var declared = WorldRuleWorkBudget.Measure(fixture.Server.Definition);

        output.WriteLine($"dense social flock: {count} creatures, 120 ticks in {elapsed.TotalMilliseconds:F1} ms "
            + $"({elapsed.TotalMilliseconds / 120:F2} ms/tick), {allocated} thread bytes, "
            + $"max {maximumEvaluations} affinity evaluations/{maximumWork} work units");
        Assert.InRange(maximumEvaluations, 1, 172 * 16 * 2);
        Assert.InRange(maximumWork, 1, declared.FlockAffinityWorkUnitsPerTick);
        Assert.InRange(allocated, 0, 512 * 120);
    }

    [Fact]
    public void DensePopulationPhaseCostsRemainVisible() {
        using var fixture = Fixtures.FreshServer(DenseDocument());
        var population = fixture.Server.Population;
        var count = WorldBodiesLimits.CapacityCeiling - population.LocalSeatCount;
        Assert.Equal(count, population.SetSimulatedCount(count));
        Coincide(fixture);
        for (ulong tick = 1; tick <= 20; tick++) {
            population.AdvanceSimulated(tick, Fixtures.StepTicks, (tick - 1) * Fixtures.StepTicks);
            population.ResolveDynamicContacts();
            population.ResolveTethers();
            population.CompleteStep(tick);
        }

        static TimeSpan Measure(Action action) {
            var start = Stopwatch.GetTimestamp();
            action();
            return Stopwatch.GetElapsedTime(start);
        }

        var advance = TimeSpan.Zero;
        var contacts = TimeSpan.Zero;
        var tethers = TimeSpan.Zero;
        var complete = TimeSpan.Zero;
        for (ulong tick = 21; tick <= 40; tick++) {
            var start = (tick - 1) * Fixtures.StepTicks;
            advance += Measure(() => population.AdvanceSimulated(tick, Fixtures.StepTicks, start));
            contacts += Measure(population.ResolveDynamicContacts);
            tethers += Measure(population.ResolveTethers);
            complete += Measure(() => population.CompleteStep(tick));
        }
        output.WriteLine($"dense population phase mean: advance {advance.TotalMilliseconds / 20:F2}, contacts {contacts.TotalMilliseconds / 20:F2}, tethers {tethers.TotalMilliseconds / 20:F2}, complete {complete.TotalMilliseconds / 20:F2} ms");
    }

    [Fact]
    public void CoincidentColliderEventsStayBounded() {
        using var fixture = Fixtures.FreshServer(DenseDocument(flocking: false, colliders: true));
        var population = fixture.Server.Population;
        var count = WorldBodiesLimits.CapacityCeiling - population.LocalSeatCount;
        Assert.Equal(count, population.SetSimulatedCount(count));
        Coincide(fixture);

        var first = Stopwatch.GetTimestamp();
        fixture.Step();
        var firstElapsed = Stopwatch.GetElapsedTime(first);
        Assert.InRange(fixture.Server.Events.CollisionTrackedPairs, 1,
            (count * fixture.Server.Definition.Collision.Events.MaxPairsPerBody) / 2);

        var start = Stopwatch.GetTimestamp();
        for (var tick = 0; tick < 20; tick++) { fixture.Step(); }
        var elapsed = Stopwatch.GetElapsedTime(start);
        output.WriteLine($"dense collision events: first {firstElapsed.TotalMilliseconds:F2} ms; steady {elapsed.TotalMilliseconds / 20:F2} ms/tick; {fixture.Server.Events.CollisionTrackedPairs} tracked, {fixture.Server.Events.CollisionCandidates} candidates, {fixture.Server.Events.CollisionLimitedBodies} limited bodies");
    }

    [Fact]
    public void CoincidentSolidBodiesStayWithinAuthoredPhysicalContactBudgets() {
        var source = DenseDocument(flocking: false, colliders: true, motionSeconds: 0f);
        var definition = source with {
            KitRowsRaw = [source.Kits[0] with { BodyContact = WorldBodyContactMode.Solid }],
        };
        using var fixture = Fixtures.FreshServer(definition);
        var population = fixture.Server.Population;
        var count = WorldBodiesLimits.CapacityCeiling - population.LocalSeatCount;
        Assert.Equal(count, population.SetSimulatedCount(count));
        Coincide(fixture);

        var start = Stopwatch.GetTimestamp();
        fixture.Step();
        var elapsed = Stopwatch.GetElapsedTime(start);
        var policy = definition.Collision.BodyContacts;

        output.WriteLine($"dense physical contacts: {count} coincident solids in {elapsed.TotalMilliseconds:F2} ms; "
            + $"{population.DynamicContactCandidates} candidates, {population.DynamicContactNarrowPairs} narrow, "
            + $"{population.DynamicContactResolvedPairs} resolved, {population.DynamicContactLimitedBodies} limited bodies");
        Assert.InRange(population.DynamicContactCandidates, 0, count * policy.CandidateBudget);
        Assert.InRange(population.DynamicContactResolvedPairs, 0, count * policy.MaxPairsPerBody / 2);
        Assert.True(population.DynamicContactLimitedBodies > 0);
    }

    [Fact]
    public void ExternalIntentPromotesABatchedCreatureImmediately() {
        using var fixture = Fixtures.FreshServer(DenseDocument(flocking: true, motionSeconds: 1f, steeringSeconds: 1f));
        Assert.Equal(64, fixture.Server.Population.SetSimulatedCount(count: 64));
        var index = fixture.Server.Population.LocalSeatCount + 63;
        var body = fixture.Server.Body(index)!;
        var before = body.FixedPosition;

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One));
        fixture.Step();

        Assert.NotEqual(expected: before, actual: body.FixedPosition);
    }

    [Fact]
    public void TimedChannelPressPromotesABatchedCreatureThroughItsLifetime() {
        using var fixture = Fixtures.FreshServer(DenseDocument(flocking: false, motionSeconds: 1f, steeringSeconds: 1f));
        Assert.Equal(64, fixture.Server.Population.SetSimulatedCount(count: 64));
        var body = fixture.Server.Body(fixture.Server.Population.LocalSeatCount + 63)!;
        var outcome = body.PressChannel(
            ordinal: 0,
            value: FixedQ4816.One,
            holdSeconds: 0.05f,
            authoredMaximum: FixedQ4816.FromInteger(value: 1L)
        );
        Assert.True(outcome.EffectiveHoldSeconds > FixedQ4816.Zero);

        var first = body.FixedPosition;
        fixture.Step();
        var second = body.FixedPosition;
        fixture.Step();
        var third = body.FixedPosition;

        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
    }

    [Fact]
    public void AutonomyCadenceRejectsUnsafeOrOutOfRangeAuthoring() {
        var document = DenseDocument(flocking: false);
        var solid = document with { KitRowsRaw = [document.Kits[0] with {
            AutonomyRaw = new WorldAutonomyCadence(MotionSeconds: 0.1f),
            BodyContact = WorldBodyContactMode.Solid,
            Collider = new WorldCollider.Sphere(Radius: 0.5f),
        }] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(solid, out var solidReason));
        Assert.Contains("must be 0 when bodyContact is Solid", solidReason);

        var tooSlow = document with { KitRowsRaw = [document.Kits[0] with {
            AutonomyRaw = new WorldAutonomyCadence(MotionSeconds: 1.01f),
        }] };
        Assert.False(WorldDefinitionValidator.TryValidateLocally(tooSlow, out var rangeReason));
        Assert.Contains("autonomy.motionSeconds", rangeReason);
    }

    [Fact]
    public void MaximumPopulationSnapshotFitsTheBoundedQuicFrame() {
        var entries = new EntitySnapshot[WorldBodiesLimits.CapacityCeiling];
        for (var index = 0; index < entries.Length; index++) {
            entries[index] = new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Index: index,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: new Vector3(index, 0f, 0f)
            );
        }
        var snapshot = new WorldSnapshot(Authority: "scale", Entries: entries, Revision: 1,
            StepTicks: Fixtures.StepTicks, Tick: 1UL);

        var encoded = WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot);

        output.WriteLine($"maximum population snapshot: {encoded.Length:N0} bytes");
        Assert.InRange(encoded.Length, 1, 512 * 1024);
        Assert.True(WorldFederationCodec.TryDecodeSnapshot(encoded, out var decoded, out var failure), failure.ToString());
        Assert.Equal(WorldBodiesLimits.CapacityCeiling, decoded.Entries.Length);
    }

    [Fact]
    public void AutonomousCadenceCheckpointResumesBitExactlyMidPhase() {
        var dense = DenseDocument();
        var document = dense with { PopulationRaw = dense.Population with { CapacityRaw = 68, NetworkPlayers = 64 } };
        using var fixture = Fixtures.FreshServer(document);
        Assert.Equal(64, fixture.Server.Population.SetSimulatedCount(count: 64));
        for (var tick = 0; tick < 13; tick++) { fixture.Step(); }
        Assert.True(fixture.Server.TryCaptureCheckpoint(
            hostRow: EmptyHostRow(),
            checkpoint: out var captured,
            reason: out var captureReason
        ), captureReason);
        var bytes = WorldAuthorityCheckpointCodec.Encode(captured!);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(bytes, out var decoded, out var decodeReason), decodeReason);

        for (var tick = 0; tick < 37; tick++) { fixture.Step(); }
        var expected = WorldRuntimeStateHash.HashAuthoritative(fixture.Server, tick: 0UL);

        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        for (var tick = 0; tick < 37; tick++) { fixture.Step(); }
        Assert.Equal(expected, WorldRuntimeStateHash.HashAuthoritative(fixture.Server, tick: 0UL));
    }
}

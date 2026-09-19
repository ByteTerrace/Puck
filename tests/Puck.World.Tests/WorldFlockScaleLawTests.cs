using System.Diagnostics;
using System.Numerics;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Whole-population acceptance evidence for the dense few-thousand-creature representation.</summary>
[Collection(AllocationCollection.Name)]
public sealed class WorldFlockScaleLawTests(ITestOutputHelper output) {
    private static WorldAuthorityHostRowCheckpoint EmptyHostRow() => new(
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
    private static WorldDefinition DenseDocument(float updateSeconds = 0.1f, bool flocking = true, bool colliders = false,
        float motionSeconds = (1f / 60f), float steeringSeconds = 0.1f) {
        var definition = Fixtures.BuildDocument();
        var profile = new WorldFlockProfile(
            Range: 20,
            SeparationRadius: 1,
            CandidateBudget: 32,
            MaxNeighbors: 16,
            UpdateSeconds: updateSeconds,
            Space: WorldFlockSpace.Tangent,
            Separation: 1,
            Alignment: 0.5f,
            Cohesion: 0.5f,
            Goal: 0,
            Inertia: 0.25f,
            ArrivalDistance: 0,
            HalfAngleDegrees: 180,
            RequiresLineOfSight: false
        );

        return definition with {
            PopulationRaw = definition.Population with {
                CapacityRaw = WorldBodiesLimits.CapacityCeiling,
                NetworkPlayers = (WorldBodiesLimits.CapacityCeiling - definition.Population.LocalSeats),
                DefaultPeerSourceRaw = (flocking
            ? IntentSource.Producer(name: "flock")
            : IntentSource.Idle),
            },
            BodyMotionProgramsRaw = [.. definition.BodyMotionPrograms, new(
                Name: "flock",
                Version: BodyMotionProgram.CurrentVersion,
                Kind: BodyProgramKind.Producer,
                Operations: [BodyMotionOp.ProduceFlockIntent]
            )],
            KitRowsRaw = [definition.Kits[0] with {
                AutonomyRaw = new WorldAutonomyCadence(
                MotionSeconds: motionSeconds,
                SteeringSeconds: steeringSeconds
            ),
                Collider = (colliders
            ? new WorldCollider.Sphere(Radius: 0.5f)
            : null),
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

    // The authority ticks an authored cadence spans, never less than one: a cadence shorter than a step runs every
    // step.
    private static int CadenceTicks(int rateHz, float seconds) => Math.Max(
        val1: 1,
        val2: ((int)MathF.Round(x: (seconds * rateHz)))
    );
    private static int PerTickShare(int population, int cadenceTicks) => (((population + cadenceTicks) - 1) / cadenceTicks);

    [Fact]
    public void FourThousandCoincidentCreaturesStayBoundedDeterministicAndAllocationStable() {
        using var fixture = Fixtures.FreshServer(DenseDocument());
        var expected = (WorldBodiesLimits.CapacityCeiling - fixture.Server.Population.LocalSeatCount);

        Assert.Equal(
            expected,
            fixture.Server.Population.SetSimulatedCount(expected)
        );
        Coincide(fixture: fixture);

        // A cadence authored in seconds spreads its population over that many authority ticks, so the per-tick
        // share is the population over the cadence's tick count, rounded up. Every ceiling below is derived from
        // the document's own rate and authored seconds, so the law holds at whatever rate the fixture runs.
        var rateHz = fixture.Server.Definition.SimulationRateHz;
        var flockCadenceTicks = CadenceTicks(
            rateHz: rateHz,
            seconds: 0.1f
        );
        var motionCadenceTicks = CadenceTicks(
            rateHz: rateHz,
            seconds: (1f / 60f)
        );
        var flockShare = PerTickShare(
            cadenceTicks: flockCadenceTicks,
            population: expected
        );

        fixture.Step();
        var burst = fixture.Server.Population.FlockStatistics;

        Assert.InRange(
            burst.Followers,
            1,
            (flockShare + 1)
        );
        Assert.InRange(
            burst.Updates,
            1,
            (flockShare + 1)
        );
        Assert.InRange(
            burst.Candidates,
            0,
            (expected * 32)
        );

        // Cover at least one whole perception cadence in both warmup and measurement. Every creature is still
        // present.
        var window = Math.Max(
            val1: 24,
            val2: flockCadenceTicks
        );

        for (var tick = 0; (tick < window); tick++) { fixture.Step(); }
        var candidates = 0;
        var retained = 0;
        var updates = 0;
        var samples = new long[window];
        var watch = Stopwatch.StartNew();

        for (var tick = 0; (tick < samples.Length); tick++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            fixture.Step();
            samples[tick] = (GC.GetAllocatedBytesForCurrentThread() - before);
            var work = fixture.Server.Population.FlockStatistics;

            candidates = Math.Max(
                val1: candidates,
                val2: work.Candidates
            );
            retained = Math.Max(
                val1: retained,
                val2: work.RetainedNeighbors
            );
            updates = Math.Max(
                val1: updates,
                val2: work.Updates
            );
        }
        watch.Stop();
        // The claim is the steady-state tick: the median holds it, and the widest tick is reported beside it so a
        // one-time lazy allocation under a parallel test run cannot fail the law on its own.
        var ordered = samples.Order().ToArray();
        var median = ordered[(ordered.Length / 2)];

        output.WriteLine(message: $"dense flock: {expected} coincident creatures, {window} ticks in {watch.Elapsed.TotalMilliseconds:F1} ms ({(watch.Elapsed.TotalMilliseconds / window):F2} ms/tick), median {median} thread bytes/tick, widest {ordered[^1]}");
        Assert.InRange(
            actual: candidates,
            high: (updates * 32),
            low: 0
        );
        Assert.InRange(
            actual: retained,
            high: (updates * 16),
            low: 0
        );
        // WorldDeadlineTable sweeps escrow, transfer, and park expiry from the front instead of walking every
        // population entry and LINQ-allocating the transfer sweep every tick, so a steady-state population with
        // nothing expiring costs a handful of bytes, not hundreds.
        Assert.InRange(
            actual: median,
            high: 64,
            low: 0
        );
        // A cadence deals its phases over the peer capacity, not the live count, so a tick's share sits within one
        // of the even split.
        Assert.InRange(
            fixture.Server.Population.AutonomyStatistics.MotionUpdates,
            ((expected / motionCadenceTicks) - 1),
            (PerTickShare(
                cadenceTicks: motionCadenceTicks,
                population: expected
            ) + 1)
        );
        Assert.InRange(
            fixture.Server.Population.AutonomyStatistics.SteeringUpdates,
            ((expected / flockCadenceTicks) - 1),
            (flockShare + 1)
        );

        var first = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 120
        );
        using var replay = Fixtures.FreshServer(DenseDocument());

        Assert.Equal(
            expected,
            replay.Server.Population.SetSimulatedCount(expected)
        );
        Coincide(fixture: replay);
        for (var tick = 0; (tick < 49); tick++) { replay.Step(); }
        Assert.Equal(
            first,
            WorldStateHashComposition.HashAuthoritative(
                server: replay.Server,
                tick: 120
            )
        );
    }
    [Fact]
    public void DenseIntegrationAndPerceptionCostsRemainVisible() {
        foreach (var sample in new[] {
            (Perception: 60f, Flocking: false, Motion: 0f, Steering: 0f),
            (Perception: 60f, Flocking: true, Motion: 0f, Steering: 0f),
            (Perception: 0.1f, Flocking: true, Motion: (1f / 60f), Steering: 0.1f),
        }) {
            using var fixture = Fixtures.FreshServer(DenseDocument(
                sample.Perception,
                sample.Flocking,
                motionSeconds: sample.Motion,
                steeringSeconds: sample.Steering
            ));
            var count = (WorldBodiesLimits.CapacityCeiling - fixture.Server.Population.LocalSeatCount);

            Assert.Equal(
                count,
                fixture.Server.Population.SetSimulatedCount(count)
            );
            Coincide(fixture: fixture);
            fixture.Step();
            for (var tick = 0; (tick < 20); tick++) { fixture.Step(); }
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();

            for (var tick = 0; (tick < 20); tick++) { fixture.Step(); }
            watch.Stop();
            output.WriteLine(message: $"dense flock={sample.Flocking} perception={sample.Perception}s motion={sample.Motion}s steering={sample.Steering}s: {(watch.Elapsed.TotalMilliseconds / 20):F2} ms/tick, {(GC.GetAllocatedBytesForCurrentThread() - before)} bytes");
        }
    }
    [Fact]
    public void DensePopulationPhaseCostsRemainVisible() {
        using var fixture = Fixtures.FreshServer(DenseDocument());
        var population = fixture.Server.Population;
        var count = (WorldBodiesLimits.CapacityCeiling - population.LocalSeatCount);

        Assert.Equal(
            count,
            population.SetSimulatedCount(count)
        );
        Coincide(fixture: fixture);
        for (ulong tick = 1; (tick <= 20); tick++) {
            population.AdvanceSimulated(
                tick,
                Fixtures.StepTicks,
                ((tick - 1) * Fixtures.StepTicks)
            );
            population.ResolveDynamicContacts();
            population.ResolveTethers();
            population.CompleteStep(tick: tick);
        }

        static TimeSpan Measure(Action action) {
            var start = Stopwatch.GetTimestamp();

            action();
            return Stopwatch.GetElapsedTime(startingTimestamp: start);
        }

        var advance = TimeSpan.Zero;
        var contacts = TimeSpan.Zero;
        var tethers = TimeSpan.Zero;
        var complete = TimeSpan.Zero;

        for (ulong tick = 21; (tick <= 40); tick++) {
            var start = ((tick - 1) * Fixtures.StepTicks);

            advance += Measure(action: () => population.AdvanceSimulated(
                tick,
                Fixtures.StepTicks,
                start
            ));
            contacts += Measure(action: population.ResolveDynamicContacts);
            tethers += Measure(action: population.ResolveTethers);
            complete += Measure(action: () => population.CompleteStep(tick: tick));
        }
        output.WriteLine(message: $"dense population phase mean: advance {(advance.TotalMilliseconds / 20):F2}, contacts {(contacts.TotalMilliseconds / 20):F2}, tethers {(tethers.TotalMilliseconds / 20):F2}, complete {(complete.TotalMilliseconds / 20):F2} ms");
    }
    [Fact]
    public void CoincidentColliderEventsStayBounded() {
        using var fixture = Fixtures.FreshServer(DenseDocument(
            flocking: false,
            colliders: true
        ));
        var population = fixture.Server.Population;
        var count = (WorldBodiesLimits.CapacityCeiling - population.LocalSeatCount);

        Assert.Equal(
            count,
            population.SetSimulatedCount(count)
        );
        Coincide(fixture: fixture);

        var first = Stopwatch.GetTimestamp();

        fixture.Step();
        var firstElapsed = Stopwatch.GetElapsedTime(startingTimestamp: first);

        Assert.InRange(
            fixture.Server.Events.CollisionTrackedPairs,
            1,
            ((count * fixture.Server.Definition.Collision.Events.MaxPairsPerBody) / 2)
        );

        var start = Stopwatch.GetTimestamp();

        for (var tick = 0; (tick < 20); tick++) { fixture.Step(); }
        var elapsed = Stopwatch.GetElapsedTime(startingTimestamp: start);

        output.WriteLine(message: $"dense collision events: first {firstElapsed.TotalMilliseconds:F2} ms; steady {(elapsed.TotalMilliseconds / 20):F2} ms/tick; {fixture.Server.Events.CollisionTrackedPairs} tracked, {fixture.Server.Events.CollisionCandidates} candidates, {fixture.Server.Events.CollisionLimitedBodies} limited bodies");
    }
    [Fact]
    public void CoincidentSolidBodiesStayWithinAuthoredPhysicalContactBudgets() {
        var source = DenseDocument(
            flocking: false,
            colliders: true,
            motionSeconds: 0f
        );
        var definition = source with {
            KitRowsRaw = [source.Kits[0] with { BodyContact = WorldBodyContactMode.Solid }],
        };
        using var fixture = Fixtures.FreshServer(definition);
        var population = fixture.Server.Population;
        var count = (WorldBodiesLimits.CapacityCeiling - population.LocalSeatCount);

        Assert.Equal(
            count,
            population.SetSimulatedCount(count)
        );
        Coincide(fixture: fixture);

        var start = Stopwatch.GetTimestamp();

        fixture.Step();
        var elapsed = Stopwatch.GetElapsedTime(startingTimestamp: start);
        var policy = definition.Collision.BodyContacts;

        output.WriteLine(message: (((string)$"dense physical contacts: {count} coincident solids in {elapsed.TotalMilliseconds:F2} ms; {population.DynamicContactCandidates} candidates, {population.DynamicContactNarrowPairs} narrow, ")
            + $"{population.DynamicContactResolvedPairs} resolved, {population.DynamicContactLimitedBodies} limited bodies"));
        Assert.InRange(
            population.DynamicContactCandidates,
            0,
            (count * policy.CandidateBudget)
        );
        Assert.InRange(
            population.DynamicContactResolvedPairs,
            0,
            ((count * policy.MaxPairsPerBody) / 2)
        );
        Assert.True(condition: (population.DynamicContactLimitedBodies > 0));
    }
    [Fact]
    public void ExternalIntentPromotesABatchedCreatureImmediately() {
        using var fixture = Fixtures.FreshServer(DenseDocument(
            flocking: true,
            motionSeconds: 1f,
            steeringSeconds: 1f
        ));

        Assert.Equal(
            64,
            fixture.Server.Population.SetSimulatedCount(count: 64)
        );
        var index = (fixture.Server.Population.LocalSeatCount + 63);
        var body = fixture.Server.Body(index: index)!;
        var before = body.FixedPosition;

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(
            ordinal: 0,
            value: FixedQ4816.One
        ));
        fixture.Step();

        Assert.NotEqual(
            expected: before,
            actual: body.FixedPosition
        );
    }
    [Fact]
    public void TimedChannelPressPromotesABatchedCreatureThroughItsLifetime() {
        using var fixture = Fixtures.FreshServer(DenseDocument(
            flocking: false,
            motionSeconds: 1f,
            steeringSeconds: 1f
        ));

        Assert.Equal(
            64,
            fixture.Server.Population.SetSimulatedCount(count: 64)
        );
        var body = fixture.Server.Body(index: (fixture.Server.Population.LocalSeatCount + 63))!;
        var outcome = body.PressChannel(
            ordinal: 0,
            value: FixedQ4816.One,
            holdSeconds: 0.05f,
            authoredMaximum: FixedQ4816.FromInteger(value: 1L)
        );

        Assert.True(condition: (outcome.EffectiveHoldSeconds > FixedQ4816.Zero));

        var first = body.FixedPosition;

        fixture.Step();
        var second = body.FixedPosition;

        fixture.Step();
        var third = body.FixedPosition;

        Assert.NotEqual(
            actual: second,
            expected: first
        );
        Assert.NotEqual(
            actual: third,
            expected: second
        );
    }
    [Fact]
    public void AutonomyCadenceRejectsUnsafeOrOutOfRangeAuthoring() {
        var document = DenseDocument(flocking: false);
        var solid = document with {
            KitRowsRaw = [document.Kits[0] with {
            AutonomyRaw = new WorldAutonomyCadence(MotionSeconds: 0.1f),
            BodyContact = WorldBodyContactMode.Solid,
            Collider = new WorldCollider.Sphere(Radius: 0.5f),
        }],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: solid,
            reason: out var solidReason
        ));
        Assert.Contains(
            actualString: solidReason,
            expectedSubstring: "must be 0 when bodyContact is Solid"
        );

        var tooSlow = document with {
            KitRowsRaw = [document.Kits[0] with {
            AutonomyRaw = new WorldAutonomyCadence(MotionSeconds: 1.01f),
        }],
        };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: tooSlow,
            reason: out var rangeReason
        ));
        Assert.Contains(
            actualString: rangeReason,
            expectedSubstring: "autonomy.motionSeconds"
        );
    }
    [Fact]
    public void MaximumPopulationSnapshotFitsTheBoundedQuicFrame() {
        var entries = new EntitySnapshot[WorldBodiesLimits.CapacityCeiling];

        for (var index = 0; (index < entries.Length); index++) {
            entries[index] = new EntitySnapshot(
                Active: true,
                BodyColor: Vector3.One,
                CatalogRig: 0,
                Continuity: EntityContinuity.Continuous,
                Index: index,
                Kit: 0,
                Look: 0,
                Orientation: Quaternion.Identity,
                Position: new Vector3(
                    x: index,
                    y: 0f,
                    z: 0f
                )
            );
        }
        var snapshot = new WorldSnapshot(
            Authority: "scale",
            Entries: entries,
            Revision: 1,
            StepTicks: Fixtures.StepTicks,
            Tick: 1UL
        );

        var encoded = WorldFederationCodec.EncodeSnapshot(snapshot: in snapshot);

        output.WriteLine(message: $"maximum population snapshot: {encoded.Length:N0} bytes");
        Assert.InRange(
            encoded.Length,
            1,
            (512 * 1024)
        );
        Assert.True(
            condition: WorldFederationCodec.TryDecodeSnapshot(
                body: encoded,
                failure: out var failure,
                snapshot: out var decoded
            ),
            userMessage: failure.ToString()
        );
        Assert.Equal(
            WorldBodiesLimits.CapacityCeiling,
            decoded.Entries.Length
        );
    }
    [Fact]
    public void AutonomousCadenceCheckpointResumesBitExactlyMidPhase() {
        var dense = DenseDocument();
        var document = dense with { PopulationRaw = dense.Population with { CapacityRaw = 68, NetworkPlayers = 64 } };
        using var fixture = Fixtures.FreshServer(document);

        Assert.Equal(
            64,
            fixture.Server.Population.SetSimulatedCount(count: 64)
        );
        for (var tick = 0; (tick < 13); tick++) { fixture.Step(); }
        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                hostRow: EmptyHostRow(),
                checkpoint: out var captured,
                reason: out var captureReason
            ),
            userMessage: captureReason
        );
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!);

        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: bytes,
                checkpoint: out var decoded,
                reason: out var decodeReason
            ),
            userMessage: decodeReason
        );

        for (var tick = 0; (tick < 37); tick++) { fixture.Step(); }
        var expected = WorldStateHashComposition.HashAuthoritative(
            fixture.Server,
            tick: 0UL
        );

        fixture.Server.RestoreCheckpoint(checkpoint: decoded!);
        for (var tick = 0; (tick < 37); tick++) { fixture.Step(); }
        Assert.Equal(
            expected,
            WorldStateHashComposition.HashAuthoritative(
                fixture.Server,
                tick: 0UL
            )
        );
    }
}

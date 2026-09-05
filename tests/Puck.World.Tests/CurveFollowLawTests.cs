using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Pins the sim curve-follow target source end to end: a body whose selected producer names
/// <see cref="BodyTargetSource.CurveFollow"/> travels off spawn while an otherwise-identical body with no producer
/// selected never moves, the driven run's per-tick state-hash trace is deterministic and diverges from that control,
/// a recorded run re-drives to an identical replay match, the travelled arc restarts at zero on a producer switch
/// (and switching back), that restart survives a checkpoint capture/restore under the same producer, and the
/// World-frame steering tracks a genuinely curved, closed path with the orbit scalar's effect observable.</summary>
public sealed class CurveFollowLawTests {
    private const string CurveRowName = "path";
    private const string FollowProgramName = "follow";
    private const string LoopCurveRowName = "loop";
    private const string LoopFollowProgramName = "followLoop";
    private const float LoopRadius = 5f;
    // Matches Fixtures' own WorldSimulationDefaults.RateHz (240) — every fixed-tick-count loop in this file already
    // assumes it (the "2 s" remarks below); named here only for the elapsed-seconds arithmetic the tracking law adds.
    private const float SimulationRateHz = 240f;
    // Fixtures' own SeatKitName kit: WorldMotion.Speed.Value, with one instant whole-vector shaping row (see
    // Fixtures.BuildKits' own remarks), so commanded planar velocity snaps to it exactly — no spring/damper
    // settling lag to account for in the tracking-tolerance derivation below.
    private const float MoveSpeed = 4f;

    // A zero-curvature open segment along +X — every §1.3 branch this fixture could hit collapses to the exact
    // straight cubic (the canonical "l = |chord| / 3" completion), so the compiled curve's arc-length coordinate and
    // the body's own world X agree exactly, with no oracle re-derivation needed to reason about where the follower
    // should be.
    private static WorldCurveRow StraightPath => new(
        Name: CurveRowName,
        Knots: [
            new WorldCurveKnot(Position: new DocumentVector3(x: 0f, y: 0f, z: 0f), TangentYaw: 0f, Curvature: 0f),
            new WorldCurveKnot(Position: new DocumentVector3(x: 20f, y: 0f, z: 0f), TangentYaw: 0f, Curvature: 0f),
        ],
        Closed: false
    );

    // A closed, genuinely curved path — three knots evenly spaced around a radius-5 circle: curvature exactly
    // 1/radius and a tangent perpendicular to the radius are exact by this construction (the same one
    // Puck.SdfVm.Tests.SdfCurvePathTests' ClosedCurve uses), so unlike StraightPath this exercises real curvature
    // and the closed wrap. TangentYaw is wrapped to the schema's canonical [-pi, pi] interval before authoring —
    // WorldDefinitionValidator.Curves refuses a knot outside it by name. The circle is centered at (-LoopRadius, 0)
    // rather than the origin so knot 0 sits exactly at Fixtures' own seat-1 spawn point (0, 0, 0) — the same
    // "target starts at spawn" shape StraightPath uses, so a short warmup still clears the standing-start catch-up
    // rather than measuring an unrelated initial straight-line gap to the circle.
    private static WorldCurveKnot LoopKnot(int index) {
        var turn = ((2f * MathF.PI) / 3f); // a full circle in three 120-degree arcs.
        var angle = (index * turn);

        return new WorldCurveKnot(
            Position: new DocumentVector3(x: (-LoopRadius + (LoopRadius * MathF.Cos(x: angle))), y: 0f, z: (LoopRadius * MathF.Sin(x: angle))),
            TangentYaw: WrapToPi(angle: (angle + (MathF.PI / 2f))),
            Curvature: (1f / LoopRadius)
        );
    }
    private static float WrapToPi(float angle) {
        const float twoPi = (2f * MathF.PI);
        var wrapped = (angle % twoPi);

        if (wrapped > MathF.PI) { wrapped -= twoPi; } else if (wrapped < -MathF.PI) { wrapped += twoPi; }

        return wrapped;
    }

    private static WorldCurveRow LoopPath => new(
        Name: LoopCurveRowName,
        Knots: [LoopKnot(index: 0), LoopKnot(index: 1), LoopKnot(index: 2)],
        Closed: true
    );

    // ProduceSteeringIntent's roam shape never governs this producer (SenseNearestInCone always finds the curve
    // point), but the op reads the full parameter set regardless of which shape a given tick takes.
    private static BodyProgramParameters FollowerProducerParameters(float orbit) => new(
        Scalars: new Dictionary<string, float> {
            ["standoffRadius"] = 0.1f,
            ["approach"] = 1f,
            ["orbit"] = orbit,
            ["altitudeGain"] = 0f,
            ["approachAltitudeGain"] = 0f,
            ["inwardGain"] = 3f,
            ["turnScale"] = 3f,
            ["forward"] = 0f,
            ["softRadius"] = 1f,
            ["weaveAmplitude"] = 0f,
            ["weaveFrequencyBase"] = 0f,
            ["weaveFrequencyRange"] = 0f,
            ["activityRateBase"] = 0f,
            ["activityRateRange"] = 0f,
            ["strafeWave"] = 0f,
            ["turnWave"] = 0f,
            ["upWave"] = 0f,
            ["pitchWave"] = 0f,
            ["rollTurn"] = 0f,
            ["pressThreshold"] = 0f,
            ["altitudeBase"] = 0f,
            ["altitudeRange"] = 0f,
        },
        Channels: new Dictionary<string, string>()
    );
    // Splices one curves row and a matching Producer-kind program (SenseNearestInCone + FaceSensorTarget +
    // ProduceSteeringIntent) onto Fixtures.BuildDocument() — the shared shape every test below boots from. The kit's
    // MoveFrame is World (Fixtures' default), so ProduceSteeringIntent drives the body directly at the sensed target's
    // bearing every tick; FaceSensorTarget's Turn write only ever reaches the drawn attitude under World, never the
    // translation basis (WorldBody.Step's ResolveYawAttitudeAndPlanarFrame). A body only actually runs the program
    // once its own intent source names it (see JoinFollower); the control tests in this file never do.
    private static WorldDefinition WithFollowerOn(WorldCurveRow curve, string programName, float rate, float orbit) {
        var document = (Fixtures.BuildDocument() with { CurvesRaw = [curve] });
        var kit = document.Kits[0];
        var followProgram = new BodyMotionProgram(
            Name: programName,
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.FaceSensorTarget, BodyMotionOp.ProduceSteeringIntent],
            Target: new BodyTargetSource.CurveFollow(Curve: curve.Name, Rate: rate)
        );

        return (document with {
            BodyMotionProgramsRaw = [.. document.BodyMotionPrograms, followProgram],
            KitRowsRaw = [kit with {
                ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                    [programName] = FollowerProducerParameters(orbit: orbit),
                },
            }],
        });
    }
    private static WorldDefinition WithFollower(float rate) => WithFollowerOn(curve: StraightPath, programName: FollowProgramName, rate: rate, orbit: 0f);
    private static WorldDefinition WithLoopFollower(float rate, float orbit = 0f) => WithFollowerOn(curve: LoopPath, programName: FollowProgramName, rate: rate, orbit: orbit);
    private static WorldBody JoinBody(WorldFixture fixture) {
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        return fixture.Server.Body(index: actor.Index)!;
    }
    // Joins the seat and selects the "follow" producer — nothing else in this file ever calls SubmitIntent, so the
    // body's whole per-tick movement comes from the staged producer output (WorldBody.Step's own merge: submitted
    // intent, when present, still outranks it — see BodyProducerSupport/WorldPopulation.Step's remarks — but nothing
    // here ever submits one).
    private static WorldBody JoinFollower(WorldFixture fixture) {
        var body = JoinBody(fixture: fixture);

        body.SetIntentSource(source: IntentSource.Producer(name: FollowProgramName));

        return body;
    }
    private static float PlanarDistance(FixedVector3 from, FixedVector3 to) {
        var delta = (to - from);

        return MathF.Sqrt(x: ((float)((double)((delta.X * delta.X) + (delta.Z * delta.Z)))));
    }

    [Fact]
    public void CurveFollowProducer_DrivesTheBodyOffSpawn_WhileANoProducerControlStaysPut() {
        using var followFixture = Fixtures.FreshServer(definition: WithFollower(rate: 2f));
        var followBody = JoinFollower(fixture: followFixture);
        var followSpawn = followBody.FixedPosition;

        for (var tick = 0; (tick < 480); tick++) {
            followFixture.Step();
        }

        var followDistance = PlanarDistance(from: followSpawn, to: followBody.FixedPosition);

        Assert.True(condition: (followDistance > 1f), userMessage: $"the curve-follow producer should have moved the body meaningfully off spawn after 2 s; moved {followDistance:0.###} units");

        // THE CONTROL: the identical document, but JoinBody alone (no SetIntentSource) leaves the seat's default
        // Live source with nothing ever submitted — WorldBody.Step's merge resolves that to a zero intent every
        // tick, so a body that never selects the curve-follow producer must not move at all.
        using var controlFixture = Fixtures.FreshServer(definition: WithFollower(rate: 2f));
        var controlBody = JoinBody(fixture: controlFixture);
        var controlSpawn = controlBody.FixedPosition;

        for (var tick = 0; (tick < 480); tick++) {
            controlFixture.Step();
        }

        var controlDistance = PlanarDistance(from: controlSpawn, to: controlBody.FixedPosition);

        Assert.True(condition: (controlDistance < 1e-4f), userMessage: $"the control (no producer selected) must not move; moved {controlDistance:0.######} units");
    }
    [Fact]
    public void CountCurveFollowers_ReportsTheSelectedBody_ButNotTheNoProducerControl() {
        using var followFixture = Fixtures.FreshServer(definition: WithFollower(rate: 2f));

        _ = JoinFollower(fixture: followFixture);
        followFixture.Step();

        Assert.Equal(expected: 1, actual: followFixture.Server.Population.CountCurveFollowers());

        using var controlFixture = Fixtures.FreshServer(definition: WithFollower(rate: 2f));

        _ = JoinBody(fixture: controlFixture);
        controlFixture.Step();

        Assert.Equal(expected: 0, actual: controlFixture.Server.Population.CountCurveFollowers());
    }
    [Fact]
    public void CurveFollowProducer_ProducesIdenticalHashTraces_WhileDivergingFromANoProducerControl() {
        var document = WithFollower(rate: 2f);
        var first = Fixtures.DriveHashTrace(document: document, ticks: 240, join: JoinFollower);
        var second = Fixtures.DriveHashTrace(document: document, ticks: 240, join: JoinFollower);

        Assert.Equal(actual: second, expected: first);

        var control = Fixtures.DriveHashTrace(document: document, ticks: 240, join: JoinBody);

        Assert.NotEqual(actual: control, expected: first);
    }
    [Fact]
    public void CurveFollowProducer_ReplayRecordingReDrivesToAnIdenticalMatch() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer(definition: WithFollower(rate: 2f));

        _ = JoinFollower(fixture: fixture);

        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [], addonHostFactory: static (_, _) => new NullAddonHost());
        var name = $"curve-follow-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        for (var tick = 0; (tick < 480); tick++) {
            fixture.Step();
            tape.NoteTick();
        }

        var result = tape.StopRecording();

        Assert.Null(@object: result.VerifyFault);
        Assert.NotNull(value: result.Verdict);
    }

    // The straight, zero-curvature StraightPath collapses arc length to world X exactly (see StraightPath's own
    // remarks), so the expected curve point at elapsed second t is (min(rate * t, 20), 0, 0) with no independent
    // spline evaluation needed.
    private static FixedVector3 ExpectedCurvePoint(float rate, float elapsedSeconds) => new(
        X: FixedQ4816.FromDouble(value: Math.Min(val1: (rate * elapsedSeconds), val2: 20f)),
        Y: FixedQ4816.Zero,
        Z: FixedQ4816.Zero
    );
    // Derived, not measured, from the pursuit's own bang-bang rule: engaged (distance > standoffRadius) the
    // pursuer closes at its full MoveSpeed while the target recedes at up to its own rate, so the gap can only
    // shrink once MoveSpeed exceeds rate; disengaged (distance <= standoffRadius) the pursuer holds and only the
    // target's own motion can grow the gap, undone the very next tick once the gate re-engages. Either actor's
    // single-tick displacement is bounded by MoveSpeed·dt (MoveSpeed already bounds rate in every case this file
    // authors), so the tracked distance can never sit further than standoffRadius plus one tick of that bound
    // outside it — a conservative, always-safe upper bound (the steady-state band this file has actually measured
    // sits well inside it), never the tightest one. A never-steers regression (the historical unrotated
    // World-frame bug this law exists to catch) drifts unboundedly over the window instead and blows through this
    // bound by orders of magnitude, so it stays fully discriminating.
    private static float TrackingTolerance(float standoffRadius) => (standoffRadius + (MoveSpeed / SimulationRateHz));

    [Fact]
    public void CurveFollowProducer_TracksTheCurvePointOnTrackAndAtTheAuthoredRate() {
        const float rate = 2f;
        const float standoffRadius = 0.1f;
        var trackingTolerance = TrackingTolerance(standoffRadius: standoffRadius);
        const int warmupTicks = 240; // 1 s clears the standing-start catch-up before the tracking window opens.
        const int windowTicks = 1200; // 5 s of steady pursuit, well inside the 10 s (20 / rate) straight segment.

        using var fixture = Fixtures.FreshServer(definition: WithFollower(rate: rate));
        var body = JoinFollower(fixture: fixture);

        for (var tick = 0; (tick < warmupTicks); tick++) {
            fixture.Step();
        }

        var windowStartX = ((float)((double)body.FixedPosition.X));
        var maxTrackingError = 0f;

        for (var tick = 0; (tick < windowTicks); tick++) {
            fixture.Step();

            var elapsedSeconds = (((warmupTicks + tick) + 1) / SimulationRateHz);
            var error = PlanarDistance(from: ExpectedCurvePoint(
                elapsedSeconds: elapsedSeconds,
                rate: rate
            ), to: body.FixedPosition);

            maxTrackingError = Math.Max(val1: maxTrackingError, val2: error);
        }

        Assert.True(condition: (maxTrackingError <= trackingTolerance), userMessage: $"the follower drifted {maxTrackingError:0.###} units from the travelling curve point (tolerance {trackingTolerance}) — a body that never steers at the target (only marches at a fixed world axis) drifts unboundedly instead");

        var windowEndX = ((float)((double)body.FixedPosition.X));
        var observedRate = ((windowEndX - windowStartX) / (windowTicks / SimulationRateHz));

        Assert.True(condition: (MathF.Abs(x: (observedRate - rate)) <= (rate * 0.02f)), userMessage: $"the follower's steady-state arc progress rate was {observedRate:0.######} u/s against an authored rate of {rate} u/s (2% tolerance) — a body driving in a fixed world direction never converges to the curve's own rate at all");
    }
    [Fact]
    public void CurveFollowProducer_TracksAGenuinelyCurvedClosedPathOnTrackAndAtTheAuthoredRate() {
        const float rate = 2f;
        const float standoffRadius = 0.1f;
        var trackingTolerance = TrackingTolerance(standoffRadius: standoffRadius);
        const int warmupTicks = 240; // 1 s clears the standing-start catch-up before the tracking window opens.
        const int windowTicks = 1200; // 5 s: ~12 units of a ~31.4-unit lap (2*pi*LoopRadius) at rate 2, no wrap.

        var document = WithLoopFollower(rate: rate);
        var compiled = document.Curves[0].Compiled;

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinFollower(fixture: fixture);

        for (var tick = 0; (tick < warmupTicks); tick++) {
            fixture.Step();
        }

        var maxTrackingError = 0f;

        for (var tick = 0; (tick < windowTicks); tick++) {
            fixture.Step();

            var elapsedSeconds = (((warmupTicks + tick) + 1) / SimulationRateHz);
            var expected = compiled.Evaluate(arcLength: FixedQ4816.FromDouble(value: (rate * elapsedSeconds))).Position;
            var error = PlanarDistance(from: expected, to: body.FixedPosition);

            maxTrackingError = Math.Max(val1: maxTrackingError, val2: error);
        }

        // No separate rate check is needed here (unlike the straight fixture's net-X-displacement check, which
        // cannot generalize to a path that curves back on itself): a SUSTAINED rate mismatch of either sign would
        // accumulate position error linearly over the whole 5 s window and blow through trackingTolerance well
        // before it ends, so maxTrackingError staying bounded across every sampled tick already proves the
        // follower's progress rate tracked the authored one, not only that its final position happened to agree.
        Assert.True(condition: (maxTrackingError <= trackingTolerance), userMessage: $"the follower drifted {maxTrackingError:0.###} units from the travelling curve point on a genuinely curved closed path (tolerance {trackingTolerance:0.###})");
    }

    // orbit is authored on the unit interval [0, 1] (WorldDefinitionValidator.Motion's RequireUnitInterval — there
    // is no negative orbit to author), so the observable law it owes is that the scalar is genuinely wired into the
    // World-frame steering rather than silently inert: it must visibly widen the tracked distance from the
    // standoff-gated orbit = 0 case, since a constant tangential bias fights the pursuer's own limited speed budget
    // for staying on the bearing line — never that the two runs converge to the identical trajectory.
    private static float MeanTrackingDistance(WorldDefinition document, float rate, int warmupTicks, int windowTicks) {
        var compiled = document.Curves[0].Compiled;

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinFollower(fixture: fixture);

        for (var tick = 0; (tick < warmupTicks); tick++) {
            fixture.Step();
        }

        var sum = 0f;

        for (var tick = 0; (tick < windowTicks); tick++) {
            fixture.Step();

            var elapsedSeconds = (((warmupTicks + tick) + 1) / SimulationRateHz);
            var target = compiled.Evaluate(arcLength: FixedQ4816.FromDouble(value: (rate * elapsedSeconds))).Position;

            sum += PlanarDistance(from: target, to: body.FixedPosition);
        }

        return (sum / windowTicks);
    }

    [Fact]
    public void CurveFollowProducer_OrbitScalarMeasurablyWidensTrackingDistance() {
        const float rate = 2f;
        const float orbit = 1f; // the scalar's own authored ceiling (RequireUnitInterval) — the largest, most
                                // detectable bias this fixture can author.
        const int warmupTicks = 240;
        const int windowTicks = 1200;

        var zeroOrbitDistance = MeanTrackingDistance(document: WithLoopFollower(orbit: 0f, rate: rate), rate: rate, warmupTicks: warmupTicks, windowTicks: windowTicks);
        var withOrbitDistance = MeanTrackingDistance(document: WithLoopFollower(orbit: orbit, rate: rate), rate: rate, warmupTicks: warmupTicks, windowTicks: windowTicks);

        // The standoff-gated approach term (bounded at MoveSpeed) dominates orbit's own bounded-at-1 tangential
        // bias whenever it is engaged, so orbit only measurably widens the tracked distance once the pursuer is
        // otherwise settled near standoffRadius — a real but small effect (observed ~0.005 units at the scalar's
        // own ceiling); the margin below is set with headroom under that observation, never at it, so an inert
        // (never-wired) orbit scalar — which would leave the two runs indistinguishable to the raw's own last bit —
        // still fails it clearly.
        Assert.True(condition: (withOrbitDistance > (zeroOrbitDistance + 0.002f)), userMessage: $"a nonzero orbit scalar must measurably widen the mean tracking distance versus orbit = 0 — an inert (never-wired) orbit scalar would leave the two indistinguishable; orbit = 0 mean {zeroOrbitDistance:0.######}, orbit = {orbit} mean {withOrbitDistance:0.######}");
    }

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
    private static long CurrentCurveArcRaw(WorldFixture fixture, int bodyIndex) {
        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: EmptyHostRow(), reason: out var reason), userMessage: reason);

        return checkpoint!.Population.Entries.Single(predicate: row => (row.Index == bodyIndex)).ProducerCurveArcRaw;
    }

    [Fact]
    public void CurveFollowProducer_SwitchingProducersRestartsTheArcAtZero_RatherThanResuming() {
        var straight = StraightPath;
        var loop = LoopPath;
        var document = (Fixtures.BuildDocument() with { CurvesRaw = [straight, loop] });
        var kit = document.Kits[0];
        var followStraight = new BodyMotionProgram(
            Name: FollowProgramName,
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.FaceSensorTarget, BodyMotionOp.ProduceSteeringIntent],
            Target: new BodyTargetSource.CurveFollow(Curve: straight.Name, Rate: 2f)
        );
        var followLoop = new BodyMotionProgram(
            Name: LoopFollowProgramName,
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.FaceSensorTarget, BodyMotionOp.ProduceSteeringIntent],
            Target: new BodyTargetSource.CurveFollow(Curve: loop.Name, Rate: 2f)
        );

        document = (document with {
            BodyMotionProgramsRaw = [.. document.BodyMotionPrograms, followStraight, followLoop],
            KitRowsRaw = [kit with {
                ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                    [FollowProgramName] = FollowerProducerParameters(orbit: 0f),
                    [LoopFollowProgramName] = FollowerProducerParameters(orbit: 0f),
                },
            }],
        });

        using var fixture = Fixtures.FreshServer(definition: document);
        var body = JoinFollower(fixture: fixture); // selects FollowProgramName, targeting "path".

        for (var tick = 0; (tick < 240); tick++) {
            fixture.Step();
        }

        var arcOnPathBeforeSwitch = CurrentCurveArcRaw(bodyIndex: 0, fixture: fixture);

        Assert.True(condition: (arcOnPathBeforeSwitch > 0L), userMessage: "the follower must have travelled before switching, or this law proves nothing");

        body.SetIntentSource(source: IntentSource.Producer(name: LoopFollowProgramName));
        fixture.Step(); // the transition tick: resets the shared accumulator to zero, then advances by one step.

        var arcOnLoopFirstTick = CurrentCurveArcRaw(bodyIndex: 0, fixture: fixture);

        Assert.True(condition: (arcOnLoopFirstTick < (arcOnPathBeforeSwitch / 4)), userMessage: $"selecting a different producer must restart its curve accumulator near zero rather than resume the prior producer's travelled arc; before switching {arcOnPathBeforeSwitch}, one tick after switching {arcOnLoopFirstTick}");

        for (var tick = 0; (tick < 9); tick++) {
            fixture.Step();
        }

        body.SetIntentSource(source: IntentSource.Producer(name: FollowProgramName));
        fixture.Step(); // switching back is itself a transition — the prior "path" travel is never re-matched.

        var arcOnPathAfterSwitchBack = CurrentCurveArcRaw(bodyIndex: 0, fixture: fixture);

        Assert.True(condition: (arcOnPathAfterSwitchBack < (arcOnPathBeforeSwitch / 4)), userMessage: $"switching back to the original producer must restart its arc at zero rather than resume where it was left off; before the first switch away {arcOnPathBeforeSwitch}, after switching back {arcOnPathAfterSwitchBack}");
    }
    [Fact]
    public void CurveFollowProducer_ArcAccumulatorSurvivesCheckpointRestoreUnderTheSameProducer() {
        using var fixture = Fixtures.FreshServer(definition: WithFollower(rate: 2f));

        _ = JoinFollower(fixture: fixture);

        for (var tick = 0; (tick < 240); tick++) {
            fixture.Step();
        }

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(checkpoint: out var checkpoint, hostRow: EmptyHostRow(), reason: out var reason), userMessage: reason);

        var capturedArcRaw = checkpoint!.Population.Entries.Single(predicate: row => (row.Index == 0)).ProducerCurveArcRaw;

        Assert.True(condition: (capturedArcRaw > 0L), userMessage: "the follower must have travelled before the checkpoint, or this law proves nothing");

        fixture.Server.RestoreCheckpoint(checkpoint: checkpoint!);

        fixture.Step();

        var arcRawAfterRestoreAndOneTick = CurrentCurveArcRaw(bodyIndex: 0, fixture: fixture);

        // Restoring under the SAME producer must resume the travelled arc — never re-latch as a fresh selection and
        // restart it at zero, which is exactly the hazard BodyProducerState.ActiveProducerName's own remarks name:
        // the restored producer identity must ride the checkpoint alongside ProducerCurveArcRaw itself, or the very
        // next tick's selection check would see no prior selection and reset the arc it is meant to be preserving.
        Assert.True(condition: (arcRawAfterRestoreAndOneTick >= capturedArcRaw), userMessage: $"restoring a checkpoint under the same producer must resume its travelled arc rather than restart it; captured {capturedArcRaw}, after restore + one tick {arcRawAfterRestoreAndOneTick}");
    }
}

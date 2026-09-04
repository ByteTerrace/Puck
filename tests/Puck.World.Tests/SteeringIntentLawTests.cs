using System.Numerics;

using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Motion;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// Contract under test: <see cref="BodyMotionOp.ProduceSteeringIntent"/> is one mechanism — an oscillator weave plus
/// a radial restoring term toward a reference, yaw-rate clamped — reachable through two runtime shapes. With no
/// sensed target the reference is the body's own home register (the roam shape); the restoring term steers the body
/// back once it strays past <c>softRadius</c>, and its own altitude term is measured along the body's resolved up
/// axis rather than baked to world Y.
/// </summary>
public sealed class SteeringIntentLawTests {
    private const string ProducerName = "roam";

    private static Dictionary<string, float> RoamScalars(float inwardGain) => new(collection: Fixtures.TravelerRoamParameters.Scalars) {
        ["weaveAmplitude"] = 0f,
        ["strafeWave"] = 0f,
        ["turnWave"] = 0f,
        ["upWave"] = 0f,
        ["pitchWave"] = 0f,
        ["rollTurn"] = 0f,
        ["softRadius"] = 2f,
        ["inwardGain"] = inwardGain,
        ["turnScale"] = 2.5f,
        ["forward"] = 1f,
    };
    private static WorldDefinition RoamDocument(float inwardGain) {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];

        return document with {
            KitRowsRaw = [kit with {
                ProducersRaw = new Dictionary<string, BodyProgramParameters>(collection: kit.Producers) {
                    [ProducerName] = Fixtures.TravelerRoamParameters with { Scalars = RoamScalars(inwardGain: inwardGain) },
                },
            }],
        };
    }
    private static WorldBody JoinAndPoseAwayFromHome(WorldFixture fixture, float distance) {
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        // Home is fixed at activation (the spawn point); moving the body afterward never moves it. Facing -Z (yaw
        // 0) while placed on -Z drives the body further from home under a zero restoring gain — the divergent
        // straight line the discriminating case below relies on.
        body.Pose(x: 0f, y: (float)(double)body.FixedPosition.Y, z: -distance, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        return body;
    }
    private static float PlanarDistanceToHome(WorldBody body) {
        var home = body.FixedHome;
        var position = body.FixedPosition;
        var dx = (float)(double)(position.X - home.X);
        var dz = (float)(double)(position.Z - home.Z);

        return MathF.Sqrt(x: ((dx * dx) + (dz * dz)));
    }

    [Fact]
    public void ARestoringGainConvergesTheBodyBackTowardHome_WhileAZeroGainOnlyRecedes() {
        const float startDistance = 20f;
        const int ticks = 2400;

        using var restoring = Fixtures.FreshServer(definition: RoamDocument(inwardGain: 1.6f));
        var restoringBody = JoinAndPoseAwayFromHome(fixture: restoring, distance: startDistance);
        restoringBody.SetIntentSource(source: IntentSource.Producer(name: ProducerName));

        for (var tick = 0; (tick < ticks); tick++) {
            restoring.Step();
        }

        var restoringDistance = PlanarDistanceToHome(body: restoringBody);

        using var control = Fixtures.FreshServer(definition: RoamDocument(inwardGain: 0f));
        var controlBody = JoinAndPoseAwayFromHome(fixture: control, distance: startDistance);
        controlBody.SetIntentSource(source: IntentSource.Producer(name: ProducerName));

        for (var tick = 0; (tick < ticks); tick++) {
            control.Step();
        }

        var controlDistance = PlanarDistanceToHome(body: controlBody);

        // THE CONTROL: a zero restoring gain leaves the oscillator-disabled roam driving a straight line away from
        // home forever — it must never converge.
        Assert.True(
            condition: (controlDistance > startDistance),
            userMessage: $"a zero restoring gain must keep receding from home; started at {startDistance}, ended at {controlDistance}"
        );
        Assert.True(
            condition: (restoringDistance < startDistance),
            userMessage: $"a positive restoring gain must converge the body back toward home; started at {startDistance}, ended at {restoringDistance}"
        );
        Assert.True(
            condition: (restoringDistance < controlDistance),
            userMessage: $"the restoring gain must leave the body measurably closer to home than the non-restoring control; restoring={restoringDistance}, control={controlDistance}"
        );
    }

    // --- The altitude term, measured along a tilted resolved up. ---

    private const int MoveUpOrdinal = 5;
    private const float FlyerAltitudeGain = 0.32f;

    private static readonly WorldChannel[] FlyerChannels = [
        new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
        new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
        new(Name: "pitch", Shape: ChannelShape.Bipolar, Role: ChannelRole.Pitch),
        new(Name: "roll", Shape: ChannelShape.Bipolar, Role: ChannelRole.Roll),
        new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn),
        new(Name: "up", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveUp),
    ];
    // ResolveYawAttitudeAndPlanarFrame runs first and is the one op that resolves m_up from ambient gravity
    // (WorldBody.Step.cs's ResolveUp) — included here only so a fixture body actually carries a tilted up to
    // measure against; IntegrateLocalAttitude overwrites its orientation write immediately after, which is the
    // only reason ProduceSteeringIntent's altitude branch fires at all (it gates on the KIT'S motion program
    // containing IntegrateLocalAttitude).
    private static readonly BodyMotionProgram FlyerFreeProgram = new(
        Name: "free",
        Version: "puck.body-motion.v1",
        Kind: BodyProgramKind.Motion,
        Operations: [
            BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
            BodyMotionOp.IntegrateLocalAttitude,
            BodyMotionOp.ResolveHold,
            BodyMotionOp.ComputeLocalTargetVelocity,
            BodyMotionOp.RunActionTriggers,
            BodyMotionOp.ApplyHold,
            BodyMotionOp.IntegrateScratchVelocity,
            BodyMotionOp.CommitPose,
        ]
    );
    // A free-flight kit naming FlyerFreeProgram — callers differ only in which producer program/scalars they set
    // through ProducersRaw.
    private static WorldKit FlyerKit() => new(
        Name: "flyer-test",
        BodyMotionProgram: FlyerFreeProgram.Name,
        Motion: new WorldMotion(
            Speed: new WorldSpeed(Value: 4f),
            Turn: new WorldTurn(Rate: 2.5f),
            Holds: [
                new WorldHold(
                    Bond: BodyHoldBond.Free,
                    Gravity: new WorldHoldGravity(Fall: 23f, Rise: 14f),
                    Hold: BodyHoldKind.Lift,
                    Lift: 1f,
                    Name: "air"
                ),
            ]
        ),
        ProducersRaw: new Dictionary<string, BodyProgramParameters>(),
        Collider: new WorldCollider.Capsule(Endpoint: new Vector3(x: 0f, y: 1f, z: 0f), Radius: 0.35f)
    );
    private static WorldDefinition FlyerDocument() {
        var roam = new BodyMotionProgram(Name: ProducerName, Version: "puck.body-motion.v1", Kind: BodyProgramKind.Producer, Operations: [BodyMotionOp.ProduceSteeringIntent]);
        var kit = FlyerKit() with {
            ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                [ProducerName] = Fixtures.TravelerRoamParameters with {
                    Scalars = new Dictionary<string, float>(collection: Fixtures.TravelerRoamParameters.Scalars) {
                        ["forward"] = 0f,
                        ["softRadius"] = 1_000_000f,
                        ["altitudeGain"] = FlyerAltitudeGain,
                    },
                },
            },
        };

        return Fixtures.BuildDocument() with {
            BodyMotionProgramsRaw = [FlyerFreeProgram, roam],
            ChannelsRaw = FlyerChannels,
            DefaultSeatKitRaw = kit.Name,
            KitRowsRaw = [kit],
        };
    }
    private static WorldBody JoinAndPose(WorldFixture fixture, float x, float y, float z) {
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        body.Pose(x: x, y: y, z: z, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        return body;
    }

    [Fact]
    public void TheAltitudeTermIsMeasuredAlongTheBodysResolvedUp_NotBakedToWorldY() {
        const float gravityMagnitude = 46f;
        // An arbitrary tilt away from world +Y — the ordinary UnitY-up world this producer ran under before the
        // altitude term generalized is the special case where AlongUp and .Y agree exactly.
        var tiltedGravity = new DocumentVector3(x: (-gravityMagnitude * 0.6f), y: (-gravityMagnitude * 0.8f), z: 0f);
        var document = FlyerDocument() with {
            GravityRaw = new WorldGravity(
                Attractors: [],
                GravitationalConstant: 0f,
                SofteningLength: 0.5f,
                Solver: WorldGravitySolver.Pairwise,
                Uniform: tiltedGravity
            ),
        };

        using var fixture = Fixtures.FreshServer(definition: document);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: actor,
            Slot: actor.Index,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: actor.Index)!;

        // A position with both a Y and an off-axis component, small enough that neither formula below saturates
        // its clamp (which would make two genuinely different pre-clamp values compare equal): at the origin, or
        // wherever .Y already agrees with Dot(position, up), the two coincide by coincidence, proving nothing.
        body.Pose(x: 3f, y: 1f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        // Settle the ambient up axis onto the tilted field's own direction before ever running the producer — the
        // kit's Free/Lift hold never grounds, so SurfaceFollowing plays no part; ambient solved gravity alone steers
        // m_up here (WorldBody.Step.cs's ResolveUp).
        for (var tick = 0; (tick < 480); tick++) {
            fixture.Step();
        }

        var up = body.FixedUp;

        Assert.NotEqual(
            expected: FixedQ4816.One,
            actual: up.Y
        );

        // Captured immediately before the one tick that runs the producer — the exact position/up the term reads,
        // since integration moves the body a (tiny, but nonzero) step within that same tick.
        var position = body.FixedPosition;

        body.SetIntentSource(source: IntentSource.Producer(name: ProducerName));
        fixture.Step();

        var producedMoveUp = body.EngagedIntent[MoveUpOrdinal];
        var alongUp = FixedVector3.Dot(
            left: position,
            right: up
        );
        var expected = FixedQ4816.Clamp(
            value: (-alongUp * FixedQ4816.FromDouble(value: FlyerAltitudeGain)),
            minimum: (-FixedQ4816.One),
            maximum: FixedQ4816.One
        );
        // THE CONTROL: the world-Y-baked formula this term replaced — the wrong answer under a tilted up, and the
        // value a regression back to it would produce.
        var bakedToWorldY = FixedQ4816.Clamp(
            value: (-position.Y * FixedQ4816.FromDouble(value: FlyerAltitudeGain)),
            minimum: (-FixedQ4816.One),
            maximum: FixedQ4816.One
        );

        Assert.Equal(
            expected: expected,
            actual: producedMoveUp
        );
        Assert.NotEqual(
            expected: bakedToWorldY,
            actual: producedMoveUp
        );
    }

    // --- The roam shape's oscillator runs every tick, including while the approach shape governs. ---

    private const string SensingProducerName = "stalk";

    // Every roam contribution but the oscillator weave zeroed (inwardGain 0 so the restoring term never reads
    // position/yaw; forward/strafeWave/turnWave/upWave/pitchWave/rollTurn/altitudeGain 0 so nothing but the
    // phase-driven Turn write is observable), and a softRadius large enough the restoring branch never opens even
    // if position drifted. The approach shape's own scalars are zeroed too (standoffRadius irrelevant since
    // approach/orbit/approachAltitudeGain are all 0), so a governed tick writes an all-zero Intent — invisible in the
    // trajectory — while the roam shape's phase state keeps advancing underneath it.
    private static Dictionary<string, float> IsolatedPhaseScalars() => new() {
        ["forward"] = 0f,
        ["softRadius"] = 1_000_000f,
        ["weaveAmplitude"] = 0.7f,
        ["inwardGain"] = 0f,
        ["turnScale"] = 2f,
        ["weaveFrequencyBase"] = 0.35f,
        ["weaveFrequencyRange"] = 0f,
        ["altitudeGain"] = 0f,
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
    };
    private static Dictionary<string, float> IsolatedPhaseScalarsWithApproach() => new(collection: IsolatedPhaseScalars()) {
        ["standoffRadius"] = 1f,
        ["approach"] = 0f,
        ["orbit"] = 0f,
        ["approachAltitudeGain"] = 0f,
        // Sensed hysteresis: releaseRadius > the target source's own range >= standoffRadius (WorldDefinitionValidator.Motion.cs).
        ["releaseRadius"] = 2000f,
    };
    // A bare roam producer — no sensing, so ProduceSteeringIntent's roam shape governs every tick and its Turn
    // write is directly observable throughout.
    private static WorldDefinition RoamOnlyIsolatedPhaseDocument() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];

        return document with {
            KitRowsRaw = [kit with {
                ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                    [ProducerName] = Fixtures.TravelerRoamParameters with { Scalars = IsolatedPhaseScalars() },
                },
            }],
        };
    }
    // Senses any other active body within a generous cone/range, so a second body placed nearby governs the
    // approach shape until it is moved out of range.
    private static WorldDefinition SensingIsolatedPhaseDocument() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var sensingProgram = new BodyMotionProgram(
            Name: SensingProducerName,
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Producer,
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.ProduceSteeringIntent],
            Target: new BodyTargetSource.Sensed(Scope: BodyTargetScope.Bodies, Range: 1000f, HalfAngleDegrees: 180f, RequiresLineOfSight: false)
        );

        return document with {
            BodyMotionProgramsRaw = [.. document.BodyMotionPrograms, sensingProgram],
            // The ONLY producer this kit declares: SeedProducer (WorldPopulation.Step.cs) seeds a seat's oscillator
            // from the first producer whose program selects ProduceSteeringIntent, so a second one (the base kit's
            // own "roam") would seed WeaveFrequency from ITS scalars instead of this one's — a divergent phase
            // that has nothing to do with the property under test.
            KitRowsRaw = [kit with {
                ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                    [SensingProducerName] = Fixtures.TravelerRoamParameters with { Scalars = IsolatedPhaseScalarsWithApproach() },
                },
            }],
        };
    }
    private static WorldBody JoinSeat(WorldFixture fixture, int slot, float x, float z) {
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: WorldPrincipal.Seat(slot: slot),
            Slot: slot,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);

        var body = fixture.Server.Body(index: slot)!;

        body.Pose(x: x, y: (float)(double)body.FixedPosition.Y, z: z, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        return body;
    }

    [Fact]
    public void TheRoamOscillatorAdvancesEveryTickRegardlessOfWhichShapeGoverns() {
        const int sensedTicks = 37;
        const int postLossTicks = 53;

        // T2 — a pure roam producer never governs anything but the roam shape; its Turn write is a direct,
        // continuous readout of sin(Phase) for the whole run. It shares slot 0's deterministic phase/weave seed
        // with T3 and T4 below (WorldSequenceSampling.FixedAngle keyed only by slot index).
        using var pureRoam = Fixtures.FreshServer(definition: RoamOnlyIsolatedPhaseDocument());
        var pureRoamBody = JoinSeat(fixture: pureRoam, slot: 0, x: 0f, z: 0f);

        pureRoamBody.SetIntentSource(source: IntentSource.Producer(name: ProducerName));

        for (var tick = 0; (tick < sensedTicks); tick++) {
            pureRoam.Step();
        }

        var pureRoamYawAtHandoff = pureRoamBody.FixedYaw;

        for (var tick = 0; (tick < postLossTicks); tick++) {
            pureRoam.Step();
        }

        var pureRoamYawAtEnd = pureRoamBody.FixedYaw;

        // T4 — the alternative a phase FROZEN during sensing would predict: a fresh body roaming for exactly the
        // post-loss tick count, starting from the SAME seeded phase T3 starts from (never having run through a
        // sensed window at all).
        using var freshRoam = Fixtures.FreshServer(definition: RoamOnlyIsolatedPhaseDocument());
        var freshRoamBody = JoinSeat(fixture: freshRoam, slot: 0, x: 0f, z: 0f);

        freshRoamBody.SetIntentSource(source: IntentSource.Producer(name: ProducerName));

        for (var tick = 0; (tick < postLossTicks); tick++) {
            freshRoam.Step();
        }

        var freshRoamYawAfterPostLossTicks = freshRoamBody.FixedYaw;

        // T3 — the case under test: a sensed target governs the approach shape (a zeroed Intent — invisible in the
        // trajectory) for sensedTicks, then is moved out of range so the roam shape resumes for postLossTicks.
        using var stalk = Fixtures.FreshServer(definition: SensingIsolatedPhaseDocument());
        var hunter = JoinSeat(fixture: stalk, slot: 0, x: 0f, z: 0f);
        var prey = JoinSeat(fixture: stalk, slot: 1, x: 1f, z: 0f);

        hunter.SetIntentSource(source: IntentSource.Producer(name: SensingProducerName));

        for (var tick = 0; (tick < sensedTicks); tick++) {
            stalk.Step();
        }

        // THE CONTROL: the approach shape's own Intent is all-zero (approach/orbit/approachAltitudeGain 0), so the sensed
        // window leaves the hunter's yaw exactly where it started — proving the sensed window is otherwise inert
        // and any divergence below can only come from the (invisible) oscillator state.
        Assert.Equal(
            expected: FixedQ4816.Zero,
            actual: hunter.FixedYaw
        );

        prey.Pose(x: 5000f, y: (float)(double)prey.FixedPosition.Y, z: 5000f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);

        for (var tick = 0; (tick < postLossTicks); tick++) {
            stalk.Step();
        }

        var stalkYawAtEnd = hunter.FixedYaw;
        // The hunter's yaw is 0 at the handoff tick (the sensed-window control above), so its own post-loss delta
        // is stalkYawAtEnd unmodified; the pure-roam control's Turn writes accumulate from tick 1, so its matching
        // delta is measured from its own handoff reading.
        var pureRoamPostLossDelta = (pureRoamYawAtEnd - pureRoamYawAtHandoff);

        // THE FIX: with the oscillator advancing every tick regardless of dispatch, the hunter's post-loss roam
        // resumes from the SAME phase the pure-roam control reached at the handoff tick, so the yaw ACCUMULATED
        // over the identical postLossTicks window matches (the two bodies' absolute yaw differs, since the
        // pure-roam control's Turn writes were live — and observable — during the sensed window too, while the
        // hunter's were held at the approach shape's all-zero Intent; only the Turn accumulated AFTER the handoff
        // isolates the oscillator's own state). A few ULP of Q16.16 tolerance: subtracting two independently
        // rounded 90-tick and 37-tick accumulations is not bit-identical to a 53-tick accumulation from zero.
        const double phaseContinuityToleranceRadians = 0.001;

        Assert.True(
            condition: (Math.Abs(value: ((double)pureRoamPostLossDelta - (double)stalkYawAtEnd)) < phaseContinuityToleranceRadians),
            userMessage: $"the hunter's post-loss yaw delta ({(double)stalkYawAtEnd}) must match the pure-roam control's ({(double)pureRoamPostLossDelta}) within {phaseContinuityToleranceRadians} rad"
        );
        // THE DISCRIMINATING CONTROL: a phase frozen for the whole sensed window (the bug this law guards against)
        // would instead reproduce the fresh-roam trajectory — proving the tolerance above is not so loose the
        // assertion is vacuously true.
        Assert.True(
            condition: (Math.Abs(value: ((double)freshRoamYawAfterPostLossTicks - (double)stalkYawAtEnd)) > (phaseContinuityToleranceRadians * 10)),
            userMessage: $"the hunter's post-loss yaw ({(double)stalkYawAtEnd}) must measurably diverge from the frozen-phase prediction ({(double)freshRoamYawAfterPostLossTicks})"
        );
        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: pureRoamYawAtHandoff
        );
    }

    // --- The roam shape runs only when the producer authors its own scalars — never unconditionally on selecting the op. ---

    // Senses nothing, ever (the fixture joins one body alone and the sensed range is far below any distance it
    // could occupy), and authors only the approach shape's own scalars — no roam scalar at all, so
    // CompiledBodyProducer.RoamActive is false and ProduceRoamIntent never runs.
    private static WorldDefinition ApproachOnlyFlyerDocument(float approachAltitudeGain = 0.5f, float sensedRange = 0.01f) {
        var approach = new BodyMotionProgram(
            Name: ProducerName,
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Producer,
            Target: new BodyTargetSource.Sensed(Scope: BodyTargetScope.Bodies, Range: sensedRange, HalfAngleDegrees: 180f, RequiresLineOfSight: false),
            Operations: [BodyMotionOp.SenseNearestInCone, BodyMotionOp.ProduceSteeringIntent, BodyMotionOp.FaceSensorTarget]
        );
        var kit = FlyerKit() with {
            ProducersRaw = new Dictionary<string, BodyProgramParameters> {
                [ProducerName] = new(
                    Scalars: new Dictionary<string, float> {
                        ["standoffRadius"] = 0.005f,
                        ["approach"] = 0f,
                        ["orbit"] = 0f,
                        ["approachAltitudeGain"] = approachAltitudeGain,
                        ["inwardGain"] = 1.6f,
                        ["turnScale"] = 2.5f,
                        ["releaseRadius"] = (sensedRange * 2f),
                    },
                    Channels: new Dictionary<string, string>()
                ),
            },
        };

        return FlyerDocument() with {
            BodyMotionProgramsRaw = [FlyerFreeProgram, approach],
            KitRowsRaw = [kit],
        };
    }

    [Fact]
    public void ApproachOnlyProducer_HoldsForeverOnAnUnsensedTick_WhereARoamAuthoringProducerHomesToItsSeededAltitude() {
        using var approachOnly = Fixtures.FreshServer(definition: ApproachOnlyFlyerDocument());
        var approachOnlyBody = JoinAndPose(fixture: approachOnly, x: 3f, y: 1f, z: 0f);

        approachOnlyBody.SetIntentSource(source: IntentSource.Producer(name: ProducerName));

        for (var tick = 0; (tick < 30); tick++) {
            approachOnly.Step();

            Assert.Equal(
                expected: FixedQ4816.Zero,
                actual: approachOnlyBody.EngagedIntent[MoveUpOrdinal]
            );
        }

        // THE CONTROL: the same free-flight kit and off-origin pose, but FlyerDocument's own producer DOES author
        // the roam scalars (and never senses at all) — an unsensed tick must home toward its seeded altitude
        // instead of holding, proving the approach-only case above holds BECAUSE it omits the roam scalars, not
        // because nothing here can ever produce a nonzero MoveUp.
        using var roaming = Fixtures.FreshServer(definition: FlyerDocument());
        var roamingBody = JoinAndPose(fixture: roaming, x: 3f, y: 1f, z: 0f);

        roamingBody.SetIntentSource(source: IntentSource.Producer(name: ProducerName));
        roaming.Step();

        Assert.NotEqual(
            expected: FixedQ4816.Zero,
            actual: roamingBody.EngagedIntent[MoveUpOrdinal]
        );
    }

    [Fact]
    public void ApproachAltitudeGain_GovernsTheSensedShapeIndependentlyOfTheOmittedRoamGain() {
        static FixedQ4816 ProducedMoveUp(float gain) {
            using var fixture = Fixtures.FreshServer(definition: ApproachOnlyFlyerDocument(approachAltitudeGain: gain, sensedRange: 100f));
            var follower = JoinSeat(fixture: fixture, slot: 0, x: 0f, z: 0f);
            var target = JoinSeat(fixture: fixture, slot: 1, x: 0f, z: 1f);

            follower.SetIntentSource(source: IntentSource.Producer(name: ProducerName));
            fixture.Step();

            // Preserve the producer's seeded preferred altitude, then move only the follower away from it while a
            // target remains sensed. The approach shape's vertical term is now the only possible MoveUp writer.
            follower.Pose(x: 0f, y: 10f, z: 0f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
            target.Pose(x: 0f, y: 0f, z: 1f, yawRadians: 0f, pitchRadians: 0f, rollRadians: 0f);
            fixture.Step();

            return follower.EngagedIntent[MoveUpOrdinal];
        }

        var disabled = ProducedMoveUp(gain: 0f);
        var enabled = ProducedMoveUp(gain: 0.5f);

        Assert.Equal(expected: FixedQ4816.Zero, actual: disabled);
        Assert.NotEqual(expected: FixedQ4816.Zero, actual: enabled);
    }
}

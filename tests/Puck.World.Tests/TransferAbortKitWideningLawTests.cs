using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.Physics.Motion;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the WIDENED <see cref="WorldBody.TransferState"/> — the abort capture must be complete
/// for a MEDIUM-hold (Dive) kit body and a DRIVE-row (Kart) kit body, not just the row-less
/// shape <see cref="TransferAbortDynamicStateLawTests"/> already covers. Each law here builds its own MINIMAL but
/// REAL drive/medium kit (motion row, channels, and one authored action exercising the action-track/action-state
/// seams), drives the body into a genuinely non-rest state across EVERY newly captured field, then proves the
/// detach/restore round trip reproduces every one of them exactly. <c>Puck.World</c> (the composition root) is out
/// of reach for this project — see <see cref="TransferAbortDynamicStateLawTests"/>'s own remarks — so this proves
/// the SAME primitive that suite does, widened to the two seat kits the finding named as making several of these
/// fields live for an ordinary seat.
/// </summary>
public sealed class TransferAbortKitWideningLawTests {
    // Every ordinal these laws address channels by, spelled once so PressChannel's raw-ordinal calls (which need no
    // kit binding at all — see TransferAbortDynamicStateLawTests' own remarks) and the authored channel table agree.
    private const int ForwardOrdinal = 0;
    private const int StrafeOrdinal = 1;
    private const int TurnOrdinal = 2;
    private const int SurgeOrdinal = 3; // the ONE composition channel bound to a real kit action below.
    private const int UpOrdinal = 4; // the medium kit only — trails "surge" so SurgeOrdinal addresses the same channel in both fixtures.
    private const int UntimedPressOrdinal = 10; // an ordinal NO channel declares — PressChannel needs no binding.
    private const int TimedPressOrdinal = 11; // likewise, distinct from UntimedPressOrdinal.

    private static WorldDefinition BuildDriveKitDocument() {
        var channels = new WorldChannel[] {
            new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
            new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
            new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn),
            new(Name: "surge", Shape: ChannelShape.Binary, Composition: true),
        };

        var driveGround = new BodyMotionProgram(
            Name: "drive-ground",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveDriveFrame,
                BodyMotionOp.ShapeDriveVelocity,
                BodyMotionOp.RunActionTriggers,
                BodyMotionOp.ApplyVerticalGravity,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        // A second, otherwise-identical program under a DIFFERENT name — the ONLY thing
        // BodyMotionProgramName's own law needs: a name distinct from the kit's own default to switch TO and prove
        // the switch survives an abort/restore.
        var driveGroundAlt = driveGround with { Name = "drive-ground-alt" };
        var wander = new BodyMotionProgram(Name: "wander", Version: "puck.body-motion.v1", Kind: BodyProgramKind.Producer, Operations: [BodyMotionOp.ProduceWanderIntent]);

        var kit = new WorldKit(
            Name: "kart-test",
            BodyMotionProgram: "drive-ground",
            Motion: new WorldMotion(
                MoveSpeed: 16f,
                TurnSpeed: 2.4f,
                RiseGravity: 14f,
                FallGravity: 26f,
                MaxFallSpeed: 30f,
                SprintMultiplier: 1f,
                MoveSpeedEnvelope: new MotionScalarEnvelope(Max: 16f, Min: 16f),
                Drive: new WorldDrive(
                    Accel: 7f,
                    Brake: 18f,
                    Coast: 4f,
                    Grip: 22f,
                    SteerReferenceSpeed: 4f,
                    SteerFalloff: 0.55f,
                    ReverseSpeed: 5f,
                    PitchRate: 0f
                )
            ),
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["wander"] = Fixtures.TravelerWanderParameters,
            },
            ActionsRaw: new Dictionary<string, ActionSpec> {
                // ONE authored action, bound OnFact only (no OnPress/OnRelease needed) — exercises the lane action
                // runtime's own edge-latch/FactHeld (LaneLatch/LaneFactHeld) AND a DURABLE action-state write
                // (ActionStateValues/Dirty/DirtyKind/DirtyOperand) through the SAME RunActionTriggers path real
                // gameplay drives. Gated on Falling (vertical velocity < 0, program-agnostic — see WorldBody.FactHolds)
                // rather than Grounded: this fixture's kit carries no collider, so gravity alone free-falls the body
                // from tick 1 (the SAME reason PortalSweepOriginLawTests' own fixture never grounds), making Falling
                // the reliable, immediately-true fact to gate on.
                ["surge"] = new ActionSpec(
                    OnPress: null,
                    OnRelease: null,
                    OnFact: [new ActionFactTrigger(Fact: ActionFact.Falling, Effects: [new ActionEffect.AddState(State: "surgeCounter", Value: 1m)], Mode: ActionTriggerMode.Edge)]
                ),
            },
            Collider: null
        );

        return Fixtures.BuildDocument() with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [driveGround, driveGroundAlt, wander],
            KitRowsRaw = [kit],
            DefaultSeatKitRaw = "kart-test",
            StateRaw = new WorldStateSection(World: Fixtures.BuildDocument().State, Identity: [new ActionStateSlot(Name: "surgeCounter", Kind: ActionStateKind.Counter, Initial: 0f)]),
        };
    }
    private static WorldDefinition BuildMediumKitDocument() {
        // "surge" stays at SurgeOrdinal (index 3), matching the drive kit's own layout above — "up" trails it so
        // the shared ordinal constants below address the same channel regardless of which fixture built the body.
        var channels = new WorldChannel[] {
            new(Name: "forward", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveAdvance),
            new(Name: "strafe", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveStrafe),
            new(Name: "turn", Shape: ChannelShape.Bipolar, Role: ChannelRole.Turn),
            new(Name: "surge", Shape: ChannelShape.Binary, Composition: true),
            new(Name: "up", Shape: ChannelShape.Bipolar, Role: ChannelRole.MoveUp),
        };

        var medium = new BodyMotionProgram(
            Name: "medium",
            Version: "puck.body-motion.v1",
            Kind: BodyProgramKind.Motion,
            Operations: [
                BodyMotionOp.ResolveYawAttitudeAndPlanarFrame,
                BodyMotionOp.ResolveHold,
                BodyMotionOp.ComputePlanarTargetVelocity,
                BodyMotionOp.ShapePlanarVelocity,
                BodyMotionOp.RunActionTriggers,
                BodyMotionOp.ApplyHold,
                BodyMotionOp.IntegratePlanarAndVerticalVelocity,
                BodyMotionOp.CommitPose,
            ]
        );
        var mediumAlt = medium with { Name = "medium-alt" };
        var wander = new BodyMotionProgram(Name: "wander", Version: "puck.body-motion.v1", Kind: BodyProgramKind.Producer, Operations: [BodyMotionOp.ProduceWanderIntent]);

        var kit = new WorldKit(
            Name: "diver-test",
            BodyMotionProgram: "medium",
            Motion: new WorldMotion(
                MoveSpeed: 3.2f,
                TurnSpeed: 2.2f,
                RiseGravity: 1f,
                FallGravity: 1f,
                MaxFallSpeed: 1f,
                SprintMultiplier: 1f,
                // Row 0 gates on "Recently Rising" — driving the Up channel positive for a few ticks makes Rising
                // hold, which THIS row's own Recently clock then reflects (WorldBody.MotionRecency's own capture).
                // Row 1 is the always-row (no gate), required last.
                Response: [
                    new MotionResponse(EngageRate: 9f, ReleaseRate: 5f, Gate: new ActionPredicate.Recently(Fact: ActionFact.Rising, WindowSeconds: 1f)),
                    new MotionResponse(EngageRate: 7f, ReleaseRate: 3.5f),
                ],
                Holds: [
                    new WorldHold(
                        Bond: BodyHoldBond.Medium,
                        Hold: BodyHoldKind.None,
                        Medium: new WorldHoldMedium(
                            Buoyancy: 0.5f,
                            FloatDepth: 1f,
                            MaxRiseSpeed: 2.4f,
                            MaxSinkSpeed: 3f,
                            SurfaceSettleRate: 6f,
                            ThrustFraction: 0.75f
                        ),
                        Name: "water"
                    ),
                ]
            ),
            ProducersRaw: new Dictionary<string, BodyProgramParameters> {
                ["wander"] = Fixtures.TravelerWanderParameters,
            },
            ActionsRaw: new Dictionary<string, ActionSpec> {
                // Mirrors the drive kit's own "surge" action above, gated on Rising instead of Falling (a diver
                // thrusting upward is the reliable, easily-driven fact here — Falling would work too, but Rising is
                // what this law already drives for MotionRecency, so one drive proves both).
                ["surge"] = new ActionSpec(
                    OnPress: null,
                    OnRelease: null,
                    OnFact: [new ActionFactTrigger(Fact: ActionFact.Rising, Effects: [new ActionEffect.AddState(State: "surgeCounter", Value: 1m)], Mode: ActionTriggerMode.Edge)]
                ),
            },
            Collider: null
        );

        return Fixtures.BuildDocument() with {
            ChannelsRaw = channels,
            BodyMotionProgramsRaw = [medium, mediumAlt, wander],
            KitRowsRaw = [kit],
            DefaultSeatKitRaw = "diver-test",
            // REQUIRED: WorldDefinitionValidator refuses a kit authoring a Medium hold when the world authors no
            // medium lattice row. A lattice covering every spawn point with plenty of margin (the body drifts only a few
            // simulation-tick-widths of distance over this law's short drive) at heightScale 5, value 1 reproduces
            // the SAME surface Y (5) the old waterline fixture floated bodies against.
            StateRaw = new WorldStateSection(
                World: [
                    .. Fixtures.BuildDocument().State,
                    Fixtures.MediumRow(),
                ],
                Identity: [new ActionStateSlot(Name: "surgeCounter", Kind: ActionStateKind.Counter, Initial: 0f)],
                Lattices: [
                    new WorldStateLatticeTopology(
                        Name: "world",
                        Origin: new DocumentVector3(x: -10f, y: 0f, z: -10f),
                        CellSize: 1f,
                        Width: 20,
                        Depth: 20,
                        Layers: 1
                    ),
                ]
            ),
        };
    }

    [Fact]
    public void DetachThenRestore_DriveKitBody_EveryNewlyCapturedFieldRoundTripsExactly() {
        using var fixture = Fixtures.FreshServer(definition: BuildDriveKitDocument());
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var population = fixture.Server.Population;
        var body = fixture.Server.Body(index: actor.Index)!;

        // Drive genuinely non-rest state across EVERY newly captured field.
        body.SetIntentSource(source: IntentSource.Idle); // Source — a fresh restore body always defaults to Live.

        // BOTH of these route through CommitTeleport (Pose ALWAYS; SetBodyMotionProgram only when the name actually
        // changes — see that method's own remarks), which resets the drive accumulators as part of its ordinary
        // "a teleport must not carry momentum" contract (WorldBody.ResetVertical's own remarks). They MUST run
        // BEFORE the velocity-driving loop below, or they would silently wipe the very accumulator/velocity state
        // this law exists to prove survives a round trip.
        var programSwitched = body.SetBodyMotionProgram(programName: "drive-ground-alt");

        Assert.True(condition: programSwitched);

        // DrivePitch — forced directly through the SAME public Pose() a hard teleport/warp already uses (this
        // kit's own PitchRate is 0, so nothing in ordinary play would move it; a future flying-variant kit would —
        // see WorldBody.TransferState's own remarks on why this is captured regardless of today's grounded-only
        // seat kits).
        body.Pose(position: body.FixedPosition, yawRadians: body.FixedYaw, pitchRadians: FixedQ4816.FromDouble(value: 0.2), rollRadians: FixedQ4816.Zero);

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One).WithChannel(ordinal: TurnOrdinal, value: FixedQ4816.One));

        for (var tick = 0; (tick < 12); tick++) {
            fixture.Step(); // drive longitudinal/lateral/residual accumulators ramp; gravity falls (no collider); the "surge" OnFact action fires once on the Falling edge.
        }

        // A live timed press (existing coverage, re-proven under a drive row) and an UNTIMED tap staged but not
        // yet materialized (MaterializeDefaultLanePresses only runs at the NEXT Advance) — captured with NO Step in
        // between, so the pending-tap fields are genuinely still pending at capture time.
        var pressOutcome = body.PressChannel(ordinal: TimedPressOrdinal, value: FixedQ4816.One, holdSeconds: 5f, authoredMaximum: FixedQ4816.FromInteger(value: 60));

        Assert.Equal(expected: PressHoldCapKind.None, actual: pressOutcome.CapKind);

        body.PressChannel(ordinal: UntimedPressOrdinal, value: FixedQ4816.One);
        body.EnqueueRun(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One), seconds: 0.5f);

        var capturedPosition = body.FixedPosition;
        var capturedYaw = body.FixedYaw;
        var capturedOrientation = body.FixedOrientation;
        var capturedState = body.CaptureTransferState();

        // Prove non-triviality BEFORE the round trip — a discriminating case, per this suite's own established
        // doctrine (TransferAbortDynamicStateLawTests' own remarks): every one of these must be NONZERO/non-default,
        // or the round-trip assertions below would pass just as well against a body that never carried the state at
        // all.
        Assert.Equal(expected: "drive-ground-alt", actual: capturedState.BodyMotionProgramName);
        Assert.Equal(expected: IntentSource.Idle, actual: capturedState.Source);
        Assert.NotEqual(expected: FixedQ4816.Zero, actual: capturedState.DrivePitch);
        Assert.True(condition: capturedState.PreviousChannelBit[ForwardOrdinal], userMessage: "the forward channel was held above threshold on the last Advance before capture");
        Assert.True(condition: capturedState.PendingDefaultChannelPress[UntimedPressOrdinal], userMessage: "the untimed tap must still be pending (not yet materialized) at capture");
        Assert.Equal(expected: FixedQ4816.One, actual: capturedState.PendingDefaultChannelValue[UntimedPressOrdinal]);
        Assert.True(condition: (capturedState.ChannelTimerTicks[TimedPressOrdinal] > 0), userMessage: "the timed press must have a live remaining-ticks countdown");
        Assert.NotEqual(expected: 0L, actual: capturedState.PlanarRampRemainder | capturedState.DriveLongRemainder | capturedState.DriveLatRemainder | capturedState.DriveResidualRemainder);
        Assert.True(condition: (capturedState.LaneFactHeld[SurgeOrdinal] != 0UL), userMessage: "the surge action's OnFact edge (Falling) must be latched held by now");
        // LaneLatch (the OnPress pending-press buffer, distinct from LaneFactHeld's OnFact edge bit — see
        // WorldBody.LaneActionRuntime's own field remarks) legitimately stays 0 here: "surge" binds OnFact only, no
        // OnPress. Round-tripped below regardless (whatever value it holds, zero included, must survive exactly).
        Assert.True(condition: (capturedState.ActionStateValues[0] > FixedQ4816.Zero), userMessage: "surgeCounter must have incremented from the Falling edge firing");
        // ActionStateDirty/DirtyKind/DirtyOperand legitimately read false/default here: WorldPopulation.CompleteStep
        // drains every body's durable dirty flags UNCONDITIONALLY at the end of EVERY tick (see its own remarks) —
        // and a transfer's mutation-drain can only ever run BETWEEN ticks, after that drain already completed for
        // the just-finished one. So in THIS engine's real architecture the triple is always false/default at any
        // point an abort could ever observe it; capturing it is still correct (harmless, round-trips whatever it
        // holds) but does not prove a live gap the way the fields above do — round-tripped below regardless.
        Assert.True(condition: (capturedState.TapeIntents.Length > 0), userMessage: "the scripted tape must still hold the enqueued segment");
        Assert.True(condition: (capturedState.TapeRemainingTicks[0] > 0));

        // LEAVE, then ABORT — the ordinary transfer detach/restore primitive.
        Assert.True(condition: population.TryDetachSeatForTransfer(slot: actor.Index, profile: out var profile));
        Assert.True(condition: population.RestoreDetachedSeat(slot: actor.Index, profile: profile, position: capturedPosition, yawRadians: capturedYaw, dynamicState: capturedState));

        var restoredBody = fixture.Server.Body(index: actor.Index)!;

        Assert.Equal(expected: capturedPosition, actual: restoredBody.FixedPosition);
        Assert.Equal(expected: capturedYaw, actual: restoredBody.FixedYaw);
        Assert.Equal(expected: capturedOrientation, actual: restoredBody.FixedOrientation);

        var restoredState = restoredBody.CaptureTransferState();

        Assert.Equal(expected: capturedState.PlanarVelocity, actual: restoredState.PlanarVelocity);
        Assert.Equal(expected: capturedState.VerticalVelocity, actual: restoredState.VerticalVelocity);
        Assert.Equal(expected: capturedState.Orientation, actual: restoredState.Orientation);
        Assert.Equal(expected: capturedState.DrivePitch, actual: restoredState.DrivePitch);
        Assert.Equal(expected: capturedState.BodyMotionProgramName, actual: restoredState.BodyMotionProgramName);
        Assert.Equal(expected: capturedState.Source, actual: restoredState.Source);
        Assert.Equal(expected: capturedState.PreviousChannelBit, actual: restoredState.PreviousChannelBit);
        Assert.Equal(expected: capturedState.PendingDefaultChannelPress, actual: restoredState.PendingDefaultChannelPress);
        Assert.Equal(expected: capturedState.PendingDefaultChannelValue, actual: restoredState.PendingDefaultChannelValue);
        Assert.Equal(expected: capturedState.ChannelTimerTicks[TimedPressOrdinal], actual: restoredState.ChannelTimerTicks[TimedPressOrdinal]);
        Assert.Equal(expected: capturedState.ChannelTimerValues[TimedPressOrdinal], actual: restoredState.ChannelTimerValues[TimedPressOrdinal]);
        Assert.Equal(expected: capturedState.PlanarRampRemainder, actual: restoredState.PlanarRampRemainder);
        Assert.Equal(expected: capturedState.DriveLongRemainder, actual: restoredState.DriveLongRemainder);
        Assert.Equal(expected: capturedState.DriveLatRemainder, actual: restoredState.DriveLatRemainder);
        Assert.Equal(expected: capturedState.DriveResidualRemainder, actual: restoredState.DriveResidualRemainder);
        Assert.Equal(expected: capturedState.LaneLatch, actual: restoredState.LaneLatch);
        Assert.Equal(expected: capturedState.LaneFactHeld, actual: restoredState.LaneFactHeld);
        Assert.Equal(expected: capturedState.ActionStateValues, actual: restoredState.ActionStateValues);
        Assert.Equal(expected: capturedState.ActionStateTimers, actual: restoredState.ActionStateTimers);
        Assert.Equal(expected: capturedState.ActionStateDirty, actual: restoredState.ActionStateDirty);
        Assert.Equal(expected: capturedState.ActionStateDirtyKind, actual: restoredState.ActionStateDirtyKind);
        Assert.Equal(expected: capturedState.ActionStateDirtyOperand, actual: restoredState.ActionStateDirtyOperand);
        Assert.Equal(expected: capturedState.TapeIntents, actual: restoredState.TapeIntents);
        Assert.Equal(expected: capturedState.TapeRemainingTicks, actual: restoredState.TapeRemainingTicks);

        // Structural, not a gap: a drive kit shapes planar velocity through its own row and authors no response
        // table, so RecencySlots is 0 — MotionRecency is legitimately empty here, proven by the MEDIUM law below
        // instead.
        Assert.Empty(collection: capturedState.MotionRecency);
        Assert.Empty(collection: restoredState.MotionRecency);

        Assert.False(condition: population.IsSeatParked(slot: actor.Index));
    }
    [Fact]
    public void DetachThenRestore_MediumKitBody_EveryNewlyCapturedFieldRoundTripsExactly() {
        using var fixture = Fixtures.FreshServer(definition: BuildMediumKitDocument());
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var population = fixture.Server.Population;
        var body = fixture.Server.Body(index: actor.Index)!;

        body.SetIntentSource(source: IntentSource.Idle);

        // MUST run BEFORE the velocity-driving loop below — see the drive law's own remarks on why (CommitTeleport
        // resets the medium ramp accumulator as part of its ordinary "a teleport must not carry momentum" contract).
        var programSwitched = body.SetBodyMotionProgram(programName: "medium-alt");

        Assert.True(condition: programSwitched);

        body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One).WithChannel(ordinal: UpOrdinal, value: FixedQ4816.One));

        for (var tick = 0; (tick < 12); tick++) {
            fixture.Step(); // the medium's thrust ramp accumulates; Up-channel thrust makes Rising hold, refreshing the Recently(Rising) response row's clock and firing the "surge" OnFact action once on the edge.
        }

        var pressOutcome = body.PressChannel(ordinal: TimedPressOrdinal, value: FixedQ4816.One, holdSeconds: 5f, authoredMaximum: FixedQ4816.FromInteger(value: 60));

        Assert.Equal(expected: PressHoldCapKind.None, actual: pressOutcome.CapKind);

        body.PressChannel(ordinal: UntimedPressOrdinal, value: FixedQ4816.One);
        body.EnqueueRun(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One), seconds: 0.5f);

        var capturedPosition = body.FixedPosition;
        var capturedYaw = body.FixedYaw;
        var capturedOrientation = body.FixedOrientation;
        var capturedState = body.CaptureTransferState();

        Assert.Equal(expected: "medium-alt", actual: capturedState.BodyMotionProgramName);
        Assert.Equal(expected: IntentSource.Idle, actual: capturedState.Source);
        Assert.True(condition: capturedState.PreviousChannelBit[ForwardOrdinal]);
        Assert.True(condition: capturedState.PendingDefaultChannelPress[UntimedPressOrdinal]);
        Assert.Equal(expected: FixedQ4816.One, actual: capturedState.PendingDefaultChannelValue[UntimedPressOrdinal]);
        Assert.True(condition: (capturedState.ChannelTimerTicks[TimedPressOrdinal] > 0));
        Assert.NotEqual(expected: 0L, actual: capturedState.MediumThrustRampRemainder);
        // MotionRecency — the MEDIUM kit's own proof (the drive law above documents why it cannot prove this):
        // Response row 0's Recently(Rising) clock must have refreshed from the driven Up-channel thrust.
        Assert.NotEmpty(collection: capturedState.MotionRecency);
        Assert.True(condition: (capturedState.MotionRecency[0] > 0UL), userMessage: "the Recently(Rising) response row's clock must have refreshed while Rising held");
        Assert.True(condition: (capturedState.LaneFactHeld[SurgeOrdinal] != 0UL));
        // LaneLatch legitimately stays 0 here — see the drive law's own remarks (OnFact carries no OnPress latch).
        Assert.True(condition: (capturedState.ActionStateValues[0] > FixedQ4816.Zero), userMessage: "surgeCounter must have incremented from the Rising edge firing");
        // ActionStateDirty/DirtyKind/DirtyOperand — see the drive law's own remarks on why these legitimately read
        // false/default at any capture point in this engine's real architecture (WorldPopulation.CompleteStep's own
        // unconditional per-tick drain). Round-tripped below regardless.
        Assert.True(condition: (capturedState.TapeIntents.Length > 0));
        Assert.True(condition: (capturedState.TapeRemainingTicks[0] > 0));

        Assert.True(condition: population.TryDetachSeatForTransfer(slot: actor.Index, profile: out var profile));
        Assert.True(condition: population.RestoreDetachedSeat(slot: actor.Index, profile: profile, position: capturedPosition, yawRadians: capturedYaw, dynamicState: capturedState));

        var restoredBody = fixture.Server.Body(index: actor.Index)!;

        Assert.Equal(expected: capturedPosition, actual: restoredBody.FixedPosition);
        Assert.Equal(expected: capturedYaw, actual: restoredBody.FixedYaw);
        Assert.Equal(expected: capturedOrientation, actual: restoredBody.FixedOrientation);

        var restoredState = restoredBody.CaptureTransferState();

        Assert.Equal(expected: capturedState.PlanarVelocity, actual: restoredState.PlanarVelocity);
        Assert.Equal(expected: capturedState.VerticalVelocity, actual: restoredState.VerticalVelocity);
        Assert.Equal(expected: capturedState.Orientation, actual: restoredState.Orientation);
        Assert.Equal(expected: capturedState.BodyMotionProgramName, actual: restoredState.BodyMotionProgramName);
        Assert.Equal(expected: capturedState.Source, actual: restoredState.Source);
        Assert.Equal(expected: capturedState.PreviousChannelBit, actual: restoredState.PreviousChannelBit);
        Assert.Equal(expected: capturedState.PendingDefaultChannelPress, actual: restoredState.PendingDefaultChannelPress);
        Assert.Equal(expected: capturedState.PendingDefaultChannelValue, actual: restoredState.PendingDefaultChannelValue);
        Assert.Equal(expected: capturedState.ChannelTimerTicks[TimedPressOrdinal], actual: restoredState.ChannelTimerTicks[TimedPressOrdinal]);
        Assert.Equal(expected: capturedState.ChannelTimerValues[TimedPressOrdinal], actual: restoredState.ChannelTimerValues[TimedPressOrdinal]);
        Assert.Equal(expected: capturedState.MediumThrustRampRemainder, actual: restoredState.MediumThrustRampRemainder);
        Assert.Equal(expected: capturedState.MotionRecency, actual: restoredState.MotionRecency);
        Assert.Equal(expected: capturedState.LaneLatch, actual: restoredState.LaneLatch);
        Assert.Equal(expected: capturedState.LaneFactHeld, actual: restoredState.LaneFactHeld);
        Assert.Equal(expected: capturedState.ActionStateValues, actual: restoredState.ActionStateValues);
        Assert.Equal(expected: capturedState.ActionStateTimers, actual: restoredState.ActionStateTimers);
        Assert.Equal(expected: capturedState.ActionStateDirty, actual: restoredState.ActionStateDirty);
        Assert.Equal(expected: capturedState.ActionStateDirtyKind, actual: restoredState.ActionStateDirtyKind);
        Assert.Equal(expected: capturedState.ActionStateDirtyOperand, actual: restoredState.ActionStateDirtyOperand);
        Assert.Equal(expected: capturedState.TapeIntents, actual: restoredState.TapeIntents);
        Assert.Equal(expected: capturedState.TapeRemainingTicks, actual: restoredState.TapeRemainingTicks);

        Assert.False(condition: population.IsSeatParked(slot: actor.Index));
    }

    // BREAK-ONCE PROOF (adversarial-review methodology, recorded here rather than left as permanent test scaffolding):
    // both laws above were verified RED by temporarily dropping ONE field from WorldBody.ApplyTransferState's own
    // restore body (m_source = state.Source removed) and confirming BOTH laws failed on the Source assertion with
    // the restored value reading IntentSource.Live instead of the captured Idle — proving the law actually
    // discriminates a missing restore rather than trivially passing regardless. Reverted immediately after
    // confirming red; the law suite as committed is the GREEN state.

    private static WorldDynamicsRow Settle => new(Damping: 1f, Frequency: 2f, Name: "settle", Response: 0f);

    [Fact]
    public void DetachThenRestore_GroundedDynamicsKitBody_PlanarFollowerStateRoundTripsExactly() {
        var document = Fixtures.BuildDocument();
        var kit = document.Kits[0];
        var motion = kit.Motion;

        document = document with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, Settle],
            KitRowsRaw = [kit with { Motion = motion with { Response = null, Dynamics = "settle" } }],
        };

        using var fixture = Fixtures.FreshServer(definition: document);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var population = fixture.Server.Population;
        var body = fixture.Server.Body(index: actor.Index)!;

        // The submitted intent is a one-tick image, consumed by each Advance — resubmitted every tick so the
        // follower's own previous-target carry (which the SETTLE round trip below proves) stays genuinely nonzero
        // rather than reverting to the idle (zero) target after the first step.
        for (var tick = 0; (tick < 24); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One));
            fixture.Step();
        }

        var capturedPosition = body.FixedPosition;
        var capturedYaw = body.FixedYaw;
        var capturedState = body.CaptureTransferState();

        // Non-triviality, BEFORE the round trip — driven along the body's own facing (-Z at rest), never assumed to
        // land on a particular lane.
        Assert.True(condition: ((capturedState.PlanarFollowerPositionRawX | capturedState.PlanarFollowerPositionRawY | capturedState.PlanarFollowerPositionRawZ) != 0L), userMessage: "the driven planar follower must have moved off rest before capture");
        Assert.NotEqual(expected: FixedVector3.Zero, actual: capturedState.PlanarFollowerPreviousTarget);

        Assert.True(condition: population.TryDetachSeatForTransfer(slot: actor.Index, profile: out var profile));
        Assert.True(condition: population.RestoreDetachedSeat(slot: actor.Index, profile: profile, position: capturedPosition, yawRadians: capturedYaw, dynamicState: capturedState));

        var restoredState = fixture.Server.Body(index: actor.Index)!.CaptureTransferState();

        Assert.Equal(expected: capturedState.PlanarFollowerPositionRawX, actual: restoredState.PlanarFollowerPositionRawX);
        Assert.Equal(expected: capturedState.PlanarFollowerPositionRawY, actual: restoredState.PlanarFollowerPositionRawY);
        Assert.Equal(expected: capturedState.PlanarFollowerPositionRawZ, actual: restoredState.PlanarFollowerPositionRawZ);
        Assert.Equal(expected: capturedState.PlanarFollowerVelocityRawX, actual: restoredState.PlanarFollowerVelocityRawX);
        Assert.Equal(expected: capturedState.PlanarFollowerVelocityRawY, actual: restoredState.PlanarFollowerVelocityRawY);
        Assert.Equal(expected: capturedState.PlanarFollowerVelocityRawZ, actual: restoredState.PlanarFollowerVelocityRawZ);
        Assert.Equal(expected: capturedState.PlanarFollowerPreviousTarget, actual: restoredState.PlanarFollowerPreviousTarget);
    }
    [Fact]
    public void DetachThenRestore_MediumDynamicsKitBody_VerticalFollowerStateRoundTripsExactly() {
        var document = BuildMediumKitDocument();
        var kit = document.Kits[0];
        var motion = kit.Motion;

        document = document with {
            DynamicsRaw = [.. Fixtures.StandardDynamics, Settle],
            KitRowsRaw = [kit with { Motion = motion with { Response = null, Dynamics = "settle" } }],
        };

        using var fixture = Fixtures.FreshServer(definition: document);
        var actor = WorldPrincipal.Seat(slot: 0);

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: actor, Slot: actor.Index, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var population = fixture.Server.Population;
        var body = fixture.Server.Body(index: actor.Index)!;

        for (var tick = 0; (tick < 24); tick++) {
            body.SubmitIntent(intent: default(PlayerIntent).WithChannel(ordinal: ForwardOrdinal, value: FixedQ4816.One).WithChannel(ordinal: UpOrdinal, value: FixedQ4816.One));
            fixture.Step(); // both the planar and the vertical follower rise toward their commanded targets under the SAME compiled propagator.
        }

        var capturedPosition = body.FixedPosition;
        var capturedYaw = body.FixedYaw;
        var capturedState = body.CaptureTransferState();

        Assert.NotEqual(expected: 0L, actual: capturedState.VerticalFollowerPositionRaw);
        Assert.NotEqual(expected: FixedQ4816.Zero, actual: capturedState.VerticalFollowerPreviousTarget);

        Assert.True(condition: population.TryDetachSeatForTransfer(slot: actor.Index, profile: out var profile));
        Assert.True(condition: population.RestoreDetachedSeat(slot: actor.Index, profile: profile, position: capturedPosition, yawRadians: capturedYaw, dynamicState: capturedState));

        var restoredState = fixture.Server.Body(index: actor.Index)!.CaptureTransferState();

        Assert.Equal(expected: capturedState.VerticalFollowerPositionRaw, actual: restoredState.VerticalFollowerPositionRaw);
        Assert.Equal(expected: capturedState.VerticalFollowerVelocityRaw, actual: restoredState.VerticalFollowerVelocityRaw);
        Assert.Equal(expected: capturedState.VerticalFollowerPreviousTarget, actual: restoredState.VerticalFollowerPreviousTarget);
    }
}

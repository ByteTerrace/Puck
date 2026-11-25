using Puck.Maths;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.Physics.Motion;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    internal bool ApplyTargetedEffect(int sourceIndex, CompiledBodyInstruction instruction, bool validatedFiring = false) {
        var slot = ((instruction.StateName is null)
            ? -1
            : FindActionState(name: instruction.StateName)
        );
        // A deferred delivery is checked against the state preceding that delivery as well as the firing's
        // earlier proposal. Another body's intervening firing may have changed the allowed tuple.
        var local = instruction with { Target = ActionTarget.Self, StateSlot = slot };

        if (!validatedFiring && (m_stateLane?.Input is { } input) && !input.TryValidateEffects(m_stateOrdinal,
            new ReadOnlySpan<CompiledBodyInstruction>(reference: in local), default, out _)) { return false; }
        var applied = true;

        switch (instruction.Operation) {
            case BodyMotionOp.SetVerticalVelocity:
            case BodyMotionOp.ScaleVerticalVelocity:
            case BodyMotionOp.PlanarImpulse:
            case BodyMotionOp.TimedMove:
                applied = TryApplyKinematicEffect(in instruction, in InputReadbacks.ReadComposed, m_orientation, m_up, m_attitudeUp);
                break;
            case BodyMotionOp.SetState when ((slot >= 0) && (m_actionStateDefinitions[slot].Kind == ActionStateKind.Counter)):
                ApplyRawState(
                    slot: slot,
                    requested: instruction.Value.Value,
                    writer: "foreign-effect",
                    reason: "setState"
                );
                MarkDurableDirty(slot: slot);
                break;
            case BodyMotionOp.AddState when ((slot >= 0) && (m_actionStateDefinitions[slot].Kind == ActionStateKind.Counter)):
                var beforeAdd = StateCounter(slot: slot);
                ApplyRawState(
                    slot: slot,
                    requested: (beforeAdd + instruction.Value).Value,
                    writer: "foreign-effect",
                    reason: "addState"
                );
                MarkDurableDirty(
                    slot: slot,
                    kind: WorldDocumentWriteKind.Add,
                    operand: (StateCounter(slot: slot) - beforeAdd)
                );
                break;
            case BodyMotionOp.StartTimer when ((slot >= 0) && (m_actionStateDefinitions[slot].Kind == ActionStateKind.Timer)):
                ApplyRawState(
                    reason: "startTimer",
                    requested: checked((long)instruction.DurationTicks),
                    slot: slot,
                    writer: "foreign-effect"
                );
                MarkDurableDirty(slot: slot);
                break;
            default:
                applied = false;
                break;
        }
        if (applied) {
            WakeUp();
            m_affectingSubject = sourceIndex;
            if (!validatedFiring && (instruction.Operation is BodyMotionOp.SetState or BodyMotionOp.AddState or BodyMotionOp.StartTimer)) {
                m_stateLane?.Input?.CommitKitEffects(m_stateOrdinal);
            }
        }
        return applied;
    }
    [System.Runtime.CompilerServices.SkipLocalsInit]
    internal void ExecuteProducer(CompiledBodyProducer producer, ref BodyProducerState state, in BodyProducerSensors sensors, ulong stepTicks) {
        System.Runtime.CompilerServices.Unsafe.SkipInit(value: out BodyMotionScratch scratch);
        scratch.Intent = default;
        scratch.Producer = producer;
        scratch.ProducerState = state;
        scratch.ProducerSensors = sensors;
        scratch.SensorTarget = BodySensorTarget.None;
        scratch.TurnSpeed = ResolveTurnRate();
        scratch.StepTicks = stepTicks;
        scratch.GoverningShapingRow = -1;

        // A producer's operations write only the scratch, so a run that throws leaves the body and its producer state
        // exactly as they were: the run is refused, the body holds still for it, and the fault is the body's own.
        try {
            for (var phase = 0; (phase < producer.Program.Phases.Length); phase++) {
                foreach (var op in producer.Program.Phases[phase]) {
                    ExecutePhaseOperation(
                        op: op,
                        scratch: ref scratch
                    );
                }
            }
        } catch (Exception error) when (IsProducerFault(error: error)) {
            NoteProducerFault(error: error);
            StageProducerIntent(intent: default);

            return;
        }

        m_producerFault = null;
        state = scratch.ProducerState;
        StageProducerIntent(intent: in scratch.Intent);
    }

    private bool ApplyEffects(CompiledBodyInstruction[] effects, ref BodyMotionScratch scratch, int logicalChannel = -1) {
        if ((m_stateLane?.Input is { } input) && !input.TryValidateEffects(body: scratch.EntityIndex, effects: effects, reason: out _, targets: scratch.EffectTargets)) {
            return false;
        }
        var epoch = InputEpoch;
        var writesState = false;

        foreach (var effect in effects) {
            if (effect.Target == ActionTarget.Self) {
                writesState |= (effect.Operation is BodyMotionOp.SetState or BodyMotionOp.AddState or BodyMotionOp.StartTimer);
                ExecuteOperation(
                    instruction: in effect,
                    scratch: ref scratch
                );
                continue;
            }

            var target = scratch.EffectTargets.Resolve(target: effect.Target);

            if ((target >= 0) && (m_stateLane?.Input is { } runtime) &&
                (effect.Operation is BodyMotionOp.SetState or BodyMotionOp.AddState or BodyMotionOp.StartTimer)) {
                runtime.ApplyKitForeignState(instruction: effect, sourceIndex: scratch.EntityIndex, targetIndex: target);
                writesState = true;
                continue;
            }

            if (
                (target >= 0) &&
                (scratch.EffectOutputs is not null)
            ) {
                scratch.EffectOutputs.Add(item: new BodyEffectOutput(
                    Instruction: effect,
                    SourceIndex: scratch.EntityIndex,
                    TargetIndex: target
                ));
            }
        }
        if (writesState) { m_stateLane?.Input?.CommitKitEffects(body: scratch.EntityIndex, logicalChannel: logicalChannel); }
        if (InputEpoch != epoch) { scratch.Intent = InputReadbacks.EngagedIntent; }
        return true;
    }
    // The shared hard-teleport commit: clear the affected integration carries. Face only resets rotation; Warp resets
    // position and vertical state but preserves rotation; full Pose/Reconcile operations reset every carry. SetBodyMotionProgram
    // resets the pose carries and only resets vertical state when switching to grounded.
    private void CommitTeleport(bool resetPosition = true, bool resetVertical = true, bool resetRotation = true) {
        // Every hard-teleport caller (Pose/Warp/Face/Reconcile, a motion-program switch) is a pose the caller
        // directed at this body — see WorldBody.Sleep.cs's own remarks. It is a discontinuity: the door wakes what rests
        // on or holds the body and forgets its contacts; it reads the contacts and the island root, never the pose the
        // caller just wrote.
        Disturb();
        WakeUp();

        if (resetPosition) {
            m_positionAccumulator.Reset();
            // A hard reposition cancels any in-flight impulse overlay (a warp never carries a dash across).
            m_overlayVelocity = default;
            m_overlayRemaining = 0;
            m_overlayAccumulator.Reset();
            // Same discontinuity reason as the velocity resets below: without this, a warped body's swept
            // portal-crossing segment would ghost from the pre-warp position through every frame in between. The
            // caller has already written the new m_position by the time CommitTeleport runs, so this collapses the
            // segment to a point exactly at the landing spot — the degenerate case the swept test reduces to.
            m_previousPosition = m_position;
        }

        if (resetRotation) {
            m_rotationAccumulator.Reset();
        }
        if (resetPosition || resetRotation) {
            m_timedMove = default;
        }

        if (resetVertical) {
            ResetVertical();
        }
    }
    // An angle in radians normalized into [0, 360) degrees, so an echo is a stable compass reading (a -10° pitch reads
    // as 350°, level as 0°).
    private static float CompassDegrees(float radians) =>
        float.RadiansToDegrees(radians: VectorFunctions.WrapTurn(radians: radians));
    private void ComputeLocalTargetVelocity(ref BodyMotionScratch scratch) {
        // The free program shares the kit's declared movement frame. Under World, the client has ALREADY rotated
        // the planar pair through camera yaw, so rotating it through body attitude again is a second composition
        // (the source of direction-dependent flight and apparent vertical loss after a handoff). World also makes
        // MoveUp literal world Y, keeping an upright hover body's ascent independent of its rendered facing. Heading
        // retains true 6DOF body-local flight.
        var facing = ((m_tuning.MoveFrame == MotionMoveFrame.World)
            ? -FixedVector3.UnitZ
            : scratch.Orientation.Rotate(vector: -FixedVector3.UnitZ)
        );
        var right = ((m_tuning.MoveFrame == MotionMoveFrame.World)
            ? FixedVector3.UnitX
            : scratch.Orientation.Rotate(vector: FixedVector3.UnitX)
        );
        var up = ((m_tuning.MoveFrame == MotionMoveFrame.World)
            ? FixedVector3.UnitY
            : scratch.Orientation.Rotate(vector: FixedVector3.UnitY)
        );

        var (forward, strafe) = PlanarIntent(intent: in scratch.Intent);

        scratch.Velocity = ((((facing * forward) + (right * strafe)) + (up * Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveUp
        ))) * scratch.MoveSpeed);
        scratch.TargetVelocity = scratch.Velocity;
    }
    // Resolve the accepted tick's movement resultant through the same surface projection as locomotion.
    // Normalization gives a fixed overlay speed even at partial stick deflection.
    private FixedVector3 ImpulseDirection(in CompiledBodyInstruction instruction, in PlayerIntent intent, FixedQuaternion orientation) {
        if (instruction.UseMoveDirection) {
            var scratch = default(BodyMotionScratch);
            FixedVector3 direction;

            scratch.Intent = intent;
            scratch.Orientation = orientation;
            scratch.Up = m_up;

            if (!TryCommandedMoveDirection(direction: out direction, scratch: ref scratch)) {
                var (forward, strafe) = PlanarIntent(intent: in intent);
                var facing = ((m_tuning.MoveFrame == MotionMoveFrame.World) ? m_frame.Rotate(vector: -FixedVector3.UnitZ) : orientation.Rotate(vector: -FixedVector3.UnitZ));
                var right = ((m_tuning.MoveFrame == MotionMoveFrame.World) ? m_frame.Rotate(vector: FixedVector3.UnitX) : orientation.Rotate(vector: FixedVector3.UnitX));

                direction = ((facing * forward) + (right * strafe)).RejectFrom(unitAxis: m_up);
            }
            if (direction != FixedVector3.Zero) { return direction.WithLength(length: FixedQ4816.One); }
        }
        return orientation.Rotate(vector: instruction.Direction);
    }
    private bool TryApplyKinematicEffect(in CompiledBodyInstruction instruction, in PlayerIntent intent,
        FixedQuaternion orientation, FixedVector3 up, FixedVector3 attitudeUp) {
        switch (instruction.Operation) {
            case BodyMotionOp.SetVerticalVelocity:
            case BodyMotionOp.ScaleVerticalVelocity:
                m_verticalVelocity = ((instruction.Operation == BodyMotionOp.SetVerticalVelocity)
                    ? (instruction.Value * m_scale)
                    : (m_verticalVelocity * instruction.Value));
                if (m_verticalVelocity > FixedQ4816.Zero) { m_grounded = false; }
                m_verticalVelocityAccumulator.Reset();
                return true;
            case BodyMotionOp.PlanarImpulse:
                m_overlayVelocity = (ImpulseDirection(in instruction, in intent, orientation) * (instruction.Value * m_scale));
                m_overlayRemaining = instruction.DurationTicks;
                m_overlayAccumulator.Reset();
                return true;
            case BodyMotionOp.TimedMove:
                if (!TryPrepareTimedMove(in instruction, orientation, up, attitudeUp, out var move)) { return false; }
                m_timedMove = move;
                if (HasTimedYaw) { m_rotationAccumulator.Reset(); }
                return true;
            default:
                throw new ArgumentOutOfRangeException(nameof(instruction));
        }
    }
    private void ComputePlanarTargetVelocity(ref BodyMotionScratch scratch) {
        var effectiveMoveSpeed = ApplySpeedHeld(
            baseSpeed: scratch.MoveSpeed,
            intent: in scratch.Intent
        );

        if (TryCommandedMoveDirection(
            direction: out var direction,
            scratch: ref scratch
        )) {
            scratch.TargetVelocity = (direction * effectiveMoveSpeed);
        } else {
            var (forward, strafe) = PlanarIntent(intent: in scratch.Intent);

            scratch.TargetVelocity = (((scratch.Facing * forward) + (scratch.Right * strafe)) * effectiveMoveSpeed);
        }

        if (m_bodyMotionProgram.PlanarTarget is { } planarTarget) {
            scratch.TargetVelocity = ScaleAgainstHeading(
                orientation: in scratch.Orientation,
                parameters: in planarTarget,
                target: scratch.TargetVelocity
            );
        }
    }
    // The program's planarTarget parameters: the target decomposes against the heading the frame operation resolved
    // (scratch.Orientation, which under a World move frame is the heading attitude while Facing/Right are world
    // axes), its lateral part scaled by Strafe and a part opposing the heading by Backward. The remainder (whatever
    // lies along the heading's up) passes through untouched.
    private static FixedVector3 ScaleAgainstHeading(in FixedQuaternion orientation, in FixedPlanarTarget parameters, FixedVector3 target) {
        var facing = orientation.Rotate(vector: -FixedVector3.UnitZ);
        var right = orientation.Rotate(vector: FixedVector3.UnitX);
        var along = FixedVector3.Dot(
            left: target,
            right: facing
        );
        var across = FixedVector3.Dot(
            left: target,
            right: right
        );
        var rest = ((target - (facing * along)) - (right * across));

        if (along < FixedQ4816.Zero) {
            along *= parameters.Backward;
        }

        return (((facing * along) + (right * (across * parameters.Strafe))) + rest);
    }
    // The commanded WORLD movement direction laid onto the surface the body stands on, or false when the world does
    // not declare the triple or nothing is commanded.
    //
    // The seat resolves the stick against the camera and the body's own up and sends the result whole
    // (WorldClient.ComposeMoveFrame), so the sim never reconstructs a basis for it — which is the point. A basis
    // reconstructed here can only be wrong on a curved surface: a frame CARRIED with the body comes back rotated
    // after a loop, so "forward" would depend on the route taken rather than the place reached, and a basis
    // PROJECTED from a fixed world axis has a ring where that axis lines up with the surface normal and the body
    // cannot leave it. Projecting the commanded RESULTANT has neither failure: it is a function of position and
    // command alone, and only the single command pointing straight into the surface resolves to nothing — every
    // other direction still moves, so there is nothing to be trapped by.
    //
    // The magnitude is restored after the projection so a command that grazes the surface keeps its authored speed
    // and only its DIRECTION is bent, rather than the body slowing down wherever the ground tilts.
    private bool TryCommandedMoveDirection(ref BodyMotionScratch scratch, out FixedVector3 direction) {
        direction = FixedVector3.Zero;

        if (!m_roleOrdinals.HasMoveDirection) {
            return false;
        }

        var commanded = new FixedVector3(
            X: Role(
                intent: in scratch.Intent,
                role: ChannelRole.MoveX
            ),
            Y: Role(
                intent: in scratch.Intent,
                role: ChannelRole.MoveY
            ),
            Z: Role(
                intent: in scratch.Intent,
                role: ChannelRole.MoveZ
            )
        );
        var speed = commanded.Length;

        if (speed <= FixedQ4816.Zero) {
            return false;
        }

        if (speed > FixedQ4816.One) {
            // The disc rule the planar pair obeys, in three dimensions: a saturated command is one direction at full
            // speed, never longer.
            commanded = (commanded / speed);
            speed = FixedQ4816.One;
        }

        var tangent = commanded.RejectFrom(unitAxis: scratch.Up);

        if (tangent == FixedVector3.Zero) {
            // Straight into (or out of) the surface: nothing to walk along. The body holds still rather than being
            // handed an arbitrary direction.
            return true;
        }

        direction = tangent.WithLength(length: speed);

        return true;
    }
    // The (forward, strafe) intent pair clamped to the unit disc: two digital keys (or a square-clamped stick) at
    // full deflection are one direction at full speed, never √2 of it. Inside the disc the pair passes through
    // untouched, so a stick's magnitude still meters speed.
    private (FixedQ4816 Forward, FixedQ4816 Strafe) PlanarIntent(in PlayerIntent intent) {
        var forward = Role(
            intent: in intent,
            role: ChannelRole.MoveAdvance
        );
        var strafe = Role(
            intent: in intent,
            role: ChannelRole.MoveStrafe
        );
        var lengthSquared = ((forward * forward) + (strafe * strafe));

        if (lengthSquared <= FixedQ4816.One) {
            return (forward, strafe);
        }

        var length = FixedQ4816.Sqrt(value: lengthSquared);

        return ((forward / length), (strafe / length));
    }
    // The presentation-side Tait-Bryan readback of the orientation FromYawPitchRoll built: the float echo of
    // body.where, never a value that re-enters state.
    private (float Yaw, float Pitch, float Roll) EulerRadians() =>
        VectorFunctions.EulerOf(attitude: m_orientation.ToQuaternion());
    private void ExecutePhaseOperation(BodyMotionOp op, ref BodyMotionScratch scratch) {
        switch (op) {
            case BodyMotionOp.SenseNearestInCone:
                SenseTarget(candidate: scratch.ProducerSensors.Candidate, scratch: ref scratch);
                break;
            case BodyMotionOp.ProduceSteeringIntent:
                ProduceSteeringIntent(scratch: ref scratch);
                break;
            case BodyMotionOp.ProduceFlockIntent:
                ProduceFlockIntent(scratch: ref scratch);
                break;
            case BodyMotionOp.FaceSensorTarget:
                FaceSensorTarget(scratch: ref scratch);
                break;
            case BodyMotionOp.ResolveYawAttitudeAndPlanarFrame:
                ResolveYawAttitudeAndPlanarFrame(scratch: ref scratch);
                break;
            case BodyMotionOp.IntegrateLocalAttitude:
                IntegrateLocalAttitude(scratch: ref scratch);
                break;
            case BodyMotionOp.ComputePlanarTargetVelocity:
                ComputePlanarTargetVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.ComputeLocalTargetVelocity:
                ComputeLocalTargetVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.ShapeVelocity:
                ShapeVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.SnapYawToPlanarIntent:
                SnapYawToPlanarIntent(scratch: ref scratch);
                break;
            case BodyMotionOp.ResolveDriveFrame:
                ResolveDriveFrame(scratch: ref scratch);
                break;
            case BodyMotionOp.ResolveHold:
                ResolveHold(scratch: ref scratch);
                break;
            case BodyMotionOp.RunActionTriggers:
                ProcessLaneActions(scratch: ref scratch);
                break;
            case BodyMotionOp.ApplyHold:
                ApplyHold(scratch: ref scratch);
                break;
            case BodyMotionOp.IntegratePlanarAndVerticalVelocity:
                IntegratePlanarAndVerticalVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.IntegrateScratchVelocity:
                IntegrateScratchVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.CommitPose:
                ConstrainFlockLocomotion(scratch: ref scratch);
                m_position = scratch.NextPosition;
                m_orientation = scratch.Orientation;
                break;
            default: {
                var instruction = new CompiledBodyInstruction(
                    Operation: op,
                    Value: default,
                    Direction: default,
                    DurationTicks: 0UL,
                    StateSlot: -1
                );

                ExecuteOperation(instruction: in instruction, scratch: ref scratch);
                break;
            }
        }
    }

    private void ExecuteOperation(in CompiledBodyInstruction instruction, ref BodyMotionScratch scratch) {
        switch (instruction.Operation) {
            case BodyMotionOp.SenseNearestInCone:
                SenseTarget(
                    candidate: scratch.ProducerSensors.Candidate,
                    scratch: ref scratch
                );
                break;
            case BodyMotionOp.ProduceSteeringIntent:
                ProduceSteeringIntent(scratch: ref scratch);
                break;
            case BodyMotionOp.ProduceFlockIntent:
                ProduceFlockIntent(scratch: ref scratch);
                break;
            case BodyMotionOp.FaceSensorTarget:
                FaceSensorTarget(scratch: ref scratch);
                break;
            case BodyMotionOp.ResolveYawAttitudeAndPlanarFrame:
                ResolveYawAttitudeAndPlanarFrame(scratch: ref scratch);
                break;
            case BodyMotionOp.IntegrateLocalAttitude:
                IntegrateLocalAttitude(scratch: ref scratch);
                break;
            case BodyMotionOp.ComputePlanarTargetVelocity:
                ComputePlanarTargetVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.ComputeLocalTargetVelocity:
                ComputeLocalTargetVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.ShapeVelocity:
                ShapeVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.SnapYawToPlanarIntent:
                SnapYawToPlanarIntent(scratch: ref scratch);
                break;
            case BodyMotionOp.ResolveDriveFrame:
                ResolveDriveFrame(scratch: ref scratch);
                break;
            case BodyMotionOp.ResolveHold:
                ResolveHold(scratch: ref scratch);
                break;
            case BodyMotionOp.RunActionTriggers:
                ProcessLaneActions(scratch: ref scratch);
                break;
            case BodyMotionOp.ApplyHold:
                ApplyHold(scratch: ref scratch);
                break;
            case BodyMotionOp.IntegratePlanarAndVerticalVelocity:
                IntegratePlanarAndVerticalVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.IntegrateScratchVelocity:
                IntegrateScratchVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.CommitPose:
                ConstrainFlockLocomotion(scratch: ref scratch);
                m_position = scratch.NextPosition;
                m_orientation = scratch.Orientation;
                break;
            case BodyMotionOp.SetVerticalVelocity:
            case BodyMotionOp.ScaleVerticalVelocity:
            case BodyMotionOp.PlanarImpulse:
            case BodyMotionOp.TimedMove:
                if (!TryApplyKinematicEffect(in instruction, in scratch.Intent, scratch.Orientation, scratch.Up, scratch.AttitudeUp)) {
                    NoteSweepRefusal(refusal: ContactRefusal.UnrepresentableSweep);
                }
                break;
            case BodyMotionOp.SetState:
                ApplyRawState(
                    slot: instruction.StateSlot,
                    requested: instruction.Value.Value,
                    writer: "world-effect",
                    reason: "setState"
                );
                MarkDurableDirty(slot: instruction.StateSlot);
                break;
            case BodyMotionOp.AddState:
                var beforeAdd = StateCounter(slot: instruction.StateSlot);
                ApplyRawState(
                    slot: instruction.StateSlot,
                    requested: (beforeAdd + instruction.Value).Value,
                    writer: "world-effect",
                    reason: "addState"
                );
                MarkDurableDirty(
                    slot: instruction.StateSlot,
                    kind: WorldDocumentWriteKind.Add,
                    operand: (StateCounter(slot: instruction.StateSlot) - beforeAdd)
                );
                break;
            case BodyMotionOp.StartTimer:
                ApplyRawState(
                    slot: instruction.StateSlot,
                    requested: checked((long)instruction.DurationTicks),
                    writer: "world-effect",
                    reason: "startTimer"
                );
                MarkDurableDirty(slot: instruction.StateSlot);
                break;
            case BodyMotionOp.Designate:
                var subject = scratch.EffectTargets.Resolve(target: instruction.Target);
                if (
                    (subject >= 0) &&
                    (instruction.StateName is { } register) &&
                    (scratch.DesignationOutputs is not null)
                ) {
                    scratch.DesignationOutputs.Add(item: new WorldDesignation(
                        EntityIndex: scratch.EntityIndex,
                        Register: register,
                        Subject: GrantSubject.Body(index: subject)
                    ));
                }
                break;
            case BodyMotionOp.Generate:
                // STAGED, never applied here: the destination is a document row, so the firing joins the ordinary
                // mutation pipeline after the whole population advance (see WorldGeneratorInvocation).
                if (
                    (instruction.StateName is { } siteRow) &&
                    (scratch.GeneratorInvocations is not null)
                ) {
                    scratch.GeneratorInvocations.Add(item: new WorldGeneratorInvocation(Row: siteRow));
                }
                break;
            default:
                throw new InvalidOperationException(message: $"Body program reached uncompiled opcode value {((int)instruction.Operation)}.");
        }
    }
    private void ExecuteProgram(in PlayerIntent intent, FixedQ4816 moveSpeed, FixedQ4816 turnSpeed, ulong stepTicks, int entityIndex, BodyEffectTargets effectTargets, List<BodyEffectOutput>? effectOutputs, List<WorldDesignation>? designationOutputs, List<WorldGeneratorInvocation>? generatorInvocations) {
        m_entityIndex = entityIndex;
        // Written field by field into the zeroed local: an object initializer builds a temporary and copies the whole
        // scratch, references included, into the local through the runtime's bulk write barrier on every step.
        var scratch = default(BodyMotionScratch);

        scratch.AttitudeUp = m_up;
        scratch.DesignationOutputs = designationOutputs;
        scratch.EffectOutputs = effectOutputs;
        scratch.EffectTargets = effectTargets;
        scratch.EntityIndex = entityIndex;
        scratch.GeneratorInvocations = generatorInvocations;
        scratch.GoverningShapingRow = -1;
        scratch.Intent = intent;
        scratch.MoveSpeed = moveSpeed;
        scratch.NextPosition = m_position;
        scratch.Orientation = m_orientation;
        scratch.StepTicks = stepTicks;
        scratch.TurnSpeed = turnSpeed;
        scratch.Up = m_up;

        // Refresh the shared shaping-table recency clocks (a Recently window refills while its fact holds, decays
        // otherwise) once, before phase 0, so every phase's gate read — a frame op's turnScale, ShapeVelocity's own
        // row, ApplyMedium's vertical lane — agrees on the same tick's answer.
        for (var slot = 0; (slot < m_motionRecency.Length); slot++) {
            m_motionRecency[slot] = (FactHolds(fact: m_tuning.ShapingRecencyFacts[slot])
                ? m_tuning.ShapingRecencyWindows[slot]
                : m_motionRecency[slot].SubtractSaturating(right: stepTicks)
            );
        }

        // Gate evaluation is a once-per-body, once-per-tick decision. Besides avoiding up to three repeated
        // predicate walks, this snapshots facts before any operation can mutate them (an action may set vertical
        // velocity later in the program), so turn, planar shaping, and a medium's vertical lane cannot disagree on
        // which row governed the same tick.
        scratch.GoverningShapingRow = ResolveGoverningShapingRow(intent: in intent);

        for (var phase = 0; (phase < m_bodyMotionProgram.Phases.Length); phase++) {
            foreach (var op in m_bodyMotionProgram.Phases[phase]) {
                ExecutePhaseOperation(
                    op: op,
                    scratch: ref scratch
                );
                if (m_sweepRefusal != ContactRefusal.None) { return; }
            }

            if (phase == 5) {
                if (!ProposeTimedMove(scratch: ref scratch)) { return; }
                ResolveProgramContacts(scratch: ref scratch);
                if (m_sweepRefusal != ContactRefusal.None) { return; }
            }
        }

        // The grip's inward standoff lands after the pose is committed, for the same reason the contact resolve
        // lands after integration: it is a correction to where the body ended up, not a term in how it got there.
        SeatToHold(stepTicks: stepTicks);
    }
    private void FaceSensorTarget(ref BodyMotionScratch scratch) {
        if (
            !scratch.SensorTarget.Exists ||
            (m_roleOrdinals.Turn < 0)
        ) {
            return;
        }

        var targetYaw = PlanarOffset(
            from: m_position,
            to: scratch.SensorTarget.Position
        ).Yaw();
        var yawRate = (scratch.Producer!.Scalar(parameter: BodyProducerParameter.InwardGain) * FixedQ4816.WrapAngle(radians: (targetYaw - FixedYaw)));
        var turn = FixedQ4816.Clamp(
            value: (yawRate / scratch.Producer.Scalar(parameter: BodyProducerParameter.TurnScale)),
            minimum: NegativeOne,
            maximum: FixedQ4816.One
        );

        scratch.Intent = scratch.Intent.WithChannel(
            ordinal: m_roleOrdinals.Turn,
            value: turn
        );
    }
    // The free integration — full 6DOF in the body frame. Compose the yaw/pitch/roll rates (each × turnSpeed) into a
    // body-frame delta and post-multiply it into the attitude (q ← normalize(q · Δq), so the rates rotate about the
    // body's own axes), then fly along the fresh body axes: velocity = (forward·MoveAdvance + right·MoveStrafe +
    // up·MoveUp) · moveSpeed, with no ground pin and no gravity. The bound actions run after the attitude update, so a
    // fired vertical impulse (the surge) rides this tick; the written channel bleeds to zero at the tuning's rise
    // gravity (no fall phase).
    private void IntegrateLocalAttitude(ref BodyMotionScratch scratch) {
        var authority = ResolveTurnAuthority(
            governingRow: scratch.GoverningShapingRow,
            moveSpeed: scratch.MoveSpeed
        );
        var angularStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: ((Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.Turn
                ) * scratch.TurnSpeed) * authority),
                Y: ((Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.Pitch
                ) * scratch.TurnSpeed) * authority),
                Z: ((Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.Roll
                ) * scratch.TurnSpeed) * authority)
            ),
            elapsedTicks: scratch.StepTicks
        );
        var delta = FixedQuaternion.FromYawPitchRoll(
            pitch: angularStep.Y,
            roll: angularStep.Z,
            yaw: angularStep.X
        );

        scratch.Orientation = (m_orientation * delta).Normalize();
    }
    private void IntegratePlanarAndVerticalVelocity(ref BodyMotionScratch scratch) {
        scratch.Velocity = (m_planarVelocity + (scratch.Up * (m_verticalVelocity + scratch.DirectVerticalVelocity)));
        m_directVerticalVelocity = scratch.DirectVerticalVelocity;
        // The support search only re-aims the planar velocity along what lies below: from a body at rest it lands a
        // zero velocity, and finding nothing keeps the zero it already holds, so a resting body skips its sweeps.
        var resting = ((m_planarVelocity == FixedVector3.Zero) && (scratch.Velocity == FixedVector3.Zero));

        if (!resting && m_grounded && !HoldOwnsVerticalChannel && !HoldIsMedium && (scratch.DirectVerticalVelocity <= FixedQ4816.Zero) &&
            (m_contactField is { } field) && (m_collider is not null)) {
            var contactStep = new BodyContactStep(field: field, entityIndex: scratch.EntityIndex, memo: ref scratch.SupportMemo,
                orientation: scratch.Orientation, up: scratch.Up, volumes: ScaledColliderVolumes(), volumesRevision: BroadphaseRadiusComputations);

            if (FixedStepUp.TryFollowSupport(step: ref contactStep, start: in m_position, velocity: in m_planarVelocity,
                up: in scratch.Up, distance: GroundSnap, result: out var support)) {
                scratch.Velocity = support.Velocity;
            }
        }
        var step = m_positionAccumulator.Integrate(
            elapsedTicks: scratch.StepTicks,
            ratePerSecond: scratch.Velocity
        );

        scratch.NextPosition = (m_position + step);
        IntegrateOverlay(scratch: ref scratch);
    }
    private void IntegrateScratchVelocity(ref BodyMotionScratch scratch) {
        scratch.NextPosition = (m_position + m_positionAccumulator.Integrate(
            elapsedTicks: scratch.StepTicks,
            ratePerSecond: scratch.Velocity
        ));
        IntegrateOverlay(scratch: ref scratch);
    }
    // Keep the timed displacement inside the program's ordinary swept contact boundary. The overlay
    // retains its own integration residue and never becomes persistent locomotion velocity.
    private void IntegrateOverlay(ref BodyMotionScratch scratch) {
        if (m_overlayRemaining == 0) { return; }
        var ticks = Math.Min(val1: scratch.StepTicks, val2: m_overlayRemaining);

        scratch.NextPosition += m_overlayAccumulator.Integrate(elapsedTicks: ticks, ratePerSecond: m_overlayVelocity);
        m_overlayRemaining -= ticks;
        if (m_overlayRemaining == 0) { m_overlayVelocity = default; m_overlayAccumulator.Reset(); }
    }
    // A step outside the kit transaction (StepIsLocal) has no tape, timed press or script controller, so it admits no
    // script observation (scripts: false) and reaches no input runtime.
    private PlayerIntent NextIntent(ulong stepTicks, ulong tick, int body, bool scripts) {
        var movement = default(PlayerIntent);
        var tapeActor = default(Principal);
        var resolved = false;

        while (
            !resolved &&
            (m_tapeCount > 0)
        ) {
            ref var segment = ref m_tape[m_tapeHead];

            if (!(segment.RemainingTicks > 0)) {
                DropFrontSegment();

                continue;
            }

            // Charge this whole tick against the front segment; durations were quantized upward to whole host ticks at
            // enqueue, so no fractional tail or floating accumulator exists here.
            segment.RemainingTicks = segment.RemainingTicks.SubtractSaturating(right: stepTicks);
            movement = segment.Intent;
            tapeActor = segment.Actor;
            segment.Intent = WithoutRelative(intent: segment.Intent);
            resolved = true;

            if (!(segment.RemainingTicks > 0)) {
                DropFrontSegment();
            }
        }

        if (!scripts) { m_scriptAccepted = default; }
        var acceptedScript = (scripts ? AdmitScriptInput(body: body, tape: movement, tapeActor: tapeActor, tick: tick) : movement);

        if (resolved) { movement = acceptedScript; }
        if (!resolved) {
            movement = ((!InputImages.Source.IsIdle && InputImages.HasSubmitted)
                ? InputImages.Submitted
                : ((SourceNamesProducer(source: InputImages.Source) && InputImages.HasProducer)
                    ? InputImages.Producer
                    : default
            ));
        }

        // Both one-tick images are a one-step publish, even when a tape or the source masked them this time. Their
        // producers must republish on the next authoritative step, matching the snapshot discipline of every other
        // input source.
        InputImagesEdit.Submitted = default;
        InputImagesEdit.HasSubmitted = false;
        InputImagesEdit.Producer = default;
        InputImagesEdit.HasProducer = false;

        // Overlay the action track, per ordinal: a wire timer (body.press) overlays UNCONDITIONALLY — the poke
        // stays a poke regardless of intent source — replacing whatever the movement tier resolved for that ordinal;
        // otherwise a non-role ordinal additionally takes the live-held device image, admitted under
        // Live only (role ordinals never carry a held-device overlay — a seat submits them directly
        // inside `movement`). Two simultaneous composition contributors join via WorldChannelTable.ComposeHeld's
        // shape-aware rule: unipolar/binary reproduce the old ActionLanes OR (maximum magnitude, both operands in
        // [0, One]); bipolar sums the two instead, so a resting (zero) side can never overwrite a genuinely negative
        // one the way a numeric max used to.
        var channels = movement.Channels;
        var heldOverlay = default(ChannelValues);
        var liveHeld = (InputImages.HasTransferHeld
            ? InputImages.TransferHeld
            : InputImages.Held
        );

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_channelShapes[ordinal] == ChannelShape.Relative) { continue; }
            if (Action.TimerTicks[ordinal] > 0) {
                channels[ordinal] = ((m_stateLane?.Input is not null) ? m_scriptAccepted[ordinal] : ((m_channelShapes[ordinal] == ChannelShape.Binary)
                    ? ((Action.TimerValues[ordinal] >= m_channelThresholds[ordinal]) ? FixedQ4816.One : FixedQ4816.Zero)
                    : Action.TimerValues[ordinal]));
            } else if (
                InputImages.Source.IsLive &&
                !m_roleChannels[ordinal]
            ) {
                heldOverlay[ordinal] = liveHeld[ordinal];
                channels[ordinal] = FixedQ4816.FromRawBits(value: WorldChannelTable.ComposeHeld(
                    a: channels[ordinal].Value,
                    b: liveHeld[ordinal].Value,
                    shape: m_channelShapes[ordinal]
                ));
            }
        }

        var readHeld = new PlayerIntent(Channels: heldOverlay);
        var composed = (movement with { Channels = channels });

        if (InputReadbacks.ReadHeld != readHeld) { InputReadbacksEdit.ReadHeld = readHeld; }
        if (InputReadbacks.ReadComposed != composed) { InputReadbacksEdit.ReadComposed = composed; }
        var scalar = ProjectRelative(intent: composed);

        if (InputReadbacks.ReadScalar != scalar) { InputReadbacksEdit.ReadScalar = scalar; }
        return scalar;
    }
    // The planar (XZ) offset from one point to another, whose Yaw is the bearing from the first to the second.
    private static FixedVector3 PlanarOffset(in FixedVector3 from, in FixedVector3 to) =>
        new(
            X: (to.X - from.X),
            Y: FixedQ4816.Zero,
            Z: (to.Z - from.Z)
        );
    // The body's own resolved up may differ from world Y (a tilted planetoid surface, a wall run) — a producer's
    // "preferred altitude" is a distance from the world origin along that axis, not a literal world-Y coordinate.
    // Under the ordinary UnitY up this is exactly the .Y component every world read before the term generalized.
    private FixedQ4816 AlongUp(in FixedVector3 point) => FixedVector3.Dot(
        left: point,
        right: m_up
    );
    // One steering intent, dispatched per tick on whether this tick's SenseNearestInCone found a target. The roam
    // shape runs first, and only when the producer's own compiled RoamActive says the kit authored it — its
    // oscillator (state.Phase/ActivityPhase) then advances every tick regardless of which shape ends up steering, so
    // losing a sensed target resumes roam from the phase it would already be at rather than one frozen at the moment
    // sensing began; the approach shape, when a target exists, then overwrites its computed intent outright. A
    // program authoring both shapes (a stalking predator) takes both across its lifetime; a producer authoring no
    // roam scalars at all (an approach-only follower) leaves this tick's Intent exactly as handed in — a hold — on any
    // tick sensing finds nothing, rather than resuming a roam it never wanted.
    private void ProduceSteeringIntent(ref BodyMotionScratch scratch) {
        if (scratch.Producer!.RoamActive) {
            ProduceRoamIntent(scratch: ref scratch);
        }

        if (scratch.SensorTarget.Exists) {
            ProduceApproachIntent(scratch: ref scratch);
        }
    }
    private void ProduceApproachIntent(ref BodyMotionScratch scratch) {
        var producer = scratch.Producer!;
        var standoff = producer.Scalar(parameter: BodyProducerParameter.StandoffRadius);
        var forward = ((scratch.SensorTarget.DistanceSquared > (standoff * standoff))
            ? producer.Scalar(parameter: BodyProducerParameter.Approach)
            : FixedQ4816.Zero
        );
        var strafe = producer.Scalar(parameter: BodyProducerParameter.Orbit);
        var followsVolume = (producer.Target is { Source: BodyTargetSource.Navigated, NavigationKind: not WorldNavigationKind.Surface });
        var preferredAltitude = (followsVolume
            ? AlongUp(point: scratch.SensorTarget.Position)
            : scratch.ProducerState.PreferredAltitude
        );
        // Its own gain, distinct from the roam shape's AltitudeGain (ProduceRoamIntent) — the two shapes are
        // independently authorable, so zeroing one's altitude term never zeroes the other's.
        var up = ((followsVolume || m_usesLocalAttitude)
            ? FixedQ4816.Clamp(
                value: ((preferredAltitude - AlongUp(point: m_position)) * producer.Scalar(parameter: BodyProducerParameter.ApproachAltitudeGain)),
                minimum: NegativeOne,
                maximum: FixedQ4816.One
            )
            : FixedQ4816.Zero
        );

        if (m_tuning.MoveFrame == MotionMoveFrame.World) {
            // Under World, MoveAdvance/MoveStrafe are raw world axes (a seat rotates its stick through camera yaw
            // before submission); a producer must rotate its own body-relative approach/orbit pair the same way,
            // using the bearing TO the sensed target — the same atan2 convention FaceSensorTarget's Turn write
            // steers the drawn attitude toward, since that Turn value never reaches the World-frame translation
            // basis (ResolveYawAttitudeAndPlanarFrame). This is what steers movement.
            var targetYaw = PlanarOffset(
                from: m_position,
                to: scratch.SensorTarget.Position
            ).Yaw();

            var (sinYaw, cosYaw) = FixedQ4816.SinCos(angle: targetYaw);

            scratch.Intent = m_roleOrdinals.Intent(
                moveAdvance: ((forward * cosYaw) + (strafe * sinYaw)),
                moveStrafe: ((-forward * sinYaw) + (strafe * cosYaw)),
                moveUp: up
            );
        } else {
            scratch.Intent = m_roleOrdinals.Intent(
                moveAdvance: forward,
                moveStrafe: strafe,
                moveUp: up
            );
        }
    }
    private static FixedQ4816 AdvancePhase(FixedQ4816 rate, ref long remainder, ulong stepTicks) {
        var accumulator = FixedRateAccumulator.FromRemainder(
            remainder: remainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        var advance = accumulator.Integrate(
            elapsedTicks: stepTicks,
            ratePerSecond: rate
        );

        remainder = accumulator.Remainder;

        return advance;
    }
    private void ProduceRoamIntent(ref BodyMotionScratch scratch) {
        var producer = scratch.Producer!;
        var state = scratch.ProducerState;

        // A refresh covers whatever whole number of engine ticks its autonomy cadence batched; the carried remainders
        // keep a second of refreshes a second of phase at any cadence.
        state.Phase += AdvancePhase(
            rate: state.WeaveFrequency,
            remainder: ref state.PhaseRemainder,
            stepTicks: scratch.StepTicks
        );
        state.ActivityPhase += AdvancePhase(
            rate: state.ActivityRate,
            remainder: ref state.ActivityPhaseRemainder,
            stepTicks: scratch.StepTicks
        );

        // Measured from the body's own HOME, never the world origin: a population spread over several placements
        // steers back to the ground it was activated on, instead of every roaming body converging on (0, 0).
        // A body with no home (the zero default) reads exactly as it did when the origin was the only anchor.
        var planarX = (m_position.X - m_home.X);
        var planarZ = (m_position.Z - m_home.Z);
        var yawRate = (producer.Scalar(parameter: BodyProducerParameter.WeaveAmplitude) * FixedQ4816.Sin(angle: state.Phase));
        var radius = FixedQ4816.Sqrt(value: ((planarX * planarX) + (planarZ * planarZ)));

        if (radius > producer.Scalar(parameter: BodyProducerParameter.SoftRadius)) {
            // The bearing from the body back to its home.
            var inwardYaw = PlanarOffset(
                from: m_position,
                to: m_home
            ).Yaw();

            yawRate += (producer.Scalar(parameter: BodyProducerParameter.InwardGain) * FixedQ4816.WrapAngle(radians: (inwardYaw - FixedYaw)));
        }

        var turn = FixedQ4816.Clamp(
            value: (yawRate / producer.Scalar(parameter: BodyProducerParameter.TurnScale)),
            minimum: NegativeOne,
            maximum: FixedQ4816.One
        );
        var wave = FixedQ4816.Sin(angle: state.ActivityPhase);
        var altitudeCorrection = FixedQ4816.Clamp(
            value: ((state.PreferredAltitude - AlongUp(point: m_position)) * producer.Scalar(parameter: BodyProducerParameter.AltitudeGain)),
            minimum: NegativeOne,
            maximum: FixedQ4816.One
        );

        if (m_usesLocalAttitude) {
            scratch.Intent = m_roleOrdinals.Intent(
                moveAdvance: producer.Scalar(parameter: BodyProducerParameter.Forward),
                moveStrafe: (wave * producer.Scalar(parameter: BodyProducerParameter.StrafeWave)),
                turn: turn,
                moveUp: (altitudeCorrection + (wave * producer.Scalar(parameter: BodyProducerParameter.UpWave))),
                pitch: (wave * producer.Scalar(parameter: BodyProducerParameter.PitchWave)),
                roll: (-turn * producer.Scalar(parameter: BodyProducerParameter.RollTurn))
            );
        } else {
            var angularIntent = FixedQ4816.Clamp(
                value: (turn + (wave * producer.Scalar(parameter: BodyProducerParameter.TurnWave))),
                minimum: NegativeOne,
                maximum: FixedQ4816.One
            );
            var forward = producer.Scalar(parameter: BodyProducerParameter.Forward);
            var strafe = (wave * producer.Scalar(parameter: BodyProducerParameter.StrafeWave));

            if (m_tuning.MoveFrame == MotionMoveFrame.World) {
                // A producer owns a body-relative steering decision even when a seat-facing kit consumes world-frame
                // axes. Resolve that decision through the same yaw convention SnapYawToPlanarIntent reads; otherwise
                // the Turn channel is deliberately inert under World and every roamer can only march toward -Z.
                var targetYaw = (FixedYaw + FixedTickConversion.ShareOfStep(
                    perSecond: (angularIntent * scratch.TurnSpeed),
                    stepTicks: scratch.StepTicks
                ));

                var (sinYaw, cosYaw) = FixedQ4816.SinCos(angle: targetYaw);
                // The Turn role carries the same angular intent, so the heading integrates toward targetYaw (the
                // facing snap turns the ATTITUDE only; without this the roamer's heading would never advance).
                scratch.Intent = m_roleOrdinals.Intent(
                    moveAdvance: ((forward * cosYaw) + (strafe * sinYaw)),
                    moveStrafe: ((-forward * sinYaw) + (strafe * cosYaw)),
                    turn: angularIntent
                );
            } else {
                scratch.Intent = m_roleOrdinals.Intent(
                    moveAdvance: forward,
                    moveStrafe: strafe,
                    turn: angularIntent
                );
            }

            var press = producer.Channel(parameter: BodyProducerParameter.Press);
            var threshold = producer.Scalar(parameter: BodyProducerParameter.PressThreshold);

            if (
                (press >= 0) &&
                (threshold > FixedQ4816.Zero) &&
                (wave > threshold)
            ) {
                scratch.Intent = scratch.Intent.WithChannel(
                    ordinal: press,
                    value: FixedQ4816.One
                );
            }
        }

        scratch.ProducerState = state;
    }
    // Reset the grounded vertical state to a clean rest on the plane — called by every hard teleport, so a jump never
    // survives an authoritative reposition. The action track (held/timed lanes) is left alone: a teleport moves the
    // body, not the player's buttons.
    private void ResetVertical() {
        m_positionAccumulator.ResetY();
        m_grounded = CanBeGrounded;
        ResetTranslationMomentum();

        // A hard teleport also clears observations about the prior support/medium.
        Array.Clear(array: m_motionRecency);
        m_inMedium = false;
        m_atMediumBand = false;
    }
    // Stops integrated locomotion without claiming a teleport, changing support facts, or cancelling timed actions.
    private void ResetTranslationMomentum() {
        m_verticalVelocity = FixedQ4816.Zero;
        m_directVerticalVelocity = FixedQ4816.Zero;
        m_verticalVelocityAccumulator.Reset();

        // A teleport must not carry momentum: drop the ramped planar velocity, its accumulator carries (the
        // isotropic ramp and an anisotropic row's decomposed channels alike), the shaping table's recency clocks, and the
        // dynamics followers' own state and previous-target carries.
        m_planarVelocity = default;
        m_planarRampAccumulator.Reset();
        m_driveLongAccumulator.Reset();
        m_driveLatAccumulator.Reset();
        m_driveResidualAccumulator.Reset();
        m_planarFollower = default;
        m_planarPreviousTarget = default;
        m_planarFollowerSeeded = false;
        m_verticalFollower = default;
        m_verticalPreviousTarget = default;
        m_verticalFollowerSeeded = false;

        m_mediumThrustRampAccumulator.Reset();
    }
    // The one seat-time resolve. Shared by Advance (which feeds this into the program) and EffectiveMoveSpeed
    // (which only reads it back) so the two can never compute two different answers to "what speed is this body
    // actually moving at". One law for every kit: the seated profile's claimed rate, else the kit's own, clamped by
    // the kit's envelope. A kart pins its speed with min == max rather than opting out of the profile read; a held
    // speed multiplier (a drive's boost) multiplies AFTER this clamp, never inside it (see ApplySpeedHeld).
    private FixedQ4816 ResolveMoveSpeed() {
        var resolved = (Profile?.FixedMoveSpeed ?? m_tuning.Speed.Value);
        var clamped = (m_tuning.Speed.Envelope?.Clamp(value: resolved) ?? resolved);

        return ((clamped * m_scale) * m_travelMultiplier);
    }
    // A body's own authored-field answer this tick, or false when the world authors no field or this body matched no
    // local area in an area-only field. True does NOT imply a nonzero vector: Replace can author a zero-G pocket,
    // Combine can cancel exactly, and a radial contribution is zero at its own centre.
    private bool TrySolvedGravity(out FixedVector3 acceleration) {
        acceleration = FixedVector3.Zero;

        return (
            (m_gravityField is { } field) &&
            field.TryAcceleration(
            acceleration: out acceleration,
            entityIndex: m_population is null ? m_entityIndex : m_populationIndex
        )
        );
    }
    private bool TrySolvedGravityMagnitude(out FixedQ4816 magnitude) {
        magnitude = FixedQ4816.Zero;

        if (!TrySolvedGravity(acceleration: out var acceleration)) {
            return false;
        }

        magnitude = acceleration.Length;

        return true;
    }
    // Moving the up axis rotates the frame the planar velocity lives in, so the velocity has to be carried with it.
    // Re-reading a stored tangent vector in a rotated frame is not the same motion: on a curved surface the component
    // that was tangent a tick ago points off the surface now, and re-applying it launches the body — the faster it
    // runs, the harder it is thrown. Rotating by the shortest arc between the two axes preserves both speed and
    // heading along the surface, which is what running around a sphere is.
    private void SetUp(FixedVector3 next) {
        if (
            (next == FixedVector3.Zero) ||
            (next == m_up)
        ) {
            return;
        }

        var transport = FixedQuaternion.FromTo(
            from: m_up,
            to: next
        );

        m_planarVelocity = transport.Rotate(vector: m_planarVelocity);

        // The follower's own velocity lane is an acceleration direction, not a re-derivable fact — carrying it
        // through the same transport keeps a live overshoot/anticipation curving with the surface instead of
        // snapping to whatever the next re-seed (StepPlanarFollower's position sync) happens to leave it pointing.
        // The position lane is left untouched here: it already tracks m_planarVelocity (rotated above) through that
        // same re-seed, next step.
        if (m_tuning.HasDynamics) {
            var rotatedVelocity = transport.Rotate(vector: m_planarFollower.Velocity);

            m_planarFollower = new SecondOrderState3(
                X: m_planarFollower.X.WithVelocity(velocity: rotatedVelocity.X),
                Y: m_planarFollower.Y.WithVelocity(velocity: rotatedVelocity.Y),
                Z: m_planarFollower.Z.WithVelocity(velocity: rotatedVelocity.Z)
            );
            m_planarPreviousTarget = transport.Rotate(vector: m_planarPreviousTarget);
        }

        m_frame = (transport * m_frame).Normalize();
        m_up = next;
    }
    // Turns the held up axis TOWARD a field-derived target, by at most one step's share of FieldUpTurnRate.
    //
    // A field's direction is only as trustworthy as its magnitude, and the two fail together. Wherever an attractor's
    // pull cancels the world's — every attractor carries such a surface, a shell where the two balance — the
    // magnitude passes through zero while the direction reverses across it. A body reading that direction raw adopts
    // a full half turn between one tick and the next: FromTo answers the antipodal pair with a deterministic but
    // arbitrary axis, and both the planar velocity and the carried frame are yanked 180 degrees through it. Held
    // against such a shell the body simply buzzes, flipping end over end at tick rate and never crossing.
    //
    // Bounding the turn makes the crossing what it physically is — one continuous half turn taking about a second —
    // and costs nothing anywhere else: an ordinary field turns far slower than the budget, so the target is reached
    // in the same tick and the axis is exactly what it was before. The antipodal case is passed THROUGH rather than
    // jumped across, so no step ever asks FromTo for an undefined axis.
    //
    // Only the FIELD-derived axis is steered. A contact normal is a measurement of the surface underfoot, not a
    // reading of a field that can vanish, so a grounded body still adopts it exactly.
    private void SteerUp(FixedVector3 target, ulong stepTicks) => _ = SteerUpToward(
        accumulator: ref m_upTurnAccumulator,
        halfRate: FieldUpTurnHalfRate,
        stepTicks: stepTicks,
        target: target
    );
    // Turns the held up axis toward a target by at most one step's share of halfRate, and answers the rotation it
    // actually applied so a caller can carry anything else living in that frame by the SAME arc.
    private FixedQuaternion SteerUpToward(FixedVector3 target, ulong stepTicks, FixedQ4816 halfRate, ref FixedRateAccumulator accumulator) {
        // The budget is accumulated as the HALF angle the rotor is built from, so the turn it authorizes is twice
        // halfRate and no runtime halving is needed.
        var budget = accumulator.Integrate(
            elapsedTicks: stepTicks,
            ratePerSecond: halfRate
        );

        if (budget <= FixedQ4816.Zero) {
            return FixedQuaternion.Identity;
        }

        var rotation = FixedQuaternion.FromTo(
            from: m_up,
            to: target
        );
        // FromTo's W is cos(half the turn) and never negative, so a LARGER W is a SMALLER turn: the target is within
        // budget exactly when its W is at or above the budget's own.
        var (halfSin, halfCos) = FixedQ4816.SinCos(angle: budget);

        if (rotation.W >= halfCos) {
            SetUp(next: target);

            return rotation;
        }

        var axis = new FixedVector3(
            X: rotation.X,
            Y: rotation.Y,
            Z: rotation.Z
        ).Normalize();

        if (axis == FixedVector3.Zero) {
            SetUp(next: target);

            return rotation;
        }

        var step = new FixedQuaternion(
            W: halfCos,
            X: (axis.X * halfSin),
            Y: (axis.Y * halfSin),
            Z: (axis.Z * halfSin)
        );

        SetUp(next: step.Rotate(vector: m_up).Normalize());

        return step;
    }
    // The ONE body-frame authority. Every policy follows solved gravity (or the contact field's ambient fallback),
    // preserving gravity's existing directional contract. SurfaceFollowing alone may keep a measured support normal
    // while grounded. A degenerate ambient query leaves the held value untouched rather than snapping to an arbitrary
    // direction. Centralizing every policy arm here makes a live policy rebuild authoritative on the very next step
    // instead of retaining whichever surface the previous policy last adopted.
    private FixedVector3 ResolveUp(ulong stepTicks) {
        // A SurfaceFollowing STANDING body's up is the surface it stands on, and it keeps it. The two candidates
        // disagree wherever a surface is not perpendicular to the field — every point of a planetoid under any second
        // attractor — and recomputing from the field each tick would flip the axis back and forth between them,
        // rotating the planar velocity one way and then the other until the body simply stops making progress.
        if (
            (m_upPolicy == WorldBodyUpPolicy.SurfaceFollowing) &&
            m_grounded &&
            TrySolvedGravity(acceleration: out _) &&
            (m_up != FixedVector3.Zero)
        ) {
            return m_up;
        }

        // Airborne, the field decides: it is what the body is falling toward, and it is what the walkable test the
        // next landing runs measures against. It decides by STEERING the held axis, never by replacing it — see
        // SteerUp for why reading the raw direction each tick does not survive a null surface.
        if (TrySolvedGravity(acceleration: out var acceleration)) {
            // Below the floor the field carries no usable direction at all, so the held axis stands: the magnitude
            // that would orient the body is the same one that has become too small to mean anything.
            if (acceleration.Length >= MinFieldUpMagnitude) {
                var down = acceleration.Normalize();

                if (down != FixedVector3.Zero) {
                    if (m_upNeedsReseat) {
                        m_upNeedsReseat = false;

                        SetUp(next: -down);
                    } else {
                        SteerUp(
                            stepTicks: stepTicks,
                            target: -down
                        );
                    }
                }
            }

            return m_up;
        }

        if (
            (m_contactField is { } field) &&
            (m_collider is not null) &&
            field.TryUp(
            position: in m_position,
            up: out var up
        )
        ) {
            if (m_upNeedsReseat) {
                m_upNeedsReseat = false;
            }

            SetUp(next: up);
        }

        return m_up;
    }
    // --- The anisotropic shaping row (ResolveDriveFrame plus ShapeVelocity's across-decomposition branch). ---

    // The turn tuning's speed-scaled authority curve, shared by every yaw-writing frame op: rises linearly from zero
    // at standstill to full at turn.referenceSpeed, falls linearly to turn.falloff× at the RESOLVED
    // (envelope-clamped) move speed — moveSpeed, so a clamped kit's falloff anchor moves with its clamp instead of an
    // unreachable authored rate — and reverses sign with reversing travel (a car backing up, not a turret). A kit
    // authoring no reference speed reads full authority at every speed (the compiled sentinel: ReferenceSpeed <= 0).
    // The governing shaping row's own turnScale multiplies the result — the tightened drift arc's spelling, and 1
    // for every ordinary row.
    private FixedQ4816 ResolveTurnAuthority(int governingRow, FixedQ4816 moveSpeed) {
        var turn = m_tuning.Turn;
        var turnScale = ((governingRow >= 0)
            ? m_tuning.Shaping[governingRow].TurnScale
            : FixedQ4816.One
        );

        if (turn.ReferenceSpeed <= FixedQ4816.Zero) {
            return turnScale;
        }

        // Shaping runs after every frame op, so the one-tick-old velocity/attitude is the deterministic witness
        // available here.
        var previousFacing = m_orientation.Rotate(vector: -FixedVector3.UnitZ);
        var longitudinal = FixedVector3.Dot(
            left: m_planarVelocity,
            right: previousFacing
        );
        var speed = FixedQ4816.Abs(value: longitudinal);
        var authority = ((speed >= turn.ReferenceSpeed)
            ? FixedQ4816.One
            : (speed / turn.ReferenceSpeed)
        );

        if (
            (speed > turn.ReferenceSpeed) &&
            (moveSpeed > turn.ReferenceSpeed)
        ) {
            var over = FixedQ4816.Clamp(
                value: ((speed - turn.ReferenceSpeed) / (moveSpeed - turn.ReferenceSpeed)),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );

            authority = (FixedQ4816.One + (over * (turn.Falloff - FixedQ4816.One)));
        }

        if (longitudinal < FixedQ4816.Zero) {
            authority = -authority;
        }

        return (authority * turnScale);
    }
    // The drive frame (phase 0): resolve up, integrate speed-scaled steering into the heading, and (under a
    // positive pitchRate) integrate the Pitch channel into the clamped pitch scalar; facing/right derive from the
    // fresh yaw(+pitch) attitude. The rate at full authority is the kit's own turn.rate (scratch.TurnSpeed), the
    // same one every other frame operation turns at.
    private void ResolveDriveFrame(ref BodyMotionScratch scratch) {
        scratch.Up = ResolveUp(stepTicks: scratch.StepTicks);
        scratch.AttitudeUp = scratch.Up;

        var authority = ResolveTurnAuthority(
            governingRow: scratch.GoverningShapingRow,
            moveSpeed: scratch.MoveSpeed
        );
        var yawRate = ((Role(
            intent: in scratch.Intent,
            role: ChannelRole.Turn
        ) * scratch.TurnSpeed) * authority);
        var pitchRate = ((m_tuning.Turn.PitchRate > FixedQ4816.Zero)
            ? (Role(
                intent: in scratch.Intent,
                role: ChannelRole.Pitch
            ) * m_tuning.Turn.PitchRate)
            : FixedQ4816.Zero
        );
        var angleStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: yawRate,
                Y: pitchRate,
                Z: FixedQ4816.Zero
            ),
            elapsedTicks: scratch.StepTicks
        );

        m_yaw += angleStep.X;
        m_drivePitch = FixedQ4816.Clamp(
            value: (m_drivePitch + angleStep.Y),
            minimum: -MaxDrivePitch,
            maximum: MaxDrivePitch
        );

        var attitude = ((m_drivePitch == FixedQ4816.Zero)
            ? FixedQuaternion.FromAxisAngle(
                angle: m_yaw,
                axis: FixedVector3.UnitY
            )
            : FixedQuaternion.FromYawPitchRoll(
                pitch: m_drivePitch,
                roll: FixedQ4816.Zero,
                yaw: m_yaw
            )
        );

        scratch.Orientation = ((scratch.Up == FixedVector3.UnitY)
            ? attitude
            : (m_frame * attitude)
        );
        scratch.Facing = scratch.Orientation.Rotate(vector: -FixedVector3.UnitZ);
        scratch.Right = scratch.Orientation.Rotate(vector: FixedVector3.UnitX);
    }
    // The grounded integration — planar math for the horizontal axes plus the bound vertical action on the other.
    // Horizontal: under MotionMoveFrame.Heading (the default, tank controls) turn the heading, step along the fresh
    // facing/right (instant velocity — no ground/air acceleration yet). A pure-yaw facing/right carry no Y, so the
    // horizontal step never disturbs the vertical axis the action owns. Under MotionMoveFrame.World the two channels
    // are ALREADY world-frame (the seat composed its camera yaw client-side before submission — determinism: no
    // camera state ever enters the sim), so the heading integrator is bypassed entirely and facing only ever moves
    // via FacingSnap, below. Trigger instructions may write vertical velocity before gravity and integration. The
    // MoveUp/Pitch/Roll channels stay inert; contact geometry owns the resting altitude.
    private void ResolveYawAttitudeAndPlanarFrame(ref BodyMotionScratch scratch) {
        scratch.Up = ResolveUp(stepTicks: scratch.StepTicks);
        scratch.AttitudeUp = scratch.Up;

        var authority = ResolveTurnAuthority(
            governingRow: scratch.GoverningShapingRow,
            moveSpeed: scratch.MoveSpeed
        );

        if (m_tuning.MoveFrame == MotionMoveFrame.World) {
            // World-frame movement means the TRANSLATION axes are already resolved by the seat; it does not make
            // the authored Turn role inert. Integrate facing independently so an author can choose camera-facing
            // strafe (FacingSnap=false plus right-stick.x -> Turn) or movement-facing locomotion (FacingSnap=true,
            // whose later SnapYawToPlanarIntent remains the final word while movement is nonzero).
            //
            // The tick movement stops, the body KEEPS the way it was facing: the attitude the facing snap left it in
            // becomes the heading (what you see is what "forward" now is), rather than swinging back to the heading
            // it moved against. Read from the persisted attitude, no flag: an idle body's attitude was rebuilt from
            // m_yaw last tick, so the two agree within rounding and nothing is adopted.
            if (
                !HasTimedYaw &&
                m_tuning.FacingSnap &&
                (Role(
                intent: in scratch.Intent,
                role: ChannelRole.MoveAdvance
            ) == FixedQ4816.Zero) &&
                (Role(
                intent: in scratch.Intent,
                role: ChannelRole.MoveStrafe
            ) == FixedQ4816.Zero)
            ) {
                var facingYaw = m_orientation.Yaw();

                if (FixedQ4816.Abs(value: FixedQ4816.WrapAngle(radians: (facingYaw - m_yaw))) > FacingAdoptEpsilon) {
                    m_yaw = facingYaw;
                }
            }

            IntegrateYawInput(scratch: in scratch, authority: authority);

            // World-frame movement resolves the TRANSLATION axes from the seat rather than the body's facing; it does
            // not mean the body ignores which way is up. The yaw attitude composes under the same up tilt the local
            // frame uses, and the axes come off that orientation, so a world-framed walker on a curved surface travels
            // its tangent instead of a fixed world direction. Identical to a hardcoded -Z/+X while up is world +Y, so
            // a flat world integrates exactly as before.
            var worldAttitude = FixedQuaternion.FromAxisAngle(
                angle: m_yaw,
                axis: FixedVector3.UnitY
            );

            scratch.Orientation = ((scratch.Up == FixedVector3.UnitY)
                ? worldAttitude
                : (m_frame * worldAttitude)
            );
            // The TRANSLATION basis is the world's own axes carried into the body's up frame, NEVER the heading
            // attitude just built. Under MotionMoveFrame.World the seat has ALREADY rotated the stick into world
            // axes before submitting it (WorldClient.ComposeMoveFrame), so resolving it against the heading rotates
            // it a second time: the body travels at exactly m_yaw to where it faces, and because the heading keeps
            // integrating the Turn role — and re-adopts the snapped attitude whenever movement stops — that angle
            // grows with every turn instead of staying put.
            //
            // The attitude and the basis are answering different questions here, which is why they must not share a
            // rotation: the attitude is which way the body is DRAWN (the facing snap has the final word on it, from
            // the same world-frame stick vector), and the basis is what "forward" and "strafe" MEAN. Composing the
            // basis from the up frame alone keeps a planetoid walker travelling its own tangent while leaving a flat
            // world's axes exactly the hardcoded -Z/+X they were.
            scratch.Facing = ((scratch.Up == FixedVector3.UnitY)
                ? -FixedVector3.UnitZ
                : m_frame.Rotate(vector: -FixedVector3.UnitZ)
            );
            scratch.Right = ((scratch.Up == FixedVector3.UnitY)
                ? FixedVector3.UnitX
                : m_frame.Rotate(vector: FixedVector3.UnitX)
            );

            return;
        }

        IntegrateYawInput(scratch: in scratch, authority: authority);
        var yawRotation = FixedQuaternion.FromAxisAngle(
            angle: m_yaw,
            axis: FixedVector3.UnitY
        );

        scratch.Orientation = ((scratch.Up == FixedVector3.UnitY)
            ? yawRotation
            : (m_frame * yawRotation)
        );
        scratch.Facing = scratch.Orientation.Rotate(vector: -FixedVector3.UnitZ);
        scratch.Right = scratch.Orientation.Rotate(vector: FixedVector3.UnitX);
    }

    private void IntegrateYawInput(in BodyMotionScratch scratch, FixedQ4816 authority) {
        if (HasTimedYaw) { return; }

        var angleStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: ((Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.Turn
                ) * scratch.TurnSpeed) * authority),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            elapsedTicks: scratch.StepTicks
        );

        m_yaw += angleStep.X;
    }
    private void SenseTarget(BodySensorTarget candidate, ref BodyMotionScratch scratch) {
        var producer = scratch.Producer!;
        var current = scratch.ProducerSensors.CurrentTarget;

        // Release-radius hysteresis exists to damp flicker among a COMPETITIVELY sensed population (Sensed alone);
        // a Designated register and a Curve follow-point are each a single deterministic candidate every tick with
        // nothing to flicker against, so both take the fresh candidate outright. Flocks retain observations on
        // their own bounded perception cadence and do not consume the approach shape's release-radius scalar.
        if (
            (producer.Flock is not null) ||
            (producer.Target?.Source is not BodyTargetSource.Sensed)
        ) {
            scratch.SensorTarget = candidate;
        } else {
            var release = producer.Scalar(parameter: BodyProducerParameter.ReleaseRadius);

            if (
                current.Exists &&
                (current.DistanceSquared <= (release * release))
            ) {
                scratch.SensorTarget = current;
            } else {
                scratch.SensorTarget = candidate;
            }
        }

        scratch.ProducerState.AcquiredTarget = scratch.SensorTarget.Index;
    }
    // --- The shaping table (the Shape stage). ---
    // Converges velocity on the commanded intent through the kit's shaping table: the first row whose gate opens
    // governs. No row matching (an empty/absent table, or every gated row closed) snaps instantly — the built-in
    // fallback every ApplyMedium vertical lane shares, and the only path a kit declaring no table at all takes.
    private void ShapeVelocity(ref BodyMotionScratch scratch) {
        var governingRow = scratch.GoverningShapingRow;

        if (governingRow < 0) {
            m_planarVelocity = scratch.TargetVelocity;
            scratch.Velocity = m_planarVelocity;

            return;
        }

        var row = m_tuning.Shaping[governingRow];

        if (row.Dynamics is { Planar: { } planar }) {
            // scratch.StepTicks can differ from planar.StepTicks for exactly one tick — see the identical note in
            // ApplyMedium.
            var ceiling = ApplySpeedHeld(
                baseSpeed: ResolveMoveSpeed(),
                intent: in scratch.Intent
            );

            scratch.Velocity = StepPlanarFollower(
                ceiling: ceiling,
                step: in planar,
                target: scratch.TargetVelocity
            );

            return;
        }

        if (row.Across is { } across) {
            ShapeAnisotropicVelocityRow(
                across: in across,
                along: row.Along!.Value,
                scratch: ref scratch
            );

            return;
        }

        ShapeWholeVelocityRow(
            along: row.Along!.Value,
            scratch: ref scratch
        );
    }
    // The whole-vector response law: converge the ramped planar velocity on the commanded target at the row's own
    // engage rate while the stick is deflected, its release rate while centered (the has-input axis is a property
    // of the command, not a body fact).
    private void ShapeWholeVelocityRow(in FixedShapingAlong along, ref BodyMotionScratch scratch) {
        var hasInput = ((Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveAdvance
        ) != FixedQ4816.Zero) || (Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveStrafe
        ) != FixedQ4816.Zero));
        var instant = (hasInput
            ? ShapingInstant.Engage
            : ShapingInstant.Release
        );

        if ((along.Instant & instant) != 0) {
            m_planarRampAccumulator.Reset();
            m_planarVelocity = scratch.TargetVelocity;
        } else {
            var rate = (hasInput
                ? along.Engage
                : along.Release
            );
            var maxDelta = m_planarRampAccumulator.Integrate(
                elapsedTicks: scratch.StepTicks,
                ratePerSecond: rate
            );

            m_planarVelocity = FixedVector3.MoveToward(
                current: m_planarVelocity,
                maxDelta: maxDelta,
                target: scratch.TargetVelocity
            );
        }
        scratch.Velocity = m_planarVelocity;
    }
    // The drive decomposition: split the carried velocity into heading-frame longitudinal/lateral/residual
    // components, converge each at its own authored rate, and recompose — the anisotropy a wheeled body's feel needs
    // and the isotropic MoveToward cannot express. The heading is the attitude the frame operation resolved, never
    // the translation basis: under a World move frame those are the world's axes, and a wheeled body grips across the
    // way it faces, not across world X.
    //
    // Longitudinal converges on the commanded target's advance along the heading — the velocity
    // ComputePlanarTargetVelocity resolved, so a seat's throttle, a World-framed stick and a producer's steering all
    // drive it alike, with the envelope-clamped move speed and any held multiplier already applied. Commanded
    // backward while moving forward reverses sign at the reversal rate, and from rest travels backward at the
    // commanded fraction of the move speed times the unenveloped backward speed; the over-speed excess bleeds at
    // release, which is also the coast with nothing commanded. Lateral and residual slip converge to zero at the
    // row's own lateral rate, whatever the target asks across the heading — a held-gated drift row authored ahead of
    // the ordinary anisotropic row is how a kart's lateral grip swaps while held. A contact-pinned variant
    // (turn.pitchRate zero) has no drive or lateral authority while airborne: a launched kart holds its velocity and
    // gravity owns the arc.
    private void ShapeAnisotropicVelocityRow(in FixedShapingAlong along, in FixedShapingAcross across, ref BodyMotionScratch scratch) {
        var facing = scratch.Orientation.Rotate(vector: -FixedVector3.UnitZ);
        var right = scratch.Orientation.Rotate(vector: FixedVector3.UnitX);
        var advance = FixedVector3.Dot(
            left: scratch.TargetVelocity,
            right: facing
        );
        var hasAuthority = ((m_tuning.Turn.PitchRate > FixedQ4816.Zero) || m_grounded);
        var velocity = m_planarVelocity;
        var longitudinal = FixedVector3.Dot(
            left: velocity,
            right: facing
        );
        var lateral = FixedVector3.Dot(
            left: velocity,
            right: right
        );
        var residual = ((velocity - (facing * longitudinal)) - (right * lateral));

        if (hasAuthority) {
            FixedQ4816 target, rate;
            ShapingInstant instant;

            if (advance > FixedQ4816.Zero) {
                target = advance;
                rate = ((longitudinal <= target)
                    ? along.Engage
                    : along.Release
                );
                instant = ((longitudinal <= target)
                    ? ShapingInstant.Engage
                    : ShapingInstant.Release
                );
            } else if (advance < FixedQ4816.Zero) {
                if (longitudinal > FixedQ4816.Zero) {
                    target = FixedQ4816.Zero;
                    rate = along.ReversalRate;
                    instant = ShapingInstant.Reversal;
                } else {
                    var ceiling = ApplySpeedHeld(
                        baseSpeed: scratch.MoveSpeed,
                        intent: in scratch.Intent
                    );

                    target = ((ceiling > FixedQ4816.Zero)
                        ? (FixedQ4816.Clamp(value: (advance / ceiling), minimum: NegativeOne, maximum: FixedQ4816.Zero) * along.BackwardSpeed)
                        : FixedQ4816.Zero
                    );
                    rate = along.Engage;
                    instant = ShapingInstant.Engage;
                }
            } else {
                target = FixedQ4816.Zero;
                rate = along.Release;
                instant = ShapingInstant.Release;
            }

            if ((along.Instant & instant) != 0) {
                m_driveLongAccumulator.Reset();
                longitudinal = target;
            } else {
                longitudinal = FixedQ4816.MoveToward(
                    current: longitudinal,
                    target: target,
                    maxDelta: m_driveLongAccumulator.Integrate(
                        elapsedTicks: scratch.StepTicks,
                        ratePerSecond: rate
                    )
                );
            }

            if (across.Instant) {
                m_driveLatAccumulator.Reset();
                m_driveResidualAccumulator.Reset();
                lateral = FixedQ4816.Zero;
                residual = default;
            } else {
                lateral = FixedQ4816.MoveToward(
                    current: lateral,
                    target: FixedQ4816.Zero,
                    maxDelta: m_driveLatAccumulator.Integrate(
                        elapsedTicks: scratch.StepTicks,
                        ratePerSecond: across.Lateral
                    )
                );
                residual = FixedVector3.MoveToward(
                    current: residual,
                    target: default,
                    maxDelta: m_driveResidualAccumulator.Integrate(
                        elapsedTicks: scratch.StepTicks,
                        ratePerSecond: across.Lateral
                    )
                );
            }
        }

        m_planarVelocity = (((facing * longitudinal) + (right * lateral)) + residual);
        scratch.Velocity = m_planarVelocity;
        scratch.TargetVelocity = m_planarVelocity;
    }
    // A commanded facing (FaceX/FaceY/FaceZ, a world-frame direction) is the final word on the HEADING whenever its
    // planar part is nonzero — the body turns to it, heading and attitude both. Otherwise, under World frame +
    // FacingSnap, the body's ATTITUDE alone turns to face its commanded travel while the heading (m_yaw — the Turn
    // role's integral, and the frame a heading-framed seat moves in) holds: a strafe angles the body toward its
    // travel without changing which way "forward" is, and the attitude returns to the heading the tick movement
    // stops (ResolveYawAttitudeAndPlanarFrame rebuilds it from m_yaw every tick). Both snaps share one convention:
    // yaw = atan2 of a world planar direction, facing (-sin yaw, -cos yaw). FaceY is elevation — no yaw to take
    // from it.
    private void SnapYawToPlanarIntent(ref BodyMotionScratch scratch) {
        if (HasTimedYaw) { return; }

        var faceX = Role(
            intent: in scratch.Intent,
            role: ChannelRole.FaceX
        );
        var faceZ = Role(
            intent: in scratch.Intent,
            role: ChannelRole.FaceZ
        );

        if (
            (faceX != FixedQ4816.Zero) ||
            (faceZ != FixedQ4816.Zero)
        ) {
            // A hard teleport earlier in this tick commands the heading. This tick's intent was composed before it,
            // against the pose the body left, so its facing does not undo the teleport; the next tick's intent is
            // composed against the posed body.
            if (m_continuity.Kind == EntityContinuityKind.Teleport) {
                return;
            }

            SnapYaw(
                scratch: ref scratch,
                yaw: (new FixedVector3(
                    X: faceX,
                    Y: FixedQ4816.Zero,
                    Z: faceZ
                )).Yaw()
            );

            return;
        }

        if (
            (m_tuning.MoveFrame != MotionMoveFrame.World) ||
            !m_tuning.FacingSnap ||
            ((Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveAdvance
        ) == FixedQ4816.Zero) && (Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveStrafe
        ) == FixedQ4816.Zero))
        ) {
            return;
        }

        // The commanded world-frame direction: advance along −Z, strafe along +X.
        SnapFacing(
            scratch: ref scratch,
            yaw: (new FixedVector3(
                X: Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.MoveStrafe
                ),
                Y: FixedQ4816.Zero,
                Z: -Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.MoveAdvance
                )
            )).Yaw()
        );
    }
    private void SnapYaw(ref BodyMotionScratch scratch, FixedQ4816 yaw) {
        m_yaw = yaw;
        SnapFacing(
            scratch: ref scratch,
            yaw: yaw
        );
    }
    // The attitude alone — the heading (m_yaw) is untouched.
    private static void SnapFacing(ref BodyMotionScratch scratch, FixedQ4816 yaw) {
        var attitude = FixedQuaternion.FromAxisAngle(
            angle: yaw,
            axis: FixedVector3.UnitY
        );

        // The snapped heading is a yaw about the axis the body's ATTITUDE stands against, so it composes under the
        // same tilt the frame resolve applies; assigning the bare yaw would drop a planetoid walker back to a
        // world-upright attitude mid-stride. That axis is ordinarily the contact axis and differs from it only where
        // a hold's lean has put the drawn body on a face the solver still measures against gravity — composing about
        // the contact axis there would flatten the lean out again on the very next phase.
        scratch.Orientation = ((scratch.AttitudeUp == FixedVector3.UnitY)
            ? attitude
            : (FixedQuaternion.FromTo(
                from: FixedVector3.UnitY,
                to: scratch.AttitudeUp
            ) * attitude)
        );
    }
    // Resolve this sub-step's full intent by the IntentSource merge rule: a live tape segment takes precedence for the
    // movement channels (consumed whole-frame, dropped when its time runs out; expired/empty front segments are
    // skipped first, so a drained tape falls through the same frame it empties); with the tape dry, the tick's
    // submitted intent (admitted unless Idle), else the producer image (iff the source names it), else zero. The
    // action-track lanes are then overlaid, so a wire body.press jumps a tape-driven runner.
    // Whether an intent source names a server-side producer whose staged image fills gaps.
    private static bool SourceNamesProducer(IntentSource source) => source.IsProducer;
    /// <summary>Updates the latched <c>world.contacts</c> obstruction witness from this tick's raw solver result. A
    /// fresh non-walkable push always (re)latches immediately and refills the grace window. Absent one, the
    /// existing latch clears immediately the instant either releasing condition holds — the raw planar move intent
    /// (<paramref name="intent"/>'s MoveAdvance/MoveStrafe roles, resolved once by <c>NextIntent</c> before any op
    /// runs — never a program-computed velocity, which may not be written yet at this exact point depending on op
    /// order, and which — once written — is the response-ramped result the wall itself just clipped, risking a
    /// feedback loop where a wall stopping the body reads back as "input released") has gone idle, or the body has
    /// meaningfully moved since the latch was captured — so the witness can never claim an obstruction the body has
    /// actually left or stopped pressing against. Short of either, the latch survives a solver pass reporting no
    /// push at all by spending down a brief grace window before giving up — ordinary query noise near a surface
    /// (the field provider's SDF gradient can land exactly on a quantization boundary, or a body settled into a
    /// blended wall/ground corner can drift in and out of the walkable classification for many consecutive ticks
    /// while genuinely never clearing) must not flicker the witness while the body is provably still pinned and
    /// still pressing.</summary>
    private void UpdateObstructionWitness(FixedVector3 rawObstruction, in PlayerIntent intent, FixedVector3 position, ulong stepTicks) {
        if (rawObstruction != FixedVector3.Zero) {
            m_obstructionWitness = rawObstruction;
            m_obstructionWitnessPosition = position;
            m_obstructionWitnessGraceTicks = ObstructionLatchGraceTicks;

            return;
        }

        if (m_obstructionWitness == FixedVector3.Zero) {
            return;
        }

        var forward = Role(
            intent: in intent,
            role: ChannelRole.MoveAdvance
        );
        var strafe = Role(
            intent: in intent,
            role: ChannelRole.MoveStrafe
        );
        var idle = ((FixedQ4816.Abs(value: forward) <= ObstructionLatchIdleThreshold) && (FixedQ4816.Abs(value: strafe) <= ObstructionLatchIdleThreshold));
        var displaced = ((position - m_obstructionWitnessPosition).LengthSquared > ObstructionLatchDisplacementSquared);

        if (
            idle ||
            displaced
        ) {
            m_obstructionWitness = FixedVector3.Zero;
            m_obstructionWitnessGraceTicks = 0;

            return;
        }

        m_obstructionWitnessGraceTicks = m_obstructionWitnessGraceTicks.SubtractSaturating(right: stepTicks);

        if (m_obstructionWitnessGraceTicks == 0) {
            m_obstructionWitness = FixedVector3.Zero;
        }
    }

    private struct BodyMotionScratch {
        public PlayerIntent Intent;
        public CompiledBodyProducer? Producer;
        public BodyProducerState ProducerState;
        public BodyProducerSensors ProducerSensors;
        public BodySensorTarget SensorTarget;
        public FixedQ4816 MoveSpeed;
        public FixedQ4816 TurnSpeed;
        public ulong StepTicks;
        public FixedVector3 Up;
        // The axis the body's drawn attitude stands against — Up unless a hold's lean has moved it (see
        // WorldBody.Hold.cs's SetHoldFrame). Every attitude writer composes about this; every solver read uses Up.
        public FixedVector3 AttitudeUp;
        public FixedVector3 Facing;
        public FixedVector3 Right;
        public FixedVector3 TargetVelocity;
        public FixedQ4816 DirectVerticalVelocity;
        public FixedVector3 Velocity;
        public FixedVector3 NextPosition;
        public FixedQuaternion Orientation;
        public int EntityIndex;
        public BodyEffectTargets EffectTargets;
        public List<BodyEffectOutput>? EffectOutputs;
        public List<WorldDesignation>? DesignationOutputs;
        public List<WorldGeneratorInvocation>? GeneratorInvocations;
        public int GoverningShapingRow;
        public BodySupportMemo SupportMemo;
    }
}

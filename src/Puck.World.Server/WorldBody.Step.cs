using System.Numerics;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Advances the body by one exact host-owned simulation step from its single merged intent: a live tape
    /// segment if one is queued, otherwise the tick's submitted intent. The selected program chooses fixed domain
    /// operations while the host retains phase order and the contact point.</summary>
    /// <param name="tick">The explicit simulation tick whose staged durable inputs may enter.</param>
    /// <param name="stepTicks">The exact engine ticks this call advances.</param>
    /// <param name="engageProbeOrdinal">The context-sensitive-button interception (the RPG A-button): a channel
    /// ordinal to test for a rising edge against this same tick's composed intent, before any integration runs, or
    /// <see langword="null"/> for no probe (every body away from an engageable-by-channel screen, and every world
    /// with none — the default, zero-cost path). <see cref="Puck.World.Server.WorldServer.Step"/> resolves eligibility
    /// (screen radius, machine-bearing, un-engaged, authority) before calling this and supplies the ordinal only when
    /// eligible; a fired edge here only reports it — the caller performs the actual
    /// <see cref="Puck.World.Server.WorldEngagement.Compose"/> afterward, through the same authority path a manual
    /// <c>body.engage</c> takes.</param>
    /// <param name="entityIndex">The source body's population index.</param>
    /// <param name="effectTargets">The pre-step entity target image.</param>
    /// <param name="effectOutputs">Receives non-self effects for post-advance application.</param>
    /// <param name="designationOutputs">Receives authored target-register submissions.</param>
    /// <param name="generatorInvocations">Receives staged <c>generate</c> effect firings, enqueued through the
    /// ordinary mutation pipeline after the whole population advance.</param>
    /// <param name="judgeInvocations">Receives staged <c>judge</c> effect firings — graded and folded into the
    /// server's last-grade table immediately after the whole population advance, against that step's own
    /// <c>ElapsedTicks</c> rather than any tick this method captures.</param>
    /// <returns><see langword="true"/> when <paramref name="engageProbeOrdinal"/>'s rising edge fired this tick
    /// (the caller should engage); otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is zero.</exception>
    internal bool Advance(ulong tick, ulong stepTicks, int? engageProbeOrdinal = null, int entityIndex = -1, BodyEffectTargets effectTargets = default, List<BodyEffectOutput>? effectOutputs = null, List<WorldDesignation>? designationOutputs = null, List<WorldGeneratorInvocation>? generatorInvocations = null, List<WorldJudgeInvocation>? judgeInvocations = null) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);

        // Captured before ExecuteProgram (or the overlay add below) can move m_position — the swept portal-crossing
        // scan's segment start for this step. A hard teleport between scans overwrites this separately (CommitTeleport).
        m_previousPosition = m_position;

        ApplyDurableInput(tick: tick);
        MaterializeDefaultLanePresses(stepTicks: stepTicks);

        // The full merged intent for this sub-step: NextIntent expresses the whole precedence (movement channels —
        // tape > submitted, gated by the possession latch — with the action-track lanes overlaid).
        var intent = NextIntent(stepTicks: stepTicks);

        // Captured EVERY Advance, regardless of the latch — a mirrored application set needs this body's resolved
        // intent for its targets' translation/passthrough even while the avatar keeps integrating below. Reading it costs nothing beyond a struct copy already computed above.
        m_engagedIntent = intent;

        // The SAME edge test ProcessLaneActions uses below (bit crossing the ordinal's threshold, previous tick's bit
        // clear) — computed here, ahead of integration, so a fired edge can preempt it entirely rather than firing a
        // bound action (a jump) the same tick it engages.
        var engageEdge = ((engageProbeOrdinal is { } probeOrdinal)
            && (intent[probeOrdinal] >= m_channelThresholds[probeOrdinal])
            && !m_previousChannelBit[probeOrdinal]);

        if (
            m_engaged ||
            engageEdge
        ) {
            // Captured (m_engaged, i.e. capture:true), OR this tick's press is being diverted into an engage instead
            // of reaching the body (engageEdge): either way the resolved intent never reaches the avatar (no pose
            // integration, so the snapshot holds it stable). The action track below still advances, so a timed press
            // drains identically whether the intent drives the avatar or the route target.
        } else {
            // The attachment surface reads its own attach/detach/reel channels directly (never through the kit's
            // action table — see WorldBody.Attachment.cs), so it runs here, ahead of the program dispatch below, on
            // the SAME "intent reaches the avatar" gate as everything else in this branch. A detach that fires this
            // tick falls through to the ordinary ExecuteProgram path below in the SAME tick, already carrying the
            // momentum Detach wrote into the vertical/planar channels. Gravity stays on while a tether holds and
            // the kit's own program keeps integrating; WorldPopulation.ResolveTethers clamps the result to the rope
            // AFTER every body this tick has advanced.
            ProcessAttachmentIntent(intent: in intent);
            ProcessReel(
                intent: in intent,
                stepTicks: stepTicks
            );

            {
                // moveSpeed goes through ResolveMoveSpeed: the rate reads live off the seated profile every frame
                // (an identity.motion edit is real-time; a profileless stand-in falls back to the tuning's speed),
                // clamped by the kit's own authored MoveSpeedEnvelope when declared. The clamp lands BEFORE
                // ExecuteProgram ever sees the value, so the sim never observes an unclamped speed, and
                // EffectiveMoveSpeed's read-back echo performs the same resolve. No envelope (the default) is a
                // no-op clamp elided entirely; a kit pinning its speed outright authors min == max.
                var moveSpeed = ResolveMoveSpeed();
                var turnSpeed = (Profile?.FixedTurnSpeed ?? m_tuning.TurnSpeed);

                ExecuteProgram(
                    designationOutputs: designationOutputs,
                    effectOutputs: effectOutputs,
                    effectTargets: effectTargets,
                    entityIndex: entityIndex,
                    generatorInvocations: generatorInvocations,
                    intent: intent,
                    judgeInvocations: judgeInvocations,
                    moveSpeed: moveSpeed,
                    stepTicks: stepTicks,
                    turnSpeed: turnSpeed
                );

                // The timed impulse overlay rides after the selected program, through its own accumulator.
                if (m_overlayRemaining > 0) {
                    var overlayTicks = Math.Min(
                        val1: stepTicks,
                        val2: m_overlayRemaining
                    );

                    m_position += m_overlayAccumulator.Integrate(
                        elapsedTicks: overlayTicks,
                        ratePerSecond: m_overlayVelocity
                    );
                    m_overlayRemaining -= overlayTicks;

                    if (m_overlayRemaining == 0) {
                        m_overlayVelocity = default;
                        m_overlayAccumulator.Reset();
                    }
                }
            }
        }

        // The previous-bit image is written once after action evaluation; timed presses drain even under capture.
        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            m_previousChannelBit[ordinal] = (intent[ordinal] >= m_channelThresholds[ordinal]);
        }

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            m_laneTimers[lane] = SubtractSaturating(
                value: m_laneTimers[lane],
                amount: stepTicks
            );
        }

        // The held-channel image is a one-tick publish, like the submitted intent: the client republishes it every
        // submission, so a missed tick reads no channels rather than a stale hold.
        m_heldChannels = default;
        m_affectingSubject = -1;

        return engageEdge;
    }
    /// <summary>Applies one deterministic body-contact depenetration without turning it into a teleport.</summary>
    internal void ApplyDynamicContact(FixedVector3 correction) {
        if (correction == FixedVector3.Zero) {
            return;
        }

        m_position += correction;
        var normal = correction.Normalize();
        var velocity = (m_planarVelocity + (UnitY * m_verticalVelocity));
        var inward = FixedVector3.Dot(
            left: velocity,
            right: normal
        );

        if (inward < FixedQ4816.Zero) {
            velocity -= (normal * inward);
            m_planarVelocity = new FixedVector3(
                X: velocity.X,
                Y: FixedQ4816.Zero,
                Z: velocity.Z
            );
            if (m_verticalVelocity != velocity.Y) {
                m_verticalVelocity = velocity.Y;
                m_verticalVelocityAccumulator.Reset();
            }
        }
    }
    internal bool ApplyTargetedEffect(int sourceIndex, CompiledBodyInstruction instruction) {
        var slot = ((instruction.StateName is null)
            ? -1
            : FindActionState(name: instruction.StateName)
        );
        var applied = true;

        switch (instruction.Operation) {
            case BodyMotionOp.SetVerticalVelocity:
                m_verticalVelocity = instruction.Value;
                m_verticalVelocityAccumulator.Reset();
                break;
            case BodyMotionOp.ScaleVerticalVelocity:
                m_verticalVelocity *= instruction.Value;
                m_verticalVelocityAccumulator.Reset();
                break;
            case BodyMotionOp.PlanarImpulse:
                m_overlayVelocity = (m_orientation.Rotate(vector: instruction.Direction) * instruction.Value);
                m_overlayRemaining = instruction.DurationTicks;
                m_overlayAccumulator.Reset();
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
                var beforeAdd = m_actionStateValues[slot];
                ApplyRawState(
                    slot: slot,
                    requested: (m_actionStateValues[slot] + instruction.Value).Value,
                    writer: "foreign-effect",
                    reason: "addState"
                );
                MarkDurableDirty(
                    slot: slot,
                    kind: WorldDocumentWriteKind.Add,
                    operand: (m_actionStateValues[slot] - beforeAdd)
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
            m_affectingSubject = sourceIndex;
        }
        return applied;
    }
    internal void ExecuteProducer(CompiledBodyProducer producer, ref BodyProducerState state, in BodyProducerSensors sensors, ulong stepTicks) {
        var scratch = new BodyMotionScratch {
            Producer = producer,
            ProducerSensors = sensors,
            ProducerState = state,
            SensorTarget = BodySensorTarget.None,
            StepTicks = stepTicks,
            TurnSpeed = (Profile?.FixedTurnSpeed ?? m_tuning.TurnSpeed),
        };

        for (var phase = 0; (phase < producer.Program.Phases.Length); phase++) {
            foreach (var op in producer.Program.Phases[phase]) {
                var instruction = new CompiledBodyInstruction(
                    Operation: op,
                    Value: default,
                    Direction: default,
                    DurationTicks: 0UL,
                    StateSlot: -1
                );

                ExecuteOperation(
                    instruction: in instruction,
                    scratch: ref scratch
                );
            }
        }

        state = scratch.ProducerState;
        StageProducerIntent(intent: in scratch.Intent);
    }

    private void ApplyEffects(CompiledBodyInstruction[] effects, ref BodyMotionScratch scratch) {
        foreach (var effect in effects) {
            if (effect.Target == ActionTarget.Self) {
                ExecuteOperation(
                    instruction: in effect,
                    scratch: ref scratch
                );
                continue;
            }

            var target = scratch.EffectTargets.Resolve(target: effect.Target);

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
    }
    // The shared hard-teleport commit: clear the affected integration carries. Face only resets rotation; Warp resets
    // position and vertical state but preserves rotation; full Pose/Reconcile operations reset every carry. SetBodyMotionProgram
    // resets the pose carries and only resets vertical state when switching to grounded.
    private void CommitTeleport(bool resetPosition = true, bool resetVertical = true, bool resetRotation = true) {
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

        if (resetVertical) {
            ResetVertical();
        }
    }
    // An angle in radians normalized into [0, 360) degrees, so an echo is a stable compass reading (a -10° pitch reads
    // as 350°, level as 0°).
    private static float CompassDegrees(float radians) {
        var degrees = (radians * (180f / MathF.PI));

        return (degrees - (360f * MathF.Floor(x: (degrees / 360f))));
    }
    private void ComputeLocalTargetVelocity(ref BodyMotionScratch scratch) {
        // The free program shares the kit's declared movement frame. Under World, the client has ALREADY rotated
        // the planar pair through camera yaw, so rotating it through body attitude again is a second composition
        // (the source of direction-dependent flight and apparent vertical loss after a handoff). World also makes
        // MoveUp literal world Y, keeping an upright hover body's ascent independent of its rendered facing. Heading
        // retains true 6DOF body-local flight.
        var facing = ((m_tuning.MoveFrame == MotionMoveFrame.World)
            ? -UnitZ
            : scratch.Orientation.Rotate(vector: -UnitZ)
        );
        var right = ((m_tuning.MoveFrame == MotionMoveFrame.World)
            ? UnitX
            : scratch.Orientation.Rotate(vector: UnitX)
        );
        var up = ((m_tuning.MoveFrame == MotionMoveFrame.World)
            ? UnitY
            : scratch.Orientation.Rotate(vector: UnitY)
        );

        var (forward, strafe) = PlanarIntent(intent: in scratch.Intent);

        scratch.Velocity = ((((facing * forward) + (right * strafe)) + (up * Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveUp
        ))) * scratch.MoveSpeed);
        scratch.TargetVelocity = scratch.Velocity;
    }
    private void ComputePlanarTargetVelocity(ref BodyMotionScratch scratch) {
        var effectiveMoveSpeed = (((m_sprintChannelOrdinal >= 0) && (scratch.Intent[m_sprintChannelOrdinal] >= m_channelThresholds[m_sprintChannelOrdinal]))
            ? (scratch.MoveSpeed * m_tuning.SprintMultiplier)
            : scratch.MoveSpeed
        );

        if (TryCommandedMoveDirection(
            direction: out var direction,
            scratch: ref scratch
        )) {
            scratch.TargetVelocity = (direction * effectiveMoveSpeed);

            return;
        }

        var (forward, strafe) = PlanarIntent(intent: in scratch.Intent);

        scratch.TargetVelocity = (((scratch.Facing * forward) + (scratch.Right * strafe)) * effectiveMoveSpeed);
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

        var tangent = (commanded - (scratch.Up * FixedVector3.Dot(
            left: commanded,
            right: scratch.Up
        )));
        var tangentLength = tangent.Length;

        if (tangentLength <= FixedQ4816.Zero) {
            // Straight into (or out of) the surface: nothing to walk along. The body holds still rather than being
            // handed an arbitrary direction.
            return true;
        }

        direction = ((tangent / tangentLength) * speed);

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
    // The canonical orientation decomposed to Tait-Bryan angles (radians), the exact inverse of OrientationFromEuler's
    // Ry(yaw)·Rx(pitch)·Rz(roll) construction (the codebase-wide yaw-about-+Y / pitch-about-+X / roll-about-+Z
    // convention). Yaw is atan2 of the facing's horizontal components; pitch is the facing's elevation; roll is the bank
    // read from the body right/up vectors' vertical parts. A pure-yaw attitude yields pitch = roll = 0.
    private (float Yaw, float Pitch, float Roll) EulerRadians() {
        var orientation = m_orientation.ToQuaternion();
        var forward = Vector3.Transform(
            value: -Vector3.UnitZ,
            rotation: orientation
        );
        var up = Vector3.Transform(
            value: Vector3.UnitY,
            rotation: orientation
        );
        var right = Vector3.Transform(
            value: Vector3.UnitX,
            rotation: orientation
        );
        var yaw = MathF.Atan2(
            x: -forward.Z,
            y: -forward.X
        );
        var pitch = MathF.Asin(x: Math.Clamp(
            max: 1f,
            min: -1f,
            value: forward.Y
        ));
        var roll = MathF.Atan2(
            x: up.Y,
            y: right.Y
        );

        return (Yaw: yaw, Pitch: pitch, Roll: roll);
    }
    private void ExecuteOperation(in CompiledBodyInstruction instruction, ref BodyMotionScratch scratch) {
        switch (instruction.Operation) {
            case BodyMotionOp.SenseNearestInCone:
                SenseTarget(
                    candidate: scratch.ProducerSensors.Candidate,
                    scratch: ref scratch
                );
                break;
            case BodyMotionOp.ProduceWanderIntent:
                ProduceWanderIntent(scratch: ref scratch);
                break;
            case BodyMotionOp.ProduceAttendIntent:
                ProduceAttendIntent(scratch: ref scratch);
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
            case BodyMotionOp.ShapePlanarVelocity:
                scratch.Velocity = ShapePlanarVelocity(
                    intent: in scratch.Intent,
                    stepTicks: scratch.StepTicks,
                    target: scratch.TargetVelocity
                );
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
            case BodyMotionOp.ShapeDriveVelocity:
                ShapeDriveVelocity(scratch: ref scratch);
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
                m_position = scratch.NextPosition;
                m_orientation = scratch.Orientation;
                break;
            case BodyMotionOp.SetVerticalVelocity:
                m_verticalVelocity = instruction.Value;
                m_verticalVelocityAccumulator.Reset();
                break;
            case BodyMotionOp.ScaleVerticalVelocity:
                m_verticalVelocity *= instruction.Value;
                m_verticalVelocityAccumulator.Reset();
                break;
            case BodyMotionOp.PlanarImpulse:
                m_overlayVelocity = (scratch.Orientation.Rotate(vector: instruction.Direction) * instruction.Value);
                m_overlayRemaining = instruction.DurationTicks;
                m_overlayAccumulator.Reset();
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
                var beforeAdd = m_actionStateValues[instruction.StateSlot];
                ApplyRawState(
                    slot: instruction.StateSlot,
                    requested: (m_actionStateValues[instruction.StateSlot] + instruction.Value).Value,
                    writer: "world-effect",
                    reason: "addState"
                );
                MarkDurableDirty(
                    slot: instruction.StateSlot,
                    kind: WorldDocumentWriteKind.Add,
                    operand: (m_actionStateValues[instruction.StateSlot] - beforeAdd)
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
            case BodyMotionOp.Judge:
                // STAGED, never graded here: grading needs the world's musical clock, which this body does not
                // hold — the fact joins WorldServer.Step's drain immediately after the whole population advance
                // (see WorldJudgeInvocation).
                if (
                    (instruction.StateName is { } judgeRef) &&
                    (scratch.JudgeInvocations is not null)
                ) {
                    scratch.JudgeInvocations.Add(item: new WorldJudgeInvocation(
                        EntityIndex: scratch.EntityIndex,
                        JudgeRef: judgeRef
                    ));
                }
                break;
            default:
                throw new InvalidOperationException(message: $"Body program reached uncompiled opcode value {((int)instruction.Operation)}.");
        }
    }
    private void ExecuteProgram(PlayerIntent intent, FixedQ4816 moveSpeed, FixedQ4816 turnSpeed, ulong stepTicks, int entityIndex, BodyEffectTargets effectTargets, List<BodyEffectOutput>? effectOutputs, List<WorldDesignation>? designationOutputs, List<WorldGeneratorInvocation>? generatorInvocations, List<WorldJudgeInvocation>? judgeInvocations) {
        m_entityIndex = entityIndex;
        var scratch = new BodyMotionScratch {
            DesignationOutputs = designationOutputs,
            EffectOutputs = effectOutputs,
            EffectTargets = effectTargets,
            EntityIndex = entityIndex,
            GeneratorInvocations = generatorInvocations,
            Intent = intent,
            JudgeInvocations = judgeInvocations,
            MoveSpeed = moveSpeed,
            NextPosition = m_position,
            Orientation = m_orientation,
            StepTicks = stepTicks,
            AttitudeUp = m_up,
            TurnSpeed = turnSpeed,
            Up = m_up,
        };

        for (var phase = 0; (phase < m_bodyMotionProgram.Phases.Length); phase++) {
            foreach (var op in m_bodyMotionProgram.Phases[phase]) {
                var instruction = new CompiledBodyInstruction(
                    Operation: op,
                    Value: default,
                    Direction: default,
                    DurationTicks: 0UL,
                    StateSlot: -1
                );

                ExecuteOperation(
                    instruction: in instruction,
                    scratch: ref scratch
                );
            }

            if (phase == 5) {
                ResolveProgramContacts(scratch: ref scratch);
            }
        }

        // The grip's inward standoff lands after the pose is committed, for the same reason the contact resolve
        // lands after integration: it is a correction to where the body ended up, not a term in how it got there.
        SeatToHold(stepTicks: stepTicks);
    }
    private static FixedQ4816 ExtractYaw(FixedQuaternion orientation) {
        var forward = orientation.Rotate(vector: -UnitZ);

        return FixedQ4816.Atan2(
            y: -forward.X,
            x: -forward.Z
        );
    }
    private void FaceSensorTarget(ref BodyMotionScratch scratch) {
        if (
            !scratch.SensorTarget.Exists ||
            (m_roleOrdinals.Turn < 0)
        ) {
            return;
        }

        var dx = (scratch.SensorTarget.Position.X - m_position.X);
        var dz = (scratch.SensorTarget.Position.Z - m_position.Z);
        var targetYaw = FixedQ4816.Atan2(
            x: -dz,
            y: -dx
        );
        var yawRate = (scratch.Producer!.Scalar(name: "inwardGain") * WrapPi(angle: (targetYaw - FixedYaw)));
        var turn = FixedQ4816.Clamp(
            value: (yawRate / scratch.Producer.Scalar(name: "turnScale")),
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
        var angularStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: (Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.Turn
                ) * scratch.TurnSpeed),
                Y: (Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.Pitch
                ) * scratch.TurnSpeed),
                Z: (Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.Roll
                ) * scratch.TurnSpeed)
            ),
            elapsedTicks: scratch.StepTicks
        );
        var delta = ((FixedQuaternion.FromAxisAngle(
            axis: UnitY,
            angle: angularStep.X
        )
            * FixedQuaternion.FromAxisAngle(
            axis: UnitX,
            angle: angularStep.Y
        ))
            * FixedQuaternion.FromAxisAngle(
            axis: UnitZ,
            angle: angularStep.Z
        ));

        scratch.Orientation = (m_orientation * delta).Normalize();
    }
    private void IntegratePlanarAndVerticalVelocity(ref BodyMotionScratch scratch) {
        scratch.Velocity = (m_planarVelocity + (scratch.Up * (m_verticalVelocity + scratch.DirectVerticalVelocity)));
        var step = m_positionAccumulator.Integrate(
            elapsedTicks: scratch.StepTicks,
            ratePerSecond: scratch.Velocity
        );

        scratch.NextPosition = (m_position + step);
    }
    private void IntegrateScratchVelocity(ref BodyMotionScratch scratch) {
        scratch.NextPosition = (m_position + m_positionAccumulator.Integrate(
            elapsedTicks: scratch.StepTicks,
            ratePerSecond: scratch.Velocity
        ));
    }
    private PlayerIntent NextIntent(ulong stepTicks) {
        var movement = default(PlayerIntent);
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
            segment.RemainingTicks = SubtractSaturating(
                amount: stepTicks,
                value: segment.RemainingTicks
            );
            movement = segment.Intent;
            resolved = true;

            if (!(segment.RemainingTicks > 0)) {
                DropFrontSegment();
            }
        }

        if (!resolved) {
            movement = ((!m_source.IsIdle && m_hasSubmittedIntent)
                ? m_submittedIntent
                : ((SourceNamesProducer(source: m_source) && m_hasProducerIntent)
                    ? m_producerIntent
                    : default
            ));
        }

        // Both one-tick images are a one-step publish, even when a tape or the source masked them this time. Their
        // producers must republish on the next authoritative step, matching the snapshot discipline of every other
        // input source.
        m_submittedIntent = default;
        m_hasSubmittedIntent = false;
        m_producerIntent = default;
        m_hasProducerIntent = false;

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
        var liveHeld = (m_hasTransferHeldChannels
            ? m_transferHeldChannels
            : m_heldChannels
        );

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_laneTimers[ordinal] > 0) {
                channels[ordinal] = m_channelTimerValues[ordinal];
            } else if (
                m_source.IsLive &&
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

        m_channelReadHeld = new PlayerIntent(Channels: heldOverlay);
        m_channelReadComposed = new PlayerIntent(Channels: channels);

        return m_channelReadComposed;
    }
    // Build a canonical orientation from Tait-Bryan angles (radians): yaw about world up (+Y), then pitch about the body
    // right (+X), then roll about the body forward (+Z) — the codebase-wide convention, the exact inverse EulerRadians
    // decomposes. Roll is about local +Z uniformly here and in the free integrator, so the pose set by body.pose and
    // the attitude flown by body.fly share one sign convention.
    private static FixedQuaternion OrientationFromEuler(FixedQ4816 yaw, FixedQ4816 pitch, FixedQ4816 roll) {
        return ((FixedQuaternion.FromAxisAngle(
            angle: yaw,
            axis: UnitY
        )
            * FixedQuaternion.FromAxisAngle(
            angle: pitch,
            axis: UnitX
        ))
            * FixedQuaternion.FromAxisAngle(
            angle: roll,
            axis: UnitZ
        )).Normalize();
    }
    private static FixedQ4816 PerStep(FixedQ4816 value, ulong stepTicks) {
        if ((EngineTicks.PerSecond % stepTicks) != 0UL) {
            throw new ArgumentException(
                message: $"The fixed-step period {stepTicks} must divide {EngineTicks.PerSecond} engine ticks exactly.",
                paramName: nameof(stepTicks)
            );
        }

        return (value / FixedQ4816.FromInteger(value: checked((long)(EngineTicks.PerSecond / stepTicks))));
    }
    private void ProduceAttendIntent(ref BodyMotionScratch scratch) {
        if (!scratch.SensorTarget.Exists) {
            return;
        }

        var producer = scratch.Producer!;
        var standoff = producer.Scalar(name: "standoffRadius");
        var forward = ((scratch.SensorTarget.DistanceSquared > (standoff * standoff))
            ? producer.Scalar(name: "approach")
            : FixedQ4816.Zero
        );
        var strafe = producer.Scalar(name: "orbit");
        var followsVolume = producer.Target is { Source: BodyTargetSource.Navigated, NavigationKind: not WorldNavigationKind.Surface };
        var preferredAltitude = (followsVolume ? scratch.SensorTarget.Position.Y : scratch.ProducerState.PreferredAltitude);
        var up = (followsVolume || m_bodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)
            ? FixedQ4816.Clamp(
                value: ((preferredAltitude - m_position.Y) * producer.Scalar(name: "altitudeGain")),
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
            var dx = (scratch.SensorTarget.Position.X - m_position.X);
            var dz = (scratch.SensorTarget.Position.Z - m_position.Z);
            var targetYaw = FixedQ4816.Atan2(
                x: -dz,
                y: -dx
            );

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
    private void ProduceWanderIntent(ref BodyMotionScratch scratch) {
        var producer = scratch.Producer!;
        var state = scratch.ProducerState;

        state.Phase += PerStep(
            stepTicks: scratch.StepTicks,
            value: state.WeaveFrequency
        );
        state.ActivityPhase += PerStep(
            stepTicks: scratch.StepTicks,
            value: state.ActivityRate
        );

        // Measured from the body's own HOME, never the world origin: a population spread over several placements
        // steers back to the ground it was activated on, instead of every wanderer in the world converging on (0, 0).
        // A body with no home (the zero default) reads exactly as it did when the origin was the only anchor.
        var planarX = (m_position.X - m_home.X);
        var planarZ = (m_position.Z - m_home.Z);
        var yawRate = (producer.Scalar(name: "weaveAmplitude") * FixedQ4816.Sin(angle: state.Phase));
        var radius = FixedQ4816.Sqrt(value: ((planarX * planarX) + (planarZ * planarZ)));

        if (radius > producer.Scalar(name: "softRadius")) {
            var inwardYaw = FixedQ4816.Atan2(
                x: planarZ,
                y: planarX
            );

            yawRate += (producer.Scalar(name: "inwardGain") * WrapPi(angle: (inwardYaw - FixedYaw)));
        }

        var turn = FixedQ4816.Clamp(
            value: (yawRate / producer.Scalar(name: "turnScale")),
            minimum: NegativeOne,
            maximum: FixedQ4816.One
        );
        var wave = FixedQ4816.Sin(angle: state.ActivityPhase);
        var altitudeCorrection = FixedQ4816.Clamp(
            value: ((state.PreferredAltitude - m_position.Y) * producer.Scalar(name: "altitudeGain")),
            minimum: NegativeOne,
            maximum: FixedQ4816.One
        );

        if (m_bodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)) {
            scratch.Intent = m_roleOrdinals.Intent(
                moveAdvance: producer.Scalar(name: "forward"),
                moveStrafe: (wave * producer.Scalar(name: "strafeWave")),
                turn: turn,
                moveUp: (altitudeCorrection + (wave * producer.Scalar(name: "upWave"))),
                pitch: (wave * producer.Scalar(name: "pitchWave")),
                roll: (-turn * producer.Scalar(name: "rollTurn"))
            );
        } else {
            var angularIntent = FixedQ4816.Clamp(
                value: (turn + (wave * producer.Scalar(name: "turnWave"))),
                minimum: NegativeOne,
                maximum: FixedQ4816.One
            );
            var forward = producer.Scalar(name: "forward");
            var strafe = (wave * producer.Scalar(name: "strafeWave"));

            if (m_tuning.MoveFrame == MotionMoveFrame.World) {
                // A producer owns a body-relative steering decision even when a seat-facing kit consumes world-frame
                // axes. Resolve that decision through the same yaw convention SnapYawToPlanarIntent reads; otherwise
                // the Turn channel is deliberately inert under World and every wanderer can only march toward -Z.
                var targetYaw = (FixedYaw + PerStep(
                    stepTicks: scratch.StepTicks,
                    value: (angularIntent * scratch.TurnSpeed)
                ));

                var (sinYaw, cosYaw) = FixedQ4816.SinCos(angle: targetYaw);
                // The Turn role carries the same angular intent, so the heading integrates toward targetYaw (the
                // facing snap turns the ATTITUDE only; without this the wanderer's heading would never advance).
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

            var press = producer.Channel(name: "press");
            var threshold = producer.Scalar(name: "pressThreshold");

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
        m_verticalVelocity = FixedQ4816.Zero;
        m_verticalVelocityAccumulator.Reset();
        m_positionAccumulator.ResetY();
        m_grounded = true;

        // A teleport must not carry momentum: drop the ramped planar velocity, its accumulator carries (the
        // isotropic ramp and a drive row's decomposed channels alike), the response table's recency clocks, and the
        // dynamics followers' own state and previous-target carries.
        m_planarVelocity = default;
        m_planarRampAccumulator.Reset();
        m_driveLongAccumulator.Reset();
        m_driveLatAccumulator.Reset();
        m_driveResidualAccumulator.Reset();
        Array.Clear(array: m_motionRecency);
        m_planarFollower = default;
        m_planarPreviousTarget = default;
        m_planarFollowerSeeded = false;
        m_verticalFollower = default;
        m_verticalPreviousTarget = default;
        m_verticalFollowerSeeded = false;

        // The medium carries are momentum and medium facts on the same terms — a warp never carries a dive across,
        // and a body warped out of the water must not read Submerged until the medium law says so again.
        m_mediumThrustRampAccumulator.Reset();
        m_submerged = false;
        m_atSurface = false;
    }
    // The one seat-time resolve. Shared by Advance (which feeds this into the program) and EffectiveMoveSpeed
    // (which only reads it back) so the two can never compute two different answers to "what speed is this body
    // actually moving at". One law for every kit: the seated profile's claimed rate, else the kit's own, clamped by
    // the kit's envelope. A kart pins its speed with min == max rather than opting out of the profile read; a held
    // sprint (a drive's boost) multiplies AFTER this clamp, never inside it (see ShapeDriveVelocity).
    private FixedQ4816 ResolveMoveSpeed() {
        var resolved = (Profile?.FixedMoveSpeed ?? m_tuning.MoveSpeed);

        return (m_tuning.MoveSpeedEnvelope?.Clamp(value: resolved) ?? resolved);
    }
    // Position/planar contact response applies to ANY collider-bearing body regardless of body motion program — a flying
    // body still shouldn't clip through a wall. The vertical WRITE-BACK (m_verticalVelocity, m_planarVelocity, the
    // grounded position-accumulator reset) is gated on CompiledBodyMotionProgram.OwnsVerticalContactState — see its
    // own remarks for which programs cede the channel and which keep it. m_grounded/m_lastContactCount stay
    // informational for every model (RunActionTriggers' ActionFact.Grounded/Airborne reads them under any program),
    // since they never feed back into an integration.
    private void ResolveProgramContacts(ref BodyMotionScratch scratch) {
        if (
            (m_contactField is { } field) &&
            (m_collider is { } collider)
        ) {
            var resolvedVelocity = scratch.Velocity;
            var contactResolution = ((field is IEntityContactField entityField)
                ? entityField.ResolveEntitySweep(
                    entityIndex: scratch.EntityIndex,
                    previousPosition: m_position,
                    position: ref scratch.NextPosition,
                    up: in scratch.Up,
                    velocity: ref resolvedVelocity,
                    orientation: in scratch.Orientation,
                    volumes: collider.Volumes
                )
                : field.ResolveSweep(
                    previousPosition: m_position,
                    position: ref scratch.NextPosition,
                    up: in scratch.Up,
                    velocity: ref resolvedVelocity,
                    orientation: in scratch.Orientation,
                    volumes: collider.Volumes
                )
            );

            m_grounded = contactResolution.Grounded;

            // Under SurfaceFollowing, a standing body's up is the SURFACE it stands on, not the direction its field
            // pulls. The two differ wherever a floor is not perpendicular to the field — a flat floor under a field
            // tilted by distant attractors is the ordinary case — and walking the field's tangent instead of the
            // floor's carries the body off the floor a little further every tick.
            //
            // The velocity is carried into the new frame by the SAME rotation. Decomposing motion that was tangent to
            // the old surface against a rotated up reads part of it as climbing, and the write-back below stores that
            // as ballistic velocity: on a sphere that is a launch, and the faster the body runs the harder it is
            // thrown off.
            // Only under SurfaceFollowing: a measured normal is a fact about the surface, and only a body policy that
            // admits surface-following may let it move the axis — a rounded lip or a blended corner tilts the normal,
            // and adopting that tilt under Ambient pitches the body over and lets the face beside it read as
            // ground. And only where this body participates in an authored solved field:
            // outside every area in an area-only world the up axis has a single source already (the field provider's
            // own per-sample gradient), and adopting a measured contact normal on top would make it wobble, which a
            // marginal handoff — an adjacency seam strip — cannot absorb.
            if (
                m_grounded &&
                (m_upPolicy == WorldBodyUpPolicy.SurfaceFollowing) &&
                TrySolvedGravity(acceleration: out _) &&
                (contactResolution.GroundNormal != FixedVector3.Zero)
            ) {
                // BOUNDED, for the same reason the field's axis is: a measured normal is continuous only where the
                // surface is. The analytic collider approximates a creation as a UNION of its primitives and carries
                // none of the authored blend, so wherever two blend in the render — a planetoid's outcrops into its
                // core — the walked surface has a crease the seen surface does not, and the normal jumps across it.
                // Adopting that jump whole rotates the velocity with it, so a body running over a crease is kicked
                // sideways by tens of degrees in a single tick.
                //
                // The ceiling is far above any real curvature (a body at full sprint on the tightest planetoid turns
                // its normal an order of magnitude slower), so ordinary running still tracks the surface exactly and
                // only a discontinuity is spread — over a few ticks, which reads as instant.
                FixedQuaternion transport;

                if (m_upNeedsReseat) {
                    m_upNeedsReseat = false;
                    transport = FixedQuaternion.FromTo(
                        from: m_up,
                        to: contactResolution.GroundNormal
                    );
                    SetUp(next: contactResolution.GroundNormal);
                } else {
                    transport = SteerUpToward(
                        accumulator: ref m_contactUpTurnAccumulator,
                        halfRate: ContactUpTurnHalfRate,
                        stepTicks: scratch.StepTicks,
                        target: contactResolution.GroundNormal
                    );
                }

                resolvedVelocity = transport.Rotate(vector: resolvedVelocity);
                scratch.Up = m_up;
            }
            m_lastContactCount = (m_grounded
                ? 1
                : 0
            );
            // scratch.Intent's raw MoveAdvance/MoveStrafe roles are the idle signal — resolved once by NextIntent
            // before ANY op runs, so it is available and current at this exact point regardless of the compiled
            // program's op order (unlike scratch.TargetVelocity/scratch.Velocity, which a Compute*TargetVelocity op
            // may not have written yet this tick depending on where contact resolution sits in that order, and which
            // — once written — is the RESPONSE-RAMPED result the wall itself just clipped: using either would risk
            // a feedback loop, a wall stopping the body read back as "input released").
            UpdateObstructionWitness(
                rawObstruction: contactResolution.ObstructionNormal,
                intent: in scratch.Intent,
                position: scratch.NextPosition,
                stepTicks: scratch.StepTicks
            );

            if (
                !m_bodyMotionProgram.OwnsVerticalContactState ||
                HoldOwnsVerticalChannel
            ) {
                // A grip owns the whole tangent-plane velocity, vertical component included — splitting it against
                // the body's up axis and storing the remainder as ballistic velocity would leave the climb's own
                // rise to be re-added by gravity the tick the hold ends.
                return;
            }

            var resolvedNormal = FixedVector3.Dot(
                left: resolvedVelocity,
                right: scratch.Up
            );

            m_planarVelocity = (resolvedVelocity - (scratch.Up * resolvedNormal));

            // The direct term is one tick of authored input, not persistent motion state. Never fold contact's
            // clipped drive into the ballistic channel: holding down against a floor would otherwise store an equal
            // upward launch for the frame the trigger was released.
            if (scratch.DirectVerticalVelocity != FixedQ4816.Zero) {
                m_verticalVelocity = FixedQ4816.Zero;
                m_verticalVelocityAccumulator.Reset();
                if (m_grounded) {
                    m_positionAccumulator.ResetY();
                }
                return;
            }

            // GROUND STICK. Contact removes the velocity driving into a surface, so a standing body carries no inward
            // motion at all — fine on a flat floor, fatal on a convex one: the surface curves away, the body keeps
            // going straight, and it leaves the ground under its own walking speed. A small inward bias while grounded
            // keeps it pressed against whatever it stands on, and depenetration removes the excess exactly as it does
            // for gravity. Released the moment the body stops being grounded, so a jump or a ledge still launches
            // cleanly.
            // Only a surface that is not world-level needs it: on flat ground contact already holds the body, and an
            // imposed inward speed there only eats into the margin a marginal handoff (an adjacency seam strip) has to
            // work with. A level floor keeps its previous behaviour exactly.
            var settled = ((m_grounded && (m_up != UnitY) && (resolvedNormal > -StickSpeed))
                ? -StickSpeed
                : resolvedNormal
            );

            if (settled != m_verticalVelocity) {
                m_verticalVelocity = settled;
                m_verticalVelocityAccumulator.Reset();
            }

            if (m_grounded) {
                m_positionAccumulator.ResetY();
            }
        } else {
            m_grounded = false;
            m_lastContactCount = 0;
            m_obstructionWitness = FixedVector3.Zero;
            m_obstructionWitnessGraceTicks = 0;
        }
    }
    // A body's own authored-field answer this tick, or false when the world authors no field or this body matched no
    // local area in an area-only field. True does NOT imply a nonzero vector: Replace can author a zero-G pocket,
    // Combine can cancel exactly, and a radial contribution is zero at its own centre.
    private bool TrySolvedGravity(out FixedVector3 acceleration) {
        acceleration = FixedVector3.Zero;

        return ((m_gravityField is { } field) && field.TryAcceleration(
            acceleration: out acceleration,
            entityIndex: m_entityIndex
        ));
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
        if (m_tuning.PlanarDynamics is not null) {
            var rotatedVelocity = transport.Rotate(vector: m_planarFollower.Velocity);

            m_planarFollower = new SecondOrderState3(
                X: new SecondOrderState(PositionRaw: m_planarFollower.X.PositionRaw, VelocityRaw: (rotatedVelocity.X.Value << 16)),
                Y: new SecondOrderState(PositionRaw: m_planarFollower.Y.PositionRaw, VelocityRaw: (rotatedVelocity.Y.Value << 16)),
                Z: new SecondOrderState(PositionRaw: m_planarFollower.Z.PositionRaw, VelocityRaw: (rotatedVelocity.Z.Value << 16))
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
    // --- The drive row (the ResolveDriveFrame/ShapeDriveVelocity ops). ---

    // The drive frame (phase 0): resolve up, integrate speed-scaled steering into the heading, and (under a
    // positive pitchRate) integrate the Pitch channel into the clamped pitch scalar; facing/right derive from the
    // fresh yaw(+pitch) attitude. Steering authority rises linearly from zero at standstill to full at
    // steerReferenceSpeed, falls linearly to steerFalloff× at the RESOLVED (envelope-clamped) move speed — the same
    // scratch.MoveSpeed ResolveMoveSpeed filled before phase 0, so a clamped kit's falloff anchor moves with its
    // clamp instead of an unreachable authored rate — reverses sign with reversing travel (a car backing up, not a
    // turret), and scales by the drift row's steerScale while its channel reads held. The rate at full authority is
    // the kit's own turnSpeed (scratch.TurnSpeed), the same one every other frame operation turns at.
    private void ResolveDriveFrame(ref BodyMotionScratch scratch) {
        scratch.Up = ResolveUp(stepTicks: scratch.StepTicks);
        scratch.AttitudeUp = scratch.Up;

        var tuning = (m_tuning.Drive ?? default);
        // The signed longitudinal speed against the PREVIOUS attitude — shaping runs after this frame op, so the
        // one-tick-old velocity is the deterministic witness available here.
        var previousFacing = m_orientation.Rotate(vector: -UnitZ);
        var longitudinal = FixedVector3.Dot(
            left: m_planarVelocity,
            right: previousFacing
        );
        var speed = FixedQ4816.Abs(value: longitudinal);
        var authority = ((speed >= tuning.SteerReferenceSpeed)
            ? FixedQ4816.One
            : (speed / tuning.SteerReferenceSpeed)
        );

        if (
            (speed > tuning.SteerReferenceSpeed) &&
            (scratch.MoveSpeed > tuning.SteerReferenceSpeed)
        ) {
            var over = FixedQ4816.Clamp(
                value: ((speed - tuning.SteerReferenceSpeed) / (scratch.MoveSpeed - tuning.SteerReferenceSpeed)),
                minimum: FixedQ4816.Zero,
                maximum: FixedQ4816.One
            );

            authority = (FixedQ4816.One + (over * (tuning.SteerFalloff - FixedQ4816.One)));
        }

        if (longitudinal < FixedQ4816.Zero) {
            authority = -authority;
        }

        if (DriftHeld(intent: in scratch.Intent)) {
            authority *= tuning.DriftSteerScale;
        }

        var yawRate = ((Role(
            intent: in scratch.Intent,
            role: ChannelRole.Turn
        ) * scratch.TurnSpeed) * authority);
        var pitchRate = ((tuning.PitchRate > FixedQ4816.Zero)
            ? (Role(
                intent: in scratch.Intent,
                role: ChannelRole.Pitch
            ) * tuning.PitchRate)
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
                axis: UnitY
            )
            : (FixedQuaternion.FromAxisAngle(
                angle: m_yaw,
                axis: UnitY
            ) * FixedQuaternion.FromAxisAngle(
                angle: m_drivePitch,
                axis: UnitX
            )).Normalize()
        );

        scratch.Orientation = ((scratch.Up == UnitY)
            ? attitude
            : (m_frame * attitude)
        );
        scratch.Facing = scratch.Orientation.Rotate(vector: -UnitZ);
        scratch.Right = scratch.Orientation.Rotate(vector: UnitX);
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
                m_tuning.FacingSnap &&
                (Role(intent: in scratch.Intent, role: ChannelRole.MoveAdvance) == FixedQ4816.Zero) &&
                (Role(intent: in scratch.Intent, role: ChannelRole.MoveStrafe) == FixedQ4816.Zero)
            ) {
                var facingYaw = ExtractYaw(orientation: m_orientation);

                if (FixedQ4816.Abs(value: WrapPi(angle: (facingYaw - m_yaw))) > FacingAdoptEpsilon) {
                    m_yaw = facingYaw;
                }
            }

            var worldAngleStep = m_rotationAccumulator.Integrate(
                ratePerSecond: new FixedVector3(
                    X: (Role(
                        intent: in scratch.Intent,
                        role: ChannelRole.Turn
                    ) * scratch.TurnSpeed),
                    Y: FixedQ4816.Zero,
                    Z: FixedQ4816.Zero
                ),
                elapsedTicks: scratch.StepTicks
            );

            m_yaw += worldAngleStep.X;

            // World-frame movement resolves the TRANSLATION axes from the seat rather than the body's facing; it does
            // not mean the body ignores which way is up. The yaw attitude composes under the same up tilt the local
            // frame uses, and the axes come off that orientation, so a world-framed walker on a curved surface travels
            // its tangent instead of a fixed world direction. Identical to a hardcoded -Z/+X while up is world +Y, so
            // a flat world integrates exactly as before.
            var worldAttitude = FixedQuaternion.FromAxisAngle(
                angle: m_yaw,
                axis: UnitY
            );

            scratch.Orientation = ((scratch.Up == UnitY)
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
            scratch.Facing = ((scratch.Up == UnitY)
                ? -UnitZ
                : m_frame.Rotate(vector: -UnitZ)
            );
            scratch.Right = ((scratch.Up == UnitY)
                ? UnitX
                : m_frame.Rotate(vector: UnitX)
            );

            return;
        }

        var angleStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: (Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.Turn
                ) * scratch.TurnSpeed),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            elapsedTicks: scratch.StepTicks
        );

        m_yaw += angleStep.X;
        var yawRotation = FixedQuaternion.FromAxisAngle(
            angle: m_yaw,
            axis: UnitY
        );

        scratch.Orientation = ((scratch.Up == UnitY)
            ? yawRotation
            : (m_frame * yawRotation)
        );
        scratch.Facing = scratch.Orientation.Rotate(vector: -UnitZ);
        scratch.Right = scratch.Orientation.Rotate(vector: UnitX);
    }
    private void SenseTarget(BodySensorTarget candidate, ref BodyMotionScratch scratch) {
        var producer = scratch.Producer!;
        var current = scratch.ProducerSensors.CurrentTarget;

        // Release-radius hysteresis exists to damp flicker among a COMPETITIVELY sensed population (Sensed alone);
        // a Designated register and a Curve follow-point are each a single deterministic candidate every tick with
        // nothing to flicker against, so both take the fresh candidate outright.
        if (producer.Target?.Source is not BodyTargetSource.Sensed) {
            scratch.SensorTarget = candidate;
        } else {
            var release = producer.Scalar(name: "releaseRadius");

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
    // --- The response table (the Shape stage). ---
    // Converge the ramped planar velocity on the commanded target through the matching response row's engage/release
    // rate. An empty table snaps instantly (today's exact behavior, the only path an unopted world takes, byte-identical).
    // A body matching no row also snaps (the always-row is optional). The has-input axis — a property of the command,
    // not a body fact — picks the engage (stick deflected) or release (stick centered) rate.
    private FixedVector3 ShapePlanarVelocity(FixedVector3 target, in PlayerIntent intent, ulong stepTicks) {
        if (m_tuning.PlanarDynamics is { Planar: { } planar }) {
            // stepTicks can differ from planar.StepTicks for exactly one tick — see the identical note in
            // ApplyMedium.
            var ceiling = (((m_sprintChannelOrdinal >= 0) && (intent[m_sprintChannelOrdinal] >= m_channelThresholds[m_sprintChannelOrdinal]))
                ? (ResolveMoveSpeed() * m_tuning.SprintMultiplier)
                : ResolveMoveSpeed()
            );

            return StepPlanarFollower(
                ceiling: ceiling,
                step: in planar,
                target: target
            );
        }

        var response = m_tuning.Response;

        if (response.Length == 0) {
            m_planarVelocity = target;

            return target;
        }

        // Refresh the shared response recency clocks (a Recently window refills while its fact holds, decays otherwise).
        for (var slot = 0; (slot < m_motionRecency.Length); slot++) {
            m_motionRecency[slot] = (FactHolds(fact: m_tuning.ResponseRecencyFacts[slot])
                ? m_tuning.ResponseRecencyWindows[slot]
                : SubtractSaturating(
                    value: m_motionRecency[slot],
                    amount: stepTicks
                )
            );
        }

        var hasInput = ((Role(
            intent: in intent,
            role: ChannelRole.MoveAdvance
        ) != FixedQ4816.Zero) || (Role(
            intent: in intent,
            role: ChannelRole.MoveStrafe
        ) != FixedQ4816.Zero));

        foreach (var row in response) {
            if (!MotionGateOpen(gate: row.Gate)) {
                continue;
            }

            var rate = (hasInput
                ? row.EngageRate
                : row.ReleaseRate
            );
            var maxDelta = m_planarRampAccumulator.Integrate(
                elapsedTicks: stepTicks,
                ratePerSecond: rate
            );

            m_planarVelocity = FixedVector3.MoveToward(
                current: m_planarVelocity,
                maxDelta: maxDelta,
                target: target
            );

            return m_planarVelocity;
        }

        m_planarVelocity = target;

        return target;
    }
    // The drive shaping (phase 2): decompose the carried velocity into body-frame longitudinal/lateral/residual
    // components, converge each at its own authored rate, and recompose — the anisotropy a kart's feel needs and the
    // isotropic MoveToward cannot express. Longitudinal follows the bipolar throttle (accelerate toward the
    // commanded fraction of scratch.MoveSpeed — the resolved, envelope-clamped move speed, the same value
    // EffectiveMoveSpeed echoes; back-throttle brakes while moving forward and reverses from rest at the unenveloped
    // reverseSpeed; the over-speed excess bleeds at coast, which is also the centered-throttle coast). A held sprint
    // multiplies scratch.MoveSpeed AFTER the clamp, on top of the resolved base rate, never inside it — the envelope
    // pins the base, the boost rides on top. Lateral and residual slip converge to zero at grip — the drift row's
    // grip while its channel reads held. A contact-pinned variant (pitchRate zero) has no drive or grip authority
    // while airborne: a launched kart holds its velocity and gravity owns the arc.
    private void ShapeDriveVelocity(ref BodyMotionScratch scratch) {
        var tuning = (m_tuning.Drive ?? default);
        var throttle = Role(
            intent: in scratch.Intent,
            role: ChannelRole.MoveAdvance
        );
        var hasAuthority = ((tuning.PitchRate > FixedQ4816.Zero) || m_grounded);
        var velocity = m_planarVelocity;
        var longitudinal = FixedVector3.Dot(
            left: velocity,
            right: scratch.Facing
        );
        var lateral = FixedVector3.Dot(
            left: velocity,
            right: scratch.Right
        );
        var residual = ((velocity - (scratch.Facing * longitudinal)) - (scratch.Right * lateral));

        if (hasAuthority) {
            FixedQ4816 target, rate;

            if (throttle > FixedQ4816.Zero) {
                var commanded = (BoostHeld(intent: in scratch.Intent)
                    ? (scratch.MoveSpeed * m_tuning.SprintMultiplier)
                    : scratch.MoveSpeed
                );

                target = (throttle * commanded);
                rate = ((longitudinal <= target)
                    ? tuning.Accel
                    : tuning.Coast
                );
            } else if (throttle < FixedQ4816.Zero) {
                if (longitudinal > FixedQ4816.Zero) {
                    target = FixedQ4816.Zero;
                    rate = tuning.Brake;
                } else {
                    target = (throttle * tuning.ReverseSpeed);
                    rate = tuning.Accel;
                }
            } else {
                target = FixedQ4816.Zero;
                rate = tuning.Coast;
            }

            longitudinal = FixedQ4816.MoveToward(
                current: longitudinal,
                target: target,
                maxDelta: m_driveLongAccumulator.Integrate(
                    elapsedTicks: scratch.StepTicks,
                    ratePerSecond: rate
                )
            );

            var grip = (DriftHeld(intent: in scratch.Intent)
                ? tuning.DriftGrip
                : tuning.Grip
            );

            lateral = FixedQ4816.MoveToward(
                current: lateral,
                target: FixedQ4816.Zero,
                maxDelta: m_driveLatAccumulator.Integrate(
                    elapsedTicks: scratch.StepTicks,
                    ratePerSecond: grip
                )
            );
            residual = FixedVector3.MoveToward(
                current: residual,
                target: default,
                maxDelta: m_driveResidualAccumulator.Integrate(
                    elapsedTicks: scratch.StepTicks,
                    ratePerSecond: grip
                )
            );
        }

        m_planarVelocity = (((scratch.Facing * longitudinal) + (scratch.Right * lateral)) + residual);
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
            SnapYaw(
                scratch: ref scratch,
                yaw: FixedQ4816.Atan2(
                    x: -faceZ,
                    y: -faceX
                )
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

        SnapFacing(
            scratch: ref scratch,
            yaw: FixedQ4816.Atan2(
                y: -Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.MoveStrafe
                ),
                x: Role(
                    intent: in scratch.Intent,
                    role: ChannelRole.MoveAdvance
                )
            )
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
            axis: UnitY
        );

        // The snapped heading is a yaw about the axis the body's ATTITUDE stands against, so it composes under the
        // same tilt the frame resolve applies; assigning the bare yaw would drop a planetoid walker back to a
        // world-upright attitude mid-stride. That axis is ordinarily the contact axis and differs from it only where
        // a hold's lean has put the drawn body on a face the solver still measures against gravity — composing about
        // the contact axis there would flatten the lean out again on the very next phase.
        scratch.Orientation = ((scratch.AttitudeUp == UnitY)
            ? attitude
            : (FixedQuaternion.FromTo(
                from: UnitY,
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
    private static ulong SubtractSaturating(ulong value, ulong amount) => ((value > amount)
        ? (value - amount)
        : 0UL
    );
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

        m_obstructionWitnessGraceTicks = SubtractSaturating(
            amount: stepTicks,
            value: m_obstructionWitnessGraceTicks
        );

        if (m_obstructionWitnessGraceTicks == 0) {
            m_obstructionWitness = FixedVector3.Zero;
        }
    }
    private static FixedQ4816 WrapPi(FixedQ4816 angle) => (angle - (TwoPi * FixedQ4816.Floor(value: ((angle + Pi) / TwoPi))));

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
        public List<WorldJudgeInvocation>? JudgeInvocations;
    }
}

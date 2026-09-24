using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Captures this body's own <see cref="WorldBodyTransferState"/> — read live, right now, never cached. Called
    /// before <see cref="Puck.World.Server.WorldPopulation.TryDetachSeatForTransfer"/> discards this body object, so
    /// the abort/restore path has something to reapply if the transfer unwinds. See <see cref="WorldBodyTransferState"/>'s own
    /// remarks for the complete field-by-field classification.</summary>
    public WorldBodyTransferState CaptureTransferState() {
        var laneLatch = new ulong[ActionLaneCount];
        var laneFactHeld = new ulong[ActionLaneCount];
        var laneRecency = new ulong[]?[ActionLaneCount];

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            laneLatch[lane] = m_laneActions[lane].Latch;
            laneFactHeld[lane] = m_laneActions[lane].FactHeld;
            laneRecency[lane] = ((m_laneActions[lane].Recency is { } recency)
                ? [.. recency]
                : null
            );
        }

        var tapeIntents = new PlayerIntent[m_tapeCount];
        var tapeRemainingTicks = new ulong[m_tapeCount];

        for (var offset = 0; (offset < m_tapeCount); offset++) {
            var segment = m_tape[((m_tapeHead + offset) % m_tape.Length)];

            tapeIntents[offset] = segment.Intent;
            tapeRemainingTicks[offset] = segment.RemainingTicks;
        }

        return new(
            PlanarVelocity: m_planarVelocity,
            VerticalVelocity: m_verticalVelocity,
            Orientation: m_orientation,
            DrivePitch: m_drivePitch,
            OverlayVelocity: m_overlayVelocity,
            OverlayRemainingTicks: m_overlayRemaining,
            ChannelTimerTicks: [.. m_laneTimers],
            ChannelTimerValues: [.. m_channelTimerValues],
            BodyMotionProgramName: m_bodyMotionProgram.Name,
            Source: m_source,
            PreviousChannelBit: [.. m_previousChannelBit],
            HeldChannelImage: (m_hasTransferHeldChannels
            ? m_transferHeldChannels
            : m_channelReadHeld),
            PendingDefaultChannelPress: [.. m_pendingDefaultChannelPress],
            PendingDefaultChannelValue: [.. m_pendingDefaultChannelValue],
            MotionRecency: [.. m_motionRecency],
            PlanarRampRemainder: m_planarRampAccumulator.Remainder,
            DriveLongRemainder: m_driveLongAccumulator.Remainder,
            DriveLatRemainder: m_driveLatAccumulator.Remainder,
            DriveResidualRemainder: m_driveResidualAccumulator.Remainder,
            MediumThrustRampRemainder: m_mediumThrustRampAccumulator.Remainder,
            PlanarFollowerPositionRawX: m_planarFollower.X.PositionRaw,
            PlanarFollowerPositionRawY: m_planarFollower.Y.PositionRaw,
            PlanarFollowerPositionRawZ: m_planarFollower.Z.PositionRaw,
            PlanarFollowerVelocityRawX: m_planarFollower.X.VelocityRaw,
            PlanarFollowerVelocityRawY: m_planarFollower.Y.VelocityRaw,
            PlanarFollowerVelocityRawZ: m_planarFollower.Z.VelocityRaw,
            PlanarFollowerPreviousTarget: m_planarPreviousTarget,
            VerticalFollowerPositionRaw: m_verticalFollower.PositionRaw,
            VerticalFollowerVelocityRaw: m_verticalFollower.VelocityRaw,
            VerticalFollowerPreviousTarget: m_verticalPreviousTarget,
            OverlayRemainderX: m_overlayAccumulator.XRemainder,
            OverlayRemainderY: m_overlayAccumulator.YRemainder,
            OverlayRemainderZ: m_overlayAccumulator.ZRemainder,
            LaneLatch: laneLatch,
            LaneFactHeld: laneFactHeld,
            LaneRecency: laneRecency,
            ActionState: CaptureActionState(),
            ActionStateDirty: [.. m_actionStateDirty],
            ActionStateDirtyKind: [.. m_actionStateDirtyKind],
            ActionStateDirtyOperand: [.. m_actionStateDirtyOperand],
            DurableInputPresent: [.. m_durableInputPresent],
            DurableInputValues: [.. m_durableInputValues],
            DurableInputTimers: [.. m_durableInputTimers],
            DurableInputWriters: [.. m_durableInputWriters],
            DurableInputTick: m_durableInputTick,
            TapeIntents: tapeIntents,
            TapeRemainingTicks: tapeRemainingTicks,
            PendingContinuum: m_pendingContinuum
        );
    }
    /// <summary>Installs an adjacency arrival's already-evaluated motion segment and resolves it through this
    /// authority's own contact field. No input, action, timer, gravity, or motion-program operation is evaluated.</summary>
    public void ApplyContinuumTrajectory(in WorldContinuumTrajectory trajectory, int entityIndex, ulong destinationCompletedEngineTick) {
        var next = m_position;
        var velocity = (m_planarVelocity + (FixedVector3.UnitY * m_verticalVelocity));
        var resolution = default(ContactResolution);

        if (
            (m_contactField is { } field) &&
            (m_collider is { } collider)
        ) {
            var volumes = ScaledColliderVolumes();

            resolution = ((field is IEntityContactField entityField)
                ? entityField.ResolveEntitySweep(
                    entityIndex: entityIndex,
                    previousPosition: trajectory.PreviousPosition,
                    position: ref next,
                    up: in m_up,
                    velocity: ref velocity,
                    orientation: in m_orientation,
                    volumes: volumes
                )
                : field.ResolveSweep(
                    previousPosition: trajectory.PreviousPosition,
                    position: ref next,
                    up: in m_up,
                    velocity: ref velocity,
                    orientation: in m_orientation,
                    volumes: volumes
                )
            );
        }

        m_previousPosition = trajectory.PreviousPosition;
        m_position = next;
        m_planarVelocity = new FixedVector3(
            X: velocity.X,
            Y: FixedQ4816.Zero,
            Z: velocity.Z
        );
        m_verticalVelocity = velocity.Y;
        m_grounded = resolution.Grounded;
        m_lastContactCount = (resolution.Grounded
            ? 1
            : 0
        );
        var consumedThrough = Math.Max(
            val1: trajectory.ConsumedThroughEngineTick,
            val2: destinationCompletedEngineTick
        );

        m_pendingContinuum = trajectory with { ConsumedThroughEngineTick = consumedThrough };
        m_continuumConsumedThroughEngineTick = consumedThrough;
        m_ordinaryAdvanceAdmitted = false;
    }
    /// <summary>Restores the named action-edge/register subset that must remain continuous when exactly one writer
    /// hands this body to another authority. Destination names and kinds are authoritative; unknown rows are ignored
    /// and a carried value the destination's own envelope refuses settles through it — a range clamps, a closed set
    /// settles to the authored initial.</summary>
    /// <param name="continuity">The carried edge and register subset.</param>
    /// <param name="channels">The destination's compiled channel table.</param>
    /// <param name="settledToInitial">Collects the name of every register a closed-set refusal settled to its
    /// authored initial, so the caller can narrate it; <see langword="null"/> collects nothing.</param>
    public void ApplyTransferActionContinuity(WorldTransferActionContinuity continuity, WorldChannelTable channels, ICollection<string>? settledToInitial = null) {
        ArgumentNullException.ThrowIfNull(continuity);
        ArgumentNullException.ThrowIfNull(channels);

        var held = default(PlayerIntent);

        foreach (var channel in continuity.Channels) {
            if (
                channels.TryGetOrdinal(
                name: channel.Name,
                ordinal: out var ordinal
            ) &&
                (((uint)ordinal) < ((uint)m_previousChannelBit.Length))
            ) {
                m_previousChannelBit[ordinal] = channel.PreviousBit;
                held = held.WithChannel(
                    ordinal: ordinal,
                    value: channel.HeldValue
                );
            }
        }

        m_transferHeldChannels = held;
        m_hasTransferHeldChannels = (held != default);

        // An arrival loads the carried register file into this body's slot lanes. A carried value the destination's
        // own envelope does not admit settles by the envelope's shape: a range has a nearest admitted value and
        // clamps to it, a closed set has none and settles to the authored initial. Neither keeps what the
        // destination happened to hold, which would let an arriving body inherit a stranger's register.
        foreach (var register in continuity.Registers) {
            for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
                var definition = m_actionStateDefinitions[slot];

                if (
                    !string.Equals(
                    a: definition.Name,
                    b: register.Name,
                    comparisonType: StringComparison.Ordinal
                ) ||
                    (definition.Kind != register.Kind)
                ) {
                    continue;
                }

                var carried = ((definition.Kind == ActionStateKind.Counter)
                    ? register.Value.Value
                    : unchecked((long)register.TimerTicks)
                );

                if (
                    (definition.Envelope is { } envelope) &&
                    !envelope.Contains(value: carried)
                ) {
                    if (envelope.Values is null) {
                        carried = Math.Clamp(
                            max: envelope.Maximum,
                            min: envelope.Minimum,
                            value: carried
                        );
                    } else {
                        carried = WorldActionStateLane.InitialRaw(definition: in definition);

                        settledToInitial?.Add(item: definition.Name);
                    }
                }
                if (definition.Kind == ActionStateKind.Timer) {
                    carried = Math.Max(
                        val1: 0L,
                        val2: carried
                    );
                }

                WriteStateRaw(
                    raw: carried,
                    slot: slot
                );

                break;
            }
        }
    }
    /// <summary>Reads this body's whole register file out of the arena slot lanes, one raw value per slot.</summary>
    /// <returns>The lane image, parallel to the kit's compiled definitions.</returns>
    public long[] CaptureActionState() {
        var image = new long[m_actionStateDefinitions.Length];

        for (var slot = 0; (slot < image.Length); slot++) {
            image[slot] = ((m_stateLane is { } lane)
                ? lane.Read(
                    ordinal: m_stateOrdinal,
                    slot: slot
                )
                : 0L
            );
        }

        return image;
    }
    /// <summary>Writes a previously captured lane image back into this body's slot lanes.</summary>
    /// <param name="image">The lane image, parallel to the kit's compiled definitions.</param>
    public void RestoreActionState(IReadOnlyList<long> image) {
        ArgumentNullException.ThrowIfNull(argument: image);

        var count = Math.Min(
            val1: image.Count,
            val2: m_actionStateDefinitions.Length
        );

        for (var slot = 0; (slot < count); slot++) {
            WriteStateRaw(
                raw: image[slot],
                slot: slot
            );
        }
    }
    /// <summary>Reapplies a captured <see cref="WorldBodyTransferState"/> — the abort/refire invariant's own ordering: call
    /// this after a hard-teleport commit (<see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>, which
    /// routes through <see cref="CommitTeleport"/> and zeroes velocity/overlay/the previous-position anchor exactly
    /// like any other hard pose write), never before — restoring perceivable dynamic state is only meaningful once the
    /// discontinuity itself has already collapsed the stale carries a fresh construction never had in the first
    /// place. The body-motion program is reapplied first, inside this method, before every other write below — see
    /// this method's own body for why: <see cref="SetBodyMotionProgram(string)"/> carries its own reset side effects
    /// (re-pinning yaw/orientation, clearing the medium facts, resetting the recency clocks) that would clobber
    /// everything else this method restores if it ran after them. Writing the channel-timer arrays here (rather than
    /// at construction) keeps this the one place a restored body's action track re-arms, symmetric with the
    /// velocity/orientation fields beside it.</summary>
    /// <param name="state">The state a matching <see cref="CaptureTransferState"/> call produced.</param>
    public void ApplyTransferState(WorldBodyTransferState state) {
        // A restored body never sleeps through the restore that just replaced its velocity/orientation/program —
        // see WorldBody.Sleep.cs's own remarks. SetBodyMotionProgram below wakes too, but only when the captured
        // program name differs from the fresh body's default, so this cannot rely on that alone.
        WakeUp();

        // FIRST: a program switch reruns part of the SAME reset ApplyTransferState exists to restore on top of (see
        // this method's own summary) — every write below must be the LAST word, never this one. A no-op when the
        // captured name already matches the fresh body's own kit-default program (the common case, no body.motion
        // switch): SetBodyMotionProgram's own early-return skips every side effect entirely.
        if (!string.IsNullOrEmpty(value: state.BodyMotionProgramName)) {
            SetBodyMotionProgram(programName: state.BodyMotionProgramName);
        }

        m_planarVelocity = state.PlanarVelocity;
        m_verticalVelocity = state.VerticalVelocity;
        m_orientation = state.Orientation;
        m_drivePitch = state.DrivePitch;
        m_overlayVelocity = state.OverlayVelocity;
        m_overlayRemaining = state.OverlayRemainingTicks;
        m_source = state.Source;
        m_pendingContinuum = state.PendingContinuum;
        m_continuumConsumedThroughEngineTick = state.PendingContinuum?.ConsumedThroughEngineTick;
        if (state.PendingContinuum is not null) {
            m_ordinaryAdvanceAdmitted = false;
        }

        var ticks = state.ChannelTimerTicks;
        var values = state.ChannelTimerValues;
        var timedCount = Math.Min(
            val1: Math.Min(
                val1: ticks.Length,
                val2: values.Length
            ),
            val2: ActionLaneCount
        );

        for (var ordinal = 0; (ordinal < timedCount); ordinal++) {
            m_laneTimers[ordinal] = ticks[ordinal];
            m_channelTimerValues[ordinal] = values[ordinal];
        }

        CopyClamped(
            source: state.PreviousChannelBit,
            destination: m_previousChannelBit
        );
        m_transferHeldChannels = state.HeldChannelImage;
        m_hasTransferHeldChannels = (state.HeldChannelImage != default);
        CopyClamped(
            source: state.PendingDefaultChannelPress,
            destination: m_pendingDefaultChannelPress
        );
        CopyClamped(
            source: state.PendingDefaultChannelValue,
            destination: m_pendingDefaultChannelValue
        );
        CopyClamped(
            source: state.MotionRecency,
            destination: m_motionRecency
        );

        // The rate accumulators' own remainders — FromRemainder never throws here: a captured remainder was always
        // read off a LIVE accumulator bound to this SAME EngineTicksPerSecond base, whose own Integrate contract
        // already guarantees |remainder| < ticksPerSecond (Puck.Maths.FixedRateAccumulator's own invariant).
        m_planarRampAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.PlanarRampRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_driveLongAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.DriveLongRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_driveLatAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.DriveLatRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_driveResidualAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.DriveResidualRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_mediumThrustRampAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.MediumThrustRampRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_planarFollower = new SecondOrderState3(
            X: SecondOrderState.FromRawBits(
                positionRaw: state.PlanarFollowerPositionRawX,
                velocityRaw: state.PlanarFollowerVelocityRawX
            ),
            Y: SecondOrderState.FromRawBits(
                positionRaw: state.PlanarFollowerPositionRawY,
                velocityRaw: state.PlanarFollowerVelocityRawY
            ),
            Z: SecondOrderState.FromRawBits(
                positionRaw: state.PlanarFollowerPositionRawZ,
                velocityRaw: state.PlanarFollowerVelocityRawZ
            )
        );
        m_planarPreviousTarget = state.PlanarFollowerPreviousTarget;
        m_planarFollowerSeeded = true;
        m_verticalFollower = SecondOrderState.FromRawBits(
            positionRaw: state.VerticalFollowerPositionRaw,
            velocityRaw: state.VerticalFollowerVelocityRaw
        );
        m_verticalPreviousTarget = state.VerticalFollowerPreviousTarget;
        m_verticalFollowerSeeded = true;
        m_overlayAccumulator = FixedVector3RateAccumulator.FromRemainders(
            xRemainder: state.OverlayRemainderX,
            yRemainder: state.OverlayRemainderY,
            zRemainder: state.OverlayRemainderZ,
            ticksPerSecond: EngineTicksPerSecond
        );

        var laneCount = Math.Min(
            val1: Math.Min(
                val1: state.LaneLatch.Length,
                val2: state.LaneFactHeld.Length
            ),
            val2: ActionLaneCount
        );

        for (var lane = 0; (lane < laneCount); lane++) {
            m_laneActions[lane].Latch = state.LaneLatch[lane];
            m_laneActions[lane].FactHeld = state.LaneFactHeld[lane];

            if (
                (lane < state.LaneRecency.Length) &&
                (state.LaneRecency[lane] is { } capturedRecency) &&
                (m_laneActions[lane].Recency is { } targetRecency)
            ) {
                CopyClamped(
                    destination: targetRecency,
                    source: capturedRecency
                );
            }
        }

        RestoreActionState(image: state.ActionState);

        var dirtyCount = Math.Min(
            val1: Math.Min(
                val1: state.ActionStateDirty.Length,
                val2: state.ActionStateDirtyKind.Length
            ),
            val2: Math.Min(
                val1: state.ActionStateDirtyOperand.Length,
                val2: m_actionStateDefinitions.Length
            )
        );

        for (var slot = 0; (slot < dirtyCount); slot++) {
            m_actionStateDirty[slot] = state.ActionStateDirty[slot];
            m_actionStateDirtyKind[slot] = state.ActionStateDirtyKind[slot];
            m_actionStateDirtyOperand[slot] = state.ActionStateDirtyOperand[slot];
        }

        var durableCount = Math.Min(
            val1: Math.Min(
                val1: state.DurableInputPresent.Length,
                val2: state.DurableInputValues.Length
            ),
            val2: Math.Min(
                val1: Math.Min(
                    val1: state.DurableInputTimers.Length,
                    val2: state.DurableInputWriters.Length
                ),
                val2: m_durableInputPresent.Length
            )
        );

        for (var slot = 0; (slot < durableCount); slot++) {
            m_durableInputPresent[slot] = state.DurableInputPresent[slot];
            m_durableInputValues[slot] = state.DurableInputValues[slot];
            m_durableInputTimers[slot] = state.DurableInputTimers[slot];
            m_durableInputWriters[slot] = state.DurableInputWriters[slot];
        }

        m_durableInputTick = state.DurableInputTick;

        RestoreTape(
            intents: state.TapeIntents,
            remainingTicks: state.TapeRemainingTicks
        );
    }
    /// <summary>Stops an exhausted continuum at the last confirmed ownership face. Tangential momentum survives;
    /// only velocity trying to leave this owner is removed.</summary>
    public void ClampContinuum(in WorldFaceFrame frame, FixedQ4816 seamU, FixedQ4816 seamV) {
        var inward = FixedQ4816.FromRawBits(value: 1L);

        m_position = (frame.PointAt(
            u: seamU,
            v: seamV
        ) - (frame.Normal * inward));
        m_previousPosition = m_position;
        var velocity = (m_planarVelocity + (FixedVector3.UnitY * m_verticalVelocity));
        var outward = FixedVector3.Dot(
            left: velocity,
            right: frame.Normal
        );

        if (outward > FixedQ4816.Zero) {
            velocity -= (frame.Normal * outward);
            m_planarVelocity = new FixedVector3(
                X: velocity.X,
                Y: FixedQ4816.Zero,
                Z: velocity.Z
            );
            m_verticalVelocity = velocity.Y;
        }
        m_positionAccumulator.Reset();
        m_pendingContinuum = null;
    }
    /// <summary>Clears the exact pending trajectory after topology either retained this owner, forwarded the body, or
    /// safety-clamped it. The independent consumed-through time fence remains until a non-overlapping ordinary
    /// authority step begins.</summary>
    public void ClearPendingContinuum() => m_pendingContinuum = null;
    /// <summary>Captures this body's integration residue — see <see cref="WorldBodyIntegrationResidue"/>. Read live, right
    /// now, never cached.</summary>
    public WorldBodyIntegrationResidue CaptureIntegrationResidue() => new(
        PreviousPosition: m_previousPosition,
        PositionRemainderX: m_positionAccumulator.XRemainder,
        PositionRemainderY: m_positionAccumulator.YRemainder,
        PositionRemainderZ: m_positionAccumulator.ZRemainder,
        RotationRemainderX: m_rotationAccumulator.XRemainder,
        RotationRemainderY: m_rotationAccumulator.YRemainder,
        RotationRemainderZ: m_rotationAccumulator.ZRemainder,
        VerticalVelocityRemainder: m_verticalVelocityAccumulator.Remainder,
        Up: m_up,
        Grounded: m_grounded,
        Engaged: m_engaged,
        EngagedIntent: m_engagedIntent,
        OrdinaryAdvanceAdmitted: m_ordinaryAdvanceAdmitted,
        ContinuumConsumedThroughEngineTick: m_continuumConsumedThroughEngineTick,
        AffectingSubject: m_affectingSubject,
        Frame: m_frame,
        UpNeedsReseat: m_upNeedsReseat,
        FieldUpTurnRemainder: m_upTurnAccumulator.Remainder,
        ContactUpTurnRemainder: m_contactUpTurnAccumulator.Remainder,
        AsleepSinceTick: m_asleepSinceTick,
        SleepIdleTicks: m_sleepIdleTicks,
        LastContactFieldVersion: m_lastContactFieldVersion,
        ContactFieldObservationCurrent: true,
        PlanarFollowerSeeded: m_planarFollowerSeeded,
        VerticalFollowerSeeded: m_verticalFollowerSeeded,
        Tether: new WorldBodyTetherResidue(
            AttachPreviousBit: m_attachPreviousBit,
            DetachPreviousBit: m_detachPreviousBit,
            Tether: m_tether?.CaptureState(),
            TetherAnchorBodyIndex: m_tetherAnchorBodyIndex,
            TetherAnchorPointOrLocalOffset: m_tetherAnchorPointOrLocalOffset
        ),
        HoldAnchor: m_holdAnchor,
        HoldIndex: m_holdIndex,
        HoldNormal: m_holdNormal,
        HoldSpendRemainder: m_holdSpendAccumulator.Remainder,
        AttitudeUp: m_attitudeUp,
        AttitudeTurnRemainder: m_attitudeTurnAccumulator.Remainder,
        AttitudeLeaned: m_attitudeLeaned,
        Home: m_home,
        RigidVelocity: m_rigidVelocity,
        RigidAngularVelocity: m_angularVelocity,
        RigidResting: m_resting,
        RigidRestingHoldTicks: m_restingHoldTicks,
        RigidGroundContacting: m_rigidGroundContacting,
        RigidObstructionContacting: m_rigidObstructionContacting,
        RigidGroundMissStreak: m_rigidGroundMissStreak,
        RigidObstructionMissStreak: m_rigidObstructionMissStreak,
        Carrying: m_carryingIndex,
        CarriedBy: m_carriedByIndex
    );
    /// <summary>Restores a previously captured integration residue onto this body — called after
    /// <see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/> has already set position/orientation and
    /// after <see cref="ApplyTransferState"/> has already set the rest of the live state, so this call's own writes
    /// are never overwritten by an earlier restore step. The restored population's equivalent contact field can have
    /// a different process-local version, so a current observation is rebased to <paramref name="contactFieldVersion"/>
    /// while a pending version edge remains deliberately unequal.</summary>
    /// <param name="residue">The checkpoint-only continuation state.</param>
    /// <param name="contactFieldVersion">The reconstructed population's current contact-field version, or
    /// <see langword="null"/> to restore the captured numeric version directly.</param>
    public void ApplyIntegrationResidue(WorldBodyIntegrationResidue residue, ulong? contactFieldVersion = null) {
        m_previousPosition = residue.PreviousPosition;
        m_positionAccumulator = FixedVector3RateAccumulator.FromRemainders(
            xRemainder: residue.PositionRemainderX,
            yRemainder: residue.PositionRemainderY,
            zRemainder: residue.PositionRemainderZ,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_rotationAccumulator = FixedVector3RateAccumulator.FromRemainders(
            xRemainder: residue.RotationRemainderX,
            yRemainder: residue.RotationRemainderY,
            zRemainder: residue.RotationRemainderZ,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_verticalVelocityAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: residue.VerticalVelocityRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_up = residue.Up;
        m_grounded = residue.Grounded;
        m_engaged = residue.Engaged;
        m_engagedIntent = residue.EngagedIntent;
        m_ordinaryAdvanceAdmitted = residue.OrdinaryAdvanceAdmitted;
        m_continuumConsumedThroughEngineTick = residue.ContinuumConsumedThroughEngineTick;
        m_affectingSubject = residue.AffectingSubject;
        m_frame = residue.Frame;
        m_upNeedsReseat = residue.UpNeedsReseat;
        m_upTurnAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: residue.FieldUpTurnRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_contactUpTurnAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: residue.ContactUpTurnRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_asleepSinceTick = residue.AsleepSinceTick;
        m_sleepIdleTicks = residue.SleepIdleTicks;
        m_lastContactFieldVersion = ((contactFieldVersion is not { } currentVersion)
            ? residue.LastContactFieldVersion
            : (residue.ContactFieldObservationCurrent
                ? currentVersion
                : currentVersion ^ 1UL
            )
        );
        m_planarFollowerSeeded = residue.PlanarFollowerSeeded;
        m_verticalFollowerSeeded = residue.VerticalFollowerSeeded;

        var tether = residue.Tether;

        m_attachPreviousBit = tether.AttachPreviousBit;
        m_detachPreviousBit = tether.DetachPreviousBit;
        m_holdAnchor = residue.HoldAnchor;
        m_holdIndex = residue.HoldIndex;
        m_holdNormal = residue.HoldNormal;
        m_holdSpendAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: residue.HoldSpendRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_attitudeUp = residue.AttitudeUp;
        m_attitudeLeaned = residue.AttitudeLeaned;
        m_attitudeTurnAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: residue.AttitudeTurnRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_home = residue.Home;
        m_tether = ((tether.Tether is { } tetherState)
            ? FixedTetherConstraint.FromState(state: tetherState)
            : null
        );
        m_tetherAnchorBodyIndex = tether.TetherAnchorBodyIndex;
        m_tetherAnchorPointOrLocalOffset = tether.TetherAnchorPointOrLocalOffset;
        m_rigidVelocity = residue.RigidVelocity;
        m_angularVelocity = residue.RigidAngularVelocity;
        m_resting = residue.RigidResting;
        m_restingHoldTicks = residue.RigidRestingHoldTicks;
        m_rigidGroundContacting = residue.RigidGroundContacting;
        m_rigidObstructionContacting = residue.RigidObstructionContacting;
        m_rigidGroundMissStreak = residue.RigidGroundMissStreak;
        m_rigidObstructionMissStreak = residue.RigidObstructionMissStreak;
        m_carryingIndex = residue.Carrying;
        m_carriedByIndex = residue.CarriedBy;
    }
}

using Puck.Maths;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.Physics;
using Puck.Physics.Motion;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Captures this body's own <see cref="WorldBodyTransferState"/> — read live, right now, never cached. Called
    /// before <see cref="Puck.World.Server.WorldPopulation.TryDetachSeatForTransfer"/> discards this body object, so
    /// the abort/restore path has something to reapply if the transfer unwinds. See <see cref="WorldBodyTransferState"/>'s own
    /// remarks for the complete field-by-field classification.</summary>
    public WorldBodyTransferState CaptureTransferState() => TransferState(buffers: null);

    // Captures into arrays taken from the buffers, so a capture taken every step (the sweep refusal's snapshot,
    // WorldBody.SweepRefusal.cs) allocates nothing in steady state. A capture into buffers lasts until they are next
    // reset.
    private WorldBodyTransferState CaptureTransferStateInto(CaptureBuffers buffers) => TransferState(buffers: buffers);
    // The one statement of what the transfer state holds. The capture owns every array, taken from the buffers when
    // there are some and fresh otherwise.
    private WorldBodyTransferState TransferState(CaptureBuffers? buffers) {
        var laneLatch = Take<ulong>(buffers: buffers, length: ActionLaneCount);
        var laneFactHeld = Take<ulong>(buffers: buffers, length: ActionLaneCount);
        var laneRecency = Take<ulong[]?>(buffers: buffers, length: ActionLaneCount);

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            laneLatch[lane] = Action.LaneLatch[lane];
            laneFactHeld[lane] = Action.LaneFactHeld[lane];
            laneRecency[lane] = ((m_laneRecencyStart[lane] == m_laneRecencyStart[(lane + 1)])
                ? null
                : CopyInto(buffers: buffers, source: LaneRecency(lane: lane))
            );
        }

        var registerWrites = m_registerWrites.Read;
        var registerWriteDirty = Take<bool>(buffers: buffers, length: registerWrites.Length);
        var registerWriteKinds = Take<WorldDocumentWriteKind>(buffers: buffers, length: registerWrites.Length);
        var registerWriteOperands = Take<FixedQ4816>(buffers: buffers, length: registerWrites.Length);

        for (var slot = 0; (slot < registerWrites.Length); slot++) {
            registerWriteDirty[slot] = registerWrites[slot].Dirty;
            registerWriteKinds[slot] = registerWrites[slot].Kind;
            registerWriteOperands[slot] = registerWrites[slot].Operand;
        }

        var durableInput = DurableInput.Rows.AsSpan();
        var durableInputPresent = Take<bool>(buffers: buffers, length: durableInput.Length);
        var durableInputValues = Take<FixedQ4816>(buffers: buffers, length: durableInput.Length);
        var durableInputTimers = Take<ulong>(buffers: buffers, length: durableInput.Length);
        var durableInputWriters = Take<string>(buffers: buffers, length: durableInput.Length);

        for (var slot = 0; (slot < durableInput.Length); slot++) {
            durableInputPresent[slot] = durableInput[slot].Present;
            durableInputValues[slot] = durableInput[slot].Value;
            durableInputTimers[slot] = durableInput[slot].Timer;
            durableInputWriters[slot] = durableInput[slot].Writer;
        }

        var actionState = Take<long>(buffers: buffers, length: m_actionStateDefinitions.Length);

        ReadActionState(image: actionState);

        var tapeIntents = Take<PlayerIntent>(buffers: buffers, length: m_tapeCount);
        var tapeRemainingTicks = Take<ulong>(buffers: buffers, length: m_tapeCount);
        var tapeActors = Take<Principal>(buffers: buffers, length: m_tapeCount);

        for (var offset = 0; (offset < m_tapeCount); offset++) {
            var segment = m_tape[((m_tapeHead + offset) % m_tape.Length)];

            tapeIntents[offset] = segment.Intent;
            tapeRemainingTicks[offset] = segment.RemainingTicks;
            tapeActors[offset] = segment.Actor;
        }

        return new(
            ScriptActors: new(CopyInto(buffers: buffers, source: ScriptActors.Timers.AsSpan()), CopyInto(buffers: buffers, source: ScriptActors.Pending.AsSpan()), tapeActors),
            PlanarVelocity: m_planarVelocity,
            VerticalVelocity: (m_verticalVelocity + m_directVerticalVelocity),
            Orientation: m_orientation,
            DrivePitch: m_drivePitch,
            OverlayVelocity: m_overlayVelocity,
            OverlayRemainingTicks: m_overlayRemaining,
            TimedMove: m_timedMove,
            ChannelTimerTicks: CopyInto(buffers: buffers, source: ((ReadOnlySpan<ulong>)Action.TimerTicks)),
            ChannelTimerValues: CopyInto(buffers: buffers, source: ((ReadOnlySpan<FixedQ4816>)Action.TimerValues)),
            RelativeRemainders: CopyInto(buffers: buffers, source: ((m_channels.RelativeMask == 0) ? default : ((ReadOnlySpan<long>)InputReadbacks.RelativeRemainders))),
            BodyMotionProgramName: m_bodyMotionProgram.Name,
            Source: InputImages.Source,
            PreviousChannelBit: CopyInto(buffers: buffers, source: ((ReadOnlySpan<bool>)Action.PreviousBit)),
            HeldChannelImage: (InputImages.HasTransferHeld
            ? InputImages.TransferHeld
            : InputReadbacks.ReadHeld),
            PendingDefaultChannelPress: CopyInto(buffers: buffers, source: ((ReadOnlySpan<bool>)Action.PendingPress)),
            PendingDefaultChannelValue: CopyInto(buffers: buffers, source: ((ReadOnlySpan<FixedQ4816>)Action.PendingValue)),
            MotionRecency: CopyInto(buffers: buffers, source: m_motionRecency),
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
            ActionState: actionState,
            ActionStateDirty: registerWriteDirty,
            ActionStateDirtyKind: registerWriteKinds,
            ActionStateDirtyOperand: registerWriteOperands,
            DurableInputPresent: durableInputPresent,
            DurableInputValues: durableInputValues,
            DurableInputTimers: durableInputTimers,
            DurableInputWriters: durableInputWriters,
            DurableInputTick: DurableInput.Tick,
            TapeIntents: tapeIntents,
            TapeRemainingTicks: tapeRemainingTicks,
            PendingContinuum: m_pendingContinuum
        );
    }

    /// <summary>Installs an adjacency arrival's already-evaluated motion segment and resolves it through this
    /// authority's own contact field. No input, action, timer, gravity, or motion-program operation is evaluated.</summary>
    public void ApplyContinuumTrajectory(in WorldContinuumTrajectory trajectory, int entityIndex, ulong destinationCompletedEngineTick) {
        var next = m_position;
        var velocity = ComposedVelocity();
        var resolution = default(ContactResolution);

        m_sweepRefusal = ContactRefusal.None;

        if (
            (m_contactField is { } field) &&
            (m_collider is { } collider)
        ) {
            var volumes = ScaledColliderVolumes();

            resolution = SweepContact(
                entityIndex: entityIndex,
                field: field,
                previousPosition: trajectory.PreviousPosition,
                position: ref next,
                up: in m_up,
                velocity: ref velocity,
                orientation: in m_orientation,
                volumes: volumes
            );
        }

        // A refused sweep is a full block (WorldBody.SweepRefusal.cs): the arrival keeps the pose, velocity and contact
        // facts it was installed with, and only the segment's consumption below advances.
        if (resolution.Refusal != ContactRefusal.None) {
            NoteSweepRefusal(refusal: resolution.Refusal);
        } else {
            m_previousPosition = trajectory.PreviousPosition;
            m_position = next;
            SplitVelocity(
                resetVerticalRemainder: false,
                velocity: velocity
            );
            m_grounded = resolution.Grounded;
            m_lastContactCount = (resolution.Grounded
                ? 1
                : 0
            );
        }
        var consumedThrough = Math.Max(
            val1: trajectory.ConsumedThroughEngineTick,
            val2: destinationCompletedEngineTick
        );

        m_pendingContinuum = trajectory with { ConsumedThroughEngineTick = consumedThrough };
        m_continuumConsumedThroughEngineTick = consumedThrough;
        m_ordinaryAdvanceAdmitted = false;
    }
    /// <summary>Restores the named action registers that remain continuous when exactly one writer
    /// hands this body to another authority. Destination names and kinds are authoritative; unknown rows are ignored
    /// and a carried value the destination's own envelope refuses settles through it — a range clamps, a closed set
    /// settles to the authored initial.</summary>
    /// <param name="continuity">The carried register subset.</param>
    /// <param name="settledToInitial">Collects the name of every register a closed-set refusal settled to its
    /// authored initial, so the caller can narrate it; <see langword="null"/> collects nothing.</param>
    public void ApplyTransferActionContinuity(WorldTransferActionContinuity continuity, ICollection<string>? settledToInitial = null) {
        ArgumentNullException.ThrowIfNull(continuity);

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

                m_actionStateRequested[slot] = carried;
                m_actionStateLastWriter[slot] = "arrival";
                m_actionStateLastReason[slot] = "carried across the crossing";

                if (
                    (definition.Envelope is { } envelope) &&
                    !envelope.Contains(value: carried)
                ) {
                    m_actionStateLastReason[slot] = "carried across the crossing; settled by this world's envelope";

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

    // An array of the given length, taken from the buffers when there are some and fresh otherwise.
    private static T[] Take<T>(CaptureBuffers? buffers, int length) => (buffers?.Take<T>(length: length) ?? ((length == 0)
        ? []
        : new T[length]
    ));
    // A copy of the source, in an array taken as Take takes one.
    private static T[] CopyInto<T>(CaptureBuffers? buffers, ReadOnlySpan<T> source) {
        var copy = Take<T>(buffers: buffers, length: source.Length);

        source.CopyTo(destination: copy);

        return copy;
    }
    // Reads the register file into image, one raw value per slot of the kit's compiled definitions.
    private void ReadActionState(long[] image) {
        for (var slot = 0; (slot < image.Length); slot++) {
            image[slot] = ((m_stateLane is { } lane)
                ? lane.Read(
                    ordinal: m_stateOrdinal,
                    slot: slot
                )
                : 0L
            );
        }
    }

    /// <summary>Writes a previously captured lane image back into this body's slot lanes.</summary>
    /// <param name="image">The lane image, parallel to the kit's compiled definitions.</param>
    public void RestoreActionState(ReadOnlySpan<long> image) {
        var count = Math.Min(
            val1: image.Length,
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
        SetScriptActors(pending: state.ScriptActors.Pending, timers: state.ScriptActors.Timers);
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
        m_timedMove = (CanStartTimedMove ? state.TimedMove : default);
        InputImagesEdit.Source = state.Source;
        m_pendingContinuum = state.PendingContinuum;
        m_continuumConsumedThroughEngineTick = state.PendingContinuum?.ConsumedThroughEngineTick;
        if (state.PendingContinuum is not null) {
            m_ordinaryAdvanceAdmitted = false;
        }

        // A body with no relative channel carries no fractions, whatever the capture held.
        if (m_channels.RelativeMask != 0) { CopyClamped(source: state.RelativeRemainders, destination: ((Span<long>)InputReadbacksEdit.RelativeRemainders)); }
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
            ActionEdit.TimerTicks[ordinal] = ticks[ordinal];
            ActionEdit.TimerValues[ordinal] = values[ordinal];
        }

        CopyClamped(
            source: state.PreviousChannelBit,
            destination: ((Span<bool>)ActionEdit.PreviousBit)
        );
        InputImagesEdit.TransferHeld = state.HeldChannelImage;
        InputImagesEdit.HasTransferHeld = (state.HeldChannelImage != default);
        CopyClamped(
            source: state.PendingDefaultChannelPress,
            destination: ((Span<bool>)ActionEdit.PendingPress)
        );
        CopyClamped(
            source: state.PendingDefaultChannelValue,
            destination: ((Span<FixedQ4816>)ActionEdit.PendingValue)
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
            ActionEdit.LaneLatch[lane] = state.LaneLatch[lane];
            ActionEdit.LaneFactHeld[lane] = state.LaneFactHeld[lane];

            if (
                (lane < state.LaneRecency.Length) &&
                (state.LaneRecency[lane] is { } capturedRecency)
            ) {
                var kept = capturedRecency.AsSpan(length: Math.Min(val1: capturedRecency.Length, val2: LaneRecency(lane: lane).Length), start: 0);

                if (!LaneRecency(lane: lane)[..kept.Length].SequenceEqual(other: kept)) {
                    kept.CopyTo(destination: m_laneRecency.Write[m_laneRecencyStart[lane]..]);
                }
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
            var staged = new WorldRegisterWriteImage(Dirty: state.ActionStateDirty[slot], Kind: state.ActionStateDirtyKind[slot], Operand: state.ActionStateDirtyOperand[slot]);

            if (m_registerWrites.Read[slot] != staged) { m_registerWrites.Write[slot] = staged; }
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
                val2: DurableInput.Rows.Length
            )
        );
        var durableDiffers = false;

        for (var slot = 0; (slot < durableCount); slot++) {
            durableDiffers |= (DurableInput.Rows[slot] != new WorldDurableInputImage(Present: state.DurableInputPresent[slot], Timer: state.DurableInputTimers[slot], Value: state.DurableInputValues[slot], Writer: state.DurableInputWriters[slot]));
        }
        // The table is immutable behind its door, so it is rebuilt only when a captured row differs from the staged one.
        if (durableDiffers) {
            var durableInput = new WorldDurableInputImage[durableCount];

            for (var slot = 0; (slot < durableCount); slot++) {
                durableInput[slot] = new(Present: state.DurableInputPresent[slot], Timer: state.DurableInputTimers[slot], Value: state.DurableInputValues[slot], Writer: state.DurableInputWriters[slot]);
            }
            RestoreDurableInput(rows: durableInput, tick: state.DurableInputTick);
        } else if (DurableInput.Tick != state.DurableInputTick) {
            m_durableInput.Write = (DurableInput with { Tick = state.DurableInputTick });
        }

        ResetTape(count: Math.Min(val1: state.TapeIntents.Count, val2: state.TapeRemainingTicks.Count));
        for (var index = 0; (index < m_tapeCount); index++) {
            m_tape[index] = new TapeSegment { Actor = state.ScriptActors.Tape[index], Intent = state.TapeIntents[index], RemainingTicks = state.TapeRemainingTicks[index] };
        }
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
        var velocity = ComposedVelocity();
        var outward = FixedVector3.Dot(
            left: velocity,
            right: frame.Normal
        );

        if (outward > FixedQ4816.Zero) {
            SplitVelocity(
                resetVerticalRemainder: false,
                velocity: (velocity - (frame.Normal * outward))
            );
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
    /// <remarks>Every step captures it (<see cref="CaptureMotion"/>), and the record holds kilobytes of intent images; the
    /// constructor writes every field of the temporaries it builds, so the frame skips the zero fill.</remarks>
    [System.Runtime.CompilerServices.SkipLocalsInit]
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
        Engaged: InputImages.Engaged,
        EngagedIntent: InputReadbacks.EngagedIntent,
        Input: new(InputImages.Submitted, InputImages.HasSubmitted, InputImages.Held, InputImages.Producer, InputImages.HasProducer,
            InputImages.TransferHeld, InputImages.HasTransferHeld, InputReadbacks.ReadHeld, InputReadbacks.ReadComposed, InputReadbacks.ReadScalar,
            InputMappingId, InputEpoch),
        OrdinaryAdvanceAdmitted: m_ordinaryAdvanceAdmitted,
        ContinuumConsumedThroughEngineTick: m_continuumConsumedThroughEngineTick,
        AffectingSubject: m_affectingSubject,
        Frame: m_frame,
        UpNeedsReseat: m_upNeedsReseat,
        FieldUpTurnRemainder: m_upTurnAccumulator.Remainder,
        ContactUpTurnRemainder: m_contactUpTurnAccumulator.Remainder,
        IdleSleepGravity: IdleSleepGravity,
        AsleepSinceTick: m_asleepSinceTick,
        SleepIdleTicks: m_sleepIdleTicks,
        LastContactFieldVersion: m_lastContactFieldVersion,

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
        RigidSleepRootPlusOne: m_rigidSleepRootPlusOne,
        Carrying: m_carryingIndex,
        CarriedBy: m_carriedByIndex
    );
    /// <summary>Restores a previously captured integration residue onto this body — called after
    /// <see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/> has already set position/orientation and
    /// after <see cref="ApplyTransferState"/> has already set the rest of the live state, so this call's own writes
    /// are never overwritten by an earlier restore step. The captured contact-field observation is restored as the
    /// number it was: the residue is a same-process snapshot, and a checkpoint restore rebases that process-local
    /// value itself (<see cref="RestoreSections"/>).</summary>
    /// <param name="residue">The same-process continuation state.</param>
    public void ApplyIntegrationResidue(WorldBodyIntegrationResidue residue) {
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
        InputImagesEdit.Engaged = residue.Engaged;
        InputReadbacksEdit.EngagedIntent = residue.EngagedIntent;
        InputImagesEdit.Submitted = residue.Input.Submitted;
        InputImagesEdit.HasSubmitted = residue.Input.HasSubmitted;
        InputImagesEdit.Held = residue.Input.Held;
        InputImagesEdit.Producer = residue.Input.Producer;
        InputImagesEdit.HasProducer = residue.Input.HasProducer;
        InputImagesEdit.TransferHeld = residue.Input.TransferHeld;
        InputImagesEdit.HasTransferHeld = residue.Input.HasTransferHeld;
        InputReadbacksEdit.ReadHeld = residue.Input.ReadHeld;
        InputReadbacksEdit.ReadComposed = residue.Input.ReadComposed;
        InputReadbacksEdit.ReadScalar = residue.Input.ReadScalar;
        InputImagesEdit.Mapping = residue.Input.Mapping;
        InputImagesEdit.Epoch = residue.Input.Epoch;
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
        IdleSleepGravity = residue.IdleSleepGravity;
        m_sleepIdleTicks = residue.SleepIdleTicks;
        m_lastContactFieldVersion = residue.LastContactFieldVersion;
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
        // The residue's island root is not applied here: the population parks the restored body under it
        // (WorldPopulation.RestoreSleepingIslands, AdoptRestoredIsland), the one writer of island membership.
        m_carryingIndex = residue.Carrying;
        m_carriedByIndex = residue.CarriedBy;
    }
}

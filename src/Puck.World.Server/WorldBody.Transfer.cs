using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    /// <summary>Captures this body's own <see cref="TransferState"/> — read live, right now, never cached. Called
    /// before <see cref="Puck.World.Server.WorldPopulation.TryDetachSeatForTransfer"/> discards this body object, so
    /// the abort/restore path has something to reapply if the transfer unwinds. See <see cref="TransferState"/>'s own
    /// remarks for the complete field-by-field classification.</summary>
    public TransferState CaptureTransferState() {
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
            VehiclePitch: m_vehiclePitch,
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
            VehicleLongRemainder: m_vehicleLongAccumulator.Remainder,
            VehicleLatRemainder: m_vehicleLatAccumulator.Remainder,
            VehicleResidualRemainder: m_vehicleResidualAccumulator.Remainder,
            SwimThrustRampRemainder: m_swimThrustRampAccumulator.Remainder,
            OverlayRemainderX: m_overlayAccumulator.XRemainder,
            OverlayRemainderY: m_overlayAccumulator.YRemainder,
            OverlayRemainderZ: m_overlayAccumulator.ZRemainder,
            LaneLatch: laneLatch,
            LaneFactHeld: laneFactHeld,
            LaneRecency: laneRecency,
            ActionStateValues: [.. m_actionStateValues],
            ActionStateTimers: [.. m_actionStateTimers],
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
        var velocity = (m_planarVelocity + (UnitY * m_verticalVelocity));
        var resolution = default(ContactResolution);

        if (
            (m_contactField is { } field) &&
            (m_collider is { } collider)
        ) {
            resolution = ((field is IEntityContactField entityField)
                ? entityField.ResolveEntitySweep(
                    entityIndex: entityIndex,
                    previousPosition: trajectory.PreviousPosition,
                    position: ref next,
                    up: in m_up,
                    velocity: ref velocity,
                    orientation: in m_orientation,
                    volumes: collider.Volumes
                )
                : field.ResolveSweep(
                    previousPosition: trajectory.PreviousPosition,
                    position: ref next,
                    up: in m_up,
                    velocity: ref velocity,
                    orientation: in m_orientation,
                    volumes: collider.Volumes
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
    /// and admitted values are clamped through the destination's own envelope.</summary>
    public void ApplyTransferActionContinuity(WorldTransferActionContinuity continuity, WorldChannelTable channels) {
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

                if (definition.Kind == ActionStateKind.Counter) {
                    var raw = register.Value.Value;
                    var initial = definition.InitialValue.Value;

                    m_actionStateValues[slot] = new FixedQ4816(Value: (definition.Envelope?.Clamp(
                        initial: initial,
                        value: raw
                    ) ?? raw));
                } else {
                    var raw = unchecked((long)register.TimerTicks);
                    var initial = unchecked((long)definition.InitialTicks);
                    var admitted = (definition.Envelope?.Clamp(
                        initial: initial,
                        value: raw
                    ) ?? raw);

                    m_actionStateTimers[slot] = unchecked((ulong)Math.Max(
                        val1: admitted,
                        val2: 0L
                    ));
                }
                break;
            }
        }
    }
    /// <summary>Reapplies a captured <see cref="TransferState"/> — the abort/refire invariant's own ordering: call
    /// this after a hard-teleport commit (<see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>, which
    /// routes through <see cref="CommitTeleport"/> and zeroes velocity/overlay/the previous-position anchor exactly
    /// like any other hard pose write), never before — restoring perceivable dynamic state is only meaningful once the
    /// discontinuity itself has already collapsed the stale carries a fresh construction never had in the first
    /// place. The body-motion program is reapplied first, inside this method, before every other write below — see
    /// this method's own body for why: <see cref="SetBodyMotionProgram(string)"/> carries its own reset side effects
    /// (re-pinning yaw/orientation, clearing swim medium facts, resetting the recency clocks) that would clobber
    /// everything else this method restores if it ran after them. Writing the channel-timer arrays here (rather than
    /// at construction) keeps this the one place a restored body's action track re-arms, symmetric with the
    /// velocity/orientation fields beside it.</summary>
    /// <param name="state">The state a matching <see cref="CaptureTransferState"/> call produced.</param>
    public void ApplyTransferState(TransferState state) {
        // FIRST: a program switch reruns part of the SAME reset ApplyTransferState exists to restore on top of (see
        // this method's own summary) — every write below must be the LAST word, never this one. A no-op when the
        // captured name already matches the fresh body's own kit-default program (the common case, no player.motion
        // switch): SetBodyMotionProgram's own early-return skips every side effect entirely.
        if (!string.IsNullOrEmpty(value: state.BodyMotionProgramName)) {
            SetBodyMotionProgram(programName: state.BodyMotionProgramName);
        }

        m_planarVelocity = state.PlanarVelocity;
        m_verticalVelocity = state.VerticalVelocity;
        m_orientation = state.Orientation;
        m_vehiclePitch = state.VehiclePitch;
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
        m_vehicleLongAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.VehicleLongRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_vehicleLatAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.VehicleLatRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_vehicleResidualAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.VehicleResidualRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
        m_swimThrustRampAccumulator = FixedRateAccumulator.FromRemainder(
            remainder: state.SwimThrustRampRemainder,
            ticksPerSecond: EngineTicksPerSecond
        );
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

        var actionStateCount = Math.Min(
            val1: Math.Min(
                val1: state.ActionStateValues.Length,
                val2: state.ActionStateTimers.Length
            ),
            val2: m_actionStateDefinitions.Length
        );

        for (var slot = 0; (slot < actionStateCount); slot++) {
            m_actionStateValues[slot] = state.ActionStateValues[slot];
            m_actionStateTimers[slot] = state.ActionStateTimers[slot];
        }

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
        var velocity = (m_planarVelocity + (UnitY * m_verticalVelocity));
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

    /// <summary>The subset of a body's own dynamic state that is perceivable — the in-flight rule docs/vision.md's
    /// "In-flight state at transfer" names ("drop and re-derive what the engine can recompute; carry what the player
    /// can perceive") applied to a same-process transfer's abort/restore path
    /// (<see cref="Puck.World.Server.WorldPopulation.TryDetachSeatForTransfer"/>/<see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/>),
    /// which otherwise discards the whole body object and reconstructs a fresh one at rest. Pose (position/yaw) is
    /// captured separately by that caller; this narrows to what else a player would notice — momentum carrying
    /// through the door, a dash still playing out, a charged button still held, a scripted tape still running, a
    /// switched body-motion program, a cooldown mid-count — never a re-derivable fact and never an absolute tick
    /// value: every ticks field here is a countdown or a remainder, decremented/consumed every <see cref="Advance"/>,
    /// never a stamped deadline tick — the same distinction that keeps a park's <c>ParkedUntilTick</c> out of this
    /// struct entirely.</summary>
    /// <remarks>
    /// <para>Every mutable instance field this class declares is classified below so a reviewer can check a table
    /// rather than re-hunt the class. Two invariants bound the classification either way: no park state (governed by
    /// <see cref="Puck.World.Server.WorldPopulation.Entry.ParkedUntilTick"/>, never this struct — re-derived on the
    /// next <c>DeactivateSeat</c>, never replayed from a snapshot), and no absolute tick (every field here is either
    /// a duration/countdown or a signed remainder — see <see cref="Puck.Maths.FixedRateAccumulator"/>'s own "the
    /// remainder is authoritative simulation state... a fraction, not a tick" contract, which is exactly why the
    /// integration-remainder fields below are safe to carry).</para>
    /// <para><b>Captured (this struct's fields, below).</b> <see cref="PlanarVelocity"/>, <see cref="VerticalVelocity"/>,
    /// <see cref="Orientation"/>, <see cref="VehiclePitch"/>, <see cref="OverlayVelocity"/>/<see cref="OverlayRemainingTicks"/>,
    /// <see cref="ChannelTimerTicks"/>/<see cref="ChannelTimerValues"/>.
    /// <see cref="BodyMotionProgramName"/> — a live <c>player.motion</c> switch away from the seat kit's own default
    /// program (<see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/> always reconstructs from the kit's
    /// default program; nothing else remembers a switch away from it).
    /// <see cref="Source"/> — the intent-source axis (<c>player.control</c>/the peer sweep); a fresh body always
    /// defaults to <c>Live</c>, so a body driven by a producer would silently snap back without this.
    /// <see cref="PreviousChannelBit"/> — the previous tick's per-ordinal threshold-crossing bit; without it a
    /// currently-held bound action's edge detector reads "not held last tick" on the very next Advance and can
    /// spuriously re-fire a rising-edge action (a jump) the player never released.
    /// <see cref="PendingDefaultChannelPress"/>/<see cref="PendingDefaultChannelValue"/> — an argument-less
    /// <c>player.press</c> tap staged but not yet materialized into a lane timer (<see cref="MaterializeDefaultLanePresses"/>
    /// only runs at the next Advance).
    /// <see cref="MotionRecency"/> — the body-motion program's own Recently-gate clocks (combo/gate windows a
    /// program's predicates read); <see cref="ResetVertical"/> zeroes these on every hard teleport by design (a
    /// teleport must not carry momentum), but an abort is not an ordinary teleport from the player's perspective —
    /// the same reasoning that justifies capturing <see cref="PlanarVelocity"/>/<see cref="VerticalVelocity"/>
    /// on top of the same reset, extended to their integration carries.
    /// <see cref="PlanarRampRemainder"/>/<see cref="VehicleLongRemainder"/>/<see cref="VehicleLatRemainder"/>/
    /// <see cref="VehicleResidualRemainder"/>/<see cref="SwimThrustRampRemainder"/>/<see cref="OverlayRemainderX"/>,Y,Z —
    /// the response/vehicle/swim/overlay rate accumulators' own <see cref="Puck.Maths.FixedRateAccumulator.Remainder"/>s.
    /// These are frame-independent (a rate of convergence, not a position), unlike the position/rotation/vertical
    /// accumulators excluded below — see this list's own "deliberately re-derived" entry for exactly why that
    /// distinction holds. Kart makes the vehicle trio live for an ordinary seat; Dive makes the swim one live.
    /// <see cref="LaneLatch"/>/<see cref="LaneFactHeld"/>/<see cref="LaneRecency"/> — the per-lane action runtime's
    /// own OnPress pending-latch bit, OnFact previous-evaluation edge bit, and Recently-gate clocks
    /// (<see cref="LaneActionRuntime"/>) — a buffered press awaiting its gate, an OnFact trigger's own edge memory,
    /// and a double-tap window all live here.
    /// <see cref="ActionStateValues"/>/<see cref="ActionStateTimers"/> — the kit's own named action-state register
    /// file's live values/timers (ammo counters, cooldown timers) — read by <see cref="GateOpen"/>'s CompareState/Timer
    /// predicates, so this is genuinely gameplay-affecting, not diagnostic.
    /// <see cref="ActionStateDirty"/>/<see cref="ActionStateDirtyKind"/>/<see cref="ActionStateDirtyOperand"/> — a
    /// durable (profile-persisted) write-back staged for <see cref="TakeDurableStateOutputs"/>.
    /// <see cref="Puck.World.Server.WorldPopulation.CompleteStep"/> drains every body's dirty flags unconditionally at
    /// the end of every tick, and a transfer's mutation-drain only ever runs between ticks, after that drain has
    /// already completed for the just-finished one, so in this engine's tick architecture the triple is always
    /// false/default at any point an abort could observe it (verified directly:
    /// <c>TransferAbortKitWideningLawTests</c> drives a real durable write through a real tick and confirms it reads
    /// drained at capture). Captured anyway, at negligible cost, as a hedge against that ordering ever changing.
    /// <see cref="DurableInputPresent"/>/<see cref="DurableInputValues"/>/<see cref="DurableInputTimers"/>/
    /// <see cref="DurableInputWriters"/>/<see cref="DurableInputTick"/> — an incoming durable-state write staged this
    /// tick (<see cref="ApplyDurableInput"/>) but not yet consumed by the next Advance — the same "staged, not yet
    /// materialized" class of gap as the pending channel press.
    /// <see cref="TapeIntents"/>/<see cref="TapeRemainingTicks"/> — the scripted tape (<c>player.fly</c>) in FIFO
    /// order, captured/restored at exact tick counts (never round-tripped through
    /// <see cref="FixedTickConversion.DurationEngineTicks"/>'s own seconds conversion, which would drift the
    /// restored duration from what was actually live) — the body's own future trajectory.</para>
    /// <para><b>Deliberately re-derived (with reason) — never added to this struct.</b>
    /// <c>m_motionArm</c>/<c>m_tuning</c>/<c>m_vehicleTuning</c>/<c>m_swimTuning</c>/<c>m_driftChannelOrdinal</c>/
    /// <c>m_sprintChannelOrdinal</c>/<c>m_laneBindings</c>/<c>m_channelThresholds</c>/<c>m_channelShapes</c>/
    /// <c>m_roleChannels</c>/<c>m_roleOrdinals</c>/<c>m_actionStateDefinitions</c>/<c>m_collider</c>/
    /// <c>m_maxSmoothError</c> — compiled kit config; <see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/>
    /// reconstructs the body from the same seat kit row (<c>m_kits[m_seatKit]</c>), so these are byte-identical
    /// without help. <c>m_contactField</c>/<c>m_hasWaterline</c>/<c>m_waterline</c> — wired directly by
    /// <c>RestoreDetachedSeat</c>'s own <see cref="SetContactField"/>/<see cref="SetWaterline"/> calls immediately
    /// after construction (the same population), always correct. <c>m_position</c>/<c>m_previousPosition</c>/
    /// <c>m_yaw</c> — captured/restored outside this struct entirely, via <c>RestoreDetachedSeat</c>'s own
    /// position/yaw parameters into <see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/> (this
    /// struct's own top-level remarks already say so). <c>m_positionAccumulator</c>/
    /// <c>m_rotationAccumulator</c>/<c>m_verticalVelocityAccumulator</c> — position-integration remainders tied to
    /// the old position's coordinate frame; <see cref="CommitTeleport"/> legitimately collapses these on every hard
    /// teleport, abort or not, so a warped/restored body's swept portal-crossing segment never ghosts back through
    /// space it never travelled in the new frame — carrying them across a discontinuous jump would be meaningless,
    /// unlike the frame-independent rate accumulators captured above. <c>m_grounded</c>/<c>m_up</c>/
    /// <c>m_lastContactCount</c> — pure functions of the current position and the world contact field; the very next
    /// grounded Advance re-derives them identically from the position <c>Pose()</c> already restored exactly. (<c>m_up</c>'s
    /// "held across a degenerate query" case is a narrow, bounded, self-correcting exception, named rather than
    /// silently assumed away: a restore whose immediately-following contact query is also degenerate at the exact
    /// restored position could read the fresh body's <c>+Y</c> default for one extra tick before a non-degenerate
    /// query corrects it.) <c>m_obstructionWitness</c>/<c>m_obstructionWitnessPosition</c>/
    /// <c>m_obstructionWitnessGraceTicks</c> — explicitly documented at their own declaration as "Read-back only" for
    /// <c>world.contacts</c>; losing the latch only ever produces a missing witness until the next real push or grace
    /// timeout, never a wrong positive one. <c>m_submerged</c>/<c>m_atSurface</c> — the swim surface stage
    /// re-derives both, purely as a function of the restored position and waterline, on the very next Advance.
    /// <c>m_heldChannels</c>/<c>m_channelReadHeld</c>/<c>m_channelReadComposed</c> — ordinary one-tick images; the
    /// last admitted held composition image is separately named in <paramref name="HeldChannelImage"/> solely for
    /// the bounded authority-handoff bridge.
    /// <c>m_submittedIntent</c>/<c>m_hasSubmittedIntent</c>/<c>m_producerIntent</c>/<c>m_hasProducerIntent</c> —
    /// one-tick device/producer images, consumed and reset every <see cref="Advance"/> by design ("a missed producer
    /// tick can never leave a stale entity moving forever" — this type's own existing doc comment). <c>m_actionStateRequested</c>/
    /// <c>m_actionStateLastWriter</c>/<c>m_actionStateLastReason</c> — pure audit text for <see cref="DescribeActionState"/>'s
    /// echo; <see cref="GateOpen"/> reads only Values/Timers, never these, so losing them regresses a diagnostic
    /// string, not gameplay — the same exclusion class as <c>m_channelReadHeld</c>. <c>m_continuity</c> — overwritten
    /// unconditionally by <c>RestoreDetachedSeat</c>'s own <c>Pose()</c> call, which already writes the correct
    /// value (Teleport) for a genuinely discontinuous restore. <c>m_affectingSubject</c> — reset to <c>-1</c> at the
    /// tail of every <see cref="Advance"/>, one-tick, like <c>m_heldChannels</c>.</para>
    /// <para><b>Not audited here — named rather than silently assumed fine.</b> <c>m_engaged</c>/<c>m_engagedIntent</c>
    /// (the screen-engagement route latch): engagement is governed by <see cref="Puck.World.Server.WorldEngagement"/>,
    /// a separate subsystem keyed by slot, not by this class — whether it re-establishes engagement onto a restored
    /// body is that subsystem's own question.
    /// <see cref="Puck.World.Server.WorldPopulation.Entry.Designations"/> and
    /// <see cref="Puck.World.Server.WorldPopulation.Entry.ProducerState"/> are not <see cref="WorldBody"/> fields at
    /// all; they live on the population's own per-seat <c>Entry</c>, entirely outside this struct's reach. They are
    /// addressed instead at the
    /// <see cref="Puck.World.Server.WorldPopulation.TryDetachSeatForTransfer"/>/<see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/>
    /// layer directly (see those methods' own remarks) — named here so a reviewer checking this struct's own
    /// completeness does not read their absence as an oversight.</para>
    /// </remarks>
    /// <param name="PlanarVelocity">The ramped horizontal velocity the grounded model integrates.</param>
    /// <param name="VerticalVelocity">The vertical (gravity/jump) velocity.</param>
    /// <param name="Orientation">The full attitude — captured directly rather than re-derived from yaw alone, so a
    /// future vehicle/swim seat kit's pitch/roll survives too (today's seat kits are grounded-only, where this always
    /// agrees with the yaw already carried alongside it — see <see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/>'s
    /// own remarks on the grounded-model exact case).</param>
    /// <param name="VehiclePitch">The vehicle frame's own climb-attitude scalar (inert, always zero, under a grounded
    /// seat kit — carried for the same forward-compatibility reason as <paramref name="Orientation"/>).</param>
    /// <param name="OverlayVelocity">The timed impulse overlay's (the dash) world-space velocity, if one is live.</param>
    /// <param name="OverlayRemainingTicks">Engine ticks remaining on the live overlay — a duration, not a deadline.</param>
    /// <param name="ChannelTimerTicks">Per-ordinal remaining ticks on an in-flight timed <c>player.press</c> — a
    /// duration per ordinal, copied defensively (never the live array).</param>
    /// <param name="ChannelTimerValues">The value each timed press in <paramref name="ChannelTimerTicks"/> holds while
    /// live, copied defensively.</param>
    /// <param name="BodyMotionProgramName">The live body-motion program's own name, reapplied through the same
    /// public <see cref="SetBodyMotionProgram(string)"/> door <c>player.motion</c> uses.</param>
    /// <param name="Source">The intent-source axis (<c>player.control</c>/the peer sweep).</param>
    /// <param name="PreviousChannelBit">The previous tick's per-ordinal threshold-crossing bit (edge-detection carry),
    /// copied defensively.</param>
    /// <param name="HeldChannelImage">The last admitted device-held composition image, carried so a destination
    /// authority does not manufacture a release while its replacement input stream is connecting.</param>
    /// <param name="PendingDefaultChannelPress">Per-ordinal: an argument-less <c>player.press</c> tap staged but not
    /// yet materialized into a lane timer, copied defensively.</param>
    /// <param name="PendingDefaultChannelValue">The value each pending tap in <paramref name="PendingDefaultChannelPress"/>
    /// holds, copied defensively.</param>
    /// <param name="MotionRecency">The body-motion program's own Recently-gate clocks, copied defensively.</param>
    /// <param name="PlanarRampRemainder">The response table's ramp accumulator's own signed remainder.</param>
    /// <param name="VehicleLongRemainder">The vehicle arm's longitudinal convergence accumulator's own remainder.</param>
    /// <param name="VehicleLatRemainder">The vehicle arm's lateral convergence accumulator's own remainder.</param>
    /// <param name="VehicleResidualRemainder">The vehicle arm's residual convergence accumulator's own remainder.</param>
    /// <param name="SwimThrustRampRemainder">The swim arm's thrust convergence accumulator's own remainder.</param>
    /// <param name="OverlayRemainderX">The dash overlay accumulator's X-axis remainder.</param>
    /// <param name="OverlayRemainderY">The dash overlay accumulator's Y-axis remainder.</param>
    /// <param name="OverlayRemainderZ">The dash overlay accumulator's Z-axis remainder.</param>
    /// <param name="LaneLatch">Per-lane action-runtime OnFact edge latch, copied defensively.</param>
    /// <param name="LaneFactHeld">Per-lane action-runtime previous-evaluation fact-held bits, copied defensively.</param>
    /// <param name="LaneRecency">Per-lane action-runtime Recently-gate clocks (<see langword="null"/> for a lane with
    /// no Recently predicates), copied defensively.</param>
    /// <param name="ActionStateValues">The kit's named action-state register file's live counter values, copied
    /// defensively, parallel to the kit's compiled definitions.</param>
    /// <param name="ActionStateTimers">The register file's live timer values, copied defensively.</param>
    /// <param name="ActionStateDirty">Per-slot: whether a durable write is staged but not yet drained, copied
    /// defensively.</param>
    /// <param name="ActionStateDirtyKind">The staged write's kind, parallel to <paramref name="ActionStateDirty"/>.</param>
    /// <param name="ActionStateDirtyOperand">The staged write's operand (meaningful only for an Add), parallel to
    /// <paramref name="ActionStateDirty"/>.</param>
    /// <param name="DurableInputPresent">Per-slot: whether an incoming durable value is staged for
    /// <paramref name="DurableInputTick"/> but not yet applied, copied defensively.</param>
    /// <param name="DurableInputValues">The staged durable values, parallel to <paramref name="DurableInputPresent"/>.</param>
    /// <param name="DurableInputTimers">The staged durable timer ticks, parallel to <paramref name="DurableInputPresent"/>.</param>
    /// <param name="DurableInputWriters">The staged durable writer ids, parallel to <paramref name="DurableInputPresent"/>.</param>
    /// <param name="DurableInputTick">The simulation tick the staged durable input targets.</param>
    /// <param name="TapeIntents">The scripted tape's live segments, in FIFO (dequeue) order.</param>
    /// <param name="TapeRemainingTicks">Each segment's own remaining ticks, parallel to <paramref name="TapeIntents"/>.</param>
    /// <param name="PendingContinuum">The already-evaluated adjacency segment awaiting ownership resolution, or
    /// <see langword="null"/> when this body may advance normally.</param>
    public readonly record struct TransferState(
        FixedVector3 PlanarVelocity,
        FixedQ4816 VerticalVelocity,
        FixedQuaternion Orientation,
        FixedQ4816 VehiclePitch,
        FixedVector3 OverlayVelocity,
        ulong OverlayRemainingTicks,
        ulong[] ChannelTimerTicks,
        FixedQ4816[] ChannelTimerValues,
        string BodyMotionProgramName,
        IntentSource Source,
        bool[] PreviousChannelBit,
        PlayerIntent HeldChannelImage,
        bool[] PendingDefaultChannelPress,
        FixedQ4816[] PendingDefaultChannelValue,
        ulong[] MotionRecency,
        long PlanarRampRemainder,
        long VehicleLongRemainder,
        long VehicleLatRemainder,
        long VehicleResidualRemainder,
        long SwimThrustRampRemainder,
        long OverlayRemainderX,
        long OverlayRemainderY,
        long OverlayRemainderZ,
        ulong[] LaneLatch,
        ulong[] LaneFactHeld,
        ulong[]?[] LaneRecency,
        FixedQ4816[] ActionStateValues,
        ulong[] ActionStateTimers,
        bool[] ActionStateDirty,
        WorldDocumentWriteKind[] ActionStateDirtyKind,
        FixedQ4816[] ActionStateDirtyOperand,
        bool[] DurableInputPresent,
        FixedQ4816[] DurableInputValues,
        ulong[] DurableInputTimers,
        string[] DurableInputWriters,
        ulong DurableInputTick,
        PlayerIntent[] TapeIntents,
        ulong[] TapeRemainingTicks,
        WorldContinuumTrajectory? PendingContinuum);
    /// <summary>The position-integration remainders <see cref="TransferState"/> deliberately drops (see its own
    /// remarks) because a transfer's destination frame makes them meaningless — captured here instead for a
    /// checkpoint restore, which is CONTINUOUS: the restored body keeps advancing in the identical frame it was
    /// captured in, so a dropped remainder would round the very next step's swept segment differently and the
    /// restored trajectory would diverge from the uninterrupted one by one raw unit within a few ticks.</summary>
    /// <param name="PreviousPosition">The position at the top of the most recently completed <see cref="Advance"/> —
    /// mirrors <see cref="FixedPreviousPosition"/>, carried here so a restore can set it directly.</param>
    /// <param name="PositionRemainderX">The position-rate accumulator's X-axis remainder.</param>
    /// <param name="PositionRemainderY">The position-rate accumulator's Y-axis remainder.</param>
    /// <param name="PositionRemainderZ">The position-rate accumulator's Z-axis remainder.</param>
    /// <param name="RotationRemainderX">The rotation-rate accumulator's X-axis remainder.</param>
    /// <param name="RotationRemainderY">The rotation-rate accumulator's Y-axis remainder.</param>
    /// <param name="RotationRemainderZ">The rotation-rate accumulator's Z-axis remainder.</param>
    /// <param name="VerticalVelocityRemainder">The vertical-velocity-rate accumulator's remainder.</param>
    /// <param name="Up">The latched contact-surface up normal — a solver fact ordinarily re-derived by the very next
    /// grounded <see cref="Advance"/>, carried here so a checkpoint taken and restored between the same two ticks
    /// (before any <see cref="Advance"/> re-derives it) reads identically to the uninterrupted server.</param>
    /// <param name="Grounded">The latched grounded state, for the identical reason as <paramref name="Up"/>.</param>
    /// <param name="Engaged">The screen-engagement route latch (<see cref="Engaged"/>/<see cref="EngagedIntent"/>) —
    /// a <see cref="WorldBody"/> field, not a <see cref="Puck.World.Server.WorldEngagement"/> field, so it belongs
    /// here rather than being re-derived by that subsystem's own restore.</param>
    /// <param name="EngagedIntent">The intent resolved on the most recent <see cref="Advance"/>, carried alongside
    /// <paramref name="Engaged"/>.</param>
    /// <param name="OrdinaryAdvanceAdmitted">Whether this body's most recent step advanced under the ordinary
    /// (non-continuum) path.</param>
    /// <param name="ContinuumConsumedThroughEngineTick">The latest engine-time boundary this body has already
    /// consumed from an inbound continuum trajectory, or <see langword="null"/> when none is in flight.</param>
    /// <param name="AffectingSubject">The entity index that most recently pushed this body during contact
    /// resolution, or <c>-1</c> — reset to <c>-1</c> at the tail of every <see cref="Advance"/>, so this is a
    /// one-tick image carried here purely so a checkpoint taken mid-tick-window reads identically on restore.</param>
    public readonly record struct IntegrationResidue(
        FixedVector3 PreviousPosition,
        long PositionRemainderX,
        long PositionRemainderY,
        long PositionRemainderZ,
        long RotationRemainderX,
        long RotationRemainderY,
        long RotationRemainderZ,
        long VerticalVelocityRemainder,
        FixedVector3 Up,
        bool Grounded,
        bool Engaged,
        PlayerIntent EngagedIntent,
        bool OrdinaryAdvanceAdmitted,
        ulong? ContinuumConsumedThroughEngineTick,
        int AffectingSubject
    );

    /// <summary>Captures this body's integration residue — see <see cref="IntegrationResidue"/>. Read live, right
    /// now, never cached.</summary>
    public IntegrationResidue CaptureIntegrationResidue() => new(
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
        AffectingSubject: m_affectingSubject
    );
    /// <summary>Restores a previously captured integration residue onto this body — called after
    /// <see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/> has already set position/orientation and
    /// after <see cref="ApplyTransferState"/> has already set the rest of the live state, so this call's own writes
    /// are never overwritten by an earlier restore step.</summary>
    public void ApplyIntegrationResidue(IntegrationResidue residue) {
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
    }
}

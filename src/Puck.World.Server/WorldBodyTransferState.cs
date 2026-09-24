using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The subset of a body's own dynamic state that is perceivable — the in-flight rule docs/architecture/worlds.md's
/// "In-flight state at transfer" names ("drop and re-derive what the engine can recompute; carry what the player
/// can perceive") applied to a same-process transfer's abort/restore path
/// (<see cref="Puck.World.Server.WorldPopulation.TryDetachSeatForTransfer"/>/<see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/>),
/// which otherwise discards the whole body object and reconstructs a fresh one at rest. Pose (position/yaw) is
/// captured separately by that caller; this narrows to what else a player would notice — momentum carrying
/// through the door, a dash still playing out, a charged button still held, a scripted tape still running, a
/// switched body-motion program, a cooldown mid-count — never a re-derivable fact and never an absolute tick
/// value: every ticks field here is a countdown or a remainder, decremented/consumed every <see cref="WorldBody.Advance"/>,
/// never a stamped deadline tick — the same distinction that keeps a park's <c>ParkedUntilTick</c> out of this
/// struct entirely.</summary>
/// <remarks>
/// <para>Every mutable instance field this class declares is classified between this transfer record and the
/// checkpoint-only <see cref="WorldBodyIntegrationResidue"/>/<see cref="WorldBodyTetherResidue"/> records below, so a reviewer
/// can check a table rather than re-hunt every partial declaration. Two invariants bound the transfer
/// classification either way: no park state (governed by
/// <see cref="Puck.World.Server.WorldPopulation.Entry.ParkedUntilTick"/>, never this struct — re-derived on the
/// next <c>DeactivateSeat</c>, never replayed from a snapshot), and no absolute tick (every field here is either
/// a duration/countdown or a signed remainder — see <see cref="Puck.Maths.FixedRateAccumulator"/>'s own "the
/// remainder is authoritative simulation state... a fraction, not a tick" contract, which is exactly why the
/// integration-remainder fields below are safe to carry).</para>
/// <para><b>Captured (this struct's fields, below).</b> <see cref="PlanarVelocity"/>, <see cref="VerticalVelocity"/>,
/// <see cref="Orientation"/>, <see cref="DrivePitch"/>, <see cref="OverlayVelocity"/>/<see cref="OverlayRemainingTicks"/>,
/// <see cref="ChannelTimerTicks"/>/<see cref="ChannelTimerValues"/>.
/// <see cref="BodyMotionProgramName"/> — a live <c>body.motion</c> switch away from the seat kit's own default
/// program (<see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/> always reconstructs from the kit's
/// default program; nothing else remembers a switch away from it).
/// <see cref="Source"/> — the intent-source axis (<c>body.control</c>/the peer sweep); a fresh body always
/// defaults to <c>Live</c>, so a body driven by a producer would silently snap back without this.
/// <see cref="PreviousChannelBit"/> — the previous tick's per-ordinal threshold-crossing bit; without it a
/// currently-held bound action's edge detector reads "not held last tick" on the very next Advance and can
/// spuriously re-fire a rising-edge action (a jump) the player never released.
/// <see cref="PendingDefaultChannelPress"/>/<see cref="PendingDefaultChannelValue"/> — an argument-less
/// <c>body.press</c> tap staged but not yet materialized into a lane timer (<see cref="WorldBody.MaterializeDefaultLanePresses"/>
/// only runs at the next Advance).
/// <see cref="MotionRecency"/> — the body-motion program's own Recently-gate clocks (combo/gate windows a
/// program's predicates read); <see cref="WorldBody.ResetVertical"/> zeroes these on every hard teleport by design (a
/// teleport must not carry momentum), but an abort is not an ordinary teleport from the player's perspective —
/// the same reasoning that justifies capturing <see cref="PlanarVelocity"/>/<see cref="VerticalVelocity"/>
/// on top of the same reset, extended to their integration carries.
/// <see cref="PlanarRampRemainder"/>/<see cref="DriveLongRemainder"/>/<see cref="DriveLatRemainder"/>/
/// <see cref="DriveResidualRemainder"/>/<see cref="MediumThrustRampRemainder"/>/<see cref="OverlayRemainderX"/>,Y,Z —
/// the isotropic/anisotropic/medium/overlay rate accumulators' own <see cref="Puck.Maths.FixedRateAccumulator.Remainder"/>s.
/// These are frame-independent (a rate of convergence, not a position), unlike the position/rotation/vertical
/// accumulators excluded below — see this list's own "deliberately re-derived" entry for exactly why that
/// distinction holds. An anisotropic shaping row makes the decomposed trio live; a medium hold makes the medium
/// one live.
/// <see cref="PlanarFollowerPositionRawX"/>,Y,Z/<see cref="PlanarFollowerVelocityRawX"/>,Y,Z/
/// <see cref="PlanarFollowerPreviousTarget"/>/<see cref="VerticalFollowerPositionRaw"/>/
/// <see cref="VerticalFollowerVelocityRaw"/>/<see cref="VerticalFollowerPreviousTarget"/> — a kit shaping planar
/// velocity through a <c>dynamics</c> row's own Q32 follower state, for the identical "an abort is not a
/// teleport" reason as the ramp accumulators above; live only under such a kit, zero (and inert) otherwise.
/// <see cref="LaneLatch"/>/<see cref="LaneFactHeld"/>/<see cref="LaneRecency"/> — the per-lane action runtime's
/// own OnPress pending-latch bit, OnFact previous-evaluation edge bit, and Recently-gate clocks
/// (<see cref="WorldBody.LaneActionRuntime"/>) — a buffered press awaiting its gate, an OnFact trigger's own edge memory,
/// and a double-tap window all live here.
/// <see cref="ActionState"/> — the arena slot lanes' image of the named register file (ammo counters, cooldown
/// timers) — read by <see cref="WorldBody.GateOpen"/>'s CompareState/Timer predicates, so this is genuinely
/// gameplay-affecting, not diagnostic.
/// <see cref="ActionStateDirty"/>/<see cref="ActionStateDirtyKind"/>/<see cref="ActionStateDirtyOperand"/> — a
/// durable (profile-persisted) write-back staged for <see cref="WorldBody.TakeDurableStateOutputs"/>.
/// <see cref="Puck.World.Server.WorldPopulation.CompleteStep"/> drains every body's dirty flags unconditionally at
/// the end of every tick, and a transfer's mutation-drain only ever runs between ticks, after that drain has
/// already completed for the just-finished one, so in this engine's tick architecture the triple is always
/// false/default at any point an abort could observe it (verified directly:
/// <c>TransferAbortKitWideningLawTests</c> drives a real durable write through a real tick and confirms it reads
/// drained at capture). Captured anyway, at negligible cost, as a hedge against that ordering ever changing.
/// <see cref="DurableInputPresent"/>/<see cref="DurableInputValues"/>/<see cref="DurableInputTimers"/>/
/// <see cref="DurableInputWriters"/>/<see cref="DurableInputTick"/> — an incoming durable-state write staged this
/// tick (<see cref="WorldBody.ApplyDurableInput"/>) but not yet consumed by the next Advance — the same "staged, not yet
/// materialized" class of gap as the pending channel press.
/// <see cref="TapeIntents"/>/<see cref="TapeRemainingTicks"/> — the scripted tape (<c>body.fly</c>) in FIFO
/// order, captured/restored at exact tick counts (never round-tripped through
/// <see cref="FixedTickConversion.DurationEngineTicks"/>'s own seconds conversion, which would drift the
/// restored duration from what was actually live) — the body's own future trajectory.</para>
/// <para><b>Deliberately re-derived (with reason) — never added to this struct.</b>
/// <c>m_tuning</c>/<c>m_laneBindings</c>/<c>m_channelThresholds</c>/<c>m_channelShapes</c>/
/// <c>m_roleChannels</c>/<c>m_roleOrdinals</c>/<c>m_actionStateDefinitions</c>/<c>m_collider</c>/
/// <c>m_maxSmoothError</c> — compiled kit config; <see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/>
/// reconstructs the body from the same seat kit row (<c>m_kits[m_seatKit]</c>), so these are byte-identical
/// without help. <c>m_contactField</c> — wired directly by <c>RestoreDetachedSeat</c>'s own
/// <see cref="WorldBody.SetContactField"/> call immediately after construction (the same population), always correct.
/// <c>m_mediumSurface</c> needs no wiring at all: <see cref="Puck.World.Server.WorldPopulation.SampleMediumSurfaces"/>
/// re-samples every active body's coupled cell fresh each tick, before that body's own Advance runs, so the
/// restored body reads a correct surface (or none) on its very next tick regardless of what it held before
/// detaching. <c>m_position</c>/<c>m_previousPosition</c>/
/// <c>m_yaw</c> — captured/restored outside this struct entirely, via <c>RestoreDetachedSeat</c>'s own
/// position/yaw parameters into <see cref="WorldBody.Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/> (this
/// struct's own top-level remarks already say so). <c>m_positionAccumulator</c>/
/// <c>m_rotationAccumulator</c>/<c>m_verticalVelocityAccumulator</c> — position-integration remainders tied to
/// the old position's coordinate frame; <see cref="WorldBody.CommitTeleport"/> legitimately collapses these on every hard
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
/// timeout, never a wrong positive one. <c>m_inMedium</c>/<c>m_atMediumBand</c> — the medium hold's law
/// re-derives both, purely as a function of the restored position and the freshly resampled medium surface, on
/// the very next Advance.
/// <c>m_heldChannels</c>/<c>m_channelReadHeld</c>/<c>m_channelReadComposed</c> — ordinary one-tick images; the
/// last admitted held composition image is separately named in <paramref name="HeldChannelImage"/> solely for
/// the bounded authority-handoff bridge.
/// <c>m_submittedIntent</c>/<c>m_hasSubmittedIntent</c>/<c>m_producerIntent</c>/<c>m_hasProducerIntent</c> —
/// one-tick device/producer images, consumed and reset every <see cref="WorldBody.Advance"/> by design ("a missed producer
/// tick can never leave a stale entity moving forever" — this type's own existing doc comment). <c>m_actionStateRequested</c>/
/// <c>m_actionStateLastWriter</c>/<c>m_actionStateLastReason</c> — pure audit text for <see cref="WorldBody.DescribeActionState"/>'s
/// echo; <see cref="WorldBody.GateOpen"/> reads only Values/Timers, never these, so losing them regresses a diagnostic
/// string, not gameplay — the same exclusion class as <c>m_channelReadHeld</c>. <c>m_continuity</c> — overwritten
/// unconditionally by <c>RestoreDetachedSeat</c>'s own <c>Pose()</c> call, which already writes the correct
/// value (Teleport) for a genuinely discontinuous restore. <c>m_affectingSubject</c> — reset to <c>-1</c> at the
/// tail of every <see cref="WorldBody.Advance"/>, one-tick, like <c>m_heldChannels</c>.</para>
/// <para><b>Checkpoint-only continuation.</b> <see cref="WorldBodyIntegrationResidue"/> carries the same-world integration
/// state a cross-world transfer deliberately cannot: position/rotation/gravity-axis accumulator remainders,
/// previous position, grounded/up/frame/reseat state, dynamics-follower seed latches, engagement latches, and the
/// complete <see cref="WorldBodyTetherResidue"/>. The latter includes every field <c>WorldBody.Tether.cs</c> declares
/// except the compiled tether facet, which the checkpoint's own world definition (its owning kit row) reconstructs
/// before restore. Grip points, tangent bases, tether anchors, and rope integration fractions stay out of
/// <see cref="WorldBodyTransferState"/> because they name the source authority's coordinate frame and geometry.</para>
/// <para><b>Population-owned state.</b>
/// <see cref="Puck.World.Server.WorldPopulation.Entry.Designations"/> and
/// <see cref="Puck.World.Server.WorldPopulation.Entry.ProducerState"/> are not <see cref="WorldBody"/> fields at
/// all; they live on the population's own per-seat <c>Entry</c>, entirely outside this struct's reach. They are
/// addressed instead at the
/// <see cref="Puck.World.Server.WorldPopulation.TryDetachSeatForTransfer"/>/<see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/>
/// layer directly (see those methods' own remarks) — named here so a reviewer checking this struct's own
/// completeness does not read their absence as an oversight.</para>
/// </remarks>
/// <param name="PlanarVelocity">The ramped horizontal velocity the grounded program integrates.</param>
/// <param name="VerticalVelocity">The vertical (gravity/jump) velocity.</param>
/// <param name="Orientation">The full attitude — captured directly rather than re-derived from yaw alone, so a
/// future driven seat kit's pitch/roll survives too (today's seat kits author no anisotropic shaping row, where this always
/// agrees with the yaw already carried alongside it — see <see cref="Puck.World.Server.WorldPopulation.RestoreDetachedSeat"/>'s
/// own remarks on the exact case).</param>
/// <param name="DrivePitch">The drive frame's own climb-attitude scalar (inert, always zero, for a kit authoring
/// no anisotropic shaping row — carried for the same forward-compatibility reason as <paramref name="Orientation"/>).</param>
/// <param name="OverlayVelocity">The timed impulse overlay's (the dash) world-space velocity, if one is live.</param>
/// <param name="OverlayRemainingTicks">Engine ticks remaining on the live overlay — a duration, not a deadline.</param>
/// <param name="ChannelTimerTicks">Per-ordinal remaining ticks on an in-flight timed <c>body.press</c> — a
/// duration per ordinal, copied defensively (never the live array).</param>
/// <param name="ChannelTimerValues">The value each timed press in <paramref name="ChannelTimerTicks"/> holds while
/// live, copied defensively.</param>
/// <param name="BodyMotionProgramName">The live body-motion program's own name, reapplied through the same
/// public <see cref="WorldBody.SetBodyMotionProgram(string)"/> door <c>body.motion</c> uses.</param>
/// <param name="Source">The intent-source axis (<c>body.control</c>/the peer sweep).</param>
/// <param name="PreviousChannelBit">The previous tick's per-ordinal threshold-crossing bit (edge-detection carry),
/// copied defensively.</param>
/// <param name="HeldChannelImage">The last admitted device-held composition image, carried so a destination
/// authority does not manufacture a release while its replacement input stream is connecting.</param>
/// <param name="PendingDefaultChannelPress">Per-ordinal: an argument-less <c>body.press</c> tap staged but not
/// yet materialized into a lane timer, copied defensively.</param>
/// <param name="PendingDefaultChannelValue">The value each pending tap in <paramref name="PendingDefaultChannelPress"/>
/// holds, copied defensively.</param>
/// <param name="MotionRecency">The body-motion program's own Recently-gate clocks, copied defensively.</param>
/// <param name="PlanarRampRemainder">The whole-vector shaping lane's ramp accumulator remainder.</param>
/// <param name="DriveLongRemainder">The anisotropic row's longitudinal convergence accumulator remainder.</param>
/// <param name="DriveLatRemainder">The anisotropic row's lateral convergence accumulator remainder.</param>
/// <param name="DriveResidualRemainder">The anisotropic row's residual convergence accumulator remainder.</param>
/// <param name="MediumThrustRampRemainder">The medium law's thrust convergence accumulator's own remainder.</param>
/// <param name="PlanarFollowerPositionRawX">The planar dynamics follower's X-lane Q32 position raw.</param>
/// <param name="PlanarFollowerPositionRawY">The planar dynamics follower's Y-lane Q32 position raw.</param>
/// <param name="PlanarFollowerPositionRawZ">The planar dynamics follower's Z-lane Q32 position raw.</param>
/// <param name="PlanarFollowerVelocityRawX">The planar dynamics follower's X-lane Q32 velocity raw.</param>
/// <param name="PlanarFollowerVelocityRawY">The planar dynamics follower's Y-lane Q32 velocity raw.</param>
/// <param name="PlanarFollowerVelocityRawZ">The planar dynamics follower's Z-lane Q32 velocity raw.</param>
/// <param name="PlanarFollowerPreviousTarget">The planar dynamics follower's previously-seen target, for the
/// next step's ZOH target-velocity derivative.</param>
/// <param name="VerticalFollowerPositionRaw">The medium's vertical dynamics follower's Q32 position raw.</param>
/// <param name="VerticalFollowerVelocityRaw">The medium's vertical dynamics follower's Q32 velocity raw.</param>
/// <param name="VerticalFollowerPreviousTarget">The medium's vertical dynamics follower's previously-seen
/// target.</param>
/// <param name="OverlayRemainderX">The dash overlay accumulator's X-axis remainder.</param>
/// <param name="OverlayRemainderY">The dash overlay accumulator's Y-axis remainder.</param>
/// <param name="OverlayRemainderZ">The dash overlay accumulator's Z-axis remainder.</param>
/// <param name="LaneLatch">Per-lane action-runtime OnFact edge latch, copied defensively.</param>
/// <param name="LaneFactHeld">Per-lane action-runtime previous-evaluation fact-held bits, copied defensively.</param>
/// <param name="LaneRecency">Per-lane action-runtime Recently-gate clocks (<see langword="null"/> for a lane with
/// no Recently predicates), copied defensively.</param>
/// <param name="ActionState">The arena slot lanes' image of this body's register file: one raw value per slot,
/// counter bits or timer ticks by the slot's own kind, parallel to the kit's compiled definitions.</param>
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
public readonly record struct WorldBodyTransferState(
    FixedVector3 PlanarVelocity,
    FixedQ4816 VerticalVelocity,
    FixedQuaternion Orientation,
    FixedQ4816 DrivePitch,
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
    long DriveLongRemainder,
    long DriveLatRemainder,
    long DriveResidualRemainder,
    long MediumThrustRampRemainder,
    long PlanarFollowerPositionRawX,
    long PlanarFollowerPositionRawY,
    long PlanarFollowerPositionRawZ,
    long PlanarFollowerVelocityRawX,
    long PlanarFollowerVelocityRawY,
    long PlanarFollowerVelocityRawZ,
    FixedVector3 PlanarFollowerPreviousTarget,
    long VerticalFollowerPositionRaw,
    long VerticalFollowerVelocityRaw,
    FixedQ4816 VerticalFollowerPreviousTarget,
    long OverlayRemainderX,
    long OverlayRemainderY,
    long OverlayRemainderZ,
    ulong[] LaneLatch,
    ulong[] LaneFactHeld,
    ulong[]?[] LaneRecency,
    long[] ActionState,
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

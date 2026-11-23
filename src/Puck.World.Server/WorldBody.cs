using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics;
using Puck.Physics.Fields;
using Puck.Physics.Motion;

namespace Puck.World.Server;


/// <summary>
/// One authoritative entity body: a full 6DOF pose (a free position and a <see cref="System.Numerics.Quaternion"/>
/// attitude) advanced from a single merged <see cref="PlayerIntent"/> every host-owned fixed simulation step under its
/// its compiled fixed-phase body motion program. A scripted tape of
/// timed segments (a <c>body.fly</c> command) takes precedence while a segment is live; with the
/// tape empty the per-tick submitted intent drives instead (a seat's device image or an authored producer,
/// via <see cref="SubmitIntent"/>). Replaying the same tape reproduces the same run. Every entity in the server's table
/// owns its own <see cref="WorldBody"/>; a driver (client, AI, replay, console) may only produce the intent — poses
/// flow out of <see cref="Advance"/> into the tick snapshot, never in.
/// </summary>
/// <remarks>
/// <para>
/// Authoritative pose, velocity, timers, tuning, and intent are fixed-point. Floating-point values exist only at
/// authored-data, console, and presentation boundaries and never feed back from rendering into simulation.
/// </para>
/// <para>
/// Single-threaded: every mutator runs while the server drains its queues at the step boundary, and
/// <see cref="Advance"/> runs immediately after — both on the launcher's window-pump thread, in sequence. So no lock
/// guards this state.
/// </para>
/// </remarks>
public sealed partial class WorldBody {
    // The channel-vector ceiling every per-ordinal action-track array is sized to (see ChannelLimits). A binding
    // exists only at the ordinals a kit's Actions map resolves; every other ordinal's entry stays null/zero and is
    // never touched by ProcessLaneActions, so a role ordinal is naturally always inert here.
    private const int ActionLaneCount = ChannelLimits.MaxChannels;
    private const long EngineTicksPerSecond = ((long)FixedTickConversion.TicksPerSecond);
    // The tape ring's initial slot count — the no-growth floor. It doubles on demand, never dropping a segment.
    private const int InitialTapeCapacity = 8;

    /// <summary>The engine backstop for a timed channel press, in seconds. A grant row may narrow it; a held live
    /// key/button has no timer.</summary>
    public const float MaxActionHoldSeconds = 60f;

    private bool m_atMediumBand;
    private CompiledBodyMotionProgram m_bodyMotionProgram;
    private bool m_usesLocalAttitude;
    private bool m_usesLocalTargetVelocity;
    // The construction-validated program table and the program this body executes.
    private IReadOnlyDictionary<string, CompiledBodyMotionProgram> m_bodyMotionPrograms;
    // The input images that arrive and their routing, behind their write door (InputImages reads, InputImagesEdit
    // writes). Submitted and Producer are the two one-tick images below the tape, consumed by the next Advance so a
    // missed producer tick can never leave a stale entity moving: the submitted image is the live stream (a seat's
    // device image or a remote client's submission), admitted unless the source is Idle; the producer image is the
    // server-side producer's output, used only when no submission arrived and the source names it. Held is the
    // per-tick live-held device image the client submits (only composition ordinals are meaningful there: a button
    // down until its release edge; one-tick, republished each submission). TransferHeld is the last admitted fold a
    // full body restore retains until the next input publication, neutral included; a cross-authority arrival clears
    // it and starts a new input episode. Source is the intent-source axis (Live by default; a peer takes the
    // population's stored default at activation), set by body.control and the peer sweep; see IntentSource for the
    // merge rule it selects. Engaged is the screen-engagement route latch (disengaged by default), set by
    // body.engage/disengage: while engaged the resolved intent is diverted to the bound screen's machine instead of
    // the avatar, and Advance holds the avatar idle (no pose integration). Engagement is orthogonal to the source:
    // it decides where the intent goes, the source decides what fills it. The source can name a producer, so the
    // section is a record whose holder compares by value.
    private readonly VersionedRecord<BodyInputImagesSection> m_inputImages;
    // What the last advance resolved, behind its write door (InputReadbacks reads, InputReadbacksEdit writes): the
    // intent an engaged body diverts; body.channels' post-fold read-back, which is the held overlay NextIntent
    // admitted, the result after composing it with the resolved movement tier and that result's scalar projection,
    // written on the join path and retained after the one-tick input images clear; and the relative input's
    // fractions below scalar precision. Rules and a targeted impulse read the composed and scalar images.
    private readonly Versioned<BodyInputReadbacksSection> m_inputReadbacks;

    private ref readonly BodyInputImagesSection InputImages => ref m_inputImages.Read;
    private ref BodyInputImagesSection InputImagesEdit => ref m_inputImages.Write;
    private ref readonly BodyInputReadbacksSection InputReadbacks => ref m_inputReadbacks.Read;
    private ref BodyInputReadbacksSection InputReadbacksEdit => ref m_inputReadbacks.Write;
    private FixedWorldCollider? m_collider;
    // The kit's rigid-dynamics facet, or null for a locomotion kit — see WorldBody.Rigid.cs. Non-null routes Advance
    // through the population's rigid solve instead of the grounded/free motion program entirely.
    private FixedWorldRigid? m_rigid;
    // The kit's carry facet, or null for a kit that can never pick up a rigid body — see WorldBody.Carry.cs.
    private FixedWorldCarry? m_carry;
    // The kit's tether facet, or null for a kit that carries no rope — see WorldBody.Tether.cs. Null makes
    // body.attach/body.detach/body.reel refuse by name; the runtime attach state is m_tether itself.
    private FixedWorldTether? m_tetherFacet;
    // Rigid-only state: a locomotion body's velocity lives in m_planarVelocity/m_verticalVelocity instead, and never
    // both at once for the same body.
    private FixedVector3 m_rigidVelocity;
    private FixedVector3 m_angularVelocity;
    private bool m_resting;
    private ulong m_restingHoldTicks;

    // The world contact field this body solves its swept grounded position against (null before a population assigns
    // the document-derived field) and the body's own capsule volume (null = a volumeless kit, never solved).
    /// <summary>The authored maximum downward snap, scaled with the body.</summary>
    private FixedQ4816 GroundSnap => (m_tuning.GroundSnap * m_scale);
    // The half-angle rate SteerUp's rotor and its within-budget test both want, so accumulating it directly spares a
    // per-tick halving — see WorldBody.Step's SteerUp. Kit-authored (WorldMotion.UpTurn); see FixedUpTurnRates.
    private FixedQ4816 FieldUpTurnHalfRate => m_tuning.UpTurn.Field;

    private static readonly FixedQ4816 MinFieldUpMagnitude = FixedQ4816.One;

    private FixedQ4816 ContactUpTurnHalfRate => m_tuning.UpTurn.Contact;

    private IContactField? m_contactField;
    // The body-owned frame policy compiled from the world document. The contact field remains a geometry seam and
    // never carries this integration decision, so wrapping or replacing a provider cannot silently change it.
    private WorldBodyUpPolicy m_upPolicy;

    // The population's live gravity field and this body's index into it, refreshed per Advance.
    private int m_entityIndex = -1;

    private WorldGravityField? m_gravityField;

    // The rotation carrying world +Y to the body's up, CARRIED rather than rebuilt. Reconstructing it from world +Y
    // each tick is unstable where up approaches world -Y — the underside of a planetoid — because the shortest arc's
    // axis is undefined there and flips with rounding, spinning the body on the spot. Transporting it by the tick's
    // own (tiny) change has no such point.
    private FixedQuaternion m_frame = FixedQuaternion.Identity;

    private ulong? m_continuumConsumedThroughEngineTick;

    // The last grounded Advance's standing witness for the world.contacts read-back.
    private int m_lastContactCount;
    private FixedQ4816 m_maxSmoothError;
    // world.contacts' obstruction witness — LATCHED, not a raw per-tick read (see UpdateObstructionWitness): the
    // last non-walkable push's normal, held across ticks while the body stays actively driven and hasn't moved
    // since, so a solver tick that happens not to re-register the push (fully depenetrated already, or a query
    // sitting exactly on a quantization boundary) can never flicker the witness back to "none" mid-obstruction.
    // Zero when nothing obstructs the body. Read-back only, exactly like m_lastContactCount above.
    private FixedVector3 m_obstructionWitness;
    // How many more ENGINE ticks (FixedTickConversion.TicksPerSecond = 50400/s — the stepTicks unit every Advance
    // call decrements this by, NOT the 240Hz simulation-step counter world.wait/console echoes count in; one fixed
    // simulation step is 210 of these) an un-refreshed latch survives a solver pass that reports no push at all — a
    // grace window absorbing ordinary
    // query noise near a surface (a gradient/quantization boundary the SDF field provider can land exactly on, or —
    // measured empirically driving a body into a world's boundary wall — a body settled into a wall/ground corner
    // under SmoothUnionContact blending, which can drift in and out of the walkable classification for many
    // consecutive simulation steps while genuinely never clearing) so that noise can never flicker the witness.
    // Reset to the full window every time a fresh push actually lands.
    private ulong m_obstructionWitnessGraceTicks;
    // The body position when m_obstructionWitness was last (re)latched — the reference point the "has it actually
    // moved clear" displacement check measures from.
    private FixedVector3 m_obstructionWitnessPosition;
    private bool m_ordinaryAdvanceAdmitted;
    private ulong m_overlayRemaining;
    // The timed impulse overlay (the dash): a world-space velocity integrated through its own accumulator on top of
    // the body's compiled motion for a bounded tick budget — integration itself is untouched. Cleared by hard teleports.
    private FixedVector3 m_overlayVelocity;
    private WorldContinuumTrajectory? m_pendingContinuum;
    // The shaping-row planar velocity — the horizontal velocity the motion program integrates. With an instant
    // whole-vector row it equals the commanded target every tick; with finite rates it converges on that target through
    // m_planarRampAccumulator. SURVIVES a live
    // kit recompile (a retune must not jerk the crowd) but is dropped alongside the vertical velocity in ResetVertical,
    // so only a hard teleport that resets vertical state clears it (Warp/Pose/Reconcile) — Face keeps it (resetVertical:
    // false, no momentum lost across a heading snap).
    private FixedVector3 m_planarVelocity;
    // The planar dynamics follower's own Q32 state — meaningful only under a kit whose shaping table names a
    // dynamics row (m_tuning.HasDynamics); read/written exclusively by WorldBody.Dynamics.cs. Its
    // Position lane tracks m_planarVelocity: StepPlanarFollower re-seeds it (keeping the velocity raw) whenever a
    // contact write-back, an up-axis transport, or a continuum arrival moved m_planarVelocity out from under it, and
    // the follower's own raw output is what a per-tick move-speed clamp on m_planarVelocity later pulls back toward.
    // SURVIVES a live kit recompile, alongside m_planarVelocity; reset in ResetVertical, alongside it.
    private SecondOrderState3 m_planarFollower;
    // The target StepPlanarFollower last saw, held so it can derive the ZOH target velocity (target − previous)
    // × the world's simulation rate — the second-order system's r-driven initial-response term. Meaningless until
    // m_planarFollowerSeeded is set: StepPlanarFollower's FIRST step after a reset writes it without differencing,
    // so a teleport can never manufacture a target-velocity impulse out of the zeroed previous target.
    private FixedVector3 m_planarPreviousTarget;
    private bool m_planarFollowerSeeded;
    // The medium law's vertical dynamics follower's own Q32 state and previous target — the one-dimensional
    // counterparts of m_planarFollower/m_planarPreviousTarget, stepped by ApplyHold's medium law under the SAME
    // compiled FixedMotionDynamics.Planar propagator the kit's planar lanes step. m_verticalFollowerSeeded is the
    // vertical lane's counterpart to m_planarFollowerSeeded.
    private SecondOrderState m_verticalFollower;
    private FixedQ4816 m_verticalPreviousTarget;
    private bool m_verticalFollowerSeeded;
    // Where this body belongs — the position its activation placed it at. Producers measure their own steering
    // against it (see ProduceSteeringIntent's roam shape), so it is simulation state: it decides trajectories. A
    // teleport never moves it, which is what separates "where the body is" from "where the body is from".
    private FixedVector3 m_home;
    // The avatar's simulation position. See Position.
    private FixedVector3 m_position;
    // The position captured at the top of the most recent Advance — the swept portal-crossing scan's segment start
    // (see FixedPreviousPosition). A hard teleport resets it to the landing position in the SAME CommitTeleport call
    // that resets m_verticalVelocity/m_planarVelocity for the identical reason: without the reset a warped body would
    // leave a ghost segment behind, sweeping a portal scan through space it never actually travelled.
    private FixedVector3 m_previousPosition;
    private RoleChannelOrdinals m_roleOrdinals;
    private bool m_inMedium;

    private int m_tapeCount;
    private int m_tapeHead;

    // The compiled locomotion feel. A seated player reads its live
    // profile's move/turn speed instead (that is what makes identity.motion real-time); a profileless stand-in falls back
    // to the tuning's speeds. Swapped in place by
    // RecompileKit when the body's kit row is retuned live (pose survives; only the compiled feel changes).
    private FixedMotionTuning m_tuning;
    // The drive frame's authoritative pitch scalar (radians) — the flying variant's climb attitude, integrated
    // alongside m_yaw and clamped so the facing can never flip past vertical. Inert (held zero) while the motion row's
    // pitchRate is zero. Levelled by Face, written by Pose, like m_yaw.
    private FixedQ4816 m_drivePitch;
    // The vertical channel — the axis the bound vertical effects write. Under the grounded program gravity integrates it
    // and m_grounded gates/refreshes the composition facts; under the free program a written impulse bleeds to zero at
    // the tuning's rise gravity (no fall phase). Reset to a clean grounded rest (in ResetVertical) only by a hard
    // teleport that resets vertical state — Warp/Pose/Reconcile — but NOT by Face (resetVertical: false, the jump arc
    // keeps running), and by SetBodyMotionProgram only when the new program integrates vertical gravity.
    private FixedQ4816 m_verticalVelocity;
    // Sampled fresh every tick, before this body's own Advance, from the population's field lattice at this body's
    // coupled cell — never captured/restored (see WorldBody.Transfer.cs's own remarks): a pure function of this
    // body's position and the live lattice, re-derived identically the very next tick regardless of any teleport.
    // A point and the lattice's own frame normal (world +Y — the lattice carries no rotation of its own); the medium
    // hold's law projects displacement along the BODY's own resolved gravity-up, not this normal, so a tilted gravity
    // area's medium still measures depth correctly.
    private FixedFieldSurface? m_mediumSurface;

    private FixedQ4816 m_travelMultiplier = FixedQ4816.One;

    /// <summary>Installs the current region resistance, derived afresh before each motion step.</summary>
    public void SetTravelResistance(FixedQ4816 resistance) => m_travelMultiplier = (FixedQ4816.One / (FixedQ4816.One + FixedQ4816.Max(x: FixedQ4816.Zero, y: resistance)));

    // The grounded program's authoritative heading scalar (radians): integrated from the Turn rate, with m_orientation
    // derived from it each step (a pure yaw rotation). Under free it is inert (orientation is authoritative and Yaw is
    // read back out of it).
    private FixedQ4816 m_yaw;

    // The anisotropic shaping row's longitudinal/lateral/residual convergence remainders — one accumulator per decomposed
    // channel so each rate's sub-tick tail carries independently (the body-frame twin of m_planarRampAccumulator).
    private FixedRateAccumulator m_driveLongAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedRateAccumulator m_driveLatAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedRateAccumulator m_driveResidualAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private static readonly FixedQ4816 MaxActionHoldSecondsFixed = FixedQ4816.FromInteger(value: 60L);
    private static readonly FixedQ4816 NegativeOne = -FixedQ4816.One;
    // Above the yaw round-trip error of FromAxisAngle -> FixedQuaternion.Yaw, below any facing a snap can leave (radians).
    private static readonly FixedQ4816 FacingAdoptEpsilon = FixedQ4816.FromDouble(value: 0.001);

    // Kit-authored (WorldTurn.MaxPitch); see FixedTurn.
    private FixedQ4816 MaxDrivePitch => m_tuning.Turn.MaxPitch;

    // The scripted tape: a FIFO of timed segments in a growable ring buffer of structs. While one is live it overrides
    // the keys; a segment is consumed one host tick at a time (one segment drives each Advance) and
    // dropped once its time runs out. An enqueue writes a struct into a pre-owned slot (no per-segment heap object) and
    // grows the ring by doubling (never dropping) when the live count would exceed capacity, so steady-state enqueue+drain
    // allocates nothing. m_tapeHead is the front index, m_tapeCount the live length; the tail wraps at m_tape.Length.
    private TapeSegment[] m_tape = new TapeSegment[InitialTapeCapacity];
    // The action track's per-channel state and the lanes' latches, behind their write door: Action reads, ActionEdit
    // moves the section's version and notes the body's slot, and nothing else reaches the section. A write-side
    // reference is used within one statement or handed to one call (DOOR005).
    private readonly Versioned<BodyActionSection> m_action;

    private ref readonly BodyActionSection Action => ref m_action.Read;
    private ref BodyActionSection ActionEdit => ref m_action.Write;
    // Whether the last contact solve found support. Only a body with a collider runs one, so this is false for a
    // colliderless body at every write: construction, teleport (ResetVertical) and kit recompile each take CanBeGrounded.
    private bool m_grounded;
    private FixedRateAccumulator m_planarRampAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    // The shaping table's shared recency clocks — one per Recently gate across the whole table (allocated to match the
    // compiled tuning's RecencySlots), refreshed while the fact holds and decaying otherwise. Reset by a teleport and a
    // recompile (the clocks are bound to the OLD table shape).
    private ulong[] m_motionRecency = [];
    // The body's up axis — the direction its gravity opposes, its planar move plane is perpendicular to, and its attitude
    // stands against. Ambient follows opposed solved gravity or the contact field's fallback; SurfaceFollowing also
    // admits a measured support normal while grounded. Held from the previous step only when the active policy's
    // ambient query is degenerate.
    private FixedVector3 m_up = FixedVector3.UnitY;

    // Set by a teleport: the next up resolve SNAPS to the field instead of steering toward it, because the body did
    // not turn — it was relocated. See WorldBody.Lifecycle's Pose and WorldBody.Step's ResolveUp.
    private bool m_upNeedsReseat;

    // Kit-authored (WorldMotion.Obstruction); see FixedObstructionLatch.
    private FixedQ4816 ObstructionLatchDisplacementSquared => m_tuning.Obstruction.DisplacementSquared;
    private FixedQ4816 ObstructionLatchIdleThreshold => m_tuning.Obstruction.IdleThreshold;
    private ulong ObstructionLatchGraceTicks => m_tuning.Obstruction.GraceTicks;

    // The per-channel action runtime: the compiled binding (null = unbound), its press latch and fact-edge memory (in
    // the Action section), and one recency clock per Recently-predicate instance. Named counters and timers live in the kit-wide action-state register file below.
    // A role ordinal never has a binding, so it is naturally inert here.
    private readonly CompiledActionSpec?[] m_laneBindings = new CompiledActionSpec?[ActionLaneCount];
    // The lanes' Recently-gate clocks behind their write door, every lane's in lane order: a lane's clocks start at
    // m_laneRecencyStart[lane] and number its binding's recency facts. The layout is configuration, laid whenever the
    // bindings are set (LayoutLaneRecency).
    private readonly VersionedArray<ulong> m_laneRecency;
    private readonly int[] m_laneRecencyStart = new int[(ActionLaneCount + 1)];
    private ulong[] m_laneRecencyScratch = [];
    private CompiledActionStateSlot[] m_actionStateDefinitions = [];

    // Named counters and timers are stored in the arena's slot lanes, addressed by this body's own entity index —
    // the body holds the address, never the values.
    private WorldActionStateLane? m_stateLane;

    private int m_stateOrdinal = -1;
    private long[] m_actionStateRequested = [];
    private string[] m_actionStateLastWriter = [];
    private string[] m_actionStateLastReason = [];
    // The durable register writes staged for the profile, one row per register, behind their write door.
    private readonly VersionedArray<WorldRegisterWriteImage> m_registerWrites;
    // The incoming durable write staged for a tick, one row per register, behind its write door. A row names its
    // writer, so the table is immutable and a write replaces it.
    private readonly VersionedRecord<BodyDurableInputSection> m_durableInput;
    private int m_affectingSubject = -1;
    // The world's declared binary crossing threshold per ordinal, bound or not — resolved once from the world's
    // channel table at construction/recompile; the engage-channel probe and the previous-bit image read unbound
    // ordinals too.
    private readonly FixedQ4816[] m_channelThresholds = new FixedQ4816[ActionLaneCount];


    // The declared shape per ordinal — EVERY ordinal, not just bound ones: the
    // held-image overlay below composes a channel whether or not a kit binds an action to it. Resolved once from the
    // world's channel table at construction/recompile; an unpopulated slot defaults to Bipolar (ChannelShape's zero
    // value), the same fallback WorldServer uses for an undeclared ordinal.
    private readonly ChannelShape[] m_channelShapes = new ChannelShape[ActionLaneCount];
    private readonly bool[] m_roleChannels = new bool[ActionLaneCount];
    private FixedVector3RateAccumulator m_overlayAccumulator = new(ticksPerSecond: EngineTicksPerSecond);

    // Sub-Q48.16 integration state. Per-second velocity/rate numerators are divided by the exact engine time base;
    // these signed remainders carry the discarded tails into later steps instead of losing them every fixed update.
    // They are authoritative state: hard pose writes reset the affected channels. Each is bound to the engine time base
    // once here — a remainder is a numerator over that denominator, so the denominator is accumulator identity.
    private FixedVector3RateAccumulator m_positionAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedVector3RateAccumulator m_rotationAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedRateAccumulator m_contactUpTurnAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedRateAccumulator m_upTurnAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedRateAccumulator m_verticalVelocityAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    // The canonical orientation — the full 6DOF attitude the renderer, the camera rigs, and body.where all read. Under
    // grounded it mirrors m_yaw (pitch = roll = 0); under free it is the integrated body-frame attitude and m_yaw is
    // ignored. The model constrains how it is written, never its shape.
    private FixedQuaternion m_orientation = FixedQuaternion.Identity;
    // How this tick's pose change should be presented — the snapshot's per-entity continuity hint. Hard teleports
    // (Warp/Face/Pose/SetBodyMotionProgram, and an over-ceiling Reconcile) write Teleport; a smoothed Reconcile writes Correction.
    // Last write wins within a tick; TakeContinuity consumes it at snapshot emit.
    private EntityContinuity m_continuity = EntityContinuity.Continuous;
    // The medium law's thrust convergence carry: ONE ramp accumulator for the whole convergence, the same
    // "remainder binds to the tick base" shape as m_planarRampAccumulator — so alternating engage/release rates
    // through it stays exact, with no separate accumulator per stage.
    private FixedRateAccumulator m_mediumThrustRampAccumulator = new(ticksPerSecond: EngineTicksPerSecond);

    /// <summary>Initializes a new instance of the <see cref="WorldBody"/> class under a compiled locomotion tuning,
    /// its kit's per-channel action bindings, and its kit's body motion program. A <see langword="null"/> binding
    /// leaves that ordinal inert.</summary>
    /// <param name="tuning">The compiled locomotion tuning to integrate under (<see cref="FixedWorldKit.Tuning"/>).</param>
    /// <param name="program">The kit's compiled body motion program.</param>
    /// <param name="programs">The world's compiled body motion program table.</param>
    /// <param name="actions">The kit's compiled per-ordinal action bindings (<see cref="ChannelLimits.MaxChannels"/> slots).</param>
    /// <param name="actionThresholds">The kit's per-ordinal binary crossing thresholds, parallel to <paramref name="actions"/>.</param>
    /// <param name="actionShapes">The world's per-ordinal declared channel shapes (every ordinal, not just bound ones).</param>
    /// <param name="roleMask">The world's compiled per-ordinal role predicate.</param>
    /// <param name="roleOrdinals">The kit's resolved engine motion role ordinals.</param>
    /// <param name="actionState">The kit's compiled named action-state register file.</param>
    /// <param name="collider">The kit's compiled body volume, or <see langword="null"/> for a volumeless kit.</param>
    /// <param name="maxSmoothError">The compiled world-distance correction smoothing threshold.</param>
    /// <param name="holds">The kit's compiled ordered hold list (<see cref="FixedWorldKit.Holds"/>), or
    /// <see langword="null"/> for a kit authoring none.</param>
    /// <param name="rigid">The kit's compiled rigid-dynamics facet (<see cref="FixedWorldKit.Rigid"/>), or
    /// <see langword="null"/> for a locomotion kit.</param>
    /// <param name="carry">The kit's compiled carry facet (<see cref="FixedWorldKit.Carry"/>), or
    /// <see langword="null"/> for a kit that can never pick up a rigid body.</param>
    /// <param name="channels">The compiled typed input table.</param>
    /// <param name="tether">The kit's compiled tether facet (<see cref="FixedWorldKit.Tether"/>), or
    /// <see langword="null"/> for a kit that carries no rope.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="programs"/> is <see langword="null"/>.</exception>
    public WorldBody(FixedMotionTuning tuning, CompiledBodyMotionProgram program, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, FixedQ4816 maxSmoothError, CompiledActionSpec?[]? actions = null, FixedQ4816[]? actionThresholds = null, ChannelShape[]? actionShapes = null, bool[]? roleMask = null, RoleChannelOrdinals roleOrdinals = default, CompiledActionStateSlot[]? actionState = null, FixedWorldCollider? collider = null, FixedBodyHold[]? holds = null, FixedWorldRigid? rigid = null, FixedWorldCarry? carry = null, FixedWorldTether? tether = null, WorldChannelTable? channels = null) {
        // One binding for every holder of this body: a write to any of them notes the body's slot once.
        var slot = new SlotBinding();

        m_action = new Versioned<BodyActionSection>(binding: slot);
        m_inputImages = new VersionedRecord<BodyInputImagesSection>(binding: slot);
        m_inputReadbacks = new Versioned<BodyInputReadbacksSection>(binding: slot);
        m_laneRecency = new VersionedArray<ulong>(binding: slot);
        m_registerWrites = new VersionedArray<WorldRegisterWriteImage>(binding: slot);
        m_durableInput = new VersionedRecord<BodyDurableInputSection>(binding: slot, value: BodyDurableInputSection.Empty(registers: 0));
        m_scriptActors = new VersionedRecord<BodyScriptActorsSection>(binding: slot, value: BodyScriptActorsSection.Empty);
        SetTuning(
            holds: holds,
            tuning: tuning
        );
        m_bodyMotionProgram = (program ?? throw new ArgumentNullException(paramName: nameof(program)));
        CacheProgramTraits(program: m_bodyMotionProgram);
        m_bodyMotionPrograms = (programs ?? throw new ArgumentNullException(paramName: nameof(programs)));
        SetChannelTable(channels: channels);
        CopyChannelBindings(
            actionShapes: actionShapes,
            actionThresholds: actionThresholds,
            actions: actions,
            roleMask: roleMask
        );
        m_roleOrdinals = roleOrdinals;
        CompileActionState(state: actionState);
        m_collider = collider;
        m_grounded = CanBeGrounded;
        RescaleColliderVolumes();
        m_rigid = rigid;
        m_carry = carry;
        m_tetherFacet = tether;
        MarkKitLocalStale();
        m_maxSmoothError = maxSmoothError;

        LayoutLaneRecency(sources: default);

        if (m_tuning.RecencySlots > 0) {
            m_motionRecency = new ulong[m_tuning.RecencySlots];
        }

    }
}

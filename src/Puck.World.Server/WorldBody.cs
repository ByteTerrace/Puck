using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

internal readonly record struct BodySensorTarget(int Index, FixedVector3 Position, FixedQ4816 DistanceSquared) {
    public static BodySensorTarget None => new(Index: -1, Position: default, DistanceSquared: FixedQ4816.MaxValue);
    public bool Exists => (Index >= 0);
}

internal readonly record struct BodyProducerSensors(BodySensorTarget Candidate, BodySensorTarget CurrentTarget);

internal readonly record struct BodyEffectTargets(int ProducerTarget, int AffectingSubject) {
    public int Resolve(ActionTarget target) => target switch {
        ActionTarget.ProducerTarget => ProducerTarget,
        ActionTarget.AffectingSubject => AffectingSubject,
        _ => -1,
    };
}

internal readonly record struct BodyEffectOutput(int SourceIndex, int TargetIndex, CompiledBodyInstruction Instruction);

/// <summary>One <see cref="BodyMotionOp.Generate"/> firing staged during a body's advance — collected across the whole
/// population advance pass (the same staged-output shape <see cref="WorldDesignation"/> already uses) and enqueued
/// through the ordinary mutation pipeline afterwards by <c>WorldServer.Step</c>. It carries no source entity index:
/// the site is a world-global state row, never body-relative, and the acting principal is
/// <see cref="Protocol.WorldPrincipal.World"/> for every firing regardless of which body fired it — the effect is the
/// world's authored program acting, not the seat (see that principal's own remarks).</summary>
/// <param name="Row">The draw site's row name.</param>
public readonly record struct WorldGeneratorInvocation(string Row);

/// <summary>The outcome of a <see cref="WorldBody.Stop"/> panic verb: how many device-held channels it released and
/// how many in-flight timed presses (<c>player.press</c> holds) it cancelled — <c>player.stop</c>'s synchronous
/// read-back, so its echo reports what actually happened instead of a fixed template.</summary>
/// <param name="ReleasedHeldChannels">Count of non-zero ordinals in the dropped device-held image.</param>
/// <param name="ClearedTimedPresses">Count of in-flight timed presses (role and composition ordinals alike)
/// cancelled.</param>
public readonly record struct StopOutcome(int ReleasedHeldChannels, int ClearedTimedPresses);

/// <summary>Which cap, if any, decided a timed <c>player.press</c>'s effective hold — <see cref="WorldBody.PressChannel(int, FixedQ4816, float, FixedQ4816)"/>'s
/// synchronous read-back discriminator, so the console echo can name the true binder instead of guessing from the
/// effective value's magnitude.</summary>
public enum PressHoldCapKind : byte {
    /// <summary>The effective hold equals the request — nothing capped it.</summary>
    None,

    /// <summary>The request was non-positive (or NaN) and was ignored outright — no timed press was created, and
    /// any in-flight hold on the ordinal was left untouched.</summary>
    Ignored,

    /// <summary>The deciding Drive grant row's <c>hold:</c> ceiling (<see cref="WorldGrant.DefaultHoldSeconds"/>
    /// absent an authored row) is what bound the request — it is authored strictly below the engine backstop, so it
    /// is doing real narrowing of its own.</summary>
    GrantBudget,

    /// <summary>The <see cref="WorldBody.MaxActionHoldSeconds"/> engine backstop is what bound the request — the
    /// grant permits up to the backstop with no narrowing of its own, and the raw request still exceeded it.</summary>
    EngineCeiling,
}

/// <summary>The outcome of a timed <c>player.press</c> — the effective hold after both caps apply, and which one
/// (if either) actually bound it. <c>player.press</c>'s synchronous read-back, so its echo can report the true
/// result instead of echoing the requested duration as though it were honored.</summary>
/// <param name="EffectiveHoldSeconds">The hold actually applied, in sim seconds.</param>
/// <param name="CapKind">Which cap decided it.</param>
public readonly record struct PressOutcome(FixedQ4816 EffectiveHoldSeconds, PressHoldCapKind CapKind);

internal struct BodyProducerState {
    public int AcquiredTarget;
    public FixedQ4816 ActivityPhase;
    public FixedQ4816 ActivityRate;
    public FixedQ4816 Phase;
    public FixedQ4816 PreferredAltitude;
    public FixedQ4816 WeaveFrequency;
}

/// <summary>
/// One authoritative entity body: a full 6DOF pose (a free position and a <see cref="System.Numerics.Quaternion"/>
/// attitude) advanced from a single merged <see cref="PlayerIntent"/> every host-owned fixed simulation step under its
/// its compiled fixed-phase body motion program. A scripted tape of
/// timed segments (a <c>player.fly</c> command) takes precedence while a segment is live; with the
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
public sealed class WorldBody {
    private const long EngineTicksPerSecond = (long)EngineTicks.PerSecond;

    // Which arm's compiled tuning ResolveMoveSpeed (and every other per-arm resolve) dispatches on — set by
    // SetTuning alongside the compiled tuning itself, never re-derived. A new model arm (swim) is a localized
    // addition: a new member here, its SetTuning case, and its ResolveMoveSpeed case — the same localized-addition
    // rule SetTuning's own remarks already state. Swim compiles its speed into the SAME shared FixedMotionTuning
    // slots grounded reads, so its ResolveMoveSpeed case rides grounded's case rather than forking one.
    private enum CompiledMotionArm : byte { Grounded, Vehicle, Swim }

    private CompiledMotionArm m_motionArm;
    // The compiled locomotion feel. A seated player reads its live
    // profile's move/turn speed instead (that is what makes identity.motion real-time); a profileless stand-in falls back
    // to the tuning's speeds. Swapped in place by
    // RecompileKit when the body's kit row is retuned live (pose survives; only the compiled feel changes).
    private FixedMotionTuning m_tuning;
    // The vehicle arm's compiled tuning — meaningful only under a vehicle-model kit (the facet gate refuses a
    // program selecting the vehicle ops against any other arm, so the ops never read the zero default).
    private FixedVehicleTuning m_vehicleTuning;
    // The vehicle frame's authoritative pitch scalar (radians) — the flying variant's climb attitude, integrated
    // alongside m_yaw and clamped so the facing can never flip past vertical. Inert (held zero) while PitchRate is
    // zero. Levelled by Face, written by Pose, like m_yaw.
    private FixedQ4816 m_vehiclePitch;
    // The vehicle arm's held drift channel: -1 (cannot drift) unless the kit's model names one that resolves —
    // the same resolved-outside/consumed-as-ordinal pattern as m_sprintChannelOrdinal.
    private int m_driftChannelOrdinal = -1;
    // The vehicle arm's longitudinal/lateral/residual convergence remainders — one accumulator per decomposed
    // channel so each rate's sub-tick tail carries independently (the body-frame twin of m_planarRampAccumulator).
    private FixedRateAccumulator m_vehicleLongAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedRateAccumulator m_vehicleLatAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedRateAccumulator m_vehicleResidualAccumulator = new(ticksPerSecond: EngineTicksPerSecond);

    /// <summary>The engine backstop for a timed channel press, in seconds. A grant row may narrow it; a held live
    /// key/button has no timer.</summary>
    public const float MaxActionHoldSeconds = 60f;

    private static readonly FixedQ4816 s_maxActionHoldSeconds = FixedQ4816.FromInteger(value: 60L);
    private static readonly FixedQ4816 s_negativeOne = -FixedQ4816.One;
    private static readonly FixedQ4816 s_pi = FixedQ4816.FromDouble(value: Math.PI);
    // The vehicle pitch clamp (~69°): the flying variant's facing can climb and dive steeply but never flip past
    // vertical, which would invert the yaw frame mid-flight.
    private static readonly FixedQ4816 s_maxVehiclePitch = FixedQ4816.FromDouble(value: 1.2);
    private static readonly FixedQ4816 s_twoPi = FixedQ4816.FromDouble(value: (2.0 * Math.PI));
    private static readonly FixedVector3 s_unitX = new(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_unitY = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_unitZ = new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);
    // The channel-vector ceiling every per-ordinal action-track array is sized to (see ChannelLimits). A binding
    // exists only at the ordinals a kit's Actions map resolves; every other ordinal's entry stays null/zero and is
    // never touched by ProcessLaneActions, so a role ordinal is naturally always inert here.
    private const int ActionLaneCount = ChannelLimits.MaxChannels;
    // The tape ring's initial slot count — the no-growth floor. It doubles on demand, never dropping a segment.
    private const int InitialTapeCapacity = 8;


    // The scripted tape: a FIFO of timed segments in a growable ring buffer of structs. While one is live it overrides
    // the keys; a segment is consumed one host tick at a time (one segment drives each Advance) and
    // dropped once its time runs out. An enqueue writes a struct into a pre-owned slot (no per-segment heap object) and
    // grows the ring by doubling (never dropping) when the live count would exceed capacity, so steady-state enqueue+drain
    // allocates nothing. m_tapeHead is the front index, m_tapeCount the live length; the tail wraps at m_tape.Length.
    private TapeSegment[] m_tape = new TapeSegment[InitialTapeCapacity];
    private int m_tapeHead;
    private int m_tapeCount;
    // The two one-tick intent images below the tape, both no-allocation and consumed by the next Advance so a missed
    // producer tick can never leave a stale entity moving forever. The submitted image is the live stream (a seat's
    // device image or a remote client's submission), admitted unless the source is Idle; the producer image is the
    // server-side producer's output, used only when no submission arrived and the source names it.
    private PlayerIntent m_submittedIntent;
    private bool m_hasSubmittedIntent;
    private PlayerIntent m_producerIntent;
    private bool m_hasProducerIntent;

    // The action track — the channel-generic buttons, independent of the movement tape/sticks. Peer producers of the
    // same ordinals, merged every sub-step: m_heldChannels is the per-tick live-held device image the client submits
    // (only composition ordinals are meaningful there — a button down until its release edge; one-tick, republished
    // each submission), m_pendingDefaultChannelPress/m_pendingDefaultChannelValue hold argument-less taps until Advance
    // can derive their duration from its host step, and m_channelTimers/m_channelTimerValues are materialized timed
    // presses (player.press, reaching ANY ordinal including movement roles) that read held until their per-ordinal
    // auto-release timer drains. m_previousChannelBit is the previous sub-step's threshold-crossing bit per ordinal —
    // the model reads it to detect a rising (fire) and release (cut) edge, generalizing the old ActionLanes OR/XOR.
    private PlayerIntent m_heldChannels;
    // A committed authority handoff can precede the new input stream's first publication by one or more destination
    // ticks. Preserve the source writer's last admitted composition image through that gap; SetHeldChannels is the
    // single replacement door and clears this bridge on the first genuine destination input, including neutral.
    private PlayerIntent m_transferHeldChannels;
    private bool m_hasTransferHeldChannels;
    // player.channels' post-fold read-back: the held overlay NextIntent actually admitted and the result after composing
    // it with the resolved movement tier. Written on that existing join path, retained after the one-tick input images
    // clear, and never read by simulation — diagnostic only, with no allocation and no feedback into either fold.
    private PlayerIntent m_channelReadHeld;
    private PlayerIntent m_channelReadComposed;
    private readonly bool[] m_pendingDefaultChannelPress = new bool[ActionLaneCount];
    private readonly FixedQ4816[] m_pendingDefaultChannelValue = new FixedQ4816[ActionLaneCount];
    private readonly bool[] m_previousChannelBit = new bool[ActionLaneCount];
    private readonly ulong[] m_laneTimers = new ulong[ActionLaneCount];
    private readonly FixedQ4816[] m_channelTimerValues = new FixedQ4816[ActionLaneCount];

    // The vertical channel — the axis the bound vertical effects write. Under the grounded model gravity integrates it
    // and m_grounded gates/refreshes the composition facts; under the free model a written impulse bleeds to zero at
    // the tuning's rise gravity (no fall phase). Reset to a clean grounded rest (in ResetVertical) only by a hard
    // teleport that resets vertical state — Warp/Pose/Reconcile — but NOT by Face (resetVertical: false, the jump arc
    // keeps running), and by SetBodyMotionProgram only when the new program integrates vertical gravity.
    private FixedQ4816 m_verticalVelocity;
    private bool m_grounded = true;

    // The response-shaped planar velocity — the ramped horizontal velocity the grounded model integrates. With an empty
    // response table it equals the commanded target every tick (today's instant snap, byte-identical); with a table it
    // converges on the target at the matching row's engage/release rate through m_planarRampAccumulator. SURVIVES a live
    // kit recompile (a retune must not jerk the crowd) but is dropped alongside the vertical velocity in ResetVertical,
    // so only a hard teleport that resets vertical state clears it (Warp/Pose/Reconcile) — Face keeps it (resetVertical:
    // false, no momentum lost across a heading snap).
    private FixedVector3 m_planarVelocity;
    private FixedRateAccumulator m_planarRampAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    // The response table's shared recency clocks — one per Recently gate across the whole table (allocated to match the
    // compiled tuning's RecencySlots), refreshed while the fact holds and decaying otherwise. Reset by a teleport and a
    // recompile (the clocks are bound to the OLD table shape).
    private ulong[] m_motionRecency = [];
    // The world contact field this body solves its swept grounded position against (null before a population assigns
    // the document-derived field) and the body's own capsule volume (null = a volumeless kit, never solved).
    private IContactField? m_contactField;
    private FixedWorldCollider? m_collider;
    private FixedQ4816 m_maxSmoothError;
    // The body's up axis — the direction its gravity opposes, its planar move plane is perpendicular to, and its attitude
    // stands against. Constant +Y under the analytic provider; the FIELD provider derives it from the surface gradient
    // each grounded step (arbitrary-up /
    // planetoid walking as a data choice), HELD from the previous step when a query is degenerate.
    private FixedVector3 m_up = s_unitY;
    // The last grounded Advance's standing witness for the world.contacts read-back.
    private int m_lastContactCount;
    // world.contacts' obstruction witness — LATCHED, not a raw per-tick read (see UpdateObstructionWitness): the
    // last non-walkable push's normal, held across ticks while the body stays actively driven and hasn't moved
    // since, so a solver tick that happens not to re-register the push (fully depenetrated already, or a query
    // sitting exactly on a quantization boundary) can never flicker the witness back to "none" mid-obstruction.
    // Zero when nothing obstructs the body. Read-back only, exactly like m_lastContactCount above.
    private FixedVector3 m_obstructionWitness;
    // The body position when m_obstructionWitness was last (re)latched — the reference point the "has it actually
    // moved clear" displacement check measures from.
    private FixedVector3 m_obstructionWitnessPosition;
    // How many more ENGINE ticks (FixedTickConversion.TicksPerSecond = 50400/s — the stepTicks unit every Advance
    // call decrements this by, NOT the 240Hz simulation-step counter world.wait/console echoes count in; one fixed
    // simulation step is 210 of these) an un-refreshed latch survives a solver pass that reports no push at all — a
    // grace window absorbing ordinary
    // query noise near a surface (a gradient/quantization boundary the SDF field provider can land exactly on, or —
    // measured empirically driving into play.world.json's east wall — a body settled into a wall/ground corner
    // under SmoothUnionContact blending, which can drift in and out of the walkable classification for many
    // consecutive simulation steps while genuinely never clearing) so that noise can never flicker the witness.
    // Reset to the full window every time a fresh push actually lands.
    private ulong m_obstructionWitnessGraceTicks;
    private static readonly FixedQ4816 s_obstructionLatchDisplacementSquared = FixedQ4816.FromDouble(value: 4.0); // (2 units)^2 — a single noisy depenetration correction at a blended corner can be large; only a body that has genuinely moved on should cross this
    // A raw MoveForward/MoveStrafe role channel reads in [-1, 1] — well clear of ordinary analog noise at this
    // threshold, so a genuinely-released stick/button (exactly 0) and a barely-held one are both "idle" alike.
    private static readonly FixedQ4816 s_obstructionLatchIdleThreshold = FixedQ4816.FromDouble(value: 0.05);
    private static readonly ulong ObstructionLatchGraceTicks = FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.FromDouble(value: 0.5)); // 0.5s of real time

    // The per-channel action runtime: the compiled binding (null = unbound), its press latch, and one recency clock per
    // Recently-predicate instance. Named counters and timers live in the kit-wide action-state register file below.
    // A role ordinal never has a binding, so it is naturally inert here.
    private readonly CompiledActionSpec?[] m_laneBindings = new CompiledActionSpec?[ActionLaneCount];
    private readonly LaneActionRuntime[] m_laneActions = new LaneActionRuntime[ActionLaneCount];
    private CompiledActionStateSlot[] m_actionStateDefinitions = [];
    private FixedQ4816[] m_actionStateValues = [];
    private ulong[] m_actionStateTimers = [];
    private long[] m_actionStateRequested = [];
    private string[] m_actionStateLastWriter = [];
    private string[] m_actionStateLastReason = [];
    private bool[] m_actionStateDirty = [];
    private WorldDocumentWriteKind[] m_actionStateDirtyKind = [];
    private FixedQ4816[] m_actionStateDirtyOperand = [];
    private bool[] m_durableInputPresent = [];
    private FixedQ4816[] m_durableInputValues = [];
    private ulong[] m_durableInputTimers = [];
    private string[] m_durableInputWriters = [];
    private ulong m_durableInputTick;
    private int m_affectingSubject = -1;
    // The binary crossing threshold per ordinal (meaningful only where m_laneBindings is non-null) — resolved once
    // from the world's channel table at construction/recompile.
    private FixedQ4816[] m_channelThresholds = new FixedQ4816[ActionLaneCount];
    // The declared shape per ordinal — EVERY ordinal, not just bound ones (unlike m_channelThresholds): the
    // held-image overlay below composes a channel whether or not a kit binds an action to it. Resolved once from the
    // world's channel table at construction/recompile; an unpopulated slot defaults to Bipolar (ChannelShape's zero
    // value), the same fallback WorldServer uses for an undeclared ordinal.
    private ChannelShape[] m_channelShapes = new ChannelShape[ActionLaneCount];
    private bool[] m_roleChannels = new bool[ActionLaneCount];
    private RoleChannelOrdinals m_roleOrdinals;
    // The sprint gap's held channel: -1 (no sprint capability) unless the kit names one that resolves. Resolved once
    // from the world's channel table at construction/recompile, exactly like the per-ordinal arrays above — the SAME
    // Resolved outside and consumed as a plain ordinal inside.
    private int m_sprintChannelOrdinal = -1;

    // The timed impulse overlay (the dash): a world-space velocity integrated through its own accumulator on top of
    // the model's motion for a bounded tick budget — integration itself is untouched. Cleared by hard teleports.
    private FixedVector3 m_overlayVelocity;
    private ulong m_overlayRemaining;
    private FixedVector3RateAccumulator m_overlayAccumulator = new(ticksPerSecond: EngineTicksPerSecond);

    // The construction-validated program table and the program this body executes.
    private IReadOnlyDictionary<string, CompiledBodyMotionProgram> m_bodyMotionPrograms;
    private CompiledBodyMotionProgram m_bodyMotionProgram;

    // The intent-source axis (Live by default; a peer takes the population's stored default at activation). Set by
    // player.control / the peer sweep. See IntentSource for the merge rule this selects.
    private IntentSource m_source = IntentSource.Live;

    // The screen-engagement route latch (disengaged by default). Set by player.engage/disengage. While engaged the
    // resolved intent is DIVERTED to the bound screen's machine instead of the avatar: Advance captures it into
    // m_engagedIntent and holds the avatar idle (no pose integration). ORTHOGONAL to m_source — engagement decides
    // where the intent GOES (avatar vs machine), the intent-source axis decides what FILLS it.
    private bool m_engaged;
    private PlayerIntent m_engagedIntent;

    // The avatar's simulation position. See Position.
    private FixedVector3 m_position;
    // The position captured at the top of the most recent Advance — the swept portal-crossing scan's segment start
    // (see FixedPreviousPosition). A hard teleport resets it to the landing position in the SAME CommitTeleport call
    // that resets m_verticalVelocity/m_planarVelocity for the identical reason: without the reset a warped body would
    // leave a ghost segment behind, sweeping a portal scan through space it never actually travelled.
    private FixedVector3 m_previousPosition;
    // Sub-Q48.16 integration state. Per-second velocity/rate numerators are divided by the exact engine time base;
    // these signed remainders carry the discarded tails into later steps instead of losing them every fixed update.
    // They are authoritative state: hard pose writes reset the affected channels. Each is bound to the engine time base
    // once here — a remainder is a numerator over that denominator, so the denominator is accumulator identity.
    private FixedVector3RateAccumulator m_positionAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedVector3RateAccumulator m_rotationAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private FixedRateAccumulator m_verticalVelocityAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    // The grounded model's authoritative heading scalar (radians): integrated from the Turn rate, with m_orientation
    // derived from it each step (a pure yaw rotation). Under free it is inert (orientation is authoritative and Yaw is
    // read back out of it).
    private FixedQ4816 m_yaw;
    // The canonical orientation — the full 6DOF attitude the renderer, the camera rigs, and player.where all read. Under
    // grounded it mirrors m_yaw (pitch = roll = 0); under free it is the integrated body-frame attitude and m_yaw is
    // ignored. The model constrains how it is written, never its shape.
    private FixedQuaternion m_orientation = FixedQuaternion.Identity;

    // How this tick's pose change should be presented — the snapshot's per-entity continuity hint. Hard teleports
    // (Warp/Face/Pose/SetBodyMotionProgram, and an over-ceiling Reconcile) write Teleport; a smoothed Reconcile writes Correction.
    // Last write wins within a tick; TakeContinuity consumes it at snapshot emit.
    private EntityContinuity m_continuity = EntityContinuity.Continuous;

    // The swim-specific compiled half (null for every non-swim kit) and the swim integrator's own carry: ONE ramp
    // accumulator for the whole thrust convergence (planar and vertical alike), the same "remainder binds to the
    // tick base" shape as m_planarRampAccumulator — so alternating engage/release rates through it stays exact, no
    // separate accumulator per stage. The waterline arrives from the population beside the contact field
    // (SetWaterline); the two swim facts are written by the surface stage and read one tick behind, the same
    // discipline m_grounded follows.
    private FixedSwimTuning? m_swimTuning;
    private FixedQ4816 m_waterline;
    private bool m_hasWaterline;
    private FixedRateAccumulator m_swimThrustRampAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    private bool m_submerged;
    private bool m_atSurface;

    // The one dispatch point from a kit's declared WorldMotionModel to the compiled fixed-point tuning this class
    // integrates under. A new model arm (swim/vehicle) is a localized addition here — a new case producing that
    // model's own compiled/integrator state — never a hunt through Advance's op handlers, which stay generic over
    // whatever the kit's body motion program selects. WorldDefinitionValidator has already refused an incoherent
    // pairing (a program whose operations need a facet the declared model doesn't supply) before this ever runs.
    //
    // The vehicle arm also fills m_tuning, from its own gravity trio: ApplyVerticalGravity/ApplyVerticalDecay read
    // m_tuning's Rise/Fall/MaxFall whichever model authored them (the validator's GravityArc/GravityBleed facets
    // guarantee the vehicle row carries all three), and MoveSpeed/TurnSpeed mirror TopSpeed/SteerRate so the
    // pre-dispatch Speed resolve stays well-formed — the vehicle ops themselves read only m_vehicleTuning. The swim
    // arm compiles STRAIGHT into the shared m_tuning slots (FixedMotionTuning.Compile(WorldMotionModel.Swim) maps
    // ThrustSpeed/ThrustSpeedEnvelope onto MoveSpeed/MoveSpeedEnvelope) — no fork, so the grounded-shaped resolve is
    // already arm-correct for swim; only the swim-specific half (buoyancy, float depth, ...) needs its own record.
    private void SetTuning(WorldMotionModel motion) {
        switch (motion) {
            case WorldMotionModel.Grounded grounded:
                m_motionArm = CompiledMotionArm.Grounded;
                m_tuning = FixedMotionTuning.Compile(tuning: grounded);
                m_vehicleTuning = default;
                m_swimTuning = null;
                break;
            case WorldMotionModel.Vehicle vehicle:
                m_motionArm = CompiledMotionArm.Vehicle;
                m_vehicleTuning = FixedVehicleTuning.Compile(tuning: vehicle);
                m_tuning = FixedMotionTuning.Compile(tuning: new WorldMotionModel.Grounded(
                    MoveSpeed: vehicle.TopSpeed,
                    TurnSpeed: vehicle.SteerRate,
                    RiseGravity: vehicle.RiseGravity,
                    FallGravity: vehicle.FallGravity,
                    MaxFallSpeed: vehicle.MaxFallSpeed,
                    Response: [],
                    SprintMultiplier: 1f
                ));
                m_swimTuning = null;
                break;
            case WorldMotionModel.Swim swim:
                m_motionArm = CompiledMotionArm.Swim;
                m_tuning = FixedMotionTuning.Compile(tuning: swim);
                m_vehicleTuning = default;
                m_swimTuning = FixedSwimTuning.Compile(tuning: swim);
                break;
            default:
                throw new NotSupportedException(message: $"Motion model '{motion.GetType().Name}' has no compiled WorldBody integrator.");
        }
    }

    /// <summary>Initializes a new instance of the <see cref="WorldBody"/> class under a motion model, its kit's
    /// per-channel action bindings, and its kit's body motion program. A <see langword="null"/> binding leaves that ordinal
    /// inert.</summary>
    /// <param name="motion">The motion model to integrate under (the body's kit's declared <see cref="WorldMotionModel"/>).</param>
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
    /// <param name="sprintChannelOrdinal">The ordinal <see cref="WorldMotionModel.Grounded.SprintChannel"/> resolved to
    /// (<see cref="FixedWorldKit.SprintChannelOrdinal"/>), or <c>-1</c> for a kit with no sprint capability.</param>
    /// <param name="driftChannelOrdinal">The ordinal <see cref="WorldMotionModel.Vehicle.DriftChannel"/> resolved to
    /// (<see cref="FixedWorldKit.DriftChannelOrdinal"/>), or <c>-1</c> for a kit that cannot drift.</param>
    /// <exception cref="ArgumentNullException"><paramref name="program"/> or <paramref name="programs"/> is <see langword="null"/>.</exception>
    public WorldBody(WorldMotionModel motion, CompiledBodyMotionProgram program, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, FixedQ4816 maxSmoothError, CompiledActionSpec?[]? actions = null, FixedQ4816[]? actionThresholds = null, ChannelShape[]? actionShapes = null, bool[]? roleMask = null, RoleChannelOrdinals roleOrdinals = default, CompiledActionStateSlot[]? actionState = null, FixedWorldCollider? collider = null, int sprintChannelOrdinal = -1, int driftChannelOrdinal = -1) {
        SetTuning(motion: motion);
        m_bodyMotionProgram = program ?? throw new ArgumentNullException(paramName: nameof(program));
        m_bodyMotionPrograms = programs ?? throw new ArgumentNullException(paramName: nameof(programs));
        CopyChannelBindings(actions: actions, actionThresholds: actionThresholds, actionShapes: actionShapes, roleMask: roleMask);
        m_roleOrdinals = roleOrdinals;
        CompileActionState(state: actionState);
        m_collider = collider;
        m_maxSmoothError = maxSmoothError;
        m_sprintChannelOrdinal = sprintChannelOrdinal;
        m_driftChannelOrdinal = driftChannelOrdinal;

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            if (m_laneBindings[lane] is { RecencyFacts.Length: > 0 } binding) {
                m_laneActions[lane].Recency = new ulong[binding.RecencyFacts.Length];
            }
        }

        if (m_tuning.RecencySlots > 0) {
            m_motionRecency = new ulong[m_tuning.RecencySlots];
        }

    }

    // Copies a compiled kit's per-ordinal actions/thresholds/shapes into this body's own arrays (never aliasing the
    // kit's shared arrays, which RecompileKit would otherwise mutate out from under every body sharing the old kit row).
    private void CopyChannelBindings(CompiledActionSpec?[]? actions, FixedQ4816[]? actionThresholds, ChannelShape[]? actionShapes, bool[]? roleMask) {
        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            m_laneBindings[ordinal] = ((actions is { } bound) && (ordinal < bound.Length)) ? bound[ordinal] : null;
            m_channelThresholds[ordinal] = ((actionThresholds is { } thresholds) && (ordinal < thresholds.Length)) ? thresholds[ordinal] : FixedQ4816.Zero;
            m_channelShapes[ordinal] = ((actionShapes is { } shapes) && (ordinal < shapes.Length)) ? shapes[ordinal] : ChannelShape.Bipolar;
            m_roleChannels[ordinal] = ((roleMask is { } roles) && (ordinal < roles.Length) && roles[ordinal]);
        }
    }

    /// <summary>Sets (or clears) the world contact field this body's grounded integrator solves its swept position
    /// against — the population hands it the live field on activation and every rebuild.</summary>
    /// <param name="field">The world contact field.</param>
    public void SetContactField(IContactField? field) {
        m_contactField = field;
    }

    /// <summary>Sets (or clears) the waterline this body's swim stages integrate against — the population hands it
    /// the world's compiled water level beside the contact field, on activation and every rebuild. Meaningful only
    /// to a swim-model kit; every other body carries it inertly.</summary>
    /// <param name="level">The waterline's world-space Y, or <see langword="null"/> for a dry world.</param>
    public void SetWaterline(FixedQ4816? level) {
        m_hasWaterline = level.HasValue;
        m_waterline = level.GetValueOrDefault();
    }

    /// <summary>Swaps this body's compiled kit feel in place after a live kit retune — the once-at-the-boundary
    /// recompile of a mutated <see cref="WorldKit"/>: the fixed-point locomotion tuning, the channel bindings, and
    /// the body motion program. The body keeps its pose, velocity, tape, source, and engagement; only the compiled feel
    /// changes. The action runtime resets because it is bound to the old binding and named-state shapes, and an
    /// incompatible program switch re-pins the pose exactly as
    /// <c>player.motion</c> does (a no-op when unchanged).</summary>
    /// <param name="motion">The kit's authored motion model.</param>
    /// <param name="actions">The kit's compiled per-ordinal action bindings.</param>
    /// <param name="actionThresholds">The kit's per-ordinal binary crossing thresholds, parallel to <paramref name="actions"/>.</param>
    /// <param name="actionShapes">The world's per-ordinal declared channel shapes (every ordinal, not just bound ones).</param>
    /// <param name="roleMask">The world's compiled per-ordinal role predicate.</param>
    /// <param name="roleOrdinals">The kit's resolved engine motion role ordinals.</param>
    /// <param name="actionState">The kit's compiled named action-state register file.</param>
    /// <param name="program">The kit's compiled body motion program.</param>
    /// <param name="programs">The world's compiled body motion program table.</param>
    /// <param name="collider">The kit's compiled body volume, or <see langword="null"/> for a volumeless kit.</param>
    /// <param name="maxSmoothError">The compiled world-distance correction smoothing threshold.</param>
    /// <param name="sprintChannelOrdinal">The ordinal <see cref="WorldMotionModel.Grounded.SprintChannel"/> resolved to, or <c>-1</c>
    /// for a kit with no sprint capability.</param>
    /// <param name="driftChannelOrdinal">The ordinal <see cref="WorldMotionModel.Vehicle.DriftChannel"/> resolved to,
    /// or <c>-1</c> for a kit that cannot drift.</param>
    public void RecompileKit(WorldMotionModel motion, CompiledActionSpec?[]? actions, FixedQ4816[]? actionThresholds, ChannelShape[]? actionShapes, bool[]? roleMask, RoleChannelOrdinals roleOrdinals, CompiledActionStateSlot[]? actionState, CompiledBodyMotionProgram program, IReadOnlyDictionary<string, CompiledBodyMotionProgram> programs, FixedWorldCollider? collider, FixedQ4816 maxSmoothError, int sprintChannelOrdinal = -1, int driftChannelOrdinal = -1) {
        SetTuning(motion: motion);
        CopyChannelBindings(actions: actions, actionThresholds: actionThresholds, actionShapes: actionShapes, roleMask: roleMask);
        m_roleOrdinals = roleOrdinals;
        CompileActionState(state: actionState);
        m_collider = collider;
        m_maxSmoothError = maxSmoothError;
        m_sprintChannelOrdinal = sprintChannelOrdinal;
        m_driftChannelOrdinal = driftChannelOrdinal;

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            m_laneActions[lane] = default;

            if (m_laneBindings[lane] is { RecencyFacts.Length: > 0 } binding) {
                m_laneActions[lane].Recency = new ulong[binding.RecencyFacts.Length];
            }
        }

        // The response recency clocks are bound to the OLD table shape (a new table may have a different Recently count),
        // so they reset on a recompile — but m_planarVelocity SURVIVES, because a live retune must not jerk the crowd.
        m_motionRecency = ((m_tuning.RecencySlots > 0) ? new ulong[m_tuning.RecencySlots] : []);

        m_bodyMotionPrograms = programs;
        SetBodyMotionProgram(program: program);
    }

    private void CompileActionState(CompiledActionStateSlot[]? state) {
        var previousDefinitions = m_actionStateDefinitions;
        var previousValues = m_actionStateValues;
        var previousTimers = m_actionStateTimers;
        var previousRequested = m_actionStateRequested;
        var previousWriters = m_actionStateLastWriter;
        var previousReasons = m_actionStateLastReason;
        m_actionStateDefinitions = (state is null ? [] : [.. state]);
        m_actionStateValues = new FixedQ4816[m_actionStateDefinitions.Length];
        m_actionStateTimers = new ulong[m_actionStateDefinitions.Length];
        m_actionStateRequested = new long[m_actionStateDefinitions.Length];
        m_actionStateLastWriter = new string[m_actionStateDefinitions.Length];
        m_actionStateLastReason = new string[m_actionStateDefinitions.Length];
        m_actionStateDirty = new bool[m_actionStateDefinitions.Length];
        m_actionStateDirtyKind = new WorldDocumentWriteKind[m_actionStateDefinitions.Length];
        m_actionStateDirtyOperand = new FixedQ4816[m_actionStateDefinitions.Length];
        m_durableInputPresent = new bool[m_actionStateDefinitions.Length];
        m_durableInputValues = new FixedQ4816[m_actionStateDefinitions.Length];
        m_durableInputTimers = new ulong[m_actionStateDefinitions.Length];
        m_durableInputWriters = new string[m_actionStateDefinitions.Length];
        m_durableInputTick = 0;

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];
            var preserved = -1;
            if (definition.Lifetime == ActionStateLifetime.Durable) {
                for (var prior = 0; (prior < previousDefinitions.Length); prior++) {
                    if ((previousDefinitions[prior].Lifetime == ActionStateLifetime.Durable) &&
                        (previousDefinitions[prior].Kind == definition.Kind) &&
                        string.Equals(a: previousDefinitions[prior].Name, b: definition.Name, comparisonType: StringComparison.Ordinal)) {
                        preserved = prior;
                        break;
                    }
                }
            }

            m_actionStateValues[slot] = ((preserved >= 0) ? previousValues[preserved] : definition.InitialValue);
            m_actionStateTimers[slot] = ((preserved >= 0) ? previousTimers[preserved] : definition.InitialTicks);
            m_actionStateRequested[slot] = ((preserved >= 0) ? previousRequested[preserved] : InitialRaw(definition: in definition));
            m_actionStateLastWriter[slot] = ((preserved >= 0) ? previousWriters[preserved] : "author");
            m_actionStateLastReason[slot] = ((preserved >= 0) ? previousReasons[preserved] : "initial value");
        }
    }

    /// <summary>Stages durable values for one explicit simulation tick. Repeated inputs in that tick compose by
    /// submission order; the last value for a slot wins.</summary>
    internal bool TryStageDurableState(ulong tick, IReadOnlyList<DurableStateValue> values, bool requirePlayerWritable, string writer, out string reason) {
        if (Profile is null) {
            reason = "the body has no player identity";
            return false;
        }

        foreach (var value in values) {
            var slot = FindActionState(name: value.Name);
            if ((slot < 0) || (m_actionStateDefinitions[slot].Lifetime != ActionStateLifetime.Durable)) {
                reason = $"state '{value.Name}' names no durable slot";
                return false;
            }
            if (requirePlayerWritable && !m_actionStateDefinitions[slot].PlayerWritable) {
                reason = $"state '{value.Name}' is not player-writable";
                return false;
            }
            if (((m_actionStateDefinitions[slot].Kind == ActionStateKind.Counter) && (value.TimerTicks != 0)) ||
                ((m_actionStateDefinitions[slot].Kind == ActionStateKind.Timer) && (value.Value != FixedQ4816.Zero))) {
                reason = $"state '{value.Name}' carries the wrong value kind";
                return false;
            }
            var raw = Raw(value: value, kind: m_actionStateDefinitions[slot].Kind);
            if (requirePlayerWritable && (m_actionStateDefinitions[slot].Envelope is { } envelope) && !envelope.Contains(value: raw)) {
                reason = $"state '{value.Name}' value lies outside the authored envelope";
                return false;
            }
        }

        if ((m_durableInputTick != 0) && (m_durableInputTick != tick)) {
            Array.Clear(array: m_durableInputPresent);
        }
        m_durableInputTick = tick;

        foreach (var value in values) {
            var slot = FindActionState(name: value.Name);
            m_durableInputPresent[slot] = true;
            m_durableInputValues[slot] = value.Value;
            m_durableInputTimers[slot] = value.TimerTicks;
            m_durableInputWriters[slot] = writer;
        }

        reason = string.Empty;
        return true;
    }

    internal void AppendDurableStateDeclarations(List<(string Name, ActionStateKind Kind)> declarations) {
        foreach (var definition in m_actionStateDefinitions) {
            if (definition.Lifetime == ActionStateLifetime.Durable) {
                declarations.Add(item: (definition.Name, definition.Kind));
            }
        }
    }

    /// <summary>Clears player-owned durable values to their authored initial values when identity changes.</summary>
    internal void ResetDurableState() {
        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (m_actionStateDefinitions[slot].Lifetime == ActionStateLifetime.Durable) {
                m_actionStateValues[slot] = m_actionStateDefinitions[slot].InitialValue;
                m_actionStateTimers[slot] = m_actionStateDefinitions[slot].InitialTicks;
                m_actionStateRequested[slot] = InitialRaw(definition: in m_actionStateDefinitions[slot]);
                m_actionStateLastWriter[slot] = "author";
                m_actionStateLastReason[slot] = "identity reset";
                m_actionStateDirty[slot] = false;
            }
        }
        Array.Clear(array: m_durableInputPresent);
        m_durableInputTick = 0;
    }

    internal string DescribeActionState() {
        if (m_actionStateDefinitions.Length == 0) {
            return "state=none";
        }

        var values = new string[m_actionStateDefinitions.Length];
        for (var slot = 0; (slot < values.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];
            var value = (definition.Kind == ActionStateKind.Counter)
                ? ((double)m_actionStateValues[slot]).ToString("0.####", CultureInfo.InvariantCulture)
                : m_actionStateTimers[slot].ToString(CultureInfo.InvariantCulture);
            var requested = DescribeRaw(definition: in definition, raw: m_actionStateRequested[slot]);
            var envelope = DescribeEnvelope(definition.Envelope, definition.Kind);
            values[slot] = $"{definition.Name}:{definition.Kind.ToString().ToLowerInvariant()}/{definition.Lifetime.ToString().ToLowerInvariant()} writable={definition.PlayerWritable.ToString().ToLowerInvariant()} envelope={envelope} requested={requested} effective={value} writer={m_actionStateLastWriter[slot]} reason={m_actionStateLastReason[slot]}";
        }

        return string.Join(separator: " ", values: values);
    }

    /// <summary>Reads one declared action-state slot without changing it.</summary>
    /// <param name="name">The slot name.</param>
    /// <param name="kind">The storage kind.</param>
    /// <param name="lifetime">The declared lifetime.</param>
    /// <param name="playerWritable">Whether the identity may submit a value.</param>
    /// <param name="value">The counter value.</param>
    /// <param name="timerTicks">The timer remainder.</param>
    /// <returns>Whether the slot exists.</returns>
    public bool TryDescribeActionState(string name, out ActionStateKind kind, out ActionStateLifetime lifetime, out bool playerWritable, out FixedQ4816 value, out ulong timerTicks) {
        var slot = FindActionState(name: name);
        if (slot < 0) {
            kind = default;
            lifetime = default;
            playerWritable = false;
            value = default;
            timerTicks = 0;
            return false;
        }

        kind = m_actionStateDefinitions[slot].Kind;
        lifetime = m_actionStateDefinitions[slot].Lifetime;
        playerWritable = m_actionStateDefinitions[slot].PlayerWritable;
        value = m_actionStateValues[slot];
        timerTicks = m_actionStateTimers[slot];
        return true;
    }

    /// <summary>Reads one effective durable counter for a visited-world decision.</summary>
    internal bool TryReadDurableCounter(string name, out FixedQ4816 value) {
        var slot = FindActionState(name: name);
        if ((slot < 0) || (m_actionStateDefinitions[slot].Kind != ActionStateKind.Counter) || (m_actionStateDefinitions[slot].Lifetime != ActionStateLifetime.Durable)) {
            value = default;
            return false;
        }
        value = m_actionStateValues[slot];
        return true;
    }

    internal void TakeDurableStateOutputs(ulong tick, int entityIndex, List<DurableStateOutput> outputs) {
        if (Profile is null) {
            Array.Clear(array: m_actionStateDirty);
            return;
        }

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (!m_actionStateDirty[slot] || (m_actionStateDefinitions[slot].Lifetime != ActionStateLifetime.Durable)) {
                continue;
            }

            outputs.Add(item: new DurableStateOutput(
                Tick: tick,
                PlayerId: Profile.Id,
                EntityIndex: entityIndex,
                Value: new DurableStateValue(
                    Name: m_actionStateDefinitions[slot].Name,
                    Value: (m_actionStateDirtyKind[slot] == WorldDocumentWriteKind.Add ? m_actionStateDirtyOperand[slot] : m_actionStateValues[slot]),
                    TimerTicks: m_actionStateTimers[slot]),
                Kind: m_actionStateDirtyKind[slot],
                StorageKind: m_actionStateDefinitions[slot].Kind));
            m_actionStateDirty[slot] = false;
        }
    }

    private int FindActionState(string name) {
        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (string.Equals(a: m_actionStateDefinitions[slot].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return slot;
            }
        }
        return -1;
    }

    /// <summary>Gets the profile this player is seated on — the live source of its move/turn speeds and look-invert (read
    /// every <see cref="Advance"/>, so an <c>identity.motion</c> edit is real-time) and the color the avatar renders. May be
    /// <see langword="null"/> before a profile is assigned, in which case the tuning's default rates apply.</summary>
    public WorldIdentity? Profile { get; set; }

    /// <summary>Gets the base move speed the sim integrates under right now, arm-aware. Under the grounded arm:
    /// <see cref="Profile"/>'s requested rate (or the tuning's profileless fallback) after the kit's
    /// <see cref="WorldMotionModel.Grounded.MoveSpeedEnvelope"/> clamp; a held sprint channel scales this after the
    /// clamp (the envelope pins the base rate, not the sprinting rate). Under the swim arm: the same resolve,
    /// verbatim — <see cref="WorldMotionModel.Swim.ThrustSpeedEnvelope"/> compiles into the identical shared
    /// <c>MoveSpeedEnvelope</c> slot the grounded arm reads, so a seated player's live profile speed clamps the same
    /// way. Under the vehicle arm: the kit's own <see cref="WorldMotionModel.Vehicle.TopSpeed"/> after its
    /// <see cref="WorldMotionModel.Vehicle.TopSpeedEnvelope"/> clamp — the vehicle arm deliberately never reads
    /// <see cref="Profile"/>'s speed (a kart's speed is the kit's, not the seat's identity), and a held boost
    /// channel scales this after the clamp, on the same sprint-after-clamp precedent. Every arm, this is the same
    /// resolve <see cref="Advance"/> performs every tick. A read-only echo: querying this never mutates state, and
    /// an unenveloped kit returns the requested/kit rate unchanged.</summary>
    public FixedQ4816 EffectiveMoveSpeed => ResolveMoveSpeed();

    // The one seat-time resolve, per arm. Shared by Advance (which feeds this into the program) and
    // EffectiveMoveSpeed (which only reads it back) so the two can never compute two different answers to "what
    // speed is this body actually moving at". A new model arm (swim) adds its own case here, alongside its
    // SetTuning case (see CompiledMotionArm's remarks).
    private FixedQ4816 ResolveMoveSpeed() {
        switch (m_motionArm) {
            case CompiledMotionArm.Grounded:
            case CompiledMotionArm.Swim:
                // Swim compiles into the SAME shared m_tuning slots grounded reads (see SetTuning's remarks), so it
                // rides this same case rather than forking its own.
                var resolved = (Profile?.FixedMoveSpeed ?? m_tuning.MoveSpeed);

                return (m_tuning.MoveSpeedEnvelope?.Clamp(value: resolved) ?? resolved);
            case CompiledMotionArm.Vehicle:
                // Deliberately never reads Profile — a kart's speed is the kit's, a design fact per arm, not an
                // omission. TopSpeed is the base rate the (optional) TopSpeedEnvelope pins; boost multiplies AFTER
                // this resolve (see ShapeVehicleVelocity), never inside it.
                return (m_vehicleTuning.TopSpeedEnvelope?.Clamp(value: m_vehicleTuning.TopSpeed) ?? m_vehicleTuning.TopSpeed);
            default:
                throw new NotSupportedException(message: $"Motion arm '{m_motionArm}' has no compiled move-speed resolve.");
        }
    }

    /// <summary>Gets the body that applied the latest targeted effect, held for one recipient advance.</summary>
    internal int AffectingSubject => m_affectingSubject;

    /// <summary>Gets the avatar's current world-space position (the ground foot point under the grounded model, where Y is
    /// pinned to the plane; a free craft's position is unconstrained in all three axes).</summary>
    public Vector3 Position => m_position.ToVector3();

    /// <summary>Gets the kit-authored body volumes, or <see langword="null"/> for a volumeless kit.</summary>
    public FixedWorldCollider? Collider => m_collider;
    /// <summary>Gets the authoritative deterministic position.</summary>
    public FixedVector3 FixedPosition => m_position;
    /// <summary>Gets the avatar's position at the top of the most recent <see cref="Advance"/> — the start point of
    /// the swept segment a portal-crossing scan tests against a slab. A hard teleport (<c>Pose</c>,
    /// <see cref="Reconcile"/>) resets this to the landing position, so the segment collapses to a point exactly
    /// where a teleport-into-the-volume must still be detected as a point test.</summary>
    public FixedVector3 FixedPreviousPosition => m_previousPosition;

    private WorldContinuumTrajectory? m_pendingContinuum;
    private ulong? m_continuumConsumedThroughEngineTick;
    private bool m_ordinaryAdvanceAdmitted;

    /// <summary>The already-evaluated source-step trajectory awaiting ownership resolution before this body may
    /// advance normally on its destination authority.</summary>
    public WorldContinuumTrajectory? PendingContinuum => m_pendingContinuum;

    /// <summary>Clears the exact pending trajectory after topology either retained this owner, forwarded the body, or
    /// safety-clamped it. The independent consumed-through time fence remains until a non-overlapping ordinary
    /// authority step begins.</summary>
    public void ClearPendingContinuum() => m_pendingContinuum = null;

    /// <summary>Attempts to admit an ordinary authority step after both geometric continuation and continuum-time
    /// ownership have settled. A refused step performs no input, action, timer, gravity, or movement work.</summary>
    public bool TryBeginOrdinaryAdvance(ulong stepStartEngineTick) {
        if ((m_pendingContinuum is not null) ||
            (m_continuumConsumedThroughEngineTick is { } consumedThrough && (stepStartEngineTick < consumedThrough))) {
            m_ordinaryAdvanceAdmitted = false;
            return false;
        }

        m_continuumConsumedThroughEngineTick = null;
        m_ordinaryAdvanceAdmitted = true;
        return true;
    }

    /// <summary>Gets whether this body entered the current authority step and may participate in its dynamic-contact
    /// solve. A continuum-fenced body is immutable until a non-overlapping ordinary step admits it.</summary>
    public bool OrdinaryAdvanceAdmitted => m_ordinaryAdvanceAdmitted;
    /// <summary>Gets the avatar's current heading in radians (0 = facing -Z; increases turning left / counter-clockwise).
    /// Under the grounded model this returns the authoritative heading scalar <c>m_yaw</c> directly (the orientation is a
    /// pure yaw rotation built from it, so decomposing it back out would be a redundant round-trip on the hot wander
    /// path). Under the free model, where the full attitude is authoritative and <c>m_yaw</c> is inert, it is the yaw
    /// component of <see cref="Orientation"/>. The <c>player.where</c> read-back and <see cref="DescribePose"/> decompose
    /// the canonical orientation directly, bypassing this property.</summary>
    public float Yaw => (float)(double)FixedYaw;
    /// <summary>Gets the authoritative deterministic heading.</summary>
    public FixedQ4816 FixedYaw => (m_bodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude) ? ExtractYaw(orientation: m_orientation) : m_yaw);
    /// <summary>Gets the avatar's full 6DOF attitude — the canonical orientation a camera rig or a dynamic transform rides.
    /// Pure yaw about world up under the grounded model; an arbitrary body attitude under the free model.</summary>
    public Quaternion Orientation => m_orientation.ToQuaternion();
    /// <summary>Gets the authoritative deterministic orientation.</summary>
    public FixedQuaternion FixedOrientation => m_orientation;
    /// <summary>Gets the body motion program this player currently executes.</summary>
    public string BodyMotionProgram => m_bodyMotionProgram.Name;
    /// <summary>Gets what fills this entity's intent gaps between tape segments — the per-entity axis (the
    /// <c>player.control</c> verb's read/write). <see cref="IntentSource.Live"/> by default; see
    /// <see cref="IntentSource"/> for the merge rule.</summary>
    public IntentSource Source => m_source;

    /// <summary>Gets a value indicating whether the body is grounded this tick (resting on a walkable contact surface) — the
    /// <c>world.contacts</c> read-back.</summary>
    public bool Grounded => m_grounded;

    /// <summary>Gets a value indicating whether the body's origin is below the waterline as of the swim model's last
    /// surface stage — the <c>world.contacts</c> read-back's swim witness. Always <see langword="false"/> for a
    /// non-swim kit.</summary>
    public bool Submerged => m_submerged;

    /// <summary>Gets a value indicating whether the body's origin is inside the swim model's surface bob band as of
    /// its last surface stage — the <c>world.contacts</c> read-back's swim witness. Always <see langword="false"/>
    /// for a non-swim kit.</summary>
    public bool AtSurface => m_atSurface;

    /// <summary>Gets the body's response-shaped planar speed (world units/second) — the coast/momentum witness the
    /// <c>world.contacts</c> read reports.</summary>
    public float PlanarSpeed => (float)(double)m_planarVelocity.Length;

    /// <summary>Gets the last <see cref="Advance"/>'s grounded witness echoed as a count — <c>1</c> when the resolve grounded
    /// this body, <c>0</c> otherwise. This is not a per-surface tally: a body pushed by a wall while airborne (or while
    /// standing on the ground elsewhere) still reads <c>0</c> here — <em>that</em> obstruction is what
    /// <see cref="LastObstructionNormal"/> now surfaces instead. Introspection-only, surfaced by the
    /// <c>world.contacts</c> read-back.</summary>
    public int ContactCount => m_lastContactCount;

    /// <summary>Gets the latched resolved non-walkable contact normal — <see cref="FixedVector3.Zero"/> when nothing
    /// obstructs the body, a unit surface normal otherwise. A walkable push (the ground, a ramp) never sets this —
    /// only a contact whose alignment fails the grounded test does, which is exactly the witness
    /// <see cref="ContactCount"/> cannot show: a body pushed by a vertical wall reads <see cref="ContactCount"/>
    /// <c>0</c> but a non-zero normal here, even while simultaneously standing (grounded) on the floor. Latched
    /// (see <see cref="UpdateObstructionWitness"/>) rather than a raw per-tick read: it survives a solver tick that
    /// happens not to re-register the push while the body stays driven and hasn't moved, and clears the instant
    /// input goes idle or the body actually gets clear. Read-back only — the raw normal is a solver fact from
    /// <see cref="ContactResolution"/>, surfaced without changing simulation behavior, via the
    /// <c>world.contacts</c> read-back.</summary>
    public FixedVector3 LastObstructionNormal => m_obstructionWitness;

    /// <summary>Gets a value indicating whether this route captures this body — the route table's <c>RouteCapture</c> latched onto the body at
    /// <c>Engage</c> time. While captured its resolved per-frame intent is diverted to the route's target (read via
    /// <see cref="EngagedIntent"/>) instead of driving the avatar, which stands idle. <see langword="false"/> while
    /// unrouted, or while routed under the mirrored (capture:false) policy — either way the avatar keeps integrating
    /// normally.</summary>
    public bool Engaged => m_engaged;

    /// <summary>Gets the intent resolved on the most recent <see cref="Advance"/> — captured every tick regardless of
    /// capture policy, so a routed body's channels are available for translation/passthrough whether or not the
    /// avatar itself is idled. The <see cref="PlayerIntent"/> default (all channels zero) before the first advance.</summary>
    public PlayerIntent EngagedIntent => m_engagedIntent;

    /// <summary>Presses a channel for the default two-host-step tap, reaching any ordinal (movement roles included).
    /// The concrete engine-tick duration is derived by the next <see cref="Advance"/> from its <c>stepTicks</c> (see
    /// <see cref="MaterializeDefaultLanePresses"/>), which merges it under the same rule <see cref="PressChannel(int, FixedQ4816, float, FixedQ4816)"/>
    /// uses: a same-value re-press only extends an in-flight hold, a different value replaces it outright.</summary>
    /// <param name="ordinal">The channel ordinal to hold.</param>
    /// <param name="value">The raw fixed-point value to hold the channel at.</param>
    public void PressChannel(int ordinal, FixedQ4816 value) {
        if ((ordinal < 0) || (ordinal >= ActionLaneCount)) {
            return;
        }

        m_pendingDefaultChannelPress[ordinal] = true;
        m_pendingDefaultChannelValue[ordinal] = value;
    }

    /// <summary>Presses a channel for a timed auto-release — the scripted/wire path (<c>player.press</c>), reaching
    /// any ordinal: the channel reads held at <paramref name="value"/> for <paramref name="holdSeconds"/> of sim time
    /// (clamped to the row's authored ceiling and the <see cref="MaxActionHoldSeconds"/> engine backstop), decremented
    /// per sub-step, then releases
    /// itself. A short hold is a short hop (the release cuts the rising jump) on a composition channel bound to a
    /// vertical effect; on a movement-role channel it is a timed analog override. A re-press carrying the same value
    /// as the ordinal's in-flight hold never shortens it (the longer of the two durations wins — repeatedly resubmitting
    /// one held key must not truncate itself); a re-press carrying a different value is a distinct action and replaces
    /// the hold outright — its own duration, not merged with whatever remained — so a short full-brake press on a
    /// channel mid a long throttle hold takes effect immediately instead of being swallowed by the throttle's
    /// remaining ticks (see <see cref="MergeLaneTimer"/>, shared with <see cref="MaterializeDefaultLanePresses"/> so
    /// the timed and untimed press paths can never drift onto two different rules). Independent of the movement
    /// tape, so <c>player.fly … ; player.press jump</c> jumps a runner mid-segment. A non-positive (or NaN) hold is
    /// ignored outright — it never touches the lane timer at all, so it cannot cancel a genuine in-flight hold on the
    /// same ordinal under the different-value rule above. Unlike the device-held channel image (see
    /// <see cref="SetHeldChannels"/>), this wire path overlays under every <see cref="IntentSource"/>. N simultaneous
    /// presses in one tick still collapse to this one slot per ordinal (summing distinct presses as pooled fold
    /// contributions, rather than one replacing another, remains owed — see
    /// <c>WorldServer.FoldChannelContributions</c>'s remarks); this fix only stops a same-tick opposing press from
    /// being silently discarded.</summary>
    /// <param name="ordinal">The channel ordinal to hold.</param>
    /// <param name="value">The raw fixed-point value to hold the channel at.</param>
    /// <param name="holdSeconds">How long (sim seconds) the channel reads held before auto-releasing.</param>
    /// <param name="authoredMaximum">The deciding Drive grant row's compiled timed-press ceiling.</param>
    /// <returns>The effective hold (in sim seconds) and which cap, if any, decided it — <c>player.press</c>'s
    /// synchronous read-back, so its echo can name a silent truncation instead of assuming the request was
    /// honored.</returns>
    public PressOutcome PressChannel(int ordinal, FixedQ4816 value, float holdSeconds, FixedQ4816 authoredMaximum) {
        if ((ordinal < 0) || (ordinal >= ActionLaneCount)) {
            return new PressOutcome(EffectiveHoldSeconds: FixedQ4816.Zero, CapKind: PressHoldCapKind.None);
        }

        if (float.IsNaN(f: holdSeconds) || (holdSeconds <= 0f)) {
            return new PressOutcome(EffectiveHoldSeconds: FixedQ4816.Zero, CapKind: PressHoldCapKind.Ignored);
        }

        // The engine-backstop-safe conversion: values at or above the backstop map straight to its constant rather
        // than converting a possibly-huge float through FixedQ4816.FromDouble. "exceedsBackstop" is the STRICT form
        // (> not >=) because it answers a different question — whether the raw request actually needed truncating,
        // not merely "which branch is safe to convert" — a request of EXACTLY the backstop must not be reported as
        // capped by it.
        var exceedsBackstop = (holdSeconds > MaxActionHoldSeconds);
        var engineClamped = ((holdSeconds >= MaxActionHoldSeconds) ? s_maxActionHoldSeconds : FixedQ4816.FromDouble(value: holdSeconds));
        var grantMaximum = FixedQ4816.Clamp(value: authoredMaximum, minimum: FixedQ4816.Zero, maximum: s_maxActionHoldSeconds);
        var hold = FixedQ4816.Min(x: engineClamped, y: grantMaximum);
        // The binder is decided STRUCTURALLY, from the two clamp inputs that produced "hold" — never by comparing
        // the final magnitude against a hardcoded 60, which cannot tell "the grant happens to equal the backstop"
        // apart from "the backstop is what is actually constraining this request". A grant ceiling authored below
        // the backstop is doing REAL narrowing and is credited as the binder even where it ties the backstop's own
        // value; only a grant that permits the full backstop (no narrowing of its own) lets the backstop take the
        // blame for a request that still exceeds it.
        var capKind = ((!exceedsBackstop && (hold >= engineClamped))
            ? PressHoldCapKind.None
            : ((grantMaximum < s_maxActionHoldSeconds) ? PressHoldCapKind.GrantBudget : PressHoldCapKind.EngineCeiling));
        var holdTicks = FixedTickConversion.DurationEngineTicks(seconds: hold);

        MergeLaneTimer(ordinal: ordinal, value: value, holdTicks: holdTicks);

        return new PressOutcome(EffectiveHoldSeconds: hold, CapKind: capKind);
    }

    // Merges (ordinal, value, holdTicks) into the lane-timer slot: a same-value re-press only extends an in-flight
    // hold (the longer of the two durations wins — repeatedly resubmitting one held key must not truncate itself);
    // a DIFFERENT value is a distinct action and replaces the hold outright — its own duration, not merged with
    // whatever ticks remained. Shared by PressChannel's timed overload and MaterializeDefaultLanePresses so the
    // wire-timed and host-step-tap press paths can never drift onto two different merge rules.
    private void MergeLaneTimer(int ordinal, FixedQ4816 value, ulong holdTicks) {
        var isSameValueRepress = ((m_laneTimers[ordinal] > 0) && (m_channelTimerValues[ordinal] == value));

        if (isSameValueRepress) {
            m_laneTimers[ordinal] = Math.Max(val1: m_laneTimers[ordinal], val2: holdTicks);
        } else {
            m_channelTimerValues[ordinal] = value;
            m_laneTimers[ordinal] = holdTicks;
        }
    }
    /// <summary>Stages one deterministic submitted intent for the next <see cref="Advance"/> — the live-stream tier: a
    /// live tape still wins, <see cref="IntentSource.Idle"/> masks it, and the value is consumed once (a driver
    /// republishes each tick, so a missed producer tick can never leave a stale entity moving forever). All axes are
    /// clamped to a physical stick's <c>[-1, 1]</c> range.</summary>
    /// <param name="intent">The fixed-point movement and action image to consume when no tape owns this step.</param>
    public void SubmitIntent(in PlayerIntent intent) {
        m_submittedIntent = Clamped(intent: in intent);
        m_hasSubmittedIntent = true;
    }
    /// <summary>Stages one deterministic producer intent for the next <see cref="Advance"/> — the producer tier below
    /// the submitted stream, used only while <see cref="Source"/> names its producer
    /// by the source's authored producer program. One-tick, consumed like the submitted
    /// image; same clamps.</summary>
    /// <param name="intent">The producer's fixed-point movement and action image.</param>
    public void StageProducerIntent(in PlayerIntent intent) {
        m_producerIntent = Clamped(intent: in intent);
        m_hasProducerIntent = true;
    }
    // The shared stick-range clamp both one-tick images pass through — the six movement-role ordinals only;
    // composition ordinals are validated {0, One} at their own doors (the affordance gate, the pump) and pass through
    // unchanged here. [-One, One] is safe here because every role channel IS bipolar by validator rule
    // (WorldDefinitionValidator.ValidateChannels refuses any other declared shape on a role channel).
    private PlayerIntent Clamped(in PlayerIntent intent) {
        var result = intent;

        result = ClampRole(intent: result, role: ChannelRole.MoveForward);
        result = ClampRole(intent: result, role: ChannelRole.MoveStrafe);
        result = ClampRole(intent: result, role: ChannelRole.Turn);
        result = ClampRole(intent: result, role: ChannelRole.MoveUp);
        result = ClampRole(intent: result, role: ChannelRole.Pitch);
        result = ClampRole(intent: result, role: ChannelRole.Roll);

        return result;
    }

    private PlayerIntent ClampRole(PlayerIntent intent, ChannelRole role) {
        var ordinal = m_roleOrdinals[role];

        return ((ordinal >= 0)
            ? intent.WithChannel(ordinal: ordinal, value: FixedQ4816.Clamp(value: intent[ordinal], minimum: s_negativeOne, maximum: FixedQ4816.One))
            : intent);
    }
    /// <summary>Stages this tick's live-held device channel image — the action overlay a held jump button rides onto a
    /// tape-driven runner. Only composition ordinals are meaningful; movement-role ordinals ride the submitted intent
    /// directly. One-tick, consumed by the next <see cref="Advance"/>; the client republishes it each
    /// submission, and the merge admits it only under <see cref="IntentSource.Live"/>.</summary>
    /// <param name="channels">The channel image live-held this tick (composition ordinals only).</param>
    public void SetHeldChannels(in PlayerIntent channels) {
        m_transferHeldChannels = default;
        m_hasTransferHeldChannels = false;
        m_heldChannels = channels;
    }

    internal WorldSubmittedInput TakeSubmittedInput() {
        var input = new WorldSubmittedInput(HasIntent: m_hasSubmittedIntent, Intent: m_submittedIntent, HeldChannels: m_heldChannels);

        m_submittedIntent = default;
        m_hasSubmittedIntent = false;
        m_heldChannels = default;

        return input;
    }

    internal void RestoreSubmittedInput(in WorldSubmittedInput input) {
        if (input.HasIntent) {
            SubmitIntent(intent: input.Intent);
        }

        // This is the input-hold runtime replaying its selected historical image, not a new writer publication.
        // Do not clear the authority-handoff bridge here: only ApplyIntentSubmission's SetHeldChannels call proves
        // the destination stream has actually supplied a replacement image (neutral included).
        m_heldChannels = input.HeldChannels;
    }
    /// <summary>Gets the held-channel overlay admitted by the last <see cref="Advance"/>, retained only for
    /// <c>player.channels</c>.</summary>
    public PlayerIntent ChannelReadHeld => m_channelReadHeld;
    /// <summary>Gets the last intent after the admitted held overlay composed with the movement tier, retained only for
    /// <c>player.channels</c>.</summary>
    public PlayerIntent ChannelReadComposed => m_channelReadComposed;
    /// <summary>Enqueues a timed scripted segment onto the tape: while it is live it drives the avatar with
    /// <paramref name="intent"/>, overriding the held keys (or, on a population entry, its wander), for
    /// <paramref name="seconds"/> of advance time. All six channels are clamped to <c>[-1, 1]</c> — the planar three
    /// three leave the 6DOF three at their zero default, and <c>player.fly</c>'s full six carry all of them.
    /// A non-positive duration is ignored.</summary>
    /// <param name="intent">The intent the segment holds while live.</param>
    /// <param name="seconds">How long (advance seconds) the segment drives before it expires.</param>
    public void EnqueueRun(PlayerIntent intent, float seconds) {
        var duration = FixedQ4816.FromDouble(value: seconds);

        if (duration <= FixedQ4816.Zero) {
            return;
        }

        // Grow the ring (doubling) before writing when it is full, so a burst never drops a segment; steady-state this
        // branch never fires, so the enqueue allocates nothing.
        if (m_tapeCount == m_tape.Length) {
            GrowTape();
        }

        var tail = ((m_tapeHead + m_tapeCount) % m_tape.Length);

        m_tape[tail] = new TapeSegment {
            Intent = Clamped(intent: in intent),
            RemainingTicks = FixedTickConversion.DurationEngineTicks(seconds: duration),
        };
        m_tapeCount++;
    }
    /// <summary>Clears the scripted tape, dropping every queued segment. The held keys (if any) resume driving.</summary>
    public void ClearTape() {
        // Drop the live range without releasing the ring's backing array — the slots are struct storage, reused next
        // enqueue. The stale segment structs are never read while m_tapeCount is 0.
        m_tapeHead = 0;
        m_tapeCount = 0;
    }
    /// <summary>Teleports the avatar to a full 6DOF pose — a free position and a Tait-Bryan attitude (yaw about world up,
    /// pitch about the body right, roll about the body forward). A hard teleport pops: the previous-pose anchor is reset to the new pose so the renderer never
    /// interpolates across the jump, and any in-flight <see cref="Reconcile"/> smoothing offset is dropped. The pose is
    /// written as-is regardless of model; a grounded entity's next <see cref="Advance"/> re-pins Y and levels the
    /// attitude to its yaw, so a full pose only persists under the free model.</summary>
    /// <param name="x">The world X coordinate.</param>
    /// <param name="y">The world Y coordinate.</param>
    /// <param name="z">The world Z coordinate.</param>
    /// <param name="yawRadians">The yaw about world up, radians.</param>
    /// <param name="pitchRadians">The pitch about the body right, radians.</param>
    /// <param name="rollRadians">The roll about the body forward, radians.</param>
    public void Pose(float x, float y, float z, float yawRadians, float pitchRadians, float rollRadians) {
        Pose(
            position: new FixedVector3(
                X: FixedQ4816.FromDouble(value: x),
                Y: FixedQ4816.FromDouble(value: y),
                Z: FixedQ4816.FromDouble(value: z)
            ),
            yawRadians: FixedQ4816.FromDouble(value: yawRadians),
            pitchRadians: FixedQ4816.FromDouble(value: pitchRadians),
            rollRadians: FixedQ4816.FromDouble(value: rollRadians)
        );
    }
    /// <summary>Teleports to a full pose already expressed in deterministic numerics.</summary>
    public void Pose(FixedVector3 position, FixedQ4816 yawRadians, FixedQ4816 pitchRadians, FixedQ4816 rollRadians) {
        m_position = position;
        m_yaw = yawRadians;
        // The vehicle pitch scalar mirrors the posed pitch inside its own clamp, so the next vehicle frame rebuilds
        // an equivalent (never inverted) attitude from its scalars.
        m_vehiclePitch = FixedQ4816.Clamp(value: pitchRadians, minimum: -s_maxVehiclePitch, maximum: s_maxVehiclePitch);
        m_orientation = OrientationFromEuler(yaw: m_yaw, pitch: pitchRadians, roll: rollRadians);
        CommitTeleport();
        m_continuity = EntityContinuity.Teleport;
    }

    /// <summary>Applies one deterministic body-contact depenetration without turning it into a teleport.</summary>
    internal void ApplyDynamicContact(FixedVector3 correction) {
        if (correction == FixedVector3.Zero) {
            return;
        }

        m_position += correction;
        var normal = correction.Normalize();
        var velocity = (m_planarVelocity + (s_unitY * m_verticalVelocity));
        var inward = FixedVector3.Dot(left: velocity, right: normal);
        if (inward < FixedQ4816.Zero) {
            velocity -= (normal * inward);
            m_planarVelocity = new FixedVector3(X: velocity.X, Y: FixedQ4816.Zero, Z: velocity.Z);
            if (m_verticalVelocity != velocity.Y) {
                m_verticalVelocity = velocity.Y;
                m_verticalVelocityAccumulator.Reset();
            }
        }
    }

    /// <summary>The subset of a body's own dynamic state that is perceivable — the in-flight rule docs/world-model.md's
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
            laneRecency[lane] = (m_laneActions[lane].Recency is { } recency) ? [.. recency] : null;
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
            HeldChannelImage: (m_hasTransferHeldChannels ? m_transferHeldChannels : m_channelReadHeld),
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
            PendingContinuum: m_pendingContinuum);
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
        var timedCount = Math.Min(val1: Math.Min(val1: ticks.Length, val2: values.Length), val2: ActionLaneCount);

        for (var ordinal = 0; (ordinal < timedCount); ordinal++) {
            m_laneTimers[ordinal] = ticks[ordinal];
            m_channelTimerValues[ordinal] = values[ordinal];
        }

        CopyClamped(source: state.PreviousChannelBit, destination: m_previousChannelBit);
        m_transferHeldChannels = state.HeldChannelImage;
        m_hasTransferHeldChannels = (state.HeldChannelImage != default);
        CopyClamped(source: state.PendingDefaultChannelPress, destination: m_pendingDefaultChannelPress);
        CopyClamped(source: state.PendingDefaultChannelValue, destination: m_pendingDefaultChannelValue);
        CopyClamped(source: state.MotionRecency, destination: m_motionRecency);

        // The rate accumulators' own remainders — FromRemainder never throws here: a captured remainder was always
        // read off a LIVE accumulator bound to this SAME EngineTicksPerSecond base, whose own Integrate contract
        // already guarantees |remainder| < ticksPerSecond (Puck.Maths.FixedRateAccumulator's own invariant).
        m_planarRampAccumulator = FixedRateAccumulator.FromRemainder(remainder: state.PlanarRampRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_vehicleLongAccumulator = FixedRateAccumulator.FromRemainder(remainder: state.VehicleLongRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_vehicleLatAccumulator = FixedRateAccumulator.FromRemainder(remainder: state.VehicleLatRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_vehicleResidualAccumulator = FixedRateAccumulator.FromRemainder(remainder: state.VehicleResidualRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_swimThrustRampAccumulator = FixedRateAccumulator.FromRemainder(remainder: state.SwimThrustRampRemainder, ticksPerSecond: EngineTicksPerSecond);
        m_overlayAccumulator = FixedVector3RateAccumulator.FromRemainders(xRemainder: state.OverlayRemainderX, yRemainder: state.OverlayRemainderY, zRemainder: state.OverlayRemainderZ, ticksPerSecond: EngineTicksPerSecond);

        var laneCount = Math.Min(val1: Math.Min(val1: state.LaneLatch.Length, val2: state.LaneFactHeld.Length), val2: ActionLaneCount);

        for (var lane = 0; (lane < laneCount); lane++) {
            m_laneActions[lane].Latch = state.LaneLatch[lane];
            m_laneActions[lane].FactHeld = state.LaneFactHeld[lane];

            if ((lane < state.LaneRecency.Length) && (state.LaneRecency[lane] is { } capturedRecency) && (m_laneActions[lane].Recency is { } targetRecency)) {
                CopyClamped(source: capturedRecency, destination: targetRecency);
            }
        }

        var actionStateCount = Math.Min(val1: Math.Min(val1: state.ActionStateValues.Length, val2: state.ActionStateTimers.Length), val2: m_actionStateDefinitions.Length);

        for (var slot = 0; (slot < actionStateCount); slot++) {
            m_actionStateValues[slot] = state.ActionStateValues[slot];
            m_actionStateTimers[slot] = state.ActionStateTimers[slot];
        }

        var dirtyCount = Math.Min(val1: Math.Min(val1: state.ActionStateDirty.Length, val2: state.ActionStateDirtyKind.Length), val2: Math.Min(val1: state.ActionStateDirtyOperand.Length, val2: m_actionStateDefinitions.Length));

        for (var slot = 0; (slot < dirtyCount); slot++) {
            m_actionStateDirty[slot] = state.ActionStateDirty[slot];
            m_actionStateDirtyKind[slot] = state.ActionStateDirtyKind[slot];
            m_actionStateDirtyOperand[slot] = state.ActionStateDirtyOperand[slot];
        }

        var durableCount = Math.Min(val1: Math.Min(val1: state.DurableInputPresent.Length, val2: state.DurableInputValues.Length), val2: Math.Min(val1: Math.Min(val1: state.DurableInputTimers.Length, val2: state.DurableInputWriters.Length), val2: m_durableInputPresent.Length));

        for (var slot = 0; (slot < durableCount); slot++) {
            m_durableInputPresent[slot] = state.DurableInputPresent[slot];
            m_durableInputValues[slot] = state.DurableInputValues[slot];
            m_durableInputTimers[slot] = state.DurableInputTimers[slot];
            m_durableInputWriters[slot] = state.DurableInputWriters[slot];
        }

        m_durableInputTick = state.DurableInputTick;

        RestoreTape(intents: state.TapeIntents, remainingTicks: state.TapeRemainingTicks);
    }

    /// <summary>Overrides just this body's own linear velocity — the mapped-arrival half of a portal transfer (see
    /// <c>Puck.World.WorldPlacementPortal.Arrival</c>): the source's captured velocity, rotated into the
    /// destination's own frame by <c>Puck.World.Server.WorldPortalArrivalMath</c>, written after
    /// <see cref="Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>'s own hard-teleport commit — the same
    /// "AFTER Pose, never before" ordering <see cref="ApplyTransferState"/> follows, so the discontinuity has already
    /// reset <see cref="FixedPreviousPosition"/> before this runs. Deliberately narrower than
    /// <see cref="ApplyTransferState"/>: the destination's own normal join already embodied the traveler fresh under
    /// its own kit (appearance, grants, action-track state, body motion program) — mapped arrival carries across
    /// only positional continuity, pose and velocity, never the source kit's dash overlay, timers, or tape.</summary>
    /// <param name="planarVelocity">The rotated planar velocity.</param>
    /// <param name="verticalVelocity">The (rotation-invariant) vertical velocity.</param>
    public void SetArrivalVelocity(FixedVector3 planarVelocity, FixedQ4816 verticalVelocity) {
        m_planarVelocity = planarVelocity;
        m_verticalVelocity = verticalVelocity;
    }

    /// <summary>Installs an adjacency arrival's already-evaluated motion segment and resolves it through this
    /// authority's own contact field. No input, action, timer, gravity, or motion-program operation is evaluated.</summary>
    public void ApplyContinuumTrajectory(in WorldContinuumTrajectory trajectory, int entityIndex, ulong destinationCompletedEngineTick) {
        var next = m_position;
        var velocity = (m_planarVelocity + (s_unitY * m_verticalVelocity));
        var resolution = default(ContactResolution);

        if ((m_contactField is { } field) && (m_collider is { } collider)) {
            resolution = ((field is IEntityContactField entityField)
                ? entityField.ResolveEntitySweep(entityIndex: entityIndex, previousPosition: trajectory.PreviousPosition, position: ref next, velocity: ref velocity, orientation: in m_orientation, volumes: collider.Volumes)
                : field.ResolveSweep(previousPosition: trajectory.PreviousPosition, position: ref next, velocity: ref velocity, orientation: in m_orientation, volumes: collider.Volumes));
        }

        m_previousPosition = trajectory.PreviousPosition;
        m_position = next;
        m_planarVelocity = new FixedVector3(X: velocity.X, Y: FixedQ4816.Zero, Z: velocity.Z);
        m_verticalVelocity = velocity.Y;
        m_grounded = resolution.Grounded;
        m_lastContactCount = (resolution.Grounded ? 1 : 0);
        var consumedThrough = Math.Max(trajectory.ConsumedThroughEngineTick, destinationCompletedEngineTick);
        m_pendingContinuum = trajectory with { ConsumedThroughEngineTick = consumedThrough };
        m_continuumConsumedThroughEngineTick = consumedThrough;
        m_ordinaryAdvanceAdmitted = false;
    }

    /// <summary>Stops an exhausted continuum at the last confirmed ownership face. Tangential momentum survives;
    /// only velocity trying to leave this owner is removed.</summary>
    public void ClampContinuum(in WorldFaceFrame frame, FixedQ4816 seamU, FixedQ4816 seamV) {
        var inward = FixedQ4816.FromRawBits(value: 1L);
        m_position = (frame.PointAt(u: seamU, v: seamV) - (frame.Normal * inward));
        m_previousPosition = m_position;
        var velocity = (m_planarVelocity + (s_unitY * m_verticalVelocity));
        var outward = FixedVector3.Dot(left: velocity, right: frame.Normal);
        if (outward > FixedQ4816.Zero) {
            velocity -= (frame.Normal * outward);
            m_planarVelocity = new FixedVector3(X: velocity.X, Y: FixedQ4816.Zero, Z: velocity.Z);
            m_verticalVelocity = velocity.Y;
        }
        m_positionAccumulator.Reset();
        m_pendingContinuum = null;
    }

    /// <summary>Restores the named action-edge/register subset that must remain continuous when exactly one writer
    /// hands this body to another authority. Destination names and kinds are authoritative; unknown rows are ignored
    /// and admitted values are clamped through the destination's own envelope.</summary>
    public void ApplyTransferActionContinuity(WorldTransferActionContinuity continuity, WorldChannelTable channels) {
        ArgumentNullException.ThrowIfNull(continuity);
        ArgumentNullException.ThrowIfNull(channels);

        var held = default(PlayerIntent);
        foreach (var channel in continuity.Channels) {
            if (channels.TryGetOrdinal(name: channel.Name, ordinal: out var ordinal) && ((uint)ordinal < (uint)m_previousChannelBit.Length)) {
                m_previousChannelBit[ordinal] = channel.PreviousBit;
                held = held.WithChannel(ordinal: ordinal, value: channel.HeldValue);
            }
        }

        m_transferHeldChannels = held;
        m_hasTransferHeldChannels = (held != default);

        foreach (var register in continuity.Registers) {
            for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
                var definition = m_actionStateDefinitions[slot];
                if (!string.Equals(a: definition.Name, b: register.Name, comparisonType: StringComparison.Ordinal) || (definition.Kind != register.Kind)) {
                    continue;
                }

                if (definition.Kind == ActionStateKind.Counter) {
                    var raw = register.Value.Value;
                    var initial = definition.InitialValue.Value;
                    m_actionStateValues[slot] = new FixedQ4816(definition.Envelope?.Clamp(value: raw, initial: initial) ?? raw);
                } else {
                    var raw = unchecked((long)register.TimerTicks);
                    var initial = unchecked((long)definition.InitialTicks);
                    var admitted = definition.Envelope?.Clamp(value: raw, initial: initial) ?? raw;
                    m_actionStateTimers[slot] = unchecked((ulong)Math.Max(val1: admitted, val2: 0L));
                }
                break;
            }
        }
    }

    // The shared clamped-copy every array field ApplyTransferState restores uses: never trusts the captured array's
    // length to match the restored body's own (a defensive habit, not a load-bearing one — the SAME seat kit always
    // produces the SAME lengths for a same-process abort/restore).
    private static void CopyClamped<T>(T[] source, T[] destination) {
        var count = Math.Min(val1: source.Length, val2: destination.Length);

        for (var index = 0; (index < count); index++) {
            destination[index] = source[index];
        }
    }

    // Rewrites the WHOLE tape ring from a captured FIFO snapshot — the abort-restore counterpart to
    // CaptureTransferState's own tape read. Bypasses EnqueueRun's seconds-based API deliberately: re-deriving ticks
    // from a seconds-rounded value would drift the restored segment's remaining duration from the exact tick count
    // that was live at capture. Always called on a freshly constructed restore body (RestoreDetachedSeat never calls
    // ApplyTransferState on a live one), so there is never an existing segment to preserve or lose.
    private void RestoreTape(PlayerIntent[] intents, ulong[] remainingTicks) {
        var count = Math.Min(val1: intents.Length, val2: remainingTicks.Length);

        while (m_tape.Length < count) {
            GrowTape();
        }

        m_tapeHead = 0;
        m_tapeCount = count;

        for (var index = 0; (index < count); index++) {
            m_tape[index] = new TapeSegment { Intent = intents[index], RemainingTicks = remainingTicks[index] };
        }
    }

    /// <summary>Sets the entity's named body motion program as an authoritative pose switch.</summary>
    /// <param name="programName">The declared program name.</param>
    /// <returns><see langword="true"/> when the program exists.</returns>
    public bool SetBodyMotionProgram(string programName) {
        if (!m_bodyMotionPrograms.TryGetValue(key: programName, value: out var program)) {
            return false;
        }

        SetBodyMotionProgram(program: program);
        return true;
    }

    private void SetBodyMotionProgram(CompiledBodyMotionProgram program) {
        if (string.Equals(a: program.Name, b: m_bodyMotionProgram.Name, comparisonType: StringComparison.Ordinal)) {
            m_bodyMotionProgram = program;
            return;
        }

        m_bodyMotionProgram = program;
        // A yaw-scalar program (grounded's frame, or the vehicle frame — which levels its pitch scalar too) re-pins
        // the attitude from the extracted heading; the free 6DOF program keeps the attitude and re-seeds the scalar.
        var resolvesYawAttitude = (program.Contains(operation: BodyMotionOp.ResolveYawAttitudeAndPlanarFrame)
            || program.Contains(operation: BodyMotionOp.ResolveVehicleFrame));

        if (resolvesYawAttitude) {
            m_yaw = ExtractYaw(orientation: m_orientation);
            m_vehiclePitch = FixedQ4816.Zero;
            m_orientation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw);
        } else if (program.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)) {
            m_yaw = ExtractYaw(orientation: m_orientation);
        }

        m_motionRecency = ((m_tuning.RecencySlots > 0) ? new ulong[m_tuning.RecencySlots] : []);
        // A program that lacks the surface stage must not leave stale medium facts behind — a swim→other switch
        // clears them here; the swim program rewrites them next tick if the switch lands back on it.
        m_submerged = false;
        m_atSurface = false;
        CommitTeleport(resetVertical: program.OwnsVerticalContactState);
        m_continuity = EntityContinuity.Teleport;
    }
    /// <summary>Sets the intent-source axis — <c>player.control</c>'s write and the peer sweep's per-entity half. A
    /// transition drops the staged transient input images (the submitted, producer, and held-lane images), so a stale
    /// image cannot leak across the switch and nothing bursts when a source returns; a seat's client half drops its own
    /// held device state in the same command. The tape and any wire-timed lane press are untouched. A no-op if the
    /// source is unchanged.</summary>
    /// <param name="source">The intent source to latch.</param>
    public void SetIntentSource(IntentSource source) {
        if (source == m_source) {
            return;
        }

        m_source = source;
        ClearTransientInput();
    }
    /// <summary>Sets the screen-engagement latch — the engagement route's write. A transition in either direction drops
    /// the staged transient input images and clears the last routed intent, so a stale image cannot leak as a stuck
    /// direction into the machine (engaging) or burst the avatar into motion (disengaging); the client seat drops its
    /// own held device state in the same operation. The tape and any wire-timed lane press are untouched — a scripted
    /// tape keeps driving whichever target now owns the intent. A no-op if the latch is unchanged.</summary>
    /// <param name="engaged">Whether the player is engaged on a screen (its intent diverted to the screen's machine).</param>
    public void SetEngaged(bool engaged) {
        if (engaged == m_engaged) {
            return;
        }

        m_engaged = engaged;
        m_engagedIntent = default;
        ClearTransientInput();
    }
    /// <summary>Applies a server correction: the sim pose snaps to the target instantly (an end-state identical to
    /// <see cref="Pose(float, float, float, float, float, float)"/>), and the tick's snapshot carries
    /// <see cref="EntityContinuityKind.Correction"/> so the client eases its render error to zero over
    /// <paramref name="seconds"/>. Snap escape: if the position error exceeds
    /// the world's <see cref="WorldMotionDefaults.MaxSmoothError"/> the snapshot reports a plain teleport instead, so a huge
    /// correction pops. Easing is client presentation state only — the sim never reads it and <c>player.where</c>
    /// never includes it.</summary>
    /// <param name="x">The authoritative world X coordinate.</param>
    /// <param name="z">The authoritative world Z coordinate.</param>
    /// <param name="yawRadians">The authoritative heading in radians (0 = facing -Z).</param>
    /// <param name="seconds">The smoothing window over which the client's render error eases to zero.</param>
    /// <returns>The continuity kind published for the correction.</returns>
    public EntityContinuityKind Reconcile(float x, float z, float yawRadians, float seconds) {
        var oldPosition = m_position;

        // Snap the sim pose immediately — the same end-state a Warp+Face would land (a planar, levelled correction).
        // The tape and any timed press are untouched: a correction is authority over the pose, not over what the
        // entity is trying to do.
        var fixedYaw = FixedQ4816.FromDouble(value: yawRadians);

        m_position = new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: oldPosition.Y,
            Z: FixedQ4816.FromDouble(value: z)
        );
        m_yaw = fixedYaw;
        m_vehiclePitch = FixedQ4816.Zero;
        m_orientation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: fixedYaw);
        CommitTeleport();

        // Over the ceiling the correction pops (a respawn/teleporter-scale jump would streak if eased); within it the
        // client eases from its previous rendered pose to authority over the window.
        var positionError = (oldPosition - m_position);

        m_continuity = ((positionError.Length > m_maxSmoothError)
            ? EntityContinuity.Teleport
            : EntityContinuity.Correction(seconds: seconds));

        return m_continuity.Kind;
    }
    /// <summary>Clears every intent producer this body owns: drops the whole tape, the staged transient input images,
    /// every in-flight timed press (<c>player.press</c> hold), and any not-yet-materialized argument-less tap staged
    /// by <see cref="PressChannel(int, FixedQ4816)"/> (see <see cref="MaterializeDefaultLanePresses"/>) — on role and
    /// composition ordinals alike, in every one of these three forms. Not an instantaneous halt — an in-flight jump
    /// arc still resolves under gravity and lands, and the ramped planar velocity decays to rest through the
    /// response table rather than snapping to zero. This is the <c>player.stop</c> panic verb's server half; the
    /// client seat drops its held device state in the same command. Unlike <see cref="SetIntentSource"/>/
    /// <see cref="SetEngaged"/>'s shared <see cref="ClearTransientInput"/> call, which deliberately leaves a timed
    /// press running across a source/engagement transition (that hold still belongs to whichever target now owns
    /// the intent — see its own remarks), Stop is the panic verb: a 60-second throttle hold left ticking after it
    /// would make "keys released" a lie.</summary>
    /// <returns>How many held channels were released and how many timed presses — materialized or still pending —
    /// were cancelled. The synchronous read-back <c>player.stop</c>'s handler quotes in its echo.</returns>
    public StopOutcome Stop() {
        ClearTape();

        var releasedHeldChannels = 0;

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_heldChannels[ordinal] != FixedQ4816.Zero) {
                releasedHeldChannels++;
            }
        }

        // An in-flight jump arc still resolves under gravity and lands — clearing the images only stops new input.
        ClearTransientInput();

        var clearedTimedPresses = 0;

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_laneTimers[ordinal] > 0) {
                clearedTimedPresses++;
                m_laneTimers[ordinal] = 0;
                m_channelTimerValues[ordinal] = default;
            }

            // A player.press with no holdSeconds hasn't materialized into a lane timer yet (MaterializeDefaultLanePresses
            // only runs at the next Advance) — panic-verb totality means this pending tap is cleared too, and counted
            // the same as an already-materialized one.
            if (m_pendingDefaultChannelPress[ordinal]) {
                clearedTimedPresses++;
                m_pendingDefaultChannelPress[ordinal] = false;
                m_pendingDefaultChannelValue[ordinal] = default;
            }
        }

        return new StopOutcome(ReleasedHeldChannels: releasedHeldChannels, ClearedTimedPresses: clearedTimedPresses);
    }

    // Drop the staged one-tick input images (the submitted, producer, and held-channel images) — the source/engagement
    // transition hygiene. The tape and any timed channel press are left running — deliberate for a source/engagement
    // switch (the hold belongs to whichever target now owns the intent), but NOT for Stop, which clears timed presses
    // itself right after calling this (see Stop's own remarks).
    private void ClearTransientInput() {
        m_submittedIntent = default;
        m_hasSubmittedIntent = false;
        m_producerIntent = default;
        m_hasProducerIntent = false;
        m_heldChannels = default;
        m_transferHeldChannels = default;
        m_hasTransferHeldChannels = false;
    }

    /// <summary>Consumes this tick's continuity hint for the snapshot: how the pose changed (ordinary integration, a
    /// hard teleport, or a smoothed correction). Resets to <see cref="EntityContinuityKind.Continuous"/>; within a tick
    /// the last authoritative write wins.</summary>
    public EntityContinuity TakeContinuity() {
        var continuity = m_continuity;

        m_continuity = EntityContinuity.Continuous;

        return continuity;
    }

    /// <summary>Reads this tick's continuity hint without consuming it — the non-consuming counterpart to
    /// <see cref="TakeContinuity"/> for a primer snapshot built for a newly attached sink
    /// (<c>WorldServer.AttachSink</c>/<c>BuildPrimerSnapshot</c>): a late attach must never steal the one-shot flag an
    /// already-attached sink is still due to observe via <see cref="TakeContinuity"/> on the ordinary next-tick
    /// broadcast.</summary>
    public EntityContinuity PeekContinuity() => m_continuity;

    /// <summary>Formats the avatar's planar pose for the roster's <c>world.players</c> glance — position X/Z and heading,
    /// culture-invariant. The full 6DOF pose is <see cref="DescribeWhere"/>.</summary>
    /// <returns>A line of the form <c>pos=(x.xx, z.zz) yaw=ddd°</c>.</returns>
    public string DescribePose() {
        var position = m_position.ToVector3();

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"pos=({position.X:0.00}, {position.Z:0.00}) yaw={CompassDegrees(radians: EulerRadians().Yaw):0}°"
        );
    }
    /// <summary>Formats the standalone <c>player.where</c> echo — the bracket-tagged, index-prefixed line a piped run
    /// asserts against — as the full 6DOF pose:
    /// <c>[player.where: p{N} pos=(x.xx, y.yy, z.zz) yaw=ddd° pitch=ddd° roll=ddd°]</c>. One format always. A grounded
    /// entity keeps a canonical level orientation — <c>pitch=0 roll=0</c> — while <c>y</c> is its resolved ground foot
    /// point (<c>0.00</c> on the flat plane, following the contact field where solids lift it). The bare planar
    /// fragment is <see cref="DescribePose"/>.</summary>
    /// <param name="index">The 1-based player display index to tag the line with.</param>
    /// <returns>The full bracketed <c>player.where</c> echo line.</returns>
    public string DescribeWhere(int index) {
        var (yaw, pitch, roll) = EulerRadians();
        var position = m_position.ToVector3();

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[player.where: p{index} pos=({position.X:0.00}, {position.Y:0.00}, {position.Z:0.00}) yaw={CompassDegrees(radians: yaw):0}° pitch={CompassDegrees(radians: pitch):0}° roll={CompassDegrees(radians: roll):0}°]"
        );
    }

    // An angle in radians normalized into [0, 360) degrees, so an echo is a stable compass reading (a -10° pitch reads
    // as 350°, level as 0°).
    private static float CompassDegrees(float radians) {
        var degrees = (radians * (180f / MathF.PI));

        return (degrees - (360f * MathF.Floor(x: (degrees / 360f))));
    }

    // The canonical orientation decomposed to Tait-Bryan angles (radians), the exact inverse of OrientationFromEuler's
    // Ry(yaw)·Rx(pitch)·Rz(roll) construction (the codebase-wide yaw-about-+Y / pitch-about-+X / roll-about-+Z
    // convention). Yaw is atan2 of the facing's horizontal components; pitch is the facing's elevation; roll is the bank
    // read from the body right/up vectors' vertical parts. A pure-yaw attitude yields pitch = roll = 0.
    private (float Yaw, float Pitch, float Roll) EulerRadians() {
        var orientation = m_orientation.ToQuaternion();
        var forward = Vector3.Transform(value: -Vector3.UnitZ, rotation: orientation);
        var up = Vector3.Transform(value: Vector3.UnitY, rotation: orientation);
        var right = Vector3.Transform(value: Vector3.UnitX, rotation: orientation);
        var yaw = MathF.Atan2(y: -forward.X, x: -forward.Z);
        var pitch = MathF.Asin(x: Math.Clamp(value: forward.Y, min: -1f, max: 1f));
        var roll = MathF.Atan2(y: right.Y, x: up.Y);

        return (Yaw: yaw, Pitch: pitch, Roll: roll);
    }

    // Build a canonical orientation from Tait-Bryan angles (radians): yaw about world up (+Y), then pitch about the body
    // right (+X), then roll about the body forward (+Z) — the codebase-wide convention, the exact inverse EulerRadians
    // decomposes. Roll is about local +Z uniformly here and in the free integrator, so the pose set by player.pose and
    // the attitude flown by player.fly share one sign convention.
    private static FixedQuaternion OrientationFromEuler(FixedQ4816 yaw, FixedQ4816 pitch, FixedQ4816 roll) {
        return ((FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: yaw)
            * FixedQuaternion.FromAxisAngle(axis: s_unitX, angle: pitch))
            * FixedQuaternion.FromAxisAngle(axis: s_unitZ, angle: roll)).Normalize();
    }
    private static FixedQ4816 ExtractYaw(FixedQuaternion orientation) {
        var forward = orientation.Rotate(vector: -s_unitZ);

        return FixedQ4816.Atan2(y: -forward.X, x: -forward.Z);
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

    // Grow the tape ring to twice its capacity, copying the live segments into FIFO order from index 0 (head reset).
    // Only when a burst would exceed the current slots; amortized O(1).
    private void GrowTape() {
        var grown = new TapeSegment[(m_tape.Length * 2)];

        for (var offset = 0; (offset < m_tapeCount); offset++) {
            grown[offset] = m_tape[((m_tapeHead + offset) % m_tape.Length)];
        }

        m_tape = grown;
        m_tapeHead = 0;
    }

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
    /// <see cref="Puck.World.Server.WorldEngagement.Engage"/> afterward, through the same authority path a manual
    /// <c>player.engage</c> takes.</param>
    /// <param name="entityIndex">The source body's population index.</param>
    /// <param name="effectTargets">The pre-step entity target image.</param>
    /// <param name="effectOutputs">Receives non-self effects for post-advance application.</param>
    /// <param name="designationOutputs">Receives authored target-register submissions.</param>
    /// <param name="generatorInvocations">Receives staged <c>generate</c> effect firings, enqueued through the
    /// ordinary mutation pipeline after the whole population advance.</param>
    /// <returns><see langword="true"/> when <paramref name="engageProbeOrdinal"/>'s rising edge fired this tick
    /// (the caller should engage); otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is zero.</exception>
    internal bool Advance(ulong tick, ulong stepTicks, int? engageProbeOrdinal = null, int entityIndex = -1, BodyEffectTargets effectTargets = default, List<BodyEffectOutput>? effectOutputs = null, List<WorldDesignation>? designationOutputs = null, List<WorldGeneratorInvocation>? generatorInvocations = null) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);

        // Captured before ExecuteProgram (or the overlay add below) can move m_position — the swept portal-crossing
        // scan's segment start for this step. A hard teleport between scans overwrites this separately (CommitTeleport).
        m_previousPosition = m_position;

        ApplyDurableInput(tick: tick);
        MaterializeDefaultLanePresses(stepTicks: stepTicks);

        // The full merged intent for this sub-step: NextIntent expresses the whole precedence (movement channels —
        // tape > submitted, gated by the possession latch — with the action-track lanes overlaid).
        var intent = NextIntent(stepTicks: stepTicks);

        // Captured EVERY Advance, regardless of capture policy — the context-routes widening's mirror form
        // (capture:false) needs this body's resolved intent for its route's translation/passthrough even while the
        // avatar keeps integrating below. Reading it costs nothing beyond a struct copy already computed above.
        m_engagedIntent = intent;

        // The SAME edge test ProcessLaneActions uses below (bit crossing the ordinal's threshold, previous tick's bit
        // clear) — computed here, ahead of integration, so a fired edge can preempt it entirely rather than firing a
        // bound action (a jump) the same tick it engages.
        var engageEdge = ((engageProbeOrdinal is { } probeOrdinal)
            && (intent[probeOrdinal] >= m_channelThresholds[probeOrdinal])
            && !m_previousChannelBit[probeOrdinal]);

        if (m_engaged || engageEdge) {
            // Captured (m_engaged, i.e. capture:true), OR this tick's press is being diverted into an engage instead
            // of reaching the body (engageEdge): either way the resolved intent never reaches the avatar (no pose
            // integration, so the snapshot holds it stable). The action track below still advances, so a timed press
            // drains identically whether the intent drives the avatar or the route target.
        } else {
            // moveSpeed goes through ResolveMoveSpeed's per-arm dispatch — grounded reads the rate live off the
            // seated profile every frame (an identity.motion edit is real-time; a profileless stand-in falls back to the
            // tuning's speed), clamped by the kit's own authored MoveSpeedEnvelope when declared; vehicle
            // deliberately never reads the profile and instead clamps the kit's OWN TopSpeed through its
            // TopSpeedEnvelope. Either arm, the clamp lands BEFORE ExecuteProgram ever sees the value, so the sim
            // never observes an unclamped speed, and EffectiveMoveSpeed's read-back echo performs the SAME resolve.
            // No envelope (the default) is a no-op clamp elided entirely: today's behavior, byte-identical.
            var moveSpeed = ResolveMoveSpeed();
            var turnSpeed = (Profile?.FixedTurnSpeed ?? m_tuning.TurnSpeed);

            ExecuteProgram(intent: intent, moveSpeed: moveSpeed, turnSpeed: turnSpeed, stepTicks: stepTicks, entityIndex: entityIndex, effectTargets: effectTargets, effectOutputs: effectOutputs, designationOutputs: designationOutputs, generatorInvocations: generatorInvocations);

            // The timed impulse overlay rides after the selected program, through its own accumulator.
            if (m_overlayRemaining > 0) {
                var overlayTicks = Math.Min(val1: stepTicks, val2: m_overlayRemaining);

                m_position += m_overlayAccumulator.Integrate(ratePerSecond: m_overlayVelocity, elapsedTicks: overlayTicks);
                m_overlayRemaining -= overlayTicks;

                if (m_overlayRemaining == 0) {
                    m_overlayVelocity = default;
                    m_overlayAccumulator.Reset();
                }
            }
        }

        // The previous-bit image is written once after action evaluation; timed presses drain even under capture.
        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            m_previousChannelBit[ordinal] = (intent[ordinal] >= m_channelThresholds[ordinal]);
        }

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            m_laneTimers[lane] = SubtractSaturating(value: m_laneTimers[lane], amount: stepTicks);
        }

        // The held-channel image is a one-tick publish, like the submitted intent: the client republishes it every
        // submission, so a missed tick reads no channels rather than a stale hold.
        m_heldChannels = default;
        m_affectingSubject = -1;

        return engageEdge;
    }

    private void ExecuteProgram(PlayerIntent intent, FixedQ4816 moveSpeed, FixedQ4816 turnSpeed, ulong stepTicks, int entityIndex, BodyEffectTargets effectTargets, List<BodyEffectOutput>? effectOutputs, List<WorldDesignation>? designationOutputs, List<WorldGeneratorInvocation>? generatorInvocations) {
        var scratch = new BodyMotionScratch {
            Intent = intent,
            MoveSpeed = moveSpeed,
            TurnSpeed = turnSpeed,
            StepTicks = stepTicks,
            Up = m_up,
            Orientation = m_orientation,
            NextPosition = m_position,
            EntityIndex = entityIndex,
            EffectTargets = effectTargets,
            EffectOutputs = effectOutputs,
            DesignationOutputs = designationOutputs,
            GeneratorInvocations = generatorInvocations,
        };

        for (var phase = 0; phase < m_bodyMotionProgram.Phases.Length; phase++) {
            foreach (var op in m_bodyMotionProgram.Phases[phase]) {
                var instruction = new CompiledBodyInstruction(Operation: op, Value: default, Direction: default, DurationTicks: 0UL, StateSlot: -1);

                ExecuteOperation(instruction: in instruction, scratch: ref scratch);
            }

            if (phase == 5) {
                ResolveProgramContacts(scratch: ref scratch);
            }
        }
    }

    internal void ExecuteProducer(CompiledBodyProducer producer, ref BodyProducerState state, in BodyProducerSensors sensors, ulong stepTicks) {
        var scratch = new BodyMotionScratch {
            StepTicks = stepTicks,
            TurnSpeed = (Profile?.FixedTurnSpeed ?? m_tuning.TurnSpeed),
            Producer = producer,
            ProducerState = state,
            ProducerSensors = sensors,
            SensorTarget = BodySensorTarget.None,
        };

        for (var phase = 0; phase < producer.Program.Phases.Length; phase++) {
            foreach (var op in producer.Program.Phases[phase]) {
                var instruction = new CompiledBodyInstruction(Operation: op, Value: default, Direction: default, DurationTicks: 0UL, StateSlot: -1);

                ExecuteOperation(instruction: in instruction, scratch: ref scratch);
            }
        }

        state = scratch.ProducerState;
        StageProducerIntent(intent: in scratch.Intent);
    }

    private void ExecuteOperation(in CompiledBodyInstruction instruction, ref BodyMotionScratch scratch) {
        switch (instruction.Operation) {
            case BodyMotionOp.SenseNearestInCone:
                SenseTarget(candidate: scratch.ProducerSensors.Candidate, scratch: ref scratch);
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
            case BodyMotionOp.ComputeSwimTargetVelocity:
                ComputeSwimTargetVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.ShapePlanarVelocity:
                scratch.Velocity = ShapePlanarVelocity(target: scratch.TargetVelocity, intent: in scratch.Intent, stepTicks: scratch.StepTicks);
                break;
            case BodyMotionOp.SnapYawToPlanarIntent:
                SnapYawToPlanarIntent(scratch: ref scratch);
                break;
            case BodyMotionOp.ResolveVehicleFrame:
                ResolveVehicleFrame(scratch: ref scratch);
                break;
            case BodyMotionOp.ShapeVehicleVelocity:
                ShapeVehicleVelocity(scratch: ref scratch);
                break;
            case BodyMotionOp.RunActionTriggers:
                ProcessLaneActions(scratch: ref scratch);
                break;
            case BodyMotionOp.ApplyVerticalGravity:
                ApplyVerticalGravity(stepTicks: scratch.StepTicks);
                break;
            case BodyMotionOp.ApplyVerticalDecay:
                ApplyVerticalDecay(scratch: ref scratch);
                break;
            case BodyMotionOp.ApplyBuoyancyAndSurface:
                ApplyBuoyancyAndSurface(scratch: ref scratch);
                break;
            case BodyMotionOp.ApplyVerticalDrive:
                ApplyVerticalDrive(scratch: ref scratch);
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
                ApplyRawState(slot: instruction.StateSlot, requested: instruction.Value.Value, writer: "world-effect", reason: "setState");
                MarkDurableDirty(slot: instruction.StateSlot);
                break;
            case BodyMotionOp.AddState:
                var beforeAdd = m_actionStateValues[instruction.StateSlot];
                ApplyRawState(slot: instruction.StateSlot, requested: (m_actionStateValues[instruction.StateSlot] + instruction.Value).Value, writer: "world-effect", reason: "addState");
                MarkDurableDirty(slot: instruction.StateSlot, kind: WorldDocumentWriteKind.Add, operand: (m_actionStateValues[instruction.StateSlot] - beforeAdd));
                break;
            case BodyMotionOp.StartTimer:
                ApplyRawState(slot: instruction.StateSlot, requested: checked((long)instruction.DurationTicks), writer: "world-effect", reason: "startTimer");
                MarkDurableDirty(slot: instruction.StateSlot);
                break;
            case BodyMotionOp.Designate:
                var subject = scratch.EffectTargets.Resolve(target: instruction.Target);
                if ((subject >= 0) && (instruction.StateName is { } register) && (scratch.DesignationOutputs is not null)) {
                    scratch.DesignationOutputs.Add(item: new WorldDesignation(EntityIndex: scratch.EntityIndex, Register: register, Subject: GrantSubject.Body(index: subject)));
                }
                break;
            case BodyMotionOp.Generate:
                // STAGED, never applied here: the destination is a document row, so the firing joins the ordinary
                // mutation pipeline after the whole population advance (see WorldGeneratorInvocation).
                if ((instruction.StateName is { } siteRow) && (scratch.GeneratorInvocations is not null)) {
                    scratch.GeneratorInvocations.Add(item: new WorldGeneratorInvocation(Row: siteRow));
                }
                break;
            default:
                throw new InvalidOperationException(message: $"Body program reached uncompiled opcode value {(int)instruction.Operation}.");
        }
    }

    private void SenseTarget(BodySensorTarget candidate, ref BodyMotionScratch scratch) {
        var producer = scratch.Producer!;
        var current = scratch.ProducerSensors.CurrentTarget;
        if (producer.Target?.Source is BodyTargetSource.Designated) {
            scratch.SensorTarget = candidate;
        } else {
            var release = producer.Scalar(name: "releaseRadius");

            if (current.Exists && (current.DistanceSquared <= (release * release))) {
                scratch.SensorTarget = current;
            } else {
                scratch.SensorTarget = candidate;
            }
        }

        scratch.ProducerState.AcquiredTarget = scratch.SensorTarget.Index;
    }

    private void ProduceWanderIntent(ref BodyMotionScratch scratch) {
        var producer = scratch.Producer!;
        var state = scratch.ProducerState;

        state.Phase += PerStep(value: state.WeaveFrequency, stepTicks: scratch.StepTicks);
        state.ActivityPhase += PerStep(value: state.ActivityRate, stepTicks: scratch.StepTicks);

        var planarX = m_position.X;
        var planarZ = m_position.Z;
        var yawRate = (producer.Scalar(name: "weaveAmplitude") * FixedQ4816.Sin(angle: state.Phase));
        var radius = FixedQ4816.Sqrt(value: ((planarX * planarX) + (planarZ * planarZ)));

        if (radius > producer.Scalar(name: "softRadius")) {
            var inwardYaw = FixedQ4816.Atan2(y: planarX, x: planarZ);
            yawRate += (producer.Scalar(name: "inwardGain") * WrapPi(angle: (inwardYaw - FixedYaw)));
        }

        var turn = FixedQ4816.Clamp(value: (yawRate / producer.Scalar(name: "turnScale")), minimum: s_negativeOne, maximum: FixedQ4816.One);
        var wave = FixedQ4816.Sin(angle: state.ActivityPhase);
        var altitudeCorrection = FixedQ4816.Clamp(
            value: ((state.PreferredAltitude - m_position.Y) * producer.Scalar(name: "altitudeGain")),
            minimum: s_negativeOne,
            maximum: FixedQ4816.One
        );

        if (m_bodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)) {
            scratch.Intent = m_roleOrdinals.Intent(
                moveForward: producer.Scalar(name: "forward"),
                moveStrafe: (wave * producer.Scalar(name: "strafeWave")),
                turn: turn,
                moveUp: (altitudeCorrection + (wave * producer.Scalar(name: "upWave"))),
                pitch: (wave * producer.Scalar(name: "pitchWave")),
                roll: (-turn * producer.Scalar(name: "rollTurn"))
            );
        } else {
            var angularIntent = FixedQ4816.Clamp(value: (turn + (wave * producer.Scalar(name: "turnWave"))), minimum: s_negativeOne, maximum: FixedQ4816.One);
            var forward = producer.Scalar(name: "forward");
            var strafe = (wave * producer.Scalar(name: "strafeWave"));

            if (m_tuning.MoveFrame == MotionMoveFrame.World) {
                // A producer owns a body-relative steering decision even when a seat-facing kit consumes world-frame
                // axes. Resolve that decision through the same yaw convention SnapYawToPlanarIntent reads; otherwise
                // the Turn channel is deliberately inert under World and every wanderer can only march toward -Z.
                var targetYaw = (FixedYaw + PerStep(value: (angularIntent * scratch.TurnSpeed), stepTicks: scratch.StepTicks));
                var (sinYaw, cosYaw) = FixedQ4816.SinCos(angle: targetYaw);
                scratch.Intent = m_roleOrdinals.Intent(
                    moveForward: ((forward * cosYaw) + (strafe * sinYaw)),
                    moveStrafe: ((-forward * sinYaw) + (strafe * cosYaw))
                );
            } else {
                scratch.Intent = m_roleOrdinals.Intent(
                    moveForward: forward,
                    moveStrafe: strafe,
                    turn: angularIntent
                );
            }

            var press = producer.Channel(name: "press");
            var threshold = producer.Scalar(name: "pressThreshold");
            if ((press >= 0) && (threshold > FixedQ4816.Zero) && (wave > threshold)) {
                scratch.Intent = scratch.Intent.WithChannel(ordinal: press, value: FixedQ4816.One);
            }
        }

        scratch.ProducerState = state;
    }

    private void ProduceAttendIntent(ref BodyMotionScratch scratch) {
        if (!scratch.SensorTarget.Exists) {
            return;
        }

        var producer = scratch.Producer!;
        var standoff = producer.Scalar(name: "standoffRadius");
        var forward = (scratch.SensorTarget.DistanceSquared > (standoff * standoff)) ? producer.Scalar(name: "approach") : FixedQ4816.Zero;
        var up = m_bodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)
            ? FixedQ4816.Clamp(value: ((scratch.ProducerState.PreferredAltitude - m_position.Y) * producer.Scalar(name: "altitudeGain")), minimum: s_negativeOne, maximum: FixedQ4816.One)
            : FixedQ4816.Zero;

        scratch.Intent = m_roleOrdinals.Intent(moveForward: forward, moveStrafe: producer.Scalar(name: "orbit"), moveUp: up);
    }

    private void FaceSensorTarget(ref BodyMotionScratch scratch) {
        if (!scratch.SensorTarget.Exists || (m_roleOrdinals.Turn < 0)) {
            return;
        }

        var dx = (scratch.SensorTarget.Position.X - m_position.X);
        var dz = (scratch.SensorTarget.Position.Z - m_position.Z);
        var targetYaw = FixedQ4816.Atan2(y: -dx, x: -dz);
        var yawRate = (scratch.Producer!.Scalar(name: "inwardGain") * WrapPi(angle: (targetYaw - FixedYaw)));
        var turn = FixedQ4816.Clamp(value: (yawRate / scratch.Producer.Scalar(name: "turnScale")), minimum: s_negativeOne, maximum: FixedQ4816.One);

        scratch.Intent = scratch.Intent.WithChannel(ordinal: m_roleOrdinals.Turn, value: turn);
    }

    private static FixedQ4816 PerStep(FixedQ4816 value, ulong stepTicks) {
        if ((EngineTicks.PerSecond % stepTicks) != 0UL) {
            throw new ArgumentException(message: $"The fixed-step period {stepTicks} must divide {EngineTicks.PerSecond} engine ticks exactly.", paramName: nameof(stepTicks));
        }

        return (value / FixedQ4816.FromInteger(value: checked((long)(EngineTicks.PerSecond / stepTicks))));
    }

    private static FixedQ4816 WrapPi(FixedQ4816 angle) => (angle - (s_twoPi * FixedQ4816.Floor(value: ((angle + s_pi) / s_twoPi))));

    // The grounded integration — planar math for the horizontal axes plus the bound vertical action on the other.
    // Horizontal: under MotionMoveFrame.Heading (the default, tank controls) turn the heading, step along the fresh
    // facing/right (instant velocity — no ground/air acceleration yet). A pure-yaw facing/right carry no Y, so the
    // horizontal step never disturbs the vertical axis the action owns. Under MotionMoveFrame.World the two channels
    // are ALREADY world-frame (the seat composed its camera yaw client-side before submission — determinism: no
    // camera state ever enters the sim), so the heading integrator is bypassed entirely and facing only ever moves
    // via FacingSnap, below. Trigger instructions may write vertical velocity before gravity and integration. The
    // MoveUp/Pitch/Roll channels stay inert; contact geometry owns the resting altitude.
    private void ResolveYawAttitudeAndPlanarFrame(ref BodyMotionScratch scratch) {
        scratch.Up = ResolveUp();

        if (m_tuning.MoveFrame == MotionMoveFrame.World) {
            // World-frame movement means the TRANSLATION axes are already resolved by the seat; it does not make
            // the authored Turn role inert. Integrate facing independently so an author can choose camera-facing
            // strafe (FacingSnap=false plus right-stick.x -> Turn) or movement-facing locomotion (FacingSnap=true,
            // whose later SnapYawToPlanarIntent remains the final word while movement is nonzero).
            var worldAngleStep = m_rotationAccumulator.Integrate(
                ratePerSecond: new FixedVector3(
                    X: (Role(intent: in scratch.Intent, role: ChannelRole.Turn) * scratch.TurnSpeed),
                    Y: FixedQ4816.Zero,
                    Z: FixedQ4816.Zero
                ),
                elapsedTicks: scratch.StepTicks
            );
            m_yaw += worldAngleStep.X;
            scratch.Orientation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw);
            scratch.Facing = -s_unitZ;
            scratch.Right = s_unitX;
            return;
        }

        var angleStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: (Role(intent: in scratch.Intent, role: ChannelRole.Turn) * scratch.TurnSpeed),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            elapsedTicks: scratch.StepTicks
        );

        m_yaw += angleStep.X;
        var yawRotation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw);

        scratch.Orientation = ((scratch.Up == s_unitY) ? yawRotation : (FixedQuaternion.FromTo(from: s_unitY, to: scratch.Up) * yawRotation));
        scratch.Facing = scratch.Orientation.Rotate(vector: -s_unitZ);
        scratch.Right = scratch.Orientation.Rotate(vector: s_unitX);
    }

    private void ComputePlanarTargetVelocity(ref BodyMotionScratch scratch) {
        var effectiveMoveSpeed = ((m_sprintChannelOrdinal >= 0) && (scratch.Intent[m_sprintChannelOrdinal] >= m_channelThresholds[m_sprintChannelOrdinal]))
            ? (scratch.MoveSpeed * m_tuning.SprintMultiplier)
            : scratch.MoveSpeed;

        scratch.TargetVelocity = (((scratch.Facing * Role(intent: in scratch.Intent, role: ChannelRole.MoveForward)) + (scratch.Right * Role(intent: in scratch.Intent, role: ChannelRole.MoveStrafe))) * effectiveMoveSpeed);
    }

    private void SnapYawToPlanarIntent(ref BodyMotionScratch scratch) {
        if ((m_tuning.MoveFrame != MotionMoveFrame.World) || !m_tuning.FacingSnap
            || ((Role(intent: in scratch.Intent, role: ChannelRole.MoveForward) == FixedQ4816.Zero) && (Role(intent: in scratch.Intent, role: ChannelRole.MoveStrafe) == FixedQ4816.Zero))) {
            return;
        }

        m_yaw = FixedQ4816.Atan2(y: -Role(intent: in scratch.Intent, role: ChannelRole.MoveStrafe), x: Role(intent: in scratch.Intent, role: ChannelRole.MoveForward));
        scratch.Orientation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw);
    }

    private void ApplyVerticalGravity(ulong stepTicks) {
        var gravity = ((m_verticalVelocity > FixedQ4816.Zero) ? m_tuning.RiseGravity : m_tuning.FallGravity);

        var gravityStep = m_verticalVelocityAccumulator.Integrate(
            ratePerSecond: -gravity,
            elapsedTicks: stepTicks
        );
        var terminalVelocity = -m_tuning.MaxFallSpeed;
        var acceleratedVelocity = (m_verticalVelocity + gravityStep);

        if (acceleratedVelocity < terminalVelocity) {
            m_verticalVelocity = terminalVelocity;
            m_verticalVelocityAccumulator.Reset();
        } else {
            m_verticalVelocity = acceleratedVelocity;
        }

    }

    // Direct vertical traversal and ballistic motion are separate channels. While MoveUp is held, direct drive is
    // the complete vertical request and the jump/fall accumulator is cleared; on release, the direct term vanishes
    // in this same tick and gravity resumes from rest. A trigger therefore cannot become stored upward velocity,
    // while the same authored program can still jump, land, and fly.
    private void ApplyVerticalDrive(ref BodyMotionScratch scratch) {
        var drive = Role(intent: in scratch.Intent, role: ChannelRole.MoveUp);
        if (drive == FixedQ4816.Zero) {
            return;
        }

        m_verticalVelocity = FixedQ4816.Zero;
        m_verticalVelocityAccumulator.Reset();
        scratch.DirectVerticalVelocity = (drive * scratch.MoveSpeed);
    }

    private void IntegratePlanarAndVerticalVelocity(ref BodyMotionScratch scratch) {
        scratch.Velocity = (m_planarVelocity + (scratch.Up * (m_verticalVelocity + scratch.DirectVerticalVelocity)));
        var step = m_positionAccumulator.Integrate(
            ratePerSecond: scratch.Velocity,
            elapsedTicks: scratch.StepTicks
        );
        scratch.NextPosition = (m_position + step);
    }

    // Position/planar contact response applies to ANY collider-bearing body regardless of motion model — a flying
    // body still shouldn't clip through a wall. The vertical WRITE-BACK (m_verticalVelocity, m_planarVelocity, the
    // grounded position-accumulator reset) is gated on CompiledBodyMotionProgram.OwnsVerticalContactState: only a
    // program that itself integrates gravity (ApplyVerticalGravity) has ceded its vertical channel to contact
    // resolution. A program that instead owns that channel directly (free's ApplyVerticalDecay bleed; the coming
    // swim arm) must keep it — folding the resolved velocity back in every tick regardless would feed a decay
    // channel's own prior value back into itself, an unbounded loop rather than a correction (the defect this
    // gate exists to close). m_grounded/m_lastContactCount stay informational for every model (RunActionTriggers'
    // ActionFact.Grounded/Airborne reads them under any program), since they never feed back into an integration.
    private void ResolveProgramContacts(ref BodyMotionScratch scratch) {
        if ((m_contactField is { } field) && (m_collider is { } collider)) {
            var resolvedVelocity = scratch.Velocity;
            var contactResolution = ((field is IEntityContactField entityField)
                ? entityField.ResolveEntitySweep(entityIndex: scratch.EntityIndex, previousPosition: m_position, position: ref scratch.NextPosition, velocity: ref resolvedVelocity, orientation: in scratch.Orientation, volumes: collider.Volumes)
                : field.ResolveSweep(previousPosition: m_position, position: ref scratch.NextPosition, velocity: ref resolvedVelocity, orientation: in scratch.Orientation, volumes: collider.Volumes));

            m_grounded = contactResolution.Grounded;
            m_lastContactCount = (m_grounded ? 1 : 0);
            // scratch.Intent's raw MoveForward/MoveStrafe roles are the idle signal — resolved once by NextIntent
            // before ANY op runs, so it is available and current at this exact point regardless of the compiled
            // program's op order (unlike scratch.TargetVelocity/scratch.Velocity, which a Compute*TargetVelocity op
            // may not have written yet this tick depending on where contact resolution sits in that order, and which
            // — once written — is the RESPONSE-RAMPED result the wall itself just clipped: using either would risk
            // a feedback loop, a wall stopping the body read back as "input released").
            UpdateObstructionWitness(rawObstruction: contactResolution.ObstructionNormal, intent: in scratch.Intent, position: scratch.NextPosition, stepTicks: scratch.StepTicks);

            if (!m_bodyMotionProgram.OwnsVerticalContactState) {
                return;
            }

            var resolvedNormal = FixedVector3.Dot(left: resolvedVelocity, right: scratch.Up);

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

            if (resolvedNormal != m_verticalVelocity) {
                m_verticalVelocity = resolvedNormal;
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

    /// <summary>Updates the latched <c>world.contacts</c> obstruction witness from this tick's raw solver result. A
    /// fresh non-walkable push always (re)latches immediately and refills the grace window. Absent one, the
    /// existing latch clears immediately the instant either releasing condition holds — the raw planar move intent
    /// (<paramref name="intent"/>'s MoveForward/MoveStrafe roles, resolved once by <c>NextIntent</c> before any op
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

        var forward = Role(intent: in intent, role: ChannelRole.MoveForward);
        var strafe = Role(intent: in intent, role: ChannelRole.MoveStrafe);
        var idle = ((FixedQ4816.Abs(value: forward) <= s_obstructionLatchIdleThreshold) && (FixedQ4816.Abs(value: strafe) <= s_obstructionLatchIdleThreshold));
        var displaced = ((position - m_obstructionWitnessPosition).LengthSquared > s_obstructionLatchDisplacementSquared);

        if (idle || displaced) {
            m_obstructionWitness = FixedVector3.Zero;
            m_obstructionWitnessGraceTicks = 0;

            return;
        }

        m_obstructionWitnessGraceTicks = SubtractSaturating(value: m_obstructionWitnessGraceTicks, amount: stepTicks);

        if (m_obstructionWitnessGraceTicks == 0) {
            m_obstructionWitness = FixedVector3.Zero;
        }
    }

    // The body up axis this grounded step integrates against. The contact field answers it (constant +Y from the
    // analytic provider AND from a field provider without GradientDerivedUp; the surface gradient only when the world
    // authors that requirement); a degenerate field query leaves the held value
    // untouched rather than snapping to something arbitrary. Only a collider-bearing kit with a field pays the query;
    // everything else keeps +Y, so the flat world never calls TryUp and integrates byte-identically.
    private FixedVector3 ResolveUp() {
        if ((m_contactField is { } field) && (m_collider is not null) && field.TryUp(position: in m_position, up: out var up)) {
            m_up = up;
        }

        return m_up;
    }

    // --- The response table (the Shape stage). ---
    // Converge the ramped planar velocity on the commanded target through the matching response row's engage/release
    // rate. An empty table snaps instantly (today's exact behavior, the only path an unopted world takes, byte-identical).
    // A body matching no row also snaps (the always-row is optional). The has-input axis — a property of the command,
    // not a body fact — picks the engage (stick deflected) or release (stick centered) rate.
    private FixedVector3 ShapePlanarVelocity(FixedVector3 target, in PlayerIntent intent, ulong stepTicks) {
        var response = m_tuning.Response;

        if (response.Length == 0) {
            m_planarVelocity = target;

            return target;
        }

        // Refresh the shared response recency clocks (a Recently window refills while its fact holds, decays otherwise).
        for (var slot = 0; (slot < m_motionRecency.Length); slot++) {
            m_motionRecency[slot] = (FactHolds(fact: m_tuning.ResponseRecencyFacts[slot])
                ? m_tuning.ResponseRecencyWindows[slot]
                : SubtractSaturating(value: m_motionRecency[slot], amount: stepTicks));
        }

        var hasInput = ((Role(intent: in intent, role: ChannelRole.MoveForward) != FixedQ4816.Zero) || (Role(intent: in intent, role: ChannelRole.MoveStrafe) != FixedQ4816.Zero));

        foreach (var row in response) {
            if (!MotionGateOpen(gate: row.Gate)) {
                continue;
            }

            var rate = (hasInput ? row.EngageRate : row.ReleaseRate);
            var maxDelta = m_planarRampAccumulator.Integrate(ratePerSecond: rate, elapsedTicks: stepTicks);

            m_planarVelocity = FixedVector3.MoveToward(current: m_planarVelocity, target: target, maxDelta: maxDelta);

            return m_planarVelocity;
        }

        m_planarVelocity = target;

        return target;
    }

    // A motion-response gate: a flattened conjunction of BODY-FACT predicates only (Now/Recently — the validator rejects
    // action-state predicates on a response gate). Every element must hold.
    private bool MotionGateOpen(CompiledPredicate[] gate) {
        foreach (var predicate in gate) {
            var holds = predicate.Kind switch {
                CompiledPredicateKind.Now => FactHolds(fact: predicate.Fact),
                CompiledPredicateKind.Recently => (m_motionRecency[predicate.RecencySlot] > 0),
                _ => false,
            };

            if (!holds) {
                return false;
            }
        }

        return true;
    }

    // Reset the grounded vertical state to a clean rest on the plane — called by every hard teleport, so a jump never
    // survives an authoritative reposition. The action track (held/timed lanes) is left alone: a teleport moves the
    // body, not the player's buttons.
    private void ResetVertical() {
        m_verticalVelocity = FixedQ4816.Zero;
        m_verticalVelocityAccumulator.Reset();
        m_positionAccumulator.ResetY();
        m_grounded = true;

        // A teleport must not carry momentum: drop the ramped planar velocity, its accumulator carries (the grounded
        // ramp and the vehicle arm's decomposed channels alike), and the response table's recency clocks.
        m_planarVelocity = default;
        m_planarRampAccumulator.Reset();
        m_vehicleLongAccumulator.Reset();
        m_vehicleLatAccumulator.Reset();
        m_vehicleResidualAccumulator.Reset();
        Array.Clear(array: m_motionRecency);

        // The swim carries are momentum and medium facts on the same terms — a warp never carries a dive across, and
        // a body warped out of the water must not read Submerged until the surface stage says so again.
        m_swimThrustRampAccumulator.Reset();
        m_submerged = false;
        m_atSurface = false;
    }

    // --- The vehicle arm (the ResolveVehicleFrame/ShapeVehicleVelocity ops). ---

    // The vehicle frame (phase 0): resolve up, integrate speed-scaled steering into the heading, and (under a
    // positive PitchRate) integrate the Pitch channel into the clamped pitch scalar; facing/right derive from the
    // fresh yaw(+pitch) attitude. Steering authority rises linearly from zero at standstill to full at
    // SteerReferenceSpeed, falls linearly to SteerFalloff× at the RESOLVED (envelope-clamped) top speed — the SAME
    // scratch.MoveSpeed ResolveMoveSpeed filled before phase 0, never a second TopSpeed read, so a clamped kit's
    // falloff anchor moves with its clamp instead of an unreachable authored TopSpeed — reverses sign with
    // reversing travel (a car backing up, not a turret), and scales by DriftSteerScale while the drift channel
    // reads held.
    private void ResolveVehicleFrame(ref BodyMotionScratch scratch) {
        scratch.Up = ResolveUp();

        var tuning = m_vehicleTuning;
        // The signed longitudinal speed against the PREVIOUS attitude — shaping runs after this frame op, so the
        // one-tick-old velocity is the deterministic witness available here.
        var previousFacing = m_orientation.Rotate(vector: -s_unitZ);
        var longitudinal = FixedVector3.Dot(left: m_planarVelocity, right: previousFacing);
        var speed = FixedQ4816.Abs(value: longitudinal);
        var authority = ((speed >= tuning.SteerReferenceSpeed)
            ? FixedQ4816.One
            : (speed / tuning.SteerReferenceSpeed));

        if ((speed > tuning.SteerReferenceSpeed) && (scratch.MoveSpeed > tuning.SteerReferenceSpeed)) {
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

        var yawRate = ((Role(intent: in scratch.Intent, role: ChannelRole.Turn) * tuning.SteerRate) * authority);
        var pitchRate = ((tuning.PitchRate > FixedQ4816.Zero)
            ? (Role(intent: in scratch.Intent, role: ChannelRole.Pitch) * tuning.PitchRate)
            : FixedQ4816.Zero);
        var angleStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(X: yawRate, Y: pitchRate, Z: FixedQ4816.Zero),
            elapsedTicks: scratch.StepTicks
        );

        m_yaw += angleStep.X;
        m_vehiclePitch = FixedQ4816.Clamp(value: (m_vehiclePitch + angleStep.Y), minimum: -s_maxVehiclePitch, maximum: s_maxVehiclePitch);

        var attitude = ((m_vehiclePitch == FixedQ4816.Zero)
            ? FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw)
            : (FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw) * FixedQuaternion.FromAxisAngle(axis: s_unitX, angle: m_vehiclePitch)).Normalize());

        scratch.Orientation = ((scratch.Up == s_unitY) ? attitude : (FixedQuaternion.FromTo(from: s_unitY, to: scratch.Up) * attitude));
        scratch.Facing = scratch.Orientation.Rotate(vector: -s_unitZ);
        scratch.Right = scratch.Orientation.Rotate(vector: s_unitX);
    }

    // The vehicle shaping (phase 2): decompose the carried velocity into body-frame longitudinal/lateral/residual
    // components, converge each at its own authored rate, and recompose — the anisotropy a kart's feel needs and
    // grounded's isotropic MoveToward cannot express. Longitudinal follows the bipolar throttle (accelerate toward
    // the commanded fraction of scratch.MoveSpeed — the RESOLVED, envelope-clamped base top speed, the same value
    // EffectiveMoveSpeed echoes, never the raw authored TopSpeed; back-throttle brakes while moving forward and
    // reverses from rest at the unenveloped ReverseTopSpeed; the over-speed excess bleeds at CoastDrag, which is
    // also the centered-throttle coast). A held boost multiplies scratch.MoveSpeed AFTER the clamp, on top of the
    // resolved base rate, never inside it — the envelope pins the base, boost rides on top. Lateral and residual
    // slip converge to zero at Grip — DriftGrip while drifting. A contact-pinned variant (PitchRate zero) has no
    // drive or grip authority while airborne: a launched kart holds its velocity and gravity owns the arc.
    private void ShapeVehicleVelocity(ref BodyMotionScratch scratch) {
        var tuning = m_vehicleTuning;
        var throttle = Role(intent: in scratch.Intent, role: ChannelRole.MoveForward);
        var hasAuthority = ((tuning.PitchRate > FixedQ4816.Zero) || m_grounded);
        var velocity = m_planarVelocity;
        var longitudinal = FixedVector3.Dot(left: velocity, right: scratch.Facing);
        var lateral = FixedVector3.Dot(left: velocity, right: scratch.Right);
        var residual = ((velocity - (scratch.Facing * longitudinal)) - (scratch.Right * lateral));

        if (hasAuthority) {
            FixedQ4816 target, rate;

            if (throttle > FixedQ4816.Zero) {
                var commanded = (BoostHeld(intent: in scratch.Intent) ? (scratch.MoveSpeed * tuning.BoostMultiplier) : scratch.MoveSpeed);

                target = (throttle * commanded);
                rate = ((longitudinal <= target) ? tuning.Accel : tuning.CoastDrag);
            } else if (throttle < FixedQ4816.Zero) {
                if (longitudinal > FixedQ4816.Zero) {
                    target = FixedQ4816.Zero;
                    rate = tuning.Brake;
                } else {
                    target = (throttle * tuning.ReverseTopSpeed);
                    rate = tuning.Accel;
                }
            } else {
                target = FixedQ4816.Zero;
                rate = tuning.CoastDrag;
            }

            longitudinal = MoveTowardScalar(
                current: longitudinal,
                target: target,
                maxDelta: m_vehicleLongAccumulator.Integrate(ratePerSecond: rate, elapsedTicks: scratch.StepTicks)
            );

            var grip = (DriftHeld(intent: in scratch.Intent) ? tuning.DriftGrip : tuning.Grip);

            lateral = MoveTowardScalar(
                current: lateral,
                target: FixedQ4816.Zero,
                maxDelta: m_vehicleLatAccumulator.Integrate(ratePerSecond: grip, elapsedTicks: scratch.StepTicks)
            );
            residual = FixedVector3.MoveToward(
                current: residual,
                target: default,
                maxDelta: m_vehicleResidualAccumulator.Integrate(ratePerSecond: grip, elapsedTicks: scratch.StepTicks)
            );
        }

        m_planarVelocity = (((scratch.Facing * longitudinal) + (scratch.Right * lateral)) + residual);
        scratch.TargetVelocity = m_planarVelocity;
    }

    private bool DriftHeld(in PlayerIntent intent) =>
        ((m_driftChannelOrdinal >= 0) && (intent[m_driftChannelOrdinal] >= m_channelThresholds[m_driftChannelOrdinal]));

    // The vehicle boost rides the same held-multiplier ordinal the grounded sprint resolves into (see
    // FixedWorldKit.SprintChannelOrdinal).
    private bool BoostHeld(in PlayerIntent intent) =>
        ((m_sprintChannelOrdinal >= 0) && (intent[m_sprintChannelOrdinal] >= m_channelThresholds[m_sprintChannelOrdinal]));

    private static FixedQ4816 MoveTowardScalar(FixedQ4816 current, FixedQ4816 target, FixedQ4816 maxDelta) {
        var delta = (target - current);

        return ((FixedQ4816.Abs(value: delta) <= maxDelta)
            ? target
            : (current + ((delta > FixedQ4816.Zero) ? maxDelta : -maxDelta)));
    }

    // The free integration — full 6DOF in the body frame. Compose the yaw/pitch/roll rates (each × turnSpeed) into a
    // body-frame delta and post-multiply it into the attitude (q ← normalize(q · Δq), so the rates rotate about the
    // body's own axes), then fly along the fresh body axes: velocity = (forward·MoveForward + right·MoveStrafe +
    // up·MoveUp) · moveSpeed, with no ground pin and no gravity. The bound actions run after the attitude update, so a
    // fired vertical impulse (the surge) rides this tick; the written channel bleeds to zero at the tuning's rise
    // gravity (no fall phase).
    private void IntegrateLocalAttitude(ref BodyMotionScratch scratch) {
        var angularStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: (Role(intent: in scratch.Intent, role: ChannelRole.Turn) * scratch.TurnSpeed),
                Y: (Role(intent: in scratch.Intent, role: ChannelRole.Pitch) * scratch.TurnSpeed),
                Z: (Role(intent: in scratch.Intent, role: ChannelRole.Roll) * scratch.TurnSpeed)
            ),
            elapsedTicks: scratch.StepTicks
        );
        var delta = ((FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: angularStep.X)
            * FixedQuaternion.FromAxisAngle(axis: s_unitX, angle: angularStep.Y))
            * FixedQuaternion.FromAxisAngle(axis: s_unitZ, angle: angularStep.Z));

        scratch.Orientation = (m_orientation * delta).Normalize();
    }

    private void ComputeLocalTargetVelocity(ref BodyMotionScratch scratch) {
        // The free program shares the kit's declared movement frame. Under World, the client has ALREADY rotated
        // the planar pair through camera yaw, so rotating it through body attitude again is a second composition
        // (the source of direction-dependent flight and apparent vertical loss after a handoff). World also makes
        // MoveUp literal world Y, keeping an upright hover body's ascent independent of its rendered facing. Heading
        // retains true 6DOF body-local flight.
        var facing = ((m_tuning.MoveFrame == MotionMoveFrame.World) ? -s_unitZ : scratch.Orientation.Rotate(vector: -s_unitZ));
        var right = ((m_tuning.MoveFrame == MotionMoveFrame.World) ? s_unitX : scratch.Orientation.Rotate(vector: s_unitX));
        var up = ((m_tuning.MoveFrame == MotionMoveFrame.World) ? s_unitY : scratch.Orientation.Rotate(vector: s_unitY));

        scratch.Velocity = ((((facing * Role(intent: in scratch.Intent, role: ChannelRole.MoveForward)) + (right * Role(intent: in scratch.Intent, role: ChannelRole.MoveStrafe))) + (up * Role(intent: in scratch.Intent, role: ChannelRole.MoveUp))) * scratch.MoveSpeed);
        scratch.TargetVelocity = scratch.Velocity;
    }

    private FixedQ4816 Role(in PlayerIntent intent, ChannelRole role) => m_roleOrdinals.Read(intent: in intent, role: role);

    private void ApplyVerticalDecay(ref BodyMotionScratch scratch) {
        if (m_verticalVelocity != FixedQ4816.Zero) {
            scratch.Velocity = scratch.Velocity with { Y = (scratch.Velocity.Y + m_verticalVelocity) };

            if (m_verticalVelocity > FixedQ4816.Zero) {
                var bleed = m_verticalVelocityAccumulator.Integrate(ratePerSecond: -m_tuning.RiseGravity, elapsedTicks: scratch.StepTicks);
                var next = (m_verticalVelocity + bleed);

                m_verticalVelocity = ((next < FixedQ4816.Zero) ? FixedQ4816.Zero : next);
            } else {
                var bleed = m_verticalVelocityAccumulator.Integrate(ratePerSecond: m_tuning.RiseGravity, elapsedTicks: scratch.StepTicks);
                var next = (m_verticalVelocity + bleed);

                m_verticalVelocity = ((next > FixedQ4816.Zero) ? FixedQ4816.Zero : next);
            }

            if (m_verticalVelocity == FixedQ4816.Zero) {
                m_verticalVelocityAccumulator.Reset();
            }
        }
    }

    private void IntegrateScratchVelocity(ref BodyMotionScratch scratch) {
        scratch.NextPosition = (m_position + m_positionAccumulator.Integrate(
            ratePerSecond: scratch.Velocity,
            elapsedTicks: scratch.StepTicks
        ));
    }

    // --- The swim model (the medium's stages). ---
    // The 3D thrust target in the body's yaw frame: the planar half rides the SAME facing/right the grounded target
    // uses (a pure-yaw frame carries no Y, so planar thrust never leaks into the vertical channel), the vertical half
    // is the explicit MoveUp channel scaled down by the authored fraction. The sprint burst scales the WHOLE vector —
    // the same held-channel read the grounded target applies to its planar half.
    private void ComputeSwimTargetVelocity(ref BodyMotionScratch scratch) {
        var effectiveSpeed = (((m_sprintChannelOrdinal >= 0) && (scratch.Intent[m_sprintChannelOrdinal] >= m_channelThresholds[m_sprintChannelOrdinal]))
            ? (scratch.MoveSpeed * m_tuning.SprintMultiplier)
            : scratch.MoveSpeed);

        scratch.TargetVelocity = (((scratch.Facing * Role(intent: in scratch.Intent, role: ChannelRole.MoveForward)) + (scratch.Right * Role(intent: in scratch.Intent, role: ChannelRole.MoveStrafe))) * effectiveSpeed);
        scratch.SwimVerticalTarget = ((m_swimTuning is { } swim)
            ? ((Role(intent: in scratch.Intent, role: ChannelRole.MoveUp) * effectiveSpeed) * swim.VerticalThrustFraction)
            : FixedQ4816.Zero);
    }

    // The swim model's ONE vertical owner: both the medium's target and the response-row convergence happen HERE,
    // never split into a separate stage — a second constant-rate owner of the same channel always beats the first,
    // which would leave an idle body short of its float line or let a held ascent breach it. The medium's own
    // target folds into the commanded thrust target BEFORE the convergence runs — below the bob
    // band the target is a constant trim drift (Buoyancy, clamped to the terminal speeds); inside the band and
    // above it (breach recovery) the target is a proportional settle toward the float line — displacement times
    // SurfaceSettleRate — capped upward at the
    // buoyant drift (continuity at the band edge) and downward at the sink terminal. The sum of medium target and
    // staged thrust (ComputeSwimTargetVelocity's SwimVerticalTarget) converges through the SAME matching response
    // row's engage/release rate the planar half rides — engage while the vertical stick is deflected, release while
    // centered — so a held ascent parks where thrust and settle balance instead of racing to the surface. The two
    // swim facts are written here, read one tick behind by gates, exactly like m_grounded.
    private void ApplyBuoyancyAndSurface(ref BodyMotionScratch scratch) {
        if ((m_swimTuning is not { } swim) || !m_hasWaterline) {
            return;
        }

        var surfaceRest = (m_waterline - swim.FloatDepth);
        var error = (surfaceRest - m_position.Y);
        FixedQ4816 medium;

        if (m_position.Y < (surfaceRest - swim.FloatDepth)) {
            medium = FixedQ4816.Clamp(value: swim.Buoyancy, minimum: -swim.MaxSinkSpeed, maximum: swim.MaxRiseSpeed);
        } else {
            var upwardCap = ((swim.Buoyancy > FixedQ4816.Zero) ? swim.Buoyancy : FixedQ4816.Zero);

            medium = FixedQ4816.Clamp(value: (error * swim.SurfaceSettleRate), minimum: -swim.MaxSinkSpeed, maximum: upwardCap);
        }

        var target = FixedQ4816.Clamp(value: (scratch.SwimVerticalTarget + medium), minimum: -swim.MaxSinkSpeed, maximum: swim.MaxRiseSpeed);
        var response = m_tuning.Response;

        if (response.Length == 0) {
            m_verticalVelocity = target;
        } else {
            var matched = false;

            // ShapePlanarVelocity already ticked the recency clocks this step (phase 2 precedes 4); this scan only
            // SELECTS (first open row wins, same rule the planar half follows).
            foreach (var row in response) {
                if (!MotionGateOpen(gate: row.Gate)) {
                    continue;
                }

                var hasVerticalInput = (Role(intent: in scratch.Intent, role: ChannelRole.MoveUp) != FixedQ4816.Zero);
                var rate = (hasVerticalInput ? row.EngageRate : row.ReleaseRate);
                var maxDelta = m_swimThrustRampAccumulator.Integrate(ratePerSecond: rate, elapsedTicks: scratch.StepTicks);

                m_verticalVelocity = MoveTowardScalar(current: m_verticalVelocity, target: target, maxDelta: maxDelta);
                matched = true;

                break;
            }

            if (!matched) {
                m_verticalVelocity = target;
            }
        }

        m_submerged = (m_position.Y < m_waterline);
        m_atSurface = (m_submerged && (((error < FixedQ4816.Zero) ? -error : error) <= swim.FloatDepth));
    }

    // Resolve this sub-step's full intent by the IntentSource merge rule: a live tape segment takes precedence for the
    // movement channels (consumed whole-frame, dropped when its time runs out; expired/empty front segments are
    // skipped first, so a drained tape falls through the same frame it empties); with the tape dry, the tick's
    // submitted intent (admitted unless Idle), else the producer image (iff the source names it), else zero. The
    // action-track lanes are then overlaid, so a wire player.press jumps a tape-driven runner.
    // Whether an intent source names a server-side producer whose staged image fills gaps.
    private static bool SourceNamesProducer(IntentSource source) => source.IsProducer;

    private PlayerIntent NextIntent(ulong stepTicks) {
        var movement = default(PlayerIntent);
        var resolved = false;

        while (!resolved && (m_tapeCount > 0)) {
            ref var segment = ref m_tape[m_tapeHead];

            if (!(segment.RemainingTicks > 0)) {
                DropFrontSegment();

                continue;
            }

            // Charge this whole tick against the front segment; durations were quantized upward to whole host ticks at
            // enqueue, so no fractional tail or floating accumulator exists here.
            segment.RemainingTicks = SubtractSaturating(value: segment.RemainingTicks, amount: stepTicks);
            movement = segment.Intent;
            resolved = true;

            if (!(segment.RemainingTicks > 0)) {
                DropFrontSegment();
            }
        }

        if (!resolved) {
            movement = (!m_source.IsIdle && m_hasSubmittedIntent) ? m_submittedIntent
                : ((SourceNamesProducer(source: m_source) && m_hasProducerIntent) ? m_producerIntent : default);
        }

        // Both one-tick images are a one-step publish, even when a tape or the source masked them this time. Their
        // producers must republish on the next authoritative step, matching the snapshot discipline of every other
        // input source.
        m_submittedIntent = default;
        m_hasSubmittedIntent = false;
        m_producerIntent = default;
        m_hasProducerIntent = false;

        // Overlay the action track, per ordinal: a wire timer (player.press) overlays UNCONDITIONALLY — the poke
        // stays a poke regardless of intent source — replacing whatever the movement tier resolved for that ordinal;
        // otherwise a non-role ordinal additionally takes the live-held device image, admitted under
        // Live only (role ordinals never carry a held-device overlay — a seat submits them directly
        // inside `movement`). Two simultaneous composition contributors join via WorldChannelTable.ComposeHeld's
        // shape-aware rule: unipolar/binary reproduce the old ActionLanes OR (maximum magnitude, both operands in
        // [0, One]); bipolar sums the two instead, so a resting (zero) side can never overwrite a genuinely negative
        // one the way a numeric max used to.
        var channels = movement.Channels;
        var heldOverlay = default(ChannelValues);
        var liveHeld = (m_hasTransferHeldChannels ? m_transferHeldChannels : m_heldChannels);

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_laneTimers[ordinal] > 0) {
                channels[ordinal] = m_channelTimerValues[ordinal];
            } else if (m_source.IsLive && !m_roleChannels[ordinal]) {
                heldOverlay[ordinal] = liveHeld[ordinal];
                channels[ordinal] = FixedQ4816.FromRawBits(value: WorldChannelTable.ComposeHeld(a: channels[ordinal].Value, b: liveHeld[ordinal].Value, shape: m_channelShapes[ordinal]));
            }
        }

        m_channelReadHeld = new PlayerIntent(Channels: heldOverlay);
        m_channelReadComposed = new PlayerIntent(Channels: channels);

        return m_channelReadComposed;
    }

    // Advance the ring past its front segment (a FIFO dequeue): step the head and shrink the live count.
    private void DropFrontSegment() {
        m_tapeHead = ((m_tapeHead + 1) % m_tape.Length);
        m_tapeCount--;
    }

    // The per-tick action machinery: for each ordinal carrying a compiled binding, derive its edge (the folded value
    // crossing the channel's threshold against the previous sub-step — never carried), refresh the recency clocks (a
    // Recently window refills while its fact holds and decays otherwise), advance named state, latch a press edge, then
    // fire the press trigger while its latch is pending and its gate holds, and the release trigger on
    // its edge — each fire applying its compiled effects in order and consuming the latch. Runs after
    // attitude/planar integration and before gravity/vertical resolution, so effects shape the same tick.
    private void ProcessLaneActions(ref BodyMotionScratch scratch) {
        AdvanceActionState(stepTicks: scratch.StepTicks);

        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (m_laneBindings[ordinal] is not { } binding) {
                continue;
            }

            ref var state = ref m_laneActions[ordinal];
            var bit = (scratch.Intent[ordinal] >= m_channelThresholds[ordinal]);
            var pressed = (bit && !m_previousChannelBit[ordinal]);
            var released = (!bit && m_previousChannelBit[ordinal]);

            for (var slot = 0; (slot < binding.RecencyFacts.Length); slot++) {
                state.Recency![slot] = (FactHolds(fact: binding.RecencyFacts[slot])
                    ? binding.RecencyWindows[slot]
                    : SubtractSaturating(value: state.Recency[slot], amount: scratch.StepTicks));
            }

            if (binding.OnPress is { } press) {
                state.Latch = (pressed ? press.LatchTicks : SubtractSaturating(value: state.Latch, amount: scratch.StepTicks));

                // LatchSeconds 0 means THIS TICK ONLY, which is what the field always documented. Demanding a
                // strictly positive latch made zero structurally dead — a zero-latch press could never fire, however
                // open its gate was. The press is pending when its latch is still running OR when this very tick is
                // its edge; consuming it still clears the latch.
                if ((pressed || (state.Latch > 0)) && GateOpen(gate: press.Gate, state: in state)) {
                    ApplyEffects(effects: press.Effects, scratch: ref scratch);
                    state.Latch = 0;
                }
            }

            if (released && (binding.OnRelease is { } release) && GateOpen(gate: release.Gate, state: in state)) {
                ApplyEffects(effects: release.Effects, scratch: ref scratch);
            }

            for (var rule = 0; (rule < binding.OnFact.Length); rule++) {
                var trigger = binding.OnFact[rule];
                var holds = (FactHolds(fact: trigger.Fact) && GateOpen(gate: trigger.Gate, state: in state));
                var wasHeld = ((state.FactHeld & (1UL << rule)) != 0UL);

                state.FactHeld = (holds ? (state.FactHeld | (1UL << rule)) : (state.FactHeld & ~(1UL << rule)));

                // ONE edge vocabulary, the same ActionTriggerMode a world rule reads: EDGE fires on the crossing
                // alone and re-arms when the condition (fact AND gate together) stops holding; LEVEL fires every
                // tick it holds, which is what every fact trigger did before the mode existed.
                if (holds && !((trigger.Mode == ActionTriggerMode.Edge) && wasHeld)) {
                    ApplyEffects(effects: trigger.Effects, scratch: ref scratch);
                }
            }
        }
    }

    private void AdvanceActionState(ulong stepTicks) {
        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            var definition = m_actionStateDefinitions[slot];

            if ((definition.ResetFact is { } reset) && FactHolds(fact: reset)) {
                m_actionStateValues[slot] = definition.InitialValue;
                m_actionStateTimers[slot] = definition.InitialTicks;
            } else if (definition.Kind == ActionStateKind.Timer) {
                var previous = m_actionStateTimers[slot];
                m_actionStateTimers[slot] = SubtractSaturating(value: previous, amount: stepTicks);
                if (m_actionStateTimers[slot] != previous) {
                    MarkDurableDirty(slot: slot);
                }
            }
        }
    }

    private void ApplyDurableInput(ulong tick) {
        if (m_durableInputTick == 0) {
            return;
        }
        if (m_durableInputTick != tick) {
            if (m_durableInputTick < tick) {
                Array.Clear(array: m_durableInputPresent);
                m_durableInputTick = 0;
            }
            return;
        }

        for (var slot = 0; (slot < m_actionStateDefinitions.Length); slot++) {
            if (!m_durableInputPresent[slot]) {
                continue;
            }
            var definition = m_actionStateDefinitions[slot];
            var requested = (definition.Kind == ActionStateKind.Counter)
                ? m_durableInputValues[slot].Value
                : checked((long)m_durableInputTimers[slot]);
            ApplyRawState(slot: slot, requested: requested, writer: m_durableInputWriters[slot], reason: "tick-stamped durable input");
        }
        Array.Clear(array: m_durableInputPresent);
        m_durableInputTick = 0;
    }

    private void MarkDurableDirty(int slot, WorldDocumentWriteKind kind = WorldDocumentWriteKind.Set, FixedQ4816 operand = default) {
        if ((slot >= 0) && (m_actionStateDefinitions[slot].Lifetime == ActionStateLifetime.Durable)) {
            if (m_actionStateDirty[slot] && (m_actionStateDirtyKind[slot] != WorldDocumentWriteKind.Add)) {
                kind = WorldDocumentWriteKind.Set;
            } else if (m_actionStateDirty[slot] && (kind == WorldDocumentWriteKind.Add)) {
                operand += m_actionStateDirtyOperand[slot];
            }
            m_actionStateDirty[slot] = true;
            m_actionStateDirtyKind[slot] = kind;
            m_actionStateDirtyOperand[slot] = operand;
        }
    }

    private void ApplyRawState(int slot, long requested, string writer, string reason) {
        var definition = m_actionStateDefinitions[slot];
        var effective = definition.Envelope?.Clamp(value: requested, initial: InitialRaw(definition: in definition)) ?? requested;
        m_actionStateRequested[slot] = requested;
        if (definition.Kind == ActionStateKind.Counter) {
            m_actionStateValues[slot] = FixedQ4816.FromRawBits(value: effective);
        } else {
            m_actionStateTimers[slot] = checked((ulong)Math.Max(val1: 0L, val2: effective));
        }
        m_actionStateLastWriter[slot] = writer;
        m_actionStateLastReason[slot] = ((effective == requested) ? reason : $"{reason}; clamped by visited world");
    }

    private static long Raw(DurableStateValue value, ActionStateKind kind) => kind == ActionStateKind.Counter
        ? value.Value.Value
        : checked((long)value.TimerTicks);

    private static long InitialRaw(in CompiledActionStateSlot definition) => definition.Kind == ActionStateKind.Counter
        ? definition.InitialValue.Value
        : checked((long)definition.InitialTicks);

    private static string DescribeRaw(in CompiledActionStateSlot definition, long raw) => definition.Kind == ActionStateKind.Counter
        ? ((double)FixedQ4816.FromRawBits(value: raw)).ToString("0.####", CultureInfo.InvariantCulture)
        : raw.ToString(CultureInfo.InvariantCulture);

    private static string DescribeEnvelope(CompiledActionStateEnvelope? envelope, ActionStateKind kind) {
        if (envelope is null) {
            return "none";
        }
        string Describe(long raw) => kind == ActionStateKind.Counter
            ? ((double)FixedQ4816.FromRawBits(value: raw)).ToString("0.####", CultureInfo.InvariantCulture)
            : raw.ToString(CultureInfo.InvariantCulture);
        return envelope.Values is { } values
            ? $"set({string.Join(separator: ',', values.Select(Describe))})"
            : $"range({Describe(envelope.Minimum)}..{Describe(envelope.Maximum)})";
    }

    private bool FactHolds(ActionFact fact) {
        return fact switch {
            ActionFact.Grounded => m_grounded,
            ActionFact.Airborne => !m_grounded,
            ActionFact.Rising => (m_verticalVelocity > FixedQ4816.Zero),
            ActionFact.Falling => (m_verticalVelocity < FixedQ4816.Zero),
            ActionFact.Submerged => m_submerged,
            ActionFact.AtSurface => m_atSurface,
            _ => (m_affectingSubject >= 0),
        };
    }

    private bool GateOpen(CompiledPredicate[] gate, in LaneActionRuntime state) {
        foreach (var predicate in gate) {
            var holds = predicate.Kind switch {
                CompiledPredicateKind.Now => FactHolds(fact: predicate.Fact),
                CompiledPredicateKind.Recently => (state.Recency![predicate.RecencySlot] > 0),
                CompiledPredicateKind.CompareState => predicate.Comparison.Holds(value: m_actionStateValues[predicate.StateSlot], expected: predicate.Value),
                _ => (m_actionStateTimers[predicate.StateSlot] == 0),
            };

            if (!holds) {
                return false;
            }
        }

        return true;
    }

    private void ApplyEffects(CompiledBodyInstruction[] effects, ref BodyMotionScratch scratch) {
        foreach (var effect in effects) {
            if (effect.Target == ActionTarget.Self) {
                ExecuteOperation(instruction: in effect, scratch: ref scratch);
                continue;
            }

            var target = scratch.EffectTargets.Resolve(target: effect.Target);
            if ((target >= 0) && (scratch.EffectOutputs is not null)) {
                scratch.EffectOutputs.Add(item: new BodyEffectOutput(SourceIndex: scratch.EntityIndex, TargetIndex: target, Instruction: effect));
            }
        }
    }

    internal bool ApplyTargetedEffect(int sourceIndex, CompiledBodyInstruction instruction) {
        var slot = ((instruction.StateName is null) ? -1 : FindActionState(name: instruction.StateName));
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
            case BodyMotionOp.SetState when (slot >= 0) && (m_actionStateDefinitions[slot].Kind == ActionStateKind.Counter):
                ApplyRawState(slot: slot, requested: instruction.Value.Value, writer: "foreign-effect", reason: "setState");
                MarkDurableDirty(slot: slot);
                break;
            case BodyMotionOp.AddState when (slot >= 0) && (m_actionStateDefinitions[slot].Kind == ActionStateKind.Counter):
                var beforeAdd = m_actionStateValues[slot];
                ApplyRawState(slot: slot, requested: (m_actionStateValues[slot] + instruction.Value).Value, writer: "foreign-effect", reason: "addState");
                MarkDurableDirty(slot: slot, kind: WorldDocumentWriteKind.Add, operand: (m_actionStateValues[slot] - beforeAdd));
                break;
            case BodyMotionOp.StartTimer when (slot >= 0) && (m_actionStateDefinitions[slot].Kind == ActionStateKind.Timer):
                ApplyRawState(slot: slot, requested: checked((long)instruction.DurationTicks), writer: "foreign-effect", reason: "startTimer");
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

    private struct LaneActionRuntime {
        public ulong Latch;
        // One bit per OnFact trigger of this lane's binding, recording whether its condition (fact AND gate) held on
        // the previous evaluation — the edge latch. A lane's OnFact list is bounded by the same authored-effects
        // budget everything else here is; a 64-bit word is the same shape every other mask in this engine uses.
        public ulong FactHeld;
        public ulong[]? Recency;
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
        public FixedVector3 Facing;
        public FixedVector3 Right;
        public FixedVector3 TargetVelocity;
        public FixedQ4816 DirectVerticalVelocity;
        public FixedQ4816 SwimVerticalTarget;
        public FixedVector3 Velocity;
        public FixedVector3 NextPosition;
        public FixedQuaternion Orientation;
        public int EntityIndex;
        public BodyEffectTargets EffectTargets;
        public List<BodyEffectOutput>? EffectOutputs;
        public List<WorldDesignation>? DesignationOutputs;
        public List<WorldGeneratorInvocation>? GeneratorInvocations;
    }

    // A tape entry: the intent it holds while live, and the host ticks it has left. A mutable struct stored inline
    // in the ring buffer (no per-segment heap object) — the front segment's RemainingTicks is decremented in place through a
    // `ref` into its slot.
    private struct TapeSegment {
        public PlayerIntent Intent;
        public ulong RemainingTicks;
    }

    // Resolve argument-less channel taps at the only boundary that knows the host's actual fixed-step period. A
    // pending tap merges into the lane timer through the SAME MergeLaneTimer PressChannel's timed overload uses —
    // one merge rule for both press paths, never two that can quietly drift apart.
    private void MaterializeDefaultLanePresses(ulong stepTicks) {
        for (var ordinal = 0; (ordinal < ActionLaneCount); ordinal++) {
            if (!m_pendingDefaultChannelPress[ordinal]) {
                continue;
            }

            MergeLaneTimer(ordinal: ordinal, value: m_pendingDefaultChannelValue[ordinal], holdTicks: stepTicks);
            m_pendingDefaultChannelPress[ordinal] = false;
        }
    }
    private static ulong SubtractSaturating(ulong value, ulong amount) => ((value > amount) ? (value - amount) : 0UL);
}

using System.Globalization;
using System.Numerics;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// One authoritative entity body: a full 6DOF pose (a free position and a <see cref="System.Numerics.Quaternion"/>
/// attitude) advanced from a single merged <see cref="PlayerIntent"/> every host-owned fixed simulation step under its
/// <see cref="MotionModel"/> — grounded (the ground avatar) or free (space-sim full 6DOF flight). A scripted tape of
/// timed segments (a <c>player.run</c>/<c>player.fly</c> command) takes precedence while a segment is live; with the
/// tape empty the per-tick submitted intent drives instead (a seat's device image or the server-side wander producer,
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
internal sealed class WorldBody {
    private const long EngineTicksPerSecond = (long)EngineTicks.PerSecond;

    // The locomotion/jump feel (move & turn speeds, the ground plane, the jump kit). A seated player reads its live
    // profile's move/turn speed instead (that is what makes profile.set real-time); a profileless stand-in falls back
    // to the tuning's speeds. The ground plane and jump-kit rates are always the tuning's. Swapped in place by
    // RecompileKit when the body's kit row is retuned live (pose survives; only the compiled feel changes).
    private FixedMotionTuning m_tuning;

    // The action-track ceiling: a timed lane press (player.press) reads held for at most this many sim seconds. A held
    // live key/button has no timer (it reads held until its release edge).
    public const float MaxActionHoldSeconds = 2f;

    private static readonly FixedQ4816 s_maxActionHoldSeconds = FixedQ4816.FromInteger(value: 2L);
    private static readonly FixedQ4816 s_negativeOne = -FixedQ4816.One;
    private static readonly FixedVector3 s_unitX = new(X: FixedQ4816.One, Y: FixedQ4816.Zero, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_unitY = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);
    private static readonly FixedVector3 s_unitZ = new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: FixedQ4816.One);
    // The number of action lanes (bits) the track keeps timers for; the timer array is sized to it.
    private const int ActionLaneCount = 2;
    // The argument-less player.press tap lasts this many launcher-owned steps. Its engine-tick duration is materialized
    // by Advance from that step's supplied period, so this game-feel choice never defines or reconstructs a tick rate.
    internal const ulong DefaultActionHoldSteps = 2UL;
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
    // server-side producer's output (today only Wander), used only when no submission arrived and the source names it.
    private PlayerIntent m_submittedIntent;
    private bool m_hasSubmittedIntent;
    private PlayerIntent m_producerIntent;
    private bool m_hasProducerIntent;

    // The action track — the digital buttons, independent of the movement tape/sticks. Two peer producers of the same
    // lanes, merged every sub-step: m_heldLanes is the per-tick live-held device image the client submits (a button down
    // until its release edge; one-tick, republished each submission), m_pendingDefaultLanePresses holds argument-less
    // taps until Advance can derive their duration from its host step, and m_laneTimers are materialized timed presses
    // that read held until their per-lane auto-release timer drains. m_previousActions is the merged lane state one
    // sub-step ago — the model reads it to detect a lane's rising (fire) and release (cut) edges.
    private ActionLanes m_heldLanes;
    private ActionLanes m_pendingDefaultLanePresses;
    private ActionLanes m_previousActions;
    private readonly ulong[] m_laneTimers = new ulong[ActionLaneCount];

    // The vertical channel — the axis the bound vertical effects write. Under the grounded model gravity integrates it
    // and m_grounded gates/refreshes the composition facts; under the free model a written impulse bleeds to zero at
    // the tuning's rise gravity (no fall phase). Reset to a clean grounded rest by any hard teleport.
    private FixedQ4816 m_verticalVelocity;
    private bool m_grounded = true;

    // The response-shaped planar velocity — the ramped horizontal velocity the grounded model integrates. With an empty
    // response table it equals the commanded target every tick (today's instant snap, byte-identical); with a table it
    // converges on the target at the matching row's engage/release rate through m_planarRampAccumulator. SURVIVES a live
    // kit recompile (a retune must not jerk the crowd) but is zeroed by every hard teleport (no momentum across a warp).
    private FixedVector3 m_planarVelocity;
    private FixedRateAccumulator m_planarRampAccumulator = new(ticksPerSecond: EngineTicksPerSecond);
    // The response table's shared recency clocks — one per Recently gate across the whole table (allocated to match the
    // compiled tuning's RecencySlots), refreshed while the fact holds and decaying otherwise. Reset by a teleport and a
    // recompile (the clocks are bound to the OLD table shape).
    private ulong[] m_motionRecency = [];
    // The world contact field this body solves its swept grounded position against (null = collision off, so the body
    // keeps its flat ground-plane land) and the body's own capsule volume (null = a volumeless kit, never solved).
    private IContactField? m_contactField;
    private FixedWorldCollider? m_collider;
    // The body's up axis — the direction its gravity opposes, its planar move plane is perpendicular to, and its attitude
    // stands against. Constant +Y under the analytic provider (and collision off), so a flat world integrates
    // byte-identically; the FIELD provider derives it from the surface gradient each grounded step (arbitrary-up /
    // planetoid walking as a data choice), HELD from the previous step when a query is degenerate.
    private FixedVector3 m_up = s_unitY;
    // The number of surfaces (ground plane + colliders) the last grounded Advance resolved this body against — the
    // world.contacts read-back. Zero while collision is off.
    private int m_lastContactCount;

    // The per-lane action runtime: the compiled binding (null = unbound) and its mutable state — the press latch (the
    // buffer), the cooldown clock, the use counter (reset on ground contact), and one recency clock per
    // Recently-predicate instance. Allocated once at construction to match the bindings.
    private readonly CompiledActionSpec?[] m_laneBindings = new CompiledActionSpec?[ActionLaneCount];
    private readonly LaneActionRuntime[] m_laneActions = new LaneActionRuntime[ActionLaneCount];

    // The timed impulse overlay (the dash): a world-space velocity integrated through its own accumulator on top of
    // the model's motion for a bounded tick budget — integration itself is untouched. Cleared by hard teleports.
    private FixedVector3 m_overlayVelocity;
    private ulong m_overlayRemaining;
    private FixedVector3RateAccumulator m_overlayAccumulator = new(ticksPerSecond: EngineTicksPerSecond);

    // The motion model this player integrates under (Grounded by default). Set by player.motion; a switch is
    // authoritative (like a game-mode change), re-pinning/leveling the pose rather than gliding.
    private MotionModel m_model = MotionModel.Grounded;

    // The intent-source axis (Live by default; a peer takes the population's stored default at activation). Set by
    // player.control / the peer sweep. See IntentSource for the merge rule this selects.
    private IntentSource m_source = IntentSource.Live;

    // The screen-engagement route latch (disengaged by default). Set by player.engage/disengage. While engaged the
    // resolved intent is DIVERTED to the bound screen's machine instead of the avatar: Advance captures it into
    // m_engagedIntent and holds the avatar idle (no pose integration). ORTHOGONAL to m_source — engagement decides
    // where the intent GOES (avatar vs machine), the intent-source axis decides what FILLS it.
    private bool m_engaged;
    private PlayerIntent m_engagedIntent;

    // The avatar's sim position (Y set from the tuning's ground plane at construction). See Position.
    private FixedVector3 m_position;
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
    // (Warp/Face/Pose/SetModel, and an over-ceiling Reconcile) write Teleport; a smoothed Reconcile writes Correction.
    // Last write wins within a tick; TakeContinuity consumes it at snapshot emit.
    private EntityContinuity m_continuity = EntityContinuity.Continuous;

    /// <summary>Initializes a new instance of the <see cref="WorldBody"/> class under a locomotion/jump feel and its
    /// kit's lane bindings. A <see langword="null"/> tuning falls back to <see cref="MotionTuning.Default"/>; a
    /// <see langword="null"/> binding leaves that lane inert.</summary>
    /// <param name="tuning">The locomotion/jump feel to integrate under, or <see langword="null"/> for the default.</param>
    /// <param name="primary">The Primary lane's compiled binding.</param>
    /// <param name="secondary">The Secondary lane's compiled binding.</param>
    /// <param name="collider">The kit's compiled body volume, or <see langword="null"/> for a volumeless kit.</param>
    public WorldBody(MotionTuning? tuning = null, CompiledActionSpec? primary = null, CompiledActionSpec? secondary = null, FixedWorldCollider? collider = null) {
        var authoredTuning = (tuning ?? MotionTuning.Default);

        m_tuning = FixedMotionTuning.Compile(tuning: in authoredTuning);
        m_laneBindings[0] = primary;
        m_laneBindings[1] = secondary;
        m_collider = collider;

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            if (m_laneBindings[lane] is { RecencyFacts.Length: > 0 } binding) {
                m_laneActions[lane].Recency = new ulong[binding.RecencyFacts.Length];
            }
        }

        if (m_tuning.RecencySlots > 0) {
            m_motionRecency = new ulong[m_tuning.RecencySlots];
        }

        // The body rests on the tuning's ground plane at the origin.
        m_position = new FixedVector3(X: FixedQ4816.Zero, Y: m_tuning.GroundY, Z: FixedQ4816.Zero);
    }

    /// <summary>Sets (or clears) the world contact field this body's grounded integrator solves its swept position
    /// against — the population hands it the live field on activation and every rebuild. A <see langword="null"/> field
    /// (collision off) restores the flat ground-plane land.</summary>
    /// <param name="field">The world contact field, or <see langword="null"/> when collision is off.</param>
    public void SetContactField(IContactField? field) {
        m_contactField = field;
    }

    /// <summary>Swaps this body's compiled kit feel in place after a live kit retune — the once-at-the-boundary
    /// recompile of a mutated <see cref="WorldKit"/>: the fixed-point locomotion/jump tuning, the two lane bindings, and
    /// the motion model. The body keeps its pose, velocity, tape, source, and engagement; only the compiled feel
    /// changes. The per-lane action runtime (latch/cooldown/uses/recency) resets because it is bound to the OLD binding
    /// shape (a new binding may have a different recency-clock count), and the model switch re-pins the pose exactly as
    /// <c>player.motion</c> does (a no-op when unchanged).</summary>
    /// <param name="tuning">The kit's authored locomotion/jump tuning.</param>
    /// <param name="primary">The Primary lane's compiled binding.</param>
    /// <param name="secondary">The Secondary lane's compiled binding.</param>
    /// <param name="model">The kit's motion model.</param>
    /// <param name="collider">The kit's compiled body volume, or <see langword="null"/> for a volumeless kit.</param>
    public void RecompileKit(MotionTuning tuning, CompiledActionSpec? primary, CompiledActionSpec? secondary, MotionModel model, FixedWorldCollider? collider) {
        m_tuning = FixedMotionTuning.Compile(tuning: in tuning);
        m_laneBindings[0] = primary;
        m_laneBindings[1] = secondary;
        m_collider = collider;

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            m_laneActions[lane] = default;

            if (m_laneBindings[lane] is { RecencyFacts.Length: > 0 } binding) {
                m_laneActions[lane].Recency = new ulong[binding.RecencyFacts.Length];
            }
        }

        // The response recency clocks are bound to the OLD table shape (a new table may have a different Recently count),
        // so they reset on a recompile — but m_planarVelocity SURVIVES, because a live retune must not jerk the crowd.
        m_motionRecency = ((m_tuning.RecencySlots > 0) ? new ulong[m_tuning.RecencySlots] : []);

        // Authoritative model switch — re-pins/levels the pose the same way player.motion does; a no-op if unchanged.
        SetModel(model: model);
    }

    /// <summary>The profile this player is seated on — the live source of its move/turn speeds and look-invert (read
    /// every <see cref="Advance"/>, so a <c>profile.set</c> edit is real-time) and the color the avatar renders. May be
    /// <see langword="null"/> before a profile is assigned, in which case the tuning's default rates apply.</summary>
    public WorldProfile? Profile { get; set; }

    /// <summary>The avatar's current world-space position (the ground foot point under the grounded model, where Y is
    /// pinned to the plane; a free craft's position is unconstrained in all three axes).</summary>
    public Vector3 Position => m_position.ToVector3();
    /// <summary>The authoritative deterministic position.</summary>
    public FixedVector3 FixedPosition => m_position;
    /// <summary>The avatar's current heading in radians (0 = facing -Z; increases turning left / counter-clockwise).
    /// Under the grounded model this returns the authoritative heading scalar <c>m_yaw</c> directly (the orientation is a
    /// pure yaw rotation built from it, so decomposing it back out would be a redundant round-trip on the hot wander
    /// path). Under the free model, where the full attitude is authoritative and <c>m_yaw</c> is inert, it is the yaw
    /// component of <see cref="Orientation"/>. The <c>player.where</c> read-back and <see cref="DescribePose"/> decompose
    /// the canonical orientation directly, bypassing this property.</summary>
    public float Yaw => (float)(double)FixedYaw;
    /// <summary>The authoritative deterministic heading.</summary>
    public FixedQ4816 FixedYaw => ((m_model == MotionModel.Grounded) ? m_yaw : ExtractYaw(orientation: m_orientation));
    /// <summary>The avatar's full 6DOF attitude — the canonical orientation a camera rig or a dynamic transform rides.
    /// Pure yaw about world up under the grounded model; an arbitrary body attitude under the free model.</summary>
    public Quaternion Orientation => m_orientation.ToQuaternion();
    /// <summary>The authoritative deterministic orientation.</summary>
    public FixedQuaternion FixedOrientation => m_orientation;
    /// <summary>The motion model this player currently integrates under (the <c>player.motion</c> verb's read/write).</summary>
    public MotionModel Model => m_model;
    /// <summary>What fills this entity's intent gaps between tape segments — the per-entity axis (the
    /// <c>player.control</c> verb's read/write). <see cref="IntentSource.Live"/> by default; see
    /// <see cref="IntentSource"/> for the merge rule.</summary>
    public IntentSource Source => m_source;

    /// <summary>Whether the body is grounded this tick (resting on the ground plane or a walkable solid surface) — the
    /// <c>world.contacts</c> read-back.</summary>
    public bool Grounded => m_grounded;

    /// <summary>The body's response-shaped planar speed (world units/second) — the coast/momentum witness the
    /// <c>world.contacts</c> read reports.</summary>
    public float PlanarSpeed => (float)(double)m_planarVelocity.Length;

    /// <summary>The last <see cref="Advance"/>'s grounded witness echoed as a count — <c>1</c> when the resolve grounded
    /// this body, <c>0</c> otherwise (and always <c>0</c> while collision is off). The field-contact seam reports only a
    /// grounded bool, so this is not a per-surface tally: a body depenetrated by a wall while airborne still reads
    /// <c>0</c>. Introspection-only, surfaced by the <c>world.contacts</c> read-back.</summary>
    public int ContactCount => m_lastContactCount;

    /// <summary>Whether this player is ENGAGED on a diegetic screen — the route latch the engagement table sets. While
    /// engaged its resolved per-frame intent is delivered to the screen's machine (read via <see cref="EngagedIntent"/>)
    /// instead of driving the avatar, which stands idle.</summary>
    public bool Engaged => m_engaged;

    /// <summary>The intent resolved on the most recent engaged <see cref="Advance"/> — the movement channels and action
    /// lanes the engagement route translates into the bound machine's joypad buttons. The <see cref="PlayerIntent"/>
    /// default (all channels zero) before the first engaged advance or while disengaged.</summary>
    public PlayerIntent EngagedIntent => m_engagedIntent;

    /// <summary>Presses an action lane for the default two-host-step tap. The concrete engine-tick duration is derived
    /// by the next <see cref="Advance"/> from its <c>stepTicks</c>; this body never reconstructs the launcher's cadence.
    /// A repeated press never shortens an in-flight timed hold.</summary>
    /// <param name="lane">The lane to hold.</param>
    public void PressLane(ActionLanes lane) {
        var index = System.Numerics.BitOperations.TrailingZeroCount(value: (uint)lane);

        if ((index < 0) || (index >= ActionLaneCount)) {
            return;
        }

        m_pendingDefaultLanePresses |= (ActionLanes)(1 << index);
    }

    /// <summary>Presses an action lane for a timed auto-release — the scripted/wire path (<c>player.press</c>): the lane
    /// reads held for <paramref name="holdSeconds"/> of sim time (clamped to <c>[0, </c><see cref="MaxActionHoldSeconds"/><c>]</c>),
    /// decremented per sub-step, then releases itself. A short hold is a short hop (the release cuts the rising jump), a
    /// long hold the full arc. Independent of a live hold on the same lane (the longer of the two wins) and of the
    /// movement tape, so <c>player.run … ; player.press jump</c> jumps a runner mid-segment. A non-positive hold is
    /// ignored. Unlike the device-held lane image (<see cref="SetHeldLanes"/>), this wire path overlays under every
    /// <see cref="IntentSource"/>.</summary>
    /// <param name="lane">The lane to hold.</param>
    /// <param name="holdSeconds">How long (sim seconds) the lane reads held before auto-releasing.</param>
    public void PressLane(ActionLanes lane, float holdSeconds) {
        var index = System.Numerics.BitOperations.TrailingZeroCount(value: (uint)lane);

        if ((index < 0) || (index >= ActionLaneCount)) {
            return;
        }

        // Take the longer of any in-flight timed hold and this one — a re-press must never shorten a lane already held.
        var hold = FixedQ4816.Clamp(
            value: FixedQ4816.FromDouble(value: holdSeconds),
            minimum: FixedQ4816.Zero,
            maximum: s_maxActionHoldSeconds
        );

        m_laneTimers[index] = Math.Max(val1: m_laneTimers[index], val2: DurationEngineTicks(duration: hold));
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
    /// the submitted stream, used only while <see cref="Source"/> names its producer (today
    /// <see cref="IntentSource.Wander"/>). One-tick, consumed like the submitted image; same clamps.</summary>
    /// <param name="intent">The producer's fixed-point movement and action image.</param>
    public void StageProducerIntent(in PlayerIntent intent) {
        m_producerIntent = Clamped(intent: in intent);
        m_hasProducerIntent = true;
    }
    // The shared stick-range clamp both one-tick images pass through.
    private static PlayerIntent Clamped(in PlayerIntent intent) {
        return new PlayerIntent(
            MoveForward: FixedQ4816.Clamp(value: intent.MoveForward, minimum: s_negativeOne, maximum: FixedQ4816.One),
            MoveStrafe: FixedQ4816.Clamp(value: intent.MoveStrafe, minimum: s_negativeOne, maximum: FixedQ4816.One),
            Turn: FixedQ4816.Clamp(value: intent.Turn, minimum: s_negativeOne, maximum: FixedQ4816.One),
            MoveUp: FixedQ4816.Clamp(value: intent.MoveUp, minimum: s_negativeOne, maximum: FixedQ4816.One),
            Pitch: FixedQ4816.Clamp(value: intent.Pitch, minimum: s_negativeOne, maximum: FixedQ4816.One),
            Roll: FixedQ4816.Clamp(value: intent.Roll, minimum: s_negativeOne, maximum: FixedQ4816.One),
            Actions: intent.Actions
        );
    }
    /// <summary>Stages this tick's live-held device lane image — the action overlay a held jump button rides onto a
    /// tape-driven runner. One-tick, consumed by the next <see cref="Advance"/>; the client republishes it each
    /// submission, and the merge admits it only under <see cref="IntentSource.Live"/>.</summary>
    /// <param name="lanes">The lanes live-held this tick.</param>
    public void SetHeldLanes(ActionLanes lanes) {
        m_heldLanes = lanes;
    }
    /// <summary>Enqueues a timed scripted segment onto the tape: while it is live it drives the avatar with
    /// <paramref name="intent"/>, overriding the held keys (or, on a population entry, its wander), for
    /// <paramref name="seconds"/> of advance time. All six channels are clamped to <c>[-1, 1]</c> — the planar three
    /// (<c>player.run</c>) leave the 6DOF three at their zero default, the full six (<c>player.fly</c>) carry all of them.
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
            Intent = new PlayerIntent(
                MoveForward: FixedQ4816.Clamp(value: intent.MoveForward, minimum: s_negativeOne, maximum: FixedQ4816.One),
                MoveStrafe: FixedQ4816.Clamp(value: intent.MoveStrafe, minimum: s_negativeOne, maximum: FixedQ4816.One),
                Turn: FixedQ4816.Clamp(value: intent.Turn, minimum: s_negativeOne, maximum: FixedQ4816.One),
                MoveUp: FixedQ4816.Clamp(value: intent.MoveUp, minimum: s_negativeOne, maximum: FixedQ4816.One),
                Pitch: FixedQ4816.Clamp(value: intent.Pitch, minimum: s_negativeOne, maximum: FixedQ4816.One),
                Roll: FixedQ4816.Clamp(value: intent.Roll, minimum: s_negativeOne, maximum: FixedQ4816.One)
            ),
            RemainingTicks = DurationEngineTicks(duration: duration),
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
    /// <summary>Teleports the avatar to a ground-plane position, leaving its heading unchanged. A hard teleport pops: the
    /// previous-pose anchor is reset to the new position so the renderer never interpolates across the jump, and any
    /// in-flight <see cref="Reconcile"/> smoothing offset is dropped.</summary>
    /// <param name="x">The world X coordinate.</param>
    /// <param name="z">The world Z coordinate.</param>
    public void Warp(float x, float z) {
        Warp(
            x: FixedQ4816.FromDouble(value: x),
            z: FixedQ4816.FromDouble(value: z)
        );
    }
    /// <summary>Teleports the avatar using deterministic coordinates.</summary>
    public void Warp(FixedQ4816 x, FixedQ4816 z) {
        m_position = new FixedVector3(
            X: x,
            Y: m_tuning.GroundY,
            Z: z
        );
        CommitTeleport(resetRotation: false);
        m_continuity = EntityContinuity.Teleport;
    }
    /// <summary>Sets the avatar's heading directly and levels it — the planar heading shorthand, so the attitude becomes
    /// a pure yaw rotation (pitch and roll zeroed). A hard teleport pops: the previous-pose anchor is reset so the
    /// renderer never interpolates the heading across the jump, and any in-flight <see cref="Reconcile"/> smoothing
    /// offset is dropped. Use <see cref="Pose(float, float, float, float, float, float)"/> to set a full 6DOF attitude.</summary>
    /// <param name="yawRadians">The new heading in radians (0 = facing -Z).</param>
    public void Face(float yawRadians) {
        Face(yawRadians: FixedQ4816.FromDouble(value: yawRadians));
    }
    /// <summary>Sets the avatar heading using deterministic radians.</summary>
    public void Face(FixedQ4816 yawRadians) {
        m_yaw = yawRadians;
        m_orientation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw);
        // Position is unchanged and the vertical/jump state keeps running (resetVertical: false) — Face only snaps the
        // heading.
        CommitTeleport(resetPosition: false, resetVertical: false);
        m_continuity = EntityContinuity.Teleport;
    }
    /// <summary>Teleports the avatar to a full 6DOF pose — a free position and a Tait-Bryan attitude (yaw about world up,
    /// pitch about the body right, roll about the body forward), the space-sim counterpart to
    /// <see cref="Warp(float, float)"/> + <see cref="Face(float)"/>. A hard teleport pops: the previous-pose anchor is reset to the new pose so the renderer never
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
        m_orientation = OrientationFromEuler(yaw: m_yaw, pitch: pitchRadians, roll: rollRadians);
        CommitTeleport();
        m_continuity = EntityContinuity.Teleport;
    }
    /// <summary>Sets the entity's <see cref="MotionModel"/>. A switch is authoritative, like a game-mode change: it does
    /// not glide. Switching to <see cref="MotionModel.Grounded"/> re-pins the position to the ground plane and levels the
    /// attitude to its yaw projection (dropping pitch/roll); either switch reports as a snapshot teleport. A no-op if the
    /// model is unchanged.</summary>
    /// <param name="model">The motion model to integrate under.</param>
    public void SetModel(MotionModel model) {
        if (model == m_model) {
            return;
        }

        m_model = model;

        if (model == MotionModel.Grounded) {
            // Snap onto the ground plane and level the craft to its heading — the grounded constraints applied at the
            // switch, and landed (CommitTeleport's resetVertical below) so a craft dropping in arrives at rest, not
            // mid-jump.
            m_yaw = ExtractYaw(orientation: m_orientation);
            m_position = m_position with { Y = m_tuning.GroundY };
            m_orientation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw);
        } else {
            // Free flight inherits the grounded attitude (already a pure yaw); m_yaw goes inert.
            m_yaw = ExtractYaw(orientation: m_orientation);
        }

        // Land the vertical state only on the switch to grounded (free flight owns its own position and keeps no jump
        // state).
        CommitTeleport(resetVertical: (model == MotionModel.Grounded));
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
    /// <see cref="Warp(float, float)"/> + <see cref="Face(float)"/>), and the tick's snapshot carries
    /// <see cref="EntityContinuityKind.Correction"/> so the client eases its render error to zero over
    /// <paramref name="seconds"/>. Snap escape: if the position error exceeds
    /// <see cref="EntityContinuity.MaxSmoothError"/> the snapshot reports a plain teleport instead, so a huge
    /// correction pops. Easing is client presentation state only — the sim never reads it and <c>player.where</c>
    /// never includes it.</summary>
    /// <param name="x">The authoritative world X coordinate.</param>
    /// <param name="z">The authoritative world Z coordinate.</param>
    /// <param name="yawRadians">The authoritative heading in radians (0 = facing -Z).</param>
    /// <param name="seconds">The smoothing window over which the client's render error eases to zero.</param>
    public void Reconcile(float x, float z, float yawRadians, float seconds) {
        var oldPosition = m_position.ToVector3();

        // Snap the sim pose immediately — the same end-state a Warp+Face would land (a planar, levelled correction).
        // The tape and any timed press are untouched: a correction is authority over the pose, not over what the
        // entity is trying to do.
        var fixedYaw = FixedQ4816.FromDouble(value: yawRadians);

        m_position = new FixedVector3(
            X: FixedQ4816.FromDouble(value: x),
            Y: m_tuning.GroundY,
            Z: FixedQ4816.FromDouble(value: z)
        );
        m_yaw = fixedYaw;
        m_orientation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: fixedYaw);
        CommitTeleport();

        // Over the ceiling the correction pops (a respawn/teleporter-scale jump would streak if eased); within it the
        // client eases from its previous rendered pose to authority over the window.
        var positionError = (oldPosition - m_position.ToVector3());

        m_continuity = ((positionError.Length() > EntityContinuity.MaxSmoothError)
            ? EntityContinuity.Teleport
            : EntityContinuity.Correction(seconds: seconds));
    }
    /// <summary>Clears every intent producer this body owns: drops the whole tape and the staged transient images, so
    /// the entity comes to a full stop. This is the <c>player.stop</c> panic verb's server half; the client seat drops
    /// its held device state in the same command.</summary>
    public void Stop() {
        ClearTape();
        // An in-flight jump arc still resolves under gravity and lands — clearing the images only stops new input.
        ClearTransientInput();
    }

    // Drop the staged one-tick input images (the submitted, producer, and held-lane images) — the source/engagement
    // transition hygiene. The tape and any timed lane press are left running.
    private void ClearTransientInput() {
        m_submittedIntent = default;
        m_hasSubmittedIntent = false;
        m_producerIntent = default;
        m_hasProducerIntent = false;
        m_heldLanes = ActionLanes.None;
    }

    /// <summary>Consumes this tick's continuity hint for the snapshot: how the pose changed (ordinary integration, a
    /// hard teleport, or a smoothed correction). Resets to <see cref="EntityContinuityKind.Continuous"/>; within a tick
    /// the last authoritative write wins.</summary>
    public EntityContinuity TakeContinuity() {
        var continuity = m_continuity;

        m_continuity = EntityContinuity.Continuous;

        return continuity;
    }
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
    /// <c>[player.where: p{N} pos=(x.xx, y.yy, z.zz) yaw=ddd° pitch=ddd° roll=ddd°]</c>. One format always: a grounded
    /// entity prints <c>y=0.00 pitch=0 roll=0</c>, deriving every angle from the canonical orientation. The bare planar
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
    // position and vertical state but preserves rotation; full Pose/Reconcile operations reset every carry. SetModel
    // resets the pose carries and only resets vertical state when switching to grounded.
    private void CommitTeleport(bool resetPosition = true, bool resetVertical = true, bool resetRotation = true) {
        if (resetPosition) {
            m_positionAccumulator.Reset();
            // A hard reposition cancels any in-flight impulse overlay (a warp never carries a dash across).
            m_overlayVelocity = default;
            m_overlayRemaining = 0;
            m_overlayAccumulator.Reset();
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
    /// segment if one is queued, otherwise the tick's submitted intent. The <see cref="MotionModel"/> chooses the
    /// integration — <see cref="MotionModel.Grounded"/> turns the heading then steps along the new facing with Y pinned
    /// to the ground plane; <see cref="MotionModel.Free"/> composes the body-frame yaw/pitch/roll rates into the
    /// attitude then flies along all three body axes with no ground pin.</summary>
    public void Advance(ulong stepTicks) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);
        MaterializeDefaultLanePresses(stepTicks: stepTicks);

        // The full merged intent for this sub-step: NextIntent expresses the whole precedence (movement channels —
        // tape > submitted, gated by the possession latch — with the action-track lanes overlaid).
        var intent = NextIntent(stepTicks: stepTicks);

        if (m_engaged) {
            // Engaged: the resolved intent is DIVERTED to the bound screen's machine (read back by the engagement
            // route), not the avatar — the avatar stands idle (no pose integration, so the snapshot holds it stable).
            // The action track below still advances, so a timed press drains identically whether the intent drives the
            // avatar or the machine.
            m_engagedIntent = intent;
        } else {
            // Read the rates live off the seated profile every frame (a profile.set edit is real-time); a profileless
            // stand-in falls back to the tuning's speeds.
            var moveSpeed = (Profile?.FixedMoveSpeed ?? m_tuning.MoveSpeed);
            var turnSpeed = (Profile?.FixedTurnSpeed ?? m_tuning.TurnSpeed);

            if (m_model == MotionModel.Free) {
                IntegrateFree(intent: intent, moveSpeed: moveSpeed, turnSpeed: turnSpeed, stepTicks: stepTicks);
            } else {
                IntegrateGrounded(intent: intent, moveSpeed: moveSpeed, turnSpeed: turnSpeed, stepTicks: stepTicks);
            }

            // The timed impulse overlay (a fired dash) rides on top of whichever model integrated, through its own
            // accumulator, for its bounded tick budget. Planar-only in the default compositions, so the grounded pin
            // is untouched; skipped entirely while no overlay is live.
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

        // Advance the action track for the next sub-step: remember this step's lane state (the models read it to detect
        // a rising/release edge) and drain the timed presses. Done for both models, so a jump press under free flight
        // ticks the track but changes no pose.
        m_previousActions = intent.Actions;

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            m_laneTimers[lane] = SubtractSaturating(value: m_laneTimers[lane], amount: stepTicks);
        }

        // The held-lane image is a one-tick publish, like the submitted intent: the client republishes it every
        // submission, so a missed tick reads no lanes rather than a stale hold.
        m_heldLanes = ActionLanes.None;
    }

    // The grounded integration — planar math for the horizontal axes plus the bound vertical action on the other.
    // Horizontal: turn the heading, step along the fresh facing/right (instant velocity — no ground/air acceleration
    // yet). A pure-yaw facing/right carry no Y, so the horizontal step never disturbs the vertical axis the action owns.
    // Vertical: under the VerticalImpulse binding, read the Primary channel's edges (rising fires, release cuts) and
    // integrate the arc with the tuning's constants. The MoveUp/Pitch/Roll channels stay inert; a resting avatar holds
    // y = GroundY exactly.
    private void IntegrateGrounded(PlayerIntent intent, FixedQ4816 moveSpeed, FixedQ4816 turnSpeed, ulong stepTicks) {
        // --- Horizontal (the planar math). ---
        var angleStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: (intent.Turn * turnSpeed),
                Y: FixedQ4816.Zero,
                Z: FixedQ4816.Zero
            ),
            elapsedTicks: stepTicks
        );

        m_yaw += angleStep.X;

        // The body up axis: constant +Y under the analytic provider / collision off (the flat world, byte-identical);
        // the field gradient at the foot under the FIELD provider (a planetoid, an inverted ceiling). The attitude is
        // the yaw ABOUT that up — a pure yaw rotation when up is +Y (the exact prior orientation), tilted into the up
        // frame otherwise. The `up == +Y` guard keeps the flat path bit-identical without relying on FromTo's rounding.
        var up = ResolveUp();
        var yawRotation = FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: m_yaw);
        var orientation = ((up == s_unitY) ? yawRotation : (FixedQuaternion.FromTo(from: s_unitY, to: up) * yawRotation));
        var facing = orientation.Rotate(vector: -s_unitZ);
        var right = orientation.Rotate(vector: s_unitX);
        // --- Shape: the commanded target planar velocity converges through the response table (the ramp), replacing
        // the direct assignment an unopted world instant-snaps. The facing/right lie in the up-tangent plane, so the
        // target is a tangent-plane velocity (its Y is zero when up is +Y). ---
        var planarTarget = (((facing * intent.MoveForward) + (right * intent.MoveStrafe)) * moveSpeed);
        var planarVelocity = ShapePlanarVelocity(target: planarTarget, intent: in intent, stepTicks: stepTicks);

        m_orientation = orientation;

        // --- The bound actions: latch/clock upkeep and trigger evaluation run after the planar step and before
        // gravity, so a fired effect shapes this tick's motion. ---
        ProcessLaneActions(intent: in intent, stepTicks: stepTicks);

        // Asymmetric gravity (heavier falling) + terminal velocity, then integrate and land on the ground crossing.
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

        // Semi-implicit Euler: gravity updates velocity first, then the exact engine-tick accumulator integrates the
        // resulting velocity. X/Z use the same carry path, so a constant one-unit/second walk advances exactly one raw
        // unit per second rather than repeating one rounded per-step displacement. The tangential (planar) and normal
        // (along up) parts recombine into the full velocity — for up = +Y this is exactly (planar.X, vertical, planar.Z).
        var velocity = (planarVelocity + (up * m_verticalVelocity));
        var step = m_positionAccumulator.Integrate(
            ratePerSecond: velocity,
            elapsedTicks: stepTicks
        );
        var nextPosition = (m_position + step);

        // --- Resolve: the swept position becomes a legal position through the contact field, which also DERIVES
        // grounded (rather than reading it off a plane compare) and kills velocity into any resolved surface. With no
        // field (collision off) or a volumeless kit, the flat ground-plane land runs exactly as before, byte-identically.
        if ((m_contactField is { } field) && (m_collider is { } collider)) {
            // Solve the FULL velocity (tangential + normal-along-up), then decompose the resolved result back against up.
            // For up = +Y this reduces exactly to the flat pack/unpack: normal = resolved.Y, planar = (resolved.X, 0, resolved.Z).
            var resolvedVelocity = (m_planarVelocity + (up * m_verticalVelocity));

            m_grounded = field.Resolve(position: ref nextPosition, velocity: ref resolvedVelocity, radius: collider.Radius, height: collider.Height);
            m_position = nextPosition;

            var resolvedNormal = FixedVector3.Dot(left: resolvedVelocity, right: up);

            m_planarVelocity = (resolvedVelocity - (up * resolvedNormal));

            if (resolvedNormal != m_verticalVelocity) {
                m_verticalVelocity = resolvedNormal;
                m_verticalVelocityAccumulator.Reset();
            }

            if (m_grounded) {
                m_positionAccumulator.ResetY();
                ResetLaneUses();
            }

            m_lastContactCount = (m_grounded ? 1 : 0);
        } else if (nextPosition.Y <= m_tuning.GroundY) {
            // Land: snap to the plane exactly (so a resting avatar reads y = 0.00), zero any downward velocity, ground
            // it, and refill the lane-use budgets (ground contact).
            m_position = nextPosition with { Y = m_tuning.GroundY };
            m_positionAccumulator.ResetY();

            if (m_verticalVelocity < FixedQ4816.Zero) {
                m_verticalVelocity = FixedQ4816.Zero;
                m_verticalVelocityAccumulator.Reset();
            }

            m_grounded = true;
            m_lastContactCount = 0;
            ResetLaneUses();
        } else {
            m_position = nextPosition;
            m_grounded = false;
            m_lastContactCount = 0;
        }
    }

    // The body up axis this grounded step integrates against. The contact field answers it (constant +Y from the
    // analytic provider, the surface gradient from the field provider); a degenerate field query leaves the held value
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

        var hasInput = ((intent.MoveForward != FixedQ4816.Zero) || (intent.MoveStrafe != FixedQ4816.Zero));

        foreach (var row in response) {
            if (!MotionGateOpen(gate: row.Gate)) {
                continue;
            }

            var rate = (hasInput ? row.EngageRate : row.ReleaseRate);
            var maxDelta = m_planarRampAccumulator.Integrate(ratePerSecond: rate, elapsedTicks: stepTicks);

            m_planarVelocity = MoveToward(current: m_planarVelocity, target: target, maxDelta: maxDelta);

            return m_planarVelocity;
        }

        m_planarVelocity = target;

        return target;
    }

    // A motion-response gate: a flattened conjunction of BODY-FACT predicates only (Now/Recently — the validator rejects
    // the lane-scoped CooldownElapsed/UsesBelow kinds on a response gate). Every element must hold.
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

    // Move current toward target by at most maxDelta (the ramp step) — four fixed-point ops; deliberately NOT promoted
    // to Puck.Maths (that would add a gameplay verb to the deterministic numerics toolkit; Arc 2's provider is the
    // second-caller moment that justifies promotion).
    private static FixedVector3 MoveToward(FixedVector3 current, FixedVector3 target, FixedQ4816 maxDelta) {
        var delta = (target - current);
        var distance = delta.Length;

        return (((distance <= maxDelta) || (distance <= FixedQ4816.Zero))
            ? target
            : (current + ((delta / distance) * maxDelta)));
    }

    // Reset the grounded vertical state to a clean rest on the plane — called by every hard teleport, so a jump never
    // survives an authoritative reposition. The action track (held/timed lanes) is left alone: a teleport moves the
    // body, not the player's buttons.
    private void ResetVertical() {
        m_verticalVelocity = FixedQ4816.Zero;
        m_verticalVelocityAccumulator.Reset();
        m_positionAccumulator.ResetY();
        m_grounded = true;
        ResetLaneUses();

        // A teleport must not carry momentum: drop the ramped planar velocity, its accumulator carry, and the response
        // table's recency clocks.
        m_planarVelocity = default;
        m_planarRampAccumulator.Reset();
        Array.Clear(array: m_motionRecency);
    }

    // Ground contact resets every lane's use counter — the UsesBelow budget refills on landing.
    private void ResetLaneUses() {
        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            m_laneActions[lane].Uses = 0;
        }
    }

    // The free integration — full 6DOF in the body frame. Compose the yaw/pitch/roll rates (each × turnSpeed) into a
    // body-frame delta and post-multiply it into the attitude (q ← normalize(q · Δq), so the rates rotate about the
    // body's own axes), then fly along the fresh body axes: velocity = (forward·MoveForward + right·MoveStrafe +
    // up·MoveUp) · moveSpeed, with no ground pin and no gravity. The bound actions run after the attitude update, so a
    // fired vertical impulse (the surge) rides this tick; the written channel bleeds to zero at the tuning's rise
    // gravity (no fall phase).
    private void IntegrateFree(PlayerIntent intent, FixedQ4816 moveSpeed, FixedQ4816 turnSpeed, ulong stepTicks) {
        var angularStep = m_rotationAccumulator.Integrate(
            ratePerSecond: new FixedVector3(
                X: (intent.Turn * turnSpeed),
                Y: (intent.Pitch * turnSpeed),
                Z: (intent.Roll * turnSpeed)
            ),
            elapsedTicks: stepTicks
        );
        var delta = ((FixedQuaternion.FromAxisAngle(axis: s_unitY, angle: angularStep.X)
            * FixedQuaternion.FromAxisAngle(axis: s_unitX, angle: angularStep.Y))
            * FixedQuaternion.FromAxisAngle(axis: s_unitZ, angle: angularStep.Z));

        m_orientation = (m_orientation * delta).Normalize();
        ProcessLaneActions(intent: in intent, stepTicks: stepTicks);

        var facing = m_orientation.Rotate(vector: -s_unitZ);
        var right = m_orientation.Rotate(vector: s_unitX);
        var up = m_orientation.Rotate(vector: s_unitY);
        var velocity = ((((facing * intent.MoveForward) + (right * intent.MoveStrafe)) + (up * intent.MoveUp)) * moveSpeed);

        if (m_verticalVelocity != FixedQ4816.Zero) {
            // Fold the vertical channel in and bleed it toward zero at the rise gravity — an impulse decelerating the
            // way a rising jump does, with no fall phase since free flight has no gravity. The whole branch is skipped
            // at zero, so free bodies without a fired impulse integrate byte-identically to before.
            velocity = velocity with { Y = (velocity.Y + m_verticalVelocity) };

            if (m_verticalVelocity > FixedQ4816.Zero) {
                var bleed = m_verticalVelocityAccumulator.Integrate(ratePerSecond: -m_tuning.RiseGravity, elapsedTicks: stepTicks);
                var next = (m_verticalVelocity + bleed);

                m_verticalVelocity = ((next < FixedQ4816.Zero) ? FixedQ4816.Zero : next);
            } else {
                var bleed = m_verticalVelocityAccumulator.Integrate(ratePerSecond: m_tuning.RiseGravity, elapsedTicks: stepTicks);
                var next = (m_verticalVelocity + bleed);

                m_verticalVelocity = ((next > FixedQ4816.Zero) ? FixedQ4816.Zero : next);
            }

            if (m_verticalVelocity == FixedQ4816.Zero) {
                m_verticalVelocityAccumulator.Reset();
            }
        }

        m_position += m_positionAccumulator.Integrate(
            ratePerSecond: velocity,
            elapsedTicks: stepTicks
        );
    }

    // Resolve this sub-step's full intent by the IntentSource merge rule: a live tape segment takes precedence for the
    // movement channels (consumed whole-frame, dropped when its time runs out; expired/empty front segments are
    // skipped first, so a drained tape falls through the same frame it empties); with the tape dry, the tick's
    // submitted intent (admitted unless Idle), else the producer image (iff the source names it), else zero. The
    // action-track lanes are then overlaid, so a wire player.press jumps a tape-driven runner.
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
            movement = ((m_source != IntentSource.Idle) && m_hasSubmittedIntent) ? m_submittedIntent
                : (((m_source == IntentSource.Wander) && m_hasProducerIntent) ? m_producerIntent : default);
        }

        // Both one-tick images are a one-step publish, even when a tape or the source masked them this time. Their
        // producers must republish on the next authoritative step, matching the snapshot discipline of every other
        // input source.
        m_submittedIntent = default;
        m_hasSubmittedIntent = false;
        m_producerIntent = default;
        m_hasProducerIntent = false;

        // Overlay the action track: a lane reads held if it is live-held (the tick's device image, admitted under
        // Live only) or its timed press still has time left. The producers are unioned, so a submitted jumper, wire
        // tap, and device hold can all light a lane.
        var lanes = (movement.Actions | ((m_source == IntentSource.Live) ? m_heldLanes : ActionLanes.None));

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            if (m_laneTimers[lane] > 0) {
                lanes |= (ActionLanes)(1 << lane);
            }
        }

        return (movement with { Actions = lanes });
    }

    // Advance the ring past its front segment (a FIFO dequeue): step the head and shrink the live count.
    private void DropFrontSegment() {
        m_tapeHead = ((m_tapeHead + 1) % m_tape.Length);
        m_tapeCount--;
    }

    // The per-tick action machinery: for each bound lane, refresh the recency clocks (a Recently window refills while
    // its fact holds and decays otherwise), decay the cooldown, latch a press edge (the buffer), then fire the press
    // trigger while its latch is pending and its gate holds, and the release trigger on its edge — each fire applying
    // its compiled effects in order and consuming the latch. Runs after attitude/planar integration and before
    // gravity/vertical resolution, so effects shape the same tick.
    private void ProcessLaneActions(in PlayerIntent intent, ulong stepTicks) {
        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            if (m_laneBindings[lane] is not { } binding) {
                continue;
            }

            ref var state = ref m_laneActions[lane];
            var mask = (ActionLanes)(1 << lane);
            var pressed = ((intent.Actions & ~m_previousActions & mask) != ActionLanes.None);
            var released = ((~intent.Actions & m_previousActions & mask) != ActionLanes.None);

            for (var slot = 0; (slot < binding.RecencyFacts.Length); slot++) {
                state.Recency![slot] = (FactHolds(fact: binding.RecencyFacts[slot])
                    ? binding.RecencyWindows[slot]
                    : SubtractSaturating(value: state.Recency[slot], amount: stepTicks));
            }

            state.Cooldown = SubtractSaturating(value: state.Cooldown, amount: stepTicks);

            if (binding.OnPress is { } press) {
                state.Latch = (pressed ? press.LatchTicks : SubtractSaturating(value: state.Latch, amount: stepTicks));

                if ((state.Latch > 0) && GateOpen(gate: press.Gate, state: in state)) {
                    ApplyEffects(effects: press.Effects, state: ref state);
                    state.Latch = 0;
                }
            }

            if (released && (binding.OnRelease is { } release) && GateOpen(gate: release.Gate, state: in state)) {
                ApplyEffects(effects: release.Effects, state: ref state);
            }
        }
    }

    // The engine-published facts the predicates gate on.
    private bool FactHolds(ActionFact fact) {
        return fact switch {
            ActionFact.Grounded => m_grounded,
            ActionFact.Airborne => !m_grounded,
            ActionFact.Rising => (m_verticalVelocity > FixedQ4816.Zero),
            _ => (m_verticalVelocity < FixedQ4816.Zero),
        };
    }

    // A compiled gate is a flattened conjunction: every element must hold.
    private bool GateOpen(CompiledPredicate[] gate, in LaneActionRuntime state) {
        foreach (var predicate in gate) {
            var holds = predicate.Kind switch {
                CompiledPredicateKind.Now => FactHolds(fact: predicate.Fact),
                CompiledPredicateKind.Recently => (state.Recency![predicate.RecencySlot] > 0),
                CompiledPredicateKind.CooldownElapsed => (state.Cooldown == 0),
                _ => (state.Uses < predicate.UsesLimit),
            };

            if (!holds) {
                return false;
            }
        }

        return true;
    }

    // Apply a fired trigger's compiled effects in authored order — the fixed-point ops on the body.
    private void ApplyEffects(CompiledEffect[] effects, ref LaneActionRuntime state) {
        foreach (var effect in effects) {
            switch (effect.Kind) {
                case CompiledEffectKind.SetVerticalVelocity:
                    m_verticalVelocity = effect.Value;
                    m_verticalVelocityAccumulator.Reset();

                    break;
                case CompiledEffectKind.ScaleVerticalVelocity:
                    m_verticalVelocity *= effect.Value;
                    m_verticalVelocityAccumulator.Reset();

                    break;
                case CompiledEffectKind.PlanarImpulse:
                    // The body-frame direction is rotated by the attitude at fire time and frozen for the burst.
                    m_overlayVelocity = (m_orientation.Rotate(vector: effect.Direction) * effect.Value);
                    m_overlayRemaining = effect.DurationTicks;
                    m_overlayAccumulator.Reset();

                    break;
                case CompiledEffectKind.StartCooldown:
                    state.Cooldown = effect.DurationTicks;

                    break;
                default:
                    state.Uses++;

                    break;
            }
        }
    }

    // One lane's mutable action state: the press latch (the buffer), the cooldown clock, the use counter (reset on
    // ground contact), and the recency clocks (allocated to match the binding's Recently instances).
    private struct LaneActionRuntime {
        public ulong Latch;
        public ulong Cooldown;
        public int Uses;
        public ulong[]? Recency;
    }

    // A tape entry: the intent it holds while live, and the host ticks it has left. A mutable struct stored inline
    // in the ring buffer (no per-segment heap object) — the front segment's RemainingTicks is decremented in place through a
    // `ref` into its slot.
    private struct TapeSegment {
        public PlayerIntent Intent;
        public ulong RemainingTicks;
    }

    // Positive authored durations become engine ticks, rounding upward so a non-zero pulse is observable. Consumers
    // drain them by the host-owned StepTicks, making elapsed behavior independent of the configured fixed-step rate.
    internal static ulong DurationEngineTicks(FixedQ4816 duration) {
        if (duration <= FixedQ4816.Zero) {
            return 0UL;
        }

        var scaled = (((Int128)duration.Value * EngineTicks.PerSecond) + 65535);

        return checked((ulong)(scaled / 65536));
    }

    // Resolve argument-less action taps at the only boundary that knows the host's actual fixed-step period. Pending
    // presses merge by maximum duration with explicit second-based holds, matching the re-press rule of PressLane.
    private void MaterializeDefaultLanePresses(ulong stepTicks) {
        if (m_pendingDefaultLanePresses == ActionLanes.None) {
            return;
        }

        var holdTicks = checked(stepTicks * DefaultActionHoldSteps);

        for (var lane = 0; (lane < ActionLaneCount); lane++) {
            if ((m_pendingDefaultLanePresses & (ActionLanes)(1 << lane)) != ActionLanes.None) {
                m_laneTimers[lane] = Math.Max(val1: m_laneTimers[lane], val2: holdTicks);
            }
        }

        m_pendingDefaultLanePresses = ActionLanes.None;
    }
    private static ulong SubtractSaturating(ulong value, ulong amount) => ((value > amount) ? (value - amount) : 0UL);
}

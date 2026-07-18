using System.Numerics;
using Puck.Hosting;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>What a <see cref="WorldPopulation"/> entry stands for — the local seats driven by client-submitted intents,
/// and the network-human peers represented locally until a transport supplies their intent. Every entry is an
/// authoritative body advanced from a <see cref="PlayerIntent"/>; a driver (a client seat, a network peer, AI, a replay
/// tape) may only produce intents, never write a pose. The render path is driven by kind, so it never learns who is
/// driving an entry.</summary>
internal enum PopulationKind {
    /// <summary>Slots 0..3 — a local roster seat: its body is minted by a session join and advanced from the client's
    /// per-tick submitted intent.</summary>
    LocalSeat,

    /// <summary>Slots 4..127 — a network-human peer that owns its own <see cref="WorldBody"/> state. Until a transport
    /// supplies its intent stream, the built-in scene's deterministic driver stands in for that remote human.</summary>
    NetworkPeer,
}

/// <summary>
/// The server's entity table — up to <see cref="MaxPopulation"/> authoritative bodies advanced as one unified system.
/// Slots <c>0..3</c> are the local seats, minted by session joins and driven by client-submitted intents. Slots
/// <c>4..</c><see cref="MaxPopulation"/> host the network-human peers (see <see cref="PopulationKind"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Revision"/> bumps only on occupancy changes (a seat joining/leaving, a simulated count change), never on
/// a per-tick pose write, so the client rebuilds the avatar program exactly when the declared set moves. The simulated
/// palette and wander seeds are baked once at construction (index-derived, no RNG), so activating an entry only flips
/// its <c>Active</c> flag and creates its own <see cref="WorldBody"/> within the frozen render envelope.
/// </para>
/// <para>
/// <b>Simulation authority.</b> Every entry is an authoritative body: an entry owns its own <see cref="WorldBody"/>
/// (created on activation, dropped on deactivation) and is advanced from an intent.
/// <see cref="AdvanceSimulated"/> shapes each peer's wander into a submitted intent
/// (<see cref="WorldBody.SubmitIntent"/>) and calls <see cref="WorldBody.Advance"/>;
/// <see cref="AdvanceSeats"/> advances the seat bodies from the intents the client submitted this tick. Poses flow out
/// of the sim, never in: the only outside writes into a body are the server-authoritative spawn at activation and the
/// command wire (<c>player.warp</c> / <c>run</c> / <c>face</c> / <c>stop</c>). A live <c>player.run</c> tape overrides
/// the submitted intent (tape &gt; submitted in the intent merge).
/// </para>
/// <para>
/// Single-threaded: <see cref="WorldServer.Step"/> drives everything on host ticks on the window-pump thread. No lock
/// guards this state.
/// </para>
/// </remarks>
internal sealed class WorldPopulation {
    /// <summary>The renderer's hard population ceiling — the 128-player scale target (1×128 / 2×64 / 4×32 shapes).</summary>
    public const int MaxPopulation = 128;

    /// <summary>The reserved local-seat count — four seats, always at the front of the entity table.</summary>
    public const int LocalSeatCount = 4;

    /// <summary>The most simulated stand-ins that fit behind the four local seats (<c>128 - 4</c>).</summary>
    public const int MaxSimulated = (MaxPopulation - LocalSeatCount);

    private readonly Entry[] m_entries = new Entry[MaxPopulation];
    // The fixed-point derived tables — recompiled in place by Rebuild when a sim-affecting section mutates (a live kit
    // tune, motion/wander retune, seat-kit or assignment change), so they are no longer readonly.
    private FixedMotionTuning m_fixedMotion;
    private FixedWanderTuning m_wander;
    // The definition's kit rows: the authored rows (body construction reads a row's tuning) and their fixed-point
    // compilations (the wander producer reads flavor), plus the resolved seat row. Assigned by CompileFixedTables from
    // the constructor (the empty seeds satisfy definite-assignment across that helper call).
    private IReadOnlyList<WorldKit> m_kitRows = [];
    private FixedWorldKit[] m_kits = [];
    private byte m_seatKit;
    // Where each seat's body spawns (X/Z; Y rides the ground plane), from the definition — staggered around the origin,
    // all facing -Z, so a fresh join never lands on top of another avatar. Order maps slots (seat n → [n]).
    private IReadOnlyList<WorldSpawnPoint> m_seatSpawns;
    // The forward-drift deflection a stand-in's synthetic move stick holds: DriftSpeed as a fraction of the profileless
    // move speed, so player.Advance integrates DriftSpeed u/s exactly (deflection × speed = DriftSpeed). Derived once.
    private FixedQ4816 m_wanderForwardDeflection;
    private int m_simulatedCount;
    private int m_revision;
    private IntentSource m_defaultPeerSource = IntentSource.Wander;
    private static readonly FixedQ4816 s_negativeOne = -FixedQ4816.One;
    private static readonly FixedQ4816 s_pi = FixedQ4816.FromDouble(value: Math.PI);
    private static readonly FixedQ4816 s_twoPi = FixedQ4816.FromDouble(value: (2.0 * Math.PI));
    private static readonly FixedQ4816 s_goldenRatioConjugate = FixedQ4816.FromDouble(value: WorldColor.GoldenRatioConjugate);
    private static readonly FixedQ4816 s_altitudeGain = FixedQ4816.FromDouble(value: 0.32);
    private static readonly FixedQ4816 s_activityRateBase = FixedQ4816.FromDouble(value: 2.2);
    private static readonly FixedQ4816 s_activityRateRange = FixedQ4816.FromDouble(value: 1.3);

    /// <summary>Initializes a new instance of the <see cref="WorldPopulation"/> class: the four local slots reserved for
    /// session joins, every network peer seeded with its deterministic color, kit, activity phase, and spawn pose, then
    /// the definition's configured peer census activated immediately. The color must be valid for all 128 from frame 1,
    /// since the program's material capacity is probed from a worst-case all-avatars build. An entry receives its
    /// <see cref="WorldBody"/> when activated.</summary>
    /// <param name="definition">The world definition supplying the kit rows, the wander tuning, and the profileless
    /// locomotion feel.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public WorldPopulation(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_seatSpawns = definition.SpawnPoints;
        // Boot the live peer-source default from the document (§2.1 session write-back home). A live retune/swap keeps the
        // running session value — this seeds only at construction, so a saved world's authored default is honored at boot.
        m_defaultPeerSource = definition.Population.DefaultPeerSource;

        CompileFixedTables(definition: definition);

        // Resolve the definition's kit→entity assignment policy ONCE into every entry's fixed kit index (precompute;
        // zero steady-state cost). The table policy resolves its kit-name cycle to row indices here; the hash policy
        // keeps the R1 low-discrepancy KitFor mapping.
        var assignmentTable = ResolveAssignmentTable(assignment: definition.Assignment);

        for (var index = 0; (index < MaxPopulation); index++) {
            m_entries[index] = new Entry {
                KitIndex = ((assignmentTable is { Length: > 0 } table) ? table[index % table.Length] : KitFor(index: index, kitCount: m_kits.Length)),
                Kind = ((index < LocalSeatCount) ? PopulationKind.LocalSeat : PopulationKind.NetworkPeer),
            };
        }

        for (var index = LocalSeatCount; (index < MaxPopulation); index++) {
            SeedSimulated(index: index);
        }

        _ = SetSimulatedCount(count: definition.Population.NetworkPlayers);
    }

    // Compile the definition's sim-affecting sections to the fixed-point tables runtime simulation reads: the profileless
    // motion/wander tunings, the derived drift deflection (DriftSpeed / MoveSpeed — deflection × speed = DriftSpeed, so
    // it crosses both tunings), the kit rows and their fixed compilations, and the resolved seat-kit row. Shared by the
    // constructor and Rebuild so a live retune quantizes through exactly the same path.
    private void CompileFixedTables(WorldDefinition definition) {
        var authoredMotion = definition.Motion;
        var authoredWander = definition.Wander;

        m_fixedMotion = FixedMotionTuning.Compile(tuning: in authoredMotion);
        m_wander = FixedWanderTuning.Compile(tuning: in authoredWander);
        m_wanderForwardDeflection = (m_wander.DriftSpeed / m_fixedMotion.MoveSpeed);
        m_kitRows = definition.Kits;
        m_kits = new FixedWorldKit[definition.Kits.Count];

        for (var kit = 0; (kit < m_kits.Length); kit++) {
            m_kits[kit] = FixedWorldKit.Compile(kit: definition.Kits[kit]);
        }

        m_seatKit = ResolveKit(name: definition.DefaultSeatKit);
    }

    /// <summary>Recompiles the population's derived state after a sim-affecting section mutation (a live kit tune, a
    /// motion/wander retune, a seat-kit or assignment change, or a whole-document swap): re-quantizes the fixed tables,
    /// re-resolves every entry's kit index, re-derives the kit/wander-dependent per-entry statics WITHOUT resetting the
    /// running wander phase, and swaps every LIVE body's compiled tuning/actions/model in place — bodies keep their
    /// pose/velocity/tape, only the compiled feel swaps. Bumps <see cref="Revision"/> so the client rebuilds the avatar
    /// program. New activations re-seed fully from these fresh tables.</summary>
    /// <param name="definition">The new live definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void Rebuild(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_seatSpawns = definition.SpawnPoints;

        CompileFixedTables(definition: definition);

        var assignmentTable = ResolveAssignmentTable(assignment: definition.Assignment);

        for (var index = 0; (index < MaxPopulation); index++) {
            m_entries[index].KitIndex = ((assignmentTable is { Length: > 0 } table) ? table[index % table.Length] : KitFor(index: index, kitCount: m_kits.Length));
        }

        // Re-derive the kit/wander-dependent per-entry statics from the fresh tables, but keep the running wander phase
        // (resetPhase: false) so the live crowd's producer stays continuous — no phase jerk on a retune.
        for (var index = LocalSeatCount; (index < MaxPopulation); index++) {
            SeedSimulated(index: index, resetPhase: false);
        }

        for (var slot = 0; (slot < LocalSeatCount); slot++) {
            if (m_entries[slot].Active) {
                SeedSeatWander(slot: slot, resetPhase: false);
            }
        }

        // Swap every live body's compiled feel in place; the seat bodies read the (possibly new) seat kit, peers read
        // their reassigned kit index. Pose/velocity/tape/source survive; only the compiled tuning/actions/model change.
        for (var index = 0; (index < MaxPopulation); index++) {
            if (m_entries[index] is not { Active: true, Body: { } body }) {
                continue;
            }

            var kitIndex = ((index < LocalSeatCount) ? m_seatKit : m_entries[index].KitIndex);
            var kit = m_kits[kitIndex];

            body.RecompileKit(tuning: m_kitRows[kitIndex].Tuning, primary: kit.Primary, secondary: kit.Secondary, model: kit.Model);
        }

        m_revision++;
    }

    // The kit row index a kebab name resolves to. The validator gates unknown names at startup.
    private byte ResolveKit(string name) {
        for (var kit = 0; (kit < m_kitRows.Count); kit++) {
            if (string.Equals(a: m_kitRows[kit].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return (byte)kit;
            }
        }

        throw new InvalidOperationException(message: $"No kit row named '{name}' in the world definition.");
    }

    // The kit-row cycle a "table" assignment policy resolves to (its kit names mapped to row indices), or null under the
    // "hash" policy (the R1 KitFor default). The validator gates the policy token and every table name at startup.
    private byte[]? ResolveAssignmentTable(WorldKitAssignment assignment) {
        if (!string.Equals(a: assignment.Policy, b: WorldKitAssignment.TablePolicy, comparisonType: StringComparison.Ordinal)) {
            return null;
        }

        var table = new byte[assignment.Table.Count];

        for (var entry = 0; (entry < table.Length); entry++) {
            table[entry] = ResolveKit(name: assignment.Table[entry]);
        }

        return table;
    }

    /// <summary>A monotonically increasing counter bumped whenever the declared set or palette changes (a seat joining,
    /// leaving, or recoloring, or the simulated count moving), never on a per-frame pose write. The frame source combines
    /// it with the roster's revision to decide when to rebuild the avatar program.</summary>
    public int Revision => m_revision;

    /// <summary>The number of active simulated stand-ins (indices <c>4..</c>).</summary>
    public int SimulatedCount => m_simulatedCount;

    /// <summary>The stored peer intent-source DEFAULT (<see cref="IntentSource.Wander"/> at boot) — a template, not an
    /// aggregate, which is why it stays observable at zero peers: newly activated peers take it, and an explicit
    /// <c>world.population idle|wander</c> sets it and sweeps every peer. Render-inert: it reshapes only the intent
    /// producers, never the declared set or palette, so it does not bump the <see cref="Revision"/>.</summary>
    public IntentSource DefaultPeerSource => m_defaultPeerSource;

    /// <summary>The deterministic kit row index assigned to a stable population slot.</summary>
    public byte KitIndex(int index) => m_entries[index].KitIndex;

    /// <summary>Counts the active entities per kit row for console diagnostics (one slot per definition row).</summary>
    public int[] ActiveKitCounts() {
        var counts = new int[m_kits.Length];

        for (var index = 0; (index < MaxPopulation); index++) {
            if (m_entries[index].Active) {
                counts[m_entries[index].KitIndex]++;
            }
        }

        return counts;
    }

    /// <summary>Assigns a kit row through the R1 low-discrepancy sequence — the implementation of the definition's
    /// <see cref="WorldKitAssignment.HashPolicy"/>, parameterized by row count. Multiplication into equal intervals
    /// avoids modulo bands while keeping the mapping a pure function of the stable population index. The
    /// <see cref="WorldKitAssignment.TablePolicy"/> is the authored-placement alternative (resolved once at
    /// construction); this hash stays the default policy.</summary>
    public static byte KitFor(int index, int kitCount) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: MaxPopulation);
        ArgumentOutOfRangeException.ThrowIfLessThan(value: kitCount, other: 1);

        var sample = LowDiscrepancy.R1(index: (ulong)(index + 1));
        var bucket = (int)(((ulong)sample.Value * (uint)kitCount) >> 32);

        return (byte)bucket;
    }

    /// <summary>Whether the entry at <paramref name="index"/> is active (drawn this frame).</summary>
    /// <param name="index">The population index (0-based, <c>0..</c><see cref="MaxPopulation"/>).</param>
    public bool IsActive(int index) => m_entries[index].Active;

    /// <summary>The <see cref="WorldBody"/> an entry owns while active, or <see langword="null"/> for an inactive
    /// entry. The <c>player.*</c> command wire resolves an index <c>1..128</c> to the entry's own body and produces
    /// intents on it (a warp/run/face/stop command), never a pose stream.</summary>
    /// <param name="index">The population index (0-based, <c>0..</c><see cref="MaxPopulation"/>).</param>
    public WorldBody? EntryBody(int index) => m_entries[index].Body;

    /// <summary>The entry's body color (the avatar's material albedo). A seat's is its assigned profile color; the
    /// client folds the pending-gray desaturation in on its side.</summary>
    /// <param name="index">The population index (0-based).</param>
    public Vector3 BodyColor(int index) => m_entries[index].BodyColor;

    /// <summary>Activates a local seat (indices <c>0..</c><see cref="LocalSeatCount"/>) — the session join's server
    /// half: mints the seat's body at its definition spawn facing -Z, seated on <paramref name="profile"/>. A no-op if
    /// the seat is already active. Bumps the revision.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The profile the seat's body reads speeds and color from, or <see langword="null"/>.</param>
    public void ActivateSeat(int slot, WorldProfile? profile) {
        var entry = m_entries[slot];

        if (entry.Active) {
            return;
        }

        // The seat body constructs from the definition's designated seat kit row (its tuning and lane bindings); the
        // seated profile's speeds still override live.
        var body = new WorldBody(tuning: m_kitRows[m_seatKit].Tuning, primary: m_kits[m_seatKit].Primary, secondary: m_kits[m_seatKit].Secondary) {
            Profile = profile,
        };
        var spawn = m_seatSpawns[slot].Position;

        body.Warp(x: spawn.X, z: spawn.Z);
        body.Face(yawRadians: 0f);
        // Seats default Live and are never touched by population operations; their wander seeds exist so
        // player.control wander <seat> runs the same deterministic producer path as a peer.
        SeedSeatWander(slot: slot);
        entry.Body = body;
        entry.BodyColor = (profile?.Color ?? Vector3.Zero);
        entry.Active = true;
        m_revision++;
    }

    /// <summary>Deactivates a local seat, dropping its body — the session leave's server half. A no-op if the seat is
    /// not active. Bumps the revision.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    public void DeactivateSeat(int slot) {
        var entry = m_entries[slot];

        if (!entry.Active) {
            return;
        }

        entry.Body = null;
        entry.Active = false;
        m_revision++;
    }

    /// <summary>Reseats a seat's body on a profile — the <c>player.profile</c>/confirm server half. The body reads its
    /// speeds live off the profile; the entry color follows for the snapshot.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The profile to seat on.</param>
    public void SetSeatProfile(int slot, WorldProfile profile) {
        var entry = m_entries[slot];

        if (entry.Body is not { } body) {
            return;
        }

        body.Profile = profile;
        entry.BodyColor = profile.Color;
    }

    /// <summary>Refreshes the cached body color of every active seat currently seated on <paramref name="profile"/> —
    /// the server half of a live <c>SetPlayerSection(identity)</c> color edit. The seat renders its color live off the
    /// shared handle client-side, but the per-entry <see cref="BodyColor"/> cache is the snapshot's source of truth, so
    /// it must not lie after an identity change. Bumps the revision when a seat's color actually moves.</summary>
    /// <param name="profile">The edited profile handle.</param>
    public void RefreshSeatColor(WorldProfile profile) {
        for (var slot = 0; (slot < LocalSeatCount); slot++) {
            var entry = m_entries[slot];

            if (entry is { Active: true, Body: { } body } && ReferenceEquals(objA: body.Profile, objB: profile) && (entry.BodyColor != profile.Color)) {
                entry.BodyColor = profile.Color;
                m_revision++;
            }
        }
    }

    /// <summary>Advances every active seat body by one exact simulation tick: a wander-sourced seat gets this tick's
    /// producer image staged first (the same deterministic path as a peer), then the body integrates its submitted
    /// intent per the merge rule. Runs after <see cref="AdvanceSimulated"/> in the server step, mirroring the
    /// historical population-then-seats order.</summary>
    /// <param name="stepTicks">The exact engine ticks this step advances.</param>
    public void AdvanceSeats(ulong stepTicks) {
        for (var slot = 0; (slot < LocalSeatCount); slot++) {
            if (m_entries[slot] is { Active: true, Body: { } body } entry) {
                if (body.Source == IntentSource.Wander) {
                    StageWander(entry: entry, body: body, stepTicks: stepTicks);
                }

                body.Advance(stepTicks: stepTicks);
            }
        }
    }

    /// <summary>Activates the first <paramref name="count"/> simulated stand-ins (indices <c>4..</c>), clamped to
    /// <c>0..</c><see cref="MaxSimulated"/>, and deactivates the rest. A newly-activated entry is re-seeded to a fresh
    /// spawn and given its own <see cref="WorldBody"/> (a server-authoritative spawn at that pose); a deactivated
    /// entry drops its body; entries already active keep wandering. Bumps the revision only when an occupancy flips.</summary>
    /// <param name="count">The requested active simulated count.</param>
    /// <returns>The clamped count actually applied.</returns>
    public int SetSimulatedCount(int count) {
        var clamped = Math.Clamp(value: count, min: 0, max: MaxSimulated);
        var changed = false;

        for (var offset = 0; (offset < MaxSimulated); offset++) {
            var index = (LocalSeatCount + offset);
            var desired = (offset < clamped);
            var entry = m_entries[index];

            if (entry.Active == desired) {
                continue;
            }

            if (desired) {
                ActivateSimulated(index: index);
            } else {
                // A re-activation mints a fresh body at the canonical spawn.
                entry.Body = null;
            }

            entry.Active = desired;
            changed = true;
        }

        m_simulatedCount = clamped;

        if (changed) {
            m_revision++;
        }

        return clamped;
    }

    /// <summary>Sets the peer intent-source default AND sweeps every peer (4..127) to it — last-writer-wins, so a
    /// per-entity source (a possession, an earlier flip) does not survive the global. Seats are never touched.
    /// Render-inert: it reshapes only the intent producers, so it does not bump the revision. A live
    /// <c>player.run</c> tape still drives regardless.</summary>
    /// <param name="source">The intent source to store and sweep.</param>
    public void SetPeerSource(IntentSource source) {
        m_defaultPeerSource = source;

        for (var index = LocalSeatCount; (index < MaxPopulation); index++) {
            m_entries[index].Body?.SetIntentSource(source: source);
        }
    }

    /// <summary>Advances every active simulated stand-in by one sub-step: a peer whose source is
    /// <see cref="IntentSource.Wander"/> gets this tick's producer image staged (the drift and weave, spring-steered
    /// inward past the wander tuning's soft radius), then every peer body integrates. A live <c>player.run</c> tape or
    /// a submitted intent overrides the producer per the merge rule; an <see cref="IntentSource.Idle"/> peer holds
    /// still between tape segments yet its tapes still play. The local seats are advanced separately by
    /// <see cref="AdvanceSeats"/>.</summary>
    public void AdvanceSimulated(ulong stepTicks) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);

        for (var index = LocalSeatCount; (index < MaxPopulation); index++) {
            var entry = m_entries[index];

            // Network peers use this deterministic producer only until a real transport, replay, or tape supplies the
            // same intent currency. An inactive entry has no body.
            if (!entry.Active || (entry.Kind != PopulationKind.NetworkPeer) || (entry.Body is not { } player)) {
                continue;
            }

            if (player.Source == IntentSource.Wander) {
                StageWander(entry: entry, body: player, stepTicks: stepTicks);
            }

            player.Advance(stepTicks: stepTicks);
        }
    }

    // Stage one entity's wander producer image for this tick: advance its weave/activity phases, read the pose out of
    // the body, shape the archetype's intent, and stage it below the submitted stream. Index-seeded and deterministic;
    // the same path serves peers and wander-sourced seats. Phases advance only while the source names the producer,
    // matching the historical idle freeze.
    private void StageWander(Entry entry, WorldBody body, ulong stepTicks) {
        // Poses flow out of the sim: read this tick's pose from the body.
        var planarX = body.FixedPosition.X;
        var planarZ = body.FixedPosition.Z;
        var yaw = body.FixedYaw;

        entry.Phase += PerStep(value: entry.WeaveFreq, stepTicks: stepTicks);
        entry.ActivityPhase += PerStep(value: entry.ActivityRate, stepTicks: stepTicks);
        var yawRate = (m_wander.WeaveAmplitude * FixedQ4816.Sin(angle: entry.Phase));
        var radius = FixedQ4816.Sqrt(value: ((planarX * planarX) + (planarZ * planarZ)));

        if (radius > m_wander.SoftRadius) {
            // Steer the turn axis inward (never clamp the position): facing = (-sin yaw, -cos yaw), so the yaw whose
            // facing points back at the origin is atan2(x, z). A proportional nudge curves it home.
            var inwardYaw = FixedQ4816.Atan2(y: planarX, x: planarZ);

            yawRate += (m_wander.InwardGain * WrapPi(angle: (inwardYaw - yaw)));
        }

        var turn = FixedQ4816.Clamp(value: (yawRate / m_fixedMotion.TurnSpeed), minimum: s_negativeOne, maximum: FixedQ4816.One);
        var wave = FixedQ4816.Sin(angle: entry.ActivityPhase);
        var altitudeCorrection = FixedQ4816.Clamp(
            value: ((entry.PreferredAltitude - body.FixedPosition.Y) * s_altitudeGain),
            minimum: s_negativeOne,
            maximum: FixedQ4816.One
        );
        var kit = m_kits[entry.KitIndex];
        PlayerIntent intent;

        if (kit.Model == MotionModel.Free) {
            // A banked, altitude-holding 6DOF path: the flavor's waves ride strafe/up/pitch, and the bank follows the
            // negative turn.
            intent = new PlayerIntent(
                MoveForward: kit.Forward,
                MoveStrafe: (wave * kit.StrafeWave),
                Turn: turn,
                MoveUp: (altitudeCorrection + (wave * kit.UpWave)),
                Pitch: (wave * kit.PitchWave),
                Roll: (-turn * kit.RollTurn)
            );
        } else {
            // A grounded path: drift-or-fixed forward, the flavor's waves on strafe/turn, and — when the flavor sets a
            // threshold — the Primary channel held for the positive part of the wave, yielding a real press/release
            // edge through the body's bound action while scripted tapes still override the movement channels.
            intent = new PlayerIntent(
                MoveForward: (kit.DriftForward ? m_wanderForwardDeflection : kit.Forward),
                MoveStrafe: (wave * kit.StrafeWave),
                Turn: FixedQ4816.Clamp(value: (turn + (wave * kit.TurnWave)), minimum: s_negativeOne, maximum: FixedQ4816.One),
                Actions: (((kit.PrimaryThreshold > FixedQ4816.Zero) && (wave > kit.PrimaryThreshold)) ? ActionLanes.Primary : ActionLanes.None)
            );
        }

        body.StageProducerIntent(intent: in intent);
    }

    private static FixedQ4816 PerStep(FixedQ4816 value, ulong stepTicks) {
        if ((EngineTicks.PerSecond % stepTicks) != 0UL) {
            throw new ArgumentException(message: $"The fixed-step period {stepTicks} must divide {EngineTicks.PerSecond} engine ticks exactly.", paramName: nameof(stepTicks));
        }

        return (value / FixedQ4816.FromInteger(value: checked((long)(EngineTicks.PerSecond / stepTicks))));
    }

    // Activate a simulated entry: re-seed its canonical pose/color/wander from its index, then mint its own body from
    // its kit row (tuning + primary-action binding) spawned at that pose with the stored peer-source default. The
    // Warp/Face is a server-authoritative spawn (a one-time write into the sim); from here the pose flows only out.
    private void ActivateSimulated(int index) {
        SeedSimulated(index: index);

        var entry = m_entries[index];
        var kit = m_kits[entry.KitIndex];
        // Profileless — advances on the kit row's tuning with the row's lane bindings.
        var player = new WorldBody(tuning: m_kitRows[entry.KitIndex].Tuning, primary: kit.Primary, secondary: kit.Secondary);

        if (kit.Model == MotionModel.Free) {
            player.SetModel(model: MotionModel.Free);
            player.Pose(
                position: entry.SpawnPosition with { Y = entry.PreferredAltitude },
                yawRadians: entry.SpawnYaw,
                pitchRadians: FixedQ4816.Zero,
                rollRadians: FixedQ4816.Zero
            );
        } else {
            player.Warp(x: entry.SpawnPosition.X, z: entry.SpawnPosition.Z);
            player.Face(yawRadians: entry.SpawnYaw);
        }

        player.SetIntentSource(source: m_defaultPeerSource);
        entry.Body = player;
    }

    // Seed a simulated entry's static per-index data from its index alone (no RNG): a phyllotaxis spawn pose inside the
    // soft disc, a golden-ratio body hue, and an index-varied slow weave frequency — all stable, so a re-activated
    // index looks the same. Baked for every entry at construction so the color is valid across all 128 from frame 1. A
    // live Rebuild re-derives the kit/wander-dependent statics with resetPhase: false, which keeps the running wander
    // phase/activity so the retune does not jerk the crowd.
    private void SeedSimulated(int index, bool resetPhase = true) {
        var offset = (index - LocalSeatCount);
        var fraction = (FixedQ4816.FromInteger(value: ((2L * offset) + 1L)) / FixedQ4816.FromInteger(value: (2L * MaxSimulated)));
        var spawnRadius = (m_wander.SpawnRadius * FixedQ4816.Sqrt(value: fraction));
        var angle = (FixedQ4816.FromInteger(value: offset) * m_wander.GoldenAngle);
        // The shared golden-ratio hue walk (WorldColor), same as profile.create's auto-color. The hue also varies the
        // slow weave frequency.
        var hue = WorldColor.GoldenRatioHue(index: offset);
        var fixedHue = FixedQ4816.Fractional(value: (FixedQ4816.FromInteger(value: offset) * s_goldenRatioConjugate));
        var (activitySample, altitudeSample) = LowDiscrepancy.R2(index: (ulong)(index + 1));
        var activityUnit = FixedQ4816.FromDouble(value: (double)activitySample);
        var altitudeUnit = FixedQ4816.FromDouble(value: (double)altitudeSample);
        var entry = m_entries[index];

        var (sin, cos) = FixedQ4816.SinCos(angle: angle);

        entry.PreferredAltitude = PreferredAltitudeFor(kit: m_kits[entry.KitIndex], altitudeUnit: altitudeUnit);
        entry.SpawnPosition = new FixedVector3(X: (spawnRadius * cos), Y: entry.PreferredAltitude, Z: (spawnRadius * sin));
        entry.SpawnYaw = angle;
        entry.WeaveFreq = (m_wander.WeaveFrequencyBase + (m_wander.WeaveFrequencyRange * fixedHue));
        entry.BodyColor = WorldColor.HsvToRgb(h: hue, s: WorldColor.SeedSaturation, v: WorldColor.SeedValue);

        if (resetPhase) {
            entry.Phase = angle;
            entry.ActivityPhase = (angle + (s_twoPi * activityUnit));
            entry.ActivityRate = (s_activityRateBase + (s_activityRateRange * activityUnit));
        }
    }

    // Seed a seat's wander-producer dynamics from its slot alone (no RNG) — the parameters player.control wander <seat>
    // steers by, slot-seeded parallel to SeedSimulated (whose R2 stream indices 5.. stay disjoint). A seat has no
    // wander spawn/color seeding — the definition spawns it and its profile colors it.
    private void SeedSeatWander(int slot, bool resetPhase = true) {
        var angle = (FixedQ4816.FromInteger(value: slot) * m_wander.GoldenAngle);
        var fixedHue = FixedQ4816.Fractional(value: (FixedQ4816.FromInteger(value: slot) * s_goldenRatioConjugate));
        var (activitySample, altitudeSample) = LowDiscrepancy.R2(index: (ulong)(slot + 1));
        var activityUnit = FixedQ4816.FromDouble(value: (double)activitySample);
        var altitudeUnit = FixedQ4816.FromDouble(value: (double)altitudeSample);
        var entry = m_entries[slot];

        entry.PreferredAltitude = PreferredAltitudeFor(kit: m_kits[entry.KitIndex], altitudeUnit: altitudeUnit);
        entry.WeaveFreq = (m_wander.WeaveFrequencyBase + (m_wander.WeaveFrequencyRange * fixedHue));

        if (resetPhase) {
            entry.Phase = angle;
            entry.ActivityPhase = (angle + (s_twoPi * activityUnit));
            entry.ActivityRate = (s_activityRateBase + (s_activityRateRange * activityUnit));
        }
    }

    // The altitude a wander entity holds: a free kit's authored base plus its per-index range sample; a grounded kit
    // rides the ground plane.
    private FixedQ4816 PreferredAltitudeFor(in FixedWorldKit kit, FixedQ4816 altitudeUnit) {
        return ((kit.Model == MotionModel.Free)
            ? (kit.AltitudeBase + (kit.AltitudeRange * altitudeUnit))
            : m_fixedMotion.GroundY);
    }

    // Wrap an angle into (-π, π] so the inward steer takes the short way around.
    private static FixedQ4816 WrapPi(FixedQ4816 angle) {
        return (angle - (s_twoPi * FixedQ4816.Floor(value: ((angle + s_pi) / s_twoPi))));
    }

    // One entity-table entry. A mutable class; Kind and KitIndex are fixed at construction. SpawnYaw is the
    // index-seeded heading a fresh activation faces the new body toward. Body is the entry's own sim — null while
    // inactive, minted on activation (a session join for a seat, the census for a peer).
    private sealed class Entry {
        public FixedQ4816 ActivityPhase { get; set; }
        public FixedQ4816 ActivityRate { get; set; }
        public bool Active { get; set; }
        public WorldBody? Body { get; set; }
        public Vector3 BodyColor { get; set; }
        public required PopulationKind Kind { get; init; }
        // Reassigned in place by Rebuild when the kit-assignment policy (or kit set) mutates; set at construction.
        public required byte KitIndex { get; set; }
        public FixedQ4816 Phase { get; set; }
        public FixedQ4816 PreferredAltitude { get; set; }
        public FixedVector3 SpawnPosition { get; set; }
        public FixedQ4816 SpawnYaw { get; set; }
        public FixedQ4816 WeaveFreq { get; set; }
    }
}

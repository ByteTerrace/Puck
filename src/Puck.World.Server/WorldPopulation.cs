using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>What a <see cref="WorldPopulation"/> entry stands for — the local seats driven by client-submitted intents,
/// and the peer slice that hosts every OTHER joined body: remote-human peers and the loopback-joined inhabitants alike.
/// Every entry is an authoritative body advanced from a <see cref="PlayerIntent"/>; a driver (a client seat, a network
/// peer, AI, an inhabitant's attend producer, a replay tape) may only produce intents, never write a pose. An inhabitant
/// is not a separate kind — it is a <see cref="NetworkPeer"/> whose body is bound to a placement (see
/// <see cref="WorldPopulation"/>), joined over the loopback link exactly as a peer is. The render path is driven by kind,
/// so it never learns who is driving an entry.</summary>
internal enum PopulationKind {
    /// <summary>Slots 0..3 — a local roster seat: its body is minted by a session join and advanced from the client's
    /// per-tick submitted intent.</summary>
    LocalSeat,

    /// <summary>Slots 4..127 — a joined peer body. A remote-human peer owns its own <see cref="WorldBody"/> state and,
    /// until a transport supplies its intent stream, runs its authored default producer; an inhabited peer (its
    /// entry carries a placement back-reference) is driven by its authored source. Admitted while a slot is free,
    /// bounded only by the entity table itself.</summary>
    NetworkPeer,
}

/// <summary>
/// The server's entity table — up to <see cref="Capacity"/> authoritative bodies advanced as one unified system.
/// Slots <c>0..3</c> are the local seats, minted by session joins and driven by client-submitted intents. Slots
/// <c>4..</c><see cref="Capacity"/> host the network-human peers (see <see cref="PopulationKind"/>).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Revision"/> bumps whenever the declared set or palette changes — a seat joining, leaving, or recoloring,
/// or the simulated count moving — never on a per-tick pose write, so the client rebuilds the avatar program exactly
/// when the declared set moves. The simulated
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
/// command wire (<c>player.pose</c> / <c>fly</c> / <c>stop</c>). A live <c>player.fly</c> tape overrides
/// the submitted intent (tape &gt; submitted in the intent merge).
/// </para>
/// <para>
/// Single-threaded: <see cref="WorldServer.Step"/> drives everything on host ticks on the window-pump thread. No lock
/// guards this state.
/// </para>
/// </remarks>
public sealed class WorldPopulation {
    /// <summary>The authored entity-table capacity allocated at world load.</summary>
    public int Capacity => m_entries.Length;

    /// <summary>The reserved local-seat count — four seats, always at the front of the entity table. Single-sourced
    /// in <see cref="WorldPopulationLimits.LocalSeatCount"/>.</summary>
    public const int LocalSeatCount = WorldPopulationLimits.LocalSeatCount;

    /// <summary>The authored peer slice behind the reserved local seats.</summary>
    public int PeerCapacity => (Capacity - LocalSeatCount);

    /// <summary>The most census/remote peers that fit RIGHT NOW behind the four local seats and BELOW the lowest inhabited
    /// body. Inhabited bodies (loopback-joined players) allocate downward from slot 127, census peers upward from slot 4,
    /// so this floor is exactly where the two packings meet; it moves only with LIVE inhabitant occupancy, never a boot
    /// reservation. A live <c>world.population &lt;n&gt;</c> clamps against it AND against the remote admission cap
    /// (<c>networkPlayers</c>). Reading the FLOOR (not a count) keeps existing inhabitant slots stable — a retired
    /// inhabitant leaves a gap peers decline rather than forcing a renumber.</summary>
    public int MaxSimulated => (m_inhabitantFloor - LocalSeatCount);

    /// <summary>The largest census <see cref="SetSimulatedCount"/> will actually grant right now — the tighter of the
    /// remote admission cap (<c>networkPlayers</c>) and the live inhabitant floor (<see cref="MaxSimulated"/>). A request
    /// above it is clamped to it, so the <c>world.population</c> echo names both the granted count and this ceiling
    /// rather than letting a script read a success for a crowd it never got.</summary>
    public int SimulatedCeiling => Math.Min(val1: m_remoteCap, val2: MaxSimulated);

    private readonly Entry[] m_entries;
    // The fixed-point derived tables — recompiled in place by Rebuild when a sim-affecting section mutates (a live kit
    // tune, motion/wander retune, seat-kit or assignment change), so they are no longer readonly.
    private FixedMotionDefaults m_fixedMotion;
    private WorldPlayerDefaults m_playerDefaults = null!;
    private WorldPopulationVariation m_peerVariation = null!;
    private WorldPopulationVariation m_seatVariation = null!;
    private WorldSequence m_peerColors = null!;
    // The definition's kit rows: the authored rows (body construction reads a row's tuning) and their fixed-point
    // compilations (producer programs read their parameter maps), plus the resolved seat row. Assigned by CompileFixedTables from
    // the constructor (the empty seeds satisfy definite-assignment across that helper call).
    private IReadOnlyList<WorldKit> m_kitRows = [];
    private FixedWorldKit[] m_kits = [];
    private IReadOnlyDictionary<string, CompiledBodyMotionProgram> m_bodyMotionPrograms = new Dictionary<string, CompiledBodyMotionProgram>();
    // The world's compiled channel table — kit Actions/PressChannel name resolution reads it once per compile pass.
    private WorldChannelTable m_channels = WorldChannelTable.Empty;
    private IReadOnlyList<WorldTargetRegister> m_targetRows = [];
    private WorldTargetRegisterTable m_targets = WorldTargetRegisterTable.Empty;
    private WorldSolidField? m_targetField;
    private byte m_seatKit;
    // Where each seat's body spawns, from the definition — staggered around the origin,
    // all facing -Z, so a fresh join never lands on top of another avatar. Order maps slots (seat n → [n]).
    private FixedSpawnPoint[] m_seatSpawns;
    // The world contact field derived from the definition's solid geometry and collision tuning. Built by
    // CompileFixedTables and handed to every live body, so a live solid-geometry or collision-tuning edit takes
    // effect on the next tick with no restart. Grounded bodies solve their swept position against it.
    private IContactField? m_contactField;
    private FixedQ4816? m_waterline;
    private WorldContactCensus m_contactCensus;
    // The definition's LOOK rows (empty ⇒ the implicit single catalog look), resolved by CompileFixedTables. Each
    // entry's LookIndex points into this list. PRESENTATION-ONLY — the snapshot carries it to the client's renderer.
    private IReadOnlyList<WorldLook> m_lookRows = [WorldLook.Implicit];
    // The compiled population distribution (fixed point). SIM-AFFECTING: SeedSimulated reads only this, never the authored floats.
    // Live for FUTURE activations, inert for bodies already standing (resetPhase: false keeps the running crowd put).
    private FixedWorldDistribution m_distribution;
    private WorldRowAssignment m_lookAssignment = null!;
    private byte[]? m_lookAssignmentRows;
    private int m_simulatedCount;
    // The authored population.reconnectGraceTicks (see its own remarks) — how long a disconnected body stays PARKED
    // before ReclaimExpiredParks tears it down. Refreshed by CompileFixedTables on a swap/rebuild, on the SAME
    // "boot-time constant, live for future disconnects only" terms the rest of this section already reads under.
    private int m_reconnectGraceTicks;
    // The remote-principal admission cap (the document's networkPlayers): the most census/remote peers world.population
    // may raise. It is a CEILING, never a boot reservation — at boot the census stands at zero (only the joined seats are
    // live) so the peer slice is entirely free for inhabitants. Refreshed by CompileFixedTables on a swap/rebuild.
    private int m_remoteCap;
    // The lowest slot index a live inhabited body occupies (Capacity = none). Inhabited bodies claim the top of the
    // entity table (slots 127 downward); the census ceiling reads this floor so census peers never reach an inhabitant.
    // Reconciled by ReconcileInhabitants.
    private int m_inhabitantFloor;
    private int m_revision;
    private IntentSource m_defaultPeerSource = IntentSource.Live;
    private readonly List<BodyEffectOutput> m_effectOutputs = [];
    private readonly List<WorldDesignation> m_designationOutputs = [];
    private readonly List<WorldGeneratorInvocation> m_generatorInvocations = [];
    private readonly List<DurableStateOutput> m_durableStateOutputs = [];
    private static readonly FixedQ4816 s_twoPi = FixedQ4816.FromDouble(value: (2.0 * Math.PI));
    private static readonly FixedVector3 s_localForward = new(X: FixedQ4816.Zero, Y: FixedQ4816.Zero, Z: -FixedQ4816.One);
    private static readonly FixedVector3 s_localSightOffset = new(X: FixedQ4816.Zero, Y: FixedQ4816.One, Z: FixedQ4816.Zero);

    /// <summary>Initializes a new instance of the <see cref="WorldPopulation"/> class: the four local slots reserved for
    /// session joins, every peer slot seeded with its deterministic color, kit, activity phase, and spawn pose. The census
    /// stands at ZERO at boot — <c>networkPlayers</c> is the remote admission CAP, not a static reservation, so the whole
    /// peer slice is free for inhabitants and later <c>world.population</c> raises. The color must be valid for all 128
    /// from frame 1, since the program's material capacity is probed from a worst-case all-avatars build. An entry
    /// receives its <see cref="WorldBody"/> when activated.</summary>
    /// <param name="definition">The world definition supplying the kit rows, producer parameters, and the profileless
    /// locomotion feel.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public WorldPopulation(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_entries = new Entry[definition.Population.Capacity];
        m_inhabitantFloor = m_entries.Length;

        m_seatSpawns = CompileSeatSpawns(spawnPoints: definition.SpawnPoints, seatSpawns: definition.Population.SeatSpawns);
        // Boot the live peer-source default from the document (the session write-back home). A live retune/swap keeps the
        // running session value — this seeds only at construction, so a saved world's authored default is honored at boot.
        m_defaultPeerSource = definition.Population.DefaultPeerSource;

        // The boot contact field: analytic is derived here; the field provider is compiled once (a bad-op world fails
        // LOUDLY at boot, which is the honest boot-time counterpart of the live apply-time rejection). A live rebuild
        // instead receives the server's pre-built field so a runtime edit never rebuilds it twice.
        CompileFixedTables(definition: definition, solids: null);

        // Resolve the definition's kit→entity assignment ONCE into every entry's fixed kit index.
        var assignmentRows = ResolveRows(assignment: definition.Assignment, resolve: ResolveKit);

        for (var index = 0; (index < Capacity); index++) {
            m_entries[index] = new Entry {
                KitIndex = SelectRow(index: index, assignment: definition.Assignment, rows: assignmentRows, rowCount: m_kits.Length),
                Kind = ((index < LocalSeatCount) ? PopulationKind.LocalSeat : PopulationKind.NetworkPeer),
                Designations = NewDesignations(),
            };
        }

        // The look table resolves after the entries exist (it writes each entry's LookIndex).
        ResolveLookIndices(definition: definition);

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            SeedSimulated(index: index);
        }

        // No boot census: the peer slice stays free until world.population raises it or an inhabitant joins. m_remoteCap
        // (set by CompileFixedTables above) caps a later raise; m_simulatedCount stays 0.
    }

    // Compile the definition's sim-affecting sections to the fixed-point tables runtime simulation reads: the profileless
    // motion tuning, kit producer parameters, kit rows and their fixed compilations, and the resolved seat-kit row. Shared by the
    // constructor and Rebuild so a live retune quantizes through exactly the same path.
    private void CompileFixedTables(WorldDefinition definition, WorldSolidField? solids) {
        var authoredMotion = definition.Motion;
        m_fixedMotion = FixedMotionDefaults.Compile(motion: in authoredMotion);
        m_playerDefaults = definition.PlayerDefaults;
        m_peerVariation = definition.Population.PeerVariation;
        m_seatVariation = definition.Population.SeatVariation;
        m_peerColors = definition.Population.PeerColors;
        m_reconnectGraceTicks = definition.Population.ReconnectGraceTicks;
        m_kitRows = definition.Kits;
        var programs = new Dictionary<string, CompiledBodyMotionProgram>(comparer: StringComparer.Ordinal);
        foreach (var program in definition.BodyMotionPrograms) {
            programs.Add(key: program.Name, value: CompiledBodyMotionProgram.Compile(program: program));
        }
        m_bodyMotionPrograms = programs;
        m_channels = WorldChannelTable.Compile(channels: definition.Channels);
        m_targetRows = definition.TargetRegisters;
        m_targets = WorldTargetRegisterTable.Compile(registers: definition.TargetRegisters, channelCount: m_channels.ChannelCount);
        m_kits = new FixedWorldKit[definition.Kits.Count];

        for (var kit = 0; (kit < m_kits.Length); kit++) {
            m_kits[kit] = FixedWorldKit.Compile(kit: definition.Kits[kit], channels: m_channels, targets: m_targets, programs: m_bodyMotionPrograms, creations: definition.Creations);
        }

        // Derive the contact field the definition selects — the ONE derivation both a fresh activation and a live body
        // read. The field provider's program is handed in pre-built at runtime; at boot it is compiled here.
        m_contactCensus = WorldColliderSet.Measure(definition: definition);
        var derivedSolids = solids;
        if ((derivedSolids is null) && (WorldContactSelection.RequiresField(collision: definition.Collision) || WorldTargetSelection.RequiresLineOfSight(definition: definition))) {
            if (!WorldSolidField.TryBuild(definition: definition, built: out derivedSolids, reason: out var reason)) {
                throw new InvalidOperationException(message: $"the target/contact field could not compile the world's solids at boot: {reason}");
            }
        }
        m_contactField = ResolveContactField(definition: definition, solids: derivedSolids);
        // The compiled waterline rides beside the contact field: one optional world fact every body carries, read only
        // by a swim-model kit's stages.
        m_waterline = ((definition.Water is { } water) ? FixedQ4816.FromDouble(value: water.Level) : (FixedQ4816?)null);
        m_targetField = (WorldTargetSelection.RequiresLineOfSight(definition: definition) ? derivedSolids : null);
        m_seatKit = ResolveKit(name: definition.DefaultSeatKit);
        // The LOOK table: the authored rows, or the implicit single catalog look when the author declared none — so an
        // empty `looks` section is the pre-arc runtime exactly, with no branch special-casing the absence.
        m_lookRows = ((definition.Looks.Count > 0) ? definition.Looks : [WorldLook.Implicit]);
        // The compiled population distribution — read ONLY by SeedSimulated (never the authored floats). The validator has already
        // resolved every named spawn point, so Compile's lookups always hit.
        m_distribution = FixedWorldDistribution.Compile(distribution: definition.Population.Distribution, spawnPoints: definition.SpawnPoints);
        m_lookAssignment = definition.LookAssignment;
        // The remote admission cap moves with the live document (a swap can raise or lower networkPlayers); the running
        // census count is re-clamped against it by ReconcileInhabitants' trailing SetSimulatedCount.
        m_remoteCap = definition.Population.NetworkPlayers;
    }

    // Resolve every entry's LookIndex from the definition's authored sequence and row view.
    private void ResolveLookIndices(WorldDefinition definition) {
        m_lookAssignmentRows = ResolveRows(assignment: definition.LookAssignment, resolve: ResolveLook);

        for (var index = 0; (index < Capacity); index++) {
            m_entries[index].LookIndex = SelectRow(index: index, assignment: definition.LookAssignment, rows: m_lookAssignmentRows, rowCount: m_lookRows.Count);
        }
    }

    // An authored row view resolved to table indices. Empty means every declared row in declaration order.
    private static byte[]? ResolveRows(WorldRowAssignment assignment, Func<string, byte> resolve) {
        if (assignment.Rows.Count == 0) {
            return null;
        }

        var table = new byte[assignment.Rows.Count];

        for (var entry = 0; (entry < table.Length); entry++) {
            table[entry] = resolve(arg: assignment.Rows[entry]);
        }

        return table;
    }

    // The look row index a kebab name resolves to. The validator gates unknown names at startup / apply.
    private byte ResolveLook(string name) {
        for (var look = 0; (look < m_lookRows.Count); look++) {
            if (string.Equals(a: m_lookRows[look].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return (byte)look;
            }
        }

        throw new InvalidOperationException(message: $"No look row named '{name}' in the world definition.");
    }

    // The requirements-selected contact field: the analytic convex-collider set when no field quality is required, or
    // the pre-built SDF field otherwise. At runtime the
    // server hands the pre-built field (built once at apply time for its loud excluded-op rejection); at boot (solids ==
    // null) the field is compiled here and a bad-op world fails loudly.
    private static IContactField? ResolveContactField(WorldDefinition definition, WorldSolidField? solids) {
        if (WorldContactSelection.RequiresField(collision: definition.Collision)) {
            if (solids is not null) {
                return solids;
            }

            return solids ?? throw new InvalidOperationException(message: "the field contact provider was not compiled.");
        }

        return WorldColliderSet.Build(definition: definition);
    }

    private int[] NewDesignations() {
        var values = new int[m_targets.Count];
        Array.Fill(array: values, value: -1);
        return values;
    }

    private static void ClearDesignations(Entry entry) {
        Array.Fill(array: entry.Designations, value: -1);
        entry.DesignationRefusal = string.Empty;
    }

    /// <summary>Recompiles the population's derived state after a sim-affecting section mutation (a live kit tune, a
    /// motion/wander retune, a seat-kit or assignment change, or a whole-document swap): re-quantizes the fixed tables,
    /// re-resolves every entry's kit index, re-derives the kit/wander-dependent per-entry statics WITHOUT resetting the
    /// running wander phase, and swaps every LIVE body's compiled tuning/actions/program in place — bodies keep their
    /// pose/velocity/tape, only the compiled feel swaps. Bumps <see cref="Revision"/> so the client rebuilds the avatar
    /// program. New activations re-seed fully from these fresh tables.</summary>
    /// <param name="definition">The new live definition.</param>
    /// <param name="solids">The server's pre-built SDF contact field for the FIELD provider (built once at apply time so
    /// a runtime edit never rebuilds it twice), or <see langword="null"/> under the analytic provider.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void Rebuild(WorldDefinition definition, WorldSolidField? solids) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_seatSpawns = CompileSeatSpawns(spawnPoints: definition.SpawnPoints, seatSpawns: definition.Population.SeatSpawns);

        var priorTargets = m_targets;
        CompileFixedTables(definition: definition, solids: solids);

        for (var bodyIndex = 0; (bodyIndex < m_entries.Length); bodyIndex++) {
            var prior = m_entries[bodyIndex].Designations;
            var current = NewDesignations();

            for (var priorIndex = 0; (priorIndex < prior.Length); priorIndex++) {
                if (m_targets.TryGetIndex(name: priorTargets.Name(index: priorIndex), index: out var currentIndex)) {
                    current[currentIndex] = prior[priorIndex];
                }
            }

            m_entries[bodyIndex].Designations = current;
        }

        var assignmentRows = ResolveRows(assignment: definition.Assignment, resolve: ResolveKit);

        for (var index = 0; (index < Capacity); index++) {
            m_entries[index].KitIndex = SelectRow(index: index, assignment: definition.Assignment, rows: assignmentRows, rowCount: m_kits.Length);
        }

        // Re-resolve the look table too — a live look row/assignment mutation flows through Rebuild (AffectsRenderEnvelope
        // + the client program rebuild the bumped revision triggers). PRESENTATION-ONLY, so it touches no body state.
        ResolveLookIndices(definition: definition);

        // Re-derive the kit/wander-dependent per-entry statics from the fresh tables, but keep the running wander phase
        // (resetPhase: false) so the live crowd's producer stays continuous — no phase jerk on a retune.
        for (var index = LocalSeatCount; (index < Capacity); index++) {
            SeedSimulated(index: index, resetPhase: false);
        }

        for (var slot = 0; (slot < LocalSeatCount); slot++) {
            if (m_entries[slot].Active) {
                SeedSeatWander(slot: slot, resetPhase: false);
            }
        }

        // Swap every live body's compiled feel in place; the seat bodies read the (possibly new) seat kit, peers read
        // their reassigned kit index. Pose/velocity/tape/source survive; only the compiled tuning/actions/program change.
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is not { Active: true, Body: { } body }) {
                continue;
            }

            var kitIndex = ResolveKitIndex(index: index);
            var kit = m_kits[kitIndex];

            body.RecompileKit(motion: m_kitRows[kitIndex].Motion, actions: kit.Actions, actionThresholds: kit.ActionThresholds, actionShapes: kit.ActionShapes, roleMask: kit.RoleMask, roleOrdinals: kit.RoleOrdinals, actionState: kit.ActionState, program: kit.BodyMotionProgram, programs: m_bodyMotionPrograms, collider: kit.Collider, maxSmoothError: m_fixedMotion.MaxSmoothError, sprintChannelOrdinal: kit.SprintChannelOrdinal, driftChannelOrdinal: kit.DriftChannelOrdinal);
            // Hand the (possibly rebuilt) contact field to every live body, so a live solid-geometry or collision-tuning
            // edit takes effect on the next tick.
            body.SetContactField(field: m_contactField);
            body.SetWaterline(level: m_waterline);
        }

        m_revision++;
    }

    /// <summary>Reconciles the inhabited-body registrations against the delivered definition (called from the server's
    /// Install AFTER <see cref="Rebuild(WorldDefinition, WorldSolidField?)"/>): a placement's INHABIT facet joins bodies
    /// into the peer slice over the loopback link — an inhabitant is a <see cref="PopulationKind.NetworkPeer"/> whose entry
    /// carries a placement back-reference, holding a normal <see cref="WorldBody"/> under the resolved kit and driven by
    /// its kit's attend producer. Bodies claim the HIGHEST FREE slots (127 downward) so an existing inhabitant never
    /// renumbers; admission is bounded ONLY by the table itself and rejects loudly when it is genuinely full — there is no
    /// census-fit reservation. Diff-by-placement: retire an entry whose row vanished, lost its facet, or changed
    /// creation/kit; keep a matching one (its pose survives an unrelated placement edit); admit new bodies at the highest
    /// free slots. The census ceiling (<see cref="MaxSimulated"/>) follows the resulting inhabitant floor (physical
    /// occupancy), and the census is re-clamped so census peers never reach an inhabitant.</summary>
    /// <param name="definition">The delivered definition (its placements, creations, kits, and look table).</param>
    /// <param name="admitted">Optional sink for the peer generations admitted by the reconciliation.</param>
    /// <param name="disconnected">Optional sink for the peer generations disconnected by the reconciliation.</param>
    public void ReconcileInhabitants(WorldDefinition definition, List<WorldPeerEventEntry>? admitted = null, List<WorldPeerEventEntry>? disconnected = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        // Pass 1 — retire inhabited slots whose placement/facet/creation-kit binding no longer holds. A surviving slot
        // keeps its body (pose preserved); a kit change recompiles in place. An inhabited entry is a peer carrying a
        // placement back-reference; a plain census peer (no PlacementId) is left untouched.
        for (var index = Capacity - 1; (index >= LocalSeatCount); index--) {
            var entry = m_entries[index];

            if (entry.PlacementId is null) {
                continue;
            }

            if ((entry.PlacementId is not { } placementId) || (FindInhabited(definition: definition, placementId: placementId) is not { } placement) ||
                (ResolveInhabitKit(definition: definition, placement: placement) is not { } kitName) || (ResolveKitOrNull(name: kitName) is not { } kitIndex)) {
                disconnected?.Add(item: PeerEventEntry(index: index));
                RetireInhabitant(index: index);

                continue;
            }

            entry.KitIndex = kitIndex;
            entry.LookIndex = ResolveInhabitLook(placement: placement);
            entry.Body?.SetIntentSource(source: placement.Inhabit!.Source);
            entry.Body?.RecompileKit(motion: m_kitRows[kitIndex].Motion, actions: m_kits[kitIndex].Actions, actionThresholds: m_kits[kitIndex].ActionThresholds, actionShapes: m_kits[kitIndex].ActionShapes, roleMask: m_kits[kitIndex].RoleMask, roleOrdinals: m_kits[kitIndex].RoleOrdinals, actionState: m_kits[kitIndex].ActionState, program: m_kits[kitIndex].BodyMotionProgram, programs: m_bodyMotionPrograms, collider: m_kits[kitIndex].Collider, maxSmoothError: m_fixedMotion.MaxSmoothError, sprintChannelOrdinal: m_kits[kitIndex].SprintChannelOrdinal, driftChannelOrdinal: m_kits[kitIndex].DriftChannelOrdinal);
        }

        // Pass 2 — grow/shrink each inhabited placement to its declared count, at the highest free slots (document order).
        foreach (var placement in definition.Placements) {
            if ((placement.Inhabit is not { } inhabit) || (ResolveInhabitKit(definition: definition, placement: placement) is not { } kitName) || (ResolveKitOrNull(name: kitName) is not { } kitIndex)) {
                continue;
            }

            var desired = Math.Clamp(value: inhabit.Count, min: 0, max: PeerCapacity);
            var live = CountInhabitants(placementId: placement.Id);

            for (var ordinal = live; (ordinal < desired); ordinal++) {
                var slot = HighestFreeSlot();

                if (slot < 0) {
                    Console.Error.WriteLine(value: $"[world.placement: inhabited '{placement.Id}' has no free entity slot — the {Capacity}-slot table is full]");

                    break;
                }

                ActivateInhabitant(index: slot, placement: placement, inhabit: inhabit, kitIndex: kitIndex, ordinal: ordinal);
                admitted?.Add(item: PeerEventEntry(index: slot));
            }

            for (var extra = desired; (extra < live); extra++) {
                var slot = LowestInhabitant(placementId: placement.Id);

                if (slot >= 0) {
                    disconnected?.Add(item: PeerEventEntry(index: slot));
                    RetireInhabitant(index: slot);
                }
            }
        }

        // The inhabitant floor is the lowest slot any live inhabitant now occupies; re-clamp the census to it so peers
        // never reach an inhabitant, then bump the revision (the declared set moved).
        m_inhabitantFloor = Capacity;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (m_entries[index].PlacementId is not null) {
                m_inhabitantFloor = index;

                break;
            }
        }

        _ = SetSimulatedCount(count: m_simulatedCount);
        m_revision++;
    }

    /// <summary>The placement id an inhabited peer slot holds — the frame source / anchor back-reference.</summary>
    /// <param name="index">The population index (0-based).</param>
    /// <returns>The held placement id, or <see langword="null"/> for a plain census peer or an empty slot.</returns>
    public string? InhabitantPlacementId(int index) => m_entries[index].PlacementId;

    // Join one inhabited body at a claimed peer slot: mint its body from the resolved kit spawned at the placement's
    // scatter pose, seat its intent source, and tag the peer with the placement back-reference (the entry stays a
    // NetworkPeer — an inhabitant is a peer, not a separate kind).
    private void ActivateInhabitant(int index, WorldPlacement placement, WorldPlacementInhabit inhabit, byte kitIndex, int ordinal) {
        var entry = m_entries[index];
        var kit = m_kits[kitIndex];
        var body = new WorldBody(motion: m_kitRows[kitIndex].Motion, program: kit.BodyMotionProgram, programs: m_bodyMotionPrograms, actions: kit.Actions, actionThresholds: kit.ActionThresholds, actionShapes: kit.ActionShapes, roleMask: kit.RoleMask, roleOrdinals: kit.RoleOrdinals, actionState: kit.ActionState, collider: kit.Collider, maxSmoothError: m_fixedMotion.MaxSmoothError, sprintChannelOrdinal: kit.SprintChannelOrdinal, driftChannelOrdinal: kit.DriftChannelOrdinal);

        body.SetContactField(field: m_contactField);
        body.SetWaterline(level: m_waterline);

        var spawn = InhabitantSpawn(placement: placement, distribution: inhabit.Distribution!, ordinal: ordinal, count: inhabit.Count);
        var altitude = FixedQ4816.FromDouble(value: placement.Position.Y);
        var yaw = FixedQ4816.FromDouble(value: (placement.YawDegrees * (Math.PI / 180.0)));

        body.Pose(position: spawn with { Y = altitude }, yawRadians: yaw, pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);

        body.SetIntentSource(source: inhabit.Source);
        entry.Body = body;
        entry.PlacementId = placement.Id;
        entry.KitIndex = kitIndex;
        entry.LookIndex = ResolveInhabitLook(placement: placement);
        entry.ProducerState.PreferredAltitude = altitude;
        entry.ProducerState.AcquiredTarget = -1;
        ClearDesignations(entry: entry);
        entry.Generation = checked(entry.Generation + 1);
        entry.Active = true;
    }

    // Retire an inhabited peer slot back to an inactive census peer (its body dropped, its placement tag cleared). The
    // slot was already a NetworkPeer; only the placement back-reference and body go.
    private void RetireInhabitant(int index) {
        var entry = m_entries[index];

        entry.Body = null;
        entry.PlacementId = null;
        entry.Active = false;
        entry.ProducerState.AcquiredTarget = -1;
        ClearDesignations(entry: entry);
    }

    private int CountInhabitants(string placementId) {
        var count = 0;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (string.Equals(a: m_entries[index].PlacementId, b: placementId, comparisonType: StringComparison.Ordinal)) {
                count++;
            }
        }

        return count;
    }

    private int LowestInhabitant(string placementId) {
        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (string.Equals(a: m_entries[index].PlacementId, b: placementId, comparisonType: StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Collects every currently-inhabited body slot bound to <paramref name="placementId"/> into
    /// <paramref name="into"/> (cleared first) — the despawn-ownership guard's read: which live bodies a
    /// <c>removePlacement</c> rule effect targeting this placement would strip their Inhabit binding from. Rule
    /// cadence only (at most once per firing rule per tick), never the per-tick pose path.</summary>
    /// <param name="placementId">The placement id to match.</param>
    /// <param name="into">The reusable destination list.</param>
    public void CollectInhabitants(string placementId, List<int> into) {
        into.Clear();

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (string.Equals(a: m_entries[index].PlacementId, b: placementId, comparisonType: StringComparison.Ordinal)) {
                into.Add(item: index);
            }
        }
    }

    /// <summary>Whether solid world geometry leaves the sight-offset segment between two live bodies unobstructed —
    /// the general body-to-body spatial primitive a world rule's <c>$los:</c> operand rides, reusing the SAME
    /// contact-field query and local sight-offset a sensed target's own cone-sense check already uses. Either index
    /// out of range or naming an inactive slot reads as <see langword="false"/> (no sight line to nothing) rather
    /// than throwing — the "an ineligible candidate reads as absent" precedent this population's own field reads
    /// already follow.</summary>
    /// <param name="bodyA">The first body's 0-based entity index.</param>
    /// <param name="bodyB">The second body's 0-based entity index.</param>
    public bool HasLineOfSightBetween(int bodyA, int bodyB) {
        if (((uint)bodyA >= (uint)Capacity) || ((uint)bodyB >= (uint)Capacity)
            || (m_entries[bodyA].Body is not { } a) || (m_entries[bodyB].Body is not { } b)) {
            return false;
        }

        return HasLineOfSight(from: a.FixedPosition, fromOrientation: a.FixedOrientation, to: b.FixedPosition, toOrientation: b.FixedOrientation);
    }

    // The highest slot (127 downward) not currently claimed by an active seat/census peer or an inhabited peer — where a
    // new inhabited body lands, so inhabitants cluster at the top and never renumber an existing peer. A free slot is one
    // that holds no placement back-reference and no active census body.
    private int HighestFreeSlot() {
        for (var index = Capacity - 1; (index >= LocalSeatCount); index--) {
            var entry = m_entries[index];

            if ((entry.PlacementId is null) && !entry.Active) {
                return index;
            }
        }

        return -1;
    }

    // The distribution for one inhabited body is anchored at the placement root.
    private static FixedVector3 InhabitantSpawn(WorldPlacement placement, WorldDistribution distribution, int ordinal, int count) {
        var position = new FixedVector3(
            X: FixedQ4816.FromDouble(value: placement.Position.X),
            Y: FixedQ4816.FromDouble(value: placement.Position.Y),
            Z: FixedQ4816.FromDouble(value: placement.Position.Z)
        );
        var disc = (WorldDistributionRegion.Disc)distribution.Region;
        var radius = FixedQ4816.FromDouble(value: disc.Radius);

        if (radius <= FixedQ4816.Zero) {
            return position;
        }

        var sampleCount = (disc.SampleCount ?? count);
        var fraction = (FixedQ4816.FromInteger(value: ((2L * ordinal) + 1L)) / FixedQ4816.FromInteger(value: (2L * sampleCount)));
        var angle = WorldSequenceSampling.FixedAngle(sequence: distribution.Fill, index: ordinal);
        var r = (radius * FixedQ4816.Sqrt(value: fraction));
        var (sin, cos) = FixedQ4816.SinCos(angle: angle);

        return new FixedVector3(X: (position.X + (r * cos)), Y: position.Y, Z: (position.Z + (r * sin)));
    }

    // The kit name an inhabited placement resolves: its explicit Inhabit.Kit, or the creation's Locomotion token as a
    // kit name (the creator's rule). Null when neither resolves to a string (the validator already rejected such a row).
    private static string? ResolveInhabitKit(WorldDefinition definition, WorldPlacement placement) {
        if (placement.Inhabit?.Kit is { Length: > 0 } explicitKit) {
            return explicitKit;
        }

        foreach (var creation in definition.Creations) {
            if (string.Equals(a: creation.Id, b: placement.CreationId, comparisonType: StringComparison.Ordinal)) {
                return creation.Document.Behavior?.Locomotion;
            }
        }

        return null;
    }

    // The look row an inhabited placement's bodies wear: its Inhabit.Look when it names an authored look, else the
    // implicit index-derived look (the client renders the creation stamp from the placement's own CreationId regardless).
    private byte ResolveInhabitLook(WorldPlacement placement) {
        if ((placement.Inhabit?.Look is { Length: > 0 } lookName) && (ResolveLookOrNull(name: lookName) is { } lookIndex)) {
            return lookIndex;
        }

        return SelectRow(index: 0, assignment: m_lookAssignment, rows: m_lookAssignmentRows, rowCount: m_lookRows.Count);
    }

    private static WorldPlacement? FindInhabited(WorldDefinition definition, string placementId) {
        foreach (var placement in definition.Placements) {
            if ((placement.Inhabit is not null) && string.Equals(a: placement.Id, b: placementId, comparisonType: StringComparison.Ordinal)) {
                return placement;
            }
        }

        return null;
    }

    private byte? ResolveKitOrNull(string name) {
        for (var kit = 0; (kit < m_kitRows.Count); kit++) {
            if (string.Equals(a: m_kitRows[kit].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return (byte)kit;
            }
        }

        return null;
    }

    private byte? ResolveLookOrNull(string name) {
        for (var look = 0; (look < m_lookRows.Count); look++) {
            if (string.Equals(a: m_lookRows[look].Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return (byte)look;
            }
        }

        return null;
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

    /// <summary>The boot-built SDF contact field when the definition selects the FIELD provider, else
    /// <see langword="null"/> — the seam <see cref="WorldServer"/> adopts at construction so it owns the field lifecycle
    /// without a second boot build. A live rebuild instead receives the server's field back through
    /// <see cref="Rebuild(WorldDefinition, WorldSolidField?)"/>.</summary>
    public WorldSolidField? SolidField => (m_contactField as WorldSolidField);

    /// <summary>The live definition's analytic collider census, including placement-derived colliders even when the
    /// selected provider is the SDF field or collision is disabled.</summary>
    public WorldContactCensus ContactCensus => m_contactCensus;

    /// <summary>A monotonically increasing counter bumped whenever the declared set or palette changes (a seat joining,
    /// leaving, or recoloring, or the simulated count moving), never on a per-frame pose write. The frame source combines
    /// it with the roster's revision to decide when to rebuild the avatar program.</summary>
    public int Revision => m_revision;

    /// <summary>The number of active simulated stand-ins (indices <c>4..</c>).</summary>
    public int SimulatedCount => m_simulatedCount;

    /// <summary>The stored peer intent-source default — a template, not an
    /// aggregate, which is why it stays observable at zero peers: newly activated peers take it, and an explicit
    /// population source command sets it and sweeps every peer. Render-inert: it reshapes only the intent
    /// producers, never the declared set or palette, so it does not bump the <see cref="Revision"/>.</summary>
    public IntentSource DefaultPeerSource => m_defaultPeerSource;

    /// <summary>The deterministic kit row index assigned to a stable population slot.</summary>
    /// <param name="index">The population index (0-based).</param>
    /// <returns>The slot's assigned kit row index.</returns>
    public byte KitIndex(int index) => m_entries[index].KitIndex;

    // The kit row a population index actually runs: a local seat (0..LocalSeatCount) always reads the resolved seat
    // kit (m_seatKit), never its entry's own KitIndex — the seat kit can differ from a seat entry's assigned row on a
    // multi-kit world. Every seat-vs-peer kit read (recompile, producer-support checks, kit-replace safety, and the
    // runtime coherence door) shares this ONE resolver so they can never disagree.
    private byte ResolveKitIndex(int index) => ((index < LocalSeatCount) ? m_seatKit : m_entries[index].KitIndex);

    /// <summary>The declared locomotion model of the kit assigned to a stable population slot — the runtime
    /// <c>player.motion</c> door's read of the same fact <see cref="WorldDefinitionValidator.TryValidateProgramCoherence"/>
    /// checks at boot, so a document-legal kit cannot runtime-switch into a program its model cannot back.</summary>
    /// <param name="index">The population index (0-based).</param>
    /// <returns>The slot's assigned kit's motion model.</returns>
    public WorldMotionModel KitMotion(int index) => m_kitRows[ResolveKitIndex(index: index)].Motion;

    /// <summary>Looks up a declared body motion program by name — the same table every kit's <see cref="WorldBody"/>
    /// resolves against, exposed so a caller (the <c>player.motion</c> switch door) can validate coherence BEFORE
    /// asking a body to switch.</summary>
    /// <param name="name">The declared program name.</param>
    /// <param name="program">The compiled program, or <see langword="null"/> when <paramref name="name"/> is undeclared.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a declared program.</returns>
    public bool TryGetBodyMotionProgram(string name, out CompiledBodyMotionProgram? program) => m_bodyMotionPrograms.TryGetValue(key: name, value: out program);

    /// <summary>The resolved LOOK row index for a stable population slot — carried out on the snapshot for the client's
    /// renderer (PRESENTATION-ONLY).</summary>
    /// <param name="index">The 0-based population index.</param>
    public byte LookIndex(int index) => m_entries[index].LookIndex;

    /// <summary>The live LOOK rows (the authored rows, or the implicit single catalog look) the census resolves against.</summary>
    public IReadOnlyList<WorldLook> LookRows => m_lookRows;

    /// <summary>Counts the active entities per kit row for console diagnostics (one slot per definition row).</summary>
    public int[] ActiveKitCounts() {
        var counts = new int[m_kits.Length];

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index].Active) {
                counts[m_entries[index].KitIndex]++;
            }
        }

        return counts;
    }

    /// <summary>Counts the active entities per LOOK row for the <c>world.looks</c> census (one slot per look row,
    /// mirroring <see cref="ActiveKitCounts"/>).</summary>
    public int[] ActiveLookCounts() {
        var counts = new int[m_lookRows.Count];

        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index].Active) {
                counts[m_entries[index].LookIndex]++;
            }
        }

        return counts;
    }

    private static byte SelectRow(int index, WorldRowAssignment assignment, byte[]? rows, int rowCount) {
        var sourceCount = (rows?.Length ?? rowCount);
        var selected = WorldSequenceSampling.Bucket(sequence: assignment.Sequence, index: index, count: sourceCount);

        return (rows is null) ? (byte)selected : rows[selected];
    }

    /// <summary>Whether the entry at <paramref name="index"/> is active (drawn this frame).</summary>
    /// <param name="index">The population index (0-based, <c>0..</c><see cref="Capacity"/>).</param>
    /// <returns><see langword="true"/> when the entry is active.</returns>
    public bool IsActive(int index) => m_entries[index].Active;

    /// <summary>The count of active entries THIS tick — a read-only aggregate over <see cref="IsActive"/>, computed
    /// on demand (never cached) since a world rule's <c>"$population"</c> reserved channel reads it at most once per
    /// tick. Each <c>WorldServer</c> — the boot instance's and every spawned <c>Puck.World.WorldInstance</c>'s alike —
    /// owns its own <see cref="WorldPopulation"/>, so this is ALREADY per-instance scoped under multi-world: reading it
    /// off one instance's population never observes another's occupancy. <c>WorldInstanceHost</c>'s reap-on-empty rule
    /// reads exactly this.</summary>
    /// <returns>The active-entry count.</returns>
    public int ActiveCount() {
        var count = 0;

        for (var index = 0; (index < m_entries.Length); index++) {
            if (m_entries[index].Active) {
                count++;
            }
        }

        return count;
    }

    /// <summary>The world's compiled channel table (name→ordinal, per-ordinal shape/threshold) — the co-driving
    /// fold's one source of per-channel shape/threshold (<see cref="Server.WorldServer"/>'s fold phase reads it
    /// directly rather than re-deriving it).</summary>
    public WorldChannelTable Channels => m_channels;

    /// <summary>The world's compiled target-register table sharing the Drive reach-mask ordinal space.</summary>
    public WorldTargetRegisterTable TargetRegisters => m_targets;

    /// <summary>Resolves an authored target register by name.</summary>
    public bool TryResolveTargetRegister(string name, out int index) => m_targets.TryGetIndex(name: name, index: out index);

    /// <summary>Writes one already-validated body subject into a body's named register.</summary>
    public void SetDesignation(int bodyIndex, int registerIndex, int subjectIndex) {
        m_entries[bodyIndex].Designations[registerIndex] = subjectIndex;
        m_entries[bodyIndex].DesignationRefusal = string.Empty;
    }

    /// <summary>Records the latest designation refusal for a live source body's read-back.</summary>
    public void NoteDesignationRefusal(int bodyIndex, string reason) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].DesignationRefusal = reason;
        }
    }

    /// <summary>Records the outcome of the latest <c>player.motion</c> switch attempt for a body — an empty
    /// <paramref name="reason"/> on success, the named refusal otherwise. <c>player.motion</c>'s handler reads this
    /// back through <see cref="MotionRefusal(int)"/> immediately after its synchronous submit (<c>WorldServer.Submit</c>
    /// drains inline) so its immediate echo reports the TRUE outcome instead of assuming success.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="reason">The refusal reason, or <see cref="string.Empty"/> on success.</param>
    public void NoteMotionRefusal(int bodyIndex, string reason) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].MotionRefusal = reason;
        }
    }

    /// <summary>The most recent <c>player.motion</c> switch refusal for a body, or <see cref="string.Empty"/> when its
    /// last attempt succeeded (or none has been made).</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public string MotionRefusal(int bodyIndex) => (((uint)bodyIndex < (uint)m_entries.Length) ? m_entries[bodyIndex].MotionRefusal : string.Empty);

    /// <summary>Records the outcome of a SUCCESSFUL <c>player.stop</c> for a body — the same synchronous-submit
    /// read-back shape as <see cref="NoteMotionRefusal"/>, so <c>player.stop</c>'s handler can quote the TRUE
    /// released/cleared counts instead of a fixed template string. ALWAYS clears any refusal note the body's stop
    /// slot carried, so a denial from an earlier attempt can never bleed into a fresh success's echo.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="outcome">The counts <see cref="WorldBody.Stop"/> computed.</param>
    public void NoteStopOutcome(int bodyIndex, StopOutcome outcome) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].StopRefusal = string.Empty;
            m_entries[bodyIndex].StopOutcome = outcome;
        }
    }

    /// <summary>Records a REFUSED <c>player.stop</c> attempt for a body — <see cref="WorldServer.ApplyCommand"/>
    /// calls this from EVERY early return a <see cref="WorldCommand.Stop"/> can take (the grant-table denial, the
    /// missing/inactive body) before it ever reaches <see cref="NoteStopOutcome"/>, so the slot is written on every
    /// single outcome a Stop command can have — never left holding a stale success from some earlier, unrelated
    /// attempt. Also resets the outcome counts to zero, so a handler that reads them without checking the refusal
    /// first still sees nothing rather than a fabricated affirmative.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="reason">The refusal reason.</param>
    public void NoteStopRefusal(int bodyIndex, string reason) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].StopRefusal = reason;
            m_entries[bodyIndex].StopOutcome = default;
        }
    }

    /// <summary>The most recent <c>player.stop</c> refusal for a body, or <see cref="string.Empty"/> when its last
    /// attempt succeeded (or none has been made). <c>player.stop</c>'s handler checks this BEFORE
    /// <see cref="LastStopOutcome"/> — a non-empty refusal means the counts were never applied.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public string StopRefusal(int bodyIndex) => (((uint)bodyIndex < (uint)m_entries.Length) ? m_entries[bodyIndex].StopRefusal : string.Empty);

    /// <summary>The most recent <c>player.stop</c> outcome for a body, or a zeroed outcome when none has been made
    /// (or the last attempt was refused — see <see cref="StopRefusal"/>).</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public StopOutcome LastStopOutcome(int bodyIndex) => (((uint)bodyIndex < (uint)m_entries.Length) ? m_entries[bodyIndex].StopOutcome : default);

    /// <summary>Records the outcome of a SUCCESSFUL timed <c>player.press</c> — the effective hold (post
    /// grant-ceiling and engine-backstop clamping) and which cap, if any, decided it — the same synchronous-submit
    /// read-back shape as <see cref="NoteMotionRefusal"/>, so the handler can name a silent truncation instead of
    /// echoing the requested duration as if it were honored. ALWAYS clears any refusal note the body's press slot
    /// carried.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="outcome">The outcome <see cref="WorldBody.PressChannel(int, FixedQ4816, float, FixedQ4816)"/> returned.</param>
    public void NotePressOutcome(int bodyIndex, PressOutcome outcome) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].PressRefusal = string.Empty;
            m_entries[bodyIndex].PressOutcome = outcome;
        }
    }

    /// <summary>Records a SUCCESSFUL untimed <c>player.press</c> (the host-step tap, which carries no numeric
    /// outcome of its own) — clears any refusal note the body's press slot carried, the same way
    /// <see cref="NotePressOutcome"/> does for the timed path, so the ONE shared refusal slot both press paths read
    /// back through is always fresh regardless of which one last ran.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public void NotePressSuccess(int bodyIndex) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].PressRefusal = string.Empty;
        }
    }

    /// <summary>Records a REFUSED <c>player.press</c> attempt (timed or untimed alike — they share one refusal
    /// slot) for a body — <see cref="WorldServer.ApplyCommand"/> calls this from EVERY early return a
    /// <see cref="WorldCommand.PressChannel"/> can take, so the slot is written on every single outcome the command
    /// can have. Also resets the timed-path's outcome to a neutral default, so a handler that reads it without
    /// checking the refusal first still sees nothing rather than a fabricated affirmative.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="reason">The refusal reason.</param>
    public void NotePressRefusal(int bodyIndex, string reason) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].PressRefusal = reason;
            m_entries[bodyIndex].PressOutcome = default;
        }
    }

    /// <summary>The most recent <c>player.press</c> refusal for a body, or <see cref="string.Empty"/> when its last
    /// attempt succeeded (or none has been made). <c>player.press</c>'s handler checks this BEFORE
    /// <see cref="LastPressOutcome"/> — a non-empty refusal means no press was applied.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public string PressRefusal(int bodyIndex) => (((uint)bodyIndex < (uint)m_entries.Length) ? m_entries[bodyIndex].PressRefusal : string.Empty);

    /// <summary>The most recent timed <c>player.press</c> outcome for a body, or a zeroed/<see cref="PressHoldCapKind.None"/>
    /// outcome when none has been made (or the last attempt was refused — see <see cref="PressRefusal"/>).</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public PressOutcome LastPressOutcome(int bodyIndex) => (((uint)bodyIndex < (uint)m_entries.Length) ? m_entries[bodyIndex].PressOutcome : default);

    /// <summary>Describes every target register and the most recent designation refusal for one body.</summary>
    public string DescribeTargets(int bodyIndex) {
        var entry = m_entries[bodyIndex];
        var rows = new string[m_targets.Count];

        for (var index = 0; (index < rows.Length); index++) {
            var register = m_targetRows[index];
            var subject = entry.Designations[index];
            var status = ((subject >= 0) ? $"body:{subject}{(IsActive(index: subject) ? string.Empty : "(inactive)")}" : "none");
            var effectiveRange = EffectiveTargetValue(body: entry.Body, stateName: register.RangeState, authoredMaximum: register.MaximumRange);
            var effectiveAngle = EffectiveTargetValue(body: entry.Body, stateName: register.HalfAngleState, authoredMaximum: register.MaximumHalfAngleDegrees);
            rows[index] = string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"{register.Name}={status} envelope:range={effectiveRange:0.###}/{register.MaximumRange:0.###},halfAngle={effectiveAngle:0.###}/{register.MaximumHalfAngleDegrees:0.###},rangeState={register.RangeState ?? "none"},halfAngleState={register.HalfAngleState ?? "none"},los={register.RequiresLineOfSight.ToString().ToLowerInvariant()}");
        }

        var refusal = (entry.DesignationRefusal.Length == 0 ? "none" : entry.DesignationRefusal);
        return $"[player.targets: p{bodyIndex + 1} {(rows.Length == 0 ? "registers=none" : string.Join(separator: "; ", values: rows))} lastRefusal={refusal}]";
    }

    /// <summary>Re-resolves a proposed body subject against one designation envelope.</summary>
    public bool DesignationWithinEnvelope(int sourceIndex, int targetIndex, WorldTargetRegister register, float rangeValue, float halfAngleDegrees, out string reason) {
        var source = m_entries[sourceIndex].Body!;
        var target = m_entries[targetIndex].Body!;
        var origin = source.FixedPosition;
        var candidate = target.FixedPosition;
        var forward = source.FixedOrientation.Rotate(vector: s_localForward);
        var range = FixedQ4816.FromDouble(value: rangeValue);
        var minimumDot = FixedQ4816.FromDouble(value: Math.Cos(halfAngleDegrees * (Math.PI / 180.0)));

        if (!BodyTargetConeSense.Contains(origin: in origin, forward: in forward, candidate: in candidate, range: range, minimumDot: minimumDot, distanceSquared: out var distanceSquared)) {
            var distance = FixedQ4816.Sqrt(value: distanceSquared);
            reason = string.Create(provider: System.Globalization.CultureInfo.InvariantCulture, handler: $"body:{targetIndex} is outside range/cone (distance={distance:0.###}, range={rangeValue:0.###}, halfAngle={halfAngleDegrees:0.###})");
            return false;
        }
        if (register.RequiresLineOfSight && !HasLineOfSight(from: origin, fromOrientation: source.FixedOrientation, to: candidate, toOrientation: target.FixedOrientation)) {
            reason = $"solid geometry blocks line of sight to body:{targetIndex}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>Reads a visited-world effective slot and composes it with a register maximum by taking the tighter value.</summary>
    public static float EffectiveTargetValue(WorldBody? body, string? stateName, float authoredMaximum) {
        if ((body is null) || string.IsNullOrWhiteSpace(value: stateName) || !body.TryReadDurableCounter(name: stateName, value: out var requested)) {
            return authoredMaximum;
        }
        return Math.Clamp(value: (float)(double)requested, min: 0f, max: authoredMaximum);
    }

    /// <summary>Whether <paramref name="bodyIndex"/> is HUMAN-OCCUPIED — the co-driving fold's occupancy
    /// discriminator (and, since owner ruling 2026-08-02, the bot-overwrite door in
    /// <c>WorldServer.ApplyIntentSubmission</c>), pinned as a thing to DEFINE rather than read: a body is
    /// human-occupied iff a LOCAL SEAT slot is <see cref="IsActive"/> AND bound to it, OR the body is bound to an
    /// <see cref="IsAdmittedPeer"/> — never <see cref="WorldBody.Source"/> (what fills gaps; its
    /// <see cref="IntentSource.Live"/> value ALSO covers a remote peer) and never engagement (an orthogonal axis).
    /// The pool this gates EXISTS only when this returns <see langword="true"/>: an unoccupied body is a bot at full
    /// authority by construction, not by an undefined ceiling.
    /// <para><b>A PARKED body (see <see cref="Entry.Parked"/>) still reads <see langword="true"/> here</b> — the
    /// owner's occupancy ruling for park-with-grace: <see cref="IsActive"/>/<see cref="IsAdmittedPeer"/> are exactly
    /// what a park leaves untouched, by construction, so no separate parked-aware branch exists in this method. A
    /// disconnected-but-parked body stays targetable and its CC pool keeps running offline through the grace window;
    /// only <see cref="ReclaimExpiredParks"/>'s eventual teardown removes it from the pool.</para></summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <returns><see langword="true"/> when the index is bound to a live local seat or an admitted peer.</returns>
    public bool IsHumanOccupied(int bodyIndex) =>
        ((((uint)bodyIndex < LocalSeatCount) && IsActive(index: bodyIndex)) || IsAdmittedPeer(bodyIndex: bodyIndex));

    /// <summary>Whether <paramref name="bodyIndex"/> is bound to a REMOTE-ADMITTED human — the P7 socket phase's own
    /// concept (design doc P7, "Socket at divisor 1"). Live for a body a <see cref="TryAdmitRemotePeer"/> call is
    /// still holding (see <see cref="Entry.IsRemoteHuman"/>); a socket door's disconnect clears it through
    /// <see cref="ApplyPeerDisconnected"/> exactly as admission set it.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public bool IsAdmittedPeer(int bodyIndex) => (((uint)bodyIndex < Capacity) && m_entries[bodyIndex].IsRemoteHuman);

    /// <summary>The current generation-bearing peer identity for a peer slot.</summary>
    /// <param name="index">The peer body index.</param>
    /// <returns>The current peer principal.</returns>
    public WorldPrincipal PeerPrincipal(int index) => WorldPrincipal.Peer(index: index, generation: m_entries[index].Generation);

    /// <summary>The <see cref="WorldBody"/> an entry owns while active, or <see langword="null"/> for an inactive
    /// entry. The <c>player.*</c> command wire resolves an index <c>1..128</c> to the entry's own body and produces
    /// intents on it (a warp/run/face/stop command), never a pose stream.</summary>
    /// <param name="index">The population index (0-based, <c>0..</c><see cref="Capacity"/>).</param>
    public WorldBody? EntryBody(int index) => m_entries[index].Body;

    /// <summary>Checks whether the kit selected for one body declares a named producer source.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <param name="source">The requested source.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when the source is live, idle, or declared by the selected kit.</returns>
    public bool SupportsSource(int index, IntentSource source, out string refusal) {
        if (source.IsLive || source.IsIdle) {
            refusal = string.Empty;

            return true;
        }
        if (!source.IsProducer || (source.ProducerName is not { } producerName)) {
            refusal = $"intent source '{source}' is not defined";

            return false;
        }

        var kitIndex = ResolveKitIndex(index: index);
        if (!m_kits[kitIndex].Producers.ContainsKey(key: producerName)) {
            refusal = $"kit '{m_kitRows[kitIndex].Name}' declares no parameters for producer '{producerName}'";

            return false;
        }

        refusal = string.Empty;

        return true;
    }

    /// <summary>Checks whether replacing a kit would orphan a producer selected by a live body.</summary>
    /// <param name="replacement">The proposed kit row.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> when every affected live source remains declared.</returns>
    public bool CanReplaceKit(WorldKit replacement, out string refusal) {
        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];
            var selectedKit = ResolveKitIndex(index: index);

            if (entry is { Active: true, Body: { } body } &&
                string.Equals(a: m_kitRows[selectedKit].Name, b: replacement.Name, comparisonType: StringComparison.Ordinal) &&
                (body.Source.ProducerName is { } producerName) &&
                !replacement.Producers.ContainsKey(key: producerName)) {
                refusal = $"body {index + 1} selects producer '{producerName}' from kit '{replacement.Name}'";

                return false;
            }
        }

        refusal = string.Empty;

        return true;
    }

    /// <summary>The entry's body color (the avatar's material albedo). A seat's is its assigned profile color; the
    /// client folds the pending-gray desaturation in on its side.</summary>
    /// <param name="index">The population index (0-based).</param>
    public Vector3 BodyColor(int index) => m_entries[index].BodyColor;

    /// <summary>Activates a local seat (indices <c>0..</c><see cref="LocalSeatCount"/>) — the session join's server
    /// half: mints the seat's body at its full authored spawn pose, seated on <paramref name="profile"/>. A no-op if
    /// the seat is already active. Bumps the revision.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The profile the seat's body reads speeds and color from, or <see langword="null"/>.</param>
    public void ActivateSeat(int slot, WorldIdentity? profile) {
        var entry = m_entries[slot];

        if (entry.Active) {
            return;
        }

        // The seat body constructs from the definition's designated seat kit row (its tuning and lane bindings); the
        // seated profile's speeds still override live.
        var body = new WorldBody(motion: m_kitRows[m_seatKit].Motion, program: m_kits[m_seatKit].BodyMotionProgram, programs: m_bodyMotionPrograms, actions: m_kits[m_seatKit].Actions, actionThresholds: m_kits[m_seatKit].ActionThresholds, actionShapes: m_kits[m_seatKit].ActionShapes, roleMask: m_kits[m_seatKit].RoleMask, roleOrdinals: m_kits[m_seatKit].RoleOrdinals, actionState: m_kits[m_seatKit].ActionState, collider: m_kits[m_seatKit].Collider, maxSmoothError: m_fixedMotion.MaxSmoothError, sprintChannelOrdinal: m_kits[m_seatKit].SprintChannelOrdinal, driftChannelOrdinal: m_kits[m_seatKit].DriftChannelOrdinal) {
            Profile = profile,
        };

        body.SetContactField(field: m_contactField);
        body.SetWaterline(level: m_waterline);

        var spawnPoint = m_seatSpawns[slot];
        body.Pose(
            position: spawnPoint.Position,
            yawRadians: spawnPoint.YawRadians,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        // Seats default Live and are never touched by population operations; producer state is seeded so a later
        // player.control producer:<name> uses the same deterministic path as a peer.
        ClearDesignations(entry: entry);
        SeedSeatWander(slot: slot);
        entry.Body = body;
        entry.BodyColor = (profile?.Color ?? Vector3.Zero);
        entry.Active = true;
        m_revision++;
    }

    /// <summary>Deactivates a local seat — the session leave's server half. A no-op if the seat is not active.
    /// PARK-WITH-GRACE: when <c>population.reconnectGraceTicks</c> is positive, this does NOT drop the body — it
    /// marks the entry <see cref="Entry.Parked"/> and stamps <see cref="Entry.ParkedUntilTick"/>, keeping the body
    /// (pose, durable state) in the sim/collider set and <see cref="IsHumanOccupied"/> reading <see langword="true"/>
    /// exactly as before the leave. The FULL teardown this method used to perform unconditionally now fires from
    /// <see cref="ReclaimExpiredParks"/> once the grace window passes with no matching re-Join (see
    /// <see cref="TryResumeParkedSeat"/>). <c>reconnectGraceTicks == 0</c> keeps the immediate-teardown behavior
    /// exactly as authored (the grace window is opt-in, not a forced behavior change for a world that authors none).
    /// Bumps the revision either way.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="tick">The current tick — the basis <see cref="Entry.ParkedUntilTick"/> is stamped from
    /// (<c>tick + reconnectGraceTicks</c>).</param>
    public void DeactivateSeat(int slot, ulong tick) {
        var entry = m_entries[slot];

        if (!entry.Active) {
            return;
        }

        if (m_reconnectGraceTicks <= 0) {
            entry.Body = null;
            entry.Active = false;
            entry.Parked = false;
            entry.ParkedUntilTick = 0L;
            m_revision++;

            return;
        }

        entry.Parked = true;
        entry.ParkedUntilTick = unchecked((long)tick + m_reconnectGraceTicks);
        m_revision++;
    }

    /// <summary>Detaches a local seat's body for a SAME-PROCESS, SAME-HOST-TICK transfer to another world instance —
    /// the LEAVE half of atomic body transfer (the composition root's per-host pending-transfer drain). Unlike
    /// <see cref="DeactivateSeat"/>, this NEVER parks and never consults <c>reconnectGraceTicks</c>: it unconditionally
    /// clears <see cref="Entry.Body"/> and <see cref="Entry.Active"/> so the body stops being advanced (or counted
    /// active) in THIS instance from the moment it returns — a park would leave <see cref="Entry.Active"/> true and
    /// <see cref="AdvanceSeats"/> would keep integrating it here, which is exactly the double-embodiment a transfer
    /// must not allow once the SAME identity is about to be re-activated in another instance's population. Only the
    /// seat binding (the caller already holds the slot) and the body's own <see cref="WorldBody.Profile"/> survive —
    /// pose, velocity, action-track state, and tape are discarded here by design (the destination world re-embodies
    /// the identity through its OWN normal join/kit-assignment; none of that state is meaningful under a different
    /// kit). A no-op returning <see langword="false"/> when the seat holds no active body — nothing captured, nothing
    /// changed.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The detached body's own retained identity, or <see langword="null"/> for an anonymous
    /// seat.</param>
    /// <returns><see langword="true"/> when an active body was detached.</returns>
    public bool TryDetachSeatForTransfer(int slot, out WorldIdentity? profile) {
        var entry = m_entries[slot];

        if (!entry.Active || (entry.Body is not { } body)) {
            profile = null;

            return false;
        }

        profile = body.Profile;
        entry.Body = null;
        entry.Active = false;
        entry.Parked = false;
        entry.ParkedUntilTick = 0L;
        ClearDesignations(entry: entry);
        m_revision++;

        return true;
    }

    /// <summary>Whether <paramref name="slot"/> holds a body currently PARKED (see <see cref="Entry.Parked"/>) —
    /// the resume-eligibility gate a re-Join checks before <see cref="ActivateSeat"/> would mint a fresh body.
    /// <see langword="false"/> for an out-of-range slot, an inactive slot, or an active-but-never-left one.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    public bool IsSeatParked(int slot) => (((uint)slot < LocalSeatCount) && m_entries[slot] is { Active: true, Parked: true });

    /// <summary>Attempts to resume a PARKED seat's retained body for a re-Join — BODY-RESUME, the reconnect
    /// primitive's third half. The match rule is deliberately narrow and precise: the incoming
    /// <paramref name="profile"/>'s <see cref="WorldIdentity.Id"/> must equal the parked body's OWN retained
    /// <see cref="WorldBody.Profile"/>.<see cref="WorldIdentity.Id"/> — read directly off the body the park never
    /// dropped, so no separate "remembered identity" field is needed. Both <see langword="null"/> (an anonymous seat
    /// reconnecting anonymously) counts as a match too. On a match: clears <see cref="Entry.Parked"/> and returns
    /// <see langword="true"/>, leaving pose/durable state exactly as parked (no fresh spawn, no
    /// <c>ResetDurableState</c> — that reset is keyed on an ACTUAL id change, and this is the SAME id). On a
    /// mismatch, the parked body is left untouched (so a later, correctly-identified re-Join can still recover it
    /// before grace expires) and <paramref name="mismatch"/> is set, letting the caller report a distinct refusal
    /// from "nothing to resume". <see langword="false"/> for a slot that is not parked at all — the caller falls
    /// back to <see cref="ActivateSeat"/>.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The re-Join's resolved identity, or <see langword="null"/> for an anonymous seat.</param>
    /// <param name="mismatch">Set <see langword="true"/> when the slot IS parked but the identity does not match.</param>
    /// <returns><see langword="true"/> when the parked body was resumed.</returns>
    public bool TryResumeParkedSeat(int slot, WorldIdentity? profile, out bool mismatch) {
        mismatch = false;

        if (!IsSeatParked(slot: slot)) {
            return false;
        }

        var entry = m_entries[slot];

        if (!string.Equals(a: entry.Body?.Profile?.Id, b: profile?.Id, comparisonType: StringComparison.Ordinal)) {
            mismatch = true;

            return false;
        }

        entry.Parked = false;
        entry.ParkedUntilTick = 0L;

        // The retained body already carries this identity (that is what the match just proved) — a re-seat only
        // matters when the caller resolved a DIFFERENT WorldIdentity instance for the same id (a profile edit
        // reloaded between park and resume), so the cached color follows without disturbing durable state.
        if ((entry.Body is { } body) && (profile is not null) && !ReferenceEquals(objA: body.Profile, objB: profile)) {
            body.Profile = profile;
            entry.BodyColor = profile.Color;
        }

        m_revision++;

        return true;
    }

    /// <summary>Reseats a seat's body on a profile — the <c>player.identity</c>/confirm server half. The body reads its
    /// speeds live off the profile; the entry color follows for the snapshot.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The profile to seat on.</param>
    public void SetSeatProfile(int slot, WorldIdentity profile) {
        var entry = m_entries[slot];

        if (entry.Body is not { } body) {
            return;
        }

        if (!string.Equals(a: body.Profile?.Id, b: profile.Id, comparisonType: StringComparison.Ordinal)) {
            body.ResetDurableState();
        }
        body.Profile = profile;
        entry.BodyColor = profile.Color;
    }

    /// <summary>Refreshes the cached body color of every active seat currently seated on <paramref name="profile"/> —
    /// the server half of a live <c>SetPlayerSection(identity)</c> color edit. The seat renders its color live off the
    /// shared handle client-side, but the per-entry <see cref="BodyColor"/> cache is the snapshot's source of truth, so
    /// it must not lie after an identity change. Bumps the revision when a seat's color actually moves.</summary>
    /// <param name="profile">The edited profile handle.</param>
    public void RefreshSeatColor(WorldIdentity profile) {
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
    /// intent per the merge rule. Runs after <see cref="AdvanceSimulated"/> in the server step, so the
    /// population advances before seats.</summary>
    /// <param name="tick">The explicit simulation tick.</param>
    /// <param name="stepTicks">The exact engine ticks this step advances.</param>
    /// <param name="engageProbeOrdinals">Per-slot channel ordinal to probe for the context-sensitive-button rising
    /// edge, sentinel <c>-1</c> for "no probe", or empty for none
    /// at all — the zero-cost path every world without an <c>engageChannel</c>-bearing screen takes.</param>
    /// <param name="engageEdges">Receives, per slot, whether that slot's probe ordinal fired a rising edge this tick
    /// (the caller — <see cref="Puck.World.Server.WorldServer.Step"/> — routes each into
    /// <see cref="Puck.World.Server.WorldEngagement.Engage"/>). Every entry is written for an active slot; an inactive
    /// slot is left at the caller's own default (callers pass a freshly zeroed span).</param>
    public void AdvanceSeats(ulong tick, ulong stepTicks, ReadOnlySpan<int> engageProbeOrdinals, Span<bool> engageEdges) {
        for (var slot = 0; (slot < LocalSeatCount); slot++) {
            if (m_entries[slot] is { Active: true, Body: { } body } entry) {
                StageProducer(entry: entry, body: body, index: slot, stepTicks: stepTicks);

                var probe = ((!engageProbeOrdinals.IsEmpty && (engageProbeOrdinals[slot] >= 0)) ? engageProbeOrdinals[slot] : (int?)null);

                var targets = ReadEffectTargets(selfIndex: slot, entry: entry, self: body.FixedPosition);
                engageEdges[slot] = body.Advance(tick: tick, stepTicks: stepTicks, engageProbeOrdinal: probe, entityIndex: slot, effectTargets: targets, effectOutputs: m_effectOutputs, designationOutputs: m_designationOutputs, generatorInvocations: m_generatorInvocations);
            }
        }
    }

    // Run the named producer before motion. Live and Idle name no producer.
    private void StageProducer(Entry entry, WorldBody body, int index, ulong stepTicks) {
        var kitIndex = ((entry.Kind == PopulationKind.LocalSeat) ? m_seatKit : entry.KitIndex);
        if ((body.Source.ProducerName is not { } name) || !m_kits[kitIndex].Producers.TryGetValue(key: name, value: out var producer)) {
            return;
        }

        var sensors = ReadProducerSensors(selfIndex: index, entry: entry, currentTarget: entry.ProducerState.AcquiredTarget, self: body.FixedPosition, forward: body.FixedOrientation.Rotate(vector: s_localForward), producer: producer);
        body.ExecuteProducer(producer: producer, state: ref entry.ProducerState, sensors: in sensors, stepTicks: stepTicks);
    }

    /// <summary>Activates the first <paramref name="count"/> census stand-ins (indices <c>4..</c>), clamped to
    /// <c>0..min(networkPlayers cap, </c><see cref="MaxSimulated"/><c>)</c>, and deactivates the rest. A newly-activated
    /// entry is re-seeded to a fresh spawn and given its own <see cref="WorldBody"/> (a server-authoritative spawn at that
    /// pose); a deactivated entry drops its body; entries already active keep wandering. Bumps the revision only when an
    /// occupancy flips.</summary>
    /// <param name="count">The requested active census count.</param>
    /// <param name="admitted">Optional sink for the peer generations admitted by the census change.</param>
    /// <param name="disconnected">Optional sink for the peer generations disconnected by the census change.</param>
    /// <returns>The clamped count actually applied.</returns>
    public int SetSimulatedCount(int count, List<WorldPeerEventEntry>? admitted = null, List<WorldPeerEventEntry>? disconnected = null) {
        // Clamp against the remote admission cap (networkPlayers) AND the live inhabitant floor: inhabited peers at the
        // top of the table lower the census ceiling by their physical occupancy, so a request past either is clamped
        // rather than allowed to collide with an inhabitant or breach the cap.
        var clamped = Math.Clamp(value: count, min: 0, max: SimulatedCeiling);
        var changed = false;

        for (var offset = 0; (offset < PeerCapacity); offset++) {
            var index = (LocalSeatCount + offset);
            var entry = m_entries[index];

            // Inhabited peer slots are owned by ReconcileInhabitants; the census never activates or clears them. Because
            // inhabitants claim the TOP of the peer slice and `clamped <= MaxSimulated`, `offset < clamped` never names
            // an inhabited slot, so census peers and inhabitants cannot meet. A remote-admitted human (also parked at
            // the top, via TryAdmitRemotePeer) is skipped the same way — a live human's body must never be silently
            // deactivated (or reseeded) by an unrelated world.population count edit.
            if ((entry.PlacementId is not null) || entry.IsRemoteHuman) {
                continue;
            }

            var desired = (offset < clamped);

            if (entry.Active == desired) {
                continue;
            }

            if (desired) {
                ActivateSimulated(index: index);
                entry.Active = true;
                admitted?.Add(item: PeerEventEntry(index: index));
            } else {
                disconnected?.Add(item: PeerEventEntry(index: index));
                // A re-activation mints a fresh body at the canonical spawn.
                entry.Body = null;
                entry.Active = false;
            }

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
    /// <c>player.fly</c> tape still drives regardless.</summary>
    /// <param name="source">The intent source to store and sweep.</param>
    /// <param name="refusal">The named refusal when an assigned kit does not declare the producer.</param>
    /// <returns><see langword="true"/> when every peer kit admits the source.</returns>
    public bool TrySetPeerSource(IntentSource source, out string refusal) {
        for (var index = LocalSeatCount; (index < Capacity); index++) {
            if (!SupportsSource(index: index, source: source, refusal: out refusal)) {
                return false;
            }
        }

        m_defaultPeerSource = source;

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            m_entries[index].Body?.SetIntentSource(source: source);
        }

        refusal = string.Empty;

        return true;
    }

    /// <summary>Advances every active simulated stand-in by one sub-step: a named producer runs before motion, then
    /// every peer body integrates. A live <c>player.fly</c> tape or
    /// a submitted intent overrides the producer per the merge rule; an <see cref="IntentSource.Idle"/> peer holds
    /// still between tape segments yet its tapes still play. The local seats are advanced separately by
    /// <see cref="AdvanceSeats"/>.</summary>
    /// <remarks>Runs FIRST, before any body (peer or seat) advances this tick, so an ATTACHED solid placement's
    /// colliders (<see cref="WorldColliderSet.RefreshAttached"/>) are refreshed exactly once and every body's push
    /// this tick resolves against the SAME snapshot — the analytic contact provider only; the field provider compiles
    /// its whole SDF program once and a bad-op world already fails loudly at boot/apply if it cannot (attach+solid
    /// stays refused there, see the document validator).</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is zero.</exception>
    public void AdvanceSimulated(ulong tick, ulong stepTicks) {
        ArgumentOutOfRangeException.ThrowIfZero(value: stepTicks);

        (m_contactField as WorldColliderSet)?.RefreshAttached(population: this);

        for (var index = LocalSeatCount; (index < Capacity); index++) {
            var entry = m_entries[index];

            // Network peers AND inhabitants advance here (both own their body and run a deterministic producer until a
            // transport/possession supplies intents). An inactive entry has no body.
            if (!entry.Active || (entry.Kind == PopulationKind.LocalSeat) || (entry.Body is not { } player)) {
                continue;
            }

            StageProducer(entry: entry, body: player, index: index, stepTicks: stepTicks);
            var targets = ReadEffectTargets(selfIndex: index, entry: entry, self: player.FixedPosition);
            player.Advance(tick: tick, stepTicks: stepTicks, entityIndex: index, effectTargets: targets, effectOutputs: m_effectOutputs, designationOutputs: m_designationOutputs, generatorInvocations: m_generatorInvocations);
        }
    }

    private BodyEffectTargets ReadEffectTargets(int selfIndex, Entry entry, in FixedVector3 self) {
        var currentTarget = entry.ProducerState.AcquiredTarget;
        var current = ((currentTarget >= 0) && (currentTarget < Capacity) && m_entries[currentTarget].Active && (m_entries[currentTarget].Body is not null))
            ? currentTarget
            : -1;
        return new BodyEffectTargets(ProducerTarget: current, AffectingSubject: entry.Body?.AffectingSubject ?? -1);
    }

    /// <summary>Applies entity-directed effect outputs after every body has advanced, then exposes player-keyed durable
    /// writes for the completed tick.</summary>
    public void CompleteStep(ulong tick) {
        foreach (var output in m_effectOutputs) {
            if (((uint)output.TargetIndex < (uint)m_entries.Length) && (m_entries[output.TargetIndex].Body is { } target)) {
                _ = target.ApplyTargetedEffect(sourceIndex: output.SourceIndex, instruction: output.Instruction);
            }
        }
        m_effectOutputs.Clear();

        m_durableStateOutputs.Clear();
        for (var index = 0; (index < m_entries.Length); index++) {
            m_entries[index].Body?.TakeDurableStateOutputs(tick: tick, entityIndex: index, outputs: m_durableStateOutputs);
        }
    }

    /// <summary>The durable writes emitted by the most recently completed tick.</summary>
    public IReadOnlyList<DurableStateOutput> DurableStateOutputs => m_durableStateOutputs;

    /// <summary>The authored designation submissions emitted during the most recently completed tick.</summary>
    public IReadOnlyList<WorldDesignation> DesignationOutputs => m_designationOutputs;

    /// <summary>Clears designation outputs after the world authority has applied them.</summary>
    public void ClearDesignationOutputs() => m_designationOutputs.Clear();

    /// <summary>The <c>generate</c> effect firings staged by the most recently completed tick's advance — drained and
    /// enqueued through the ordinary mutation pipeline by <c>WorldServer.Step</c>, mirroring
    /// <see cref="DesignationOutputs"/>'s own shape.</summary>
    public IReadOnlyList<WorldGeneratorInvocation> GeneratorInvocationOutputs => m_generatorInvocations;

    /// <summary>Clears staged generator invocations after the world authority has enqueued them.</summary>
    public void ClearGeneratorInvocationOutputs() => m_generatorInvocations.Clear();

    private BodyProducerSensors ReadProducerSensors(int selfIndex, Entry entry, int currentTarget, in FixedVector3 self, in FixedVector3 forward, CompiledBodyProducer producer) {
        var candidate = BodySensorTarget.None;
        var targetSource = producer.Target;

        if (targetSource?.Source is BodyTargetSource.Designated) {
            var designated = entry.Designations[targetSource.Value.RegisterIndex];

            if ((designated >= 0) && (designated < Capacity) && m_entries[designated].Active && (m_entries[designated].Body is { } designatedBody)) {
                var position = designatedBody.FixedPosition;
                candidate = new BodySensorTarget(Index: designated, Position: position, DistanceSquared: (position - self).LengthSquared);
            }
        } else if (targetSource?.Source is BodyTargetSource.Sensed sensed) {
            var fixedSource = targetSource.Value;

            for (var index = 0; (index < Capacity); index++) {
                if ((index == selfIndex) || !m_entries[index].Active || (m_entries[index].Body is not { } body)
                    || ((sensed.Scope == BodyTargetScope.Seats) && (m_entries[index].Kind != PopulationKind.LocalSeat))) {
                    continue;
                }

                var position = body.FixedPosition;
                if (!BodyTargetConeSense.Contains(origin: in self, forward: in forward, candidate: in position, range: fixedSource.Range, minimumDot: fixedSource.MinimumDot, distanceSquared: out var squared)
                    || (sensed.RequiresLineOfSight && !HasLineOfSight(from: self, fromOrientation: m_entries[selfIndex].Body!.FixedOrientation, to: position, toOrientation: body.FixedOrientation))) {
                    continue;
                }

                if (squared < candidate.DistanceSquared) {
                    candidate = new BodySensorTarget(Index: index, Position: position, DistanceSquared: squared);
                }
            }
        }

        var current = ((currentTarget >= 0) && (currentTarget < Capacity) && m_entries[currentTarget].Active && (m_entries[currentTarget].Body is { } held))
            ? new BodySensorTarget(Index: currentTarget, Position: held.FixedPosition, DistanceSquared: (held.FixedPosition - self).LengthSquared)
            : BodySensorTarget.None;

        return new BodyProducerSensors(Candidate: candidate, CurrentTarget: current);
    }

    private bool HasLineOfSight(in FixedVector3 from, in FixedQuaternion fromOrientation, in FixedVector3 to, in FixedQuaternion toOrientation) {
        var start = (from + fromOrientation.Rotate(vector: s_localSightOffset));
        var end = (to + toOrientation.Rotate(vector: s_localSightOffset));
        return m_targetField?.LineOfSight(from: start, to: end) ?? false;
    }

    // Activate a simulated entry: re-seed its canonical pose/color/wander from its index, then mint its own body from
    // its kit row (tuning + primary-action binding) spawned at that pose with the stored peer-source default. The
    // Warp/Face is a server-authoritative spawn (a one-time write into the sim); from here the pose flows only out.
    private void ActivateSimulated(int index, int? generation = null, IntentSource? source = null) {
        SeedSimulated(index: index);

        var entry = m_entries[index];
        var kit = m_kits[entry.KitIndex];
        // Profileless — advances on the kit row's tuning with the row's lane bindings.
        var player = new WorldBody(motion: m_kitRows[entry.KitIndex].Motion, program: kit.BodyMotionProgram, programs: m_bodyMotionPrograms, actions: kit.Actions, actionThresholds: kit.ActionThresholds, actionShapes: kit.ActionShapes, roleMask: kit.RoleMask, roleOrdinals: kit.RoleOrdinals, actionState: kit.ActionState, collider: kit.Collider, maxSmoothError: m_fixedMotion.MaxSmoothError, sprintChannelOrdinal: kit.SprintChannelOrdinal, driftChannelOrdinal: kit.DriftChannelOrdinal);

        player.SetContactField(field: m_contactField);
        player.SetWaterline(level: m_waterline);

        player.Pose(
            position: entry.SpawnPosition,
            yawRadians: entry.SpawnYaw,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );

        player.SetIntentSource(source: (source ?? m_defaultPeerSource));
        entry.Body = player;
        ClearDesignations(entry: entry);
        entry.Generation = (generation ?? checked(entry.Generation + 1));
    }

    /// <summary>Admits ONE remote-human peer body at the point of effect — the P7 socket door's own primitive,
    /// parallel to <see cref="ReconcileInhabitants"/>'s inhabited-body admission: the body claims the HIGHEST FREE
    /// slot (127 downward, via <see cref="HighestFreeSlot"/>) so it never renumbers an existing peer and never
    /// collides with the census's own upward allocation (<see cref="SetSimulatedCount"/> now skips any slot this
    /// method marks <see cref="Entry.IsRemoteHuman"/>). Refused by name on whichever bound fails: no free slot in the
    /// 128-body table, or the document's <c>networkPlayers</c> admission cap already met (census bots and admitted
    /// remote humans share that one cap — see <see cref="CountActiveCensus"/>).</summary>
    /// <param name="source">The intent source the body starts with (<see cref="IntentSource.Live"/> for a genuine
    /// remote human — a submitted intent/command fills its gaps, never a wander/attend producer).</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public bool TryAdmitRemotePeer(IntentSource source, out WorldPeerEventEntry admitted, out string refusal) {
        var slot = HighestFreeSlot();

        if (slot < 0) {
            admitted = default;
            refusal = $"the {Capacity}-slot entity table is full";

            return false;
        }

        if (CountActiveCensus() >= m_remoteCap) {
            admitted = default;
            refusal = $"the networkPlayers admission cap ({m_remoteCap}) is already met";

            return false;
        }

        ActivateSimulated(index: slot, source: source);

        var entry = m_entries[slot];

        entry.Active = true;
        entry.IsRemoteHuman = true;
        m_simulatedCount = CountActiveCensus();
        m_revision++;
        admitted = PeerEventEntry(index: slot);
        refusal = string.Empty;

        return true;
    }

    /// <summary>Re-applies one recorded admission through the population door. A live event reaches this after the
    /// point of effect and is idempotent (<see cref="TryAdmitRemotePeer"/> already set every field this touches);
    /// replay reaches it before the effect and reconstructs the body — including the <see cref="Entry.IsRemoteHuman"/>
    /// marker, inferred from <see cref="WorldPeerEventEntry.Source"/> being <see cref="IntentSource.Live"/> (the
    /// document-authored census/inhabitant defaults are never <see cref="IntentSource.Live"/>).</summary>
    /// <param name="peer">The recorded peer entry.</param>
    public void ApplyPeerAdmitted(in WorldPeerEventEntry peer) {
        if ((uint)(peer.BodyIndex - LocalSeatCount) >= PeerCapacity) {
            return;
        }

        var entry = m_entries[peer.BodyIndex];

        if (!entry.Active) {
            ActivateSimulated(index: peer.BodyIndex, generation: peer.Generation, source: peer.Source);
            entry.Active = true;
            entry.IsRemoteHuman = (peer.Source == IntentSource.Live);
            m_simulatedCount = CountActiveCensus();
            m_revision++;
        }
    }

    /// <summary>Re-applies one recorded disconnect through the population door. PARK-WITH-GRACE: on the SAME terms as
    /// <see cref="DeactivateSeat"/>, this defers the body/occupancy half of the teardown (<see cref="Entry.Body"/>,
    /// <see cref="Entry.Active"/>, <see cref="Entry.IsRemoteHuman"/>) to <see cref="ReclaimExpiredParks"/> when
    /// <c>population.reconnectGraceTicks</c> is positive — the entry marks <see cref="Entry.Parked"/> instead, and
    /// <see cref="IsAdmittedPeer"/> (hence <see cref="IsHumanOccupied"/>) keeps reading <see langword="true"/>
    /// through the grace window since <see cref="Entry.IsRemoteHuman"/> is untouched. The GRANT half of the teardown
    /// is deliberately NOT deferred here — the caller (<c>Server.WorldServer.ApplyServerEvent</c>) still revokes the
    /// disconnected generation's grants immediately, unchanged from the pre-park behavior; deferring THAT too would
    /// mean reshaping <c>WorldServerEvent.PeerDisconnected</c>'s ordered-domain/replay-tape contract, which this
    /// population-local change does not reach into. See the reconnect-primitives change notes for this as a named,
    /// deliberate scope line, not a silent gap.</summary>
    /// <param name="peer">The recorded peer entry.</param>
    /// <param name="tick">The current tick — the basis <see cref="Entry.ParkedUntilTick"/> is stamped from.</param>
    public void ApplyPeerDisconnected(in WorldPeerEventEntry peer, ulong tick) {
        if ((uint)(peer.BodyIndex - LocalSeatCount) >= PeerCapacity) {
            return;
        }

        var entry = m_entries[peer.BodyIndex];

        if (entry.Active && (entry.Generation == peer.Generation)) {
            if (m_reconnectGraceTicks <= 0) {
                entry.Body = null;
                entry.Active = false;
                entry.IsRemoteHuman = false;
                entry.Parked = false;
                entry.ParkedUntilTick = 0L;
            } else {
                entry.Parked = true;
                entry.ParkedUntilTick = unchecked((long)tick + m_reconnectGraceTicks);
            }

            m_simulatedCount = CountActiveCensus();
            m_revision++;
        }
    }

    /// <summary>Tears down every entry PARKED past its grace deadline — the deferred half of
    /// <see cref="DeactivateSeat"/>/<see cref="ApplyPeerDisconnected"/>'s teardown (see <see cref="Entry.Parked"/>'s
    /// own remarks): drops the body, clears <see cref="Entry.Active"/> and (for a peer) <see cref="Entry.IsRemoteHuman"/>,
    /// exactly as an immediate disconnect already did before park-with-grace existed. Covers BOTH local seats and
    /// peers in one pass — the same <c>Active &amp;&amp; Parked</c> gate discriminates a park regardless of
    /// <see cref="PopulationKind"/>, so there is no separate seat/peer sweep. A disconnected PEER generation's grants
    /// are revoked at disconnect time already (see <see cref="ApplyPeerDisconnected"/>'s own remarks on why that
    /// revocation is not deferred here); a local seat never held generation-scoped grants to revoke, so nothing
    /// grant-shaped happens here for either kind. Driven purely by <paramref name="tick"/> — no wall clock, no
    /// randomness — so it is exactly as replay-deterministic as <c>Server.WorldServer.ReclaimExpiredEscrows</c>,
    /// which this mirrors and is swept beside every tick.</summary>
    /// <param name="tick">The current (just-completed) simulation tick.</param>
    public void ReclaimExpiredParks(ulong tick) {
        var signedTick = unchecked((long)tick);
        var changed = false;

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (entry is { Active: true, Parked: true } && (signedTick >= entry.ParkedUntilTick)) {
                entry.Body = null;
                entry.Active = false;
                entry.Parked = false;
                entry.ParkedUntilTick = 0L;

                if (entry.IsRemoteHuman) {
                    entry.IsRemoteHuman = false;
                }

                changed = true;
            }
        }

        if (changed) {
            m_simulatedCount = CountActiveCensus();
            m_revision++;
        }
    }

    /// <summary>Whether <paramref name="index"/> currently holds a PARKED body (see <see cref="Entry.Parked"/>) —
    /// the general form of <see cref="IsSeatParked"/>, valid for a local seat OR a peer index alike. The read-back
    /// verb's own enumeration gate.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns><see langword="true"/> when the index holds a parked body.</returns>
    public bool IsParked(int index) => (((uint)index < Capacity) && m_entries[index] is { Active: true, Parked: true });

    /// <summary>Reads the reconnect-park reserved rule channel's live value for one body — the remaining grace ticks
    /// (<c>ParkedUntilTick - tick</c>, floored at zero) when the body is parked, <c>0</c> for an active, unparked, or
    /// out-of-range body alike. The <c>Server.WorldServer.ReadWorldFact</c> door for
    /// <c>WorldRuleFactKind.Parked</c> (<see cref="WorldRuleFacts.ParkedPrefix"/>) calls this directly, after its own
    /// body-reference resolution (a literal index or an argmax/argmin winner) — this method takes only the resolved
    /// index, mirroring every other reserved-channel reader's shape.</summary>
    /// <param name="index">The resolved 0-based entity index, or a negative sentinel for "no body".</param>
    /// <param name="tick">The current tick.</param>
    /// <returns>The remaining grace ticks when the body is parked; <c>0</c> for an active, unparked, or out-of-range body.</returns>
    public long ParkedRemainingTicks(int index, ulong tick) {
        if ((uint)index >= Capacity) {
            return 0L;
        }

        var entry = m_entries[index];

        if (!(entry is { Active: true, Parked: true })) {
            return 0L;
        }

        return Math.Max(val1: 0L, val2: (entry.ParkedUntilTick - unchecked((long)tick)));
    }

    private WorldPeerEventEntry PeerEventEntry(int index) {
        var entry = m_entries[index];
        var identity = WorldPrincipal.Peer(index: index, generation: entry.Generation);

        return new WorldPeerEventEntry(BodyIndex: index, Generation: entry.Generation, Source: (entry.Body?.Source ?? m_defaultPeerSource), Identity: identity);
    }

    private int CountActiveCensus() {
        var count = 0;

        for (var index = LocalSeatCount; index < m_inhabitantFloor; index++) {
            if (m_entries[index].Active && (m_entries[index].PlacementId is null)) {
                count++;
            }
        }

        return count;
    }

    // Seed a simulated entry's static per-index data from the authored distribution and independent sequences. Baked
    // for every entry at construction so the color is valid across all 128 from frame 1. A
    // live Rebuild re-derives the kit/wander-dependent statics with resetPhase: false, which keeps the running wander
    // phase/activity so the retune does not jerk the crowd.
    private void SeedSimulated(int index, bool resetPhase = true) {
        var offset = (index - LocalSeatCount);
        var producer = SeedProducer(kit: m_kits[m_entries[index].KitIndex]);
        var phase = WorldSequenceSampling.FixedAngle(sequence: m_peerVariation.Phase, index: index);
        var weaveUnit = WorldSequenceSampling.FixedScalar(sequence: m_peerVariation.Weave, index: index);
        var (activityUnit, altitudeUnit) = WorldSequenceSampling.FixedPair(sequence: m_peerVariation.Activity, index: index);
        var hue = WorldColor.SequenceHue(index: index, sequence: m_peerColors);
        var entry = m_entries[index];

        entry.ProducerState.PreferredAltitude = PreferredAltitudeFor(kit: m_kits[entry.KitIndex], producer: producer, altitudeUnit: altitudeUnit);
        if (m_distribution.Points is { Length: > 0 } points) {
            var basePoint = points[offset % points.Length];

            entry.ProducerState.PreferredAltitude = basePoint.Position.Y;
            entry.SpawnPosition = SpawnAtPoint(basePoint: basePoint.Position, halfExtent: m_distribution.Radius, fill: m_distribution.Fill, ordinal: offset);
            entry.SpawnYaw = basePoint.YawRadians;
        } else {
            var sampleCount = ((m_distribution.SampleCount > 0) ? m_distribution.SampleCount : Math.Max(val1: m_remoteCap, val2: 1));
            var fraction = (FixedQ4816.FromInteger(value: ((2L * offset) + 1L)) / FixedQ4816.FromInteger(value: (2L * sampleCount)));
            var spawnRadius = (m_distribution.Radius * FixedQ4816.Sqrt(value: fraction));
            var spawnAngle = WorldSequenceSampling.FixedAngle(sequence: m_distribution.Fill, index: offset);
            var (sin, cos) = FixedQ4816.SinCos(angle: spawnAngle);

            entry.SpawnPosition = new FixedVector3(X: (spawnRadius * cos), Y: entry.ProducerState.PreferredAltitude, Z: (spawnRadius * sin));
            entry.SpawnYaw = spawnAngle;
        }
        entry.BodyColor = WorldColor.HsvToRgb(h: hue, s: m_playerDefaults.Saturation, v: m_playerDefaults.Value);
        ApplyVariation(entry: entry, producer: producer, phase: phase, weaveUnit: weaveUnit, activityUnit: activityUnit, resetPhase: resetPhase);
    }

    // Seed a seat's wander-producer dynamics from its slot alone (no RNG) — the parameters player.control producer:<name>
    // steers by, parallel to the independently authored peer variation. A seat has no wander spawn/color seeding — the
    // definition spawns it and its profile colors it.
    private void SeedSeatWander(int slot, bool resetPhase = true) {
        var producer = SeedProducer(kit: m_kits[m_seatKit]);
        var phase = WorldSequenceSampling.FixedAngle(sequence: m_seatVariation.Phase, index: slot);
        var weaveUnit = WorldSequenceSampling.FixedScalar(sequence: m_seatVariation.Weave, index: slot);
        var (activityUnit, altitudeUnit) = WorldSequenceSampling.FixedPair(sequence: m_seatVariation.Activity, index: slot);
        var entry = m_entries[slot];

        entry.ProducerState.PreferredAltitude = PreferredAltitudeFor(kit: m_kits[m_seatKit], producer: producer, altitudeUnit: altitudeUnit);
        ApplyVariation(entry: entry, producer: producer, phase: phase, weaveUnit: weaveUnit, activityUnit: activityUnit, resetPhase: resetPhase);
    }

    private static void ApplyVariation(Entry entry, CompiledBodyProducer producer, FixedQ4816 phase, FixedQ4816 weaveUnit, FixedQ4816 activityUnit, bool resetPhase) {
        entry.ProducerState.WeaveFrequency = (producer.Scalar(name: "weaveFrequencyBase") + (producer.Scalar(name: "weaveFrequencyRange") * weaveUnit));

        if (resetPhase) {
            entry.ProducerState.AcquiredTarget = -1;
            entry.ProducerState.Phase = phase;
            entry.ProducerState.ActivityPhase = (phase + (s_twoPi * activityUnit));
            entry.ProducerState.ActivityRate = (producer.Scalar(name: "activityRateBase") + (producer.Scalar(name: "activityRateRange") * activityUnit));
        }
    }

    private static FixedSpawnPoint[] CompileSeatSpawns(IReadOnlyList<WorldSpawnPoint> spawnPoints, IReadOnlyList<string> seatSpawns) {
        var compiled = new FixedSpawnPoint[seatSpawns.Count];

        for (var index = 0; (index < compiled.Length); index++) {
            var point = WorldDefinitionRows.FindSpawnPoint(spawnPoints: spawnPoints, id: seatSpawns[index])!.Value;

            compiled[index] = FixedSpawnPoint.Compile(point: in point);
        }

        return compiled;
    }

    private static FixedVector3 SpawnAtPoint(FixedVector3 basePoint, FixedQ4816 halfExtent, WorldSequence fill, int ordinal) {
        var (jitterX, jitterZ) = WorldSequenceSampling.FixedPair(sequence: fill, index: ordinal);
        var scatterX = (halfExtent * ((jitterX * FixedQ4816.FromInteger(value: 2L)) - FixedQ4816.One));
        var scatterZ = (halfExtent * ((jitterZ * FixedQ4816.FromInteger(value: 2L)) - FixedQ4816.One));

        return new FixedVector3(X: (basePoint.X + scatterX), Y: basePoint.Y, Z: (basePoint.Z + scatterZ));
    }

    // The altitude a wander entity holds: a free kit's authored base plus its per-index range sample; a grounded kit
    // starts at the authored spawn point or the world origin and lets contact geometry settle it.
    private static CompiledBodyProducer SeedProducer(in FixedWorldKit kit) =>
        kit.Producers.Values.First(producer => producer.Program.Contains(operation: BodyMotionOp.ProduceWanderIntent));

    private FixedQ4816 PreferredAltitudeFor(in FixedWorldKit kit, CompiledBodyProducer producer, FixedQ4816 altitudeUnit) {
        return (kit.BodyMotionProgram.Contains(operation: BodyMotionOp.IntegrateLocalAttitude)
            ? (producer.Scalar(name: "altitudeBase") + (producer.Scalar(name: "altitudeRange") * altitudeUnit))
            : FixedQ4816.Zero);
    }

    // One entity-table entry. A mutable class; Kind and KitIndex are fixed at construction. SpawnYaw is the
    // index-seeded heading a fresh activation faces the new body toward. Body is the entry's own sim — null while
    // inactive, minted on activation (a session join for a seat, the census or an inhabitant join for a peer).
    private sealed class Entry {
        public bool Active { get; set; }
        public WorldBody? Body { get; set; }
        public Vector3 BodyColor { get; set; }
        // Kind is fixed at construction (LocalSeat for slots 0..3, NetworkPeer for 4..127) and never changes: an
        // inhabitant is a NetworkPeer distinguished by its PlacementId, not a kind flip.
        public required PopulationKind Kind { get; init; }
        // Bumped every time this peer slot transitions inactive -> active. Never reset on disconnect.
        public int Generation { get; set; }
        // The placement row this peer inhabits (null for a plain census peer or an empty slot) — the back-reference the
        // frame source and anchor resolver look up by, and the flag that marks a peer as an inhabitant. Set/cleared by
        // ReconcileInhabitants.
        public string? PlacementId { get; set; }
        // Whether this slot is bound to a REMOTE-ADMITTED human connection (Server.WorldTcpHost's Hello door), as
        // opposed to a locally-simulated census stand-in. Set by TryAdmitRemotePeer/ApplyPeerAdmitted, cleared by
        // ApplyPeerDisconnected — SetSimulatedCount skips a slot carrying it exactly like an inhabited one, so a
        // world.population edit can never silently reassign or deactivate a connected human's body.
        public bool IsRemoteHuman { get; set; }
        // PARK-WITH-GRACE: set by DeactivateSeat/ApplyPeerDisconnected instead of the immediate teardown those
        // methods used to perform unconditionally. While Parked, Active (and, for a peer, IsRemoteHuman) STAY true
        // and Body stays retained (pose/state intact, still in the sim/collider set) — a disconnected body reads
        // IsHumanOccupied exactly as an occupied one does (the owner's occupancy ruling: parked stays targetable,
        // CC continues offline). Cleared by TryResumeParkedSeat on a matching re-Join, or by ReclaimExpiredParks
        // once ParkedUntilTick passes with no reconnect — see both their own remarks.
        public bool Parked { get; set; }
        // The tick AT OR AFTER which ReclaimExpiredParks tears this entry down — the SAME "DeadlineTick" shape
        // OwnershipEscrow already uses for its own tick-driven sweep. Meaningless while !Parked.
        public long ParkedUntilTick { get; set; }
        public BodyProducerState ProducerState;
        public required int[] Designations { get; set; }
        public string DesignationRefusal { get; set; } = string.Empty;
        // The most recent player.motion refusal reason (empty on the last switch's success) — the synchronous
        // submitter's read-back so an honest immediate echo never has to guess the outcome.
        public string MotionRefusal { get; set; } = string.Empty;
        // The most recent player.stop refusal reason (empty on success) and outcome (released/cleared counts) —
        // ALWAYS written together (see NoteStopOutcome/NoteStopRefusal) so the pair can never desync into a
        // refusal note pointing at stale success counts or vice versa.
        public string StopRefusal { get; set; } = string.Empty;
        public StopOutcome StopOutcome { get; set; }
        // The most recent player.press refusal reason (empty on success — timed or untimed alike, they share this
        // one slot) and the timed path's outcome (effective hold + which cap decided it) — ALWAYS written together,
        // the same pairing discipline as StopRefusal/StopOutcome above.
        public string PressRefusal { get; set; } = string.Empty;
        public PressOutcome PressOutcome { get; set; }
        // Reassigned in place by Rebuild when the kit-assignment policy (or kit set) mutates; set at construction.
        public required byte KitIndex { get; set; }
        // The resolved LOOK row index (PRESENTATION-ONLY; carried out on the snapshot). Reassigned by ResolveLookIndices
        // on construction and on every Rebuild.
        public byte LookIndex { get; set; }
        public FixedVector3 SpawnPosition { get; set; }
        public FixedQ4816 SpawnYaw { get; set; }
    }
}

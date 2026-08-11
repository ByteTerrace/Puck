using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>What a <see cref="WorldPopulation"/> entry stands for — the local seats driven by client-submitted intents,
/// and the peer slice that hosts every other joined body: remote-human peers and the loopback-joined inhabitants alike.
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

    /// <summary>The number of entity-table slots currently eligible for destination-authored census bodies. Inhabitants,
    /// connected humans, and authority-transferred entities are excluded wherever they sit; mapped handoff makes a
    /// low-index exclusion ordinary, so no packing-order or contiguous-floor assumption is valid.</summary>
    public int MaxSimulated => AvailableCensusSlots();

    /// <summary>The largest census <see cref="SetSimulatedCount"/> will actually grant right now — the tighter of the
    /// remaining remote admission budget (<c>networkPlayers</c>) and eligible slots (<see cref="MaxSimulated"/>). A request
    /// above it is clamped to it, so the <c>world.population</c> echo names both the granted count and this ceiling
    /// rather than letting a script read a success for a crowd it never got.</summary>
    public int SimulatedCeiling => Math.Min(val1: Math.Max(val1: 0, val2: (m_remoteCap - CountExternalNetworkPlayers())), val2: MaxSimulated);

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
    // m_contactField is the EFFECTIVE field a body resolves against — m_baseContactField wrapped by
    // WorldAdjacencyContactField when an adjacency resolver is configured AND the live definition authors an edge
    // band, or m_baseContactField unwrapped otherwise. Composed by
    // ComposeContactField, the ONE place either input changes.
    private IContactField? m_baseContactField;
    private IContactField? m_contactField;
    private WorldDefinition? m_adjacencyDefinition;
    private IWorldAdjacencySource? m_adjacencies;
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
    // The compiled population.reconnectGraceSeconds (see WorldDefinition.PopulationReconnectGraceTicks' own
    // remarks) — how long a disconnected body stays PARKED before ReclaimExpiredParks tears it down, or NEVER (a
    // positive authored grace compiled against simulation.rateHz 0 has no tick mapping — see CompiledTickDuration).
    // Refreshed by CompileFixedTables on a swap/rebuild, on the SAME "boot-time constant, live for future
    // disconnects only" terms the rest of this section already reads under.
    private CompiledTickDuration m_reconnectGraceTicks;
    // The remote-principal admission cap (the document's networkPlayers): the most census/remote peers world.population
    // may raise. It is a CEILING, never a boot reservation — at boot the census stands at zero (only the joined seats are
    // live) so the peer slice is entirely free for inhabitants. Refreshed by CompileFixedTables on a swap/rebuild.
    private int m_remoteCap;
    // The lowest slot index a live inhabited body occupies (Capacity = none). Inhabited bodies claim the top of the
    // entity table (slots 127 downward); the census ceiling reads this floor so census peers never reach an inhabitant.
    // Reconciled by ReconcileInhabitants.
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
    /// stands at zero at boot — <c>networkPlayers</c> is the remote admission cap, not a static reservation, so the whole
    /// peer slice is free for inhabitants and later <c>world.population</c> raises. The color must be valid for all 128
    /// from frame 1, since the program's material capacity is probed from a worst-case all-avatars build. An entry
    /// receives its <see cref="WorldBody"/> when activated.</summary>
    /// <param name="definition">The world definition supplying the kit rows, producer parameters, and the profileless
    /// locomotion feel.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public WorldPopulation(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        m_entries = new Entry[definition.Population.Capacity];

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
                CatalogRig = checked((byte)index),
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
        m_reconnectGraceTicks = definition.PopulationReconnectGraceTicks;
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
        m_baseContactField = ResolveContactField(definition: definition, solids: derivedSolids);
        m_adjacencyDefinition = definition;
        ComposeContactField();
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

    // Recomposes m_contactField from the current base field, adjacency source, and live definition — the
    // ONE place any of the three changes. A definition authoring no adjacency, or no injected source, leaves
    // m_contactField pointing at m_baseContactField directly.
    private void ComposeContactField() {
        if ((m_baseContactField is not { } baseField) || (m_adjacencyDefinition is not { } definition)) {
            m_contactField = m_baseContactField;

            return;
        }

        var bands = ((m_adjacencies is not null) ? WorldAdjacencyBands.CollectFrom(definition: definition) : []);

        m_contactField = ((m_adjacencies is { } source) && (bands.Count > 0))
            ? new WorldAdjacencyContactField(definition: definition, inner: baseField, bands: bands, source: source)
            : baseField;
    }

    /// <summary>Configures (or clears) the adjacency source every live body's contact resolution consults inside an
    /// overlap — see
    /// <see cref="WorldServer.Adjacencies"/>, the one writer. Recomposes <see cref="m_contactField"/> immediately
    /// against the current definition/base field, without rebuilding either — a border resolver becoming reachable
    /// (or unreachable) never itself re-derives the world's own solid geometry.</summary>
    /// <param name="source">The resolver, or <see langword="null"/> to fall back to this world's own geometry alone.</param>
    public void ConfigureAdjacencies(IWorldAdjacencySource? source) {
        if (ReferenceEquals(objA: m_adjacencies, objB: source)) {
            return;
        }

        m_adjacencies = source;
        ComposeContactField();

        // The composition root configures the runtime adjacency source after the boot seats already exist. Bodies retain
        // their own field reference, so recomposing only the population's field would leave those live bodies on the
        // old base field forever. Hand the effective field to every live body on the same terms as Rebuild; pose,
        // velocity, intent, and every other body property remain untouched.
        for (var index = 0; (index < Capacity); index++) {
            if (m_entries[index] is { Active: true, Body: { } body }) {
                body.SetContactField(field: m_contactField);
            }
        }
    }

    /// <summary>Gets the currently configured adjacency resolver — see <see cref="ConfigureAdjacencies"/>, the
    /// one writer.</summary>
    public IWorldAdjacencySource? Adjacencies => m_adjacencies;

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
    /// re-resolves every entry's kit index, re-derives the kit/wander-dependent per-entry statics without resetting the
    /// running wander phase, and swaps every live body's compiled tuning/actions/program in place — bodies keep their
    /// pose/velocity/tape, only the compiled feel swaps. Bumps <see cref="Revision"/> so the client rebuilds the avatar
    /// program. New activations re-seed fully from these fresh tables.</summary>
    /// <param name="definition">The new live definition.</param>
    /// <param name="solids">The server's pre-built SDF contact field for the field provider (built once at apply time so
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
    /// Install after <see cref="Rebuild(WorldDefinition, WorldSolidField?)"/>): a placement's inhabit facet joins bodies
    /// into the peer slice over the loopback link — an inhabitant is a <see cref="PopulationKind.NetworkPeer"/> whose entry
    /// carries a placement back-reference, holding a normal <see cref="WorldBody"/> under the resolved kit and driven by
    /// its kit's attend producer. Bodies claim the highest free slots (127 downward) so an existing inhabitant never
    /// renumbers; admission is bounded only by the table itself and rejects loudly when it is genuinely full — there is no
    /// census-fit reservation. Diff-by-placement: retire an entry whose row vanished, lost its facet, or changed
    /// creation/kit; keep a matching one (its pose survives an unrelated placement edit); admit new bodies at the highest
    /// free slots. The census ceiling (<see cref="MaxSimulated"/>) follows all non-census physical occupancy, and the
    /// census is re-clamped without renumbering an inhabitant or transferred entity.</summary>
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

        // Re-clamp the census against every entity-table slot now owned by an inhabitant or transferred authority.
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
        entry.CatalogRig = checked((byte)index);
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

    /// <summary>Returns a value indicating whether solid world geometry leaves the sight-offset segment between two live bodies unobstructed —
    /// the general body-to-body spatial primitive a world rule's <c>$los:</c> operand rides, reusing the same
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

    /// <summary>The boot-built SDF contact field when the definition selects the field provider, else
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
    /// resolves against, exposed so a caller (the <c>player.motion</c> switch door) can validate coherence before
    /// asking a body to switch.</summary>
    /// <param name="name">The declared program name.</param>
    /// <param name="program">The compiled program, or <see langword="null"/> when <paramref name="name"/> is undeclared.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names a declared program.</returns>
    public bool TryGetBodyMotionProgram(string name, out CompiledBodyMotionProgram? program) => m_bodyMotionPrograms.TryGetValue(key: name, value: out program);

    /// <summary>The resolved look row index for a stable population slot — carried out on the snapshot for the client's
    /// renderer (presentation-only).</summary>
    /// <param name="index">The 0-based population index.</param>
    public byte LookIndex(int index) => m_entries[index].LookIndex;

    /// <summary>The entity-owned procedural appearance rig. Unlike a look row, this follows the occupant when
    /// authority transfer assigns it a different population slot.</summary>
    public byte CatalogRig(int index) => m_entries[index].CatalogRig;

    /// <summary>Restores a transferred occupant's procedural appearance identity.</summary>
    public void SetCatalogRig(int slot, byte catalogRig) {
        if ((uint)slot < Capacity) {
            m_entries[slot].CatalogRig = catalogRig;
        }
    }

    /// <summary>The live look rows (the authored rows, or the implicit single catalog look) the census resolves against.</summary>
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

    /// <summary>Counts the active entities per look row for the <c>world.looks</c> census (one slot per look row,
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

    /// <summary>Returns a value indicating whether the entry at <paramref name="index"/> is active (drawn this frame).</summary>
    /// <param name="index">The population index (0-based, <c>0..</c><see cref="Capacity"/>).</param>
    /// <returns><see langword="true"/> when the entry is active.</returns>
    public bool IsActive(int index) => m_entries[index].Active;

    /// <summary>The count of active entries this tick — a read-only aggregate over <see cref="IsActive"/>, computed
    /// on demand (never cached) since a world rule's <c>"$population"</c> reserved channel reads it at most once per
    /// tick. Each <c>WorldServer</c> — the boot instance's and every spawned <c>Puck.World.WorldInstance</c>'s alike —
    /// owns its own <see cref="WorldPopulation"/>, so this is already per-instance scoped under multi-world: reading it
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
    /// drains inline) so its immediate echo reports the true outcome instead of assuming success.</summary>
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

    /// <summary>Records the outcome of a successful <c>player.stop</c> for a body — the same synchronous-submit
    /// read-back shape as <see cref="NoteMotionRefusal"/>, so <c>player.stop</c>'s handler can quote the true
    /// released/cleared counts instead of a fixed template string. Always clears any refusal note the body's stop
    /// slot carried, so a denial from an earlier attempt can never bleed into a fresh success's echo.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="outcome">The counts <see cref="WorldBody.Stop"/> computed.</param>
    public void NoteStopOutcome(int bodyIndex, StopOutcome outcome) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].StopRefusal = string.Empty;
            m_entries[bodyIndex].StopOutcome = outcome;
        }
    }

    /// <summary>Records a refused <c>player.stop</c> attempt for a body — <see cref="WorldServer.ApplyCommand"/>
    /// calls this from every early return a <see cref="WorldCommand.Stop"/> can take (the grant-table denial, the
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
    /// attempt succeeded (or none has been made). <c>player.stop</c>'s handler checks this before
    /// <see cref="LastStopOutcome"/> — a non-empty refusal means the counts were never applied.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public string StopRefusal(int bodyIndex) => (((uint)bodyIndex < (uint)m_entries.Length) ? m_entries[bodyIndex].StopRefusal : string.Empty);

    /// <summary>The most recent <c>player.stop</c> outcome for a body, or a zeroed outcome when none has been made
    /// (or the last attempt was refused — see <see cref="StopRefusal"/>).</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public StopOutcome LastStopOutcome(int bodyIndex) => (((uint)bodyIndex < (uint)m_entries.Length) ? m_entries[bodyIndex].StopOutcome : default);

    /// <summary>Records the outcome of a successful timed <c>player.press</c> — the effective hold (post
    /// grant-ceiling and engine-backstop clamping) and which cap, if any, decided it — the same synchronous-submit
    /// read-back shape as <see cref="NoteMotionRefusal"/>, so the handler can name a silent truncation instead of
    /// echoing the requested duration as if it were honored. Always clears any refusal note the body's press slot
    /// carried.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <param name="outcome">The outcome <see cref="WorldBody.PressChannel(int, FixedQ4816, float, FixedQ4816)"/> returned.</param>
    public void NotePressOutcome(int bodyIndex, PressOutcome outcome) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].PressRefusal = string.Empty;
            m_entries[bodyIndex].PressOutcome = outcome;
        }
    }

    /// <summary>Records a successful untimed <c>player.press</c> (the host-step tap, which carries no numeric
    /// outcome of its own) — clears any refusal note the body's press slot carried, the same way
    /// <see cref="NotePressOutcome"/> does for the timed path, so the one shared refusal slot both press paths read
    /// back through is always fresh regardless of which one last ran.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public void NotePressSuccess(int bodyIndex) {
        if ((uint)bodyIndex < (uint)m_entries.Length) {
            m_entries[bodyIndex].PressRefusal = string.Empty;
        }
    }

    /// <summary>Records a refused <c>player.press</c> attempt (timed or untimed alike — they share one refusal
    /// slot) for a body — <see cref="WorldServer.ApplyCommand"/> calls this from every early return a
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
    /// attempt succeeded (or none has been made). <c>player.press</c>'s handler checks this before
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

    /// <summary>Returns a value indicating whether <paramref name="bodyIndex"/> is human-occupied — the co-driving
    /// fold's occupancy discriminator (and the bot-overwrite door in <c>WorldServer.ApplyIntentSubmission</c>): a
    /// body is human-occupied iff a local seat slot is <see cref="IsActive"/> and bound to it, or the body is bound
    /// to an <see cref="IsAdmittedPeer"/> — never <see cref="WorldBody.Source"/> (what fills gaps; its
    /// <see cref="IntentSource.Live"/> value also covers a remote peer) and never engagement (an orthogonal axis).
    /// The pool this gates exists only when this returns <see langword="true"/>: an unoccupied body is a bot at full
    /// authority by construction, not by an undefined ceiling.
    /// <para><b>A parked body (see <see cref="Entry.Parked"/>) still reads <see langword="true"/> here</b> —
    /// <see cref="IsActive"/>/<see cref="IsAdmittedPeer"/> are exactly what a park leaves untouched, by construction,
    /// so no separate parked-aware branch exists in this method. A disconnected-but-parked body stays targetable and
    /// its CC pool keeps running offline through the grace window; only <see cref="ReclaimExpiredParks"/>'s eventual
    /// teardown removes it from the pool.</para></summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    /// <returns><see langword="true"/> when the index is bound to a live local seat or an admitted peer.</returns>
    public bool IsHumanOccupied(int bodyIndex) =>
        ((((uint)bodyIndex < LocalSeatCount) && IsActive(index: bodyIndex)) || IsAdmittedPeer(bodyIndex: bodyIndex));

    /// <summary>Returns a value indicating whether <paramref name="bodyIndex"/> is bound to a remote-admitted human.
    /// Live for a body a <see cref="TryAdmitRemotePeer"/> call is still holding (see
    /// <see cref="Entry.IsRemoteHuman"/>); a socket door's disconnect clears it through
    /// <see cref="ApplyPeerDisconnected"/> exactly as admission set it.</summary>
    /// <param name="bodyIndex">The 0-based entity index.</param>
    public bool IsAdmittedPeer(int bodyIndex) => (((uint)bodyIndex < Capacity) && m_entries[bodyIndex].IsRemoteHuman);

    /// <summary>Gets the current generation-bearing peer identity for a peer slot.</summary>
    /// <param name="index">The peer body index.</param>
    /// <returns>The current peer principal.</returns>
    public WorldPrincipal PeerPrincipal(int index) => WorldPrincipal.Peer(index: index, generation: m_entries[index].Generation);

    /// <summary>Returns the <see cref="WorldBody"/> an entry owns while active, or <see langword="null"/> for an inactive
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

    /// <summary>Sets the exact rendered material color retained by an authority-transferred body.</summary>
    public void SetBodyColor(int slot, Vector3 color) {
        if ((uint)slot < (uint)Capacity) {
            m_entries[slot].BodyColor = color;
        }
    }

    /// <summary>The entry's activation generation. Combined with authority and slot, this prevents stale entity
    /// addresses from aliasing a later occupant.</summary>
    public int Generation(int index) => m_entries[index].Generation;

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
        entry.CatalogRig = checked((byte)slot);
        entry.Generation = checked(entry.Generation + 1);
        entry.Active = true;
        m_revision++;
    }

    /// <summary>Deactivates a local seat — the session leave's server half. A no-op if the seat is not active.
    /// Park-with-grace: when the compiled grace (<see cref="m_reconnectGraceTicks"/>) is positive or
    /// <see cref="CompiledTickDuration.IsNever"/>, this does not drop the body — it marks the entry
    /// <see cref="Entry.Parked"/> and, for a finite grace, stamps <see cref="Entry.ParkedUntilTick"/> (left
    /// <see langword="null"/> for never — a rate-0 world has no tick to stamp a deadline at, so the body parks
    /// forever instead of tearing down), keeping the body (pose, durable state) in the sim/collider set and
    /// <see cref="IsHumanOccupied"/> reading <see langword="true"/> exactly as before the leave. The full teardown
    /// this method used to perform unconditionally now fires from <see cref="ReclaimExpiredParks"/> once a finite
    /// grace window passes with no matching re-Join (see <see cref="TryResumeParkedSeat"/>) — never, for never. An
    /// authored-disabled grace (<see cref="CompiledTickDuration.IsZero"/>, distinct from never: a positive authored
    /// grace at rate 0 is never, an authored zero is disabled at any rate) keeps the immediate-teardown behavior
    /// exactly as authored (the grace window is opt-in, not a forced behavior change for a world that authors none).
    /// Bumps the revision either way.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="tick">The current tick — the basis a finite <see cref="Entry.ParkedUntilTick"/> is stamped from
    /// (<c>tick + reconnectGraceTicks</c>).</param>
    public void DeactivateSeat(int slot, ulong tick) {
        var entry = m_entries[slot];

        if (!entry.Active) {
            return;
        }

        if (m_reconnectGraceTicks.IsNever) {
            entry.Parked = true;
            entry.ParkedUntilTick = null;
            m_revision++;

            return;
        }

        if (m_reconnectGraceTicks.IsZero) {
            entry.Body = null;
            entry.Active = false;
            entry.Parked = false;
            entry.ParkedUntilTick = null;
            m_revision++;

            return;
        }

        entry.Parked = true;
        entry.ParkedUntilTick = unchecked((long)tick + m_reconnectGraceTicks.Ticks);
        m_revision++;
    }

    /// <summary>Detaches an authoritative body's embodiment for an atomic transfer to another world authority —
    /// the leave half of atomic body transfer (the composition root's per-host pending-transfer drain). Unlike
    /// <see cref="DeactivateSeat"/>, this never parks and never consults <c>reconnectGraceTicks</c>: it unconditionally
    /// clears <see cref="Entry.Body"/> and <see cref="Entry.Active"/> so the body stops being advanced (or counted
    /// active) in this instance from the moment it returns — a park would leave <see cref="Entry.Active"/> true and
    /// <see cref="AdvanceSeats"/> would keep integrating it here, which is exactly the double-embodiment a transfer
    /// must not allow once the same identity is about to be re-activated in another instance's population. Only the
    /// seat binding (the caller already holds the slot) and the body's own <see cref="WorldBody.Profile"/> survive
    /// this call — pose, velocity, action-track state, and tape are discarded here by design (the destination world
    /// re-embodies the identity through its own normal join/kit-assignment; none of that state is meaningful under a
    /// different kit). A caller preparing for a possible abort reads <see cref="WorldBody.CaptureTransferState"/>
    /// (and the body's own pose) off the still-active body before calling this — this method itself does not do so,
    /// since a committed transfer never needs it and this stays the single unconditional "leave" primitive either way
    /// (see <see cref="RestoreDetachedSeat"/> for where a captured state re-enters). This method also clears
    /// <see cref="Entry.Designations"/> (via <see cref="ClearDesignations"/>) unconditionally, before the caller
    /// knows whether the transfer will abort — an abort-preparing caller that wants designations to survive an abort
    /// must read <see cref="CaptureDesignations"/> before calling this, exactly like it already does for
    /// <see cref="WorldBody.CaptureTransferState"/>. <see cref="Entry.Designations"/> and
    /// <see cref="Entry.ProducerState"/> live on this class's own <see cref="Entry"/>, entirely outside
    /// <see cref="WorldBody"/>'s own reach, which is why they are addressed here rather than in
    /// <see cref="WorldBody.TransferState"/>. A no-op returning <see langword="false"/> when the seat holds no active
    /// body — nothing captured, nothing changed.</summary>
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
        entry.ParkedUntilTick = null;
        ClearDesignations(entry: entry);
        entry.PlacementId = null;
        entry.IsAuthorityTransferred = false;
        if (entry.IsRemoteHuman) {
            entry.IsRemoteHuman = false;
            entry.AdmissionInstalledGrantTemplates = [];
            entry.AdmissionRevokedKeys.Clear();
            entry.IdentityDomain = string.Empty;
            entry.IdentitySubject = string.Empty;
            m_simulatedCount = CountActiveCensus();
        }
        m_revision++;

        return true;
    }

    /// <summary>Reads a live seat's own <see cref="Entry.Designations"/> register — a defensive copy, safe to hold
    /// past the register's own future mutation. The one moment an abort-preparing caller can read it, mirroring
    /// <see cref="WorldBody.CaptureTransferState"/>'s own "read live, right now, never cached" contract: call this
    /// before <see cref="TryDetachSeatForTransfer"/>, which clears the live register unconditionally regardless of
    /// whether the transfer that follows ever aborts (see that method's own remarks) — pass the result to
    /// <see cref="RestoreDetachedSeat"/> on an abort so the seat's designations survive the round trip.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <returns>A defensive copy of the slot's current designation register, or an empty array for an out-of-range
    /// slot.</returns>
    public int[] CaptureDesignations(int slot) => (((uint)slot < m_entries.Length) ? [.. m_entries[slot].Designations] : []);

    /// <summary>Restores a body <see cref="TryDetachSeatForTransfer"/> just detached back onto its original seat at
    /// the exact pose it held at detach — the abort half of a same-process transfer's atomic move. Unlike
    /// <see cref="ActivateSeat"/>'s fresh-spawn path, the body is posed at <paramref name="position"/>/<paramref name="yawRadians"/>
    /// instead of the seat's authored spawn point, so a transfer that must abort after this seat already departed
    /// restores play exactly where it left off rather than teleporting it home. The seat kit every local seat
    /// constructs today authors no <c>vehicle</c>/<c>swim</c> model, so <see cref="WorldBody.FixedOrientation"/> is
    /// always a pure yaw rotation (pitch = roll = 0) for a seat body — capturing position and yaw alone therefore
    /// reconstructs the departed body's orientation bit-for-bit, the identical construction <see cref="ActivateSeat"/>'s
    /// own spawn already relies on. A seat kit that someday adopts a genuine free/vehicle attitude for a local seat
    /// would need this method (or a sibling) to accept the full orientation instead.
    /// <para><b>Dynamic state.</b>
    /// <paramref name="dynamicState"/> carries the perceivable subset <see cref="WorldBody.CaptureTransferState"/>
    /// read off the departed body before <see cref="TryDetachSeatForTransfer"/> discarded it — velocity, a live dash
    /// overlay, and in-flight timed-press state (see that struct's own remarks for exactly what and why). It is
    /// applied via <see cref="WorldBody.ApplyTransferState"/> after <see cref="WorldBody.Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>
    /// below — the abort-refire invariant's own ordering: <c>Pose</c> is the same hard-teleport commit
    /// <see cref="WorldBody.Reconcile"/> and every other discontinuity in this engine routes through
    /// (<see cref="WorldBody.FixedPreviousPosition"/> collapses to the landing point, so the restored body's own
    /// swept portal-crossing segment starts exactly here rather than ghosting back through the volume it just left —
    /// this is what stops an aborted transfer's stale pre-detach origin from re-firing the door it was just refused
    /// by), and velocity/overlay/timer state is only meaningful once that discontinuity has already run.</para>
    /// <para><b>Park stays derived.</b> This method never writes <see cref="Entry.Parked"/>
    /// or <see cref="Entry.ParkedUntilTick"/> — <see cref="TryDetachSeatForTransfer"/> already cleared both at detach
    /// time and nothing here reinstates them from <paramref name="dynamicState"/> or any other capture, because park
    /// is a live-compiled-grace fact the next <see cref="DeactivateSeat"/> re-derives, never a snapshot to replay.</para>
    /// A no-op returning <see langword="false"/> when the slot is already active — nothing to restore onto; the
    /// caller's own bookkeeping (never restoring the same detach twice) is what keeps this from firing over a live
    /// occupant.</summary>
    /// <param name="slot">The seat index (0-based) — the same slot the detach came from.</param>
    /// <param name="profile">The detached body's own retained identity, exactly as <see cref="TryDetachSeatForTransfer"/>
    /// returned it.</param>
    /// <param name="position">The captured pre-detach position.</param>
    /// <param name="yawRadians">The captured pre-detach yaw.</param>
    /// <param name="dynamicState">The captured pre-detach dynamic state (velocity, overlay, action-track) — see
    /// <see cref="WorldBody.TransferState"/>.</param>
    /// <param name="designations">The seat's own pre-detach <see cref="Entry.Designations"/> register, from
    /// <see cref="CaptureDesignations"/>, or <see langword="null"/> to leave the register at its cleared default (a
    /// non-abort restore caller has nothing to pass — every actual caller today is abort-only, so this defaults to
    /// <see langword="null"/> only for a hypothetical future caller, never today's).</param>
    /// <returns><see langword="true"/> when the seat was restored.</returns>
    public bool RestoreDetachedSeat(int slot, WorldIdentity? profile, FixedVector3 position, FixedQ4816 yawRadians, WorldBody.TransferState dynamicState, IReadOnlyList<int>? designations = null) {
        var entry = m_entries[slot];

        if (entry.Active) {
            return false;
        }

        var body = new WorldBody(motion: m_kitRows[m_seatKit].Motion, program: m_kits[m_seatKit].BodyMotionProgram, programs: m_bodyMotionPrograms, actions: m_kits[m_seatKit].Actions, actionThresholds: m_kits[m_seatKit].ActionThresholds, actionShapes: m_kits[m_seatKit].ActionShapes, roleMask: m_kits[m_seatKit].RoleMask, roleOrdinals: m_kits[m_seatKit].RoleOrdinals, actionState: m_kits[m_seatKit].ActionState, collider: m_kits[m_seatKit].Collider, maxSmoothError: m_fixedMotion.MaxSmoothError, sprintChannelOrdinal: m_kits[m_seatKit].SprintChannelOrdinal, driftChannelOrdinal: m_kits[m_seatKit].DriftChannelOrdinal) {
            Profile = profile,
        };

        body.SetContactField(field: m_contactField);
        body.SetWaterline(level: m_waterline);
        body.Pose(
            position: position,
            yawRadians: yawRadians,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        // AFTER Pose's own CommitTeleport — see this method's own "Dynamic state" remarks above.
        body.ApplyTransferState(state: dynamicState);
        ClearDesignations(entry: entry);

        // Reapply the CAPTURED pre-detach register on top of the defensive clear above — the same
        // "restore on top of the reset" ordering ApplyTransferState's own fields already follow.
        // Absent (null) means the caller captured nothing to restore (never today's abort-only caller, which always
        // reads CaptureDesignations before detaching) — leaves the cleared default alone rather than throwing.
        if (designations is not null) {
            var count = Math.Min(val1: designations.Count, val2: entry.Designations.Length);

            for (var index = 0; (index < count); index++) {
                entry.Designations[index] = designations[index];
            }
        }

        // resetPhase:false: entry.ProducerState is NEVER cleared by TryDetachSeatForTransfer (it only clears
        // Body/Active/Parked/Designations — see that method's own remarks), so the pre-detach wander
        // phase/activity/acquired-target are still sitting right here, untouched, the moment this runs — reseeding
        // them would needlessly discard state that was never actually lost, only about to be overwritten.
        // WeaveFrequency/PreferredAltitude are still recomputed either way (a pure function of slot+kit,
        // safe/idempotent to redo), matching SeedSeatWander's other resetPhase:false caller (the ApplyPeerAdmitted-adjacent path).
        SeedSeatWander(slot: slot, resetPhase: false);
        entry.Body = body;
        entry.BodyColor = (profile?.Color ?? Vector3.Zero);
        entry.Active = true;
        m_revision++;

        return true;
    }

    /// <summary>Captures the generation-bearing admission row for a live transferred peer before detachment.</summary>
    public bool TryCaptureTransferredPeer(int index, out WorldPeerEventEntry peer) {
        if (((uint)(index - LocalSeatCount) < PeerCapacity) && m_entries[index].Active && m_entries[index].IsRemoteHuman) {
            peer = PeerEventEntry(index: index);
            return true;
        }

        peer = default;
        return false;
    }

    /// <summary>Captures the generation-bearing row for any active entity-table peer before authority transfer.
    /// Unlike <see cref="TryCaptureTransferredPeer"/>, this includes autonomous census and inhabitant bodies.</summary>
    public bool TryCaptureTransferredEntity(int index, out WorldPeerEventEntry peer) {
        if (((uint)(index - LocalSeatCount) < PeerCapacity) && m_entries[index].Active) {
            peer = PeerEventEntry(index: index);
            return true;
        }

        peer = default;
        return false;
    }

    /// <summary>Restores a just-detached federated peer after an aborted transfer, preserving its generation,
    /// admission facts, pose, dynamic state, and designation registers.</summary>
    public bool RestoreDetachedPeer(in WorldPeerEventEntry peer, IReadOnlyList<WorldAdmissionGrant> grantTemplates, WorldIdentity? profile, FixedVector3 position, FixedQ4816 yawRadians, WorldBody.TransferState dynamicState, IReadOnlyList<int>? designations = null) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);
        if (((uint)(peer.BodyIndex - LocalSeatCount) >= PeerCapacity) || m_entries[peer.BodyIndex].Active) {
            return false;
        }

        ApplyPeerAdmitted(peer: in peer, grantTemplates: grantTemplates);
        var entry = m_entries[peer.BodyIndex];
        if (entry.Body is not { } body) {
            return false;
        }

        body.Profile = profile;
        entry.BodyColor = (profile?.Color ?? Vector3.Zero);
        body.Pose(position: position, yawRadians: yawRadians, pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);
        body.ApplyTransferState(state: dynamicState);
        ClearDesignations(entry: entry);
        if (designations is not null) {
            var count = Math.Min(val1: designations.Count, val2: entry.Designations.Length);
            for (var index = 0; index < count; index++) {
                entry.Designations[index] = designations[index];
            }
        }

        return true;
    }

    /// <summary>Overrides an already-active seat's own pose and velocity — the mapped-arrival half of a portal
    /// transfer (see <c>Puck.World.WorldPlacementPortal.Arrival</c>): called by <c>Puck.World.WorldInstanceHost</c>
    /// after the destination's own ordinary <see cref="ActivateSeat"/> join already embodied the traveler fresh
    /// under its own kit (appearance, grants, action-track state) — this call carries across only the
    /// positional-continuity facts <c>Puck.World.Server.WorldPortalArrivalMath.ComputeArrival</c> computed: pose,
    /// and captured velocity rotated into the destination's frame. Never touches kit, appearance, grants, or any
    /// other dynamic-state facet (dash overlay, timers, tape) — those stay the destination's own fresh values,
    /// exactly like an ordinary spawn arrival. <see cref="WorldBody.Pose(FixedVector3, FixedQ4816, FixedQ4816, FixedQ4816)"/>
    /// runs first (the hard-teleport commit), <see cref="WorldBody.SetArrivalVelocity"/> after — the same
    /// "after Pose, never before" ordering <see cref="WorldBody.ApplyTransferState"/> already follows, so the
    /// discontinuity has already reset <see cref="WorldBody.FixedPreviousPosition"/> before velocity is written. A
    /// no-op returning <see langword="false"/> for an inactive slot — nothing to override.</summary>
    /// <param name="slot">The seat index (0-based) — the same slot the destination's own join just activated.</param>
    /// <param name="position">The mapped arrival position, fixed point.</param>
    /// <param name="yawRadians">The mapped arrival yaw, fixed-point radians.</param>
    /// <param name="planarVelocity">The mapped (rotated) planar velocity.</param>
    /// <param name="verticalVelocity">The mapped (rotation-invariant) vertical velocity.</param>
    /// <returns><see langword="true"/> when the seat was active and its body was overridden.</returns>
    public bool ApplyMappedArrival(int slot, FixedVector3 position, FixedQ4816 yawRadians, FixedVector3 planarVelocity, FixedQ4816 verticalVelocity) {
        var entry = m_entries[slot];

        if (!entry.Active || (entry.Body is not { } body)) {
            return false;
        }

        body.Pose(position: position, yawRadians: yawRadians, pitchRadians: FixedQ4816.Zero, rollRadians: FixedQ4816.Zero);
        // AFTER Pose's own CommitTeleport — see this method's own remarks.
        body.SetArrivalVelocity(planarVelocity: planarVelocity, verticalVelocity: verticalVelocity);

        return true;
    }

    /// <summary>Returns a value indicating whether <paramref name="slot"/> holds a body currently parked (see <see cref="Entry.Parked"/>) —
    /// the resume-eligibility gate a re-Join checks before <see cref="ActivateSeat"/> would mint a fresh body.
    /// <see langword="false"/> for an out-of-range slot, an inactive slot, or an active-but-never-left one.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    public bool IsSeatParked(int slot) => (((uint)slot < LocalSeatCount) && m_entries[slot] is { Active: true, Parked: true });

    /// <summary>Attempts to resume a parked seat's retained body for a re-Join — body-resume, the reconnect
    /// primitive's third half. The match rule is deliberately narrow and precise: the incoming
    /// <paramref name="profile"/>'s <see cref="WorldIdentity.Id"/> must equal the parked body's own retained
    /// <see cref="WorldBody.Profile"/>.<see cref="WorldIdentity.Id"/> — read directly off the body the park never
    /// dropped, so no separate "remembered identity" field is needed. Both <see langword="null"/> (an anonymous seat
    /// reconnecting anonymously) counts as a match too. On a match: clears <see cref="Entry.Parked"/> and returns
    /// <see langword="true"/>, leaving pose/durable state exactly as parked (no fresh spawn, no
    /// <c>ResetDurableState</c> — that reset is keyed on an actual id change, and this is the same id). On a
    /// mismatch, the parked body is left untouched (so a later, correctly-identified re-Join can still recover it
    /// before grace expires) and <paramref name="mismatch"/> is set, letting the caller report a distinct refusal
    /// from "nothing to resume". <see langword="false"/> for a slot that is not parked at all — the caller falls
    /// back to <see cref="ActivateSeat"/>.</summary>
    /// <param name="slot">The seat index (0-based).</param>
    /// <param name="profile">The re-Join's resolved identity, or <see langword="null"/> for an anonymous seat.</param>
    /// <param name="mismatch">Set <see langword="true"/> when the slot is parked but the identity does not match.</param>
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
        entry.ParkedUntilTick = null;

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

    /// <summary>
    /// Resolves active local body pairs after every body has integrated. Pair order is stable population-index order;
    /// each body's own authority remains its sole pose writer and an overlap is shared equally between the pair.
    /// </summary>
    public void ResolveDynamicContacts() {
        var two = FixedQ4816.FromInteger(value: 2L);
        for (var leftIndex = 0; leftIndex < Capacity; leftIndex++) {
            if (!m_entries[leftIndex].Active || (m_entries[leftIndex].Body is not { Collider: { } leftCollider } left)) {
                continue;
            }

            for (var rightIndex = (leftIndex + 1); rightIndex < Capacity; rightIndex++) {
                if (!m_entries[rightIndex].Active || (m_entries[rightIndex].Body is not { Collider: { } rightCollider } right)) {
                    continue;
                }

                if (WorldDynamicBodyContacts.TryCorrection(
                    leftPosition: left.FixedPosition,
                    leftOrientation: left.FixedOrientation,
                    leftCollider: in leftCollider,
                    rightPosition: right.FixedPosition,
                    rightOrientation: right.FixedOrientation,
                    rightCollider: in rightCollider,
                    tieBreaker: (leftIndex ^ rightIndex),
                    correction: out var correction)) {
                    var shared = (correction / two);
                    left.ApplyDynamicContact(correction: shared);
                    right.ApplyDynamicContact(correction: -shared);
                }
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
        // Clamp against the shared networkPlayers budget and every non-census occupant's physical slot.
        var clamped = Math.Clamp(value: count, min: 0, max: SimulatedCeiling);
        var changed = false;
        var remaining = clamped;

        for (var offset = 0; (offset < PeerCapacity); offset++) {
            var index = (LocalSeatCount + offset);
            var entry = m_entries[index];

            // Inhabited and authority-transferred slots are owned by their own lifecycles. They may occupy any index
            // after a mapped handoff, so census selection counts eligible slots instead of assuming all exclusions
            // live at the top of the table.
            if ((entry.PlacementId is not null) || entry.IsRemoteHuman || entry.IsAuthorityTransferred) {
                continue;
            }

            var desired = (remaining > 0);
            if (desired) {
                remaining--;
            }

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

    /// <summary>Sets the peer intent-source default and sweeps every peer (4..127) to it — last-writer-wins, so a
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
    /// <remarks>Runs first, before any body (peer or seat) advances this tick, so an attached solid placement's
    /// colliders (<see cref="WorldColliderSet.RefreshAttached"/>) are refreshed exactly once and every body's push
    /// this tick resolves against the same snapshot — the analytic contact provider only; the field provider compiles
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
        m_entries[index].CatalogRig = checked((byte)index);
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

    /// <summary>Admits one remote-human peer body at the point of effect — the P7 socket door's own primitive,
    /// parallel to <see cref="ReconcileInhabitants"/>'s inhabited-body admission: the body claims the highest free
    /// slot (127 downward, via <see cref="HighestFreeSlot"/>) so it never renumbers an existing peer and never
    /// collides with the census's own upward allocation (<see cref="SetSimulatedCount"/> now skips any slot this
    /// method marks <see cref="Entry.IsRemoteHuman"/>). Refused by name on whichever bound fails: no free slot in the
    /// 128-body table, or the document's <c>networkPlayers</c> admission cap already met (census bots and admitted
    /// remote humans share that one cap — see <see cref="CountActiveCensus"/>).</summary>
    /// <param name="source">The intent source the body starts with (<see cref="IntentSource.Live"/> for a genuine
    /// remote human — a submitted intent/command fills its gaps, never a wander/attend producer).</param>
    /// <param name="grantTemplates">The verified admission entry's own grant templates for this connection (see
    /// <see cref="WorldAdmissionDoor"/>) — stored on the activated slot so a later whole-document rebuild can
    /// compare the then-live rows with the policy baseline before re-authorizing
    /// (<see cref="Server.WorldServer.RemintPeerAdmissionGrants"/>). Empty (never null) for the identical reason a
    /// verified-but-granted-nothing identity is a legitimate outcome — see <see cref="WorldAdmissionEntry.Grants"/>.</param>
    /// <param name="identityDomain">The verified admission identity's own domain (see
    /// <see cref="WorldAdmissionDoor"/>) — stored alongside <paramref name="grantTemplates"/> so a later rebuild can
    /// re-match this identity against the current admission policy instead of trusting the connection-time
    /// verdict still holds (<see cref="Server.WorldServer.RemintPeerAdmissionGrants"/>).</param>
    /// <param name="identitySubject">The verified admission identity's own subject (empty for a Vouches root's
    /// chain-resolved subject).</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public bool TryAdmitRemotePeer(IntentSource source, IReadOnlyList<WorldAdmissionGrant> grantTemplates, string identityDomain, string identitySubject, out WorldPeerEventEntry admitted, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);

        var slot = HighestFreeSlot();

        return TryAdmitRemotePeerAt(slot: slot, source: source, grantTemplates: grantTemplates, identityDomain: identityDomain, identitySubject: identitySubject, admitted: out admitted, refusal: out refusal);
    }

    /// <summary>Admits a remote peer at a body index already bound by a transfer reservation. This is the
    /// commit-side companion to <see cref="TryAdmitRemotePeer"/>; callers must reserve the exact index first.</summary>
    /// <param name="slot">The reserved peer body index.</param>
    /// <param name="source">The body's live or simulated intent source.</param>
    /// <param name="grantTemplates">Admission grant templates to install.</param>
    /// <param name="identityDomain">The verified identity domain.</param>
    /// <param name="identitySubject">The verified identity subject.</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <param name="authorityTransferred">Whether the peer arrived through authority transfer and is therefore not
    /// eligible for destination census reconciliation.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public bool TryAdmitRemotePeerAt(int slot, IntentSource source, IReadOnlyList<WorldAdmissionGrant> grantTemplates, string identityDomain, string identitySubject, out WorldPeerEventEntry admitted, out string refusal, bool authorityTransferred = false) {
        return TryAdmitTransferredEntityAtCore(slot: slot, source: source, remoteHuman: true, authorityTransferred: authorityTransferred, grantTemplates: grantTemplates, identityDomain: identityDomain, identitySubject: identitySubject, admitted: out admitted, refusal: out refusal);
    }

    /// <summary>Admits an autonomous traveler at the peer body index already bound by a transfer reservation.</summary>
    public bool TryAdmitTransferredEntityAt(int slot, IntentSource source, out WorldPeerEventEntry admitted, out string refusal) =>
        TryAdmitTransferredEntityAtCore(slot: slot, source: source, remoteHuman: false, authorityTransferred: true, grantTemplates: [], identityDomain: string.Empty, identitySubject: string.Empty, admitted: out admitted, refusal: out refusal);

    private bool TryAdmitTransferredEntityAtCore(int slot, IntentSource source, bool remoteHuman, bool authorityTransferred, IReadOnlyList<WorldAdmissionGrant> grantTemplates, string identityDomain, string identitySubject, out WorldPeerEventEntry admitted, out string refusal) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);

        if ((slot < LocalSeatCount) || (slot >= Capacity) || m_entries[slot].Active) {
            admitted = default;
            refusal = ((slot < 0) ? $"the {Capacity}-slot entity table is full" : $"reserved peer body:{slot} is no longer free");

            return false;
        }

        if (CountNetworkPlayers() >= m_remoteCap) {
            admitted = default;
            refusal = $"the networkPlayers admission cap ({m_remoteCap}) is already met";

            return false;
        }

        if (!SupportsSource(index: slot, source: source, refusal: out refusal)) {
            admitted = default;
            refusal = $"reserved peer body:{slot} {refusal}";
            return false;
        }

        ActivateSimulated(index: slot, source: source);

        var entry = m_entries[slot];

        entry.Active = true;
        entry.IsRemoteHuman = remoteHuman;
        entry.IsAuthorityTransferred = authorityTransferred;
        // The server-authored PeerAdmitted event applies the requested rows through the live grant door immediately
        // after this allocation and then records ONLY the rows that succeeded. Nothing is installed yet at this
        // point, so the revocation baseline must begin empty rather than containing authored attempts.
        entry.AdmissionInstalledGrantTemplates = [];
        entry.AdmissionRevokedKeys.Clear();
        entry.IdentityDomain = (identityDomain ?? string.Empty);
        entry.IdentitySubject = (identitySubject ?? string.Empty);
        m_simulatedCount = CountActiveCensus();
        m_revision++;
        admitted = PeerEventEntry(index: slot);
        refusal = string.Empty;

        return true;
    }

    /// <summary>The admission templates that actually reached the live grant table for the connection bound to
    /// <paramref name="bodyIndex"/> (see <see cref="TryAdmitRemotePeer"/>), or empty when the slot is not a
    /// remote-admitted peer. <see cref="Server.WorldServer.RemintPeerAdmissionGrants"/>'s one read.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    public IReadOnlyList<WorldAdmissionGrant> PeerAdmissionInstalledGrantTemplates(int bodyIndex) =>
        (((uint)bodyIndex < Capacity) ? m_entries[bodyIndex].AdmissionInstalledGrantTemplates : []);

    /// <summary>Gets admission-grant keys explicitly revoked during this connection. Unlike the current policy
    /// baseline, these survive a policy generation that temporarily removes the row, and are cleared when the live
    /// table shows the row was explicitly granted back.</summary>
    /// <param name="bodyIndex">The 0-based remote-peer body index.</param>
    public IReadOnlySet<(WorldCapability Capability, GrantSubject Subject)> PeerAdmissionRevokedKeys(int bodyIndex) =>
        (((uint)bodyIndex < Capacity) ? m_entries[bodyIndex].AdmissionRevokedKeys : EmptyAdmissionRevokedKeys);

    private static IReadOnlySet<(WorldCapability Capability, GrantSubject Subject)> EmptyAdmissionRevokedKeys { get; } = new HashSet<(WorldCapability Capability, GrantSubject Subject)>();

    /// <summary>Updates the successfully-installed admission baseline for one connected peer after authorization.
    /// A later rebuild compares only these rows with the then-live table: an authored attempt rejected by the grant
    /// door was never present and therefore cannot be inferred as an explicit runtime revoke.</summary>
    /// <param name="bodyIndex">The 0-based remote-peer body index.</param>
    /// <param name="grantTemplates">The templates successfully installed from the current matched policy.</param>
    public void SetPeerAdmissionInstalledGrantTemplates(int bodyIndex, IReadOnlyList<WorldAdmissionGrant> grantTemplates) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);

        if ((uint)bodyIndex < Capacity) {
            m_entries[bodyIndex].AdmissionInstalledGrantTemplates = grantTemplates;
        }
    }

    /// <summary>Replaces one connected peer's persistent explicit-revocation set after rebuild re-authorization.</summary>
    /// <param name="bodyIndex">The 0-based remote-peer body index.</param>
    /// <param name="revokedKeys">The currently remembered revoked keys.</param>
    public void SetPeerAdmissionRevokedKeys(int bodyIndex, IReadOnlySet<(WorldCapability Capability, GrantSubject Subject)> revokedKeys) {
        ArgumentNullException.ThrowIfNull(argument: revokedKeys);

        if ((uint)bodyIndex < Capacity) {
            m_entries[bodyIndex].AdmissionRevokedKeys = new HashSet<(WorldCapability Capability, GrantSubject Subject)>(collection: revokedKeys);
        }
    }

    /// <summary>The verified admission identity's own (Domain, Subject) for the connection currently bound to
    /// <paramref name="bodyIndex"/> (see <see cref="TryAdmitRemotePeer"/>), or two empty strings when the slot is
    /// not a remote-admitted peer. <see cref="Server.WorldServer.RemintPeerAdmissionGrants"/>'s re-authorization
    /// key — it re-matches this pair against the rebuild candidate's own admission entries
    /// (<see cref="WorldAdmissionDoor.TryMatchEntry"/>) rather than trusting <see cref="PeerAdmissionInstalledGrantTemplates"/>
    /// is still what the current document would mint.</summary>
    /// <param name="bodyIndex">The 0-based body index.</param>
    public (string Domain, string Subject) PeerIdentity(int bodyIndex) =>
        (((uint)bodyIndex < Capacity) ? (m_entries[bodyIndex].IdentityDomain, m_entries[bodyIndex].IdentitySubject) : (string.Empty, string.Empty));

    /// <summary>Re-applies one recorded admission through the population door. A live event reaches this after the
    /// point of effect and is idempotent (<see cref="TryAdmitRemotePeer"/> already set every field this touches);
    /// replay reaches it before the effect and reconstructs the body — including the <see cref="Entry.IsRemoteHuman"/>
    /// marker, inferred from <see cref="WorldPeerEventEntry.Source"/> being <see cref="IntentSource.Live"/> (the
    /// document-authored census/inhabitant defaults are never <see cref="IntentSource.Live"/>).</summary>
    /// <param name="peer">The recorded peer entry.</param>
    /// <param name="grantTemplates">The admission templates reconstructed from this event's concrete minted grant
    /// rows. Empty for a document-authored/simulated peer and for a legitimately zero-grant remote identity.</param>
    public void ApplyPeerAdmitted(in WorldPeerEventEntry peer, IReadOnlyList<WorldAdmissionGrant> grantTemplates) {
        ArgumentNullException.ThrowIfNull(argument: grantTemplates);

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
        entry.IsAuthorityTransferred = peer.AuthorityTransferred;
        entry.PlacementId = peer.PlacementId;
        entry.CatalogRig = peer.CatalogRig;

        // Live admission already installed these fields before emitting the event, so this is idempotent there.
        // Replay reaches this path with a fresh population and needs the verified identity restored so a later
        // recorded rebuild re-authorizes the peer against the same facts the live rebuild consulted.
        entry.AdmissionInstalledGrantTemplates = grantTemplates;
        entry.AdmissionRevokedKeys.Clear();
        entry.IdentityDomain = peer.IdentityDomain;
        entry.IdentitySubject = peer.IdentitySubject;
    }

    /// <summary>Re-applies one recorded disconnect through the population door. Park-with-grace: on the same terms as
    /// <see cref="DeactivateSeat"/>, this defers the body/occupancy half of the teardown (<see cref="Entry.Body"/>,
    /// <see cref="Entry.Active"/>, <see cref="Entry.IsRemoteHuman"/>) to <see cref="ReclaimExpiredParks"/> when the
    /// compiled grace is positive or <see cref="CompiledTickDuration.IsNever"/> (a positive authored grace at
    /// simulation rate 0 — see <see cref="DeactivateSeat"/>'s own remarks) — the entry marks
    /// <see cref="Entry.Parked"/> instead, and <see cref="IsAdmittedPeer"/> (hence <see cref="IsHumanOccupied"/>)
    /// keeps reading <see langword="true"/> through the grace window since <see cref="Entry.IsRemoteHuman"/> is
    /// untouched. The grant half of the teardown is deliberately not deferred here — the caller
    /// (<c>Server.WorldServer.ApplyServerEvent</c>) still revokes the disconnected generation's grants immediately,
    /// unchanged from the pre-park behavior; deferring that too would mean reshaping
    /// <c>WorldServerEvent.PeerDisconnected</c>'s ordered-domain/replay-tape contract, which this population-local
    /// change does not reach into. See the reconnect-primitives change notes for this as a named, deliberate scope
    /// line, not a silent gap.</summary>
    /// <param name="peer">The recorded peer entry.</param>
    /// <param name="tick">The current tick — the basis a finite <see cref="Entry.ParkedUntilTick"/> is stamped
    /// from.</param>
    public void ApplyPeerDisconnected(in WorldPeerEventEntry peer, ulong tick) {
        if ((uint)(peer.BodyIndex - LocalSeatCount) >= PeerCapacity) {
            return;
        }

        var entry = m_entries[peer.BodyIndex];

        if (entry.Active && (entry.Generation == peer.Generation)) {
            if (m_reconnectGraceTicks.IsNever) {
                entry.Parked = true;
                entry.ParkedUntilTick = null;
            } else if (m_reconnectGraceTicks.IsZero) {
                entry.Body = null;
                entry.Active = false;
                entry.IsRemoteHuman = false;
                entry.IsAuthorityTransferred = false;
                entry.PlacementId = null;
                entry.AdmissionInstalledGrantTemplates = [];
                entry.AdmissionRevokedKeys.Clear();
                entry.IdentityDomain = string.Empty;
                entry.IdentitySubject = string.Empty;
                entry.Parked = false;
                entry.ParkedUntilTick = null;
            } else {
                entry.Parked = true;
                entry.ParkedUntilTick = unchecked((long)tick + m_reconnectGraceTicks.Ticks);
            }

            m_simulatedCount = CountActiveCensus();
            m_revision++;
        }
    }

    /// <summary>Tears down every entry parked past its grace deadline — the deferred half of
    /// <see cref="DeactivateSeat"/>/<see cref="ApplyPeerDisconnected"/>'s teardown (see <see cref="Entry.Parked"/>'s
    /// own remarks): drops the body, clears <see cref="Entry.Active"/> and (for a peer) <see cref="Entry.IsRemoteHuman"/>,
    /// exactly as an immediate disconnect already did before park-with-grace existed. Covers both local seats and
    /// peers in one pass — the same <c>Active &amp;&amp; Parked</c> gate discriminates a park regardless of
    /// <see cref="PopulationKind"/>, so there is no separate seat/peer sweep. A disconnected peer generation's grants
    /// are revoked at disconnect time already (see <see cref="ApplyPeerDisconnected"/>'s own remarks on why that
    /// revocation is not deferred here); a local seat never held generation-scoped grants to revoke, so nothing
    /// grant-shaped happens here for either kind. Driven purely by <paramref name="tick"/> — no wall clock, no
    /// randomness — so it is exactly as replay-deterministic as <c>Server.WorldServer.ReclaimExpiredEscrows</c>,
    /// which this mirrors and is swept beside every tick.
    /// <para><b>Revival re-stamp.</b> This method is per-tick and so never runs for a rate-0 world (the step loop
    /// that calls it is itself skipped — see <c>WorldInstanceHost</c>'s stepping gate); a seat that parked with
    /// <see cref="Entry.ParkedUntilTick"/> <see langword="null"/> (a positive reconnect grace compiled against rate
    /// 0 — <see cref="CompiledTickDuration.IsNever"/>) therefore stays exactly as parked, untouched, until the world
    /// steps again. <see cref="Rebuild"/> recompiles <see cref="m_reconnectGraceTicks"/> against whatever rate a
    /// reload delivers, but it only ever touches the compiled tables — it does not walk live entries — so the first
    /// sweep after a revival to a positive rate is exactly the moment a null-forever deadline is resolved against
    /// the now-finite grace: it is dropped and re-derived, never left stranded. A null deadline with a still-never
    /// compiled grace (the world reloaded but is still rate 0, or reloaded at a positive rate with the grace itself
    /// re-authored as never — not possible today, since never only arises at rate 0, but the branch reads correctly
    /// either way) is left null, exactly as before. A freshly-stamped entry is deliberately not evaluated for
    /// teardown in the same pass — the visitor's window restarts at the revival tick, so it must survive at least
    /// one full sweep before it can expire.</para></summary>
    /// <param name="tick">The current (just-completed) simulation tick.</param>
    public void ReclaimExpiredParks(ulong tick) {
        var signedTick = unchecked((long)tick);
        var changed = false;

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (!(entry is { Active: true, Parked: true })) {
                continue;
            }

            if (entry.ParkedUntilTick is not { } deadline) {
                // A NEVER park (see this method's own "Revival re-stamp" remarks). Only re-derivable once the
                // compiled grace itself is no longer NEVER — a rate-0 world never reaches this method at all, so
                // reading m_reconnectGraceTicks.IsNever here is exactly "has this world been revived to a positive
                // rate since the park happened".
                if (m_reconnectGraceTicks.IsNever) {
                    continue;
                }

                entry.ParkedUntilTick = (signedTick + m_reconnectGraceTicks.Ticks);
                changed = true;

                continue;
            }

            if (signedTick >= deadline) {
                entry.Body = null;
                entry.Active = false;
                entry.Parked = false;
                entry.ParkedUntilTick = null;
                entry.IsAuthorityTransferred = false;
                entry.PlacementId = null;

                if (entry.IsRemoteHuman) {
                    entry.IsRemoteHuman = false;
                    entry.AdmissionInstalledGrantTemplates = [];
                    entry.AdmissionRevokedKeys.Clear();
                    entry.IdentityDomain = string.Empty;
                    entry.IdentitySubject = string.Empty;
                }

                changed = true;
            }
        }

        if (changed) {
            m_simulatedCount = CountActiveCensus();
            m_revision++;
        }
    }

    /// <summary>Returns a value indicating whether <paramref name="index"/> currently holds a parked body (see <see cref="Entry.Parked"/>) —
    /// the general form of <see cref="IsSeatParked"/>, valid for a local seat or a peer index alike. The read-back
    /// verb's own enumeration gate.</summary>
    /// <param name="index">The 0-based entity index.</param>
    /// <returns><see langword="true"/> when the index holds a parked body.</returns>
    public bool IsParked(int index) => (((uint)index < Capacity) && m_entries[index] is { Active: true, Parked: true });

    /// <summary>Reads the reconnect-park reserved rule channel's live value for one body — the remaining grace ticks
    /// (<c>ParkedUntilTick - tick</c>, floored at zero) when the body is parked with a finite deadline, <c>0</c> for
    /// an active, unparked, or out-of-range body alike, and <see langword="null"/> for a body parked forever
    /// (<see cref="Entry.ParkedUntilTick"/> is <see langword="null"/> — a positive grace compiled at simulation rate
    /// 0; see <see cref="DeactivateSeat"/>'s own remarks).
    /// <para><b>Forever is not a number, and every consumer says so in its own vocabulary.</b> <c>world.parked</c>
    /// renders <c>never</c> for both fields; the <c>$parked:</c> reserved rule channel
    /// (<see cref="WorldRuleFacts.ParkedPrefix"/>, read through <c>Server.WorldServer.ReadWorldFact</c>'s
    /// <c>WorldRuleFactKind.Parked</c> arm) carries it as positive infinity — <c>remaining &gt; 0</c> holds (the
    /// seat is parked, more so than any other), <c>remaining &gt; any finite</c> holds, <c>remaining &lt;= any
    /// finite</c> does not, and a copy operand alone cannot fire because there is no representable number to store
    /// (the <c>ActionStateComparisons</c> infinity-aware overload owns the comparison semantics). That is exactly
    /// what the expiry sweep already says on the deadline side, where <c>signedTick &gt;= ParkedUntilTick</c> never
    /// fires against a null deadline: the channel repeats what the sweep says rather than inventing a third answer.
    /// Reading forever as no fact was considered and rejected — it would make the most-parked seat of all invisible
    /// to a rule gated on <c>remaining &gt; 0</c>, a lie by omission rather than by sentinel; and a numeric sentinel
    /// was rejected because a rule could not tell it from an authored literal.</para></summary>
    /// <param name="index">The resolved 0-based entity index, or a negative sentinel for "no body".</param>
    /// <param name="tick">The current tick.</param>
    /// <returns>The remaining grace ticks when the body is parked with a finite deadline; <see langword="null"/>
    /// when parked forever (a deadline that will never arrive is not a count — see <c>ParkedUntilTick</c>'s own
    /// remarks for why no numeric sentinel is admissible); <c>0</c> for an active, unparked, or out-of-range
    /// body.</returns>
    public long? ParkedRemainingTicks(int index, ulong tick) {
        if ((uint)index >= Capacity) {
            return 0L;
        }

        var entry = m_entries[index];

        if (!(entry is { Active: true, Parked: true })) {
            return 0L;
        }

        if (entry.ParkedUntilTick is not { } deadline) {
            return null;
        }

        return Math.Max(val1: 0L, val2: (deadline - unchecked((long)tick)));
    }

    private WorldPeerEventEntry PeerEventEntry(int index) {
        var entry = m_entries[index];
        var identity = WorldPrincipal.Peer(index: index, generation: entry.Generation);

        return new WorldPeerEventEntry(BodyIndex: index, Generation: entry.Generation, Source: (entry.Body?.Source ?? m_defaultPeerSource), Identity: identity, IdentityDomain: entry.IdentityDomain, IdentitySubject: entry.IdentitySubject, AuthorityTransferred: entry.IsAuthorityTransferred, PlacementId: entry.PlacementId, CatalogRig: entry.CatalogRig);
    }

    private int CountActiveCensus() {
        var count = 0;

        for (var index = LocalSeatCount; index < Capacity; index++) {
            if (m_entries[index].Active && (m_entries[index].PlacementId is null) && !m_entries[index].IsRemoteHuman && !m_entries[index].IsAuthorityTransferred) {
                count++;
            }
        }

        return count;
    }

    private int CountNetworkPlayers() {
        var count = 0;
        for (var index = LocalSeatCount; index < Capacity; index++) {
            if (m_entries[index].Active && (m_entries[index].PlacementId is null)) {
                count++;
            }
        }
        return count;
    }

    private int CountExternalNetworkPlayers() {
        var count = 0;
        for (var index = LocalSeatCount; index < Capacity; index++) {
            if (m_entries[index].Active && (m_entries[index].IsRemoteHuman || m_entries[index].IsAuthorityTransferred)) {
                count++;
            }
        }
        return count;
    }

    private int AvailableCensusSlots() {
        var count = 0;
        for (var index = LocalSeatCount; index < Capacity; index++) {
            var entry = m_entries[index];
            if ((entry.PlacementId is null) && !entry.IsRemoteHuman && !entry.IsAuthorityTransferred) {
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
        // True when this entity-table occupant arrived through authority transfer rather than the destination's own
        // census/inhabitant authoring. Population edits must not reseed it; replay and abort carry this bit explicitly.
        public bool IsAuthorityTransferred { get; set; }
        // Admission templates that ACTUALLY reached the live table for THIS connection (empty for a plain census/
        // inhabitant activation, which never carries a verified remote identity). Updated after initial admission
        // and every rebuild re-authorization from successful grant-door outcomes only; an authored-but-rejected row
        // is not a revocation candidate. Read back only by WorldServer.RemintPeerAdmissionGrants before the rebuild
        // wipes the live table. Cleared alongside IsRemoteHuman on every teardown path.
        public IReadOnlyList<WorldAdmissionGrant> AdmissionInstalledGrantTemplates { get; set; } = [];
        // Explicit runtime revocations inferred at each rebuild from baseline templates absent in the live table.
        // Kept separately from the moving installed-row baseline so temporarily removing/re-adding a policy row does not
        // forget a revocation, and cleared when a later live snapshot shows the row was explicitly granted back.
        public HashSet<(WorldCapability Capability, GrantSubject Subject)> AdmissionRevokedKeys { get; set; } = [];
        // The verified admission identity's own (Domain, Subject) for THIS connection — set at TryAdmitRemotePeer,
        // restored by ApplyPeerAdmitted during replay, and cleared everywhere the installed baseline is cleared.
        // RemintPeerAdmissionGrants' re-authorization key: it re-matches THESE against the rebuild candidate's own
        // admission entries (WorldAdmissionDoor.TryMatchEntry) rather than trusting the stored templates are still
        // what the current document would mint.
        public string IdentityDomain { get; set; } = string.Empty;
        public string IdentitySubject { get; set; } = string.Empty;
        // PARK-WITH-GRACE: set by DeactivateSeat/ApplyPeerDisconnected instead of tearing the entry down immediately.
        // While Parked, Active (and, for a peer, IsRemoteHuman) STAY true
        // and Body stays retained (pose/state intact, still in the sim/collider set) — a disconnected body reads
        // IsHumanOccupied exactly as an occupied one does (the owner's occupancy ruling: parked stays targetable,
        // CC continues offline). Cleared by TryResumeParkedSeat on a matching re-Join, or by ReclaimExpiredParks
        // once ParkedUntilTick passes with no reconnect — see both their own remarks.
        public bool Parked { get; set; }
        // The tick AT OR AFTER which ReclaimExpiredParks tears this entry down — the SAME "DeadlineTick" shape
        // OwnershipEscrow already uses for its own tick-driven sweep. Meaningless while !Parked. NULL means parked
        // FOREVER (a positive reconnect grace compiled at simulation.rateHz 0 has no tick mapping — see
        // CompiledTickDuration): deliberately NOT a numeric sentinel (e.g. long.MaxValue). This field is stamped by
        // `unchecked((long)tick + grace)` and read by subtraction in ParkedRemainingTicks, so a sentinel large
        // enough to mean "forever" would be exactly the value most likely to wrap under that addition, or to
        // silently produce a plausible-but-wrong remaining count under that subtraction. A nullable never
        // participates in that arithmetic at all — the NEVER case is assigned directly, with no addition performed,
        // and every reader must unwrap it explicitly before doing arithmetic.
        public long? ParkedUntilTick { get; set; }
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
        // The occupant's implicit procedural rig, separate from its authority-local slot. Explicit authored looks can
        // override it, but an authority handoff preserves it.
        public required byte CatalogRig { get; set; }
        public FixedVector3 SpawnPosition { get; set; }
        public FixedQ4816 SpawnYaw { get; set; }
    }
}

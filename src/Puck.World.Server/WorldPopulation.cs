using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.Physics;
using Puck.Physics.Motion;

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
/// command wire (<c>body.pose</c> / <c>fly</c> / <c>stop</c>). A live <c>body.fly</c> tape overrides
/// the submitted intent (tape &gt; submitted in the intent merge).
/// </para>
/// <para>
/// Single-threaded: <see cref="WorldServer.Step"/> drives everything on host ticks on the window-pump thread. No lock
/// guards this state.
/// </para>
/// </remarks>
public sealed partial class WorldPopulation {
    /// <summary>Gets the currently configured adjacency resolver — see <see cref="ConfigureAdjacencies"/>, the
    /// one writer.</summary>
    public IWorldAdjacencySource? Adjacencies => m_adjacencies;
    /// <summary>The authored entity-table capacity allocated at world load.</summary>
    public int Capacity => m_entries.Length;
    /// <summary>The world's compiled channel table (name→ordinal, per-ordinal shape/threshold) — the co-driving
    /// fold's one source of per-channel shape/threshold (<see cref="Server.WorldServer"/>'s fold phase reads it
    /// directly rather than re-deriving it).</summary>
    public WorldChannelTable Channels => m_channels;
    /// <summary>The live definition's analytic collider census, including placement-derived colliders even when the
    /// selected provider is the SDF field or collision is disabled.</summary>
    public WorldContactCensus ContactCensus => m_contactCensus;
    /// <summary>The stored peer intent-source default — a template, not an
    /// aggregate, which is why it stays observable at zero peers: newly activated peers take it, and an explicit
    /// population source command sets it and sweeps every peer. Render-inert: it reshapes only the intent
    /// producers, never the declared set or palette, so it does not bump the <see cref="Revision"/>.</summary>
    public IntentSource DefaultPeerSource => m_defaultPeerSource;
    /// <summary>The authored designation submissions emitted during the most recently completed tick.</summary>
    public IReadOnlyList<WorldDesignation> DesignationOutputs => m_designationOutputs;
    /// <summary>The durable writes emitted by the most recently completed tick.</summary>
    public IReadOnlyList<DurableStateOutput> DurableStateOutputs => m_durableStateOutputs;
    /// <summary>Number of pairs admitted to narrow phase by the most recently completed broadphase.</summary>
    public int DynamicContactNarrowPairs { get; private set; }
    /// <summary>Number of solid-body pairs before broadphase pruning in the most recently completed solve.</summary>
    public int DynamicContactPotentialPairs { get; private set; }
    /// <summary>Number of overlaps resolved by the most recently completed dynamic-body solve.</summary>
    public int DynamicContactResolvedPairs { get; private set; }
    /// <summary>The <c>generate</c> effect firings staged by the most recently completed tick's advance — drained and
    /// enqueued through the ordinary mutation pipeline by <c>WorldServer.Step</c>, mirroring
    /// <see cref="DesignationOutputs"/>'s own shape.</summary>
    public IReadOnlyList<WorldGeneratorInvocation> GeneratorInvocationOutputs => m_generatorInvocations;
    /// <summary>The <c>judge</c> effect firings staged by the most recently completed tick's advance — drained and
    /// graded by <c>WorldServer.Step</c> immediately after the whole population advance, mirroring
    /// <see cref="GeneratorInvocationOutputs"/>'s own shape.</summary>
    public IReadOnlyList<WorldJudgeInvocation> JudgeInvocationOutputs => m_judgeInvocations;
    /// <summary>The live look rows (the authored rows, or the implicit single catalog look) the census resolves against.</summary>
    public IReadOnlyList<WorldLook> LookRows => m_lookRows;
    /// <summary>The number of entity-table slots currently eligible for destination-authored census bodies. Inhabitants,
    /// connected humans, and authority-transferred entities are excluded wherever they sit; mapped handoff makes a
    /// low-index exclusion ordinary, so no packing-order or contiguous-floor assumption is valid.</summary>
    public int MaxSimulated => AvailableCensusSlots();
    /// <summary>The authored peer slice behind the reserved local seats.</summary>
    public int PeerCapacity => (Capacity - LocalSeatCount);
    /// <summary>A monotonically increasing counter bumped whenever the declared set or palette changes (a seat joining,
    /// leaving, or recoloring, or the simulated count moving), never on a per-frame pose write. The frame source combines
    /// it with the roster's revision to decide when to rebuild the avatar program.</summary>
    public int Revision => m_revision;
    /// <summary>The largest census <see cref="SetSimulatedCount"/> will actually grant right now — the tighter of the
    /// remaining remote admission budget (<c>networkPlayers</c>) and eligible slots (<see cref="MaxSimulated"/>). A request
    /// above it is clamped to it, so the <c>world.population</c> echo names both the granted count and this ceiling
    /// rather than letting a script read a success for a crowd it never got.</summary>
    public int SimulatedCeiling => Math.Min(
        val1: Math.Max(
            val1: 0,
            val2: (m_remoteCap - CountExternalNetworkPlayers())
        ),
        val2: MaxSimulated
    );
    /// <summary>The number of active simulated stand-ins (indices <c>4..</c>).</summary>
    public int SimulatedCount => m_simulatedCount;
    /// <summary>The boot-built SDF contact field when the definition selects the field provider, else
    /// <see langword="null"/> — the seam <see cref="WorldServer"/> adopts at construction so it owns the field lifecycle
    /// without a second boot build. A live rebuild instead receives the server's field back through
    /// <see cref="Rebuild(WorldDefinition, WorldSolidField?)"/>.</summary>
    public WorldSolidField? SolidField => (m_contactField as WorldSolidField);
    /// <summary>The world's compiled target-register table sharing the Drive reach-mask ordinal space.</summary>
    public WorldTargetRegisterTable TargetRegisters => m_targets;
    /// <summary>Gets this world's reserved local-seat count — the document's own <c>population.localSeats</c>
    /// declaration, always at the front of the entity table, up to the host's seat ceiling
    /// (<see cref="WorldBodiesLimits.LocalSeatCount"/>).</summary>
    public int LocalSeatCount { get; private set; }

    private readonly Entry[] m_entries;

    private IWorldAdjacencySource? m_adjacencies;
    private WorldDefinition? m_adjacencyDefinition;
    // The world contact field derived from the definition's solid geometry and collision tuning. Built by
    // CompileFixedTables and handed to every live body, so a live solid-geometry or collision-tuning edit takes
    // effect on the next tick with no restart. Grounded bodies solve their swept position against it.
    // m_contactField is the EFFECTIVE field a body resolves against — m_baseContactField wrapped by
    // WorldAdjacencyContactField when an adjacency resolver is configured AND the live definition authors an edge
    // band, or m_baseContactField unwrapped otherwise. Composed by
    // ComposeContactField, the ONE place either input changes.
    private IContactField? m_baseContactField;
    private WorldContactCensus m_contactCensus;
    private IContactField? m_contactField;
    // The compiled population distribution (fixed point). SIM-AFFECTING: SeedSimulated reads only this, never the authored floats.
    // Live for FUTURE activations, inert for bodies already standing (resetPhase: false keeps the running crowd put).
    private FixedWorldDistribution m_distribution;
    // The fixed-point derived tables — recompiled in place by Rebuild when a sim-affecting section mutates (a live kit
    // tune, motion/wander retune, seat-kit or assignment change), so they are no longer readonly.
    private FixedMotionDefaults m_fixedMotion;
    private byte[]? m_lookAssignmentRows;
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
    private byte m_seatKit;
    // Where each seat's body spawns, from the definition — staggered around the origin,
    // all facing -Z, so a fresh join never lands on top of another avatar. Order maps slots (seat n → [n]).
    private FixedSpawnPoint[] m_seatSpawns;
    private int m_simulatedCount;
    private WorldSolidField? m_targetField;
    private FixedQ4816? m_waterline;
    private WorldFieldLattice? m_fields;
    // The compiled climb/grapple policy every live body reads its attach/detach/reel channel ordinals and grip/rope
    // tuning from. Recompiled by CompileFixedTables beside every other sim-affecting table and handed to each live
    // body the same way the contact field/gravity/waterline are (see the SetAttachmentPolicy call sites).
    private FixedWorldAttachment m_fixedAttachment = FixedWorldAttachment.Absent;

    /// <summary>Gets the compiled climb/grapple policy for read-back (<c>world.attach-policy</c>).</summary>
    public FixedWorldAttachment CompiledAttachment => m_fixedAttachment;
    /// <summary>Gets the field lattice, when the world declares a <c>fields</c> section.</summary>
    public WorldFieldLattice? Fields => m_fields;
    /// <summary>Gets the compiled gravity declaration for read-back.</summary>
    public FixedWorldGravity CompiledGravity => (m_gravityField?.Compiled ?? FixedWorldGravity.Inert);
    /// <summary>Gets the last gravity solve's deterministic structural work counters.</summary>
    public GravitySolveStatistics GravityStatistics => (m_gravityField?.Statistics ?? default);

    private WorldPlayerDefaults m_playerDefaults = null!;
    private WorldPopulationVariation m_peerVariation = null!;
    private WorldPopulationVariation m_seatVariation = null!;
    private WorldSequence m_peerColors = null!;
    // The definition's kit rows: the authored rows (body construction reads a row's tuning) and their fixed-point
    // compilations (producer programs read their parameter maps), plus the resolved seat row. Assigned by CompileFixedTables from
    // the constructor (the empty seeds satisfy definite-assignment across that helper call).
    private IReadOnlyList<WorldKit> m_kitRows = [];

    private WorldGravityField? m_gravityField;

    // Reused across ticks: a steady-state population allocates nothing to solve its field.
    private readonly List<WorldGravityTarget> m_gravityTargets = [];
    private FixedWorldKit[] m_kits = [];
    private IReadOnlyDictionary<string, CompiledBodyMotionProgram> m_bodyMotionPrograms = new Dictionary<string, CompiledBodyMotionProgram>();
    // The world's compiled channel table — kit Actions/PressChannel name resolution reads it once per compile pass.
    private WorldChannelTable m_channels = WorldChannelTable.Empty;
    private IReadOnlyList<WorldTargetRegister> m_targetRows = [];
    private WorldTargetRegisterTable m_targets = WorldTargetRegisterTable.Empty;
    // The definition's LOOK rows (empty ⇒ the implicit single catalog look), resolved by CompileFixedTables. Each
    // entry's LookIndex points into this list. PRESENTATION-ONLY — the snapshot carries it to the client's renderer.
    private IReadOnlyList<WorldLook> m_lookRows = [WorldLook.Implicit];
    private WorldRowAssignment m_lookAssignment = null!;
    private IntentSource m_defaultPeerSource = IntentSource.Live;
    private readonly List<BodyEffectOutput> m_effectOutputs = [];
    private readonly List<WorldDesignation> m_designationOutputs = [];
    private readonly List<WorldGeneratorInvocation> m_generatorInvocations = [];
    private readonly List<WorldJudgeInvocation> m_judgeInvocations = [];
    private readonly List<DurableStateOutput> m_durableStateOutputs = [];
    private static readonly FixedQ4816 TwoPi = FixedQ4816.FromDouble(value: (2.0 * Math.PI));
    private static readonly FixedVector3 LocalForward = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.Zero,
        Z: -FixedQ4816.One
    );
    private static readonly FixedVector3 LocalSightOffset = new(
        X: FixedQ4816.Zero,
        Y: FixedQ4816.One,
        Z: FixedQ4816.Zero
    );
    private static IReadOnlySet<(WorldCapability Capability, GrantSubject Subject)> EmptyAdmissionRevokedKeys { get; } = new HashSet<(WorldCapability Capability, GrantSubject Subject)>();

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

        m_seatSpawns = CompileSeatSpawns(
            spawnPoints: definition.SpawnPoints,
            seatSpawns: definition.Population.SeatSpawns
        );
        // Boot the live peer-source default from the document (the session write-back home). A live retune/swap keeps the
        // running session value — this seeds only at construction, so a saved world's authored default is honored at boot.
        m_defaultPeerSource = definition.Population.DefaultPeerSource;

        // The boot contact field: analytic is derived here; the field provider is compiled once (a bad-op world fails
        // LOUDLY at boot, which is the honest boot-time counterpart of the live apply-time rejection). A live rebuild
        // instead receives the server's pre-built field so a runtime edit never rebuilds it twice.
        CompileFixedTables(
            definition: definition,
            solids: null
        );

        // Resolve the definition's kit→entity assignment ONCE into every entry's fixed kit index.
        var assignmentRows = ResolveRows(
            assignment: definition.Assignment,
            resolve: ResolveKit
        );

        for (var index = 0; (index < Capacity); index++) {
            m_entries[index] = new Entry {
                KitIndex = SelectRow(
                index: index,
                assignment: definition.Assignment,
                rows: assignmentRows,
                rowCount: m_kits.Length
            ),
                Kind = ((index < LocalSeatCount)
                ? PopulationKind.LocalSeat
                : PopulationKind.NetworkPeer),
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

    // One entity-table entry. A mutable class; Kind and KitIndex are fixed at construction. SpawnYaw is the
    // index-seeded heading a fresh activation faces the new body toward. Body is the entry's own sim — null while
    // inactive, minted on activation (a session join for a seat, the census or an inhabitant join for a peer).
    private sealed class Entry {
        public bool Active { get; set; }
        public WorldBody? Body { get; set; }
        public Vector3 BodyColor { get; set; }
        // The occupant's implicit procedural rig, separate from its authority-local slot. Explicit authored looks can
        // override it, but an authority handoff preserves it.
        public required byte CatalogRig { get; set; }
        public required WorldTargetDesignation[] Designations { get; set; }
        // Bumped every time this peer slot transitions inactive -> active. Never reset on disconnect.
        public int Generation { get; set; }
        // True when this entity-table occupant arrived through authority transfer rather than the destination's own
        // census/inhabitant authoring. Population edits must not reseed it; replay and abort carry this bit explicitly.
        public bool IsAuthorityTransferred { get; set; }
        // Whether this slot is bound to a REMOTE-ADMITTED human connection (Server.WorldTcpHost's Hello door), as
        // opposed to a locally-simulated census stand-in. Set by TryAdmitRemotePeer/ApplyPeerAdmitted, cleared by
        // ApplyPeerDisconnected — SetSimulatedCount skips a slot carrying it exactly like an inhabited one, so a
        // world.population edit can never silently reassign or deactivate a connected human's body.
        public bool IsRemoteHuman { get; set; }
        // Kind is fixed at construction (LocalSeat for slots 0..3, NetworkPeer for 4..127) and never changes: an
        // inhabitant is a NetworkPeer distinguished by its PlacementId, not a kind flip.
        public required PopulationKind Kind { get; init; }
        // Reassigned in place by Rebuild when the kit-assignment policy (or kit set) mutates; set at construction.
        public required byte KitIndex { get; set; }
        // The resolved LOOK row index (PRESENTATION-ONLY; carried out on the snapshot). Reassigned by ResolveLookIndices
        // on construction and on every Rebuild.
        public byte LookIndex { get; set; }
        // The stable cross-authority incarnation/epoch and the local generation whose occupant owns it. The stamp
        // makes ordinary slot reuse invalidate stale mobility without comparing the foreign origin generation.
        public WorldMobilityIdentity? Mobility { get; set; }
        public int MobilityGeneration { get; set; }
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
        // The placement row this peer inhabits (null for a plain census peer or an empty slot) — the back-reference the
        // frame source and anchor resolver look up by, and the flag that marks a peer as an inhabitant. Set/cleared by
        // ReconcileInhabitants.
        public string? PlacementId { get; set; }
        public PressOutcome PressOutcome { get; set; }
        public FixedVector3 SpawnPosition { get; set; }
        public FixedQ4816 SpawnYaw { get; set; }
        public StopOutcome StopOutcome { get; set; }

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
        public string DesignationRefusal { get; set; } = string.Empty;
        // The most recent body.motion refusal reason (empty on the last switch's success) — the synchronous
        // submitter's read-back so an honest immediate echo never has to guess the outcome.
        public string MotionRefusal { get; set; } = string.Empty;
        // The most recent body.stop refusal reason (empty on success) and outcome (released/cleared counts) —
        // ALWAYS written together (see NoteStopOutcome/NoteStopRefusal) so the pair can never desync into a
        // refusal note pointing at stale success counts or vice versa.
        public string StopRefusal { get; set; } = string.Empty;
        // The most recent body.press refusal reason (empty on success — timed or untimed alike, they share this
        // one slot) and the timed path's outcome (effective hold + which cap decided it) — ALWAYS written together,
        // the same pairing discipline as StopRefusal/StopOutcome above.
        public string PressRefusal { get; set; } = string.Empty;

        public BodyProducerState ProducerState;
    }
}

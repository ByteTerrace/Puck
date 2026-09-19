using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>
/// The tick facade of <see cref="WorldServer"/>: the ordered domain and the intent queue, the step clock, the
/// co-driving contribution fold and its read-back, the per-tick engagement, transfer, field, music, response,
/// placement-deal and board-enforcement work, and the snapshot each completed tick emits.
/// </summary>
/// <remarks>Ordinary work is single-threaded on the host tick. Submissions arrive during the command pump's apply
/// window and <see cref="Step"/> runs immediately after, both on the launcher's window-pump thread; authenticated
/// federation operations reach the queues only under <see cref="WorldServer.ExecuteAuthorityOperation{T}"/>.</remarks>
public sealed partial class WorldTick {
    private readonly Queue<IntentSubmission> m_intents = new();
    // Reused scratch for the possessed-inhabitant walk — cleared and refilled on every check rather than allocated
    // per call. Single-threaded on the host tick, like every other tick-facade buffer.
    private readonly List<int> m_possessedInhabitantScratch = [];
    // The (entity, principal) pairs an ALLOWED intent has already written THIS TICK — the seat drain plus every mounted
    // addon's contributions. Sized at AttachAddons to the local-seat lane count (Client.WorldClient.SubmitSeatIntents
    // produces one per live roster slot) plus two per mounted addon, which covers the ordinary case of a guest driving
    // one or two granted bodies. It is a BOUND on distinct entities tracked for one tick, not a correctness invariant:
    // a guest holding Drive over more bodies than that saturates the tracking, and the defensive length check in
    // ReportContention is what makes saturation degrade contention REPORTING rather than break anything — deliberately,
    // because the alternative is a per-tick resize on the hot path to improve a diagnostic. A second submission naming
    // an entity already written this tick by a DIFFERENT principal is a genuine conflict between two distinct Drive
    // grants over one body — Step reports it loudly rather than letting the later one silently overwrite the earlier.
    private int[] m_tickWrittenEntity = new int[WorldBodiesLimits.LocalSeatCount];
    private WorldPrincipal[] m_tickWrittenPrincipal = new WorldPrincipal[WorldBodiesLimits.LocalSeatCount];
    // Whether the matching m_tickWrittenEntity slot saw a SECOND, different-principal write THIS tick — read once the
    // whole drain AND the addon contributions have finished (see Step) to settle m_contended for real, since which
    // submission a queue happens to dequeue first says nothing about whether the body was genuinely contended for the
    // tick as a whole.
    private bool[] m_tickCollided = new bool[WorldBodiesLimits.LocalSeatCount];
    // --- The co-driving contribution set (fed to FixedContributionFold below) ---
    // A contribution can only ever land on a HUMAN-OCCUPIED body (WorldPopulation.IsHumanOccupied gates it — an
    // unoccupied body is a bot at full authority, applied directly in ApplyIntentSubmission, and never reaches this
    // set), and occupancy today is exactly the local-seat slice — so the set preallocates LocalSeatCount ×
    // ChannelLimits.MaxChannels raw-Int64 slots (a handful of longs) rather than a per-mounted-addon bound, and
    // folding a tick allocates nothing regardless of how many addons are mounted. m_ownerBase/m_hasOwnerBase are the
    // tick's `h` per seat (the OCCUPYING seat's own submission — never the ladder's winner: a tape still outranks
    // it, see WorldBody.NextIntent), and m_ownerHeld is that submission's held-device image; the sum arrays are
    // indexed `(seat * ChannelLimits.MaxChannels) + ordinal`. There is NO per-tick ceiling accumulator: the pool
    // ceiling is one number per (seat, channel) read straight off the seat's own grant row
    // (WorldGrants.PoolCeilings), never derived from whichever contributors happened to land this tick.
    private readonly PlayerIntent[] m_ownerBase = new PlayerIntent[WorldBodiesLimits.LocalSeatCount];
    private readonly PlayerIntent[] m_ownerHeld = new PlayerIntent[WorldBodiesLimits.LocalSeatCount];
    private readonly bool[] m_hasOwnerBase = new bool[WorldBodiesLimits.LocalSeatCount];
    private readonly bool[] m_hasContribution = new bool[WorldBodiesLimits.LocalSeatCount];
    // Every staged delta was already bounded to |d| <= One at the pump. FixedContributionFold records the exact
    // generic Int64 accumulator boundary; World's concrete set is far smaller: untrusted terms are mounted Wasmtime
    // instances (memory exhausts around 2^20), while trusted co-driving seats number at most LocalSeatCount - 1.
    // Timed presses still bypass this fold and collapse to one timer per channel in WorldBody.PressChannel, so they
    // do not enter either sum today. With One = 2^16 (FixedQ4816) and at most ~2^20 untrusted terms, a completed sum
    // peaks near 2^36, so it stays roughly twenty-seven binary orders below Int64 overflow without a hot-path
    // checked/saturating add.
    private readonly long[] m_untrustedSum = new long[(WorldBodiesLimits.LocalSeatCount * ChannelLimits.MaxChannels)];
    private readonly long[] m_trustedSum = new long[(WorldBodiesLimits.LocalSeatCount * ChannelLimits.MaxChannels)];
    // This tick's contributed HELD-device image per (seat, channel) — a non-owner's composition act (see
    // WorldAddonRuntime.Submit's HeldChannels), accumulated by WorldChannelTable.ComposeHeld's shape-aware rule: a
    // unipolar/binary channel maxes across contributors (an overlay of {0, One} bits — old ActionLanes OR, no ceiling
    // applies to it, and max is associative so arrival order cannot change the result); a BIPOLAR channel instead sums
    // RAW and UNCLAMPED here (see StageContribution) — clamping per contributor would make the result depend on
    // arrival order, so the one clamp is deferred to FoldChannelContributions, where this accumulator is finally
    // combined with the owning seat's own held value.
    private readonly long[] m_contributedHeld = new long[(WorldBodiesLimits.LocalSeatCount * ChannelLimits.MaxChannels)];
    // Per (seat, ordinal): whether THIS TICK's contribution set actually reached this channel through the UNTRUSTED
    // (pooled) path — independent of the numeric sum, which a cancelling pair of contributions can net to zero while
    // the pool was still genuinely exercised. Gates body.channels' ceiling report (FoldChannelContributions): an
    // authored ceiling nobody exercised this tick must read back as "no ceiling in force," never as the number on
    // paper. Reset per seat by ClearContribution once the fold has read it.
    private readonly ChannelHeldMask[] m_untrustedAcceptedMask = new ChannelHeldMask[WorldBodiesLimits.LocalSeatCount];
    // --- body.channels read-back (the Puck.Maths fold primitive retains none of this itself;
    // without it the verification walk could only infer a contribution's effect from displacement across ticks) ---
    // The fold accumulates and clears m_untrustedSum/m_trustedSum/m_contributedHeld above every tick (the hot path pays
    // nothing extra to KEEP them); everything below is written only at the same two sites that already write a body
    // this tick (the owning seat's direct write in ApplyIntentSubmission, and FoldChannelContributions' composed
    // write), while WorldBody retains the later held-overlay inputs/result directly on NextIntent's existing join path.
    // There is never a new tick-wide scan. NEVER cleared blind at tick start — a seat with no traffic THIS tick still
    // answers with its last settled write, exactly like m_ownerBase's own raw persistence above. Diagnostic only: read
    // by body.channels alone, off every hashed path, and never fed back into a fold (a read-back must never change
    // what it observes).
    private readonly PlayerIntent[] m_channelReadBase = new PlayerIntent[WorldBodiesLimits.LocalSeatCount];   // h
    private readonly PlayerIntent[] m_channelReadFolded = new PlayerIntent[WorldBodiesLimits.LocalSeatCount]; // what SubmitIntent received
    // Per (seat, ordinal): the pool ceiling in force for the last write that touched this channel (0 = no untrusted
    // contributor reached it; a consent row nobody exercised this write is honestly "no ceiling in force," not the
    // ceiling on paper), and whether the untrusted pool step actually bound the value (Evaluate's poolClamped output).
    private readonly long[] m_channelReadCeiling = new long[(WorldBodiesLimits.LocalSeatCount * ChannelLimits.MaxChannels)];
    private readonly bool[] m_channelReadClamped = new bool[(WorldBodiesLimits.LocalSeatCount * ChannelLimits.MaxChannels)];
    private readonly WorldPrincipal[] m_channelReadContributor = new WorldPrincipal[(WorldBodiesLimits.LocalSeatCount * MaxReadContributorsPerSeat)];
    private readonly bool[] m_channelReadContributorTrusted = new bool[(WorldBodiesLimits.LocalSeatCount * MaxReadContributorsPerSeat)];
    private readonly ChannelHeldMask[] m_channelReadContributorMask = new ChannelHeldMask[(WorldBodiesLimits.LocalSeatCount * MaxReadContributorsPerSeat)];
    private readonly int[] m_channelReadContributorCount = new int[WorldBodiesLimits.LocalSeatCount];
    // The one ordered domain for every non-intent submission — command, grant, revoke, session,
    // definition, mutation, undo, composition, lever, and query all enqueue here, never a per-kind queue. A local
    // caller (LoopbackTransport) enqueues and immediately drains inline (see EnqueueOrdered/DrainOrdered), so this
    // queue never holds more than the single in-flight envelope for loopback; it exists as the one front door a
    // future fair-merged remote submission stream drains through identically.
    // Guarded by m_authorityGate — reached from the tick thread and from socket workers' authority operations alike;
    // this is a plain Queue<T> only because EnqueueOrdered is its single door and holds that gate.
    private readonly Queue<WorldOrderedEntry> m_ordered = new();
    // The contributor rows that reached the last write, per seat, capped at MaxReadContributorsPerSeat — a
    // find-or-add slice (RecordContributor) tagging each contributing principal trusted/untrusted plus a bitmask of
    // which ordinals its delta reached, so a channel's read-back can list who touched it without a per-channel list.
    // Past the cap the read-back saturates (the same diagnostic-degrades trade ReportContention makes above) rather
    // than resizing on the contribution path.
    /// <summary>The per-seat cap on recorded contributor rows; past it the read-back saturates rather than
    /// resizing on the contribution path.</summary>
    internal const int MaxReadContributorsPerSeat = 8;
    // Per-body "the last FULLY-DRAINED tick reported this body contended" latch — the SAME once-per-episode shape as
    // m_driveDenied (checked BEFORE the current tick's outcome overwrites it, so the transition into a contended state
    // logs once, not the state itself), so two addons left permanently double-granted over one body log the collision
    // ONCE rather than flooding stderr at the 240 Hz sim rate.
    private readonly bool[] m_contended;
    // Per-body "an intent was denied last drain" latch, so a revoked driver that keeps submitting logs its loud drop
    // ONCE per denial episode (reset when an allowed intent for that body arrives) rather than once per tick.
    private readonly bool[] m_driveDenied;
    // The tick-denominated musical clock and its event-driven segment director, compiled once at construction from
    // the FIRST declared definition.Music row (a world authoring none carries neither). Stepped in Step, right
    // after m_events.Collect — see the call site's own remarks for the projection order this depends on.
    private readonly Puck.Audio.Simulation.MusicClock? m_musicClock;
    private readonly Puck.Audio.Simulation.MusicDirector? m_musicDirector;
    // A federated player's device image is replicated state, not a packet-rate-shaped impulse. One authenticated
    // intent stream owns each slot at a time and the destination republishes its latest image on every authority
    // tick until that stream changes it or disconnects. This is what makes a 30 Hz player host driving a 240 Hz
    // authority move exactly like a colocated player: missing network packets cannot masquerade as released sticks.
    // The stream lease id is server-minted; an older socket's finally block can therefore never clear a replacement
    // socket's state after a reconnect.
    private readonly WorldFederatedIntentState[] m_federatedIntents;
    private readonly EntitySnapshot[] m_snapshotEntries;
    // Reentrancy guard, guarded by m_authorityGate with m_ordered: DrainOrdered dequeues and applies until empty, so
    // a re-entrant enqueue from inside an apply is a defined no-op (re-enqueue, return to the outer drain) instead
    // of a stack-recursive double-drain. Because the gate is held across every drain, this flag is never set by one
    // thread and read by another — a drain skipped on that reading would strand an applied population change without
    // the grant rows its own queued event carries.
    private bool m_drainingOrdered;
    private ulong m_lastCompletedEngineTicks;
    private ulong m_lastCompletedTick;
    // The step width EmitSnapshot delivered the most recently completed tick's snapshot with — set alongside
    // m_lastCompletedTick at the end of Step. Exists so a primer built OUTSIDE a Step (AttachSink, at an arbitrary
    // point on the tick thread) can stamp itself with the server's actual current tick/step width rather than the
    // literal 0/0 that is only honest before the first Step has ever run — see BuildPrimerSnapshot.
    private ulong m_lastStepTicks;
    private int m_tickWrittenCount;

    private readonly WorldServer m_host;

    /// <summary>Gets the exact engine-time boundary completed by the latest authoritative step.</summary>
    internal ulong CompletedEngineTicks => m_lastCompletedEngineTicks;
    /// <summary>Gets the tick the latest authoritative step completed, or zero before the first step.</summary>
    internal ulong CompletedTick => m_lastCompletedTick;
    /// <summary>Gets the server whose document, arena, entity table, grants and narration this tick advances.</summary>
    private WorldServer Host => m_host;
    /// <summary>Gets the per-tick intent queue, drained at the step boundary after the live-edit ops.</summary>
    internal Queue<IntentSubmission> Intents => m_intents;
    /// <summary>Gets the width of the latest authoritative step, or zero before the first step.</summary>
    internal ulong LastStepTicks => m_lastStepTicks;
    /// <summary>Gets the tick-denominated musical clock, or <see langword="null"/> for a world authoring no
    /// <c>music</c> row.</summary>
    internal Puck.Audio.Simulation.MusicClock? MusicClock => m_musicClock;
    /// <summary>Gets the event-driven segment director, or <see langword="null"/> for a world authoring no
    /// <c>music</c> row.</summary>
    internal Puck.Audio.Simulation.MusicDirector? MusicDirector => m_musicDirector;
    /// <summary>Gets the one ordered domain every non-intent submission enqueues on.</summary>
    internal Queue<WorldOrderedEntry> Ordered => m_ordered;
    /// <summary>Gets the owning seat's own submission per seat, as the last write that touched a channel saw it.</summary>
    internal PlayerIntent[] ChannelReadBase => m_channelReadBase;
    /// <summary>Gets the pool ceiling in force for the last write that touched each (seat, ordinal) channel.</summary>
    internal long[] ChannelReadCeiling => m_channelReadCeiling;
    /// <summary>Gets whether the untrusted pool step actually bound each (seat, ordinal) channel's value.</summary>
    internal bool[] ChannelReadClamped => m_channelReadClamped;
    /// <summary>Gets the contributor rows that reached the last write, per seat, capped at
    /// <see cref="MaxReadContributorsPerSeat"/>.</summary>
    internal WorldPrincipal[] ChannelReadContributor => m_channelReadContributor;
    /// <summary>Gets how many contributor rows each seat's slice currently holds.</summary>
    internal int[] ChannelReadContributorCount => m_channelReadContributorCount;
    /// <summary>Gets the channel ordinals each recorded contributor's delta reached.</summary>
    internal ChannelHeldMask[] ChannelReadContributorMask => m_channelReadContributorMask;
    /// <summary>Gets whether each recorded contributor was a trusted one.</summary>
    internal bool[] ChannelReadContributorTrusted => m_channelReadContributorTrusted;
    /// <summary>Gets what the intent submission received per seat, after the fold.</summary>
    internal PlayerIntent[] ChannelReadFolded => m_channelReadFolded;
    /// <summary>Gets the per-entity snapshot scratch each completed tick's delivery fills.</summary>
    internal EntitySnapshot[] SnapshotEntries => m_snapshotEntries;
    /// <summary>Gets whether each tracked slot saw a second, different-principal write this tick.</summary>
    internal bool[] TickCollided => m_tickCollided;
    /// <summary>Gets the entity indices an allowed intent has already written this tick.</summary>
    internal int[] TickWrittenEntity => m_tickWrittenEntity;
    /// <summary>Gets the principals that wrote the matching <see cref="TickWrittenEntity"/> slot.</summary>
    internal WorldPrincipal[] TickWrittenPrincipal => m_tickWrittenPrincipal;

    /// <summary>Initializes the facade over the server it advances and the document it boots on.</summary>
    /// <param name="host">The owning server.</param>
    /// <param name="definition">The boot document, whose first <c>music</c> row compiles the clock and director.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A validated music row does not resolve.</exception>
    internal WorldTick(WorldServer host, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: host);

        var capacity = host.Population.Capacity;

        m_contended = new bool[capacity];
        m_driveDenied = new bool[capacity];
        m_federatedIntents = new WorldFederatedIntentState[capacity];
        m_host = host;
        m_snapshotEntries = new EntitySnapshot[capacity];

        if (
            (definition.Music is { Count: > 0 } music) &&
            (music[0] is { } row)
        ) {
            // The row's Source/Hash were already proven to load, canonicalize, and pin-verify by
            // WorldDefinitionValidator — this load is expected to succeed by construction.
            if (!WorldAssetRowLoader.TryLoadMusic(
                document: out var score,
                error: out var loadError,
                row: row
            )) {
                throw new InvalidOperationException(message: $"music[{row.Name}]: {loadError} (a validated document must still resolve at construction)");
            }

            var tempo = score!.Tempo;

            m_musicClock = new Puck.Audio.Simulation.MusicClock(
                beatsPerBar: (tempo.BeatsPerBar ?? 4),
                ticksPerBeat: tempo.TicksPerBeat
            );
            m_musicDirector = new Puck.Audio.Simulation.MusicDirector(graph: MusicDirectorFactory.CompileGraph(document: score));
        }
    }

    /// <summary>Restores the step clock verbatim from a checkpoint.</summary>
    /// <param name="completedEngineTicks">The captured engine-time boundary.</param>
    /// <param name="completedTick">The captured completed tick.</param>
    /// <param name="lastStepTicks">The captured step width.</param>
    internal void RestoreClock(ulong completedEngineTicks, ulong completedTick, ulong lastStepTicks) {
        m_lastCompletedEngineTicks = completedEngineTicks;
        m_lastCompletedTick = completedTick;
        m_lastStepTicks = lastStepTicks;
    }
    /// <summary>Drops every placement-deal memo and the definition the sweep last ran over, so the next sweep
    /// re-derives from the installed document.</summary>
    internal void ResetPlacementDeals() {
        m_dealMemos.Clear();
        m_dealSweptDefinition = null;
    }
    /// <summary>Adopts a widened contention-tracking trio in one step, so the three arrays can never disagree on
    /// length.</summary>
    /// <param name="collided">The new collision flags.</param>
    /// <param name="entity">The new written-entity indices.</param>
    /// <param name="principal">The new writing principals.</param>
    internal void AdoptContentionArrays(bool[] collided, int[] entity, WorldPrincipal[] principal) {
        m_tickCollided = collided;
        m_tickWrittenEntity = entity;
        m_tickWrittenPrincipal = principal;
    }
}

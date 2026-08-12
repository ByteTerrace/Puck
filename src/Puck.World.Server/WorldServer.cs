using System.Globalization;
using Puck.Hosting;
using Puck.Maths;
using Puck.Scripting;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Defines the memory-peek seam that <c>WorldAddonRuntime</c> reads through, mirroring
/// <c>Puck.Abstractions.Machines.IMachineMemoryPeek</c>'s contract. <see cref="WorldMachineHost"/>, reached via
/// <see cref="WorldServer.Machines"/>, is the only implementation; callers should not reach past this interface into
/// the concrete host.</summary>
public interface IWorldMachineMemoryPeek {
    /// <summary>Reads one byte from a screen's booted machine, or fails when the screen has no machine or the
    /// machine does not support memory peek.</summary>
    /// <param name="screen">The engine screen-surface index.</param>
    /// <param name="address">A machine-defined bus address.</param>
    /// <param name="value">The byte read, or 0 on failure.</param>
    /// <returns>Whether the read succeeded.</returns>
    bool TryPeek(int screen, int address, out byte value);
}

/// <summary>What kind of edit boundary a <see cref="WorldEditEcho"/> narrates — the class the editor HUD tags.</summary>
public enum WorldEditEchoKind {
    /// <summary>A world-document mutation that applies live on delivery (cameras included).</summary>
    Mutation,

    /// <summary>A document-defaults mutation — it changes what the next boot wakes on while the live session levers
    /// keep their values (<c>world.row.set render</c> / <c>world.row.set population</c>).</summary>
    DocumentDefaults,

    /// <summary>A grant-table change (<c>world.grant</c>/<c>world.revoke</c>) — runtime capability state, not a
    /// document edit.</summary>
    GrantTable,

    /// <summary>A live addon-runtime lifecycle change (<c>world.addon.mount</c>/<c>world.addon.unmount</c>) —
    /// runtime guest state, not a document edit.</summary>
    AddonLifecycle,

    /// <summary>A whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>) —
    /// stronger than an ordinary <see cref="Mutation"/>: every section may have moved, the journal always clears,
    /// and admitted peer connections re-mint their admission grant.</summary>
    Rebuild,

    /// <summary>A live screen-machine lifecycle change (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/
    /// <c>.options</c>/<c>.link</c>/<c>.unlink</c>) — runtime machine-host state, not a document edit.</summary>
    ScreenOp,

    /// <summary>A target-register designation outcome.</summary>
    Designation,

    /// <summary>A body motion program switch outcome (<c>player.motion</c>).</summary>
    BodyMotion,
}

/// <summary>One edit-boundary outcome echoed beside the loud stderr line — the payload of
/// <see cref="WorldServer.EchoTap"/>, so a UI surface (the overlay toast, the editor HUD's act-class tag, the drag
/// channel's frozen-preview retirement) narrates outcomes without scraping stderr.</summary>
/// <param name="Message">The human-readable outcome line (no brackets).</param>
/// <param name="Rejected">Whether the outcome is a rejection/denial.</param>
/// <param name="Kind">The edit-boundary class the outcome belongs to.</param>
/// <param name="Mutation">The mutation the outcome answers, when the boundary was a mutation — the correlation key a
/// released drag preview retires against (<c>WorldEditorDrag.NoteRejected</c>) and the at-site position source the
/// applied-cue lane derives from; <see langword="null"/> otherwise.</param>
/// <param name="Denied">Whether the rejection was a capability denial (a missing mutate grant, a refused grant
/// acquisition) rather than a validator/guard rejection — the discriminator the cue lane's <c>grant.denied</c> vs
/// <c>mutation.rejected</c> tokens ride.</param>
/// <param name="ConnectionId">The submitting envelope's connection id (<see cref="SubmissionEnvelope.LocalConnectionId"/>
/// for the local stdin/loopback connection, always <c>0</c> today) — the submitter identity a future wire-addressed
/// echo routes back to. Defaults to the local connection: replay and the addon runtime call the underlying apply
/// methods directly, with no originating envelope, which is correctly "local" for both.</param>
/// <param name="CorrelationId">The submitting envelope's correlation id, or <c>0</c> when none (see
/// <see cref="ConnectionId"/>'s own remarks for why direct callers default here).</param>
/// <param name="RebuildOrigin">For a successful <see cref="WorldEditEchoKind.Rebuild"/> outcome that replaced the
/// base (<c>world.load</c>/<c>world.reload</c>), the new origin path — the seam <c>Puck.World</c>'s composition root
/// uses to keep the console's tracked document origin (<c>world.save</c>'s default target, <c>world.status</c>'s
/// reported source, <c>world.reload</c>'s re-read target) truthful after a runtime rebuild. <see langword="null"/>
/// for every other outcome, including a successful <c>world.reset</c> (reset targets the base without moving it).</param>
public readonly record struct WorldEditEcho(string Message, bool Rejected, WorldEditEchoKind Kind, WorldMutation? Mutation = null, bool Denied = false, int ConnectionId = SubmissionEnvelope.LocalConnectionId, long CorrelationId = 0, string? RebuildOrigin = null);

/// <summary>
/// The authoritative world server — one logical instance owning the live <see cref="WorldDefinition"/>, the entity
/// table (<see cref="WorldPopulation"/>), the profile catalog, and the mutation journal. Every non-intent submission
/// (command/grant/revoke/session/definition/mutation/undo/composition/lever/query) arrives as one
/// <see cref="SubmissionEnvelope"/> through <see cref="Submit"/> — the server's single ordered domain, never split by
/// kind. Command/grant/revoke/session/composition/lever/query still apply synchronously at submit exactly as before
/// (the host guarantees submissions arrive inside the command-apply window immediately preceding the tick's
/// <see cref="Step"/>, so every one lands before that tick's advance in stdin FIFO order — a grant a script submits
/// immediately before a command is guaranteed visible to it). Live world edits — mutations, definition swaps, and
/// journal undo — still buffer and drain at <see cref="Step"/>, before the intent drain, so they stay tick-aligned:
/// the envelope reshape unifies how every kind is submitted, not when a buffered kind applies. No submission returns a
/// value directly; every envelope resolves to a typed <see cref="WorldSubmissionResult"/> (an inline callback for a
/// local caller, fired before <see cref="Submit"/> returns). Per-tick intents buffer separately (their own queue, not
/// the ordered domain) and drain at <see cref="Step"/> too, which then advances every body and pushes the tick's
/// <see cref="WorldSnapshot"/> — plus, in any step that applied at least one edit, the new definition — to every
/// subscriber of the <see cref="WorldOutputHub"/>.
/// </summary>
/// <remarks>Ordinary work is single-threaded on the host tick: submissions arrive during the command
/// pump's apply window and <see cref="Step"/> runs immediately after, both on the launcher's window-pump thread.
/// Authenticated federation operations use <see cref="ExecuteAuthorityOperation{T}"/> so cyclic cross-host traffic
/// can settle without racing that fold or waiting for another authority's next tick. The
/// journal is the undo engine: the loaded base definition plus an append-only list of applied <see cref="WorldMutation"/>s;
/// undo restores the base and deterministically replays the journal minus its tail through the same apply path — no
/// per-mutation inverse logic is ever written.</remarks>
public sealed class WorldServer : IWorldServerHost {
    // Federation reserve/commit requests arrive on socket workers. They must be able to settle while this host's
    // simulation thread waits on a different authority, otherwise simultaneous cyclic crossings deadlock.
    private readonly object m_authorityGate = new();
    private readonly WorldPopulation m_population;
    private readonly WorldInputHoldRuntime m_inputHold;
    private readonly WorldOwnedWorlds m_profiles;
    private readonly WorldTransferEscrow m_transferEscrow;
    private WorldDocumentSubmissionReceipt? m_lastDocumentReceipt;
    private readonly WorldRenderEnvelope m_envelope;
    private readonly Queue<IntentSubmission> m_intents = new();
    // A federated player's device image is replicated state, not a packet-rate-shaped impulse. One authenticated
    // intent stream owns each slot at a time and the destination republishes its latest image on every authority
    // tick until that stream changes it or disconnects. This is what makes a 30 Hz player host driving a 240 Hz
    // authority move exactly like a colocated player: missing network packets cannot masquerade as released sticks.
    // The stream lease id is server-minted; an older socket's finally block can therefore never clear a replacement
    // socket's state after a reconnect.
    private readonly FederatedIntentState[] m_federatedIntents;
    // The buffered live-edit ops (mutations, whole-document swaps, journal undo), drained FIFO at the step boundary
    // BEFORE intents. New allocation lives here, at the mutation boundary; an idle tick pays one empty-queue check.
    private readonly Queue<PendingOp> m_pending = new();
    // The mutation journal — the undo engine. m_base is the loaded base definition (reset by a swap or world.save
    // compaction); m_journal is the append-only edit history over it. dirty == m_journal.Count.
    private readonly List<JournalEntry> m_journal = new();
    // The ONE capability table — every write boundary checks it. Seeded permissive for local play (see WorldGrants).
    private readonly WorldGrants m_grants;
    // The per-tick dispatch tally behind TryAdmitMutation's budget gate — ONE meter for every untrusted ingress (a
    // mounted addon's guest and a remote peer alike), opened at the top of Step.
    private readonly WorldMutationBudgetMeter m_mutationBudget = new();
    // Per-body "an intent was denied last drain" latch, so a revoked driver that keeps submitting logs its loud drop
    // ONCE per denial episode (reset when an allowed intent for that body arrives) rather than once per tick.
    private readonly bool[] m_driveDenied;
    // The (entity, principal) pairs an ALLOWED intent has already written THIS TICK — the seat drain plus every mounted
    // addon's contributions. Sized at AttachAddons to the local-seat lane count (Client.WorldClient.SubmitSeatIntents
    // produces one per live roster slot) plus two per mounted addon, which covers the ordinary case of a guest driving
    // one or two granted bodies. It is a BOUND on distinct entities tracked for one tick, not a correctness invariant:
    // a guest holding Drive over more bodies than that saturates the tracking, and the defensive length check in
    // ReportContention is what makes saturation degrade contention REPORTING rather than break anything — deliberately,
    // because the alternative is a per-tick resize on the hot path to improve a diagnostic. A second submission naming
    // an entity already written this tick by a DIFFERENT principal is a genuine conflict between two distinct Drive
    // grants over one body — Step reports it loudly rather than letting the later one silently overwrite the earlier.
    private int[] m_tickWrittenEntity = new int[WorldPopulation.LocalSeatCount];
    private WorldPrincipal[] m_tickWrittenPrincipal = new WorldPrincipal[WorldPopulation.LocalSeatCount];
    // Whether the matching m_tickWrittenEntity slot saw a SECOND, different-principal write THIS tick — read once the
    // whole drain AND the addon contributions have finished (see Step) to settle m_contended for real, since which
    // submission a queue happens to dequeue first says nothing about whether the body was genuinely contended for the
    // tick as a whole.
    private bool[] m_tickCollided = new bool[WorldPopulation.LocalSeatCount];
    private int m_tickWrittenCount;
    // Per-body "the last FULLY-DRAINED tick reported this body contended" latch — the SAME once-per-episode shape as
    // m_driveDenied (checked BEFORE the current tick's outcome overwrites it, so the transition into a contended state
    // logs once, not the state itself), so two addons left permanently double-granted over one body log the collision
    // ONCE rather than flooding stderr at the 240 Hz sim rate.
    private readonly bool[] m_contended;
    private readonly EntitySnapshot[] m_snapshotEntries;
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
    private readonly PlayerIntent[] m_ownerBase = new PlayerIntent[WorldPopulation.LocalSeatCount];
    private readonly PlayerIntent[] m_ownerHeld = new PlayerIntent[WorldPopulation.LocalSeatCount];
    private readonly bool[] m_hasOwnerBase = new bool[WorldPopulation.LocalSeatCount];
    private readonly bool[] m_hasContribution = new bool[WorldPopulation.LocalSeatCount];
    // Every staged delta was already bounded to |d| <= One at the pump. FixedContributionFold records the exact
    // generic Int64 accumulator boundary; World's concrete set is far smaller: untrusted terms are mounted Wasmtime
    // instances (memory exhausts around 2^20), while trusted co-driving seats number at most LocalSeatCount - 1.
    // Timed presses still bypass this fold and collapse to one timer per channel in WorldBody.PressChannel, so they
    // do not enter either sum today. With One = 2^16 (FixedQ4816) and at most ~2^20 untrusted terms, a completed sum
    // peaks near 2^36, so it stays roughly twenty-seven binary orders below Int64 overflow without a hot-path
    // checked/saturating add.
    private readonly long[] m_untrustedSum = new long[WorldPopulation.LocalSeatCount * ChannelLimits.MaxChannels];
    private readonly long[] m_trustedSum = new long[WorldPopulation.LocalSeatCount * ChannelLimits.MaxChannels];
    // This tick's contributed HELD-device image per (seat, channel) — a non-owner's composition act (see
    // WorldAddonRuntime.Submit's HeldChannels), accumulated by WorldChannelTable.ComposeHeld's shape-aware rule: a
    // unipolar/binary channel maxes across contributors (an overlay of {0, One} bits — old ActionLanes OR, no ceiling
    // applies to it, and max is associative so arrival order cannot change the result); a BIPOLAR channel instead sums
    // RAW and UNCLAMPED here (see StageContribution) — clamping per contributor would make the result depend on
    // arrival order, so the one clamp is deferred to FoldChannelContributions, where this accumulator is finally
    // combined with the owning seat's own held value.
    private readonly long[] m_contributedHeld = new long[WorldPopulation.LocalSeatCount * ChannelLimits.MaxChannels];
    // Per (seat, ordinal): whether THIS TICK's contribution set actually reached this channel through the UNTRUSTED
    // (pooled) path — independent of the numeric sum, which a cancelling pair of contributions can net to zero while
    // the pool was still genuinely exercised. Gates player.channels' ceiling report (FoldChannelContributions): an
    // authored ceiling nobody exercised this tick must read back as "no ceiling in force," never as the number on
    // paper. Reset per seat by ClearContribution once the fold has read it.
    private readonly ChannelHeldMask[] m_untrustedAcceptedMask = new ChannelHeldMask[WorldPopulation.LocalSeatCount];
    // --- player.channels read-back (the Puck.Maths fold primitive retains none of this itself;
    // without it the verification walk could only infer a contribution's effect from displacement across ticks) ---
    // The fold accumulates and clears m_untrustedSum/m_trustedSum/m_contributedHeld above every tick (the hot path pays
    // nothing extra to KEEP them); everything below is written only at the same two sites that already write a body
    // this tick (the owning seat's direct write in ApplyIntentSubmission, and FoldChannelContributions' composed
    // write), while WorldBody retains the later held-overlay inputs/result directly on NextIntent's existing join path.
    // There is never a new tick-wide scan. NEVER cleared blind at tick start — a seat with no traffic THIS tick still
    // answers with its last settled write, exactly like m_ownerBase's own raw persistence above. Diagnostic only: read
    // by player.channels alone, off every hashed path, and never fed back into a fold (a read-back must never change
    // what it observes).
    private readonly PlayerIntent[] m_channelReadBase = new PlayerIntent[WorldPopulation.LocalSeatCount];   // h
    private readonly PlayerIntent[] m_channelReadFolded = new PlayerIntent[WorldPopulation.LocalSeatCount]; // what SubmitIntent received
    // Per (seat, ordinal): the pool ceiling in force for the last write that touched this channel (0 = no untrusted
    // contributor reached it; a consent row nobody exercised this write is honestly "no ceiling in force," not the
    // ceiling on paper), and whether the untrusted pool step actually bound the value (Evaluate's poolClamped output).
    private readonly long[] m_channelReadCeiling = new long[WorldPopulation.LocalSeatCount * ChannelLimits.MaxChannels];
    private readonly bool[] m_channelReadClamped = new bool[WorldPopulation.LocalSeatCount * ChannelLimits.MaxChannels];
    // The contributor rows that reached the last write, per seat, capped at MaxReadContributorsPerSeat — a
    // find-or-add slice (RecordContributor) tagging each contributing principal trusted/untrusted plus a bitmask of
    // which ordinals its delta reached, so a channel's read-back can list who touched it without a per-channel list.
    // Past the cap the read-back saturates (the same diagnostic-degrades trade ReportContention makes above) rather
    // than resizing on the contribution path.
    private const int MaxReadContributorsPerSeat = 8;
    private readonly WorldPrincipal[] m_channelReadContributor = new WorldPrincipal[WorldPopulation.LocalSeatCount * MaxReadContributorsPerSeat];
    private readonly bool[] m_channelReadContributorTrusted = new bool[WorldPopulation.LocalSeatCount * MaxReadContributorsPerSeat];
    private readonly ChannelHeldMask[] m_channelReadContributorMask = new ChannelHeldMask[WorldPopulation.LocalSeatCount * MaxReadContributorsPerSeat];
    private readonly int[] m_channelReadContributorCount = new int[WorldPopulation.LocalSeatCount];
    private WorldDefinition m_definition;
    private WorldDefinition m_base;
    // A human-readable description of m_base's origin — world.reset's read-back rule ("the completion echo names
    // what was reset to"). Set at construction (the boot document), replaced by Compact (world.save — "the last
    // world.save") and by ApplyRebuild's Load/Reload arm (a new base replaces the old one, exactly like a swap
    // always has). Reset itself never writes this: reset targets the base WITHOUT moving it.
    private string m_baseOrigin = "the boot document";
    // The multi-subscriber output hub — supports a local sink plus N future connections. See WorldOutputHub's own remarks.
    private readonly WorldOutputHub m_output = new();
    // The ONE ordered domain for every non-intent submission — command, grant, revoke, session,
    // definition, mutation, undo, composition, lever, and query all enqueue here, never a per-kind queue. A local
    // caller (LoopbackTransport) enqueues and immediately drains inline (see Submit/DrainOrdered), so this queue
    // never holds more than the single in-flight envelope for loopback; it exists as the ONE front door a future
    // fair-merged remote submission stream drains through identically.
    private readonly Queue<OrderedEntry> m_ordered = new();
    // Reentrancy guard: DrainOrdered dequeues and applies until empty; nothing today re-enters Submit from inside an
    // apply, but the guard makes that a defined no-op (re-enqueue, return to the outer drain) instead of a
    // stack-recursive double-drain if a future caller ever does.
    private bool m_drainingOrdered;
    // The mounted Simulation-lane guests, attached once at composition (see AttachAddons) and pumped at the three
    // pinned points of Step. Null until then, and null for the whole life of a server nobody mounts addons into (the
    // offline replay re-drive attaches its own).
    private WorldAddonRuntime? m_addons;
    // The live SDF contact field under the FIELD provider (null under analytic) — the server OWNS this
    // provider's lifecycle: it is built ONCE at apply time (for its loud excluded-op rejection) and handed to the
    // population's rebuild, so a body's first step after a solid edit already solves against the new field. Adopted at
    // construction from the population's boot build so it is never compiled twice for one boundary.
    private WorldSolidField? m_solids;
    // The solid-field revision — bumped each time m_solids is rebuilt (a solid-affecting edit under the field provider),
    // the world.collision.status read-back. Starts at 1 when the boot world uses the field provider, else 0.
    private int m_solidRevision;
    // The engagement fold — the seat/peer→screen route decision, its per-tick pad fold, and the
    // screen-removal admin cleanup. Assigned in the constructor (not a field initializer: it needs the constructor's
    // own population/definition parameters), never rebuilt afterward — channels are boot-fixed, so its compiled
    // per-screen translation tables never go stale.
    private readonly WorldEngagement m_engagement;
    // The world-scoped event feed (four of the five senses-lane families — see WorldEventFeed's own remarks for why
    // the fifth, machine-memory watches, is addon-scoped instead). Collected once per Step, after the population
    // advances; drained by WorldAddonRuntime.ResolveReads the same tick.
    private readonly WorldEventFeed m_events;
    // The authoritative screen-machine host — a PEER singleton
    // assigned in the constructor, never owned/disposed here (see WorldMachineHost's own remarks).
    private readonly WorldMachineHost m_machines;
    // The compiled `rules` section, rebuilt on every Install (and once at construction, which never calls Install)
    // from the SAME WorldRuleCompiler path the validator already ran over this candidate — so this call is trusted
    // never to throw. Recomputed unconditionally: rules and state rows are both small-capacity sections, so there is
    // no AffectsRules classification predicate earning its keep here.
    private CompiledWorldRule[] m_rules = [];
    // The EDGE latch, keyed by rule name and deliberately OUTSIDE m_rules: a rule's own effect installs a new
    // definition, which recompiles m_rules, so a latch living in the compiled record would clear itself every time it
    // fired — which is exactly the 503-entries-in-500-ticks shape edge mode exists to close. Surviving names keep
    // their bit across an install; vanished names are dropped.
    private readonly Dictionary<string, bool> m_ruleGateHeld = new(comparer: StringComparer.Ordinal);
    // The compiled `interactions` section — a SECOND compiled array, evaluated after m_rules (see
    // EvaluateWorldRules), never merged into it: an interaction desugars into a synthesized WorldRule and rides the
    // SAME per-rule evaluation, but interactions occupy their OWN name namespace (WorldInteraction.Name), so a
    // shared latch dictionary would risk aliasing a rule and an interaction that happen to share a name.
    private CompiledWorldRule[] m_interactions = [];
    // The interaction family's own EDGE latch — the SAME shape m_ruleGateHeld is, kept separate for the identical
    // aliasing reason m_interactions itself is kept separate from m_rules.
    private readonly Dictionary<string, bool> m_interactionGateHeld = new(comparer: StringComparer.Ordinal);
    // Reused scratch for the despawn-ownership guard (FireWorldRuleEffect's RemovePlacement arm) — rule-fire cadence
    // only, cleared and refilled on every check rather than allocated per firing.
    private readonly List<int> m_ruleInhabitantScratch = [];
    private ulong m_lastCompletedTick;
    // The step width EmitSnapshot delivered the most recently completed tick's snapshot with — set alongside
    // m_lastCompletedTick at the end of Step. Exists so a primer built OUTSIDE a Step (AttachSink, at an arbitrary
    // point on the tick thread) can stamp itself with the server's actual current tick/step width rather than the
    // literal 0/0 that is only honest before the first Step has ever run — see BuildPrimerSnapshot.
    private ulong m_lastStepTicks;
    private ulong m_lastCompletedEngineTicks;

    /// <summary>Gets the optional host sink for the player-keyed durable writes emitted by each completed tick.</summary>
    public Action<IReadOnlyList<DurableStateOutput>>? DurableStateOutputTap { get; set; }
    /// <summary>Observes each visiting-world durable-state verdict for a tape.</summary>
    public Action<WorldDocumentSubmissionReceipt>? DocumentSubmissionTap { get; set; }

    /// <summary>Gets the tick a durable input submitted during the current command window must name.</summary>
    public ulong NextInputTick => (m_lastCompletedTick + 1UL);

    /// <summary>Gets the exact engine-time boundary completed by the latest authoritative step.</summary>
    public ulong CompletedEngineTicks => m_lastCompletedEngineTicks;

    /// <summary>Gets the width of the latest authoritative step, or zero before the first step.</summary>
    public ulong LastStepTicks => m_lastStepTicks;

    /// <summary>Reserves destination body indices under a binding transfer lease. The same method backs loopback
    /// colocation and the TCP authority door; callers never reserve population capacity by inspecting it directly.</summary>
    /// <param name="request">The source-tick deadline, border policy, and prospective travelers.</param>
    /// <returns>The destination's verdict and assigned body indices.</returns>
    public WorldTransferReservationReply ReserveTransfer(WorldTransferReservationRequest request) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Reserve(request: request));

    /// <summary>Commits detached bodies into a live reservation. A repeated committed id is idempotently accepted;
    /// an expired or absent reservation is refused.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    /// <param name="members">The travelers in reservation order.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns>Whether the commit is authoritative at this destination.</returns>
    public bool CommitTransfer(string sourceAuthority, ulong transferId, IReadOnlyList<WorldTransferCommitMember> members, out string reason) {
        var resolvedReason = string.Empty;
        var accepted = ExecuteAuthorityOperation(operation: () => m_transferEscrow.Commit(sourceAuthority: sourceAuthority, transferId: transferId, members: members, reason: out resolvedReason));
        reason = resolvedReason;
        return accepted;
    }

    /// <summary>Releases a reservation before commit. A destination that already committed ignores the abort.</summary>
    /// <param name="sourceAuthority">The authenticated namespace that minted the transfer id.</param>
    /// <param name="transferId">The source-minted transfer id.</param>
    public void AbortTransfer(string sourceAuthority, ulong transferId) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Abort(sourceAuthority: sourceAuthority, transferId: transferId));

    /// <summary>Retires the acknowledged transaction while preserving stable mobility replay protection.</summary>
    public void AcknowledgeTransfer(string sourceAuthority, ulong transferId) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Acknowledge(sourceAuthority: sourceAuthority, transferId: transferId));

    /// <summary>Resolves the ordinary peer principal a committed federated transfer assigned.</summary>
    public bool TryTransferredPrincipal(string sourceAuthority, ulong transferId, int ordinal, out WorldPrincipal principal) {
        var resolved = default(WorldPrincipal);
        var found = ExecuteAuthorityOperation(operation: () => m_transferEscrow.TryCommittedPrincipal(sourceAuthority: sourceAuthority, transferId: transferId, ordinal: ordinal, principal: out resolved));
        principal = resolved;
        return found;
    }

    /// <summary>Resolves a stable incarnation/epoch credential without retaining its disposable transfer id.</summary>
    public bool TryTransferredPrincipal(string sourceAuthority, in WorldMobilityIdentity mobility, out WorldPrincipal principal) {
        var resolved = default(WorldPrincipal);
        var credential = mobility;
        var found = ExecuteAuthorityOperation(operation: () => m_transferEscrow.TryMobilityPrincipal(sourceAuthority: sourceAuthority, mobility: in credential, principal: out resolved));
        principal = resolved;
        return found;
    }

    /// <summary>Terminally retires a traveler incarnation after its accepted leave has propagated through this hop.</summary>
    public void RetireTransferredMobility(in WorldMobilityIdentity mobility) {
        var credential = mobility;
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.RetireMobility(mobility: in credential));
    }

    /// <summary>Returns the destination's idempotent view of a source-scoped transfer.</summary>
    public WorldTransferStatus TransferStatus(string sourceAuthority, ulong transferId) =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Status(sourceAuthority: sourceAuthority, transferId: transferId));

    /// <summary>Reads bounded transfer transaction, mobility credential, and in-flight mobility-lease counts.</summary>
    public WorldTransferTableCounts TransferTableCounts =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Counts);

    /// <summary>Reads the authenticated source-border identity for an active escrow-arrived body. Callers already
    /// under the authority operation gate use this to apply reciprocal adjacency hysteresis.</summary>
    public bool TryTransferArrivalBorder(int bodyIndex, out string border) =>
        m_transferEscrow.TryArrivalBorder(bodyIndex: bodyIndex, border: out border);

    /// <summary>Clears one exact authenticated arrival-border latch after reciprocal hysteresis is satisfied.
    /// Callers already execute under the authority operation gate.</summary>
    public bool ClearTransferArrivalBorder(int bodyIndex, string expectedBorder) =>
        m_transferEscrow.ClearArrivalBorder(bodyIndex: bodyIndex, expectedBorder: expectedBorder);

    /// <summary>Executes a narrow authority operation without racing the fixed-step population fold.</summary>
    public T ExecuteAuthorityOperation<T>(Func<T> operation) {
        ArgumentNullException.ThrowIfNull(operation);
        lock (m_authorityGate) { return operation(); }
    }

    /// <summary>Executes a void authority operation under the same gate.</summary>
    public void ExecuteAuthorityOperation(Action operation) {
        ArgumentNullException.ThrowIfNull(operation);
        lock (m_authorityGate) { operation(); }
    }

    /// <summary>Gets this server's own running-instance identity — the draw seed ladder's instance rung (see
    /// <c>WorldGeneratorEngine.ComputeSeedState</c>). A live redraw folds the same value the boot/first-fill resolver
    /// used, so a site's first fill and its later redraws share one deterministic stream per instance.</summary>
    public string InstanceIdentity { get; }

    /// <summary>Gets the namespace used by durable entity addresses emitted by this authority. A federated
    /// authority uses its declared network identity so two processes whose local instance is named <c>boot</c>
    /// cannot publish colliding addresses; a loopback-only authority uses its process-local instance identity.</summary>
    public string AuthorityIdentity { get; }

    /// <summary>Gets the durable writes emitted by the most recently completed tick.</summary>
    public IReadOnlyList<DurableStateOutput> DurableStateOutputs => m_population.DurableStateOutputs;

    /// <summary>Initializes a new instance of the <see cref="WorldServer"/> class over the world it authoritatively owns.</summary>
    /// <param name="definition">The loaded world definition (the initial live definition and journal base).</param>
    /// <param name="population">The entity table (all bodies, seats included).</param>
    /// <param name="profiles">The profile catalog.</param>
    /// <param name="envelope">The render-capacity oracle a scene/screen mutation is checked against at apply time.</param>
    /// <param name="machines">The authoritative screen-machine host (owns every booted <c>IScreenMachine</c>) — a
    /// peer singleton, not a private field this constructor builds, so the composition root disposes it (see
    /// <see cref="WorldMachineHost"/>'s own remarks on why).</param>
    /// <param name="instanceIdentity">This server's own running-instance identity — the draw seed ladder's instance
    /// rung (see <c>WorldGeneratorEngine.ComputeSeedState</c> in <c>Puck.World.Data</c>). Defaults to the boot
    /// instance's own constant name (<c>Puck.World.WorldInstanceHost.BootInstanceName</c>, not referenced directly —
    /// this project sits below <c>Puck.World</c> in the layering).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="instanceIdentity"/> is empty.</exception>
    public WorldServer(WorldDefinition definition, WorldPopulation population, WorldOwnedWorlds profiles, WorldRenderEnvelope envelope, WorldMachineHost machines, string instanceIdentity = "boot") {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: envelope);
        ArgumentNullException.ThrowIfNull(argument: machines);
        ArgumentException.ThrowIfNullOrEmpty(argument: instanceIdentity);

        InstanceIdentity = instanceIdentity;
        AuthorityIdentity = (definition.Host.Authority is { Length: > 0 } authority) ? authority : instanceIdentity;
        BootDerivedFaceScreens = definition.Authoring.DerivedFaceScreens;
        m_machines = machines;
        m_driveDenied = new bool[population.Capacity];
        m_contended = new bool[population.Capacity];
        m_federatedIntents = new FederatedIntentState[population.Capacity];
        m_snapshotEntries = new EntitySnapshot[population.Capacity];
        m_events = new WorldEventFeed();
        m_grants = new WorldGrants(seatCount: WorldPopulation.LocalSeatCount, population: population.Capacity, routeTransition: QueueRouteTransition);
        // The group+membership+ownership substrate's own sync, run here (BEFORE the document's shipped grants
        // replay below — a document-authored row naming a group: principal needs the group table settled first)
        // since Install never runs at construction, same reasoning as the RecompileRules/ReconcileLinks calls at the
        // end of this ctor. The drive-gate index (Seam A) syncs alongside it for the identical reason — a boot
        // document may ship an already-gated body (a scenario opening on a downed NPC), and the first Step's intent
        // drain must see it without waiting for a mutation to trigger Install.
        m_grants.SyncGroups(groups: (definition.Groups ?? WorldGroupsSection.Empty).Groups, kinds: (definition.Groups ?? WorldGroupsSection.Empty).Kinds, ownership: (definition.Groups ?? WorldGroupsSection.Empty).Ownership);
        m_grants.SyncState(definition: definition);

        // THE BOOT-LOUD CATALOG CHECK: WorldServer is constructed exactly once per world boot (or per replay
        // rehydration), so this is the "at startup" hook the kind catalog's own remarks call for — a broken catalog
        // (a duplicate or out-of-range ordinal, a kind missing its attribute) throws HERE, before any session starts,
        // rather than surfacing lazily the first time something reads it.
        WorldMutationKindCatalog.Validate();

        m_definition = definition;
        m_base = definition;
        m_population = population;
        m_inputHold = new WorldInputHoldRuntime(settings: definition.CompiledInputHold, capacity: population.Capacity);
        m_profiles = profiles;
        m_transferEscrow = new WorldTransferEscrow(server: this);
        m_envelope = envelope;
        // Adopt the population's boot-built field (the field provider compiled it once for the bodies it minted at
        // construction) — the server owns it from here without a second build.
        m_solids = population.SolidField;
        m_solidRevision = ((m_solids is null) ? 0 : 1);
        // The engagement fold — over the population and THIS server's own grant table (m_grants was assigned earlier
        // in this constructor body, at the WorldGrants construction above). Never rebuilt: channels are boot-fixed.
        m_engagement = new WorldEngagement(population: population, grants: m_grants, definition: definition);
        // Join the bodies the boot definition's inhabited placements declare into free peer slots (the population
        // constructor activates nothing — the boot census is zero, the whole peer slice is free). Every later Install
        // re-runs this after Rebuild.
        var admittedAtBoot = new List<WorldPeerEventEntry>();
        var disconnectedAtBoot = new List<WorldPeerEventEntry>();

        m_population.ReconcileInhabitants(definition: definition, admitted: admittedAtBoot, disconnected: disconnectedAtBoot);
        ApplyLifecycleEvents(admitted: admittedAtBoot, disconnected: disconnectedAtBoot, ordered: false);

        // The document's SHIPPED grants: applied AFTER the permissive seed, in document order, through the identical Grant() path
        // world.grant submits through — so an illegitimate or conflicting authored row prints the same loud accept/
        // reject line an operator would see typing it, rather than being silently seated or silently dropped. Empty
        // (every world authored before this section existed) prints nothing and changes nothing. Applied BEFORE any
        // addon mounts (WorldAddonRuntime.Create runs after this constructor returns), so a mount-time requested-vs-
        // granted report already sees the settled table.
        foreach (var grant in definition.Grants) {
            if (IsDocumentChannelRow(grant: grant)) {
                continue;
            }

            Grant(grant: WithoutAuthoredConsent(grant: grant), actor: WorldPrincipal.Console);
        }

        // Establish the boot document's OWN declared cable links: Install (which ALSO calls this on every later
        // mutation/rebuild) never runs at construction, so a links row authored in the boot document itself needs
        // this one extra call here, or it would never establish until the first live edit touched ANY section —
        // headless included, since nothing presentation-side ever called it either.
        m_machines.ReconcileLinks(links: definition.Links);

        // Same reasoning as the cable links above: a rules row authored in the BOOT document needs its own compile
        // call here, since Install never runs at construction.
        RecompileRules(definition: definition);
    }

    private void QueueRouteTransition(WorldPrincipal principal, GrantSubject? previous, GrantSubject? current) {
        var sourceBody = principal.Kind switch {
            PrincipalKind.Seat => principal.Index,
            PrincipalKind.Peer => principal.Index,
            _ => -1,
        };

        if (sourceBody < 0) {
            return;
        }

        if (previous is { } disengaged) {
            m_events.QueueRouteDisengaged(sourceBody: sourceBody, target: disengaged);
        }

        if (current is { } engaged) {
            m_events.QueueRouteEngaged(sourceBody: sourceBody, target: engaged);
        }
    }

    // THE BOOT CONSENT WITHHOLDING. A document row is applied under the CONSOLE principal, which
    // HoldsForAdministration exempts unconditionally — so without
    // this, the narrowing that stops a Seat from authoring a grant over anyone else's body would close only the live
    // verb and leave the document path wide open: a shipped row could hand an addon a pooled reach over a body its
    // human never consented to, and the human would inherit it the moment they sat down (occupancy at boot proves
    // nothing — no seat is active yet when this runs).
    //
    // The rule chosen is ADMIT THE ROW, WITHHOLD THE CONSENT: the ceiling — the number that is the consent, and the
    // only thing that lets an untrusted contribution move a human's body at all — never comes from a document. A
    // contributor's REACH mask still does, so a world can pre-wire an addon exactly as before; it simply contributes
    // nothing until a seat authors a ceiling live. Refusing the whole row instead would have cost that pre-wiring for
    // no additional safety, since a reach with no ceiling already folds nothing.
    //
    // The withholding is LOUD: a silently-narrowed row would read, in world.grants, as a document that never asked.
    private static WorldGrant WithoutAuthoredConsent(WorldGrant grant) {
        if (grant.Ceiling is null) {
            return grant;
        }

        Console.Error.WriteLine(value: $"[world.grant: {grant.Principal.Describe()} drive {grant.Subject.Describe()} — the document's ceiling is WITHHELD (a pooled ceiling is consent, and consent is authored live by the seated human on its own body, never shipped in a world document); the row applies with no pool]");

        // The mask travels with the ceiling on a seat's own gesture and means nothing without it, so both go.
        return (grant with { Reach = null, Consent = null, Ceiling = null });
    }

    // Whether a document-authored `grants` row belongs to the CROSS-DOCUMENT write-back channel rather than to the
    // live table — a `document:<id>` principal, whose capability Server.WorldOwnedWorlds.Decide and
    // TryReadDurableState resolve by reading the OWNER'S DOCUMENT directly. Both replays (the constructor's and the
    // rebuild's) skip these rather than handing them to Grant: the grant table refuses them BY NAME (WorldGrants
    // .Conflicts rule (-1b) — a live row for one is budget-less, mask-less, and read by nothing), so replaying them
    // would print a loud rejection for data the document is CORRECT to carry. Skipping is not hiding them: they are
    // echoed by `world.grants` as document-authored rows, which is where they actually live and act.
    private static bool IsDocumentChannelRow(WorldGrant grant) => (grant.Principal.Kind == PrincipalKind.Document);

    /// <summary>Gets the live world definition this server runs — swapped in place as buffered edits apply.</summary>
    public WorldDefinition Definition => m_definition;

    /// <summary>Gets the entity table this server advances.</summary>
    public WorldPopulation Population => m_population;

    /// <summary>Gets this instance's own render-capacity oracle — configured by whatever presentation-side content
    /// source renders this instance (the boot world's own <c>WorldFrameSource</c>, or an observing destination's
    /// session or continuum view), so a document mutation the same instance receives is checked against the same
    /// probed floor a renderer already committed to. Unconfigured (nothing renders this instance yet) reads as
    /// "fits" — <see cref="WorldRenderEnvelope"/>'s own documented default.</summary>
    public WorldRenderEnvelope Envelope => m_envelope;

    /// <summary>Gets the derived-face screen slots this instance's boot document reserved. The presentation binder
    /// registers exactly that band up front and the render provider key set is frozen there, so a live edit may lower
    /// <see cref="WorldAuthoringDefaults.DerivedFaceScreens"/> but never raise it past this — a raise is refused by
    /// name, in the same family as the boot-allocated population capacity, rather than seating faces at indices no
    /// renderer holds.</summary>
    public int BootDerivedFaceScreens { get; }

    // The boot-frozen derived-face reservation gate, shared by the mutation and rebuild apply paths so the two can
    // never disagree about what the binder can actually show.
    private bool ExceedsBootDerivedFaceReservation(WorldDefinition candidate, out string reason) {
        if (candidate.Authoring.DerivedFaceScreens <= BootDerivedFaceScreens) {
            reason = string.Empty;

            return false;
        }

        reason = $"authoring.derivedFaceScreens {candidate.Authoring.DerivedFaceScreens} exceeds the boot-reserved {BootDerivedFaceScreens} derived-face screen slot(s); the binder registers that band once at boot and the render provider key set is frozen there, so restart the host to load a wider one";

        return true;
    }

    /// <summary>Observes server-authored ordered events after they take effect. The replay tape attaches only while
    /// armed; clients never receive this submission-only seam.</summary>
    public Action<WorldServerEvent>? ServerEventTap { get; set; }

    /// <summary>Observes a whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>)
    /// once <see cref="ApplyRebuild"/> has resolved its candidate and computed its CAS content hash, but before any
    /// refusal gate (grant check, dirty-journal guard, validate, capacity, solids) runs — so a rebuild the door goes
    /// on to refuse is still taped and reproduces as the identical refusal on replay, matching
    /// <see cref="ServerEventTap"/>/<c>LoopbackTransport</c>'s own taps. Apply-time, not submission-time, because
    /// Reset's hash (the base's own canonical bytes) is only knowable once <see cref="ApplyRebuild"/> actually reads
    /// <c>m_base</c> — a value private to this server that can move between submission and drain (see
    /// <see cref="EnqueueRebuild"/>'s remarks). The replay tape attaches only while armed; clients never receive this
    /// submission-only seam.</summary>
    public Action<WorldRebuildRequest, WorldPrincipal, string>? RebuildTap { get; set; }

    /// <summary>Gets the profile catalog (the routed store persists through it).</summary>
    public WorldOwnedWorlds Profiles => m_profiles;

    /// <summary>Gets the capability table's <see cref="IWorldGrantsView"/> — the one grant primitive the engagement view, the
    /// addon runtime, and the grant/mutation command modules read (plus the two engagement-route writes the view
    /// carries). Reads are loopback-local today; a socket transport moves grant changes onto the wire. Deliberately not
    /// the concrete <see cref="WorldGrants"/>: its <see cref="WorldGrants.TryGrant"/>/<see cref="WorldGrants.Revoke"/>
    /// authority doors stay reachable only through <see cref="Grant"/>/<see cref="Revoke"/> below, which run the actor
    /// check those two methods do not — a caller that only holds this property can never skip it.</summary>
    public IWorldGrantsView Grants => m_grants;

    /// <summary>Returns the concrete grant rows held by one principal. Transfer rollback captures these before a
    /// federated peer generation leaves so an aborted onward handoff can restore the exact source authority.</summary>
    public IReadOnlyList<WorldGrant> GrantRows(WorldPrincipal principal) => m_grants.Rows(principal: principal);

    /// <summary>Gets the engagement fold (headless design §1.8) — the seat/peer→screen route decision
    /// (<see cref="WorldCommand.Engage"/>/<see cref="WorldCommand.Disengage"/> apply through it, from
    /// <see cref="ApplyCommand"/>), its per-tick pad fold (<see cref="Server.WorldEngagement.FoldTick"/>, folded into
    /// every <see cref="WorldSnapshot"/>), and the screen-removal admin cleanup
    /// (<c>Puck.World.WorldScreenBinder.ReconcileScreens</c> calls <see cref="Server.WorldEngagement.DisengageScreen"/>
    /// directly — loopback-only, like every other client↔server call that has not yet crossed a wire).</summary>
    public WorldEngagement Engagement => m_engagement;

    /// <summary>Gets the world-scoped event feed — the four senses-lane families collected once per <see cref="Step"/>
    /// (collision pairs, region enter/exit, seat join/leave, route/engagement transitions). Read by
    /// <see cref="WorldAddonRuntime"/>'s read pump; a diagnostic/delivery surface, never itself hashed (the
    /// underlying state it derives from already is).</summary>
    public WorldEventFeed Events => m_events;

    /// <summary>Gets the authoritative screen-machine host — owns every booted <c>IScreenMachine</c>, its memory-peek
    /// surface (<see cref="WorldMachineHost"/> implements <see cref="IWorldMachineMemoryPeek"/> directly), and the
    /// screen-op verb surface's runtime target. Always present (never null): machines are booted and stepped in
    /// every boot shape.</summary>
    public WorldMachineHost Machines => m_machines;

    /// <summary>Observes every screen op (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/<c>.options</c>/
    /// <c>.link</c>/<c>.unlink</c>) after <see cref="TryApplyScreenOp"/> finishes — success or refusal alike, so a
    /// screen op the Control-authority gate refuses is still taped and reproduces as the identical refusal on
    /// replay (the same property <see cref="RebuildTap"/> establishes, reached here by a simpler route: unlike a
    /// rebuild, a screen op applies synchronously with no drain-time gap for its content to move between submission
    /// and apply, so the authority gate runs first, before any content is read). The carried content hash is
    /// non-null only for a successful <see cref="WorldScreenOp.Insert"/> — every other op kind and every refusal
    /// carries <see langword="null"/>, since nothing else on the tape needs a CAS pin (see
    /// <see cref="WorldScreenOp.Insert"/>'s own remarks on why <see cref="WorldScreenOp.Select"/> needs none). The
    /// replay tape attaches only while armed; clients never receive this submission-only seam.</summary>
    public Action<WorldScreenOp, string?, WorldPrincipal>? ScreenOpTap { get; set; }

    /// <summary>Gets the journal length — the number of applied mutations over the base (the <c>world.status</c> dirty
    /// count, and the <c>world.undo</c> budget).</summary>
    public int JournalLength => m_journal.Count;

    /// <summary>Gets the live SDF contact field under the field provider, or <see langword="null"/> under the analytic
    /// provider — the <c>world.collision.probe</c>/<c>world.collision.status</c> reads' window onto the
    /// surface the simulation itself solves against.</summary>
    public WorldSolidField? SolidField => m_solids;

    /// <summary>Gets the solid-field revision — bumped each time the field is rebuilt (a solid-affecting edit under the field
    /// provider). The <c>world.collision.status</c> read-back.</summary>
    public int SolidRevision => m_solidRevision;

    /// <summary>Gets an optional edit-echo tap invoked beside the loud stderr accept/reject lines — mutation outcomes,
    /// grant/revoke outcomes, and their document-only class — so a UI surface (the overlay toast, the editor HUD)
    /// narrates them without scraping stderr. Fires synchronously inline with the apply, never from a background
    /// thread: at submit-time for the ordered-domain kinds applied inline (grant, revoke, command, designation,
    /// composition, screen op), and inside <see cref="Step"/> for the kinds buffered to the tick boundary (mutation,
    /// rebuild, undo, addon lifecycle) and for a fired world-rule effect.</summary>
    public Action<WorldEditEcho>? EchoTap { get; set; }

    /// <summary>Gets the rule-fired <c>save</c> effect's own I/O seam, invoked with the settling tick from
    /// <c>FireWorldRuleEffect</c> — mirroring <see cref="EchoTap"/>/<see cref="ScreenOpTap"/>'s "the server calls
    /// out, the composition root supplies the capability" shape: this project (<c>Puck.World.Server</c>) references
    /// no rendering or input, so it cannot itself run the settle-at-save capture the manual <c>world.save</c> verb
    /// runs (<c>WorldSessionCapture.Capture</c>, in the composition root, needs the live render levers, screen
    /// binder, audio director and pacing control — none of which exist here). A <see langword="null"/> tap is a
    /// silent no-op, the same convention <see cref="EchoTap"/> follows; every live boot shape wires one
    /// (<c>WorldPostBuildWiring.Install</c>). <c>WorldReplaySnapshot.Drive</c> — the offline replay-verification
    /// drive — wires its own narration-only tap instead of the live closure, so replay verification stays
    /// side-effect-free: a fired save effect there is suppressed, never reaching disk, and is named on stderr rather
    /// than left indistinguishable from a rule that never fired. See <c>ActionEffect.Save</c>'s remarks for why this
    /// effect submits no <see cref="WorldMutation"/> and so needs a seam other than <c>TryApplyMutation</c> at
    /// all.</summary>
    public Action<ulong>? SaveEffectTap { get; set; }

    /// <summary>Gets or sets the injected neighbour resolver <see cref="WorldDefinitionValidator.Validate"/> reads
    /// for a cross-document adjacency proof
    /// — the same "the server calls out, the composition root supplies the capability" shape as <see cref="EchoTap"/>/
    /// <see cref="SaveEffectTap"/>, and for the identical reason: <c>Puck.World.Server</c> carries no storage
    /// or filesystem dependency, so it cannot construct either resolver itself. Read only during synchronous
    /// submission/load preparation, before a rebuild is buffered: live wiring supplies a local-first composite whose
    /// second resolver is the storage transport. <see cref="ApplyRebuild"/> runs later from <see cref="Step"/> and
    /// deliberately performs document-local validation only, so neither the composite nor its blocking storage read
    /// is reachable from the tick path. The resolver is likewise never read from <see cref="TryApplyMutation"/> or
    /// <see cref="ApplyUndo"/>: cross-document border compatibility is proved once at load, never re-litigated per
    /// mutation or journal entry. <see langword="null"/> (the default) is correct for an offline replay drive with no
    /// reachable transport and means an authored adjacency refuses by name for want of proof.</summary>
    public IWorldNeighbourResolver? Neighbours { get; set; }

    /// <summary>Gets or sets the composition-owned factory for proving a replacement document relative to that
    /// candidate's own origin rather than the currently loaded document. The path is the candidate's full path.</summary>
    public Func<string, IWorldNeighbourResolver?>? RebuildNeighbours { get; set; }

    /// <summary>Gets or sets the composition-root route for a federated peer that subsequently leaves this
    /// authority. Null when this server is not hosted by a multi-authority composition.</summary>
    public IWorldTransferForwarder? TransferForwarder { get; set; }

    /// <summary>Resolves the proof transport appropriate to one replacement document path.</summary>
    public IWorldNeighbourResolver? ResolveRebuildNeighbours(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        return (RebuildNeighbours?.Invoke(arg: path) ?? Neighbours);
    }

    /// <summary>Gets or sets the runtime adjacency source every live body's contact resolution consults inside the
    /// compiler-derived overlap — the per-tick counterpart to <see cref="Neighbours"/> (which proves reciprocal
    /// topology once at document-load time). The same
    /// "the server calls out, the composition root supplies the capability" shape as <see cref="Neighbours"/>/
    /// <see cref="EchoTap"/>: <c>Puck.World.Server</c> carries no cross-instance dependency, so it cannot resolve a
    /// sibling instance itself. Forwards straight to <see cref="Population"/>'s own contact-field composition — see
    /// <see cref="Server.WorldPopulation.ConfigureAdjacencies"/> — so a caller that sets this after the server
    /// already stepped still takes effect on the very next tick, no restart. <see langword="null"/> (the default) is
    /// correct whenever no source is wired; the body then solves against this authority's own geometry alone.</summary>
    public IWorldAdjacencySource? Adjacencies {
        get => m_population.Adjacencies;
        set => m_population.ConfigureAdjacencies(source: value);
    }

    /// <summary>Compacts the journal: the live definition becomes the new base and the edit history is cleared (the
    /// <c>world.save</c> half — a saved world is clean). Reads/writes only journal state, so it runs on the Immediate
    /// console path behind the stdin barrier.</summary>
    public void Compact() {
        m_base = m_definition;
        m_baseOrigin = "the last world.save";
        m_journal.Clear();
    }

    /// <summary>Attaches a client sink the per-tick snapshot is delivered to, immediately delivering the live
    /// definition followed by a primer snapshot of the current table, so the client renders the current state before
    /// its first ordinary tick delivery. A subscribe, not an overwrite: <see cref="WorldOutputHub"/> supports more
    /// than one attached sink (play-and-host — a local sink plus N future connections plus the tape all
    /// subscribing), so a second call adds a second subscriber rather than displacing the first.</summary>
    /// <param name="sink">The sink to deliver snapshots to.</param>
    /// <returns>A lease that detaches <paramref name="sink"/> when disposed — see
    /// <see cref="WorldOutputHub.Subscribe"/> for the threading/idempotency contract. Disposal takes the sink out of
    /// every future delivery; it never retracts what the primer or an earlier tick already delivered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sink"/> is <see langword="null"/>.</exception>
    public IDisposable AttachSink(IClientSink sink) {
        ArgumentNullException.ThrowIfNull(argument: sink);

        var lease = m_output.Subscribe(sink: sink);

        // Both the definition and the primer go to the NEWLY attached sink only (not a hub-wide broadcast) — an
        // already-attached sink must not replay a stale definition/snapshot every time a later sink joins. Isolated
        // the SAME way WorldOutputHub isolates an ordinary tick delivery fault (its own remarks): a sink that throws
        // during its own attach primer must not take down whoever called AttachSink, and is detached before it ever
        // reaches an ordinary tick delivery.
        try {
            sink.DeliverDefinition(definition: m_definition);
            sink.DeliverSnapshot(snapshot: BuildPrimerSnapshot());
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"[world.output: {sink.GetType().Name} threw during its own attach primer — detached] {exception}");
            lease.Dispose();
        }

        return lease;
    }

    /// <summary>Attaches the mounted addon runtime this server pumps at the three pinned points of <see cref="Step"/>,
    /// mirroring <c>LoopbackTransport.Bind</c>'s one-shot wiring. Called by <see cref="WorldAddonRuntime.Create"/> once
    /// its guests have mounted, so the server never observes a half-built runtime. Also re-sizes the per-tick contention
    /// tracking to cover the addon writers that now exist beside the seat lanes.</summary>
    /// <param name="runtime">The mounted runtime.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is <see langword="null"/>.</exception>
    public void AttachAddons(WorldAddonRuntime runtime) {
        ArgumentNullException.ThrowIfNull(argument: runtime);

        m_addons = runtime;

        // Two lanes per mounted guest beside the seats: an addon holds Drive over as many bodies as it was granted, so
        // this is a sized BOUND on how many distinct entities one tick's contention tracking follows, never a limit on
        // how many a guest may drive. Past it, ReportContention's defensive length check stops recording new entities
        // and contention reporting saturates — deliberately, because the cost of exactness here is a per-tick resize on
        // the hot path to sharpen a diagnostic that nothing depends on.
        var capacity = (WorldPopulation.LocalSeatCount + (runtime.MountedCount * 2));

        m_tickWrittenEntity = new int[capacity];
        m_tickWrittenPrincipal = new WorldPrincipal[capacity];
        m_tickCollided = new bool[capacity];
    }

    /// <summary>Gets the attached addon runtime's mount receipts, in mount order — empty when no runtime is attached (a
    /// world that enables no addon, or an offline re-drive read before its own mount). The record side of the replay
    /// tape reads this at record-start so a saved tape pins the guests it will re-run.</summary>
    public IReadOnlyList<WorldAddonReceipt> AddonReceipts => (m_addons?.Receipts ?? Array.Empty<WorldAddonReceipt>());

    /// <summary>Gets a value indicating whether any mounted addon has ever had an admitted execution attempted (a real
    /// <c>TickAddons</c> pump, not merely mounting) — a boot-anchored replay arm predicate.
    /// <c>false</c> for a world that mounts no addon, or one whose mounted addons never reached their first tick.
    /// Latched per mounted entry the first time it is pumped and never cleared — a guest's accumulated memory/tick
    /// state before a recording began is exactly what offline replay cannot re-establish (fresh guests, sim
    /// counter zero), so <c>replay.record</c> refuses to arm once this is <see langword="true"/>.</summary>
    public bool AnyAddonEverPumped => (m_addons?.AnyEverPumped ?? false);

    /// <summary>Gets a value indicating whether any booted screen machine has ever had a step/segment actually submitted — the identical
    /// boot-anchored replay arm predicate <see cref="AnyAddonEverPumped"/> applies to addons: offline replay
    /// rehydrates a fresh <see cref="WorldMachineHost"/> from the tape's embedded
    /// definition, which can reconstruct a machine's boot image but never its accumulated core state (WRAM, CPU
    /// registers) once real ticks have run it. A world with a boot-declared cartridge means recording must arm
    /// before its first step, same as a world that mounts an addon must arm before its first tick.</summary>
    public bool AnyMachineEverPumped => m_machines.AnyEverPumped;

    /// <summary>Gets a value indicating whether any screen op has ever applied (changed host state — <c>ok</c> from
    /// <see cref="TryApplyScreenOp"/>, never merely attempted) this session — a third boot-anchored replay arm
    /// predicate beside <see cref="AnyAddonEverPumped"/>/<see cref="AnyMachineEverPumped"/>. Screen ops apply
    /// synchronously, between fixed steps, not inside <see cref="Step"/> — so a
    /// <c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/<c>.options</c>/<c>.link</c>/<c>.unlink</c> that lands
    /// before <c>replay.record</c> arms (even with zero steps run since) changes live host state
    /// (<see cref="WorldMachineHost"/>'s slots/links) that the tape's record-start definition snapshot never
    /// reflects — these ops are not document mutations, so nothing about them exists in
    /// <see cref="WorldDefinition"/> for the snapshot to capture, and they are only ever added to the tape's own
    /// authority list from the moment <see cref="ScreenOpTap"/> attaches (recording-arm time onward) — never
    /// retroactively. Left ungated, offline replay reconstruction (a fresh
    /// <see cref="WorldMachineHost"/> booted from that snapshot alone) would simply lack the machine/link/eject
    /// entirely, a divergence the pose-only hash cannot see. Latched the instant any op applies and never cleared,
    /// mirroring <see cref="AnyMachineEverPumped"/>'s own shape exactly. Only ever added to a recording's own
    /// authority list from the moment <see cref="ScreenOpTap"/> attaches (recording-arm time) onward — never
    /// retroactively for an op that already applied before that.</summary>
    public bool AnyScreenOpEverApplied { get; private set; }

    /// <summary>Returns the body at a 0-based entity index, or <see langword="null"/> when the index holds no live body.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public WorldBody? Body(int index) => (((uint)index < (uint)m_population.Capacity) ? m_population.EntryBody(index: index) : null);

    /// <summary>Buffers one entity's submitted intent for the next <see cref="Step"/>.</summary>
    /// <param name="submission">The tick, entity index, and merged intent.</param>
    public void EnqueueIntent(in IntentSubmission submission) {
        m_intents.Enqueue(item: submission);
    }

    /// <summary>Publishes one authenticated federation stream's latest device image. The image is held as replicated
    /// input state and reapplied once per destination tick; it is not consumed merely because this socket update was
    /// sparse relative to the destination clock.</summary>
    public void PublishFederatedIntent(long leaseId, in IntentSubmission submission) {
        if ((leaseId <= 0) || ((uint)submission.EntityIndex >= (uint)m_federatedIntents.Length)) {
            return;
        }

        var published = submission;
        ExecuteAuthorityOperation(operation: () => {
            ref var state = ref m_federatedIntents[published.EntityIndex];
            state = new FederatedIntentState(LeaseId: leaseId, Principal: published.Principal, Submission: published, Active: true);
        });
    }

    /// <summary>Releases every device image still owned by one closing federation stream. Lease comparison makes
    /// reconnect replacement atomic: a superseded stream cannot release the newer writer.</summary>
    public void ReleaseFederatedIntents(long leaseId) {
        if (leaseId <= 0) {
            return;
        }

        ExecuteAuthorityOperation(operation: () => {
            for (var index = 0; index < m_federatedIntents.Length; index++) {
                if (m_federatedIntents[index].Active && (m_federatedIntents[index].LeaseId == leaseId)) {
                    m_federatedIntents[index] = default;
                }
            }
        });
    }

    /// <summary>Buffers one live world mutation for the next <see cref="Step"/> (drained before intents). Retains the
    /// submitting envelope's connection/correlation identity so the eventual accept/reject <see cref="WorldEditEcho"/>
    /// routes back to the submitter (see <see cref="WorldEditEcho.ConnectionId"/>) — a deferred op's echo fires later
    /// than its submission, so that identity must travel with the buffered entry rather than being read live.</summary>
    /// <param name="mutation">The mutation to apply.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="sourceAddonIndex">The mounted addon index this mutation was decoded from, or <c>-1</c> for a
    /// console/client submission (the addon mutation seam's completion field — see <see cref="PendingOp.Mutate"/>).</param>
    /// <param name="actOrdinal">The addon's own output-batch ordinal this mutation answers, when
    /// <paramref name="sourceAddonIndex"/> is not <c>-1</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="mutation"/> is <see langword="null"/>.</exception>
    public void EnqueueMutation(WorldMutation mutation, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, int sourceAddonIndex = -1, ushort actOrdinal = 0) {
        ArgumentNullException.ThrowIfNull(argument: mutation);

        m_pending.Enqueue(item: new PendingOp.Mutate(Mutation: mutation, ConnectionId: connectionId, CorrelationId: correlationId, SourceAddonIndex: sourceAddonIndex, ActOrdinal: actOrdinal));
    }

    /// <summary>Buffers a whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>)
    /// for the next <see cref="Step"/> (drained before intents). Retains the submitting envelope's
    /// connection/correlation identity — see <see cref="EnqueueMutation"/>'s own remarks.</summary>
    /// <param name="request">The rebuild request.</param>
    /// <param name="principal">The acting identity the rebuild is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="expectedContentHash">Replay only: the CAS content hash a recorded tape entry pins. When set,
    /// <see cref="ApplyRebuild"/> compares it against the hash it computes for this drive's own resolved candidate
    /// (its own base for Reset, a fresh re-read of <see cref="WorldRebuildRequest.PathHint"/> for Load/Reload) and
    /// refuses by name on a mismatch, before any other guard runs. <see langword="null"/> (the default) is the live
    /// path — nothing to compare against, since the live drive is what establishes the hash a later recording pins.
    /// <see cref="WorldReplaySnapshot.Drive"/> is the one caller that ever passes a non-null value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public void EnqueueRebuild(WorldRebuildRequest request, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0, string? expectedContentHash = null) {
        ArgumentNullException.ThrowIfNull(argument: request);

        string? preparationFailure = null;

        // A carried document is available at submission time, outside Step. Prove its neighbour claims here and carry
        // any refusal into the ordered tick-boundary decision; ApplyRebuild repeats only document-local checks.
        var rebuildNeighbours = ((request.PathHint is { } candidatePath) ? ResolveRebuildNeighbours(path: candidatePath) : Neighbours);

        if ((request.Definition is { } supplied) && !WorldDefinitionValidator.TryValidate(definition: supplied, reason: out var proofReason, neighbours: rebuildNeighbours)) {
            preparationFailure = $"cross-document load proof failed before enqueue — {proofReason}";
        }

        m_pending.Enqueue(item: new PendingOp.Rebuild(Request: request, Principal: principal, ConnectionId: connectionId, CorrelationId: correlationId, ExpectedContentHash: expectedContentHash, PreparationFailure: preparationFailure));
    }

    /// <summary>Buffers a journal undo of the last <paramref name="count"/> mutations for the next <see cref="Step"/>.
    /// Retains the submitting envelope's connection/correlation identity — see <see cref="EnqueueMutation"/>'s own
    /// remarks.</summary>
    /// <param name="count">How many trailing mutations to undo (clamped to at least 1 and at most the journal length).</param>
    /// <param name="principal">The acting identity the undo is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    public void EnqueueUndo(int count, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        m_pending.Enqueue(item: new PendingOp.Undo(Count: count, Principal: principal, ConnectionId: connectionId, CorrelationId: correlationId));
    }

    /// <summary>Buffers a live addon-runtime lifecycle change (<c>world.addon.mount</c>/<c>world.addon.unmount</c>)
    /// for the next <see cref="Step"/> — drained at the same door <see cref="EnqueueMutation"/> uses (before
    /// intents), so a mount lands at the same defined tick-boundary point a document mutation does. Retains the
    /// submitting envelope's connection/correlation identity — see <see cref="EnqueueMutation"/>'s own remarks.</summary>
    /// <param name="lifecycle">The mount/unmount action.</param>
    /// <param name="principal">The acting identity the change is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lifecycle"/> is <see langword="null"/>.</exception>
    public void EnqueueAddonLifecycle(WorldAddonLifecycle lifecycle, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        ArgumentNullException.ThrowIfNull(argument: lifecycle);

        m_pending.Enqueue(item: new PendingOp.AddonLifecycle(Lifecycle: lifecycle, Principal: principal, ConnectionId: connectionId, CorrelationId: correlationId));
    }

    /// <summary>Adds a grant to the table synchronously (the <c>world.grant</c> half; like a command, so the next tick's
    /// checks observe it). Checks <paramref name="actor"/> — the principal asking, distinct from
    /// <see cref="WorldGrant.Principal"/> (the principal receiving it) — via
    /// <see cref="WorldGrants.HoldsForAdministration"/>, which is enforced only for actors outside the trust boundary
    /// (an <c>Addon</c> or <c>Peer</c> may only grant authority it itself holds); a <c>Console</c> or <c>Seat</c> actor
    /// passes unconditionally, because gating a fully-trusted operator's own grant path is ceremony, not security — see
    /// the check's own doc for why. A denied actor prints the same loud, attributed line as a conflicting exclusive
    /// acquisition and changes nothing.</summary>
    /// <param name="grant">The grant to add.</param>
    /// <param name="actor">The principal asking for the grant to be added.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller (replay, the addon runtime) with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    /// <remarks>A Drive grant whose subject is a remote-admitted human body
    /// (<see cref="WorldPopulation.IsAdmittedPeer"/>) refuses, by name, for any <see cref="WorldGrant.Principal"/>
    /// other than that body's own <see cref="PrincipalKind.Peer"/>: with no Peer-authored consent grammar,
    /// <c>Reach ∧ Consent</c> is <c>0</c> by construction for any other principal, so such a row would compose to
    /// nothing anyway; the refusal states this at the door instead of leaving an operator to infer it from a pool
    /// that silently never moves. <see cref="WorldPopulation.IsAdmittedPeer"/> is currently always
    /// <see langword="false"/>, so this door does not yet trigger.</remarks>
    public void Grant(WorldGrant grant, WorldPrincipal actor, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) =>
        _ = TryApplyGrant(grant: grant, actor: actor, connectionId: connectionId, correlationId: correlationId);

    // The ordinary public door intentionally remains void: callers submit an authority operation and observe its
    // attributed echo. Admission re-authorization additionally needs to know whether the row ACTUALLY reached the
    // live table so a conflict refusal is not later misclassified as an explicit revoke; it uses this identical
    // implementation and keeps the boolean inside the server.
    private bool TryApplyGrant(WorldGrant grant, WorldPrincipal actor, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        var label = $"{grant.Principal.Describe()} {grant.Capability.ToString().ToLowerInvariant()} {grant.Subject.Describe()}";

        if (!m_grants.HoldsForAdministration(principal: actor, capability: grant.Capability, subject: grant.Subject)) {
            DenyGrantTable(
                denial: $"{actor.Describe()} cannot grant {grant.Capability.ToString().ToLowerInvariant()} over {grant.Subject.Describe()} to {grant.Principal.Describe()} — it holds none there itself",
                connectionId: connectionId,
                correlationId: correlationId
            );

            return false;
        }

        if ((grant.Capability == WorldCapability.Drive) &&
            (grant.Subject.Kind == GrantSubjectKind.Body) &&
            m_population.IsAdmittedPeer(bodyIndex: grant.Subject.Value) &&
            (grant.Principal != m_population.PeerPrincipal(index: grant.Subject.Value))) {
            DenyGrantTable(
                denial: $"{grant.Principal.Describe()} cannot co-drive {grant.Subject.Describe()} — no consent authorship exists for a remote-admitted body except its own peer ({m_population.PeerPrincipal(index: grant.Subject.Value).Describe()}); Reach ∧ Consent composes to nothing until that peer authors it",
                connectionId: connectionId,
                correlationId: correlationId
            );

            return false;
        }

        if (m_grants.TryGrant(grant: grant, reason: out var reason)) {
            Console.Error.WriteLine(value: $"[world.grant: {label}{(grant.Exclusive ? " exclusive" : string.Empty)}]");

            // THE JOIN: the grant's channel mask was validated against the WORLD's channel table, and the guest's own
            // channel names were resolved against that same table at its handshake — and until now nothing compared the
            // two to each other. A consent row could therefore name a real channel its holder never emits, be accepted
            // in full, and drive nothing, leaving an operator to read that absence as a pool set too low or a body that
            // will not move. Reported, never refused: a later reload may legitimately add the channel, so the row is a
            // standing intent rather than a mistake.
            if (m_addons?.DescribeUndeclaredGrantedChannels(principal: grant.Principal, reach: grant.Reach, channels: m_population.Channels) is { } undeclared) {
                Console.Error.WriteLine(value: $"[world.grant: {grant.Principal.Describe()} is granted channel(s) it never declares — inert until it does: {undeclared}]");
            }

            EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"grant {label}{(grant.Exclusive ? " exclusive" : string.Empty)}", Rejected: false, Kind: WorldEditEchoKind.GrantTable, ConnectionId: connectionId, CorrelationId: correlationId));

            return true;
        } else {
            Console.Error.WriteLine(value: $"[world.grant rejected: {label} — {reason}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"grant {label} rejected: {reason}", Rejected: true, Kind: WorldEditEchoKind.GrantTable, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }
    }

    /// <summary>Removes a grant from the table synchronously (the <c>world.revoke</c> half). Checks <paramref name="actor"/>
    /// against the same administration rule as <see cref="Grant"/> — enforced only for an <c>Addon</c>/<c>Peer</c> actor,
    /// which must itself hold <see cref="WorldGrant.Capability"/> over <see cref="WorldGrant.Subject"/> (ignoring the
    /// exclusivity override <see cref="WorldGrants.Allows"/> enforces at use, so an untrusted actor can always revoke an
    /// exclusive grant it itself authorized); a <c>Console</c> or <c>Seat</c> actor passes unconditionally — see
    /// <see cref="WorldGrants.HoldsForAdministration"/> for why gating the trusted side would only brick self-revocation
    /// without buying any security.</summary>
    /// <param name="grant">The grant (capability + subject) to revoke.</param>
    /// <param name="actor">The principal asking for the grant to be revoked.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    public void Revoke(WorldGrant grant, WorldPrincipal actor, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        var label = $"{grant.Principal.Describe()} {grant.Capability.ToString().ToLowerInvariant()} {grant.Subject.Describe()}";

        if (!m_grants.HoldsForAdministration(principal: actor, capability: grant.Capability, subject: grant.Subject)) {
            DenyGrantTable(
                denial: $"{actor.Describe()} cannot revoke {grant.Capability.ToString().ToLowerInvariant()} over {grant.Subject.Describe()} from {grant.Principal.Describe()} — it holds none there itself",
                connectionId: connectionId,
                correlationId: correlationId
            );

            return;
        }

        var removed = m_grants.Revoke(principal: grant.Principal, capability: grant.Capability, subject: grant.Subject);

        Console.Error.WriteLine(value: removed
            ? $"[world.revoke: {label}]"
            : $"[world.revoke: {grant.Principal.Describe()} held no {grant.Capability.ToString().ToLowerInvariant()} over {grant.Subject.Describe()}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: (removed ? $"revoke {label}" : $"revoke {label} — nothing held"), Rejected: !removed, Kind: WorldEditEchoKind.GrantTable, ConnectionId: connectionId, CorrelationId: correlationId));
    }

    // The ONE grant-table DENIAL emission — the loud stderr line plus the submitter-routed denied echo. Grant's
    // administration and co-drive-consent refusals and Revoke's administration refusal differ only in what they say,
    // never in how it is reported, so the echo's shape (GrantTable, Rejected, Denied) is decided once.
    private void DenyGrantTable(string denial, int connectionId, long correlationId) {
        Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: denial, Rejected: true, Kind: WorldEditEchoKind.GrantTable, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));
    }

    /// <summary>Applies a live window-composition override synchronously (the <c>view.override layout</c>/<c>view.override camera</c>
    /// path). Checks <see cref="WorldCapability.Control"/> over
    /// <see cref="GrantSubject.Composition"/>; on accept pushes it to the client composer, on denial prints a loud line
    /// and changes nothing. Never durable — no document, no journal.</summary>
    /// <param name="composition">The composition override.</param>
    /// <param name="principal">The acting identity the override is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="composition"/> is <see langword="null"/>.</exception>
    public void ApplyComposition(WorldComposition composition, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        ArgumentNullException.ThrowIfNull(argument: composition);

        if (m_grants.Allows(principal: principal, capability: WorldCapability.Control, subject: GrantSubject.Composition) is { IsAllowed: false } verdict) {
            var denial = $"{principal.Describe()} cannot control composition ({verdict.DescribeDenial()}) — {composition.GetType().Name} dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: denial, Rejected: true, Kind: WorldEditEchoKind.GrantTable, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));

            return;
        }

        m_output.DeliverComposition(composition: composition);
    }

    /// <summary>Applies a live session lever — the same shape as <see cref="ApplyComposition"/> one section over:
    /// checked against <see cref="WorldCapability.Mutate"/> over the section the lever folds into, then pushed to the
    /// client to write onto its presentation service. Synchronous at submit (like a command), never journaled, and
    /// never a <see cref="WorldMutation"/> — a slider must not mint an undo entry, and "live now, document owns boot"
    /// is the asymmetry a lever exists for.</summary>
    /// <remarks>Writing the injected presentation service directly, bypassing this method, skips the grant check
    /// below and lets an ungranted caller move — and persist through <c>world.save</c> — a knob in a section it
    /// holds no grant over.</remarks>
    /// <param name="lever">The lever write.</param>
    /// <param name="principal">The acting identity the lever is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    public void ApplySessionLever(WorldSessionLever lever, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        if (m_grants.Allows(principal: principal, capability: WorldCapability.Mutate, subject: GrantSubject.Section(section: lever.Section)) is { IsAllowed: false } verdict) {
            var denial = $"{principal.Describe()} cannot mutate section:{lever.Section.ToString().ToLowerInvariant()} ({verdict.DescribeDenial()}) — {lever.Kind} lever dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: denial, Rejected: true, Kind: WorldEditEchoKind.GrantTable, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));

            return;
        }

        m_output.DeliverSessionLever(lever: lever);
    }

    /// <summary>Validates and applies one subject-bearing target-register write.</summary>
    public bool ApplyDesignation(WorldDesignation designation, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        return ApplyDesignationCore(designation: designation, principal: principal, knownSubject: false, connectionId: connectionId, correlationId: correlationId);
    }

    private bool ApplyDesignationCore(WorldDesignation designation, WorldPrincipal principal, bool knownSubject, int connectionId, long correlationId) {
        var sourceIndex = designation.EntityIndex;

        if (((uint)sourceIndex >= (uint)m_population.Capacity) || (Body(index: sourceIndex) is not { } source)) {
            return Refuse($"body:{sourceIndex} is not active");
        }
        if (!m_population.TryResolveTargetRegister(name: designation.Register, index: out var registerIndex)) {
            return Refuse($"register '{designation.Register}' is not declared");
        }

        var sourceSubject = GrantSubject.Body(index: sourceIndex);
        if (!knownSubject) {
            var drive = m_grants.Allows(principal: principal, capability: WorldCapability.Drive, subject: sourceSubject);
            if (!drive.IsAllowed) {
                return Refuse($"{principal.Describe()} cannot drive {sourceSubject.Describe()} ({drive.DescribeDenial()})", denied: true);
            }

            var ownsBody = ((principal.Kind is PrincipalKind.Seat or PrincipalKind.Peer) && (principal.Index == sourceIndex));
            if (!ownsBody && (principal.Kind != PrincipalKind.Console)
                && (!m_grants.TryGetChannelReach(principal: principal, subject: sourceSubject, mask: out var reach)
                    || !reach.Contains(ordinal: m_population.TargetRegisters.ReachOrdinal(index: registerIndex)))) {
                return Refuse($"{principal.Describe()} Drive reach does not include target register '{designation.Register}'", denied: true);
            }
        }
        if (designation.Subject.Kind != GrantSubjectKind.Body) {
            return Refuse($"subject '{designation.Subject.Describe()}' is not a body");
        }

        var targetIndex = designation.Subject.Value;
        if ((targetIndex == sourceIndex) || ((uint)targetIndex >= (uint)m_population.Capacity) || (Body(index: targetIndex) is null)) {
            return Refuse((targetIndex == sourceIndex) ? "a body cannot designate itself" : $"body:{targetIndex} is not active");
        }

        var targetSubject = GrantSubject.Body(index: targetIndex);
        if (!knownSubject) {
            var observe = m_grants.Allows(principal: principal, capability: WorldCapability.Observe, subject: targetSubject);
            if (!observe.IsAllowed) {
                return Refuse($"{principal.Describe()} cannot observe {targetSubject.Describe()} ({observe.DescribeDenial()})", denied: true);
            }
        }

        if (!knownSubject) {
            var register = m_definition.TargetRegisters[registerIndex];
            var range = WorldPopulation.EffectiveTargetValue(body: source, stateName: register.RangeState, authoredMaximum: register.MaximumRange);
            var halfAngle = WorldPopulation.EffectiveTargetValue(body: source, stateName: register.HalfAngleState, authoredMaximum: register.MaximumHalfAngleDegrees);
            if (!m_population.DesignationWithinEnvelope(sourceIndex: sourceIndex, targetIndex: targetIndex, register: register, rangeValue: range, halfAngleDegrees: halfAngle, reason: out var reason)) {
                return Refuse(reason);
            }
        }

        m_population.SetDesignation(bodyIndex: sourceIndex, registerIndex: registerIndex, subjectIndex: targetIndex);
        var message = $"body:{sourceIndex} {designation.Register}={targetSubject.Describe()}";
        Console.Error.WriteLine(value: $"[world.designation: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: message, Rejected: false, Kind: WorldEditEchoKind.Designation, ConnectionId: connectionId, CorrelationId: correlationId));
        return true;

        bool Refuse(string reason, bool denied = false) {
            m_population.NoteDesignationRefusal(bodyIndex: sourceIndex, reason: reason);
            Console.Error.WriteLine(value: $"[world.designation refused: {reason}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: reason, Rejected: true, Kind: WorldEditEchoKind.Designation, Denied: denied, ConnectionId: connectionId, CorrelationId: correlationId));
            return false;
        }
    }

    /// <summary>Applies an authority command to its target body. Synchronous at submit (see the class summary), so a
    /// policy read following the command in the same batch observes its effect. A command whose entity is not live
    /// no-ops (validation happened at submit; the miss is benign).</summary>
    /// <param name="command">The command to apply.</param>
    /// <param name="connectionId">The submitting envelope's connection id (see <see cref="WorldEditEcho.ConnectionId"/>);
    /// defaults to the local connection for a direct caller (replay, the addon runtime) with no originating envelope.</param>
    /// <param name="correlationId">The submitting envelope's correlation id; defaults to none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    public void ApplyCommand(WorldCommand command, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        ArgumentNullException.ThrowIfNull(argument: command);

        // Engage/Disengage are Control-over-SCREEN commands, never Drive-over-BODY ones — the generic gate below does
        // not apply to them at all, so they branch out first. Both apply through Server.WorldEngagement, which runs
        // its own check-then-mutate (see its own remarks); nothing here duplicates that check.
        switch (command) {
            case WorldCommand.Engage engage:
                if (!CheckEngagePolicy(entityIndex: engage.EntityIndex, target: engage.Target, reason: out var reason)) {
                    Console.Error.WriteLine(value: $"[world.engage denied: {reason}]");

                    return;
                }

                _ = m_engagement.Engage(entityIndex: engage.EntityIndex, target: engage.Target, capture: engage.Capture, actingPrincipal: engage.Principal, targetPrincipal: engage.TargetPrincipal);

                return;
            case WorldCommand.Disengage disengage:
                _ = m_engagement.Disengage(entityIndex: disengage.EntityIndex, actingPrincipal: disengage.Principal, targetPrincipal: disengage.TargetPrincipal);

                return;
        }

        // CC/death gating (Seam A) — see TryDriveGateVerdict's own remarks: the SAME rule ApplyIntentSubmission
        // consults, checked here BEFORE the ordinary grant-table lookup so a scripted tape segment
        // (player.fly/EnqueueSegment) or any other authority command is refused by the identical state fact a raw
        // per-tick submission is, never a lesser door a script could walk around.
        var gated = TryDriveGateVerdict(bodyIndex: command.EntityIndex, verdict: out var gatedVerdict);
        var verdict = (gated ? gatedVerdict : m_grants.Allows(principal: command.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: command.EntityIndex)));

        if (!verdict.IsAllowed) {
            var denial = $"{command.Principal.Describe()} cannot drive body:{command.EntityIndex} ({verdict.DescribeDenial()}) — {command.GetType().Name} dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: denial, Rejected: true, Kind: WorldEditEchoKind.GrantTable, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));
            NoteDriveRefusalIfTracked(command: command, reason: denial);

            return;
        }

        if (Body(index: command.EntityIndex) is not { } body) {
            NoteDriveRefusalIfTracked(command: command, reason: $"body:{command.EntityIndex} is inactive");

            return;
        }

        switch (command) {
            case WorldCommand.SnapPose snap:
                switch (snap.Mode) {
                    case SnapPoseMode.Pose:
                        body.Pose(x: snap.Position.X, y: snap.Position.Y, z: snap.Position.Z, yawRadians: snap.YawRadians, pitchRadians: snap.PitchRadians, rollRadians: snap.RollRadians);
                        break;
                    default:
                        throw new InvalidOperationException(message: $"SnapPose mode value {(int)snap.Mode} reached the server without codec validation.");
                }

                break;
            case WorldCommand.EnqueueSegment segment:
                body.EnqueueRun(intent: segment.Intent, seconds: segment.Seconds);

                break;
            case WorldCommand.PressChannel press:
                if (press.HoldSeconds is { } holdSeconds) {
                    var holdCeiling = FixedQ4816.FromRawBits(value: m_grants.HoldCeiling(principal: press.Principal, subject: GrantSubject.Body(index: press.EntityIndex)));
                    var outcome = body.PressChannel(ordinal: press.ChannelOrdinal, value: press.Value, holdSeconds: holdSeconds, authoredMaximum: holdCeiling);

                    // The submit drains synchronously, so player.press's handler can read this back immediately —
                    // the same MotionRefusal/StopOutcome read-back shape — and name a silent grant-budget truncation
                    // instead of echoing the requested duration as if it were honored. Clears any prior refusal note
                    // this body's press slot carried, so a stale denial can never bleed into a fresh success.
                    m_population.NotePressOutcome(bodyIndex: press.EntityIndex, outcome: outcome);
                } else {
                    body.PressChannel(ordinal: press.ChannelOrdinal, value: press.Value);
                    m_population.NotePressSuccess(bodyIndex: press.EntityIndex);
                }

                break;
            case WorldCommand.SetBodyMotion motion:
                // The runtime door: a program that exists is not automatically one this body's kit can run. Coherence
                // is the SAME check WorldDefinitionValidator runs at boot (WorldDefinitionValidator.TryValidateProgramCoherence)
                // — reusing it here is what keeps a document-legal kit from runtime-switching into an incoherent program.
                // Refusal narrates through the SAME echo path world.designation/world.grant use (stderr line + EchoTap),
                // and records on the population so the SYNCHRONOUS submitter (player.motion's handler) can read back the
                // true outcome instead of assuming success.
                if (!m_population.TryGetBodyMotionProgram(name: motion.BodyMotionProgram, out var targetMotionProgram) || (targetMotionProgram is not { } resolvedMotionProgram)) {
                    var reason = $"body motion program '{motion.BodyMotionProgram}' is not declared";

                    m_population.NoteMotionRefusal(bodyIndex: motion.EntityIndex, reason: reason);
                    Console.Error.WriteLine(value: $"[player.motion refused: {reason}]");
                    EchoTap?.Invoke(obj: new WorldEditEcho(Message: reason, Rejected: true, Kind: WorldEditEchoKind.BodyMotion, ConnectionId: connectionId, CorrelationId: correlationId));
                } else if (!WorldDefinitionValidator.TryValidateProgramCoherence(model: m_population.KitMotion(index: motion.EntityIndex), program: resolvedMotionProgram, reason: out var coherenceReason)) {
                    m_population.NoteMotionRefusal(bodyIndex: motion.EntityIndex, reason: coherenceReason);
                    Console.Error.WriteLine(value: $"[player.motion refused: {coherenceReason}]");
                    EchoTap?.Invoke(obj: new WorldEditEcho(Message: coherenceReason, Rejected: true, Kind: WorldEditEchoKind.BodyMotion, ConnectionId: connectionId, CorrelationId: correlationId));
                } else {
                    body.SetBodyMotionProgram(programName: motion.BodyMotionProgram);
                    m_population.NoteMotionRefusal(bodyIndex: motion.EntityIndex, reason: string.Empty);
                    EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"body:{motion.EntityIndex} motion={motion.BodyMotionProgram}", Rejected: false, Kind: WorldEditEchoKind.BodyMotion, ConnectionId: connectionId, CorrelationId: correlationId));
                }

                break;
            case WorldCommand.SetControl control:
                if (m_population.SupportsSource(index: control.EntityIndex, source: control.Source, refusal: out var sourceRefusal)) {
                    body.SetIntentSource(source: control.Source);
                } else {
                    Console.Error.WriteLine(value: $"[player.control refused: {sourceRefusal}]");
                }

                break;
            case WorldCommand.Reconcile reconcile:
                var continuity = body.Reconcile(x: reconcile.X, z: reconcile.Z, yawRadians: reconcile.YawRadians, seconds: reconcile.Seconds);
                Console.Error.WriteLine(value: $"[player.reconcile: body:{reconcile.EntityIndex} continuity={continuity.ToString().ToLowerInvariant()} maxSmoothError={m_definition.Motion.MaxSmoothError:0.###}]");

                break;
            case WorldCommand.Stop:
                // The submit drains synchronously (WorldServer.Submit), so player.stop's handler can read this back
                // through WorldPopulation.LastStopOutcome the instant control returns to it — the same pattern
                // player.motion's MotionRefusal read-back uses.
                m_population.NoteStopOutcome(bodyIndex: command.EntityIndex, outcome: body.Stop());

                break;
            case WorldCommand.LoadDurableState load:
                if (load.Tick != NextInputTick) {
                    Console.Error.WriteLine(value: $"[player.state-load refused: tick {load.Tick} is not next tick {NextInputTick}]");
                } else if (!body.TryStageDurableState(tick: load.Tick, values: load.Values, requirePlayerWritable: true, writer: load.Principal.Describe(), reason: out var stateReason)) {
                    Console.Error.WriteLine(value: $"[player.state-load refused: {stateReason}]");
                }

                break;
        }
    }

    // player.press and player.stop are read back SYNCHRONOUSLY by their console handlers immediately after a submit
    // (WorldPopulation.PressRefusal/StopRefusal, mirroring MotionRefusal) — so a refusal that reaches EITHER of
    // ApplyCommand's early returns above (the grant-table denial, the missing/inactive body) must leave a note
    // behind too, or the handler reads whatever an EARLIER, unrelated attempt on the SAME body left there and
    // echoes a fabricated affirmative quoting stale numbers. Every other command kind is untracked and a no-op here
    // — their handlers narrate a refusal off the existing stderr/EchoTap path instead.
    private void NoteDriveRefusalIfTracked(WorldCommand command, string reason) {
        switch (command) {
            case WorldCommand.PressChannel press:
                m_population.NotePressRefusal(bodyIndex: press.EntityIndex, reason: reason);

                break;
            case WorldCommand.Stop stop:
                m_population.NoteStopRefusal(bodyIndex: stop.EntityIndex, reason: reason);

                break;
        }
    }

    /// <summary>Applies a session request synchronously and returns the reply. The protocol handshake is checked here: a
    /// <see cref="SessionRequest.Join"/> whose <see cref="SessionRequest.Join.WireProtocolKey"/> mismatches
    /// <see cref="WorldProtocol.WireProtocolKey"/> is rejected with a distinct reason. Seat allocation is likewise validated: an
    /// out-of-range slot is rejected, as is an unknown profile name on a <see cref="SessionRequest.SetIdentity"/> — a
    /// <see cref="SessionRequest.Join"/> naming an unresolved profile seats with no identity rather than refusing.</summary>
    /// <param name="request">The session request.</param>
    /// <returns>The session reply.</returns>
    public SessionReply ApplySession(SessionRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);

        switch (request) {
            case SessionRequest.Join join: {
                // LOOPBACK STAYS CREDENTIAL-FREE BY CONSTRUCTION. This is the in-process Session.Join path — the
                // boot client, the console, and every local seat all reach it through LoopbackTransport, never a
                // socket — so it checks WorldHelloDoor's protocol-version compatibility and STOPS there; it never
                // calls Protocol.WorldAdmissionDoor. The reason is the BOUNDARY, not the code path: an identity
                // check exists to answer "is the party on the other side of this wire who they claim to be", and
                // there is no wire here — the caller is this same process, already running as whichever principal
                // the OS session grants it. The trust boundary this door polices is the process boundary itself;
                // requiring a signed claim from your own process to talk to your own process would authenticate
                // nothing real while adding a key-management burden with no attacker on the other side of it. A
                // REMOTE connection (Server.WorldTcpHost) crosses a real wire and passes through WorldAdmissionDoor
                // in addition to this check, once this one succeeds.
                if (!WorldHelloDoor.TryAccept(offeredKey: join.WireProtocolKey, refusal: out var helloRefusal)) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"{helloRefusal}: wire key 0x{join.WireProtocolKey:x16} != server 0x{WorldProtocol.WireProtocolKey:x16}");
                }

                if ((uint)join.Slot >= WorldPopulation.LocalSeatCount) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"slot {join.Slot} out of range");
                }

                // A seat's own Drive/body:slot grant (seeded at construction) is the "this principal legitimately IS
                // this seat" check for the whole session-lifecycle family: a principal with no drive claim on the slot
                // (an addon, which is seeded nothing) can never mint or reseat its participant.
                if (m_grants.Allows(principal: join.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: join.Slot)) is { IsAllowed: false } joinVerdict) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"{join.Principal.Describe()} cannot join slot {join.Slot} ({joinVerdict.DescribeDenial()})");
                }

                var profile = ((join.IdentityName is { } name) ? m_profiles.Find(name: name) : null);

                // BODY-RESUME: a re-Join against a slot still PARKED from an earlier leave tries to recover that
                // retained body first — see TryResumeParkedSeat's own remarks for the identity match rule. Only a
                // slot that is not parked at all falls through to ActivateSeat's fresh-spawn path (its own no-op
                // guard against an already-active, never-parked slot is unaffected).
                if (m_population.IsSeatParked(slot: join.Slot)) {
                    if (!m_population.TryResumeParkedSeat(slot: join.Slot, profile: profile, mismatch: out _)) {
                        return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty,
                            Reason: $"slot {join.Slot} is parked by a different identity — it can only resume for the identity that disconnected, or reactivate once its grace window ends");
                    }
                } else {
                    m_population.ActivateSeat(slot: join.Slot, profile: profile);
                }

                StageOwnedState(slot: join.Slot, profile: profile);

                return new SessionReply(Accepted: true, AssignedIndex: (join.Slot + 1), RosterEcho: string.Empty, Reason: string.Empty);
            }
            case SessionRequest.Leave leave:
                if ((uint)leave.Slot >= WorldPopulation.LocalSeatCount) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"slot {leave.Slot} out of range");
                }

                if (m_grants.Allows(principal: leave.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: leave.Slot)) is { IsAllowed: false } leaveVerdict) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"{leave.Principal.Describe()} cannot leave slot {leave.Slot} ({leaveVerdict.DescribeDenial()})");
                }

                m_population.DeactivateSeat(slot: leave.Slot, tick: NextInputTick);

                return new SessionReply(Accepted: true, AssignedIndex: (leave.Slot + 1), RosterEcho: string.Empty, Reason: string.Empty);
            case SessionRequest.SetIdentity setProfile: {
                if (((uint)setProfile.Slot >= WorldPopulation.LocalSeatCount) || (m_profiles.Find(name: setProfile.IdentityName) is not { } profile)) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: "slot or identity not found");
                }

                if (m_grants.Allows(principal: setProfile.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: setProfile.Slot)) is { IsAllowed: false } profileVerdict) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"{setProfile.Principal.Describe()} cannot set the profile of slot {setProfile.Slot} ({profileVerdict.DescribeDenial()})");
                }

                m_population.SetSeatProfile(slot: setProfile.Slot, profile: profile);
                StageOwnedState(slot: setProfile.Slot, profile: profile);

                return new SessionReply(Accepted: true, AssignedIndex: (setProfile.Slot + 1), RosterEcho: string.Empty, Reason: string.Empty);
            }
            case SessionRequest.SetPopulation setPopulation: {
                // A global census lever, not a per-slot one: gated the same way SetPopulationDefaults' document edit
                // is (Mutate over the Population section) rather than a per-body Drive check.
                if (m_grants.Allows(principal: setPopulation.Principal, capability: WorldCapability.Mutate, subject: GrantSubject.Section(section: WorldSection.Population)) is { IsAllowed: false } populationVerdict) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"{setPopulation.Principal.Describe()} cannot mutate section:population ({populationVerdict.DescribeDenial()})");
                }

                var admitted = new List<WorldPeerEventEntry>();
                var disconnected = new List<WorldPeerEventEntry>();
                var applied = m_population.SetSimulatedCount(count: setPopulation.Count, admitted: admitted, disconnected: disconnected);

                ApplyLifecycleEvents(admitted: admitted, disconnected: disconnected, ordered: true);

                return new SessionReply(Accepted: true, AssignedIndex: applied, RosterEcho: string.Empty, Reason: string.Empty);
            }
            case SessionRequest.SetPeerSource setPeerSource:
                if (m_grants.Allows(principal: setPeerSource.Principal, capability: WorldCapability.Mutate, subject: GrantSubject.Section(section: WorldSection.Population)) is { IsAllowed: false } peerSourceVerdict) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: $"{setPeerSource.Principal.Describe()} cannot mutate section:population ({peerSourceVerdict.DescribeDenial()})");
                }

                if (!m_population.TrySetPeerSource(source: setPeerSource.Source, refusal: out var peerSourceRefusal)) {
                    return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: peerSourceRefusal);
                }

                return new SessionReply(Accepted: true, AssignedIndex: -1, RosterEcho: string.Empty, Reason: string.Empty);
            default:
                return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: "unknown session request");
        }
    }

    /// <summary>Composes the authoritative answer to a read-back query.</summary>
    /// <param name="query">The read-back query.</param>
    /// <returns>The authoritative answer.</returns>
    public QueryAnswer Answer(WorldQuery query) {
        ArgumentNullException.ThrowIfNull(argument: query);

        return query switch {
            WorldQuery.PlayerWhere where when (Body(index: (where.Index - 1)) is { } body) => new QueryAnswer(Text: body.DescribeWhere(index: where.Index)),
            WorldQuery.PlayerWhere where => new QueryAnswer(Text: $"[player.where: player {where.Index} is not an active population entry — see world.population]", Refused: true),
            WorldQuery.PlayerChannels channels when (Body(index: (channels.Index - 1)) is { } body) => new QueryAnswer(Text: DescribeChannels(index: channels.Index, bodyIndex: (channels.Index - 1), body: body)),
            WorldQuery.PlayerChannels channels => new QueryAnswer(Text: $"[player.channels: player {channels.Index} is not an active population entry — see world.population]", Refused: true),
            WorldQuery.PlayerState state when (Body(index: (state.Index - 1)) is { } body) => new QueryAnswer(Text: $"[player.state: p{state.Index} identity={(body.Profile?.Id ?? "none")} {body.DescribeActionState()} outputs={DescribeDurableOutputs(entityIndex: (state.Index - 1))} writeback={DescribeDocumentReceipt(body.Profile?.Id)}]"),
            WorldQuery.PlayerState state => new QueryAnswer(Text: $"[player.state: player {state.Index} is not an active population entry — see world.population]", Refused: true),
            WorldQuery.InputHolds => new QueryAnswer(Text: m_inputHold.Describe()),
            WorldQuery.Rules => new QueryAnswer(Text: DescribeRules()),
            WorldQuery.PlayerTargets targets when (Body(index: (targets.Index - 1)) is not null) => new QueryAnswer(Text: m_population.DescribeTargets(bodyIndex: (targets.Index - 1))),
            WorldQuery.PlayerTargets targets => new QueryAnswer(Text: $"[player.targets: player {targets.Index} is not an active population entry — see world.population]", Refused: true),
            WorldQuery.Contacts contacts when (Body(index: (contacts.Index - 1)) is { } body) => new QueryAnswer(Text: DescribeContacts(index: contacts.Index, body: body)),
            WorldQuery.Contacts contacts => new QueryAnswer(Text: $"[world.contacts: body {contacts.Index} is inactive — see world.population]", Refused: true),
            WorldQuery.Properties properties => new QueryAnswer(Text: DescribeProperties(bodyIndex: properties.BodyIndex)),
            WorldQuery.Interactions => new QueryAnswer(Text: DescribeInteractions()),
            _ => new QueryAnswer(Text: string.Empty),
        };
    }

    // The public Answer surface remains the trusted in-process read-back composer. An envelope, however, may have
    // arrived over WorldTcpHost, so it crosses Observe before reaching that composer. Loopback queries are stamped as
    // Console and pass through the same check using the permissive local seed rather than a separate bypass.
    private QueryAnswer AnswerSubmittedQuery(WorldQuery query, WorldPrincipal principal) {
        var subject = query.ObservationSubject();
        var verdict = m_grants.Allows(principal: principal, capability: WorldCapability.Observe, subject: subject);

        if (!verdict.IsAllowed) {
            return new QueryAnswer(Text: $"[query refused: {principal.Describe()} cannot observe {subject.Describe()} ({verdict.DescribeDenial()})]", Refused: true);
        }

        return Answer(query: query);
    }

    private void StageOwnedState(int slot, WorldIdentity? profile) {
        if ((profile is null) || (Body(index: slot) is not { } body)) {
            return;
        }
        var declarations = new List<(string Name, ActionStateKind Kind)>();
        var values = new List<DurableStateValue>();
        body.AppendDurableStateDeclarations(declarations: declarations);
        foreach (var declaration in declarations) {
            if (m_profiles.TryReadDurableState(ownerId: profile.Id, sourceDocumentId: m_definition.DocumentId ?? string.Empty, slot: declaration.Name, kind: declaration.Kind, value: out var value, reason: out _)) {
                values.Add(item: value);
            }
        }
        if (values.Count > 0) {
            _ = body.TryStageDurableState(tick: NextInputTick, values: values, requirePlayerWritable: false, writer: $"world:{profile.Id}", reason: out _);
        }
    }

    private string DescribeDurableOutputs(int entityIndex) {
        var values = m_population.DurableStateOutputs
            .Where(output => output.EntityIndex == entityIndex)
            .Select(output => $"{output.Value.Name}@{output.Tick}");
        var text = string.Join(separator: ",", values: values);
        return (text.Length == 0 ? "none" : text);
    }

    private string DescribeDocumentReceipt(string? ownerId) {
        if ((m_lastDocumentReceipt is not { } receipt) || !string.Equals(a: receipt.Submission.OwnerDocumentId, b: ownerId, comparisonType: StringComparison.Ordinal)) {
            return "none";
        }
        return $"{receipt.Submission.Kind.ToString().ToLowerInvariant()}:{receipt.Submission.Slot}@{receipt.Submission.Tick}:{(receipt.Accepted ? "accepted" : "refused")}({receipt.Reason})";
    }

    /// <summary>Composes the <c>player.channels</c> echo — the fold and held-image join's read-back
    /// (the arithmetic rule lives in <see cref="FixedContributionFold"/>), so a script can tell "the addon asked for more
    /// and the pool held it" apart from "the addon asked for exactly this" without inferring it from displacement
    /// across ticks. Reports every declared channel of <paramref name="bodyIndex"/>'s last write: the folded value
    /// the simulation received, the owning seat's own base <c>h</c>, every contributor that reached it tagged by
    /// principal (trusted/untrusted), the pool ceiling in force, whether the pool actually clamped, the held overlay
    /// admitted later by <see cref="WorldBody"/>, and the value after that overlay composed with the movement tier.</summary>
    /// <param name="index">The 1-based player display index (for the echo's own tag).</param>
    /// <param name="bodyIndex">The 0-based entity index already resolved to a live body.</param>
    /// <param name="body">The live body retaining the later held-overlay decision.</param>
    private string DescribeChannels(int index, int bodyIndex, WorldBody body) {
        // The fold — and this read-back — only ever exists over a HUMAN-OCCUPIED LOCAL SEAT
        // (WorldPopulation.IsHumanOccupied; the whole per-seat retention above is sized WorldPopulation.LocalSeatCount).
        // A population entry (5..128) or an unoccupied local seat is a bot at full authority by construction — there
        // is no base/pool/contributor to report, so say that rather than fabricating one.
        if (!m_population.IsHumanOccupied(bodyIndex: bodyIndex)) {
            return $"[player.channels: p{index} body:{bodyIndex} is not human-occupied — the co-driving pool only ever exists over an occupied local seat (see world.population); nothing folds here]";
        }

        // The route summary — context-routes widening: what target (if any) this seat's channels also reach, its
        // capture policy, and its channel mask, so the same read-back that already shows the fold shows the routing
        // truth beside it (CLAUDE.md's read-back rule: no decision surface without an echoing verb).
        var routePrincipal = WorldPrincipal.Seat(slot: bodyIndex);
        var routeText = ((m_grants.ControlRoute(principal: routePrincipal) is { } route)
            ? $"route={route.Describe()}({(m_grants.RouteCapture(principal: routePrincipal) ? "capture" : "mirror")},mask=0x{m_grants.RouteChannelMask(principal: routePrincipal).Bits:x4})"
            : "route=none");

        var channels = m_population.Channels;
        var h = m_channelReadBase[bodyIndex];
        var folded = m_channelReadFolded[bodyIndex];
        var held = body.ChannelReadHeld;
        var composed = body.ChannelReadComposed;
        var baseSlot = (bodyIndex * ChannelLimits.MaxChannels);
        var contributorBase = (bodyIndex * MaxReadContributorsPerSeat);
        var contributorCount = m_channelReadContributorCount[bodyIndex];
        var segments = new List<string>(capacity: ChannelLimits.MaxChannels);

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            if (!channels.IsDeclared(ordinal: ordinal)) {
                continue;
            }

            var slot = (baseSlot + ordinal);
            var trustedTags = new List<string>();
            var untrustedTags = new List<string>();

            for (var contributor = 0; (contributor < contributorCount); contributor++) {
                var contributorSlot = (contributorBase + contributor);

                if (!m_channelReadContributorMask[contributorSlot].Contains(ordinal: ordinal)) {
                    continue;
                }

                (m_channelReadContributorTrusted[contributorSlot] ? trustedTags : untrustedTags).Add(item: m_channelReadContributor[contributorSlot].Describe());
            }

            var ceiling = m_channelReadCeiling[slot];

            segments.Add(item: $"{channels.Name(ordinal: ordinal)}:{ShapeWord(shape: channels.Shape(ordinal: ordinal))} folded={folded[ordinal]}({folded[ordinal].Value}) h={h[ordinal]}({h[ordinal].Value}) held={held[ordinal]}({held[ordinal].Value}) composed={composed[ordinal]}({composed[ordinal].Value}) trusted=[{string.Join(separator: ",", values: trustedTags)}] untrusted=[{string.Join(separator: ",", values: untrustedTags)}] ceiling={((ceiling > 0) ? $"{FixedQ4816.FromRawBits(value: ceiling)}({ceiling})" : "none")} clamped={(m_channelReadClamped[slot] ? "yes" : "no")}");
        }

        return $"[player.channels: p{index} {routeText} {string.Join(separator: " | ", values: segments)}]";
    }

    // The lowercase shape word the fold's own read-back names a channel's shape with — the single place these words
    // are produced, never re-derived elsewhere.
    private static string ShapeWord(ChannelShape shape) => shape switch {
        ChannelShape.Unipolar => "unipolar",
        ChannelShape.Binary => "binary",
        _ => "bipolar",
    };

    /// <summary>Advances the authoritative world by one exact host tick: run every mounted addon's guest code first (see
    /// <see cref="WorldAddonRuntime.TickAddons"/>, which applies nothing) → drain the buffered live edits (mutations,
    /// swaps, undo), applying each at the tick boundary and delivering the new definition once if any applied → drain
    /// the tick's submitted intents → apply the addons' staged contributions
    /// (<see cref="WorldAddonRuntime.ApplyContributions"/>) → fold every human-occupied body's tick (see
    /// <see cref="FoldChannelContributions"/>) → settle per-body contention over the tick as a whole → advance every
    /// body (peers, then seats) → resolve the addons' reads against the stepped state
    /// (<see cref="WorldAddonRuntime.ResolveReads"/>) → deliver the tick's <see cref="WorldSnapshot"/>.</summary>
    /// <remarks>The three addon points are pinned, and each is pinned for a reason: guests run before anything is
    /// applied so a guest's own effect never depends on where in the tick it happened to be pumped; reads resolve
    /// after the step of the tick they were written in, so a verdict, a minted handle, and a pose all describe the
    /// same settled instant. <b>An addon's contribution to a human-occupied body is never a plain overwrite of the
    /// seat's own submission (<see cref="FixedContributionFold"/>).</b> <see cref="ApplyIntentSubmission"/> routes a
    /// non-owning contributor into a per-tick contribution set instead of calling <see cref="WorldBody.SubmitIntent"/>
    /// directly, and <see cref="FoldChannelContributions"/> — the fourth point, run once contributions have finished
    /// landing and before the population advances — folds each occupied body's owning-seat base with its tick's
    /// pooled/unpooled contributions into the single value <see cref="WorldBody.SubmitIntent"/> receives. An
    /// unoccupied body (no seat, or an inactive one) is untouched by any of this and keeps plain overwrite
    /// semantics, because occupancy is what makes a pool exist at all (a bot at full authority is not an oversight
    /// there). <see cref="WorldBody.NextIntent"/>'s tape-outranks-submitted ladder is itself untouched; only how the
    /// submitted tier is produced differs by occupancy.</remarks>
    /// <param name="context">The launcher's fixed-step context for this tick.</param>
    public void Step(in FixedStepContext context) {
        lock (m_authorityGate) {
            StepCore(context: in context);
        }
    }

    private static string DescribeContacts(int index, WorldBody body) {
        var normal = body.LastObstructionNormal;
        var obstruction = ((normal == FixedVector3.Zero)
            ? "none"
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"({(double)normal.X:0.###},{(double)normal.Y:0.###},{(double)normal.Z:0.###})"));

        return string.Create(provider: CultureInfo.InvariantCulture, handler: $"[world.contacts: p{index} grounded={(body.Grounded ? "true" : "false")} planarSpeed={body.PlanarSpeed:0.00} resolved={body.ContactCount} submerged={(body.Submerged ? "true" : "false")} atSurface={(body.AtSurface ? "true" : "false")} obstruction={obstruction}]");
    }

    private void StepCore(in FixedStepContext context) {
        // The per-tick mutation-dispatch allowance opens HERE, before either half of the tick that spends it: the
        // addon seam's pre-flight (TickAddons, immediately below) and the drain that applies what it — and every peer
        // submission buffered since the last step — enqueued.
        m_mutationBudget.BeginTick();
        m_addons?.TickAddons(tick: (context.Tick + 1UL));
        _ = DrainPendingOps(tick: context.Tick);
        TransferForwarder?.ResolveContinuations(source: this);
        m_inputHold.PrepareParticipants(population: m_population);

        m_tickWrittenCount = 0;

        while (m_intents.TryDequeue(result: out var submission)) {
            if (Body(index: submission.EntityIndex) is not { } body) {
                continue;
            }

            _ = ApplyIntentSubmission(body: body, submission: in submission);
        }

        ApplyFederatedIntents();

        m_addons?.ApplyContributions(tick: (context.Tick + 1UL));
        FoldChannelContributions();
        m_inputHold.Apply(population: m_population);

        // Settle m_contended for real now that the WHOLE tick's writers have run — the seat drain AND the addon
        // contributions: a queue's dequeue order says nothing about whether a body was genuinely contended for the tick
        // as a whole, only ReportContention's own observation of the FULL set could (see its remarks) — this is that
        // observation, applied once per tracked entity rather than mid-drain.
        for (var index = 0; (index < m_tickWrittenCount); index++) {
            m_contended[m_tickWrittenEntity[index]] = m_tickCollided[index];
        }

        // The context-sensitive-button interception's eligibility pass (the RPG A-button) — resolved against the
        // PRE-MOVE positions (this tick's population has not advanced yet), so a rising edge computed inside
        // AdvanceSeats below diverts into an Engage instead of ever reaching the avatar's action track.
        Span<int> engageProbeOrdinals = stackalloc int[WorldPopulation.LocalSeatCount];
        Span<int> engageProbeScreens = stackalloc int[WorldPopulation.LocalSeatCount];
        Span<bool> engageEdges = stackalloc bool[WorldPopulation.LocalSeatCount];

        ResolveEngageProbes(ordinals: engageProbeOrdinals, screens: engageProbeScreens);

        var tick = (context.Tick + 1UL);
        m_population.Adjacencies?.BeginTick(tick: tick);
        var stepStartEngineTick = (context.ElapsedTicks - context.StepTicks);
        m_population.AdvanceSimulated(tick: tick, stepTicks: context.StepTicks, stepStartEngineTick: stepStartEngineTick);
        m_population.AdvanceSeats(tick: tick, stepTicks: context.StepTicks, stepStartEngineTick: stepStartEngineTick, engageProbeOrdinals: engageProbeOrdinals, engageEdges: engageEdges);
        m_population.ResolveDynamicContacts();
        m_population.CompleteStep(tick: tick);
        foreach (var designation in m_population.DesignationOutputs) {
            _ = ApplyDesignationCore(designation: designation, principal: WorldPrincipal.Console, knownSubject: true, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0);
        }
        m_population.ClearDesignationOutputs();

        // Kit-fired `generate` effects, staged during THIS tick's advance and enqueued through the ORDINARY mutation
        // pipeline for the NEXT tick's drain — the same door a console world.generate and a world rule both use, so
        // one mechanism covers all three rather than three. The one-tick latency is real and reported: this is the
        // first ActionEffect to write the DOCUMENT rather than per-body state, so it is the first to pay the
        // pipeline's own round trip. The acting principal is WorldPrincipal.World whichever body fired it — the
        // effect is the world's authored program acting, not the seat (see that principal's remarks).
        foreach (var invocation in m_population.GeneratorInvocationOutputs) {
            EnqueueMutation(mutation: new WorldMutation.Generate(Principal: WorldPrincipal.World, Row: invocation.Row));
        }

        m_population.ClearGeneratorInvocationOutputs();
        if (m_population.DurableStateOutputs.Count > 0) {
            DurableStateOutputTap?.Invoke(obj: m_population.DurableStateOutputs);
            foreach (var output in m_population.DurableStateOutputs) {
                var submission = new WorldDocumentSubmission(
                    SourceDocumentId: m_definition.DocumentId ?? string.Empty,
                    OwnerDocumentId: output.PlayerId,
                    Tick: output.Tick,
                    Slot: output.Value.Name,
                    Kind: output.Kind,
                    StorageKind: output.StorageKind,
                    Value: (output.StorageKind == ActionStateKind.Counter ? output.Value.Value.Value : checked((long)output.Value.TimerTicks)));
                m_lastDocumentReceipt = m_profiles.Submit(submission: submission);
                DocumentSubmissionTap?.Invoke(obj: m_lastDocumentReceipt.Value);
            }
        }

        // Route every fired probe into an ordinary Engage, through the SAME authority path a manual player.engage
        // takes — see ResolveEngageProbes for why this is expected to succeed (its own eligibility pass already
        // re-checks CheckEngage), so a denial here can only mean the grant table changed between the two passes on
        // this single-threaded step (an admin revoke applied in between — not a concurrent race, the step runs one
        // thread) — rare enough to accept as a swallowed press rather than a second suppression path.
        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            if (!engageEdges[slot]) {
                continue;
            }

            var principal = WorldPrincipal.Seat(slot: slot);
            var target = GrantSubject.Screen(index: engageProbeScreens[slot]);

            if (m_engagement.Engage(entityIndex: slot, target: target, capture: true, actingPrincipal: principal, targetPrincipal: principal)) {
                Console.Error.WriteLine(value: $"[world.engage: {principal.Describe()} auto-engaged {target.Describe()} — context button]");
            }
        }

        // Collect this tick's world-scoped events AFTER the population settles (so positions/occupancy are this
        // tick's) and BEFORE the addon read pump, so ResolveReads can stage them into the SAME batch as this tick's
        // disclosures/answers.
        m_events.Collect(definition: m_definition, population: m_population);

        // World rules evaluate HERE — after the event feed (so a $region gate reads this tick's settled occupancy)
        // and before the addon read pump and the snapshot (so a rule's write is visible to the same tick's guest
        // reads and delivery).
        EvaluateWorldRules(tick: tick, stepTicks: context.StepTicks);
        // Escrow recovery evaluates on the SAME terms, right beside rules — see ReclaimExpiredEscrows' own remarks.
        ReclaimExpiredEscrows(tick: tick);
        m_transferEscrow.ReclaimExpired(tick: tick);
        // Market deadline recovery — the SAME tick-driven, replay-deterministic shape ReclaimExpiredEscrows already
        // establishes, for a listing that reached its deadline instead of an unaccepted ownership offer's.
        SettleExpiredMarketListings(tick: tick);
        // Market retention sweep — runs right beside deadline recovery, archiving terminal rows once they have aged
        // past market.retentionSeconds so the section's lifetime listing count stays bounded.
        PruneExpiredMarketListings(tick: tick);
        // Reconnect-park recovery — the SAME tick-driven, replay-deterministic shape ReclaimExpiredEscrows already
        // establishes, for a disconnected body's deferred teardown instead of an unaccepted ownership offer's.
        m_population.ReclaimExpiredParks(tick: tick);
        m_addons?.ResolveReads(tick: (context.Tick + 1UL));
        // Fold this tick's routed intents into their targets BEFORE the snapshot is built.
        m_engagement.FoldTick();

        // Step every booted machine off THIS tick's freshly-folded pads: reads WorldEngagement.BuildPadSnapshot()
        // directly, in-process, no client/wire round-trip. Runs in EVERY boot shape via WorldServerStepShell.Step
        // (headless and windowed alike both call WorldServer.Step) — ROM state IS sim state, not presentation-fed.
        // context.StepTicks is forwarded exactly, preserving the exact-rational T-cycle bridge.
        m_machines.Advance(stepTicks: context.StepTicks, pads: m_engagement.BuildPadSnapshot());

        // A body-target route's contribution lands on the TARGET's NEXT tick — FoldTick runs after this tick's
        // population has already advanced, so there is no earlier point this tick where the target could still fold
        // it in. Queued through the ordinary intent path (never LoopbackTransport's IntentTap), so it is re-derived at
        // replay time rather than taped directly — see WorldEngagement's class remarks on replay visibility.
        foreach (var contribution in m_engagement.BodyContributions) {
            EnqueueIntent(submission: new IntentSubmission(
                Tick: (context.Tick + 2UL),
                EntityIndex: contribution.TargetBody,
                Intent: contribution.Intent,
                Principal: contribution.Principal
            ));
        }

        EmitSnapshot(tick: (context.Tick + 1UL), stepTicks: context.StepTicks);
        m_lastCompletedTick = (context.Tick + 1UL);
        m_lastStepTicks = context.StepTicks;
        m_lastCompletedEngineTicks = context.ElapsedTicks;
    }

    /// <summary>Returns the context-sensitive-button interception's eligibility pass (the RPG A-button, <c>CLAUDE.md</c>'s
    /// overworld intent) — for each active, un-routed local seat, the first (document order) screen that is
    /// engageable and backed by a live booted machine (the real gate is <see cref="CheckScreenEngagePolicy"/>'s
    /// <see cref="WorldMachineHost.HasMachine"/> check — the authoritative server-side boot signal; the host boots and
    /// steps the machine in-process, so this project sees the real boot directly rather than a document-declared
    /// proxy), names an <see cref="WorldScreenRoute.EngageChannel"/> this world's channel table resolves, carries no live occupant
    /// (<see cref="WorldEngagement.PlayersOn"/> empty), sits within <see cref="WorldScreenRoute.EngageRadius"/> of the
    /// seat's pre-move position (this tick's population has not advanced yet — <c>Step</c> calls this before
    /// <see cref="WorldPopulation.AdvanceSeats"/>), and would actually pass <see cref="WorldEngagement.CheckEngage"/>.
    /// <para>
    /// <see cref="WorldEngagement.Engage"/>'s own remarks leave engageable/proximity/machine policy to the caller
    /// (ordinarily the client, ahead of a manual <c>player.engage</c>'s submission) — this is that same policy,
    /// resolved here instead, from document and grant state alone. Pure sim state in, pure sim state out: a shadow
    /// replay re-derives the identical decision at the identical tick from the identical taped inputs, with nothing
    /// new to tape — the same "re-derived, not recorded" shape <see cref="WorldEngagement"/>'s own body-route
    /// contributions already establish (see its class remarks).
    /// </para></summary>
    /// <param name="ordinals">Per-seat-slot output: the channel ordinal to probe this tick, or <c>-1</c> for none —
    /// filled entirely (every slot without an eligible screen reads <c>-1</c>, the zero-cost default every world
    /// without an <c>engageChannel</c>-bearing screen takes, which is every shipped world today).</param>
    /// <param name="screens">Per-seat-slot output: the eligible screen's engine index paired with <paramref name="ordinals"/>'s
    /// entry, or <c>-1</c> alongside a <c>-1</c> ordinal.</param>
    private void ResolveEngageProbes(Span<int> ordinals, Span<int> screens) {
        ordinals.Fill(-1);
        screens.Fill(-1);

        var screenRows = m_definition.Screens;

        for (var slot = 0; (slot < WorldPopulation.LocalSeatCount); slot++) {
            if ((Body(index: slot) is not { } body) || body.Engaged) {
                continue;
            }

            var principal = WorldPrincipal.Seat(slot: slot);

            // A seat already routed somewhere (captured OR mirrored) keeps that ONE route — Engage re-points rather
            // than stacking, and re-pointing an active possession/mirror off an unrelated button press is not this
            // feature's job.
            if (m_grants.ControlRoute(principal: principal) is not null) {
                continue;
            }

            for (var index = 0; (index < screenRows.Count); index++) {
                var screen = screenRows[index];

                if ((screen.Route.EngageChannel is not { Length: > 0 } channelName)
                    || !CheckScreenEngagePolicy(entityIndex: slot, screen: screen, reason: out _)) {
                    continue;
                }

                if (!m_population.Channels.TryGetOrdinal(name: channelName, ordinal: out var ordinal)) {
                    continue;
                }

                if (m_engagement.PlayersOn(screenIndex: screen.Index).Count > 0) {
                    continue;
                }

                if (!m_engagement.CheckEngage(target: GrantSubject.Screen(index: screen.Index), actingPrincipal: principal).IsAllowed) {
                    continue;
                }

                ordinals[slot] = ordinal;
                screens[slot] = screen.Index;

                break;
            }
        }
    }

    private bool CheckEngagePolicy(int entityIndex, GrantSubject target, out string reason) {
        reason = string.Empty;

        if (target.Kind != GrantSubjectKind.Screen) {
            return true;
        }

        foreach (var screen in m_definition.Screens) {
            if (screen.Index == target.Value) {
                return CheckScreenEngagePolicy(entityIndex: entityIndex, screen: screen, reason: out reason);
            }
        }

        reason = $"screen {target.Value} does not exist";
        return false;
    }

    private bool CheckScreenEngagePolicy(int entityIndex, WorldScreen screen, out string reason) {
        if (!screen.Route.Engageable) {
            reason = $"screen {screen.Index} is not engageable";
            return false;
        }

        if (!m_machines.HasMachine(index: screen.Index)) {
            reason = $"screen {screen.Index} has no machine to control";
            return false;
        }

        if (Body(index: entityIndex) is not { } body) {
            reason = $"body {entityIndex} is not live";
            return false;
        }

        var position = body.FixedPosition;
        var delta = new FixedVector2(
            X: (position.X - FixedQ4816.FromDouble(value: screen.Origin.X)),
            Y: (position.Z - FixedQ4816.FromDouble(value: screen.Origin.Z))
        );
        var radius = FixedQ4816.FromDouble(value: screen.Route.EngageRadius);

        if (delta.LengthSquared > (radius * radius)) {
            reason = $"body {entityIndex} is outside screen {screen.Index}'s engage radius ({screen.Route.EngageRadius.ToString(System.Globalization.CultureInfo.InvariantCulture)})";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    // Re-materializes every live federation stream's latest device state into this authority tick. A row is
    // accepted only while the same peer principal still occupies its slot; an onward transfer leaves the old row
    // inert, and slot reuse can never inherit it. ApplyIntentSubmission remains the one Drive/grant/input-hold door.
    private void ApplyFederatedIntents() {
        for (var index = 0; index < m_federatedIntents.Length; index++) {
            ref readonly var state = ref m_federatedIntents[index];

            if (!state.Active || (Body(index: index) is not { } body) ||
                !m_population.IsAdmittedPeer(bodyIndex: index) || (m_population.PeerPrincipal(index: index) != state.Principal)) {
                continue;
            }

            var submission = state.Submission with { EntityIndex = index, Principal = state.Principal };
            _ = ApplyIntentSubmission(body: body, submission: in submission);
        }
    }

    /// <summary>Applies one submission to a live body under the per-tick Drive check, and returns the verdict that
    /// decided it. The one write path every intent producer shares — the seat drain and every mounted addon's staged
    /// contributions — so authority, the fold routing below, and the denial latch can never diverge between them.
    /// <para>A submission whose principal does not hold <see cref="WorldCapability.Drive"/> over the target body
    /// applies nothing and is reported once per denial episode (a revoked driver keeps submitting; the first
    /// refused tick logs, then the body idles until re-granted). Allocation-free, O(1). The line prints the
    /// verdict's reason, so distinct denial causes such as "exclusively reserved by seat1" and "no grant names it"
    /// surface as distinct messages. The <c>m_driveDenied</c> reporting latch stays deliberately outside the
    /// verdict.</para>
    /// <para>An allowed submission then routes one of two ways, because one body has exactly one base: the
    /// participant that owns it. The body's owning seat or peer (its principal index equals the entity index) — or
    /// any principal when the body is not human-occupied (<see cref="WorldPopulation.IsHumanOccupied"/>: an
    /// unoccupied body is a bot at full authority by construction) — writes through
    /// <see cref="WorldBody.SubmitIntent"/>, which overwrites, and this tick's write is tracked for contention
    /// reporting. Everything else — an addon's contribution, or a different seat co-driving a body it does not own
    /// — is staged into the per-tick contribution set instead (<see cref="StageContribution"/>, which carries both
    /// the submission's intent and its held-channel composition image) and folded later by
    /// <see cref="FoldChannelContributions"/>; it is never tracked as contention, because a consented (or
    /// default-denied) contribution is a deliberate composition path, not a race.</para></summary>
    /// <param name="body">The live body the submission targets — the caller resolves it, because a submission
    /// naming an entity that holds no body is not an authority outcome and must not be answered as one.</param>
    /// <param name="submission">The tick, entity index, principal, intent, and held-lane image.</param>
    /// <returns>The verdict that decided the check; nothing was applied unless it allows.</returns>
    /// <remarks>A body carrying a nonzero cell on a <see cref="WorldStateRow.GatesDrive"/> row
    /// (<see cref="TryDriveGateVerdict"/>, resynced from live document state — see
    /// <see cref="WorldGrants.SyncState"/>) has its intent refused before the grant table is checked, regardless of
    /// any Drive hold, including an exclusive reservation: a status effect is a fact about the body, not about who
    /// is allowed to drive it, so it outranks a principal that genuinely holds Drive. No rule or effect touches the
    /// grant table to express this; the check reads the state fact directly, the same "deciding fact beyond the
    /// static grant table" shape <see cref="GrantRule.OwnershipHold"/> reads a different fact through. The gate is
    /// released, never latched: once the gate row's cell reads zero, this check passes straight through to the
    /// ordinary <see cref="WorldGrants.Allows"/> call below. <see cref="ApplyCommand"/>'s generic Drive gate checks
    /// the same <see cref="TryDriveGateVerdict"/> before its own <see cref="WorldGrants.Allows"/> call, so a
    /// scripted tape segment (<c>player.fly</c>/<c>EnqueueSegment</c>) is refused by the same fact a raw per-tick
    /// channel submission is.</remarks>
    internal GrantVerdict ApplyIntentSubmission(WorldBody body, in IntentSubmission submission) {
        if (TryDriveGateVerdict(bodyIndex: submission.EntityIndex, verdict: out var gated)) {
            if (!m_driveDenied[submission.EntityIndex]) {
                Console.Error.WriteLine(value: $"[world.grant denied: {submission.Principal.Describe()} cannot drive body:{submission.EntityIndex} ({gated.DescribeDenial()}) — intent dropped, body idle]");
                m_driveDenied[submission.EntityIndex] = true;
            }

            return gated;
        }

        var verdict = m_grants.Allows(principal: submission.Principal, capability: WorldCapability.Drive, subject: GrantSubject.Body(index: submission.EntityIndex));

        if (!verdict.IsAllowed) {
            if (!m_driveDenied[submission.EntityIndex]) {
                Console.Error.WriteLine(value: $"[world.grant denied: {submission.Principal.Describe()} cannot drive body:{submission.EntityIndex} ({verdict.DescribeDenial()}) — intent dropped, body idle]");
                m_driveDenied[submission.EntityIndex] = true;
            }

            return verdict;
        }

        m_driveDenied[submission.EntityIndex] = false;
        m_inputHold.ObserveMeasurement(submission: in submission);

        var bodyIndex = submission.EntityIndex;
        var isOwningParticipant = (((submission.Principal.Kind == PrincipalKind.Seat) || (submission.Principal.Kind == PrincipalKind.Peer)) && (submission.Principal.Index == bodyIndex));
        var isOwningSeat = ((submission.Principal.Kind == PrincipalKind.Seat) && (submission.Principal.Index == bodyIndex));
        var occupied = m_population.IsHumanOccupied(bodyIndex: bodyIndex);

        if (isOwningParticipant || !occupied) {
            ReportContention(entityIndex: bodyIndex, principal: submission.Principal);
            body.SubmitIntent(intent: submission.Intent);
            body.SetHeldChannels(channels: submission.HeldChannels);

            if (isOwningSeat) {
                // This tick's `h` and its held-device image — never the ladder's winner (a tape still outranks the
                // former; see WorldBody.NextIntent). Recorded even when nothing ends up contributing this tick, so
                // FoldChannelContributions's common-case check (m_hasContribution) stays the only extra cost an
                // uncontended body pays. The held image is recorded because the fold may have to REPLACE the direct
                // write above with the max of it and a contributor's own composition act.
                m_ownerBase[bodyIndex] = submission.Intent;
                m_ownerHeld[bodyIndex] = submission.HeldChannels;
                m_hasOwnerBase[bodyIndex] = true;
                // The read-back's baseline for THIS write: no pool, no contributors — true unless
                // FoldChannelContributions (below, later this same tick) overwrites it with a real fold, because a
                // contribution actually landed. Reset rather than left stale, so a body contended two ticks ago and
                // quiet since does not keep reporting a pool that no longer exists.
                RecordDirectChannelRead(seat: bodyIndex, intent: submission.Intent);
            }
        } else {
            StageContribution(bodyIndex: bodyIndex, principal: submission.Principal, submission: in submission);
        }

        return verdict;
    }

    /// <summary>Determines whether <paramref name="bodyIndex"/> carries a nonzero cell on a state row declaring
    /// <see cref="WorldStateRow.GatesDrive"/> — Composition-core's CC/death gating (Seam A), the one rule both
    /// Drive-admission doors consult (<see cref="ApplyIntentSubmission"/>'s per-tick channel submission,
    /// <see cref="ApplyCommand"/>'s generic Drive gate over an authority command such as
    /// <c>EnqueueSegment</c>/<c>SnapPose</c>). The door reads state — this never touches the grant table, and neither
    /// caller consults it before this check: a status effect refuses regardless of what <see cref="WorldGrants.Allows"/>
    /// would otherwise answer, including for a principal that genuinely holds Drive (an exclusive reserver
    /// included).</summary>
    /// <param name="bodyIndex">The 0-based entity index the command/submission targets.</param>
    /// <param name="verdict">The <see cref="GrantRule.DriveGated"/> verdict, when gated.</param>
    /// <returns><see langword="true"/> when the body is gated — the caller must refuse without consulting the grant
    /// table at all.</returns>
    /// <remarks>The complete ingress inventory obliged to call this before admitting a drive (a new drive ingress is
    /// obliged to call it too — that is what keeps a two-call-site pattern honest over time): <see cref="ApplyIntentSubmission"/>
    /// — seat-channel submissions, addon FoldActs, the unoccupied-body bot at full authority, and co-drive folds all
    /// land there — and <see cref="ApplyCommand"/>, the command-shaped drive path. Two call-sites, one rule.</remarks>
    private bool TryDriveGateVerdict(int bodyIndex, out GrantVerdict verdict) {
        if (m_grants.TryGetDriveGate(bodyIndex: bodyIndex, gateRow: out var gateRow)) {
            verdict = new GrantVerdict(Rule: GrantRule.DriveGated, GateRow: gateRow);

            return true;
        }

        verdict = default;

        return false;
    }

    // Buffers one non-owning principal's contribution to a human-occupied body's per-tick contribution set, raw
    // Int64 accumulation only (see FixedContributionFold's remarks on why: never through a saturating operator).
    // BOTH halves of the submission ride: the movement/analog `Intent` accumulates into the tick's sums, and the
    // HeldChannels composition image accumulates into m_contributedHeld via WorldChannelTable.ComposeHeld's shape rule
    // — max for unipolar/binary, RAW UNCLAMPED SUM for bipolar (see that method's own remarks on why this accumulator
    // must not clamp per contributor) — a contributor's composition act is an act; dropping it here would make a
    // guest's press vanish the moment a tape drives the body it is pressing on.
    //
    // TRUSTED-BY-AUTHORSHIP: classification keys on HOST LOCUS, not on
    // principal KIND by coincidence of vocabulary alone. THREE terms exist today:
    //   - Console/Seat (another seat co-driving the body it does not own; a console press once one reaches this
    //     path): a human's own tool, added OUTSIDE the pool, wholly UNMASKED — no reach, no ceiling.
    //   - A document-mounted Addon: WORLD LOGIC authored by the world itself (every mounted addon today runs on
    //     Puck.World.Server/WorldAddonRuntime — the Simulation lane — so PrincipalKind.Addon alone already names
    //     that host locus; a FUTURE client-hosted addon would need its own kind here, never a silent share of this
    //     one). Also added OUTSIDE the pool — consent does not apply to world logic (a world doesn't ask permission
    //     to apply wind) — but unlike Console/Seat its term still respects its OWN declared Reach (DATA describing
    //     which channels the world logic touches, never a security boundary the occupying seat must consent to): an
    //     addon that declares nothing still contributes nothing. Fuel/budget remain robustness bounds regardless
    //     (WorldGrants' metering of an untrusted-for-administration principal is unchanged by this reclassification).
    //   - Genuinely untrusted principals (a Peer today; a future client-hosted addon would join this branch) stay
    //     POOLED under Reach ∧ Consent exactly as before: default-deny per channel, needing BOTH the contributor's
    //     own row to REACH the channel AND the OCCUPYING SEAT to have authored a ceiling for it. A channel missing
    //     either contributes nothing, silently — the addon's own act already carries a verdict from
    //     WorldAddonRuntime.FoldActs; a per-channel miss is a quieter refinement of the same "requested, not
    //     received" shape, not a second refusal channel. An ordinal accepted through the POOLED branch alone marks
    //     m_untrustedAcceptedMask, regardless of the delta's own value — a cancelling pair of contributors must
    //     still read back as "the pool was reached," never as "nothing happened" (player.channels' ceiling report).
    private void StageContribution(int bodyIndex, WorldPrincipal principal, in IntentSubmission submission) {
        var isConsoleOrSeat = (principal.Kind is PrincipalKind.Console or PrincipalKind.Seat);
        var isAddon = (principal.Kind == PrincipalKind.Addon);
        var trustedInFold = (isConsoleOrSeat || isAddon);
        var subject = GrantSubject.Body(index: bodyIndex);
        var reach = default(ChannelReachMask);
        var hasReach = (!isConsoleOrSeat && m_grants.TryGetChannelReach(principal: principal, subject: subject, mask: out reach));
        // The occupying seat's OWN authored ceilings only ever bound the POOLED (genuinely untrusted) path — a
        // trusted addon's own declared Reach is the whole gate; there is no seat consent for it to consult.
        var ceilings = ((!isConsoleOrSeat && !isAddon) ? m_grants.PoolCeilings(seat: WorldPrincipal.Seat(slot: bodyIndex), subject: subject) : default);
        var eligible = (!hasReach
            ? default
            : (isAddon ? new ChannelHeldMask(Bits: reach.Bits) : reach.Meet(consent: ceilings.Support)));

        if (!m_hasContribution[bodyIndex]) {
            // First touch THIS tick for this seat: the read-back's contributor list starts a fresh episode here — the
            // one place a new episode's stale rows (left over from whichever earlier tick last touched this seat) must
            // be dropped before RecordContributor appends fresh ones. ClearContribution (below) wipes the sums and
            // held image once the fold has READ them (no ceiling: that is durable grant-table state, not a per-tick
            // accumulator); it never touches the read-back, which must survive past that point.
            m_channelReadContributorCount[bodyIndex] = 0;
        }

        m_hasContribution[bodyIndex] = true;

        var baseSlot = (bodyIndex * ChannelLimits.MaxChannels);
        var acceptedMask = default(ChannelHeldMask);
        var untrustedAccepted = m_untrustedAcceptedMask[bodyIndex];

        for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
            var slot = (baseSlot + ordinal);
            var delta = submission.Intent[ordinal].Value;
            var held = submission.HeldChannels[ordinal].Value;

            if ((delta == 0L) && (held == 0L)) {
                continue;
            }

            var shape = (m_population.Channels.IsDeclared(ordinal: ordinal) ? m_population.Channels.Shape(ordinal: ordinal) : ChannelShape.Bipolar);
            var isBipolar = (shape == ChannelShape.Bipolar);

            if (isConsoleOrSeat || (isAddon && eligible.Contains(ordinal: ordinal))) {
                // Trusted, outside the pool: Console/Seat unmasked; a document-mounted Addon gated by its OWN
                // declared Reach only.
                m_trustedSum[slot] += delta;
                // Deferred clamp: bipolar sums raw and unclamped (WorldChannelTable.ComposeHeld applies the ONE clamp
                // later, in FoldChannelContributions); unipolar/binary max as before.
                m_contributedHeld[slot] = (isBipolar ? (m_contributedHeld[slot] + held) : Math.Max(val1: m_contributedHeld[slot], val2: held));
                acceptedMask = acceptedMask.With(ordinal: ordinal);
            } else if (!trustedInFold && eligible.Contains(ordinal: ordinal)) {
                // Genuinely untrusted (pooled): Reach ∧ Consent.
                m_untrustedSum[slot] += delta;
                m_contributedHeld[slot] = (isBipolar ? (m_contributedHeld[slot] + held) : Math.Max(val1: m_contributedHeld[slot], val2: held));
                acceptedMask = acceptedMask.With(ordinal: ordinal);
                untrustedAccepted = untrustedAccepted.With(ordinal: ordinal);
            }
        }

        m_untrustedAcceptedMask[bodyIndex] = untrustedAccepted;

        if (!acceptedMask.IsEmpty) {
            RecordContributor(bodyIndex: bodyIndex, principal: principal, trusted: trustedInFold, channelMask: acceptedMask);
        }
    }

    // Find-or-add PRINCIPAL's read-back contributor row within bodyIndex's slice, merging channel-mask bits when the
    // SAME principal reaches this method more than once THIS tick (a guest whose separate acts each touch one
    // channel). Past MaxReadContributorsPerSeat the read-back saturates — the same diagnostic-degrades trade
    // ReportContention makes above — rather than resizing on the contribution path.
    private void RecordContributor(int bodyIndex, WorldPrincipal principal, bool trusted, ChannelHeldMask channelMask) {
        var baseSlot = (bodyIndex * MaxReadContributorsPerSeat);
        var count = m_channelReadContributorCount[bodyIndex];

        for (var index = 0; (index < count); index++) {
            var slot = (baseSlot + index);

            if (m_channelReadContributor[slot] == principal) {
                m_channelReadContributorMask[slot] = m_channelReadContributorMask[slot].Union(other: channelMask);

                return;
            }
        }

        if (count < MaxReadContributorsPerSeat) {
            var slot = (baseSlot + count);

            m_channelReadContributor[slot] = principal;
            m_channelReadContributorTrusted[slot] = trusted;
            m_channelReadContributorMask[slot] = channelMask;
            m_channelReadContributorCount[bodyIndex] = (count + 1);
        }
    }

    // Resets bodyIndex's read-back to "no pool, no contributors" and records the direct-write outcome (h == folded ==
    // the intent SubmitIntent actually received) — the owning seat's own write in ApplyIntentSubmission, before
    // FoldChannelContributions gets a chance to run for this seat this same tick. Left standing when no contribution
    // ever lands; overwritten by the real fold breakdown when one does (see FoldChannelContributions).
    private void RecordDirectChannelRead(int seat, PlayerIntent intent) {
        m_channelReadBase[seat] = intent;
        m_channelReadFolded[seat] = intent;
        m_channelReadContributorCount[seat] = 0;

        var start = (seat * ChannelLimits.MaxChannels);

        Array.Clear(array: m_channelReadCeiling, index: start, length: ChannelLimits.MaxChannels);
        Array.Clear(array: m_channelReadClamped, index: start, length: ChannelLimits.MaxChannels);
    }

    /// <summary>Runs the fold phase (<see cref="FixedContributionFold"/>) once per tick,
    /// after every seat submission and every mounted addon's contribution has landed (see <see cref="Step"/>) and
    /// before the population advances. For each human-occupied local seat that received at least one contribution
    /// this tick, folds its owning seat's own base <c>h</c> (zero when the seat submitted nothing this tick) with the
    /// tick's pooled untrusted sum and unpooled trusted sum, per channel, and calls <see cref="WorldBody.SubmitIntent"/>
    /// once with the composed result — replacing the pass-through write <see cref="ApplyIntentSubmission"/> already
    /// made for the owning seat's own submission. The held-device image is composed the same pass by
    /// <see cref="WorldChannelTable.ComposeHeld"/>'s shape-aware rule (see <see cref="WorldBody.SetHeldChannels"/>: a
    /// unipolar/binary channel joins by maximum — a {0, One} overlay, so a contributor's composition act joins the
    /// seat's the way two simultaneous composition contributors already join inside <see cref="WorldBody"/> — a
    /// bipolar channel instead sums, so a resting contributor can never overwrite a genuinely negative held value).
    /// The pool is the occupying seat's authored limit on how far contributors that human did not authorize may pull
    /// its value away from <c>h</c>. Another co-driving seat is a trusted human tool, so its term is added outside the
    /// pool and consumes none of that ceiling. Occupancy is load-bearing: only a human-occupied body has an owning
    /// seat whose consent can define a pool; an unoccupied bot stays on the full-authority overwrite path.
    /// An occupied body with no contribution this tick (the overwhelming common case) is untouched here: <see
    /// cref="ApplyIntentSubmission"/>'s own direct writes already stand, so this method costs one bool check per
    /// seat.</summary>
    private void FoldChannelContributions() {
        for (var seat = 0; (seat < WorldPopulation.LocalSeatCount); seat++) {
            if (m_hasContribution[seat] && m_population.IsHumanOccupied(bodyIndex: seat) && (Body(index: seat) is { } body)) {
                var h = (m_hasOwnerBase[seat] ? m_ownerBase[seat] : default);
                var ownerHeld = (m_hasOwnerBase[seat] ? m_ownerHeld[seat] : default);
                var folded = h;
                var held = ownerHeld;
                var baseSlot = (seat * ChannelLimits.MaxChannels);
                // The pool ceilings THIS SEAT authored — one number per channel, read once per folded body. Empty
                // when the seat authored none; StageContribution already refused every untrusted delta in that case,
                // so an empty vector can only be reached with a trusted-only (unpooled) contribution set.
                var ceilings = m_grants.PoolCeilings(seat: WorldPrincipal.Seat(slot: seat), subject: GrantSubject.Body(index: seat));

                var untrustedAccepted = m_untrustedAcceptedMask[seat];

                for (var ordinal = 0; (ordinal < ChannelLimits.MaxChannels); ordinal++) {
                    var slot = (baseSlot + ordinal);
                    var untrusted = m_untrustedSum[slot];
                    var trusted = m_trustedSum[slot];
                    var poolCeiling = ceilings[ordinal];
                    var contributedHeld = m_contributedHeld[slot];
                    var shape = (m_population.Channels.IsDeclared(ordinal: ordinal) ? m_population.Channels.Shape(ordinal: ordinal) : ChannelShape.Bipolar);
                    var combinedHeld = WorldChannelTable.ComposeHeld(a: ownerHeld[ordinal].Value, b: contributedHeld, shape: shape);

                    if (combinedHeld != ownerHeld[ordinal].Value) {
                        held = held.WithChannel(ordinal: ordinal, value: FixedQ4816.FromRawBits(value: combinedHeld));
                    }

                    // The read-back's ceiling in force is poolCeiling ONLY when this tick's contribution set actually
                    // reached this ordinal through the untrusted (pooled) path (m_untrustedAcceptedMask) — an authored
                    // ceiling the seat has on file but nobody exercised THIS write reads back as "no ceiling in
                    // force," never as the number on paper, so player.channels can prove the fold ran rather than
                    // just that a grant exists.
                    var poolReached = untrustedAccepted.Contains(ordinal: ordinal);

                    m_channelReadCeiling[slot] = (poolReached ? poolCeiling : 0L);

                    if ((untrusted == 0L) && (trusted == 0L)) {
                        m_channelReadClamped[slot] = false;

                        continue;
                    }

                    var threshold = m_population.Channels.Threshold(ordinal: ordinal);
                    var (minimum, maximum, quantizationThreshold) = WorldChannelTable.CompileFoldShape(shape: shape, threshold: threshold);
                    // WorldGrants uses raw zero as its "no ceiling authored" sentinel, while FixedContributionFold
                    // deliberately reserves zero for a PRESENT zero-width pool. Preserve the old omission semantics:
                    // only a positive authored ceiling becomes a radius; the sentinel becomes null.
                    FixedQ4816? poolRadius = ((poolCeiling > 0L) ? FixedQ4816.FromRawBits(value: poolCeiling) : null);

                    folded = folded.WithChannel(
                        ordinal: ordinal,
                        value: FixedContributionFold.Evaluate(
                            baseline: h[ordinal],
                            poolDeltaRaw: untrusted,
                            outsidePoolDeltaRaw: trusted,
                            poolRadius: poolRadius,
                            minimum: minimum,
                            maximum: maximum,
                            threshold: quantizationThreshold,
                            poolClamped: out var clamped
                        )
                    );
                    m_channelReadClamped[slot] = clamped;
                }

                body.SubmitIntent(intent: folded);
                body.SetHeldChannels(channels: held);

                m_channelReadBase[seat] = h;
                m_channelReadFolded[seat] = folded;
            }

            if (m_hasContribution[seat]) {
                ClearContribution(bodyIndex: seat);
            }

            m_hasOwnerBase[seat] = false;
        }
    }

    // Resets one seat's per-tick contribution accumulator (the two sums and the contributed held image) — called only
    // when it was actually touched this tick, so an uncontended body's fold phase never pays for a clear it does not
    // need. No ceiling accumulator to clear: the ceiling is durable grant-table state the seat authored, never a
    // per-tick derivation.
    private void ClearContribution(int bodyIndex) {
        m_hasContribution[bodyIndex] = false;
        m_untrustedAcceptedMask[bodyIndex] = default;

        var start = (bodyIndex * ChannelLimits.MaxChannels);

        Array.Clear(array: m_untrustedSum, index: start, length: ChannelLimits.MaxChannels);
        Array.Clear(array: m_trustedSum, index: start, length: ChannelLimits.MaxChannels);
        Array.Clear(array: m_contributedHeld, index: start, length: ChannelLimits.MaxChannels);
    }

    // Records this tick's (entity, principal) write; when a DIFFERENT principal already wrote the SAME entity earlier
    // in this same tick, prints one loud, attributed line — but ONLY the first tick this body transitions into a
    // contended state (see m_contended's own remarks: the check reads LAST tick's settled outcome, never this tick's
    // in-progress one, so a body contended for many consecutive ticks logs once, not once per tick). Two distinct
    // ALLOWED Drive grants naming one body is a genuine conflict (see WorldClient.SubmitSeatIntents' own remarks) —
    // the later writer still wins (unchanged from before this method existed); only the SILENCE of that outcome is what
    // this closes. Allocation-free: past m_tickWrittenEntity's sized capacity (see its own remarks) the tracked set
    // simply stops growing, so a body written for the first time after saturation goes unreported this tick. That is a
    // DIAGNOSTIC degrading, never a write changing — the deliberate trade against resizing on the hot path.
    private void ReportContention(int entityIndex, WorldPrincipal principal) {
        for (var index = 0; (index < m_tickWrittenCount); index++) {
            if (m_tickWrittenEntity[index] != entityIndex) {
                continue;
            }

            if (m_tickWrittenPrincipal[index] != principal) {
                if (!m_contended[entityIndex]) {
                    Console.Error.WriteLine(value: $"[world.grant: body:{entityIndex} driven by both {m_tickWrittenPrincipal[index].Describe()} and {principal.Describe()} this tick — {principal.Describe()}'s intent applies]");
                }

                m_tickCollided[index] = true;
                m_tickWrittenPrincipal[index] = principal;
            }

            return;
        }

        if (m_tickWrittenCount < m_tickWrittenEntity.Length) {
            m_tickWrittenEntity[m_tickWrittenCount] = entityIndex;
            m_tickWrittenPrincipal[m_tickWrittenCount] = principal;
            m_tickCollided[m_tickWrittenCount] = false;
            m_tickWrittenCount++;
        }
    }

    // Drain every buffered live edit in FIFO order, applying it at this tick boundary. Delivers the new definition to
    // the client sink ONCE if at least one edit applied (once per step with >=1 applied edit, not once per edit).
    private bool DrainPendingOps(ulong tick) {
        var applied = false;

        while (m_pending.TryDequeue(result: out var op)) {
            var ok = op switch {
                // An addon-sourced op was already metered at the seam's pre-flight (before decode, deliberately), so
                // re-entering the budget gate here would charge one guest dispatch twice against the same tick's
                // allowance. Every other source — console, loopback, a peer's submission — is metered right here.
                PendingOp.Mutate mutate => TryApplyMutation(mutation: mutate.Mutation, tick: tick, connectionId: mutate.ConnectionId, correlationId: mutate.CorrelationId, preMetered: (mutate.SourceAddonIndex >= 0)),
                PendingOp.Rebuild rebuild => ApplyRebuild(request: rebuild.Request, principal: rebuild.Principal, connectionId: rebuild.ConnectionId, correlationId: rebuild.CorrelationId, expectedContentHash: rebuild.ExpectedContentHash, preparationFailure: rebuild.PreparationFailure),
                PendingOp.Undo undo => ApplyUndo(count: undo.Count, principal: undo.Principal, connectionId: undo.ConnectionId, correlationId: undo.CorrelationId),
                PendingOp.AddonLifecycle lifecycle => TryApplyAddonLifecycle(lifecycle: lifecycle.Lifecycle, principal: lifecycle.Principal, connectionId: lifecycle.ConnectionId, correlationId: lifecycle.CorrelationId),
                _ => false,
            };

            // An addon-lifecycle op never touches WorldDefinition (see TryApplyAddonLifecycle's own remarks), so its
            // outcome must never fold into `applied` below — that flag exists to trigger ONE redundant-free
            // DeliverDefinition when the document actually moved, and a mount/unmount redelivering the SAME
            // definition would be a wasted send masquerading as a document change.
            if (op is PendingOp.AddonLifecycle) {
                continue;
            }

            // The addon mutation seam's I2: an addon-sourced Mutate op's OUTCOME — never its application, which
            // just ran above through the identical machinery a console mutation runs through — routes back to the
            // originating guest's RESERVED answer cell here, at drain time (same Step, before intents). The cell
            // itself is not delivered until ResolveReads(T) stages it into the guest's batch T+1; this only records
            // which verdict that staging will use. A well-formed mutation the document-apply pipeline itself
            // refused (a validation/capacity/cross-row failure — TryApplyMutation already printed the loud reason)
            // answers Rejected, distinct from every dispatch-door refusal the seam's earlier stages produce.
            if ((op is PendingOp.Mutate { SourceAddonIndex: >= 0 } addonMutate)) {
                m_addons?.CompleteMutation(addonIndex: addonMutate.SourceAddonIndex, actOrdinal: addonMutate.ActOrdinal, verdict: (ok ? AddonVerdict.Applied : AddonMutateRefusals.ToVerdict(reason: AddonMutateRefusal.ApplyRejected)));
            }

            applied |= ok;
        }

        if (applied) {
            m_output.DeliverDefinition(definition: m_definition);
        }

        return applied;
    }

    /// <summary>The administrative drain — applies every buffered document-level operation (mutations, rebuilds,
    /// undo, addon lifecycle changes) without advancing simulation time: no addon tick, no intent drain, no body
    /// integration, no rules, no event collection, and no snapshot delivery. <see cref="DrainPendingOps"/> is
    /// normally reached only from inside <see cref="Step"/>, so an instance that never steps (an authored
    /// <c>simulation.rateHz</c> of 0, or a live <c>world.rate pause</c>) could otherwise never apply the very
    /// mutation that would change that — a permanent self-lock. Called on the host's own master timeline in place
    /// of <see cref="Step"/> for a tick a stopped/paused instance does not take (see <c>Puck.World.WorldInstanceHost</c>'s
    /// per-instance scheduling remarks for the host-side half of this contract — that type lives a layer above this
    /// assembly, hence prose rather than a cref here).</summary>
    /// <remarks>Opens a fresh per-tick mutation-dispatch allowance exactly as <see cref="Step"/>'s own top does
    /// (<see cref="WorldMutationBudgetMeter.BeginTick"/> is a plain clear — safe to call once per administrative
    /// drain, same as once per real tick), so an untrusted principal keeps a steady dispatch rate while stopped
    /// rather than being starved by a budget that never resets. Every applied entry journals against
    /// <see cref="m_lastCompletedTick"/> — the tick that does not move while stopped — so <c>world.undo</c> stays
    /// coherent: an administrative entry undoes exactly like an ordinary one, it is simply attributed to a tick
    /// number that repeats until the instance actually steps again. Document mutations are outside the replay
    /// tape's own recorded scope already (<see cref="Puck.World.WorldReplayTape"/>'s honest-scope remarks — the
    /// tape records the human/authority command stream, never a raw <see cref="Protocol.WorldMutation"/>), so this
    /// method introduces no new tape interaction.</remarks>
    /// <returns><see langword="true"/> when anything applied (a definition delivery occurred).</returns>
    public bool DrainAdministrative() {
        m_mutationBudget.BeginTick();

        return DrainPendingOps(tick: m_lastCompletedTick);
    }

    /// <summary>
    /// Applies the admission predicate for a mutation — the one place the whole authority decision for a document write is
    /// made, and the only place any ingress is allowed to make it.
    /// </summary>
    /// <remarks>
    /// <para>Before the gates, one structural exemption: a <see cref="PrincipalKind.World"/> principal — the document
    /// acting on itself (a rule's effects, a kit's generate effect), never an actor — is admitted outright as
    /// <c>WorldMutationAdmissionRule.Structural</c>, before any authority is consulted. The gates below decide every
    /// other principal.</para>
    /// <para>Four gates, in order: (1) the coarse Mutate hold over the mutation's own document section; (2) the
    /// deciding Mutate row's <see cref="MutationKindMask"/>; (3) for a state-row or state-cell write, the row-scoped
    /// Edit hold over the concrete <c>state:&lt;name&gt;</c> subject and, beneath it, that deciding row's own kind
    /// mask; (4) for an untrusted principal, the per-tick dispatch budget. "Deciding row" always means the rule the
    /// verdict itself reports — <c>ConcreteHold</c> beats <c>WildcardHold</c> — never a union of a concrete and a
    /// wildcard row's masks.</para>
    /// <para><b>An absent kind mask is full reach.</b> A mask is opt-in narrowing beneath an already deny-by-default
    /// capability, never a second authority check: Console legitimately holds maskless <c>Mutate/section:*</c> rows
    /// from the boot seed, so refuse-all-on-unmasked here would deny every trusted mutation in the engine. Untrusted
    /// strictness lives at the grant door instead (<c>WorldGrants.Conflicts</c> refuses a maskless untrusted
    /// Mutate/section row outright), which is what makes an unmasked untrusted row unreachable rather than
    /// permissive.</para>
    /// <para>Every mutating ingress passes this: <see cref="TryApplyMutation"/> for the ordered domain (loopback,
    /// console, and the <c>WorldTcpHost</c> peer door, which converge there), and the addon mutation seam's
    /// pre-flight (<c>WorldAddonRuntime.ResolveMutations</c>), which keeps its own earlier call site — it refuses
    /// before decode so a guest cannot probe the decoder for free — but as a call to this rule, never a second copy
    /// of it. Call-site duplication is fine; rule reimplementation is the defect class this predicate exists to
    /// close.</para>
    /// </remarks>
    /// <param name="principal">The acting principal, as its ingress stamped it.</param>
    /// <param name="section">The document section the mutation targets (<c>SectionOf</c>).</param>
    /// <param name="kindOrdinal">The mutation's declared kind ordinal (<see cref="WorldMutationKindCatalog"/>).</param>
    /// <param name="rowScopedEditSubject">The concrete <c>state:&lt;name&gt;</c> subject a state write names, or
    /// <see langword="null"/> when the mutation is not row-scoped — or when the caller cannot yet know it (the addon
    /// pre-flight runs before decode, so its state writes take gate (3) later, at apply).</param>
    /// <param name="meter">Whether this call is the metering point for the dispatch. False only where the ingress
    /// already charged it (an addon act charged at its pre-flight, re-entering at apply).</param>
    /// <param name="admission">The decided outcome — which gate fired and the row-level evidence behind it.</param>
    /// <returns><see langword="true"/> when every gate cleared (and the dispatch was charged, when metered).</returns>
    public bool TryAdmitMutation(WorldPrincipal principal, WorldSection section, int kindOrdinal, GrantSubject? rowScopedEditSubject, bool meter, out WorldMutationAdmission admission) {
        var sectionSubject = GrantSubject.Section(section: section);

        // THE ONE STRUCTURAL EXEMPTION, keyed on the principal KIND and decided HERE so nothing else has to know
        // about it — no bypass parameter threaded through the apply path, no seeded wildcard row standing in for
        // authority. The world's own authored program (a rule's effects, a kit's generate effect) is not an actor
        // submitting a write; it is the document acting on itself, exactly as a per-body ActionEffect has always
        // done without consulting this table at all. See WorldPrincipal.World for the full argument, including why
        // this is NOT the "handler constructs a principal to launder an identity" defect. Every gate BELOW authority
        // still runs unconditionally at the call site: compose, whole-document validate, envelope, solids.
        if (principal.Kind == PrincipalKind.World) {
            admission = new WorldMutationAdmission(Rule: WorldMutationAdmissionRule.Structural, Verdict: default, Subject: sectionSubject, DecidingSubject: sectionSubject, Mask: MutationKindMask.Empty, Budget: 0);

            return true;
        }
        var mutateVerdict = m_grants.Allows(principal: principal, capability: WorldCapability.Mutate, subject: sectionSubject);

        if (!mutateVerdict.IsAllowed) {
            admission = new WorldMutationAdmission(Rule: WorldMutationAdmissionRule.SectionDenied, Verdict: mutateVerdict, Subject: sectionSubject, DecidingSubject: sectionSubject, Mask: MutationKindMask.Empty, Budget: 0);

            return false;
        }

        var decidingMutateSubject = ((mutateVerdict.Rule == GrantRule.WildcardHold) ? GrantSubject.All : sectionSubject);

        if (m_grants.TryGetKindMask(principal: principal, capability: WorldCapability.Mutate, subject: decidingMutateSubject, out var mutateMask) && !mutateMask.Contains(ordinal: kindOrdinal)) {
            admission = new WorldMutationAdmission(Rule: WorldMutationAdmissionRule.MaskedKind, Verdict: mutateVerdict, Subject: sectionSubject, DecidingSubject: decidingMutateSubject, Mask: mutateMask, Budget: 0);

            return false;
        }

        // A state-row OR state-cell mutation is checked a SECOND time: Edit over the CONCRETE state:<name> subject the
        // mutation names — the SAME subject whether the write is whole-row (UpsertStateRow/RemoveStateRow) or per-cell
        // (UpsertStateCell/RemoveStateCell), beneath the coarse section-level Mutate hold above.
        // The domain-seeded Edit/all every seat and Console already holds reaches every row and
        // every cell until an operator deliberately narrows it (see WorldCapability.Edit's remarks).
        if (rowScopedEditSubject is { } editSubject) {
            var editVerdict = m_grants.Allows(principal: principal, capability: WorldCapability.Edit, subject: editSubject);

            if (!editVerdict.IsAllowed) {
                admission = new WorldMutationAdmission(Rule: WorldMutationAdmissionRule.RowDenied, Verdict: editVerdict, Subject: editSubject, DecidingSubject: editSubject, Mask: MutationKindMask.Empty, Budget: 0);

                return false;
            }

            var decidingEditSubject = ((editVerdict.Rule == GrantRule.WildcardHold) ? GrantSubject.All : editSubject);

            if (m_grants.TryGetKindMask(principal: principal, capability: WorldCapability.Edit, subject: decidingEditSubject, out var editMask) && !editMask.Contains(ordinal: kindOrdinal)) {
                admission = new WorldMutationAdmission(Rule: WorldMutationAdmissionRule.RowMaskedKind, Verdict: editVerdict, Subject: editSubject, DecidingSubject: decidingEditSubject, Mask: editMask, Budget: 0);

                return false;
            }
        }

        // THE BUDGET, for every untrusted principal and no other — a mounted addon's guest compute and a remote peer's
        // submission are the same denial-of-service shape, so they owe the same per-tick ceiling from the same meter.
        // The budget is read off the DECIDING row (identical to the concrete subject for every reachable case: only a
        // trusted principal can hold Mutate/all, and trusted principals are unmetered). A held row with NO recorded
        // budget is unreachable by construction — WorldGrants.Conflicts refuses an untrusted Mutate row without one
        // before it can be added — so it REFUSES rather than dispatching unmetered: an unmetered dispatch silently
        // defeats the very budget this gate exists to enforce.
        if (meter && !WorldGrants.IsTrusted(principal: principal)) {
            if (!m_grants.TryGetBudget(principal: principal, capability: WorldCapability.Mutate, subject: decidingMutateSubject, out var budget)) {
                admission = new WorldMutationAdmission(Rule: WorldMutationAdmissionRule.MissingBudget, Verdict: mutateVerdict, Subject: sectionSubject, DecidingSubject: decidingMutateSubject, Mask: MutationKindMask.Empty, Budget: 0);

                return false;
            }

            if (!m_mutationBudget.TryCharge(principal: principal, section: section, budget: budget)) {
                admission = new WorldMutationAdmission(Rule: WorldMutationAdmissionRule.BudgetExhausted, Verdict: mutateVerdict, Subject: sectionSubject, DecidingSubject: decidingMutateSubject, Mask: MutationKindMask.Empty, Budget: budget);

                return false;
            }
        }

        admission = new WorldMutationAdmission(Rule: WorldMutationAdmissionRule.Admitted, Verdict: mutateVerdict, Subject: sectionSubject, DecidingSubject: decidingMutateSubject, Mask: MutationKindMask.Empty, Budget: 0);

        return true;
    }

    // Apply one mutation at the tick boundary: authority through the ONE admission predicate → compose a candidate
    // (with-expression) → revalidate the WHOLE document → capacity-check scene/screen edits against the probed render
    // envelope → on any failure reject loudly (definition unchanged) → on success swap the live definition, rebuild the
    // changed section's derived state, and journal it.
    private bool TryApplyMutation(WorldMutation mutation, ulong tick, int connectionId, long correlationId, bool preMetered) {
        // THE ONE ADMISSION PREDICATE decides the whole authority question — section hold, the Mutate/section kind
        // mask, the row-scoped Edit hold and ITS mask, and the untrusted per-tick dispatch budget. Every ordered-domain
        // ingress converges here (loopback, console, and the TCP peer door alike), so this call is what gives the peer
        // door exactly the masks and metering the addon seam has, from the same code rather than from a second reading
        // of the same rules. `preMetered` says only whether THIS ingress already charged the dispatch (the addon seam
        // meters at its own pre-flight, before decode, deliberately); it never changes which rules run.
        if (!TryAdmitMutation(
                principal: mutation.Principal,
                section: SectionOf(mutation: mutation),
                kindOrdinal: WorldMutationKindCatalog.OrdinalOf(mutation: mutation),
                rowScopedEditSubject: RowScopedEditSubjectOf(mutation: mutation),
                meter: !preMetered,
                admission: out var admission)) {
            var denial = admission.Describe();

            Console.Error.WriteLine(value: $"[world.grant denied: {mutation.Principal.Describe()} {denial} — {Describe(mutation: mutation)} dropped]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"{Describe(mutation: mutation)} denied: {denial}", Rejected: true, Kind: WorldEditEchoKind.Mutation, Mutation: mutation, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }

        if (!TryCompose(current: m_definition, mutation: mutation, tick: tick, instanceIdentity: InstanceIdentity, candidate: out var candidate, reason: out var composeReason, evictedKey: out var evictedKey)) {
            Reject(mutation: mutation, reason: composeReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        candidate = RebaseAdvanceEpoch(original: m_definition, candidate: candidate, mutation: mutation, tick: tick);

        if ((mutation is WorldMutation.UpsertKit upsertKit) && !m_population.CanReplaceKit(replacement: upsertKit.Kit, refusal: out var sourceReason)) {
            Reject(mutation: mutation, reason: sourceReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        // Cross-document adjacency claims are proved at load, never from this tick path. An edit that can change a
        // standing claim or one of its floor inputs must go through a document reload; unrelated edits revalidate
        // only the facts owned by this document.
        if ((candidate.Adjacencies is { Count: > 0 }) && AdjacencyProofInputsChanged(current: m_definition, candidate: candidate, mutation: mutation)) {
            Reject(mutation: mutation, reason: "the mutation changes an adjacency overlap input; apply it through world.load/world.reload so the neighbour can be re-proved outside the tick path", connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        if (!WorldDefinitionValidator.TryValidateLocally(definition: candidate, reason: out var validationReason)) {
            Reject(mutation: mutation, reason: validationReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        if (ExceedsBootDerivedFaceReservation(candidate: candidate, reason: out var reservationReason)) {
            Reject(mutation: mutation, reason: reservationReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        if (AffectsRenderEnvelope(mutation: mutation) && !m_envelope.TryFit(candidate: candidate, reason: out var capacityReason)) {
            Reject(mutation: mutation, reason: capacityReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        // Step 4b — the SDF contact field, built once here (before install) so the warp-free evaluator's excluded-op
        // ceiling is a LOUD apply-time rejection (the definition and the field both stay byte-identical on failure)
        // rather than a constructor throw at install. Only a solid-affecting mutation rebuilds it; otherwise the live
        // field carries forward untouched.
        var solids = m_solids;
        var solidAffecting = AffectsSolidField(mutation: mutation);

        if (solidAffecting) {
            // A SetCollision edit touches only the collision tuning row — the compiled SDF program (screens and
            // placements) is byte-identical — so when the live field already exists and the requirements still need it,
            // candidate still is, re-wrap the existing evaluator with the new scalars instead of recompiling the program
            // (a slope/skin drag never rebuilds hundreds of instructions). Every other solid-affecting edit, and a
            // a requirement-selection flip rebuilds from scratch.
            if ((mutation is WorldMutation.SetCollision) && (m_solids is { } live) && WorldContactSelection.RequiresField(collision: candidate.Collision)) {
                solids = live.WithTuning(tuning: FixedWorldCollision.Compile(collision: candidate.Collision));
            } else if (!TryBuildSolids(definition: candidate, solids: out solids, reason: out var solidReason)) {
                Reject(mutation: mutation, reason: solidReason, connectionId: connectionId, correlationId: correlationId);

                return false;
            }
        }

        // Assign the field BEFORE the rebuild so a recompiled body's first step already solves against it. A field change
        // forces a population rebuild (bodies must receive the new field reference) even when the mutation kind is not
        // otherwise population-affecting; the analytic path is untouched (solidAffecting is inert without the field provider).
        if (solidAffecting && !ReferenceEquals(objA: solids, objB: m_solids)) {
            m_solids = solids;
            m_solidRevision++;
        }

        Install(definition: candidate, rebuildPopulation: (AffectsPopulation(mutation: mutation) || (solidAffecting && WorldContactSelection.RequiresField(collision: candidate.Collision))));
        m_journal.Add(item: new JournalEntry(Tick: tick, Mutation: mutation));

        // A defaults-class mutation edits what the NEXT boot wakes on while the live
        // session levers keep their values (world.save folds them); every other mutation applies live on delivery.
        // SetAuthoringDefaults is the honest exception to the binary split: ONE whole-row mutation carries BOTH
        // classes at once (WorldAuthoringDefaults' own remarks name which field is which) — the headroom/repeat-cap
        // fields are boot-consumed by the frozen render-envelope probe, while candidate/layout/preview fields are
        // re-read live at every use site. The narration spells out the split rather than forcing the mutation into
        // either WorldEditEchoKind bucket; Kind stays Mutation because the live-consumed majority applies NOW.
        var documentOnly = IsDocumentDefaults(mutation: mutation);
        var message = mutation switch {
            WorldMutation.SetAuthoringDefaults => $"{Describe(mutation: mutation)} applied — candidate/layout/preview levers live now; headroom + max-repeat-per-segment apply at next boot",
            // SetPopulationDefaults is a THIRD timing class: the census figures are document defaults (next boot), but
            // the distribution is LIVE for future activations while INERT for bodies already standing — spell out the split.
            WorldMutation.SetPopulationDefaults => $"{Describe(mutation: mutation)} applied — census figures next boot; spawn policy live for future activations, standing bodies unmoved",
            _ => $"{Describe(mutation: mutation)} applied{(documentOnly ? " — document default (next boot; live levers unchanged)" : string.Empty)}",
        };

        // An Evicts row's overflow policy dropped a cell to make room — named on the SAME echo line rather than a
        // separate one, so an eviction can never scroll past unnoticed the way a second stderr line could.
        if (evictedKey is { } evicted) {
            message = $"{message} (evicted '{evicted}')";
        }

        Console.Error.WriteLine(value: $"[world.mutation: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: message, Rejected: false, Kind: (documentOnly ? WorldEditEchoKind.DocumentDefaults : WorldEditEchoKind.Mutation), Mutation: mutation, ConnectionId: connectionId, CorrelationId: correlationId));

        return true;
    }

    // The whole-document rebuild-and-swap (SubmitRebuild / world.reset, world.load, world.reload): resolve the
    // candidate (the server's own base for Reset, the console-resolved document for Load/Reload — or, on a REPLAY
    // drive, a fresh re-read of the tape's path hint) → compute/check its CAS content hash → validate → capacity-check
    // → solids rebuild → swap → journal RESET → re-mint every admitted peer connection's admission grant
    // (the document swap re-syncs group/ownership grant state but never re-mints the admitted peers' admission grants
    // on its own, and a rebuild is exactly the kind of whole-state swap a future authority change might reasonably
    // reset around — this closes that loudly, by construction, rather than by omission). The console handler already
    // validated a Load/Reload file (WorldDefinitionLoader.TryLoadFile); this
    // re-check is the defensive apply-time gate every install passes through, same as the prior world.load-only path.
    private bool ApplyRebuild(WorldRebuildRequest request, WorldPrincipal principal, int connectionId, long correlationId, string? expectedContentHash = null, string? preparationFailure = null) {
        var verb = request.Kind switch {
            WorldRebuildKind.Reset => "world.reset",
            WorldRebuildKind.Load => "world.load",
            WorldRebuildKind.Reload => "world.reload",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(request), actualValue: request.Kind, message: $"no {nameof(ApplyRebuild)} verb for rebuild kind '{request.Kind}'."),
        };

        // THE CAS RESOLUTION, FIRST — before any refusal gate, so a rebuild the door goes on to refuse below is
        // still taped and reproduces as the identical refusal on replay (RebuildTap's own remarks).
        //   Reset: the candidate is THIS DRIVE'S OWN current base (m_base) — live or replay, always freshly read
        //   here, never the request's (Reset carries none). Its hash is the base's canonical bytes AT THIS MOMENT,
        //   which is why it must be computed here rather than at submission (m_base can move between submission and
        //   drain — see EnqueueRebuild's remarks).
        //   Load/Reload: request.Definition is non-null on the LIVE path (the console already read + validated the
        //   file and computed request.ContentHash from those exact bytes) and null on a REPLAY drive (the tape never
        //   embeds the document — WorldReplaySnapshot.Drive passes only Kind/PathHint/Force/ContentHash, so a
        //   re-drive proves the file on disk still matches what was recorded rather than trusting a stored copy).
        WorldDefinition candidate;
        string contentHash;

        if (request.Kind == WorldRebuildKind.Reset) {
            candidate = m_base;
            contentHash = WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: candidate));
        } else if (request.Definition is { } supplied) {
            candidate = supplied;
            contentHash = (request.ContentHash ?? throw new InvalidOperationException(message: $"{verb}: a Load/Reload request carrying a document must also carry its content hash."));
        } else if (request.PathHint is not { } path) {
            throw ReplayRefusal.RebuildSourceUnavailable.Raise(message: $"{verb}: a Load/Reload request with no embedded document must carry a path hint to re-read for replay.");
        } else if (!WorldDefinitionFileSource.TryLoadLocally(path: path, definition: out var reread, contentHash: out var rereadHash, reason: out var rereadReason)) {
            throw ReplayRefusal.RebuildSourceUnavailable.Raise(message: $"{verb}: cannot re-read '{path}' for replay — {rereadReason}");
        } else {
            candidate = reread!;
            contentHash = rereadHash;
        }

        if ((expectedContentHash is { } expected) && !string.Equals(a: contentHash, b: expected, comparisonType: StringComparison.Ordinal)) {
            var pinned = ((request.Kind == WorldRebuildKind.Reset) ? "the re-driven run's own base" : $"'{request.PathHint}'");

            throw ReplayRefusal.RebuildContentMismatch.Raise(message: $"{verb}: content hash mismatch on {pinned} — found {contentHash}, expected {expected} (recorded). The pinned content has changed since this recording was made; re-record it.");
        }

        RebuildTap?.Invoke(arg1: request, arg2: principal, arg3: contentHash);

        // A rebuild can touch any section: the principal must hold Mutate over EVERY section — the same door
        // world.load/world.undo have always used.
        if (!m_grants.AllowsAllSections(principal: principal, capability: WorldCapability.Mutate, deniedSection: out var deniedSection, denial: out var deniedVerdict)) {
            var denial = $"{principal.Describe()} cannot mutate every section (section:{deniedSection.ToString().ToLowerInvariant()} — {deniedVerdict.DescribeDenial()}) — {verb} dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: denial, Rejected: true, Kind: WorldEditEchoKind.Rebuild, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }

        // The live-authoring guard: world.load without `force` refuses outright while the journal is dirty, rather
        // than silently discarding unsaved work. Orthogonal to world.reset (reset IS the discard, by name, every
        // time) and to world.reload (the artist external-edit loop is expected to discard the in-session journal on
        // every reload — its whole point is re-reading the file the artist just edited).
        if ((request.Kind == WorldRebuildKind.Load) && !request.Force && (m_journal.Count > 0)) {
            var denial = $"{m_journal.Count} unsaved mutation(s) would be discarded — world.save first, world.reset to discard them without loading a new document, or world.load {request.PathHint} force to discard them and load anyway";

            Console.Error.WriteLine(value: $"[world.load rejected: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"{verb} rejected: {denial}", Rejected: true, Kind: WorldEditEchoKind.Rebuild, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }

        if (preparationFailure is not null) {
            RejectRebuild(verb: verb, reason: preparationFailure, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        // The load command already proved cross-document claims before enqueue. Apply-time validation runs from
        // Step, so it repeats only document-local checks and never reaches transport from the tick path.
        if (!WorldDefinitionValidator.TryValidateLocally(definition: candidate, reason: out var validationReason)) {
            RejectRebuild(verb: verb, reason: validationReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        if (candidate.Population.Capacity != m_population.Capacity) {
            RejectRebuild(verb: verb, reason: $"population capacity {candidate.Population.Capacity} differs from the boot-allocated capacity {m_population.Capacity}; restart the host to load it", connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        if (ExceedsBootDerivedFaceReservation(candidate: candidate, reason: out var reservationReason)) {
            RejectRebuild(verb: verb, reason: reservationReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        if (!m_envelope.TryFit(candidate: candidate, reason: out var capacityReason)) {
            RejectRebuild(verb: verb, reason: capacityReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        // A rebuild rebuilds the field wholesale (loud rejection on an unsupported solid, definition unchanged) —
        // same as a whole-document swap always has.
        if (!TryBuildSolids(definition: candidate, solids: out var rebuildSolids, reason: out var rebuildSolidReason)) {
            RejectRebuild(verb: verb, reason: rebuildSolidReason, connectionId: connectionId, correlationId: correlationId);

            return false;
        }

        SwapSolids(solids: rebuildSolids);
        if (request.Kind != WorldRebuildKind.Reset) {
            m_machines.SetDocumentPath(documentPath: request.PathHint);
        }
        Install(definition: candidate, rebuildPopulation: true);
        m_journal.Clear();

        // Snapshot, for every CURRENTLY CONNECTED (admitted, not parked) peer, exactly
        // which of its ORIGINAL admission-minted rows it still actually holds — BEFORE the reset below discards the
        // whole table. A row present at connection time but absent here is a live world.revoke the operator issued
        // against this exact peer since; RemintPeerAdmissionGrants must not resurrect it. A PARKED peer (disconnected,
        // inside its reconnect grace — WorldPopulation.IsAdmittedPeer stays true through that window) is excluded
        // outright: it has no live session to act through, so it is re-authorized (and, if still trusted, reminted)
        // only on an actual reconnect, never on a rebuild that happens to land during its grace window.
        var preRebuildPeerRows = new Dictionary<int, IReadOnlyList<WorldGrant>>();

        for (var peerIndex = WorldPopulation.LocalSeatCount; (peerIndex < m_population.Capacity); peerIndex++) {
            if (!m_population.IsAdmittedPeer(bodyIndex: peerIndex) || m_population.IsParked(index: peerIndex)) {
                continue;
            }

            preRebuildPeerRows[peerIndex] = m_grants.Rows(principal: m_population.PeerPrincipal(index: peerIndex));
        }

        // THE GRANT-TABLE HALF: runtime grants drop; document grants re-apply as at boot.
        // A world.grant/world.revoke acquisition is RUNTIME state — orthogonal to the document
        // and never touched by Install/Rebuild on its own — so a rebuild that left it standing would silently keep
        // whatever authority the PRE-rebuild session had accumulated, including grants a fresh boot of this exact
        // document would never have seeded. Reset silently to the SAME permissive local-play defaults the
        // constructor seeds (WorldGrants.Reset — never the loud Grant door, exactly like the constructor's own
        // seed), THEN replay the NEW candidate's own document-authored Grants section under Console through the
        // IDENTICAL loud accept/reject path the constructor and world.grant both use — same consent-withholding
        // (WithoutAuthoredConsent), same narration.
        m_grants.Reset(seatCount: WorldPopulation.LocalSeatCount);

        foreach (var grant in candidate.Grants) {
            if (IsDocumentChannelRow(grant: grant)) {
                continue;
            }

            Grant(grant: WithoutAuthoredConsent(grant: grant), actor: WorldPrincipal.Console, connectionId: connectionId, correlationId: correlationId);
        }

        // Admitted PEER CONNECTIONS are the one exception: "admitted peers survive"
        // means their CONNECTION stays (WorldPopulation never dropped them — Install/Rebuild's own Install call
        // above left every admitted peer body active), but the reset above just wiped their admission grant along
        // with everything else, because a peer is not a boot-time seat and not a document row either. RE-AUTHORIZE
        // (never blindly re-mint) each one against the CANDIDATE's own current admission policy.
        RemintPeerAdmissionGrants(candidate: candidate, preRebuildPeerRows: preRebuildPeerRows);

        // Reset targets the base WITHOUT moving it (the whole point: repeated resets always land on the same base
        // until the next save/load). Load/Reload REPLACE the base — the newly installed document becomes what the
        // NEXT reset targets, exactly like a swap always has.
        string origin;

        if (request.Kind == WorldRebuildKind.Reset) {
            origin = m_baseOrigin;
        } else {
            m_base = candidate;
            origin = $"'{request.PathHint}' ({verb})";
            m_baseOrigin = origin;
        }

        var message = $"{verb} applied — base is {origin}, journal cleared";

        Console.Error.WriteLine(value: $"[world.definition: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: message, Rejected: false, Kind: WorldEditEchoKind.Rebuild, ConnectionId: connectionId, CorrelationId: correlationId, RebuildOrigin: ((request.Kind == WorldRebuildKind.Reset) ? null : request.PathHint)));

        return true;
    }

    // Re-establishes admission grants for every peer connection ApplyRebuild's snapshot pass captured (admitted,
    // NOT parked — see that pass's own remarks) — after WorldGrants.Reset wiped the whole runtime grant table. A
    // peer is a CONNECTION, not a document row or a boot-time seat, so nothing in WorldGrants.Reset or the
    // document-Grants replay re-establishes it — this is the one thing that must run AFTER both.
    //
    // Re-authorizes each peer rather than replaying its stored, connection-time templates, so a world.revoke
    // against a peer, or an operator narrowing/removing its admission entry, is honored across a
    // world.reset/load/reload rather than silently undone:
    //
    //  1. Re-match the peer's verified (Domain, Subject) — WorldPopulation.PeerIdentity, stored at
    //     TryAdmitRemotePeer, never recomputed here — against the CANDIDATE document's OWN admission entries,
    //     through WorldAdmissionDoor.TryMatchEntry: the SAME (domain, subject, mode) rule a fresh connection would
    //     be judged by. No match at all (the identity's entry was removed, or never existed in this candidate)
    //     mints nothing — "an identity no longer trusted... gets the current verdict, not the boot-time one".
    //  2. A match's CURRENT Grants list governs, not the stored connection-time templates — narrower or wider than
    //     what was minted at connection, exactly as if this peer connected fresh right now.
    //  3. Any row that WAS successfully installed in the peer's prior authorization, but is missing from the caller's preRebuildPeerRows
    //     snapshot (taken an instant before the wipe), was explicitly revoked live — that omission is preserved
    //     rather than re-derived, because live revocation is runtime state a document can never express. The baseline
    //     advances to the successfully-installed rows after every re-authorization, so a policy row rejected by the
    //     grant door is retried later rather than misremembered as revoked.
    //
    // A re-grant of an already-held row is a no-op acceptance, not a duplicate (WorldGrants keys on the (principal,
    // capability, subject) triple).
    private void RemintPeerAdmissionGrants(WorldDefinition candidate, IReadOnlyDictionary<int, IReadOnlyList<WorldGrant>> preRebuildPeerRows) {
        foreach (var (index, priorRows) in preRebuildPeerRows) {
            var principal = m_population.PeerPrincipal(index: index);
            var baselineTemplates = m_population.PeerAdmissionInstalledGrantTemplates(bodyIndex: index);
            var (domain, subject) = m_population.PeerIdentity(bodyIndex: index);

            // Everything in baselineTemplates that is NOT still present in priorRows (the live snapshot taken right
            // before the wipe) was revoked at runtime since connection — never resurrect it. Anything never in
            // the baseline is not a revocation candidate (there was nothing to revoke under that policy generation).
            var revokedKeys = new HashSet<(WorldCapability Capability, GrantSubject Subject)>(collection: m_population.PeerAdmissionRevokedKeys(bodyIndex: index));

            foreach (var template in baselineTemplates) {
                revokedKeys.Add(item: (template.Capability, template.SubjectFor(bodyIndex: index)));
            }

            foreach (var row in priorRows) {
                // A live re-grant is just as explicit as a live revoke: if the row is held again when the next
                // rebuild snapshots it, forget any older remembered revocation for this key.
                revokedKeys.Remove(item: (row.Capability, row.Subject));
            }

            m_population.SetPeerAdmissionRevokedKeys(bodyIndex: index, revokedKeys: revokedKeys);

            // An arrival is re-authorized against its own authority row, a connection against its identity row: the
            // same door decides both, from the candidate document rather than the connection-time policy.
            var stillTrusted = m_population.PeerAuthorityTransferred(bodyIndex: index)
                ? ((Protocol.WorldAdmissionDoor.TryAdmitArrival(entries: candidate.Admission, sourceAuthority: domain, verdict: out var arrivalVerdict) is null) ? arrivalVerdict : null)
                : (Protocol.WorldAdmissionDoor.TryMatchEntry(entries: candidate.Admission, domain: domain, subject: subject, verdict: out var matchedVerdict) ? matchedVerdict : null);

            if (stillTrusted is not { } current) {
                m_population.SetPeerAdmissionInstalledGrantTemplates(bodyIndex: index, grantTemplates: []);

                continue;
            }

            var installedTemplates = new List<WorldAdmissionGrant>();

            foreach (var template in current.Templates) {
                if (revokedKeys.Contains(item: (template.Capability, template.SubjectFor(bodyIndex: index)))) {
                    continue;
                }

                if (TryApplyGrant(grant: new WorldGrant(Principal: principal, Capability: template.Capability, Subject: template.SubjectFor(bodyIndex: index), Exclusive: template.Exclusive, Budget: template.Budget, EventBudget: template.EventBudget, KindMask: template.KindMask), actor: WorldPrincipal.Console)) {
                    installedTemplates.Add(item: template);
                }
            }

            // The next absence comparison may only contain rows that ACTUALLY reached the live table. An authored
            // row rejected by exclusivity or another grant-door rule was never present to revoke; recording it here
            // would turn that refusal into a permanent remembered revoke and prevent a later conflict-free rebuild
            // from retrying the current policy. Explicitly revoked rows need no baseline entry — revokedKeys already
            // carries them independently until a live re-grant clears them.
            m_population.SetPeerAdmissionInstalledGrantTemplates(bodyIndex: index, grantTemplates: installedTemplates);
        }
    }

    // Undo the last `count` applied mutations (default clamps to 1): restore the base and deterministically replay the
    // journal minus its tail through the SAME per-entry gates a live mutation passes — compose, whole-document
    // validate, render-envelope capacity, and solid-field buildability — everything but the authority check (the
    // every-section Mutate hold below already re-proves authority for the whole undo, so no per-entry grant lookup is
    // needed). The replay is ALL-OR-NOTHING: any entry failing any gate refuses the undo outright, names the failing
    // entry's index and reason on stderr, and installs NOTHING — a validated prefix is not a validated document, and no
    // general admissibility invariant lets a partially-replayed journal stand in for one that fully replayed.
    private bool ApplyUndo(int count, WorldPrincipal principal, int connectionId, long correlationId) {
        // Journal control is Mutate territory over every section (a replay can rebuild any).
        if (!m_grants.AllowsAllSections(principal: principal, capability: WorldCapability.Mutate, deniedSection: out var undoSection, denial: out var undoVerdict)) {
            var denial = $"{principal.Describe()} cannot mutate every section (section:{undoSection.ToString().ToLowerInvariant()} — {undoVerdict.DescribeDenial()}) — world.undo dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: denial, Rejected: true, Kind: WorldEditEchoKind.Mutation, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }

        if (m_journal.Count == 0) {
            Console.Error.WriteLine(value: "[world.undo: nothing to undo]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: "undo refused: nothing to undo", Rejected: true, Kind: WorldEditEchoKind.Mutation, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }

        var drop = Math.Clamp(value: count, min: 1, max: m_journal.Count);
        var keep = (m_journal.Count - drop);
        var candidate = m_base;
        var kept = new List<JournalEntry>(capacity: keep);

        for (var index = 0; (index < keep); index++) {
            var entry = m_journal[index];

            if (!TryCompose(current: candidate, mutation: entry.Mutation, tick: entry.Tick, instanceIdentity: InstanceIdentity, candidate: out var next, reason: out var composeReason, evictedKey: out _)) {
                var composeRefusal = $"undo refused: replay failed at journal entry {index} ({Describe(mutation: entry.Mutation)}) — {composeReason}";

                Console.Error.WriteLine(value: $"[world.undo: {composeRefusal}]");
                EchoTap?.Invoke(obj: new WorldEditEcho(Message: composeRefusal, Rejected: true, Kind: WorldEditEchoKind.Mutation, ConnectionId: connectionId, CorrelationId: correlationId));

                return false;
            }

            // An advancing row's epoch re-bases to the ORIGINAL journal tick it was set at, exactly as it did on the
            // live apply this replays — see RebaseAdvanceEpoch's remarks. Doing this BEFORE revalidation is what lets
            // world.undo rewind a regen row's accumulation bit-identically, same as it already does for a generator's
            // $cursor.
            next = RebaseAdvanceEpoch(original: candidate, candidate: next, mutation: entry.Mutation, tick: entry.Tick);

            // Cross-document claims were proved before the journal was admitted; replay repeats only local checks.
            if (!WorldDefinitionValidator.TryValidateLocally(definition: next, reason: out var reason) ||
                (AffectsRenderEnvelope(mutation: entry.Mutation) && !m_envelope.TryFit(candidate: next, reason: out reason)) ||
                (AffectsSolidField(mutation: entry.Mutation) && !TryBuildSolids(definition: next, solids: out _, reason: out reason))) {
                var refusal = $"undo refused: replay failed at journal entry {index} ({Describe(mutation: entry.Mutation)}) — {reason}";

                Console.Error.WriteLine(value: $"[world.undo: {refusal}]");
                EchoTap?.Invoke(obj: new WorldEditEcho(Message: refusal, Rejected: true, Kind: WorldEditEchoKind.Mutation, ConnectionId: connectionId, CorrelationId: correlationId));

                return false;
            }

            candidate = next;
            kept.Add(item: entry);
        }

        // The full replay validated every entry above, so this rebuild is expected to succeed; still checked and
        // still loud on failure rather than installing a half-built field, for the same reason the loop above refuses
        // rather than tolerates: no step here is allowed to half-apply.
        if (!TryBuildSolids(definition: candidate, solids: out var undoSolids, reason: out var undoSolidReason)) {
            var refusal = $"undo refused: solid field rebuild failed — {undoSolidReason}";

            Console.Error.WriteLine(value: $"[world.undo: {refusal}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: refusal, Rejected: true, Kind: WorldEditEchoKind.Mutation, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }

        SwapSolids(solids: undoSolids);
        Install(definition: candidate, rebuildPopulation: true);
        m_journal.Clear();
        m_journal.AddRange(collection: kept);
        Console.Error.WriteLine(value: $"[world.undo: dropped {drop}, {m_journal.Count} remaining]");

        return true;
    }

    // Apply one buffered addon-lifecycle op at the tick boundary — the SAME door TryApplyMutation runs (Mutate over
    // section:addons, checked BEFORE the runtime is touched, so a denial changes nothing), drained from the SAME
    // queue a document mutation drains from. Never journaled (it is not a WorldMutation and is not undo-able through
    // world.undo — a runtime lifecycle change is not a document edit) and never touches WorldDefinition.Addons (that
    // stays world.row.set addons/world.row.remove addons's document-only territory); this is the RUNTIME's own half.
    private bool TryApplyAddonLifecycle(WorldAddonLifecycle lifecycle, WorldPrincipal principal, int connectionId, long correlationId) {
        var verb = (lifecycle is WorldAddonLifecycle.Mount ? "world.addon.mount" : "world.addon.unmount");

        if (m_grants.Allows(principal: principal, capability: WorldCapability.Mutate, subject: GrantSubject.Section(section: WorldSection.Addons)) is { IsAllowed: false } verdict) {
            var denial = $"{principal.Describe()} cannot mutate section:addons ({verdict.DescribeDenial()}) — {verb} dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: denial, Rejected: true, Kind: WorldEditEchoKind.AddonLifecycle, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }

        if (m_addons is not { } addons) {
            var refusal = $"{verb} refused — this world enables no addon; there is no runtime to mount into";

            Console.Error.WriteLine(value: $"[{verb}: {refusal}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: refusal, Rejected: true, Kind: WorldEditEchoKind.AddonLifecycle, ConnectionId: connectionId, CorrelationId: correlationId));

            return false;
        }

        var status = lifecycle switch {
            WorldAddonLifecycle.Mount mount => addons.Mount(name: mount.Name, modulePath: mount.ModulePath, hash: mount.Hash, fuel: mount.Fuel, requests: mount.Requests),
            WorldAddonLifecycle.Unmount unmount => addons.Unmount(name: unmount.Name),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(lifecycle), actualValue: lifecycle, message: $"no {nameof(TryApplyAddonLifecycle)} arm for addon lifecycle kind '{lifecycle.GetType().Name}'."),
        };
        // Both Mount and Unmount report failure as a leading quote-mark on the status line ("'name' ...") — the same
        // convention Reload/SetEnabled already use, so this narrow check is the one place that turns their prose back
        // into the Rejected/Denied-shaped WorldEditEcho every other apply path emits.
        var rejected = status.StartsWith(value: '\'');

        Console.Error.WriteLine(value: $"[{verb}: {status}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: status, Rejected: rejected, Kind: WorldEditEchoKind.AddonLifecycle, ConnectionId: connectionId, CorrelationId: correlationId));

        return !rejected;
    }

    /// <summary>Applies one screen op synchronously, under the ordinary Control-authority gate — the public entry
    /// point <see cref="WorldReplaySnapshot.Drive"/> re-applies a recorded <see cref="WorldReplayEntry.ScreenOp"/>
    /// through (mirroring <see cref="ApplyCommand"/>/<see cref="Grant"/>/<see cref="Revoke"/>'s own re-drive shape:
    /// a live screen op never buffers, so a replayed one does not either).</summary>
    /// <param name="op">The screen op.</param>
    /// <param name="principal">The acting identity the op is checked against.</param>
    /// <param name="expectedContentHash">Replay only: the CAS pin a recorded <see cref="WorldScreenOp.Insert"/> or
    /// machine-booting <see cref="WorldScreenOp.Select"/> entry carries (a real <c>sha256-64</c> hash, or
    /// <see cref="WorldMachineHost"/>'s "content absent" sentinel when the recording itself never read the file) —
    /// see <see cref="WorldMachineHost.TryInsert"/>'s own remarks. <see langword="null"/> for every other op kind
    /// and for the live path.</param>
    public void ApplyScreenOp(WorldScreenOp op, WorldPrincipal principal, string? expectedContentHash = null) =>
        TryApplyScreenOp(op: op, principal: principal, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, expectedContentHash: expectedContentHash);

    // Applies one screen op SYNCHRONOUSLY (see WorldScreenOp's own remarks for why: never buffered, so a following
    // Command.Engage in the same batch observes the effect). Authority FIRST — Control over the targeted screen(s),
    // the SAME grant subject ScreenCommandModule's pre-inversion client-side precheck used, now checked
    // AUTHORITATIVELY server-side — then the mechanical apply through m_machines. ScreenOpTap fires exactly once,
    // after the outcome (success or refusal) is known, so a refused op still reproduces on replay.
    private bool TryApplyScreenOp(WorldScreenOp op, WorldPrincipal principal, int connectionId, long correlationId, string? expectedContentHash) {
        var verb = op switch {
            WorldScreenOp.Insert => "screen.insert",
            WorldScreenOp.Eject => "screen.eject",
            WorldScreenOp.Select => "screen.select",
            WorldScreenOp.SetOptions => "screen.options",
            WorldScreenOp.Link => "screen.link",
            WorldScreenOp.Unlink => "screen.unlink",
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(op), actualValue: op, message: $"no {nameof(TryApplyScreenOp)} verb name for screen op kind '{op.GetType().Name}'."),
        };

        if (!TryCheckScreenOpControl(op: op, principal: principal, denial: out var deniedIndex)) {
            var denial = $"{principal.Describe()} lacks Control over screen {deniedIndex} — {verb} dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(Message: denial, Rejected: true, Kind: WorldEditEchoKind.ScreenOp, Denied: true, ConnectionId: connectionId, CorrelationId: correlationId));
            ScreenOpTap?.Invoke(arg1: op, arg2: null, arg3: principal);

            return false;
        }

        var (ok, message, contentHash) = op switch {
            WorldScreenOp.Insert insert => m_machines.TryInsert(index: insert.Index, contentPath: insert.ContentPath, engineId: insert.EngineId, options: insert.Options, expectedContentHash: expectedContentHash),
            WorldScreenOp.Eject eject => (m_machines.TryEject(index: eject.Index) is (var ejectOk, var ejectMessage) ? (ejectOk, ejectMessage, (string?)null) : default),
            // Select threads the SAME CAS pin Insert does when the entry it resolves to is a Machine row — a
            // magazine entry's document-declared path is not immune to on-disk drift either. See
            // WorldMachineHost.TrySelect's own remarks.
            WorldScreenOp.Select select => m_machines.TrySelect(index: select.Index, entry: select.Entry, expectedContentHash: expectedContentHash),
            WorldScreenOp.SetOptions options => (m_machines.TryReconfigure(index: options.Index, options: options.Options) is (var optionsOk, var optionsMessage) ? (optionsOk, optionsMessage, (string?)null) : default),
            WorldScreenOp.Link link => (m_machines.TryLink(name: link.Name, members: link.Members) is (var linkOk, var linkMessage) ? (linkOk, linkMessage, (string?)null) : default),
            WorldScreenOp.Unlink unlink => (m_machines.TryUnlink(name: unlink.Name) is (var unlinkOk, var unlinkMessage) ? (unlinkOk, unlinkMessage, (string?)null) : default),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(op), actualValue: op, message: $"no {nameof(TryApplyScreenOp)} apply arm for screen op kind '{op.GetType().Name}'."),
        };

        Console.Error.WriteLine(value: $"[{verb}: {message}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: message, Rejected: !ok, Kind: WorldEditEchoKind.ScreenOp, ConnectionId: connectionId, CorrelationId: correlationId));
        // The content signature (a real hash, or WorldMachineHost's "content absent" sentinel) rides the tape
        // REGARDLESS of whether the op succeeded — a FAILED insert/select must refuse the identical way on
        // replay, or refuse BY NAME the moment the file's on-disk state has since changed (present when it was
        // absent, or vice versa). Gating this on `ok` would tape a failed insert with a null hash, which replays
        // as an UNPINNED live insert — silently diverging if the file later became readable.
        ScreenOpTap?.Invoke(arg1: op, arg2: contentHash, arg3: principal);

        // Latch AnyScreenOpEverApplied for EVERY op that reaches dispatch — not just `ok` ones. A host-level
        // refusal is not uniformly mutation-free: TrySelect moves slot.SelectedEntry BEFORE booting and retains
        // the new selector when the boot fails, so a pre-record failed select still diverges the live host from
        // the definition a recording would capture. Grant denials return before this point and mutate nothing;
        // past the authority gate, over-blocking a recording is safe and under-blocking is a silent divergence.
        AnyScreenOpEverApplied = true;

        return ok;
    }

    // The Control check over a screen op's targeted screen(s): every op names exactly one index except Link (every
    // named member) and Unlink (every member of the ALREADY-LIVE link by that name, when one exists — mirroring the
    // pre-inversion console module's own "control over every member is required to sever" rule; a missing link
    // passes this check trivially and falls through to TryUnlink's own honest "no link" refusal).
    private bool TryCheckScreenOpControl(WorldScreenOp op, WorldPrincipal principal, out int denial) {
        denial = -1;

        var indices = ScreenOpTargets(op: op);

        foreach (var index in indices) {
            if (m_grants.Allows(principal: principal, capability: WorldCapability.Control, subject: GrantSubject.Screen(index: index)) is { IsAllowed: false }) {
                denial = index;

                return false;
            }
        }

        return true;
    }

    // The screen index(es) an op's Control check runs over — see TryCheckScreenOpControl's own remarks for Link/
    // Unlink's multi-member shape.
    private IReadOnlyList<int> ScreenOpTargets(WorldScreenOp op) {
        switch (op) {
            case WorldScreenOp.Insert insert: return new[] { insert.Index };
            case WorldScreenOp.Eject eject: return new[] { eject.Index };
            case WorldScreenOp.Select select: return new[] { select.Index };
            case WorldScreenOp.SetOptions options: return new[] { options.Index };
            case WorldScreenOp.Link link: return link.Members;
            case WorldScreenOp.Unlink unlink:
                return (m_machines.TryReadLinkMembers(name: unlink.Name, members: out var members) ? members : []);
            default:
                throw new ArgumentOutOfRangeException(paramName: nameof(op), actualValue: op, message: $"no {nameof(ScreenOpTargets)} arm for screen op kind '{op.GetType().Name}'.");
        }
    }

    // Swap the live definition and rebuild the derived state that compiled from it. Sim-affecting sections (kits,
    // assignment, motion, wander, seat kit, spawns) recompile the population's fixed tables and live bodies; the
    // scene/screens rebuild on the client through the delivered definition, and cameras/render/population defaults are
    // document-only.
    private void Install(WorldDefinition definition, bool rebuildPopulation) {
        m_definition = definition;
        m_inputHold.Reconfigure(settings: definition.CompiledInputHold);
        RecompileRules(definition: definition);
        // Unconditional, like RecompileRules above: a group/member count is capacity-bounded, so a full resync costs
        // nothing on the ticks that never touch the groups section, and unconditional is what keeps membership
        // expansion CHECK-TIME correct without a bespoke "did this mutation touch Groups" classification to maintain.
        m_grants.SyncGroups(groups: (definition.Groups ?? WorldGroupsSection.Empty).Groups, kinds: (definition.Groups ?? WorldGroupsSection.Empty).Kinds, ownership: (definition.Groups ?? WorldGroupsSection.Empty).Ownership);
        // Unconditional for the identical reason — a drive-gate row lives in `state`, an ordinary section like any
        // other, so there is no cheaper "did this mutation touch a gate row" classification worth maintaining
        // either; this is what makes a live world.state.cell.set that flips a gate settle before the SAME tick's
        // later intent drain reads it (Install always runs before the intents loop within one Step).
        m_grants.SyncState(definition: definition);

        // Reconcile the machine host to the (possibly changed) screens section on EVERY install — cheap (a
        // dictionary diff over a handful of declared screens), and the one choke point every screen-affecting
        // mutation AND every whole-document rebuild both pass through. The host reports which indices it removed;
        // this project (not the host — see WorldMachineHost's own remarks on why) owns the engagement-side admin
        // cleanup for them: m_engagement.DisengageScreen runs before the removed slot's machine is disposed.
        foreach (var removed in m_machines.ReconcileScreens(screens: definition.Screens)) {
            m_engagement.DisengageScreen(screenIndex: removed);
        }

        // Cable links reconcile AFTER screens (a link resolves against the live slot set) — the SAME choke point,
        // so a live UpsertScreenLink/RemoveScreenLink mutation AND a whole-document rebuild (world.reset/.load/
        // .reload) both establish/tear down live links, not merely the boot constructor.
        m_machines.ReconcileLinks(links: definition.Links);

        if (rebuildPopulation) {
            m_population.Rebuild(definition: definition, solids: m_solids);
            m_inputHold.Reset();
            // Reconcile inhabited placements AFTER the census rebuild (a placement/creation/kit edit can add, retire, or
            // re-kit a driven body). Idempotent — a no-op when the inhabited set is unchanged.
            var admitted = new List<WorldPeerEventEntry>();
            var disconnected = new List<WorldPeerEventEntry>();

            m_population.ReconcileInhabitants(definition: definition, admitted: admitted, disconnected: disconnected);
            ApplyLifecycleEvents(admitted: admitted, disconnected: disconnected, ordered: true);
        }
    }

    // Recompiles the rules section and prunes the edge latch to the surviving names. The compiler is called here
    // UNWRAPPED because WorldDefinitionValidator already compiled this exact candidate and refused it if it could
    // not — the same trusted-second-call shape every other derived-state rebuild in Install has.
    private void RecompileRules(WorldDefinition definition) {
        m_rules = WorldRuleCompiler.CompileAll(definition: definition);
        m_interactions = WorldRuleCompiler.CompileAllInteractions(definition: definition);

        PruneGateLatch(latch: m_ruleGateHeld, compiled: m_rules);
        PruneGateLatch(latch: m_interactionGateHeld, compiled: m_interactions);
    }

    // A rule/interaction that no longer exists loses its latch; every surviving name KEEPS its bit, which is the
    // whole reason the latch does not live inside the recompiled array. Shared by m_ruleGateHeld/m_interactions'
    // OWN latch — the two never cross since each call is handed only its own compiled array.
    private static void PruneGateLatch(Dictionary<string, bool> latch, CompiledWorldRule[] compiled) {
        if (latch.Count == 0) {
            return;
        }

        var live = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var rule in compiled) {
            _ = live.Add(item: rule.Name);
        }

        foreach (var name in latch.Keys.ToArray()) {
            if (!live.Contains(item: name)) {
                _ = latch.Remove(key: name);
            }
        }
    }

    // Evaluates every compiled rule's gate and fires its effects, in DOCUMENT ORDER — then every compiled
    // INTERACTION's, same terms, AFTER every rule. That ordering (rules, then interactions, each internally in
    // document order) IS the same-tick effect tiebreak this pair documents: a rule can set up a fact an interaction's
    // gate reads THIS tick, and two interactions cascade in their own declared order (interaction A tags a carrier
    // interaction B's gate then reads) on the identical terms a rule chain already does.
    //
    // Effects apply IMMEDIATELY, not at a boundary: FireWorldRuleEffect calls TryApplyMutation, which installs the
    // composed definition on the spot. So a later rule's gate DOES read an earlier rule's same-tick write — and so
    // does a later effect's live 'from' operand, which reads through the same ReadWorldFact walk. The rules in one
    // tick are a sequence, not a simultaneous snapshot, and a chain (rule A sets a flag, rule B gates on it, rule C
    // copies it) fires end to end within one tick. That is deterministic because document order is: the same
    // document and the same input produce the same sequence on every run, machine, and backend.
    private void EvaluateWorldRules(ulong tick, ulong stepTicks) {
        EvaluateCompiledRules(rules: m_rules, latch: m_ruleGateHeld, tick: tick, stepTicks: stepTicks);
        EvaluateCompiledRules(rules: m_interactions, latch: m_interactionGateHeld, tick: tick, stepTicks: stepTicks);
    }

    // The rule/interaction ARRAY is snapshotted first (the caller's own m_rules/m_interactions read), which is a
    // different thing from the state the gates read: a rule's own effect installs a new definition, which reassigns
    // m_rules/m_interactions — and iterating a field an inner call reassigns is how a rule would silently stop seeing
    // its siblings mid-tick. Every row declared at the top of the tick evaluates during this tick; a row ADDED by
    // this tick's effects starts on the next one, the same next-tick boundary every other mutation already lands on.
    private void EvaluateCompiledRules(CompiledWorldRule[] rules, Dictionary<string, bool> latch, ulong tick, ulong stepTicks) {
        if (rules.Length == 0) {
            return;
        }

        foreach (var rule in rules) {
            var open = RuleGateOpen(gate: rule.Gate, tick: tick);
            var wasOpen = latch.GetValueOrDefault(key: rule.Name);

            latch[rule.Name] = open;

            if (!open) {
                continue;
            }

            // EDGE fires on the CROSSING alone and re-arms only when the gate closes again; LEVEL fires every tick
            // the gate holds. One vocabulary, the same ActionTriggerMode a per-body fact trigger reads.
            if ((rule.Mode == ActionTriggerMode.Edge) && wasOpen) {
                continue;
            }

            foreach (var effect in rule.Effects) {
                FireWorldRuleEffect(effect: effect, tick: tick, stepTicks: stepTicks);
            }
        }
    }

    // ESCROW RECOVERY — the "recovery is a LIFETIME RULE" half of the escrow/transfer lane, run every tick right
    // beside world-rule evaluation (deterministic, tick-driven, no wall clock — the SAME $tick unit a rule's own
    // gate would compare against). Fires an ordinary SettleOwnership(Reclaim: true) under WorldPrincipal.World — the
    // SAME structural-exemption door a rule's own effects use (Server.WorldServer.TryAdmitMutation admits it before
    // the grant table is even consulted) — for every subject whose escrow has reached its DeadlineTick with no
    // accept. Recovery therefore needs no operator action: the offerer gets the subject back the tick the deadline
    // passes, exactly as if a world-authored rule had reclaimed it. `ownership` is read once, before any mutation in
    // this pass swaps `m_definition` — an IReadOnlyList this project never mutates in place (every write rebuilds a
    // new list via Upsert), so iterating the pre-sweep snapshot while TryApplyMutation installs later candidates is
    // safe; a subject an earlier iteration already reclaimed this tick simply is not read again.
    private void ReclaimExpiredEscrows(ulong tick) {
        var ownership = (m_definition.Groups ?? WorldGroupsSection.Empty).Ownership;

        foreach (var row in ownership) {
            if ((row.Owner.Kind == OwnershipOwnerKind.Escrow) && (row.Owner.Escrow is { } escrow) && (unchecked((long)tick) >= escrow.DeadlineTick)) {
                _ = TryApplyMutation(mutation: new WorldMutation.SettleOwnership(Principal: WorldPrincipal.World, Subject: row.Subject, Reclaim: true), tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false);
            }
        }
    }

    // MARKET DEADLINE RECOVERY — the SAME "recovery is a LIFETIME RULE" shape ReclaimExpiredEscrows establishes,
    // fired right beside it: an Active listing whose DeadlineTick has passed settles (a standing bid wins) or
    // expires (no bid ever landed) with no operator action, under WorldPrincipal.World, the identical structural
    // exemption a rule effect's own writes use. `listings` is read once, before any mutation in this pass swaps
    // m_definition, matching ReclaimExpiredEscrows' own safe-iteration remark.
    private void SettleExpiredMarketListings(ulong tick) {
        var listings = (m_definition.Market ?? WorldMarketSection.Empty).Listings ?? [];

        foreach (var listing in listings) {
            if ((listing.Status == WorldMarketListingStatus.Active) && (unchecked((long)tick) >= listing.DeadlineTick)) {
                _ = TryApplyMutation(mutation: new WorldMutation.SettleMarketListing(Principal: WorldPrincipal.World, ListingId: listing.Id), tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false);
            }
        }
    }

    // Market retention sweep — the same "recovery is a lifetime rule" shape ReclaimExpiredEscrows/
    // SettleExpiredMarketListings establish: once a terminal row has stood past market.retentionSeconds, fires
    // exactly one PruneMarketListings mutation (never one per row — its own compose arm removes every eligible row
    // in the same candidate) under WorldPrincipal.World. Checked here, before submitting, so a quiescent market with
    // nothing yet eligible never drives a mutation that would only compose to a loud no-op refusal every tick — the
    // identical reason SettleExpiredMarketListings/ReclaimExpiredEscrows pre-filter their own loops.
    private void PruneExpiredMarketListings(ulong tick) {
        var market = (m_definition.Market ?? WorldMarketSection.Empty);

        if (m_definition.SimulationRateHz <= 0) {
            return;
        }

        var retentionTicks = unchecked((long)WorldSimulationTickConversion.DurationTicks(seconds: market.RetentionSeconds, ratePerSecond: (uint)m_definition.SimulationRateHz));

        foreach (var listing in (market.Listings ?? [])) {
            if ((listing.Status != WorldMarketListingStatus.Active)
                && (listing.ResolvedTick is { } resolvedTick)
                && (unchecked((long)tick) >= unchecked(resolvedTick + retentionTicks))) {
                _ = TryApplyMutation(mutation: new WorldMutation.PruneMarketListings(Principal: WorldPrincipal.World), tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false);

                return;
            }
        }
    }

    private bool RuleGateOpen(CompiledWorldPredicate[] gate, ulong tick) {
        foreach (var predicate in gate) {
            var value = ReadWorldFact(operand: predicate.Left, tick: tick);

            // The comparand is EITHER the compile-time constant (Comparand null) or a second live operand read on the
            // SAME terms as the primary side — the cross-row spelling of compareState. Both facts are read from THIS
            // tick's live m_definition, so a rule that just advanced its own comparand row (a self-advancing
            // schedule) sees the post-advance value on the VERY NEXT evaluation, never the value it opened against.
            var expected = ((predicate.Comparand is { } comparand)
                ? ReadWorldFact(operand: comparand, tick: tick)
                : new WorldFact(Value: predicate.Value, IsForever: false));

            if (!predicate.Comparison.Holds(value: value.Value, valueIsForever: value.IsForever, expected: expected.Value, expectedIsForever: expected.IsForever)) {
                return false;
            }
        }

        return true;
    }

    // One live fact off a rule operand: a fixed-point value, or POSITIVE INFINITY (IsForever) for the one channel
    // whose magnitude can exceed every number — $parked: on a forever-parked body. Infinity participates in
    // comparisons through the ActionStateComparisons overload and is never encoded as a numeric stand-in.
    private readonly record struct WorldFact(FixedQ4816 Value, bool IsForever);

    // Shared by both sides of a compareState conjunct — the primary operand and, when present, the comparand — so
    // the two reads can never diverge in how a reserved channel or a declared row resolves to a live fact.
    private WorldFact ReadWorldFact(CompiledWorldOperand operand, ulong tick) => operand.Kind switch {
        WorldRuleFactKind.Tick => Finite(value: FixedQ4816.FromInteger(value: unchecked((long)tick))),
        WorldRuleFactKind.Population => Finite(value: FixedQ4816.FromInteger(value: m_population.ActiveCount())),
        WorldRuleFactKind.RegionOccupancy => Finite(value: FixedQ4816.FromInteger(value: m_events.OccupantCount(placementId: operand.Row!))),
        // The SAME IWorldMachineMemoryPeek.TryPeek primitive WorldAddonRuntime's memory-watch family already rides,
        // called directly instead of accumulated as a change event. No machine booted (or no peek capability) reads
        // as 0 — never a hard refusal, since the machine can boot on a later tick.
        WorldRuleFactKind.MachineMemory => Finite(value: FixedQ4816.FromInteger(value: (Machines.TryPeek(screen: operand.Screen, address: operand.Address, out var raw) ? raw : (byte)0))),
        WorldRuleFactKind.Reduction => Finite(value: ReadReduction(row: operand.Row!, op: operand.Reduce, tick: tick)),
        WorldRuleFactKind.ArgBody => Finite(value: FixedQ4816.FromInteger(value: ResolveArgBody(row: operand.Row!, op: operand.Reduce, tick: tick))),
        WorldRuleFactKind.BodyDistance => Finite(value: ReadBodyDistance(bodyA: operand.BodyA!.Value, bodyB: operand.BodyB!.Value, tick: tick)),
        WorldRuleFactKind.LineOfSight => Finite(value: FixedQ4816.FromInteger(value: (ReadBodyLineOfSight(bodyA: operand.BodyA!.Value, bodyB: operand.BodyB!.Value, tick: tick) ? 1 : 0))),
        // Preserve the reserved channel's authored contract: $parked reports the population deadline's own
        // SIMULATION-tick unit. Engine-tick countdown rows use countdownState instead; changing this unrelated
        // channel's unit would silently retune every existing raw compareState threshold and fromState copy.
        WorldRuleFactKind.Parked => ((ReadParkedRemaining(bodyRef: operand.BodyA!.Value, tick: tick) is { } remaining)
            ? Finite(value: FixedQ4816.FromInteger(value: remaining))
            : new WorldFact(Value: FixedQ4816.Zero, IsForever: true)),
        _ => Finite(value: ReadStateCell(row: operand.Row!, key: operand.Key!, tick: tick)),
    };

    private static WorldFact Finite(FixedQ4816 value) => new(Value: value, IsForever: false);

    // $parked: — the remaining reconnect-grace ticks for ONE named body, resolved through the SAME ResolveBodyRef
    // walk $distance:/$los: use for each of their two body references. THREE REGIMES, deliberately distinct:
    // ABSENT (a reference resolving to no live body, or an unparked one) reads as 0 through
    // WorldPopulation.ParkedRemainingTicks' own guards — the ordinary "absent reads as the neutral falsy value"
    // convention (see WorldRuleFacts.ParkedPrefix's remarks for why 0 is right for absence); FINITE parks read
    // their real remaining count; FOREVER (a null deadline — parked at rate 0) reads as null here and becomes
    // POSITIVE INFINITY in the fact layer, never a numeric sentinel: it IS parked (remaining > 0 holds, > any
    // finite holds, <= any finite does not), but there is no number to compare with or copy — a copy operand
    // alone cannot fire from it (see ApplyRuleEffect's own forever guard).
    private long? ReadParkedRemaining(CompiledBodyRef bodyRef, ulong tick) =>
        m_population.ParkedRemainingTicks(index: ResolveBodyRef(bodyRef: bodyRef, tick: tick), tick: tick);

    // Reads a declared cell as fixed point off the LIVE definition (Install swaps it on every apply, so this is
    // always this tick's settled document), through the ONE shared (row, key) resolver — which computes an advancing
    // row's LIVE value rather than its stored base, so a rule composes with the trait instead of duplicating it. A
    // row or cell the document no longer declares reads as zero rather than throwing — a mid-tick RemoveStateRow is
    // the only way to get there, and the next Install's recompile refuses the rule outright if it can no longer
    // resolve.
    private FixedQ4816 ReadStateCell(string row, string key, ulong tick) {
        if (!WorldStateReader.TryRead(definition: m_definition, rowName: row, key: key, tick: tick, row: out var declared, rawValue: out var rawValue, text: out _) || (rawValue is not { } raw)) {
            return FixedQ4816.Zero;
        }

        return ((declared.Kind == CellKind.Fixed) ? FixedQ4816.FromRawBits(value: raw) : FixedQ4816.FromInteger(value: raw));
    }

    // The $reduce: aggregate — a thin delegation to WorldStateReader.Reduce, the ONE (row, key) read seam's sibling
    // for a whole-row aggregate: it resolves EACH cell's value through TryRead's own per-key path (not the row's
    // declared cell list raw), so a future per-cell advance widening flows through here for free. Count is always
    // integer regardless of the row's declared kind (a count is never fixed-point); Max/Min/Sum preserve the row's
    // kind, matching the compiler's own ValueKind (WorldRuleCompiler.ResolveOperand's reduce branch). An empty row
    // reads as zero for every op — the SAME "absent reads as zero" precedent ReadStateCell itself follows for a
    // vanished cell.
    private FixedQ4816 ReadReduction(string row, WorldStateReduceOp op, ulong tick) =>
        WorldStateReader.Reduce(definition: m_definition, rowName: row, op: op, tick: tick);

    // The $argmax:/$argmin: extremum — a thin delegation to WorldStateReader.ArgExtremum, the SAME per-key read seam
    // ReadReduction's sibling resolves each candidate cell through, filtered here to the body indices the LIVE
    // population actually holds (a cell whose key does not parse as a non-negative index is excluded inside the
    // reader itself; the row can gain a non-numeric-keyed cell after compile via an ordinary world.state.cell.set,
    // and compile-time already proved the row is keyed, not that every future key will parse). Ties resolve to the
    // LOWEST eligible index, deterministically. Returns -1 ("no body") when no cell is eligible.
    private int ResolveArgBody(string row, WorldStateReduceOp op, ulong tick) {
        var winner = WorldStateReader.ArgExtremum(
            definition: m_definition,
            rowName: row,
            op: op,
            tick: tick,
            isCandidateIndex: (index => (index < m_population.Capacity))
        );

        return ((winner is null) ? -1 : int.Parse(s: winner, style: System.Globalization.NumberStyles.Integer, provider: System.Globalization.CultureInfo.InvariantCulture));
    }

    // The engine's largest representable magnitude — the DELIBERATELY-INVERTED sentinel WorldRuleFacts.DistancePrefix's
    // own remarks explain: unlike $machine:/$region:, where zero is a correct neutral count for "nothing there",
    // distance's neutral-for-absence value must never read as "close", or a within-range gate (compareState against
    // lessOrEqual) would spuriously OPEN for a body reference that resolved to nothing.
    private static readonly FixedQ4816 s_noBodyDistance = FixedQ4816.MaxValue;

    // $distance: — the straight-line distance between two named bodies, read through WorldServer.Body(int)'s own
    // bounds check (null for an out-of-range index or an inactive slot). Either side missing reads as
    // s_noBodyDistance rather than zero (see its own remarks).
    private FixedQ4816 ReadBodyDistance(CompiledBodyRef bodyA, CompiledBodyRef bodyB, ulong tick) {
        var a = Body(index: ResolveBodyRef(bodyRef: bodyA, tick: tick));
        var b = Body(index: ResolveBodyRef(bodyRef: bodyB, tick: tick));

        if ((a is null) || (b is null)) {
            return s_noBodyDistance;
        }

        return (b.FixedPosition - a.FixedPosition).Length;
    }

    // $los: — the SAME WorldPopulation.HasLineOfSightBetween a sensed target's own RequiresLineOfSight check rides,
    // called against two RESOLVED body references. Either side resolving to no body (a negative index) reads as
    // false — no sight line to nothing, the ordinary "absent reads as the falsy value" convention.
    private bool ReadBodyLineOfSight(CompiledBodyRef bodyA, CompiledBodyRef bodyB, ulong tick) {
        var indexA = ResolveBodyRef(bodyRef: bodyA, tick: tick);
        var indexB = ResolveBodyRef(bodyRef: bodyB, tick: tick);

        return ((indexA >= 0) && (indexB >= 0) && m_population.HasLineOfSightBetween(bodyA: indexA, bodyB: indexB));
    }

    // Resolves ONE body reference to a live 0-based index (or -1 for "no body") — a literal index passes through
    // unchanged (compile time already bounded it against the document's declared capacity), an argmax/argmin
    // resolves through the SAME ResolveArgBody walk the standalone $argmax:/$argmin: channel uses.
    private int ResolveBodyRef(CompiledBodyRef bodyRef, ulong tick) => (bodyRef.Kind switch {
        CompiledBodyRefKind.Literal => bodyRef.Index,
        _ => ResolveArgBody(row: bodyRef.Row!, op: ((bodyRef.Kind == CompiledBodyRefKind.ArgMax) ? WorldStateReduceOp.Max : WorldStateReduceOp.Min), tick: tick),
    });

    // The inverse of the compiler's own literal-to-raw conversion (WorldRuleCompiler.ResolveWrite), applied to a LIVE
    // FixedQ4816 read instead of an authored constant. Compile-time kind-matching (EffectSourceKindMismatch) already
    // proved 'kind' equals the source operand's own resolved kind, so an Int/Bool source is always an EXACT integer
    // in fixed-point form (ReadWorldFact only ever reaches FromInteger for those two) — recovered by an exact shift,
    // never a float round-trip; a Fixed source's raw bits are copied verbatim, bit-identical to the source cell.
    private static long ConvertWorldFactToRaw(FixedQ4816 value, CellKind kind) => kind switch {
        CellKind.Fixed => value.Value,
        CellKind.Bool => ((value.Value != 0L) ? 1L : 0L),
        _ => (value.Value >> FixedQ4816.FractionBitCount), // Int.
    };

    // Submits the effect's own ORDINARY mutation through the ordinary pipeline (admission → compose → whole-document
    // validate → install → journal → echo), stamped WorldPrincipal.World — the SAME door UpsertHudPanel/RemoveHudPanel
    // /UpsertPlacement/RemovePlacement already have from the console or an addon; nothing here is a new admission
    // path. A WRITE THAT CANNOT MOVE THE DESTINATION is skipped before submission — either the resolved value already
    // matches the cell, or the row's declared envelope pins the cell where it is (see the Write arm): a
    // level-triggered gate re-fires every tick it holds, and without this a standing rule would append an identical
    // journal entry forever, or draw an identical refusal forever. A GENERATE is never a no-op — it advances the
    // generator's cursor by construction — and neither is a HUD/placement upsert or remove, so both are submitted
    // (the one exception being a removePlacement on a possessed carrier, which the CarrierPossessed guard below skips
    // outright rather than submitting). SAVE is the one exception to all of this: it submits no WorldMutation at all (see
    // ActionEffect.Save's remarks) and is handled before the mutation switch below ever runs.
    private void FireWorldRuleEffect(CompiledWorldEffect effect, ulong tick, ulong stepTicks) {
        if (effect.Kind == WorldRuleEffectKind.Save) {
            // No compose, no validate, no install, no journal — SaveEffectTap performs the identical settle-at-save
            // capture 'world.save' itself runs, straight to the world's own loaded file. A null tap (no composition
            // root wired) is a silent no-op, the same convention EchoTap follows.
            SaveEffectTap?.Invoke(tick);

            return;
        }

        if (effect.Kind is WorldRuleEffectKind.Write or WorldRuleEffectKind.Countdown) {
            // The destination's CURRENT value through the same shared resolver the gate read: an absent cell reads as
            // zero (an Add mints it), an absent ROW is nothing to write. On an ADVANCING row that is the LIVE value,
            // not the stored base, which is what the could-this-move skip below needs: a base is a fixed point of
            // its own accumulation, so comparing against it would call a write "no-op" whenever the base already
            // happened to match — silently skipping the write, and with it the rebase that is the only way a rule
            // can reset an advancing row at all.
            if (!WorldStateReader.TryRead(definition: m_definition, rowName: effect.Row, key: effect.Key, tick: tick, row: out var row, rawValue: out var destination, text: out _)) {
                return;
            }

            var current = (destination ?? 0L);

            // A live 'from' operand is read fresh EVERY firing (Install swaps m_definition on every apply, so this
            // reads the same settled state a compareState comparand would this tick) and converted to the
            // destination row's own encoding; a literal effect keeps the value the compiler already converted once.
            // A FOREVER fact ($parked: on a forever-parked body) has no number to store — the copy silently does not
            // fire, the same no-narration shape a level gate's own not-holding takes (see ReadParkedRemaining).
            if ((effect.Kind != WorldRuleEffectKind.Countdown) && (effect.From is { } foreverProbe) && ReadWorldFact(operand: foreverProbe, tick: tick).IsForever) {
                return;
            }

            var raw = effect.Kind == WorldRuleEffectKind.Countdown
                ? -Math.Min(val1: current, val2: checked((long)stepTicks))
                : ((effect.From is { } from) ? ConvertWorldFactToRaw(value: ReadWorldFact(operand: from, tick: tick).Value, kind: row.Kind) : effect.RawValue);
            var next = ((effect.Write == WorldDocumentWriteKind.Add) ? unchecked(current + raw) : raw);

            // SUBMIT ONLY WHAT COULD MOVE THE DESTINATION. Arithmetic identity is not the whole of that test: a cell
            // already sitting ON a bound its own row declares (NonNegative/Min/Max) cannot be pushed further past it,
            // so a Level gate pointed at a floored row would go on composing a candidate the whole-document validator
            // refuses, once per tick, for the life of the session — the same standing-rule failure the arithmetic
            // check was added for, reached through the row's envelope instead of through its arithmetic.
            //
            // The projection decides WHETHER to submit and never WHAT is submitted: the mutation still carries the
            // rule's own unclamped operand, so a write that genuinely tries to cross a bound (a cell at 3 taking -5)
            // is still submitted and still refused BY NAME. That is the settled envelope duality — a computed value
            // clamps, an explicit write refuses — with the inert case removed from the write side, not softened.
            if (row.ClampToEnvelope(value: next) == current) {
                return;
            }

            _ = TryApplyMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.World, Row: effect.Row, Key: effect.Key, Value: raw, Kind: effect.Write), tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false);

            return;
        }

        // DESPAWN-OF-OWNED-CARRIER GUARD (WorldRuleEffectRefusal.CarrierPossessed): a removePlacement targeting a
        // placement whose Inhabit facet is currently bound to a POSSESSED body (a concrete drive grant — see
        // WorldGrants.IsBodyPossessed's own remarks) is skipped rather than fired. This is the widening
        // UpsertPlacement/RemovePlacement's admission into the rule-effect vocabulary was missing: a placement's
        // Inhabit/Region facets already make an ordinary whole-row upsert/remove a BODY/REGION carrier spawn/despawn
        // (WorldPopulation.ReconcileInhabitants reconciles from ANY accepted mutation, principal-agnostic) — this is
        // the one case that must NOT go through silently, because it would destroy an explicit possession grant's
        // binding out from under it (the slot a later, unrelated inhabitant can then claim). OWNER DECISION: REFUSE,
        // never orphan-to-escrow (see the refusal's own remarks for why).
        if ((effect.Kind == WorldRuleEffectKind.RemovePlacement) && TryFindPossessedInhabitant(placementId: effect.Row, bodyIndex: out var possessedBody, holder: out var possessor)) {
            Console.Error.WriteLine(value: $"[world.rule: despawn refused ({WorldRuleEffectRefusal.CarrierPossessed}) — placement '{effect.Row}' carries inhabitant body:{possessedBody}, possessed by {possessor.Describe()}; revoke the drive grant before despawning, or clear the possession in the rule's own effects first]");

            return;
        }

        WorldMutation mutation = effect.Kind switch {
            WorldRuleEffectKind.Generate => new WorldMutation.Generate(Principal: WorldPrincipal.World, Row: effect.Row),
            WorldRuleEffectKind.UpsertHudPanel => new WorldMutation.UpsertHudPanel(Principal: WorldPrincipal.World, Panel: effect.HudPanel!),
            WorldRuleEffectKind.RemoveHudPanel => new WorldMutation.RemoveHudPanel(Principal: WorldPrincipal.World, Id: effect.Row),
            WorldRuleEffectKind.UpsertPlacement => new WorldMutation.UpsertPlacement(Principal: WorldPrincipal.World, Placement: effect.Placement!),
            WorldRuleEffectKind.RemovePlacement => new WorldMutation.RemovePlacement(Principal: WorldPrincipal.World, Id: effect.Row),
            _ => throw new InvalidOperationException(message: $"world rule effect kind '{effect.Kind}' has no fire mapping."),
        };

        _ = TryApplyMutation(mutation: mutation, tick: tick, connectionId: SubmissionEnvelope.LocalConnectionId, correlationId: 0, preMetered: false);
    }

    // Every LIVE inhabitant of placementId, drive-possessed by a concrete grant — the guard's own read, walked over
    // WorldPopulation.CollectInhabitants' (small, per-placement) result. Stops at the first possessed inhabitant: one
    // is enough to refuse the whole despawn, and a multi-count Inhabit facet is rare enough that finding every
    // possessed slot before refusing would not change the operator's remedy.
    private bool TryFindPossessedInhabitant(string placementId, out int bodyIndex, out WorldPrincipal holder) {
        m_population.CollectInhabitants(placementId: placementId, into: m_ruleInhabitantScratch);

        foreach (var index in m_ruleInhabitantScratch) {
            if (m_grants.IsBodyPossessed(body: index, holder: out holder)) {
                bodyIndex = index;

                return true;
            }
        }

        bodyIndex = -1;
        holder = default;

        return false;
    }

    // The `world.rules` read-back. An `all` gate prints ITS PREDICATES, never a List type name — the whole reason a
    // compiled conjunct carries its authored spelling beside its resolved form.
    //
    // `latch=held|open` is m_ruleGateHeld, and the KEY names what the values say: the gate-held latch is HELD when
    // the gate held at the last evaluation (an Edge rule will not fire again until it lets go) and OPEN when it did
    // not (so the next tick the gate holds is a crossing, and an Edge rule fires). It read `armed=` before, which
    // inverted the sense it implied — a latch reading `armed=open` is the state in which an edge rule IS armed.
    private string DescribeRules() => DescribeCompiledRules(verb: "world.rules", rules: m_rules, latch: m_ruleGateHeld);

    // The `world.interactions` read-back — the SAME line shape DescribeRules gives a compiled rule, since an
    // interaction IS a compiled rule under the hood (see WorldRuleCompiler.CompileAllInteractions). This is also the
    // "echo an interaction firing" read-back the effect substrate promises: `latch=held` at the last evaluation IS
    // "this interaction fired (or is still holding, under Level)", the identical signal a rule's own latch already
    // gives.
    private string DescribeInteractions() => DescribeCompiledRules(verb: "world.interactions", rules: m_interactions, latch: m_interactionGateHeld);

    // Shared by DescribeRules/DescribeInteractions: an `all` gate prints ITS PREDICATES, never a List type name — the
    // whole reason a compiled conjunct carries its authored spelling beside its resolved form.
    //
    // `latch=held|open` is HELD when the gate held at the last evaluation (an Edge row will not fire again until it
    // lets go) and OPEN when it did not (so the next tick the gate holds is a crossing, and an Edge row fires).
    private static string DescribeCompiledRules(string verb, CompiledWorldRule[] rules, Dictionary<string, bool> latch) {
        if (rules.Length == 0) {
            return $"[{verb}: none]";
        }

        var lines = new List<string>(capacity: rules.Length);

        foreach (var rule in rules) {
            var gate = ((rule.Gate.Length == 0)
                ? "always"
                : string.Join(separator: " and ", values: rule.Gate.Select(selector: static predicate => predicate.Describe)));
            var effects = string.Join(separator: "; ", values: rule.Effects.Select(selector: static effect => effect.Describe));

            lines.Add(item: $"{rule.Name} mode={rule.Mode.ToString().ToLowerInvariant()} latch={(latch.GetValueOrDefault(key: rule.Name) ? "held" : "open")} when {gate} -> {effects}");
        }

        return $"[{verb}: {string.Join(separator: " | ", values: lines)}]";
    }

    // The `world.properties` read-back: with no body index, the declared vocabulary; with one, which registered
    // properties are currently ON for that carrier (a nonzero cell at key=<bodyIndex>) — resolved through
    // WorldStateReader.TryRead, the SAME (row, key) read a rule's gate and world.state itself run, so this cannot
    // report a tag the engine would not have read.
    private string DescribeProperties(int? bodyIndex) {
        var names = (m_definition.Properties?.Names ?? []);

        if (bodyIndex is not { } index) {
            return ((names.Count == 0)
                ? "[world.properties: none]"
                : $"[world.properties: {string.Join(separator: ", ", values: names)}]");
        }

        if ((index < 0) || (index >= m_definition.Population.Capacity)) {
            return $"[world.properties {index}: outside 0..{(m_definition.Population.Capacity - 1)} for the authored population capacity]";
        }

        var tick = (NextInputTick - 1UL);
        var key = index.ToString(provider: System.Globalization.CultureInfo.InvariantCulture);
        var tags = new List<string>();

        foreach (var name in names) {
            if (WorldStateReader.TryRead(definition: m_definition, rowName: name, key: key, tick: tick, row: out _, rawValue: out var raw, text: out _) && (raw is { } value) && (value != 0)) {
                tags.Add(item: name);
            }
        }

        return $"[world.properties {index}: {((tags.Count == 0) ? "none" : string.Join(separator: ", ", values: tags))}]";
    }

    // A refused whole-document rebuild: loud, and echoed so the same tap that counts a refused mutation counts this
    // too.
    private void RejectRebuild(string verb, string reason, int connectionId, long correlationId) {
        Console.Error.WriteLine(value: $"[world.definition rejected: {verb} — {reason}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"{verb} rejected: {reason}", Rejected: true, Kind: WorldEditEchoKind.Rebuild, ConnectionId: connectionId, CorrelationId: correlationId));
    }

    private void Reject(WorldMutation mutation, string reason, int connectionId, long correlationId) {
        Console.Error.WriteLine(value: $"[world.mutation rejected: {Describe(mutation: mutation)} — {reason}]");
        EchoTap?.Invoke(obj: new WorldEditEcho(Message: $"{Describe(mutation: mutation)} rejected: {reason}", Rejected: true, Kind: WorldEditEchoKind.Mutation, Mutation: mutation, ConnectionId: connectionId, CorrelationId: correlationId));
    }

    // Whether a mutation is DOCUMENT-DEFAULTS class (edits the next boot's wake state; live session levers own "now").
    // Everything else, cameras included, applies live on delivery.
    private static bool IsDocumentDefaults(WorldMutation mutation) => mutation is
        WorldMutation.SetRenderDefaults or WorldMutation.SetPopulationDefaults or WorldMutation.SetHostDefaults;

    // Whether a mutation recompiles the population's fixed-point derived state (kit table, kit indices, live bodies'
    // compiled tuning/actions, AND the analytic collider set). A screen/collision edit rebuilds the collider set so a
    // live screens or collision change takes effect on the next tick with no restart.
    private static bool AffectsPopulation(WorldMutation mutation) => mutation is
        WorldMutation.UpsertKit or WorldMutation.RemoveKit or WorldMutation.SetDefaultSeatKit or
        WorldMutation.SetKitAssignment or WorldMutation.SetMotion or WorldMutation.SetSpawns or
        WorldMutation.SetCollision or
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        // The LOOK mutations re-resolve the population's look table (PRESENTATION-ONLY, but Rebuild is the one path that
        // re-runs ResolveLookIndices and bumps the client's program-rebuild revision).
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment or
        // SetPopulationDefaults carries the distribution and variation rows: Rebuild recompiles their fixed spawn
        // policy so it is LIVE for future activations (the live census count still stays the world.population verb — this
        // Rebuild re-seeds SpawnPosition but never re-activates or teleports a standing body).
        WorldMutation.SetPopulationDefaults or
        // A placement row can change the census (Arc 7's Inhabit facet: a placement contributes driven bodies), and an
        // inhabited row's kit resolution reads the creation's Locomotion, so a creation swap can move a body between
        // kits — all must trigger Rebuild + ReconcileInhabitants. (R13: the third and last edit to this switch.)
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation;

    // Adjacency overlap proofs depend on motion envelopes, every kit's collider, and interaction/targeting reach.
    // Those edits need a fresh neighbour proof at the load boundary.
    private static bool AdjacencyProofInputsChanged(WorldDefinition current, WorldDefinition candidate, WorldMutation mutation) => mutation switch {
        WorldMutation.SetMotion => (current.Motion != candidate.Motion),
        WorldMutation.UpsertKit or WorldMutation.RemoveKit => !current.Kits.SequenceEqual(second: candidate.Kits),
        WorldMutation.UpsertInteraction or WorldMutation.RemoveInteraction => current.Interactions != candidate.Interactions,
        _ => false,
    };

    // Build the SDF contact field for a candidate — null when the requirements permit analytic contact (the set is
    // derived inside the population's compile, not here), the built field under the FIELD provider, or a
    // named failure when a solid names an op the warp-free evaluator cannot interpret.
    private static bool TryBuildSolids(WorldDefinition definition, out WorldSolidField? solids, out string reason) {
        reason = string.Empty;

        if (!WorldContactSelection.RequiresField(collision: definition.Collision) && !WorldTargetSelection.RequiresLineOfSight(definition: definition)) {
            solids = null;

            return true;
        }

        return WorldSolidField.TryBuild(definition: definition, built: out solids, reason: out reason);
    }

    // Adopt a wholesale-rebuilt field (a swap/undo), bumping the revision when the field actually moved so the status
    // read-back tracks it. A swap into an analytic world clears the field.
    private void SwapSolids(WorldSolidField? solids) {
        if (!ReferenceEquals(objA: solids, objB: m_solids)) {
            m_solids = solids;
            m_solidRevision++;
        }
    }

    // Whether a mutation can change the SDF contact field: the collision tuning and every solid-bearing section
    // (screens, creations that reshape a stamp, placements). Coarse by section,
    // matching AffectsPopulation/AffectsRenderEnvelope.
    private static bool AffectsSolidField(WorldMutation mutation) => mutation is
        WorldMutation.SetCollision or
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement;

    // Whether a mutation can grow the SDF program past the probed render envelope (screen slabs / creation stamps — an
    // UpsertCreation re-shapes every live placement of it, so it measures too).
    private static bool AffectsRenderEnvelope(WorldMutation mutation) => mutation is
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen or
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation or
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement or
        // A creation look will change the emitted program word count (a body worn as a stamp) once creation-look
        // rendering lands (Arc 7); catalog looks add zero words today, so this arm is honest groundwork — all three look
        // mutations already ride the envelope gate so the loud capacity rejection will fire at apply time, not at a later
        // GPU allocation, the moment creation stamps render.
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment;

    // The world-document section a mutation targets — the Mutate-capability subject it is checked against. One section
    // per mutation kind (coarse, section-keyed — a genre world adds sections + kinds, never changes this mapping).
    private static WorldSection SectionOf(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertKit or WorldMutation.RemoveKit or WorldMutation.SetDefaultSeatKit or WorldMutation.SetKitAssignment => WorldSection.Kits,
        WorldMutation.UpsertScreen or WorldMutation.RemoveScreen => WorldSection.Screens,
        WorldMutation.UpsertCamera or WorldMutation.RemoveCamera => WorldSection.Cameras,
        WorldMutation.SetSpawns => WorldSection.Spawns,
        WorldMutation.SetMotion => WorldSection.Motion,
        WorldMutation.SetPopulationDefaults => WorldSection.Population,
        WorldMutation.SetRenderDefaults => WorldSection.Render,
        WorldMutation.UpsertAddon or WorldMutation.RemoveAddon => WorldSection.Addons,
        WorldMutation.UpsertBindingOverlay or WorldMutation.RemoveBindingOverlay => WorldSection.Bindings,
        WorldMutation.UpsertCreation or WorldMutation.RemoveCreation => WorldSection.Creations,
        WorldMutation.UpsertPlacement or WorldMutation.RemovePlacement => WorldSection.Placements,
        WorldMutation.SetAuthoringDefaults => WorldSection.Authoring,
        WorldMutation.UpsertSpeaker or WorldMutation.RemoveSpeaker => WorldSection.Speakers,
        WorldMutation.UpsertTune or WorldMutation.RemoveTune => WorldSection.Tunes,
        WorldMutation.UpsertPatch or WorldMutation.RemovePatch => WorldSection.Patches,
        WorldMutation.SetAudioDefaults => WorldSection.Audio,
        WorldMutation.SetCollision => WorldSection.Collision,
        WorldMutation.SetHostDefaults => WorldSection.Host,
        WorldMutation.SetViewDefaults or WorldMutation.UpsertViewLayout or WorldMutation.RemoveViewLayout => WorldSection.Views,
        WorldMutation.SetPlayerDefaults => WorldSection.PlayerDefaults,
        WorldMutation.UpsertLook or WorldMutation.RemoveLook or WorldMutation.SetLookAssignment => WorldSection.Looks,
        WorldMutation.UpsertScreenLink or WorldMutation.RemoveScreenLink => WorldSection.Links,
        WorldMutation.UpsertGrant or WorldMutation.RemoveGrant => WorldSection.Grants,
        WorldMutation.UpsertHudPanel or WorldMutation.RemoveHudPanel or WorldMutation.UpsertHudElement or WorldMutation.RemoveHudElement or WorldMutation.SetHudDefaults => WorldSection.Hud,
        // Generate's OBSERVABLE effect is a state write, so it shares the state section's coarse hold; its narrower
        // authority is the SAME row-scoped Edit/state:<row> hold every other state write takes, never a second
        // section.
        WorldMutation.UpsertStateRow or WorldMutation.RemoveStateRow or WorldMutation.UpsertStateCell or WorldMutation.RemoveStateCell or WorldMutation.Generate => WorldSection.State,
        WorldMutation.SetInputHold => WorldSection.InputHold,
        WorldMutation.UpsertWorldRule or WorldMutation.RemoveWorldRule => WorldSection.Rules,
        WorldMutation.UpsertGroupKind or WorldMutation.RemoveGroupKind or WorldMutation.FormGroup or WorldMutation.JoinGroup or WorldMutation.LeaveGroup or WorldMutation.KickMember
            or WorldMutation.OfferOwnership or WorldMutation.SettleOwnership => WorldSection.Groups,
        WorldMutation.SetProperty => WorldSection.Properties,
        WorldMutation.UpsertInteraction or WorldMutation.RemoveInteraction => WorldSection.Interactions,
        WorldMutation.CreateMarketListing or WorldMutation.PlaceMarketBid or WorldMutation.BuyoutMarketListing or WorldMutation.CancelMarketListing or WorldMutation.SettleMarketListing or WorldMutation.PruneMarketListings => WorldSection.Market,
        // No silent fallback: a new mutation kind added without its own arm would otherwise inherit Kits authority. A
        // missing arm throws the first time that kind is mapped — surfaced loudly at runtime rather than mis-authorized.
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(mutation), actualValue: mutation, message: $"no WorldSection arm for mutation kind '{mutation.GetType().Name}' — every kind must map to its authorizing section."),
    };

    // The row-scoped Edit subject a state-row or state-cell mutation names, for the second authority check — null
    // for every other mutation kind (the check above is a no-op then). Both the whole-row upsert/remove AND the
    // per-cell upsert/remove check the SAME Edit/state:<name> subject now — a slot is a table with one key, so there
    // is one row, one subject, never a separate table:<name> narrowing independent of the whole row's own hold.
    private static GrantSubject? RowScopedEditSubjectOf(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertStateRow m => GrantSubject.State(name: m.Row.Name),
        WorldMutation.RemoveStateRow m => GrantSubject.State(name: m.Name),
        WorldMutation.UpsertStateCell m => GrantSubject.State(name: m.Row),
        WorldMutation.RemoveStateCell m => GrantSubject.State(name: m.Row),
        // The row this mutation WRITES — the identical subject an UpsertStateCell into the same row is checked
        // against, which is what makes `verbs:Generate` on an Edit/state:<row> hold the fire-without-redefine
        // separation. Advancing the GENERATOR row's own cursor is engine bookkeeping intrinsic to firing; the
        // interesting authority over a generator is re-authoring it, which is an UpsertStateRow against ITS row.
        WorldMutation.Generate m => GrantSubject.State(name: m.Row),
        _ => null,
    };

    // The dependents a placement-removal guard names: every speaker anchored to the placement (null = none).
    private static string? DescribeSpeakersAnchoredTo(IReadOnlyList<WorldSpeaker> speakers, string placementId) {
        List<string>? names = null;

        foreach (var speaker in speakers) {
            if ((speaker is WorldSpeaker.Anchored { Anchor: WorldAnchor.Placement anchor }) &&
                string.Equals(a: anchor.PlacementId, b: placementId, comparisonType: StringComparison.Ordinal)) {
                (names ??= new List<string>()).Add(item: $"'{speaker.Name}'");
            }
        }

        return ((names is null) ? null : string.Join(separator: ", ", values: names));
    }

    // The dependents a tune/patch-removal guard names among speaker feeds (null = none).
    private static string? DescribeSpeakersSourcing(IReadOnlyList<WorldSpeaker> speakers, Func<WorldSpeakerSource, bool> matches) {
        List<string>? names = null;

        foreach (var speaker in speakers) {
            if ((speaker.Feed?.Source is { } source) && matches(arg: source)) {
                (names ??= new List<string>()).Add(item: $"'{speaker.Name}'");
            }
        }

        return ((names is null) ? null : string.Join(separator: ", ", values: names));
    }

    // Every dependent a patch-removal guard names: synth-sourced speakers plus placement emission facets
    // (creation sounds carry their patches INLINE, so they can never dangle). Null = none.
    private static string? DescribePatchDependents(WorldDefinition current, string patchId) {
        List<string>? dependents = null;

        if (DescribeSpeakersSourcing(speakers: current.Speakers, matches: source => ((source is WorldSpeakerSource.Synth synth) && string.Equals(a: synth.PatchId, b: patchId, comparisonType: StringComparison.Ordinal))) is { } speakers) {
            (dependents ??= new List<string>()).Add(item: $"speaker(s) {speakers}");
        }

        foreach (var placement in current.Placements) {
            if ((placement.Emission is { } emission) && string.Equals(a: emission.PatchId, b: patchId, comparisonType: StringComparison.Ordinal)) {
                (dependents ??= new List<string>()).Add(item: $"placement '{placement.Id}'");
            }
        }

        return ((dependents is null) ? null : string.Join(separator: ", ", values: dependents));
    }

    // A short mutation label for the accept/reject console line — the kind plus its stable-id subject.
    private static string Describe(WorldMutation mutation) => mutation switch {
        WorldMutation.UpsertKit m => $"UpsertKit '{m.Kit.Name}'",
        WorldMutation.RemoveKit m => $"RemoveKit '{m.Name}'",
        WorldMutation.SetDefaultSeatKit m => $"SetDefaultSeatKit '{m.Name}'",
        WorldMutation.SetKitAssignment m => $"SetKitAssignment '{m.Assignment.Sequence.Name}'",
        WorldMutation.UpsertScreen m => $"UpsertScreen {m.Screen.Index}",
        WorldMutation.RemoveScreen m => $"RemoveScreen {m.Index}",
        WorldMutation.UpsertCamera m => $"UpsertCamera '{m.Camera.Name}'",
        WorldMutation.RemoveCamera m => $"RemoveCamera '{m.Name}'",
        WorldMutation.SetSpawns => "SetSpawns",
        WorldMutation.SetMotion => "SetMotion",
        WorldMutation.SetPopulationDefaults => "SetPopulationDefaults",
        WorldMutation.SetRenderDefaults => "SetRenderDefaults",
        WorldMutation.UpsertAddon m => $"UpsertAddon '{m.Addon.Name}'",
        WorldMutation.RemoveAddon m => $"RemoveAddon '{m.Name}'",
        WorldMutation.UpsertBindingOverlay m => $"UpsertBindingOverlay '{m.Overlay.Id}'",
        WorldMutation.RemoveBindingOverlay m => $"RemoveBindingOverlay '{m.Id}'",
        WorldMutation.UpsertCreation m => $"UpsertCreation '{m.Creation.Id}'",
        WorldMutation.RemoveCreation m => $"RemoveCreation '{m.Id}'",
        WorldMutation.UpsertPlacement m => $"UpsertPlacement '{m.Placement.Id}'",
        WorldMutation.RemovePlacement m => $"RemovePlacement '{m.Id}'",
        WorldMutation.SetAuthoringDefaults => "SetAuthoringDefaults",
        WorldMutation.UpsertSpeaker m => $"UpsertSpeaker '{m.Speaker.Name}'",
        WorldMutation.RemoveSpeaker m => $"RemoveSpeaker '{m.Name}'",
        WorldMutation.UpsertTune m => $"UpsertTune '{m.Tune.Id}'",
        WorldMutation.RemoveTune m => $"RemoveTune '{m.Id}'",
        WorldMutation.UpsertPatch m => $"UpsertPatch '{m.Patch.Id}'",
        WorldMutation.RemovePatch m => $"RemovePatch '{m.Id}'",
        WorldMutation.SetAudioDefaults => "SetAudioDefaults",
        WorldMutation.SetCollision => "SetCollision",
        WorldMutation.SetHostDefaults => "SetHostDefaults",
        WorldMutation.SetViewDefaults => "SetViewDefaults",
        WorldMutation.SetPlayerDefaults => "SetPlayerDefaults",
        WorldMutation.UpsertViewLayout m => $"UpsertViewLayout '{m.Layout.Name}'",
        WorldMutation.RemoveViewLayout m => $"RemoveViewLayout '{m.Name}'",
        WorldMutation.UpsertLook m => $"UpsertLook '{m.Look.Name}'",
        WorldMutation.RemoveLook m => $"RemoveLook '{m.Name}'",
        WorldMutation.SetLookAssignment m => $"SetLookAssignment '{m.Assignment.Sequence.Name}'",
        WorldMutation.UpsertScreenLink m => $"UpsertScreenLink '{m.Link.Name}'",
        WorldMutation.RemoveScreenLink m => $"RemoveScreenLink '{m.Name}'",
        WorldMutation.UpsertGrant m => $"UpsertGrant {m.Row.Principal.Describe()} {m.Row.Capability.ToString().ToLowerInvariant()} {m.Row.Subject.Describe()}",
        WorldMutation.RemoveGrant m => $"RemoveGrant {m.Target.Principal.Describe()} {m.Target.Capability.ToString().ToLowerInvariant()} {m.Target.Subject.Describe()}",
        WorldMutation.UpsertHudPanel m => $"UpsertHudPanel '{m.Panel.Id}'",
        WorldMutation.RemoveHudPanel m => $"RemoveHudPanel '{m.Id}'",
        WorldMutation.UpsertHudElement m => $"UpsertHudElement '{m.PanelId}'.'{m.Element.Id}'",
        WorldMutation.RemoveHudElement m => $"RemoveHudElement '{m.PanelId}'.'{m.ElementId}'",
        WorldMutation.SetHudDefaults => "SetHudDefaults",
        WorldMutation.UpsertStateRow m => $"UpsertStateRow '{m.Row.Name}'",
        WorldMutation.RemoveStateRow m => $"RemoveStateRow '{m.Name}'",
        WorldMutation.UpsertStateCell m => $"UpsertStateCell '{m.Row}'.'{m.Key}'",
        WorldMutation.RemoveStateCell m => $"RemoveStateCell '{m.Row}'.'{m.Key}'",
        WorldMutation.SetInputHold => "SetInputHold",
        WorldMutation.Generate m => $"Generate '{m.Row}'",
        WorldMutation.UpsertWorldRule m => $"UpsertWorldRule '{m.Rule.Name}'",
        WorldMutation.RemoveWorldRule m => $"RemoveWorldRule '{m.Name}'",
        WorldMutation.UpsertGroupKind m => $"UpsertGroupKind '{m.Kind.Name}'",
        WorldMutation.RemoveGroupKind m => $"RemoveGroupKind '{m.Name}'",
        WorldMutation.FormGroup m => $"FormGroup '{m.Id}' kind '{m.KindName}'",
        WorldMutation.JoinGroup m => $"JoinGroup '{m.GroupId}' <- {m.Member.Describe()}",
        WorldMutation.LeaveGroup m => $"LeaveGroup '{m.GroupId}' <- {m.Member.Describe()}",
        WorldMutation.KickMember m => $"KickMember '{m.GroupId}' <- {m.Member.Describe()}",
        WorldMutation.OfferOwnership m => $"OfferOwnership '{m.Subject.Describe()}' {m.Principal.Describe()} -> escrow(recipient={m.Recipient.Describe()},deadline={m.DeadlineTick})",
        WorldMutation.SettleOwnership m => (m.Reclaim
            ? $"SettleOwnership '{m.Subject.Describe()}' reclaim by {m.Principal.Describe()}"
            : $"SettleOwnership '{m.Subject.Describe()}' accept by {m.Principal.Describe()}"),
        WorldMutation.SetProperty m => (m.Remove ? $"RemoveProperty '{m.Name}'" : $"UpsertProperty '{m.Name}'"),
        WorldMutation.UpsertInteraction m => $"UpsertInteraction '{m.Interaction.Name}'",
        WorldMutation.RemoveInteraction m => $"RemoveInteraction '{m.Name}'",
        WorldMutation.CreateMarketListing m => $"CreateMarketListing {m.Quantity}x'{m.ItemRow}' seller={m.Seller.Describe()} by {m.Principal.Describe()}",
        WorldMutation.PlaceMarketBid m => $"PlaceMarketBid #{m.ListingId} {m.Amount} bidder={m.Bidder.Describe()} by {m.Principal.Describe()}",
        WorldMutation.BuyoutMarketListing m => $"BuyoutMarketListing #{m.ListingId} buyer={m.Buyer.Describe()} by {m.Principal.Describe()}",
        WorldMutation.CancelMarketListing m => $"CancelMarketListing #{m.ListingId} canceler={m.Canceler.Describe()} by {m.Principal.Describe()}",
        WorldMutation.SettleMarketListing m => $"SettleMarketListing #{m.ListingId}",
        WorldMutation.PruneMarketListings => "PruneMarketListings",
        _ => "unknown",
    };

    // Compose a candidate definition from the current one and a mutation — a with-expression over the coarse section,
    // whole-row upsert addressed by stable id. A remove of a missing id fails here (before validation) with a reason.
    // `tick` is the tick this mutation APPLIES at — the live tick boundary, or a journal entry's own tick during
    // world.undo's replay. The state-cell arm reads it (an Add against an advancing row resolves its target LIVE);
    // OfferOwnership/SettleOwnership read it too (a deadline is checked against the SAME tick the offer/reclaim
    // applies at, never a wall clock — see their own remarks); it is threaded rather than defaulted so a caller can
    // never silently compose against tick zero. `evictedKey` is non-null only when an UpsertStateCell write against an
    // Evicts row dropped its oldest cell to make room — the same pure function every re-composition (live apply,
    // world.undo's journal replay) runs, so the reported victim and the actually-dropped cell can never disagree.
    private static bool TryCompose(WorldDefinition current, WorldMutation mutation, ulong tick, string instanceIdentity, out WorldDefinition candidate, out string reason, out WorldCellName? evictedKey) {
        reason = string.Empty;
        evictedKey = null;

        switch (mutation) {
            case WorldMutation.UpsertKit m:
                candidate = (current with { Kits = Upsert(list: current.Kits, item: m.Kit, keyOf: static kit => kit.Name) });

                return true;
            case WorldMutation.RemoveKit m:
                if (!Remove(list: current.Kits, key: m.Name, keyOf: static kit => kit.Name, result: out var kits)) {
                    candidate = current;
                    reason = $"no kit row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Kits = kits });

                return true;
            case WorldMutation.SetDefaultSeatKit m:
                candidate = (current with { DefaultSeatKit = m.Name });

                return true;
            case WorldMutation.SetKitAssignment m:
                candidate = (current with { Assignment = m.Assignment });

                return true;
            case WorldMutation.UpsertScreen m:
                candidate = (current with { Screens = Upsert(list: current.Screens, item: m.Screen, keyOf: static screen => screen.Index) });

                return true;
            case WorldMutation.RemoveScreen m:
                if (!Remove(list: current.Screens, key: m.Index, keyOf: static screen => screen.Index, result: out var screens)) {
                    candidate = current;
                    reason = $"no screen at index {m.Index}";

                    return false;
                }

                candidate = (current with { Screens = screens });

                return true;
            case WorldMutation.UpsertCamera m:
                candidate = (current with { Cameras = Upsert(list: current.Cameras, item: m.Camera, keyOf: static camera => camera.Name) });

                return true;
            case WorldMutation.RemoveCamera m:
                if (!Remove(list: current.Cameras, key: m.Name, keyOf: static camera => camera.Name, result: out var cameras)) {
                    candidate = current;
                    reason = $"no camera named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Cameras = cameras });

                return true;
            case WorldMutation.SetSpawns m:
                candidate = (current with { SpawnPoints = m.Spawns });

                return true;
            case WorldMutation.SetMotion m:
                candidate = (current with { Motion = m.Motion });

                return true;
            case WorldMutation.SetPopulationDefaults m:
                candidate = (current with { Population = m.Population });

                return true;
            case WorldMutation.SetRenderDefaults m:
                candidate = (current with { Render = m.Render });

                return true;
            case WorldMutation.UpsertAddon m:
                candidate = (current with { Addons = Upsert(list: current.Addons, item: m.Addon, keyOf: static addon => addon.Name) });

                return true;
            case WorldMutation.RemoveAddon m:
                if (!Remove(list: current.Addons, key: m.Name, keyOf: static addon => addon.Name, result: out var addons)) {
                    candidate = current;
                    reason = $"no addon named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Addons = addons });

                return true;
            case WorldMutation.UpsertCreation m: {
                if (!TryCanonicalizeDocument(
                    document: m.Creation.Document,
                    id: m.Creation.Id,
                    hash: m.Creation.Hash,
                    kind: "creation",
                    canonicalize: static (document, source) => Puck.Forge.Authoring.CreationCanonicalizer.Canonicalize(document: document, source: source),
                    canonicalDocument: out var canonicalDocument,
                    reason: out reason)) {
                    candidate = current;

                    return false;
                }

                candidate = (current with { Creations = Upsert(list: current.Creations, item: (m.Creation with { Document = canonicalDocument }), keyOf: static creation => creation.Id) });

                return true;
            }
            case WorldMutation.RemoveCreation m: {
                // The conservative no-cascade ruling: a creation with live placements rejects loudly rather than
                // silently unstamping the world (remove the placements first; undo replay stays order-honest).
                var referencing = 0;

                foreach (var placement in current.Placements) {
                    if (string.Equals(a: placement.CreationId, b: m.Id, comparisonType: StringComparison.Ordinal)) {
                        referencing++;
                    }
                }

                if (referencing > 0) {
                    candidate = current;
                    reason = $"creation '{m.Id}' has {referencing} live placement(s) — remove them first";

                    return false;
                }

                if (!Remove(list: current.Creations, key: m.Id, keyOf: static creation => creation.Id, result: out var creations)) {
                    candidate = current;
                    reason = $"no creation with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Creations = creations });

                return true;
            }
            case WorldMutation.UpsertPlacement m:
                candidate = (current with { Placements = Upsert(list: current.Placements, item: m.Placement, keyOf: static placement => placement.Id) });

                return true;
            case WorldMutation.RemovePlacement m: {
                // The no-cascade guard: a placement a speaker anchors to rejects loudly naming the dependents, never
                // silently unanchoring the speaker (full-document revalidation would also catch the dangling anchor,
                // but the guard names WHO depends rather than echoing a validator path).
                if (DescribeSpeakersAnchoredTo(speakers: current.Speakers, placementId: m.Id) is { } anchored) {
                    candidate = current;
                    reason = $"placement '{m.Id}' anchors speaker(s) {anchored} — remove or re-anchor them first";

                    return false;
                }

                if (!Remove(list: current.Placements, key: m.Id, keyOf: static placement => placement.Id, result: out var placements)) {
                    candidate = current;
                    reason = $"no placement with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Placements = placements });

                return true;
            }
            case WorldMutation.UpsertSpeaker m:
                candidate = (current with { Speakers = Upsert(list: current.Speakers, item: m.Speaker, keyOf: static speaker => speaker.Name) });

                return true;
            case WorldMutation.RemoveSpeaker m:
                if (!Remove(list: current.Speakers, key: m.Name, keyOf: static speaker => speaker.Name, result: out var speakers)) {
                    candidate = current;
                    reason = $"no speaker named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Speakers = speakers });

                return true;
            case WorldMutation.UpsertTune m: {
                if (!TryCanonicalizeDocument(
                    document: m.Tune.Document,
                    id: m.Tune.Id,
                    hash: m.Tune.Hash,
                    kind: "tune",
                    canonicalize: static (document, source) => Puck.Forge.Authoring.AudioCanonicalizer.Canonicalize(document: document, source: source),
                    canonicalDocument: out var canonicalDocument,
                    reason: out reason)) {
                    candidate = current;

                    return false;
                }

                candidate = (current with { Tunes = Upsert(list: current.Tunes, item: (m.Tune with { Document = canonicalDocument }), keyOf: static tune => tune.Id) });

                return true;
            }
            case WorldMutation.RemoveTune m: {
                if (DescribeSpeakersSourcing(speakers: current.Speakers, matches: source => ((source is WorldSpeakerSource.Tune tune) && string.Equals(a: tune.TuneId, b: m.Id, comparisonType: StringComparison.Ordinal))) is { } dependents) {
                    candidate = current;
                    reason = $"tune '{m.Id}' feeds speaker(s) {dependents} — remove or re-source them first";

                    return false;
                }

                if (!Remove(list: current.Tunes, key: m.Id, keyOf: static tune => tune.Id, result: out var tunes)) {
                    candidate = current;
                    reason = $"no tune with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Tunes = tunes });

                return true;
            }
            case WorldMutation.UpsertPatch m: {
                if (!TryCanonicalizeDocument(
                    document: m.Patch.Document,
                    id: m.Patch.Id,
                    hash: m.Patch.Hash,
                    kind: "patch",
                    canonicalize: static (document, source) => Puck.Forge.Authoring.SynthPatchCanonicalizer.Canonicalize(document: document, source: source),
                    canonicalDocument: out var canonicalDocument,
                    reason: out reason)) {
                    candidate = current;

                    return false;
                }

                candidate = (current with { Patches = Upsert(list: current.Patches, item: (m.Patch with { Document = canonicalDocument }), keyOf: static patch => patch.Id) });

                return true;
            }
            case WorldMutation.RemovePatch m: {
                if (DescribePatchDependents(current: current, patchId: m.Id) is { } dependents) {
                    candidate = current;
                    reason = $"patch '{m.Id}' is referenced by {dependents} — remove or re-source them first";

                    return false;
                }

                if (!Remove(list: current.Patches, key: m.Id, keyOf: static patch => patch.Id, result: out var patches)) {
                    candidate = current;
                    reason = $"no patch with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Patches = patches });

                return true;
            }
            case WorldMutation.SetAudioDefaults m:
                candidate = (current with { Audio = m.Audio });

                return true;
            case WorldMutation.UpsertBindingOverlay m:
                candidate = (current with { BindingOverlays = Upsert(list: current.BindingOverlays, item: m.Overlay, keyOf: static overlay => overlay.Id) });

                return true;
            case WorldMutation.SetAuthoringDefaults m:
                candidate = (current with { Authoring = m.Authoring });

                return true;
            case WorldMutation.SetCollision m:
                candidate = (current with { Collision = m.Collision });

                return true;
            case WorldMutation.SetHostDefaults m:
                candidate = (current with { Host = m.Host });

                return true;
            case WorldMutation.SetViewDefaults m:
                candidate = (current with { Views = m.Views });

                return true;
            case WorldMutation.SetPlayerDefaults m:
                candidate = (current with { PlayerDefaults = m.Defaults });

                return true;
            case WorldMutation.UpsertViewLayout m: {
                var views = current.Views;

                candidate = (current with { Views = (views with { Layouts = Upsert(list: views.Layouts, item: m.Layout, keyOf: static layout => layout.Name) }) });

                return true;
            }
            case WorldMutation.RemoveViewLayout m: {
                var views = current.Views;

                if (!Remove(list: views.Layouts, key: m.Name, keyOf: static layout => layout.Name, result: out var layouts)) {
                    candidate = current;
                    reason = $"no view layout named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Views = (views with { Layouts = layouts }) });

                return true;
            }
            case WorldMutation.RemoveBindingOverlay m:
                if (!Remove(list: current.BindingOverlays, key: m.Id, keyOf: static overlay => overlay.Id, result: out var overlays)) {
                    candidate = current;
                    reason = $"no binding overlay with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { BindingOverlays = overlays });

                return true;
            case WorldMutation.UpsertLook m:
                candidate = (current with { Looks = Upsert(list: current.Looks, item: m.Look, keyOf: static look => look.Name) });

                return true;
            case WorldMutation.RemoveLook m:
                if (!Remove(list: current.Looks, key: m.Name, keyOf: static look => look.Name, result: out var looks)) {
                    candidate = current;
                    reason = $"no look row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Looks = looks });

                return true;
            case WorldMutation.SetLookAssignment m:
                candidate = (current with { LookAssignment = m.Assignment });

                return true;
            case WorldMutation.UpsertScreenLink m:
                candidate = (current with { Links = Upsert(list: current.Links, item: m.Link, keyOf: static link => link.Name) });

                return true;
            case WorldMutation.RemoveScreenLink m:
                if (!Remove(list: current.Links, key: m.Name, keyOf: static link => link.Name, result: out var links)) {
                    candidate = current;
                    reason = $"no cable link named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Links = links });

                return true;
            case WorldMutation.UpsertGrant m:
                candidate = (current with { Grants = Upsert(list: current.Grants, item: m.Row, keyOf: static grant => (grant.Principal, grant.Capability, grant.Subject)) });

                return true;
            case WorldMutation.RemoveGrant m:
                if (!Remove(list: current.Grants, key: (m.Target.Principal, m.Target.Capability, m.Target.Subject), keyOf: static grant => (grant.Principal, grant.Capability, grant.Subject), result: out var grants)) {
                    candidate = current;
                    reason = $"no grant row for {m.Target.Principal.Describe()} {m.Target.Capability.ToString().ToLowerInvariant()} {m.Target.Subject.Describe()}";

                    return false;
                }

                candidate = (current with { Grants = grants });

                return true;
            case WorldMutation.UpsertHudPanel m:
                candidate = (current with { Hud = (current.Hud with { Panels = Upsert(list: current.Hud.Panels, item: m.Panel, keyOf: static panel => panel.Id) }) });

                return true;
            case WorldMutation.RemoveHudPanel m:
                if (!Remove(list: current.Hud.Panels, key: m.Id, keyOf: static panel => panel.Id, result: out var hudPanels)) {
                    candidate = current;
                    reason = $"no hud panel with id '{m.Id}'";

                    return false;
                }

                candidate = (current with { Hud = (current.Hud with { Panels = hudPanels }) });

                return true;
            case WorldMutation.UpsertHudElement m: {
                if (FindHudPanel(panels: current.Hud.Panels, id: m.PanelId) is not { } panel) {
                    candidate = current;
                    reason = $"no hud panel with id '{m.PanelId}'";

                    return false;
                }

                var updatedPanel = (panel with { Elements = Upsert(list: panel.Elements, item: m.Element, keyOf: static element => element.Id) });

                candidate = (current with { Hud = (current.Hud with { Panels = Upsert(list: current.Hud.Panels, item: updatedPanel, keyOf: static p => p.Id) }) });

                return true;
            }
            case WorldMutation.RemoveHudElement m: {
                if (FindHudPanel(panels: current.Hud.Panels, id: m.PanelId) is not { } panel) {
                    candidate = current;
                    reason = $"no hud panel with id '{m.PanelId}'";

                    return false;
                }

                if (!Remove(list: panel.Elements, key: m.ElementId, keyOf: static element => element.Id, result: out var elements)) {
                    candidate = current;
                    reason = $"no hud element with id '{m.ElementId}' in panel '{m.PanelId}'";

                    return false;
                }

                var updatedPanel = (panel with { Elements = elements });

                candidate = (current with { Hud = (current.Hud with { Panels = Upsert(list: current.Hud.Panels, item: updatedPanel, keyOf: static p => p.Id) }) });

                return true;
            }
            case WorldMutation.SetHudDefaults m:
                candidate = (current with { Hud = (current.Hud with { Defaults = m.Defaults }) });

                return true;
            case WorldMutation.UpsertStateRow m:
                candidate = (current with { State = Upsert(list: current.State, item: m.Row, keyOf: static row => row.Name) });

                return true;
            case WorldMutation.RemoveStateRow m:
                if (!Remove(list: current.State, key: m.Name, keyOf: static row => row.Name, result: out var stateRows)) {
                    candidate = current;
                    reason = $"no state row named '{m.Name}'";

                    return false;
                }

                candidate = (current with { State = stateRows });

                return true;
            case WorldMutation.UpsertStateCell m: {
                // The ONE door: every row-existence and row-KIND decision this write depends on is asked here, against
                // the CANDIDATE this batch has built so far — never at the console verb, which cannot know whether a
                // same-batch UpsertStateRow ahead of this one has already declared (or redeclared the kind of) the row
                // it names.
                if (WorldDefinitionRows.FindStateRow(rows: current.State, name: m.Row) is not { } row) {
                    candidate = current;
                    reason = $"no state row named '{m.Row}' — declare it first with world.row.set state <json>";

                    return false;
                }

                if (!WorldCellName.TryParse(candidate: m.Key, name: out var cellKey, reason: out var keyReason)) {
                    candidate = current;
                    reason = $"state row '{m.Row}' cell key '{m.Key}' {keyReason}";

                    return false;
                }

                // Whether THIS write is a text write is a fact of the WRITE, not the row — a text write always
                // carries a non-null Text (even ""), a numeric one never does. Asking it this way
                // (rather than switching on row.Kind) is what lets a kind-mismatched write refuse BY NAME instead of
                // silently composing against the wrong field: a numeric write against a text row would
                // otherwise fall into this arm with Text null and overwrite the cell with an empty string.
                var isTextWrite = (m.Text is not null);

                // A TEXT row's cell carries a literal string, never a numeric operand: world.state.cell.set's text
                // arm is this shape's ONE ingress, always submitting Kind=Set, so the Add/advance machinery below never applies.
                // The whole upsert-or-append-plus-eviction composition (including the reserved-key rule — a text row
                // is never a generator, so its only legitimate reserved key is the slot cell) delegates to
                // WorldStateCellWriter — the SHARED pure function an owned-identity document write (which has no
                // ordered mutation domain of its own) also runs, so the two can never disagree about a victim or a
                // reserved-cell refusal. TryComposeTextCell itself refuses BY NAME when row.Kind is not Text, which is
                // this arm's ONE check for "a text operand against a numeric/bool row".
                if (isTextWrite) {
                    if (!WorldStateCellWriter.TryComposeTextCell(row: row, key: cellKey, text: m.Text!, cells: out var textCells, evictedKey: out evictedKey, reason: out var composeTextReason)) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell '{m.Key}' {composeTextReason}";

                        return false;
                    }

                    candidate = (current with { State = Upsert(list: current.State, item: (row with { Cells = textCells }), keyOf: static row => row.Name) });

                    return true;
                }

                // The reverse kind mismatch: a numeric operand against a Text-kind row. This, and the bool+add
                // refusal below, are the two kind-dependent REFUSALS the console verb used to ask before submitting —
                // moved here so they see the same candidate row the existence check above just resolved, rather than
                // whatever the live definition happened to hold at text-submit time.
                if (row.Kind == CellKind.Text) {
                    candidate = current;
                    reason = $"state row '{m.Row}' cell '{m.Key}' is text-kind and takes a text operand, never a numeric one";

                    return false;
                }

                if ((m.Kind == WorldDocumentWriteKind.Add) && (row.Kind == CellKind.Bool)) {
                    candidate = current;
                    reason = $"state row '{m.Row}' cell '{m.Key}' — 'add' is refused on a bool-kind row";

                    return false;
                }

                // The honest encoding for a payload whose SHAPE depends on the row's kind: a console write carries
                // the un-interpreted wire token (RawToken) because it cannot know Fixed-vs-Int-vs-Bool before this
                // row's kind resolves against the candidate; a caller that already knows the kind (the rule-effect
                // engine, which reads the destination row itself before submitting) carries the resolved Value
                // directly. See WorldMutation.UpsertStateCell.RawToken's remarks.
                long operand;

                if (m.RawToken is { } rawToken) {
                    if (!WorldStateCellWriter.TryParseNumericToken(kind: row.Kind, token: rawToken, value: out operand, reason: out var tokenReason)) {
                        candidate = current;
                        reason = $"state row '{m.Row}' cell '{m.Key}' {tokenReason}";

                        return false;
                    }
                } else {
                    operand = m.Value;
                }

                // The Add operand comes from WorldStateReader — the SAME read every gate, binding and read-back runs —
                // rather than from the stored cell. On an ORDINARY row the two are the same value, so this arm keeps
                // the read-modify-write-onto-the-base behaviour it always had. On an ADVANCING row they differ, and
                // the live value is the right operand: the stored cell there is a BASE the row has been accumulating
                // away from, so adding to it would silently discard every unit gained since the epoch (a regen row
                // sitting at a live 41 taking a -10 would land on -10, not 31). Add means "add to what a reader
                // sees"; RebaseAdvanceEpoch then makes that sum the new base and starts the accumulation again from
                // this tick, so the row keeps advancing from the value the author just composed.
                _ = WorldStateReader.TryRead(definition: current, rowName: m.Row, key: m.Key, tick: tick, row: out _, rawValue: out var addend, text: out _);

                long value;

                try {
                    value = ((m.Kind == WorldDocumentWriteKind.Add) ? checked((addend ?? 0L) + operand) : operand);
                } catch (OverflowException) {
                    candidate = current;
                    reason = $"state row '{m.Row}' cell '{m.Key}' overflowed";

                    return false;
                }

                // The engine-minted-cell rule, asked at the VERB so the operator reads why the cell they just typed
                // was refused rather than a whole-document validation error. Same code, not a second reading: the
                // document walk (boot, every mutation, every undo-replay entry) calls the identical
                // WorldStateReservedCells rule, so the two can never disagree about which reserved keys a row mints.
                if (!WorldStateReservedCells.TryValidateReservedCell(row: row, key: cellKey, reason: out var reservedReason)) {
                    candidate = current;
                    reason = $"state row '{m.Row}' cell '{m.Key}' {reservedReason}";

                    return false;
                }

                // UpsertStateCell carries only a scalar VALUE — a cell's own advance RATE is authored only through a
                // whole-row UpsertStateRow — so a base-value write here preserves whatever the existing cell already
                // declared rather than silently deleting it; RebaseAdvanceEpoch (below TryCompose) then re-bases its
                // epoch to this tick, exactly as it already does for a row-level advance's slot cell.
                var existingAdvance = FindCellAdvance(cells: (row.Cells ?? []), key: cellKey);
                var isNewKey = !WorldStateCellWriter.ContainsKey(cells: (row.Cells ?? []), key: cellKey);
                var cells = Upsert(list: (row.Cells ?? []), item: new WorldStateCell(Key: cellKey, Value: value, Advance: existingAdvance), keyOf: static (WorldStateCell cell) => cell.Key);

                cells = WorldStateCellWriter.ApplyEviction(row: row, cells: cells, addedNewKey: isNewKey, evictedKey: out evictedKey);
                candidate = (current with { State = Upsert(list: current.State, item: (row with { Cells = cells }), keyOf: static row => row.Name) });

                return true;
            }
            case WorldMutation.RemoveStateCell m: {
                if (WorldDefinitionRows.FindStateRow(rows: current.State, name: m.Row) is not { } row) {
                    candidate = current;
                    reason = $"no state row named '{m.Row}'";

                    return false;
                }

                if (!Remove(list: (row.Cells ?? []), key: m.Key, keyOf: static (WorldStateCell cell) => cell.Key, result: out var cells)) {
                    candidate = current;
                    reason = $"state row '{m.Row}' has no cell keyed '{m.Key}'";

                    return false;
                }

                candidate = (current with { State = Upsert(list: current.State, item: (row with { Cells = cells }), keyOf: static row => row.Name) });

                return true;
            }
            case WorldMutation.SetInputHold m:
                // The mutation's own wire shape is the COMPILED (ticks) form — the addon-mutation ABI's raw-ticks
                // contract, unchanged — but InputHold itself stores the AUTHORED (seconds) shape (see its remarks), so
                // decompile through the candidate's OWN rate before storing. Exact for a row-set verb's compiled
                // seconds (round-trips through the SAME rate it compiled from); the addon ABI's raw ticks are the one
                // narrow exception WorldInputHoldSettings.ToAuthoring's remarks already accept.
                //
                // THE UNIT-GAP REFUSAL (rate-0 self-lock follow-on): a tick-denominated write has no meaning in a
                // world whose simulation.rateHz is the durable stop — there is no tick↔seconds mapping to decompile
                // through, and dividing by the rate would produce Infinity/NaN that later throws unguarded out of
                // Serialize on save/sync/record. Now that the administrative drain applies buffered mutations even
                // while an instance never steps, this path is reachable, not hypothetical, so it is refused HERE, by
                // name, at the apply door — the legible verdict in front of the structural backstop
                // WorldInputHoldSettings.ToAuthoring's own division-by-rate is separately being hardened to refuse
                // rather than divide; this refusal does not rely on catching that exception.
                if (current.SimulationRateHz <= 0) {
                    candidate = current;
                    reason = $"'{nameof(WorldMutation.SetInputHold)}' carries raw engine ticks, which have no seconds mapping in a world whose simulation.rateHz is 0 (the document's own durable stop) — author input-hold seconds directly, or write this while the world's rate is nonzero";

                    return false;
                }

                candidate = (current with { InputHold = m.Settings.ToAuthoring(ratePerSecond: (uint)current.SimulationRateHz) });

                return true;
            case WorldMutation.Generate m:
                return TryComposeGenerate(current: current, mutation: m, instanceIdentity: instanceIdentity, candidate: out candidate, reason: out reason);
            case WorldMutation.UpsertWorldRule m:
                candidate = (current with { Rules = Upsert(list: (current.Rules ?? []), item: m.Rule, keyOf: static (WorldRule rule) => rule.Name) });

                return true;
            case WorldMutation.RemoveWorldRule m:
                if (!Remove(list: (current.Rules ?? []), key: m.Name, keyOf: static (WorldRule rule) => rule.Name, result: out var rules)) {
                    candidate = current;
                    reason = $"no rule named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Rules = rules });

                return true;
            case WorldMutation.UpsertGroupKind m: {
                var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                candidate = (current with { Groups = (groupsSection with { Kinds = Upsert(list: groupsSection.Kinds, item: m.Kind, keyOf: static (WorldGroupKind kind) => kind.Name) }) });

                return true;
            }
            case WorldMutation.RemoveGroupKind m: {
                var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);
                var referencing = 0;

                foreach (var row in groupsSection.Groups) {
                    if (string.Equals(a: row.KindName, b: m.Name, comparisonType: StringComparison.Ordinal)) {
                        referencing++;
                    }
                }

                if (referencing > 0) {
                    candidate = current;
                    reason = $"group kind '{m.Name}' has {referencing} live group row(s) — remove or re-kind them first";

                    return false;
                }

                if (!Remove(list: groupsSection.Kinds, key: m.Name, keyOf: static (WorldGroupKind kind) => kind.Name, result: out var kinds)) {
                    candidate = current;
                    reason = $"no group kind named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Groups = (groupsSection with { Kinds = kinds }) });

                return true;
            }
            case WorldMutation.FormGroup m: {
                var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                // The earliest door a LIVE-minted group id crosses (WorldSafeName's own doctrine — see
                // WorldGroup.Id's remarks): a document-authored id already crossed this door at JSON parse, but
                // FormGroup mints one at RUNTIME, so this mutation's own apply site IS that door for it. Refused by
                // name rather than let an unsafe id reach WorldGroup.Id, which the id-to-instance-name composition
                // (WorldSessionResolver.MintInstanceName) depends on staying safe for every group id, live-formed or
                // authored alike.
                if (!WorldSafeName.TryParse(candidate: m.Id, name: out var safeId, reason: out var idReason)) {
                    candidate = current;
                    reason = $"group id '{m.Id}' is not a safe name — {idReason}";

                    return false;
                }

                if (FindGroupRow(groups: groupsSection.Groups, id: m.Id) is not null) {
                    candidate = current;
                    reason = $"group '{m.Id}' already exists";

                    return false;
                }

                if (FindGroupKind(kinds: groupsSection.Kinds, name: m.KindName) is null) {
                    candidate = current;
                    reason = $"no declared group kind named '{m.KindName}'";

                    return false;
                }

                candidate = (current with { Groups = (groupsSection with { Groups = Upsert(list: groupsSection.Groups, item: new WorldGroup(Id: safeId, KindName: m.KindName, Members: []), keyOf: static (WorldGroup row) => row.Id) }) });

                return true;
            }
            case WorldMutation.JoinGroup m: {
                var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                if (FindGroupRow(groups: groupsSection.Groups, id: m.GroupId) is not { } group) {
                    candidate = current;
                    reason = $"no group named '{m.GroupId}'";

                    return false;
                }

                if (ContainsMember(members: group.Members, member: m.Member)) {
                    candidate = current;
                    reason = $"{m.Member.Describe()} already belongs to group '{m.GroupId}'";

                    return false;
                }

                var joined = new List<WorldPrincipal>(collection: group.Members) { m.Member };

                candidate = (current with { Groups = (groupsSection with { Groups = Upsert(list: groupsSection.Groups, item: (group with { Members = joined }), keyOf: static (WorldGroup row) => row.Id) }) });

                return true;
            }
            case WorldMutation.LeaveGroup m: {
                var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                if (FindGroupRow(groups: groupsSection.Groups, id: m.GroupId) is not { } group) {
                    candidate = current;
                    reason = $"no group named '{m.GroupId}'";

                    return false;
                }

                if (!ContainsMember(members: group.Members, member: m.Member)) {
                    candidate = current;
                    reason = $"{m.Member.Describe()} does not belong to group '{m.GroupId}'";

                    return false;
                }

                var kind = FindGroupKind(kinds: groupsSection.Kinds, name: group.KindName);

                candidate = (current with { Groups = (groupsSection with { Groups = RemoveMemberAndMaybeDissolve(groups: groupsSection.Groups, group: group, kind: kind, member: m.Member) }) });

                return true;
            }
            case WorldMutation.KickMember m: {
                var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                if (FindGroupRow(groups: groupsSection.Groups, id: m.GroupId) is not { } group) {
                    candidate = current;
                    reason = $"no group named '{m.GroupId}'";

                    return false;
                }

                if (!ContainsMember(members: group.Members, member: m.Member)) {
                    candidate = current;
                    reason = $"{m.Member.Describe()} does not belong to group '{m.GroupId}'";

                    return false;
                }

                var kind = FindGroupKind(kinds: groupsSection.Kinds, name: group.KindName);

                if (kind?.EvictionPolicy == WorldGroupEvictionPolicy.Disband) {
                    _ = Remove(list: groupsSection.Groups, key: m.GroupId, keyOf: static (WorldGroup row) => row.Id, result: out var disbanded);

                    candidate = (current with { Groups = (groupsSection with { Groups = disbanded }) });

                    return true;
                }

                candidate = (current with { Groups = (groupsSection with { Groups = RemoveMemberAndMaybeDissolve(groups: groupsSection.Groups, group: group, kind: kind, member: m.Member) }) });

                return true;
            }
            // ESCROW/TRANSFER — the refusal obligation this pair upholds, stated verbatim: no sequence of
            // accepted/refused submissions may leave the same item owned by two principals or by none (escrow counts
            // as one). Every arm below only ever REPLACES one WorldOwnership row's whole Owner with a single,
            // fully-populated OwnershipOwner value — never a partial write — and the ordinary compose->validate->swap
            // pipeline revalidates the WHOLE candidate after EVERY one of these mutations, not just at the end of a
            // trade, so the structural half of the invariant (exactly one owner variant populated) holds at every
            // intermediate state, not only the final one.
            case WorldMutation.OfferOwnership m: {
                var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                if (FindOwnershipRow(ownership: groupsSection.Ownership, subject: m.Subject) is not { } row) {
                    candidate = current;
                    reason = $"no ownership row for subject '{m.Subject.Describe()}'";

                    return false;
                }

                if ((row.Owner.Kind != OwnershipOwnerKind.Principal) || (row.Owner.Principal != m.Principal)) {
                    candidate = current;
                    reason = $"'{m.Subject.Describe()}' is not owned by {m.Principal.Describe()} (owner.kind={row.Owner.Kind}) — only the current owner may offer it, and only a Principal-owned subject may be offered directly";

                    return false;
                }

                if (m.Recipient == m.Principal) {
                    candidate = current;
                    reason = "cannot offer a subject to oneself — that is not a trade";

                    return false;
                }

                if (m.DeadlineTick <= unchecked((long)tick)) {
                    candidate = current;
                    reason = $"deadlineTick {m.DeadlineTick} does not lie strictly after tick {tick} — an offer needs a real acceptance window";

                    return false;
                }

                var escrowed = (row with { Owner = new OwnershipOwner(Kind: OwnershipOwnerKind.Escrow, Escrow: new OwnershipEscrow(Offerer: m.Principal, Recipient: m.Recipient, DeadlineTick: m.DeadlineTick)) });

                candidate = (current with { Groups = (groupsSection with { Ownership = ReplaceOwnership(ownership: groupsSection.Ownership, row: escrowed) }) });

                return true;
            }
            case WorldMutation.SettleOwnership m: {
                var groupsSection = (current.Groups ?? WorldGroupsSection.Empty);

                if (FindOwnershipRow(ownership: groupsSection.Ownership, subject: m.Subject) is not { } row) {
                    candidate = current;
                    reason = $"no ownership row for subject '{m.Subject.Describe()}'";

                    return false;
                }

                if ((row.Owner.Kind != OwnershipOwnerKind.Escrow) || (row.Owner.Escrow is not { } escrow)) {
                    // The structural guard against the naive "flip the owner field directly" two-submission race: a
                    // settle can ONLY resolve a subject that is ALREADY in escrow — there is no arm anywhere in this
                    // catalog that moves a subject straight from one principal to another, so at most one of a racing
                    // accept/reclaim pair (drained in submission order at the same tick boundary) can ever find the
                    // row still escrowed; the other finds it already resolved and refuses here, never double-applies.
                    candidate = current;
                    reason = $"'{m.Subject.Describe()}' is not currently in escrow (owner.kind={row.Owner.Kind}) — settle only resolves an OfferOwnership, it never transfers directly";

                    return false;
                }

                WorldOwnership settled;

                if (m.Reclaim) {
                    // Manual reclaim is the offerer's own remedy; WorldPrincipal.World is the engine's automatic
                    // sweep (ReclaimExpiredEscrows) firing the identical mutation once the deadline passes with no
                    // accept, so recovery needs no operator action. Both paths are gated on the SAME deadline check
                    // below — the sweep does not jump the queue, it just never forgets to ask.
                    if ((m.Principal != escrow.Offerer) && (m.Principal != WorldPrincipal.World)) {
                        candidate = current;
                        reason = $"only the offerer {escrow.Offerer.Describe()} (or the engine's own timeout sweep) may reclaim '{m.Subject.Describe()}'";

                        return false;
                    }

                    if (unchecked((long)tick) < escrow.DeadlineTick) {
                        candidate = current;
                        reason = $"'{m.Subject.Describe()}' is not yet reclaimable — tick {tick} has not reached its deadline {escrow.DeadlineTick}";

                        return false;
                    }

                    settled = (row with { Owner = new OwnershipOwner(Kind: OwnershipOwnerKind.Principal, Principal: escrow.Offerer) });
                } else {
                    if (m.Principal != escrow.Recipient) {
                        candidate = current;
                        reason = $"'{m.Subject.Describe()}' names recipient {escrow.Recipient.Describe()}, not the acting principal {m.Principal.Describe()}";

                        return false;
                    }

                    settled = (row with { Owner = new OwnershipOwner(Kind: OwnershipOwnerKind.Principal, Principal: escrow.Recipient) });
                }

                candidate = (current with { Groups = (groupsSection with { Ownership = ReplaceOwnership(ownership: groupsSection.Ownership, row: settled) }) });

                return true;
            }
            // ONE kind, two shapes (Remove) — see SetProperty's own remarks for why the pair is consolidated onto a
            // single ordinal.
            case WorldMutation.SetProperty m: {
                var propertiesSection = (current.Properties ?? WorldPropertyRegistrySection.Empty);

                if (!m.Remove) {
                    candidate = (current with { Properties = (propertiesSection with { Names = Upsert(list: propertiesSection.Names, item: m.Name, keyOf: static (string name) => name) }) });

                    return true;
                }

                if (!propertiesSection.Names.Contains(value: m.Name)) {
                    candidate = current;
                    reason = $"no property named '{m.Name}'";

                    return false;
                }

                var referencing = 0;

                foreach (var interaction in (current.Interactions?.Interactions ?? [])) {
                    if (string.Equals(a: interaction.Left, b: m.Name, comparisonType: StringComparison.Ordinal)
                        || ((interaction.CoOccurrence == WorldInteractionCoOccurrence.Distance) && string.Equals(a: interaction.Right, b: m.Name, comparisonType: StringComparison.Ordinal))) {
                        referencing++;
                    }
                }

                if (referencing > 0) {
                    candidate = current;
                    reason = $"property '{m.Name}' has {referencing} live interaction row(s) referencing it — remove or re-target them first";

                    return false;
                }

                _ = Remove(list: propertiesSection.Names, key: m.Name, keyOf: static (string name) => name, result: out var names);

                candidate = (current with { Properties = (propertiesSection with { Names = names }) });

                return true;
            }
            case WorldMutation.UpsertInteraction m: {
                var interactionsSection = (current.Interactions ?? WorldInteractionsSection.Empty);

                candidate = (current with { Interactions = (interactionsSection with { Interactions = Upsert(list: interactionsSection.Interactions, item: m.Interaction, keyOf: static (WorldInteraction row) => row.Name) }) });

                return true;
            }
            case WorldMutation.RemoveInteraction m: {
                var interactionsSection = (current.Interactions ?? WorldInteractionsSection.Empty);

                if (!Remove(list: interactionsSection.Interactions, key: m.Name, keyOf: static (WorldInteraction row) => row.Name, result: out var interactions)) {
                    candidate = current;
                    reason = $"no interaction named '{m.Name}'";

                    return false;
                }

                candidate = (current with { Interactions = (interactionsSection with { Interactions = interactions }) });

                return true;
            }
            case WorldMutation.CreateMarketListing m:
                return TryComposeCreateMarketListing(current: current, mutation: m, tick: tick, candidate: out candidate, reason: out reason);
            case WorldMutation.PlaceMarketBid m:
                return TryComposePlaceMarketBid(current: current, mutation: m, tick: tick, candidate: out candidate, reason: out reason);
            case WorldMutation.BuyoutMarketListing m:
                return TryComposeBuyoutMarketListing(current: current, mutation: m, tick: tick, candidate: out candidate, reason: out reason);
            case WorldMutation.CancelMarketListing m:
                return TryComposeCancelMarketListing(current: current, mutation: m, tick: tick, candidate: out candidate, reason: out reason);
            case WorldMutation.SettleMarketListing m:
                return TryComposeSettleMarketListing(current: current, mutation: m, tick: tick, candidate: out candidate, reason: out reason);
            case WorldMutation.PruneMarketListings m:
                return TryComposePruneMarketListings(current: current, mutation: m, tick: tick, candidate: out candidate, reason: out reason);
            default:
                candidate = current;
                reason = "unknown mutation kind";

                return false;
        }
    }

    // The item/currency fact vocabulary's cell key for a market participant. A seat's index is its own stable
    // identity (generation is always 0), so its key is the plain 0-based entity index — the same addressing
    // WorldRuleFacts.ArgMaxPrefix/ArgMinPrefix already read off an unkeyed row. A peer's index is not stable on its
    // own: WorldPopulationLimits recycles a vacated population slot for a later, unrelated connection, and
    // WorldGrants/the ownership escrow substrate both key a peer's real authority on the full (index, generation)
    // pair (WorldPrincipal's own equality) — so a market cell keys the same pair, or a later occupant of the same
    // slot would silently inherit the departed peer's balance/items/listing proceeds. The compound key never
    // collides with a seat's plain-integer key (it always carries a reserved '_' the kernel would otherwise refuse
    // in an authored key) and reads as a non-candidate to ArgExtremum's int.TryParse scan, exactly like any other
    // non-numeric key already does. Only a real player (seat or peer) may hold a market fact; console/world/addon/
    // document/group principals refuse here rather than minting a cell no player could ever read back.
    private static bool TryPlayerCellKey(WorldPrincipal principal, out string key) {
        switch (principal.Kind) {
            case PrincipalKind.Seat:
                key = principal.Index.ToString(CultureInfo.InvariantCulture);

                return true;
            case PrincipalKind.Peer:
                key = $"{principal.Index.ToString(CultureInfo.InvariantCulture)}_{principal.Generation.ToString(CultureInfo.InvariantCulture)}";

                return true;
            default:
                key = string.Empty;

                return false;
        }
    }

    // The trade-party authority split every market mutation checks beneath the coarse Mutate/section:market hold:
    // the acting principal may name itself as the trade party (a real connected client acting for itself) or,
    // narrowly, Console may name any seat/peer — the split stdin's own Console-stamped, seat-naming submissions rely
    // on. A seat's own boot-seeded Mutate/section:market hold is authority to trade its own inventory, never another
    // seat's — a hold over the section was never a hold over every party inside it, the same distinction the
    // row-scoped Edit/state:&lt;row&gt; check draws for state writes. WorldPrincipal.World is exempt for the same
    // reason Console is (both are trusted, structural or operator identities that never impersonate a live player),
    // even though no engine sweep constructs a market mutation naming a party today.
    private static bool TryAuthorizeMarketParty(WorldPrincipal actingPrincipal, WorldPrincipal party, out string reason) {
        if ((actingPrincipal == party) || (actingPrincipal.Kind is PrincipalKind.Console or PrincipalKind.World)) {
            reason = string.Empty;

            return true;
        }

        reason = $"{actingPrincipal.Describe()} may not act as trade party {party.Describe()} — only Console or {party.Describe()} itself may name it";

        return false;
    }

    // Reads a market fact cell, defaulting to zero for a holder who has never traded — the SAME "absent key ==
    // zero" convention UpsertStateCell's own Add operand already follows.
    private static long ReadMarketCellValue(WorldDefinition definition, WorldCellName row, string key, ulong tick) {
        _ = WorldStateReader.TryRead(definition: definition, rowName: row, key: key, tick: tick, row: out _, rawValue: out var raw, text: out _);

        return (raw ?? 0L);
    }

    // Writes a market fact cell's quantity/balance, preserving whatever Advance/Provenance the cell already carried
    // (a market move is a value write, never a re-mint) — the SAME base-value-write-preserves-advance rule
    // UpsertStateCell's own compose arm follows. Assumes `rowName` already resolved against `rows` (every caller
    // validates existence first); the row's declared envelope (Min/Max/NonNegative) is left to the whole-document
    // revalidation TryApplyMutation runs after compose, exactly like every other state write here.
    private static IReadOnlyList<WorldStateRow> WriteMarketCell(IReadOnlyList<WorldStateRow> rows, WorldCellName rowName, string key, long value) {
        var row = WorldDefinitionRows.FindStateRow(rows: rows, name: rowName)!;
        var cellKey = WorldCellName.Parse(candidate: key);
        WorldStateAdvance? existingAdvance = null;
        string? existingProvenance = null;

        foreach (var cell in (row.Cells ?? [])) {
            if (cell.Key == cellKey) {
                existingAdvance = cell.Advance;
                existingProvenance = cell.Provenance;

                break;
            }
        }

        var cells = Upsert(list: (row.Cells ?? []), item: new WorldStateCell(Key: cellKey, Value: value, Advance: existingAdvance, Provenance: existingProvenance), keyOf: static (WorldStateCell cell) => cell.Key);

        return Upsert(list: rows, item: (row with { Cells = cells }), keyOf: static (WorldStateRow r) => r.Name);
    }

    // The house fee on a settled amount, in basis points — bounded well inside `long` (amount is capped at
    // WorldStateCapacity.MaxIntCellValue and feeBasisPoints at WorldMarketCapacity.MaxFeeBasisPoints, so the
    // intermediate product can never overflow).
    private static long MarketFee(long amount, int feeBasisPoints) => ((amount * feeBasisPoints) / 10_000L);

    private static WorldMarketListing? FindMarketListing(IReadOnlyList<WorldMarketListing> listings, long id) {
        foreach (var listing in listings) {
            if (listing.Id == id) {
                return listing;
            }
        }

        return null;
    }

    // market.list — escrows Quantity out of the seller's own ItemRow cell atomically with minting the listing row.
    private static bool TryComposeCreateMarketListing(WorldDefinition current, WorldMutation.CreateMarketListing mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        if (current.Market is not { } market) {
            reason = "this world authors no market section";

            return false;
        }

        if (!TryPlayerCellKey(principal: mutation.Seller, key: out var sellerKey)) {
            reason = $"seller {mutation.Seller.Describe()} must be a seat or peer";

            return false;
        }

        if (!TryAuthorizeMarketParty(actingPrincipal: mutation.Principal, party: mutation.Seller, reason: out reason)) {
            return false;
        }

        var admitted = false;

        foreach (var format in market.EffectiveFormats) {
            if (format == mutation.Format) {
                admitted = true;

                break;
            }
        }

        if (!admitted) {
            reason = $"market does not admit format '{mutation.Format}'";

            return false;
        }

        if (!float.IsFinite(f: mutation.DurationSeconds) || (mutation.DurationSeconds < market.MinDurationSeconds) || (mutation.DurationSeconds > market.MaxDurationSeconds)) {
            reason = $"durationSeconds {mutation.DurationSeconds} is outside {market.MinDurationSeconds}..{market.MaxDurationSeconds}";

            return false;
        }

        if (mutation.Quantity <= 0) {
            reason = $"quantity {mutation.Quantity} must be positive";

            return false;
        }

        if (WorldDefinitionRows.FindStateRow(rows: current.State, name: mutation.ItemRow) is not { } itemRow) {
            reason = $"no state row named '{mutation.ItemRow}'";

            return false;
        }

        if (WorldDefinitionRows.FindStateRow(rows: current.State, name: mutation.CurrencyRow) is not { } currencyRow) {
            reason = $"no state row named '{mutation.CurrencyRow}'";

            return false;
        }

        if ((itemRow.Kind != CellKind.Int) || (itemRow.Capacity is null)) {
            reason = $"'{mutation.ItemRow}' is not a capacity-bounded int state row";

            return false;
        }

        if ((currencyRow.Kind != CellKind.Int) || (currencyRow.Capacity is null)) {
            reason = $"'{mutation.CurrencyRow}' is not a capacity-bounded int state row";

            return false;
        }

        if (mutation.Format == WorldMarketFormat.English) {
            if (mutation.StartPrice <= 0) {
                reason = "startPrice must be positive for an English listing";

                return false;
            }
        } else {
            if ((mutation.BuyoutPrice is not { } requiredBuyout) || (requiredBuyout <= 0)) {
                reason = "a buyout listing requires a positive buyoutPrice";

                return false;
            }

            // startPrice is unused by buyout (market.list's own help text says so); refused rather than silently
            // carried, the same door-not-type instinct WorldDefinitionValidator.ValidateMarket's Buyout arm applies
            // to currentBid/currentBidder — this is the immediate, per-field refusal, before whole-document
            // revalidation would catch the same thing with a less specific reason.
            if (mutation.StartPrice != 0) {
                reason = "startPrice is unused by buyout — pass 0";

                return false;
            }
        }

        if ((mutation.BuyoutPrice is { } declaredBuyout) && (declaredBuyout <= 0)) {
            reason = "buyoutPrice must be positive";

            return false;
        }

        if (current.SimulationRateHz <= 0) {
            reason = "a market listing needs a tick-mapped duration, refused in a rate-0 world";

            return false;
        }

        var sellerBalance = ReadMarketCellValue(definition: current, row: mutation.ItemRow, key: sellerKey, tick: tick);

        if (sellerBalance < mutation.Quantity) {
            reason = $"seller holds {sellerBalance} of '{mutation.ItemRow}', short of the {mutation.Quantity} listed";

            return false;
        }

        var durationTicks = WorldSimulationTickConversion.DurationTicks(seconds: mutation.DurationSeconds, ratePerSecond: (uint)current.SimulationRateHz);
        var deadlineTick = unchecked((long)tick + (long)durationTicks);

        var state = WriteMarketCell(rows: current.State, rowName: mutation.ItemRow, key: sellerKey, value: (sellerBalance - mutation.Quantity));

        var listing = new WorldMarketListing(
            Id: market.NextListingId,
            Seller: mutation.Seller,
            ItemRow: mutation.ItemRow,
            Quantity: mutation.Quantity,
            CurrencyRow: mutation.CurrencyRow,
            Format: mutation.Format,
            StartPrice: mutation.StartPrice,
            BuyoutPrice: mutation.BuyoutPrice,
            DeadlineTick: deadlineTick
        );

        var newMarket = (market with {
            Listings = Upsert(list: (market.Listings ?? []), item: listing, keyOf: static (WorldMarketListing l) => l.Id),
            NextListingId = (market.NextListingId + 1),
        });

        candidate = (current with { State = state, Market = newMarket });

        return true;
    }

    // market.bid — escrows Amount out of the bidder's own currency cell, refunding any standing bidder in the SAME
    // candidate. English format only. A standing bidder raising their OWN bid is netted against their own standing
    // escrow (one read, one write, delta-charged) rather than charged the full new amount and then "refunded" the
    // old one through a second read of the very cell this compose pass just wrote — that second read would see the
    // cell's pre-rebase Advance/EpochTick (RebaseAdvanceEpoch runs AFTER TryCompose) and re-apply the elapsed
    // accrual TryRead already folded into the first read, on top of a base that already carries it.
    private static bool TryComposePlaceMarketBid(WorldDefinition current, WorldMutation.PlaceMarketBid mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        var market = (current.Market ?? WorldMarketSection.Empty);

        if (FindMarketListing(listings: (market.Listings ?? []), id: mutation.ListingId) is not { } listing) {
            reason = $"no listing #{mutation.ListingId}";

            return false;
        }

        if (listing.Status != WorldMarketListingStatus.Active) {
            reason = $"listing #{mutation.ListingId} is {listing.Status}, not active";

            return false;
        }

        if (unchecked((long)tick) >= listing.DeadlineTick) {
            reason = $"listing #{mutation.ListingId} has reached its deadline";

            return false;
        }

        if (listing.Format != WorldMarketFormat.English) {
            reason = $"listing #{mutation.ListingId} is {listing.Format}, which takes no incremental bids";

            return false;
        }

        if (!TryPlayerCellKey(principal: mutation.Bidder, key: out var bidderKey)) {
            reason = $"bidder {mutation.Bidder.Describe()} must be a seat or peer";

            return false;
        }

        if (!TryAuthorizeMarketParty(actingPrincipal: mutation.Principal, party: mutation.Bidder, reason: out reason)) {
            return false;
        }

        if (mutation.Bidder == listing.Seller) {
            reason = "the seller may not bid on their own listing";

            return false;
        }

        // long.MaxValue is a legal carried balance/bid, but it has no representable successor. Refuse explicitly:
        // adding one would wrap the minimum negative, admit a lower bid, and make a self-bid's net charge negative.
        if (listing.CurrentBid == long.MaxValue) {
            reason = $"listing #{mutation.ListingId} already carries the maximum representable bid and cannot be raised";

            return false;
        }

        var minBid = ((listing.CurrentBid > 0) ? (listing.CurrentBid + 1) : listing.StartPrice);

        if (mutation.Amount < minBid) {
            reason = $"bid {mutation.Amount} does not meet the minimum {minBid}";

            return false;
        }

        var isSelfRaise = (mutation.Bidder == listing.CurrentBidder);
        var netCharge = (isSelfRaise ? (mutation.Amount - listing.CurrentBid) : mutation.Amount);

        var bidderBalance = ReadMarketCellValue(definition: current, row: listing.CurrencyRow, key: bidderKey, tick: tick);

        if (bidderBalance < netCharge) {
            reason = isSelfRaise
                ? $"bidder holds {bidderBalance} of '{listing.CurrencyRow}', short of the {netCharge} additional needed to raise from {listing.CurrentBid} to {mutation.Amount}"
                : $"bidder holds {bidderBalance} of '{listing.CurrencyRow}', short of the {mutation.Amount} bid";

            return false;
        }

        var state = WriteMarketCell(rows: current.State, rowName: listing.CurrencyRow, key: bidderKey, value: (bidderBalance - netCharge));

        if (!isSelfRaise && (listing.CurrentBidder is { } previousBidder) && TryPlayerCellKey(principal: previousBidder, key: out var previousKey)) {
            var previousBalance = ReadMarketCellValue(definition: (current with { State = state }), row: listing.CurrencyRow, key: previousKey, tick: tick);

            state = WriteMarketCell(rows: state, rowName: listing.CurrencyRow, key: previousKey, value: (previousBalance + listing.CurrentBid));
        }

        var updatedListing = (listing with { CurrentBid = mutation.Amount, CurrentBidder = mutation.Bidder });
        var newMarket = (market with { Listings = Upsert(list: (market.Listings ?? []), item: updatedListing, keyOf: static (WorldMarketListing l) => l.Id) });

        candidate = (current with { State = state, Market = newMarket });

        return true;
    }

    // market.buyout — settles a listing immediately at its declared BuyoutPrice: pays the seller net of fee, refunds
    // any standing English bidder, credits the buyer's item cell, all in the SAME candidate.
    private static bool TryComposeBuyoutMarketListing(WorldDefinition current, WorldMutation.BuyoutMarketListing mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        var market = (current.Market ?? WorldMarketSection.Empty);

        if (FindMarketListing(listings: (market.Listings ?? []), id: mutation.ListingId) is not { } listing) {
            reason = $"no listing #{mutation.ListingId}";

            return false;
        }

        if (listing.Status != WorldMarketListingStatus.Active) {
            reason = $"listing #{mutation.ListingId} is {listing.Status}, not active";

            return false;
        }

        if (unchecked((long)tick) >= listing.DeadlineTick) {
            reason = $"listing #{mutation.ListingId} has reached its deadline";

            return false;
        }

        if (listing.BuyoutPrice is not { } buyoutPrice) {
            reason = $"listing #{mutation.ListingId} declares no buyoutPrice";

            return false;
        }

        if (!TryPlayerCellKey(principal: mutation.Buyer, key: out var buyerKey)) {
            reason = $"buyer {mutation.Buyer.Describe()} must be a seat or peer";

            return false;
        }

        if (!TryAuthorizeMarketParty(actingPrincipal: mutation.Principal, party: mutation.Buyer, reason: out reason)) {
            return false;
        }

        if (mutation.Buyer == listing.Seller) {
            reason = "the seller may not buy out their own listing";

            return false;
        }

        if (!TryPlayerCellKey(principal: listing.Seller, key: out var sellerKey)) {
            reason = "listing seller is not a seat or peer";

            return false;
        }

        // A standing bidder buying themselves out only owes the difference — their own escrowed bid is refunded
        // and re-spent in the SAME move, never round-tripped through a separate refund the caller could observe.
        var refundToSelf = ((listing.CurrentBidder == mutation.Buyer) ? listing.CurrentBid : 0L);
        var buyerBalance = ReadMarketCellValue(definition: current, row: listing.CurrencyRow, key: buyerKey, tick: tick);
        var effectiveCost = (buyoutPrice - refundToSelf);

        if (buyerBalance < effectiveCost) {
            reason = $"buyer holds {buyerBalance} of '{listing.CurrencyRow}', short of the {effectiveCost} needed";

            return false;
        }

        var state = WriteMarketCell(rows: current.State, rowName: listing.CurrencyRow, key: buyerKey, value: (buyerBalance - effectiveCost));

        if ((listing.CurrentBidder is { } previousBidder) && (previousBidder != mutation.Buyer) && TryPlayerCellKey(principal: previousBidder, key: out var previousKey)) {
            var previousBalance = ReadMarketCellValue(definition: (current with { State = state }), row: listing.CurrencyRow, key: previousKey, tick: tick);

            state = WriteMarketCell(rows: state, rowName: listing.CurrencyRow, key: previousKey, value: (previousBalance + listing.CurrentBid));
        }

        var fee = MarketFee(amount: buyoutPrice, feeBasisPoints: market.FeeBasisPoints);
        var sellerBalance = ReadMarketCellValue(definition: (current with { State = state }), row: listing.CurrencyRow, key: sellerKey, tick: tick);

        state = WriteMarketCell(rows: state, rowName: listing.CurrencyRow, key: sellerKey, value: (sellerBalance + (buyoutPrice - fee)));

        var buyerItemBalance = ReadMarketCellValue(definition: (current with { State = state }), row: listing.ItemRow, key: buyerKey, tick: tick);

        state = WriteMarketCell(rows: state, rowName: listing.ItemRow, key: buyerKey, value: (buyerItemBalance + listing.Quantity));

        var updatedListing = (listing with { Status = WorldMarketListingStatus.Settled, ResolvedTick = unchecked((long)tick) });

        candidate = (current with {
            State = state,
            Market = (market with {
                Listings = Upsert(list: (market.Listings ?? []), item: updatedListing, keyOf: static (WorldMarketListing l) => l.Id),
                FeeReserve = (market.FeeReserve + fee),
            }),
        });

        return true;
    }

    // market.cancel — withdraws a listing, returning the escrowed item to the seller and refunding any standing
    // English bidder, all in the SAME candidate. Seller-only.
    private static bool TryComposeCancelMarketListing(WorldDefinition current, WorldMutation.CancelMarketListing mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        var market = (current.Market ?? WorldMarketSection.Empty);

        if (FindMarketListing(listings: (market.Listings ?? []), id: mutation.ListingId) is not { } listing) {
            reason = $"no listing #{mutation.ListingId}";

            return false;
        }

        if (listing.Status != WorldMarketListingStatus.Active) {
            reason = $"listing #{mutation.ListingId} is {listing.Status}, not active";

            return false;
        }

        if (!TryAuthorizeMarketParty(actingPrincipal: mutation.Principal, party: mutation.Canceler, reason: out reason)) {
            return false;
        }

        if (mutation.Canceler != listing.Seller) {
            reason = $"only the seller {listing.Seller.Describe()} may cancel listing #{mutation.ListingId}";

            return false;
        }

        if (!TryPlayerCellKey(principal: listing.Seller, key: out var sellerKey)) {
            reason = "listing seller is not a seat or peer";

            return false;
        }

        var sellerItemBalance = ReadMarketCellValue(definition: current, row: listing.ItemRow, key: sellerKey, tick: tick);
        var state = WriteMarketCell(rows: current.State, rowName: listing.ItemRow, key: sellerKey, value: (sellerItemBalance + listing.Quantity));

        if ((listing.CurrentBidder is { } bidder) && TryPlayerCellKey(principal: bidder, key: out var bidderKey)) {
            var bidderBalance = ReadMarketCellValue(definition: (current with { State = state }), row: listing.CurrencyRow, key: bidderKey, tick: tick);

            state = WriteMarketCell(rows: state, rowName: listing.CurrencyRow, key: bidderKey, value: (bidderBalance + listing.CurrentBid));
        }

        var updatedListing = (listing with { Status = WorldMarketListingStatus.Cancelled, ResolvedTick = unchecked((long)tick) });
        var newMarket = (market with { Listings = Upsert(list: (market.Listings ?? []), item: updatedListing, keyOf: static (WorldMarketListing l) => l.Id) });

        candidate = (current with { State = state, Market = newMarket });

        return true;
    }

    // The engine's own deadline sweep, fired by Server.WorldServer's per-tick market pass (the SAME shape as its own
    // ReclaimExpiredEscrows) under WorldPrincipal.World — never reachable from a player submission. A standing
    // English bid settles (pays the seller net of fee, credits the winner's item cell); no bid at all expires
    // (returns the escrowed item to the seller).
    private static bool TryComposeSettleMarketListing(WorldDefinition current, WorldMutation.SettleMarketListing mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        if (mutation.Principal != WorldPrincipal.World) {
            reason = "only the engine's own timeout sweep may settle a listing";

            return false;
        }

        var market = (current.Market ?? WorldMarketSection.Empty);

        if (FindMarketListing(listings: (market.Listings ?? []), id: mutation.ListingId) is not { } listing) {
            reason = $"no listing #{mutation.ListingId}";

            return false;
        }

        if (listing.Status != WorldMarketListingStatus.Active) {
            reason = $"listing #{mutation.ListingId} is {listing.Status}, not active";

            return false;
        }

        if (unchecked((long)tick) < listing.DeadlineTick) {
            reason = $"listing #{mutation.ListingId} has not yet reached its deadline";

            return false;
        }

        if (!TryPlayerCellKey(principal: listing.Seller, key: out var sellerKey)) {
            reason = "listing seller is not a seat or peer";

            return false;
        }

        if ((listing.CurrentBidder is { } winner) && TryPlayerCellKey(principal: winner, key: out var winnerKey)) {
            var fee = MarketFee(amount: listing.CurrentBid, feeBasisPoints: market.FeeBasisPoints);
            var sellerBalance = ReadMarketCellValue(definition: current, row: listing.CurrencyRow, key: sellerKey, tick: tick);
            var state = WriteMarketCell(rows: current.State, rowName: listing.CurrencyRow, key: sellerKey, value: (sellerBalance + (listing.CurrentBid - fee)));
            var winnerItemBalance = ReadMarketCellValue(definition: (current with { State = state }), row: listing.ItemRow, key: winnerKey, tick: tick);

            state = WriteMarketCell(rows: state, rowName: listing.ItemRow, key: winnerKey, value: (winnerItemBalance + listing.Quantity));

            var settled = (listing with { Status = WorldMarketListingStatus.Settled, ResolvedTick = unchecked((long)tick) });

            candidate = (current with {
                State = state,
                Market = (market with {
                    Listings = Upsert(list: (market.Listings ?? []), item: settled, keyOf: static (WorldMarketListing l) => l.Id),
                    FeeReserve = (market.FeeReserve + fee),
                }),
            });

            return true;
        }

        // No bid ever landed — expiry, not a sale: return the escrowed item.
        var sellerItemBalance = ReadMarketCellValue(definition: current, row: listing.ItemRow, key: sellerKey, tick: tick);
        var expiredState = WriteMarketCell(rows: current.State, rowName: listing.ItemRow, key: sellerKey, value: (sellerItemBalance + listing.Quantity));
        var expired = (listing with { Status = WorldMarketListingStatus.Expired, ResolvedTick = unchecked((long)tick) });

        candidate = (current with {
            State = expiredState,
            Market = (market with { Listings = Upsert(list: (market.Listings ?? []), item: expired, keyOf: static (WorldMarketListing l) => l.Id) }),
        });

        return true;
    }

    // market retention sweep — removes every terminal (settled/cancelled/expired) row whose ResolvedTick lies at
    // least market.retentionSeconds (converted the same way a listing's own duration becomes its DeadlineTick)
    // behind the applying tick, in the same candidate. An active row is never touched, and NextListingId never
    // rewinds — a pruned id is retired, not reissued. World-only, fired by PruneExpiredMarketListings once at least
    // one row is eligible; refuses (a no-op) if it somehow fires with nothing eligible, rather than journaling an
    // empty "prune".
    private static bool TryComposePruneMarketListings(WorldDefinition current, WorldMutation.PruneMarketListings mutation, ulong tick, out WorldDefinition candidate, out string reason) {
        candidate = current;
        reason = string.Empty;

        if (mutation.Principal != WorldPrincipal.World) {
            reason = "only the engine's own retention sweep may prune market listings";

            return false;
        }

        if (current.Market is not { } market) {
            reason = "this world authors no market section";

            return false;
        }

        if (current.SimulationRateHz <= 0) {
            reason = "market retention needs a tick-mapped window, refused in a rate-0 world";

            return false;
        }

        var retentionTicks = unchecked((long)WorldSimulationTickConversion.DurationTicks(seconds: market.RetentionSeconds, ratePerSecond: (uint)current.SimulationRateHz));
        var listings = (market.Listings ?? []);
        List<WorldMarketListing>? kept = null;

        for (var index = 0; (index < listings.Count); index++) {
            var listing = listings[index];
            var eligible = ((listing.Status != WorldMarketListingStatus.Active)
                && (listing.ResolvedTick is { } resolvedTick)
                && (unchecked((long)tick) >= unchecked(resolvedTick + retentionTicks)));

            if (eligible) {
                kept ??= new List<WorldMarketListing>(collection: listings.Take(count: index));

                continue;
            }

            kept?.Add(item: listing);
        }

        if (kept is null) {
            reason = "no terminal listing has yet reached market.retentionSeconds";

            return false;
        }

        candidate = (current with { Market = (market with { Listings = kept }) });

        return true;
    }

    private static bool ContainsMember(IReadOnlyList<WorldPrincipal> members, WorldPrincipal member) {
        foreach (var existing in members) {
            if (existing == member) {
                return true;
            }
        }

        return false;
    }

    private static WorldGroup? FindGroupRow(IReadOnlyList<WorldGroup> groups, string id) {
        foreach (var row in groups) {
            if (string.Equals(a: row.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return row;
            }
        }

        return null;
    }

    private static WorldGroupKind? FindGroupKind(IReadOnlyList<WorldGroupKind> kinds, string name) {
        foreach (var kind in kinds) {
            if (string.Equals(a: kind.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return kind;
            }
        }

        return null;
    }

    // The Ownership section's own find — keyed by the Subject value (a readonly record struct, so structural
    // equality is exact) rather than a name string, since a subject is (kind, id) rather than one bare identifier.
    private static WorldOwnership? FindOwnershipRow(IReadOnlyList<WorldOwnership> ownership, OwnershipSubject subject) {
        foreach (var row in ownership) {
            if (row.Subject == subject) {
                return row;
            }
        }

        return null;
    }

    // Replaces the row naming the SAME subject as `row` — the coarse whole-row upsert every other section rides,
    // specialized because OwnershipSubject (not a bare name) is the key.
    private static IReadOnlyList<WorldOwnership> ReplaceOwnership(IReadOnlyList<WorldOwnership> ownership, WorldOwnership row) =>
        Upsert(list: ownership, item: row, keyOf: static (WorldOwnership o) => o.Subject);

    // The shared LeaveGroup/KickMember(Remove) tail: drop the one member's row, then dissolve the WHOLE group when
    // that empties it and the kind's Lifetime is Ephemeral — checked ONLY when the group HAD at least one member
    // before (forming an empty group never auto-dissolves it). A null kind (defensive — the validator refuses a
    // dangling kindName before this could be reached live) leaves the group Persistent by default.
    private static IReadOnlyList<WorldGroup> RemoveMemberAndMaybeDissolve(IReadOnlyList<WorldGroup> groups, WorldGroup group, WorldGroupKind? kind, WorldPrincipal member) {
        var remaining = new List<WorldPrincipal>(capacity: group.Members.Count);

        foreach (var existing in group.Members) {
            if (existing != member) {
                remaining.Add(item: existing);
            }
        }

        if ((remaining.Count == 0) && (group.Members.Count > 0) && (kind?.Lifetime == WorldGroupLifetime.Ephemeral)) {
            _ = Remove(list: groups, key: group.Id, keyOf: static (WorldGroup row) => row.Id, result: out var dissolved);

            return dissolved;
        }

        return Upsert(list: groups, item: (group with { Members = remaining }), keyOf: static (WorldGroup row) => row.Id);
    }

    /// <summary>Owns canonical document validation and authored-hash matching at the mutation composition boundary.</summary>
    private static bool TryCanonicalizeDocument<TDocument>(
        TDocument document,
        string id,
        string hash,
        string kind,
        Func<TDocument, string, Puck.Forge.Authoring.CanonicalDocument<TDocument>> canonicalize,
        out TDocument canonicalDocument,
        out string reason) {
        Puck.Forge.Authoring.CanonicalDocument<TDocument> canonical;

        try {
            canonical = canonicalize(arg1: document, arg2: id);
        } catch (Puck.Forge.Authoring.DocumentValidationException exception) {
            canonicalDocument = document;
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }

        if (!string.Equals(a: hash, b: canonical.Hash, comparisonType: StringComparison.Ordinal)) {
            canonicalDocument = document;
            reason = $"{kind} '{id}' hash '{hash}' does not match the canonical sha256 '{canonical.Hash}' — a hash must come from the canonicalize pipeline";

            return false;
        }

        canonicalDocument = canonical.Document;
        reason = string.Empty;

        return true;
    }

    // Composes Generate as a PURE function of (candidate document, instance identity): the site's source resolves
    // from the document, WorldGeneratorEngine SEEKS the stream to the position the site's own DrawCursor records, and
    // BOTH the drawn value and the advanced cursor/decks land in the SAME candidate. Nothing lives outside the
    // document, which is what makes world.undo rewind a draw bit-identically with no bookkeeping to reconcile. The
    // sampling itself lives in Puck.World.Data because the BOOT resolver — which runs before this server exists —
    // must reach the identical code.
    private static bool TryComposeGenerate(WorldDefinition current, WorldMutation.Generate mutation, string instanceIdentity, out WorldDefinition candidate, out string reason) {
        candidate = current;

        if (WorldDefinitionRows.FindStateRow(rows: current.State, name: mutation.Row) is not { } siteRow) {
            reason = $"no state row named '{mutation.Row}'";

            return false;
        }

        if (siteRow.Draw is not { } draw) {
            reason = $"state row '{mutation.Row}' declares no draw — 'generate' redraws a draw site";

            return false;
        }

        if (draw.Timing == WorldDrawTiming.Boot) {
            reason = $"state row '{mutation.Row}' declares timing=boot — it draws once at first fill and is never redrawn";

            return false;
        }

        if (!WorldGeneratorEngine.TryResolveSource(generators: current.Generators, draw: draw, generator: out var generator, reason: out var resolveReason)) {
            reason = $"state row '{mutation.Row}' {resolveReason}";

            return false;
        }

        var site = WorldDrawSites.StateRow(rowName: siteRow.Name);

        if (!WorldGeneratorEngine.TryFire(
                generator: generator,
                targetKind: siteRow.Kind,
                seedState: WorldGeneratorEngine.ComputeSeedState(worldSeed: (current.Generation?.WorldSeed ?? 0UL), instanceIdentity: instanceIdentity, site: site),
                stream: WorldGeneratorEngine.ComputeStreamId(site: site),
                cursor: siteRow.DrawCursor,
                decks: siteRow.DrawDecks,
                result: out var fired,
                reason: out var fireReason)) {
            reason = $"state row '{mutation.Row}' {fireReason}";

            return false;
        }

        if ((fired.Text is { } emission) && (emission.Length > WorldStateCapacity.MaxTextValueLength)) {
            reason = $"state row '{mutation.Row}' emission length {emission.Length} exceeds the {WorldStateCapacity.MaxTextValueLength}-unit text bound";

            return false;
        }

        var cell = ((fired.Text is { } text)
            ? new WorldStateCell(Key: WorldStateRow.SlotKey, Text: text)
            : new WorldStateCell(Key: WorldStateRow.SlotKey, Value: fired.Numeric!.Value));
        var state = Upsert(
            list: current.State,
            item: (siteRow with { Cells = [cell], DrawCursor = (siteRow.DrawCursor + fired.Samples), DrawDecks = (fired.Decks ?? siteRow.DrawDecks) }),
            keyOf: static (WorldStateRow row) => row.Name
        );

        candidate = (current with { State = state });
        reason = string.Empty;

        return true;
    }

    // The (row, key) PAIR rule at the mutation boundary: a null key means the row's SLOT cell, and a row that is
    // positively keyed (WorldStateRow.IsKeyed) has no single cell for a null key to mean — refused by name rather
    // than silently writing cells[0].
    private static bool TryResolveTargetKey(WorldStateRow row, string? key, out WorldCellName resolved, out string reason) {
        if (key is not null) {
            if (!WorldCellName.TryParse(candidate: key, name: out resolved, reason: out var keyReason)) {
                reason = $"cell key '{key}' {keyReason}";

                return false;
            }

            reason = string.Empty;

            return true;
        }

        resolved = WorldStateRow.SlotKey;

        if (row.IsKeyed) {
            reason = $"state row '{row.Name}' is keyed and no cell key was named — a keyed row has no single cell to write";

            return false;
        }

        reason = string.Empty;

        return true;
    }

    // An EXPLICIT write to an advancing cell — a whole-row UpsertStateRow (which re-bases the row's OWN slot advance
    // AND every keyed cell's own advance, since it re-declares the whole row), an UpsertStateCell (which re-bases
    // ONLY the one cell it names — the row's slot advance when that cell IS the slot key, or that cell's own advance
    // otherwise), or a market mutation (which re-bases every (row, key) cell it actually wrote through
    // WriteMarketCell — see MarketCellTouches) — re-bases WorldStateAdvance.EpochTick to `tick`, unconditionally
    // overwriting whatever epoch the write's own payload carried (see WorldStateAdvance's remarks). A market write
    // that skipped this would let a cell's elapsed accrual apply a second time on its very next read: WriteMarketCell
    // preserves the pre-write Advance record verbatim (it is a value move, never a re-mint), so the base it installs
    // already has that accrual baked in — an un-rebased epoch would let the same elapsed span compute again from the
    // old epoch against the new base. Runs AFTER TryCompose so it sees the row/cell TryCompose just installed, and
    // BEFORE validation/journal so a rebased epoch is what gets journaled, replayed by world.undo, and read back.
    // `original` is the document the mutation composed against (before this mutation applied) — market's own touches
    // need it to resolve a listing's pre-write state (its standing bidder, in particular) since `candidate` already
    // reflects the write. A no-op for every other mutation kind, and for a cell (row-level or per-cell) that carries
    // no advance trait at all.
    private static WorldDefinition RebaseAdvanceEpoch(WorldDefinition original, WorldDefinition candidate, WorldMutation mutation, ulong tick) {
        if (MarketCellTouches(original: original, mutation: mutation) is { } touches) {
            var touchedState = candidate.State;

            foreach (var touch in touches) {
                touchedState = RebaseKeyedCellAdvanceEpoch(rows: touchedState, rowName: touch.Row, key: touch.Key, tick: tick);
            }

            return (ReferenceEquals(objA: touchedState, objB: candidate.State) ? candidate : (candidate with { State = touchedState }));
        }

        string? rowName;
        string? cellKey; // null on a whole-row write (every advancing cell re-bases); the named key on a per-cell write.

        switch (mutation) {
            case WorldMutation.UpsertStateRow m:
                rowName = m.Row.Name.Value;
                cellKey = null;
                break;
            case WorldMutation.UpsertStateCell m:
                rowName = m.Row;
                cellKey = m.Key;
                break;
            default:
                return candidate;
        }

        if (WorldDefinitionRows.FindStateRow(rows: candidate.State, name: rowName) is not { } row) {
            return candidate;
        }

        var epoch = unchecked((long)tick);
        var rebasedRow = row;

        if (((cellKey is null) || string.Equals(a: cellKey, b: WorldStateRow.SlotKey, comparisonType: StringComparison.Ordinal))
            && (row.Advance is { } rowAdvance)) {
            rebasedRow = (rebasedRow with { Advance = (rowAdvance with { EpochTick = epoch }) });
        }

        var cells = (rebasedRow.Cells ?? []);
        List<WorldStateCell>? rebasedCells = null;

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];

            if ((cell.Advance is not { } cellAdvance) || ((cellKey is not null) && !string.Equals(a: cell.Key.Value, b: cellKey, comparisonType: StringComparison.Ordinal))) {
                continue;
            }

            rebasedCells ??= new List<WorldStateCell>(collection: cells);
            rebasedCells[index] = (cell with { Advance = (cellAdvance with { EpochTick = epoch }) });
        }

        if (rebasedCells is not null) {
            rebasedRow = (rebasedRow with { Cells = rebasedCells });
        }

        return (ReferenceEquals(objA: rebasedRow, objB: row)
            ? candidate
            : (candidate with { State = Upsert(list: candidate.State, item: rebasedRow, keyOf: static (WorldStateRow r) => r.Name) }));
    }

    // One (row, key) cell a market compose arm wrote through WriteMarketCell.
    private readonly record struct MarketCellTouch(WorldCellName Row, string Key);

    // The (row, key) pairs a market mutation actually wrote — derived the same way each TryCompose*Market* arm
    // derives its own keys (TryPlayerCellKey off each party's principal), reading `original`'s pre-write listing for
    // a bid/buyout/cancel/settle since `candidate` already carries this mutation's own write (in particular,
    // PlaceMarketBid's previous bidder is only findable in the listing as it stood before this bid replaced it).
    // Returns null for every non-market mutation kind, distinguishing "not a market mutation" from "a market
    // mutation that happens to touch nothing" (an empty list) — the latter can occur when a party is somehow not a
    // seat/peer at this point (defensive; TryCompose already refused before this ever runs).
    private static IReadOnlyList<MarketCellTouch>? MarketCellTouches(WorldDefinition original, WorldMutation mutation) {
        switch (mutation) {
            case WorldMutation.CreateMarketListing m: {
                if (!TryPlayerCellKey(principal: m.Seller, key: out var sellerKey)) {
                    return [];
                }

                return [new MarketCellTouch(Row: m.ItemRow, Key: sellerKey)];
            }
            case WorldMutation.PlaceMarketBid m: {
                if (FindMarketListing(listings: (original.Market?.Listings ?? []), id: m.ListingId) is not { } listing) {
                    return [];
                }

                var touches = new List<MarketCellTouch>(capacity: 2);

                if (TryPlayerCellKey(principal: m.Bidder, key: out var bidderKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.CurrencyRow, Key: bidderKey));
                }

                if ((listing.CurrentBidder is { } previous) && TryPlayerCellKey(principal: previous, key: out var previousKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.CurrencyRow, Key: previousKey));
                }

                return touches;
            }
            case WorldMutation.BuyoutMarketListing m: {
                if (FindMarketListing(listings: (original.Market?.Listings ?? []), id: m.ListingId) is not { } listing) {
                    return [];
                }

                var touches = new List<MarketCellTouch>(capacity: 4);

                if (TryPlayerCellKey(principal: m.Buyer, key: out var buyerKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.CurrencyRow, Key: buyerKey));
                    touches.Add(item: new MarketCellTouch(Row: listing.ItemRow, Key: buyerKey));
                }

                if (TryPlayerCellKey(principal: listing.Seller, key: out var sellerKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.CurrencyRow, Key: sellerKey));
                }

                if ((listing.CurrentBidder is { } previous) && (previous != m.Buyer) && TryPlayerCellKey(principal: previous, key: out var previousKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.CurrencyRow, Key: previousKey));
                }

                return touches;
            }
            case WorldMutation.CancelMarketListing m: {
                if (FindMarketListing(listings: (original.Market?.Listings ?? []), id: m.ListingId) is not { } listing) {
                    return [];
                }

                var touches = new List<MarketCellTouch>(capacity: 2);

                if (TryPlayerCellKey(principal: listing.Seller, key: out var sellerKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.ItemRow, Key: sellerKey));
                }

                if ((listing.CurrentBidder is { } bidder) && TryPlayerCellKey(principal: bidder, key: out var bidderKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.CurrencyRow, Key: bidderKey));
                }

                return touches;
            }
            case WorldMutation.SettleMarketListing m: {
                if (FindMarketListing(listings: (original.Market?.Listings ?? []), id: m.ListingId) is not { } listing) {
                    return [];
                }

                var touches = new List<MarketCellTouch>(capacity: 2);

                if ((listing.CurrentBidder is { } winner) && TryPlayerCellKey(principal: winner, key: out var winnerKey) && TryPlayerCellKey(principal: listing.Seller, key: out var winSellerKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.CurrencyRow, Key: winSellerKey));
                    touches.Add(item: new MarketCellTouch(Row: listing.ItemRow, Key: winnerKey));
                } else if (TryPlayerCellKey(principal: listing.Seller, key: out var expiredSellerKey)) {
                    touches.Add(item: new MarketCellTouch(Row: listing.ItemRow, Key: expiredSellerKey));
                }

                return touches;
            }
            default:
                return null;
        }
    }

    // Rebases one keyed cell's WorldStateAdvance.EpochTick to `tick` — the same rebase RebaseAdvanceEpoch's own
    // UpsertStateCell arm performs on a single named cell, factored out so a market write (which may touch several
    // cells across two rows in one mutation) can apply it per touch without duplicating the clamp-free with-expression
    // rebuild. A no-op for a cell that carries no advance trait, or a row/key MarketCellTouches named that this
    // document does not (or no longer) declare.
    private static IReadOnlyList<WorldStateRow> RebaseKeyedCellAdvanceEpoch(IReadOnlyList<WorldStateRow> rows, WorldCellName rowName, string key, ulong tick) {
        if (WorldDefinitionRows.FindStateRow(rows: rows, name: rowName) is not { } row) {
            return rows;
        }

        var cellKey = WorldCellName.Parse(candidate: key);
        var cells = (row.Cells ?? []);

        for (var index = 0; (index < cells.Count); index++) {
            var cell = cells[index];

            if ((cell.Key != cellKey) || (cell.Advance is not { } advance)) {
                continue;
            }

            var rebasedCells = new List<WorldStateCell>(collection: cells);

            rebasedCells[index] = (cell with { Advance = (advance with { EpochTick = unchecked((long)tick) }) });

            return Upsert(list: rows, item: (row with { Cells = rebasedCells }), keyOf: static (WorldStateRow r) => r.Name);
        }

        return rows;
    }

    // The KEYED counterpart of a slot cell's row-level rebase target: looks up an already-installed cell's own
    // advance trait so a scalar-value UpsertStateCell write preserves it (see the UpsertStateCell compose arm above)
    // rather than a fresh WorldStateCell record silently dropping it.
    private static WorldStateAdvance? FindCellAdvance(IReadOnlyList<WorldStateCell> cells, WorldCellName key) {
        foreach (var cell in cells) {
            if (cell.Key == key) {
                return cell.Advance;
            }
        }

        return null;
    }

    // ContainsKey/ApplyEviction moved to Puck.World.Data's WorldStateCellWriter (public, cross-project)
    // so an owned-identity document write — which has no ordered mutation domain of its own — runs the IDENTICAL
    // pure composition rather than a second reading of it. See WorldStateCellWriter's own remarks.

    // Replace the row whose key matches the item's, or append it — the coarse whole-row upsert.
    private static IReadOnlyList<T> Upsert<T, TKey>(IReadOnlyList<T> list, T item, Func<T, TKey> keyOf) {
        var key = keyOf(arg: item);
        var result = new List<T>(capacity: (list.Count + 1));
        var replaced = false;

        foreach (var existing in list) {
            if (!replaced && EqualityComparer<TKey>.Default.Equals(x: keyOf(arg: existing), y: key)) {
                result.Add(item: item);
                replaced = true;
            } else {
                result.Add(item: existing);
            }
        }

        if (!replaced) {
            result.Add(item: item);
        }

        return result;
    }

    // Drop the first row whose key matches — reports whether a row was actually removed.
    private static bool Remove<T, TKey>(IReadOnlyList<T> list, TKey key, Func<T, TKey> keyOf, out IReadOnlyList<T> result) {
        var kept = new List<T>(capacity: list.Count);
        var removed = false;

        foreach (var existing in list) {
            if (!removed && EqualityComparer<TKey>.Default.Equals(x: keyOf(arg: existing), y: key)) {
                removed = true;

                continue;
            }

            kept.Add(item: existing);
        }

        result = kept;

        return removed;
    }

    // The HUD element mutations' panel lookup — a single-element read-modify-write needs its OWNING panel by id
    // before it can rewrite that panel's Elements list; null when no panel declares that id.
    private static WorldHudPanel? FindHudPanel(IReadOnlyList<WorldHudPanel> panels, string id) {
        foreach (var panel in panels) {
            if (string.Equals(a: panel.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return panel;
            }
        }

        return null;
    }

    // Build and deliver the tick's snapshot to every typed-lane subscriber. Skipped with no subscriber attached.
    private void EmitSnapshot(ulong tick, ulong stepTicks) {
        if (!m_output.HasTypedSubscribers) {
            return;
        }

        m_output.DeliverSnapshot(snapshot: BuildSnapshot(tick: tick, stepTicks: stepTicks));
    }

    // Every live body's authoritative sim pose, color, archetype, and this tick's continuity hint, written into the
    // reused m_snapshotEntries array — the SAME borrowed-scratch shape as before the output hub: a typed subscriber
    // must fully consume (or copy) the returned WorldSnapshot before returning from DeliverSnapshot, because the next
    // tick's BuildSnapshot call overwrites this same backing array. Consumes (TakeContinuity) every body's one-shot
    // continuity hint — the ORDINARY per-tick broadcast path (EmitSnapshot). A late AttachSink must NOT call this
    // overload: see BuildPrimerSnapshot.
    private WorldSnapshot BuildSnapshot(ulong tick, ulong stepTicks) => BuildSnapshotCore(tick: tick, stepTicks: stepTicks, consumeContinuity: true);

    // The non-consuming primer path for AttachSink: PEEKS every body's continuity hint instead of consuming it, so a
    // newly attached sink's boot-state primer can never steal the flag an already-attached sink is still due to
    // observe via the next ordinary EmitSnapshot broadcast (the bug this repairs — see docs/world-model.md's
    // "Observation and display" section). Stamped with the server's actual current tick/step width
    // (m_lastCompletedTick/m_lastStepTicks), which are still their default 0/0 before the very first Step has ever
    // run — the one case where 0/0 is the honest answer, preserved exactly.
    private WorldSnapshot BuildPrimerSnapshot() => BuildSnapshotCore(tick: m_lastCompletedTick, stepTicks: m_lastStepTicks, consumeContinuity: false);

    private WorldSnapshot BuildSnapshotCore(ulong tick, ulong stepTicks, bool consumeContinuity) {
        var count = 0;

        for (var index = 0; (index < m_population.Capacity); index++) {
            if (!m_population.IsActive(index: index) || (m_population.EntryBody(index: index) is not { } body)) {
                continue;
            }

            m_snapshotEntries[count++] = new EntitySnapshot(
                Index: index,
                Position: body.Position,
                Orientation: body.Orientation,
                BodyColor: m_population.BodyColor(index: index),
                Active: true,
                Kit: m_population.KitIndex(index: index),
                Look: m_population.LookIndex(index: index),
                CatalogRig: m_population.CatalogRig(index: index),
                Continuity: (consumeContinuity ? body.TakeContinuity() : body.PeekContinuity()),
                Generation: m_population.Generation(index: index),
                PlacementId: m_population.InhabitantPlacementId(index: index)
            );
        }

        return new WorldSnapshot(
            Tick: tick,
            Revision: m_population.Revision,
            StepTicks: stepTicks,
            Entries: m_snapshotEntries.AsMemory(start: 0, length: count),
            Authority: AuthorityIdentity
        );
    }

    // One journal entry — the tick a mutation applied and the mutation itself (the edit history replay reproduces).
    private readonly record struct JournalEntry(ulong Tick, WorldMutation Mutation);

    // One buffered live-edit op, drained FIFO at the step boundary before intents. Each retains the submitting
    // envelope's connection/correlation identity (see EnqueueMutation's own remarks) so its eventual WorldEditEcho —
    // fired later, from inside DrainPendingOps, not at submit time — still names the right submitter.
    private abstract record PendingOp {
        // SourceAddonIndex/ActOrdinal are the addon mutation seam's completion fields (Phase-3 plan AXIS 2, I1):
        // -1/0 for every non-addon submitter (a console/client mutation has no act to complete). A Mutate op WITH a
        // source addon carries them through DrainPendingOps -> WorldAddonRuntime.CompleteMutation so the reserved
        // Answer cell EmitDisclosures already withheld space for gets its verdict staged at ResolveReads(T), for
        // delivery in the guest's batch T+1 — never applied here, only routed.
        public sealed record Mutate(WorldMutation Mutation, int ConnectionId, long CorrelationId, int SourceAddonIndex = -1, ushort ActOrdinal = 0) : PendingOp;
        public sealed record Rebuild(WorldRebuildRequest Request, WorldPrincipal Principal, int ConnectionId, long CorrelationId, string? ExpectedContentHash = null, string? PreparationFailure = null) : PendingOp;
        public sealed record Undo(int Count, WorldPrincipal Principal, int ConnectionId, long CorrelationId) : PendingOp;
        public sealed record AddonLifecycle(WorldAddonLifecycle Lifecycle, WorldPrincipal Principal, int ConnectionId, long CorrelationId) : PendingOp;
    }

    // One entry in the ordered domain (see m_ordered's own remarks): the envelope plus the completion its submitter
    // supplied (null when the caller does not need one).
    private abstract record OrderedEntry {
        public sealed record Submission(SubmissionEnvelope Envelope, Action<WorldSubmissionResult>? Completion) : OrderedEntry;
        public sealed record ServerEvent(WorldServerEvent Value) : OrderedEntry;
    }

    /// <summary>Submits one envelope into the ordered domain — the single front door every non-intent submission kind
    /// drains through (see <see cref="IWorldServerHost.Submit"/>'s own remarks). Enqueues, then immediately drains
    /// the whole queue inline, so a submission applies synchronously before this call returns — exactly matching the
    /// per-kind synchronous methods it replaces. The in-process <c>LoopbackTransport</c> submits on connection 0;
    /// <c>WorldTcpHost</c> submits each admitted socket peer under its own per-connection id.</summary>
    /// <param name="envelope">The envelope to submit.</param>
    /// <param name="completion">Invoked once with the envelope's typed result, or <see langword="null"/>.</param>
    public void Submit(SubmissionEnvelope envelope, Action<WorldSubmissionResult>? completion = null) {
        m_ordered.Enqueue(item: new OrderedEntry.Submission(Envelope: envelope, Completion: completion));
        DrainOrdered();
    }

    // Drains the ordered domain FIFO until empty, applying each envelope through the SAME per-kind apply methods the
    // old per-kind IServerLink surface called directly, and invoking that entry's completion with the typed result.
    // The reentrancy guard makes a (currently impossible) re-entrant Submit-from-inside-an-apply a defined no-op —
    // the re-enqueued entry is picked up by the OUTER drain's own loop instead of recursing.
    private void DrainOrdered() {
        if (m_drainingOrdered) {
            return;
        }

        m_drainingOrdered = true;

        try {
            while (m_ordered.TryDequeue(result: out var entry)) {
                switch (entry) {
                    case OrderedEntry.Submission submission:
                        var result = ApplyEnvelope(envelope: submission.Envelope);

                        submission.Completion?.Invoke(obj: result);
                        break;
                    case OrderedEntry.ServerEvent serverEvent:
                        ApplyServerEvent(serverEvent: serverEvent.Value);
                        break;
                }
            }
        } finally {
            m_drainingOrdered = false;
        }
    }

    // Dispatches one envelope to the apply method its payload kind names, stamping the envelope's connection/
    // correlation identity onto the WorldEditEcho those methods emit. Grant/Revoke's actor and Session/Mutation/
    // Definition/Undo/Composition/Lever/AddonLifecycle's acting principal are ALWAYS the envelope's own Principal —
    // the one field every submission kind funnels its acting identity through now, never a second copy.
    private WorldSubmissionResult ApplyEnvelope(SubmissionEnvelope envelope) {
        switch (envelope.Payload) {
            case WorldSubmissionPayload.Command command:
                ApplyCommand(command: command.Value, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Grant grant:
                Grant(grant: grant.Value, actor: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Revoke revoke:
                Revoke(grant: revoke.Value, actor: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Session session:
                return new WorldSubmissionResult.Session(Reply: ApplySession(request: session.Value));
            case WorldSubmissionPayload.Rebuild rebuild:
                EnqueueRebuild(request: rebuild.Value, principal: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Mutation mutation:
                EnqueueMutation(mutation: mutation.Value, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Undo undo:
                EnqueueUndo(count: undo.Count, principal: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Composition composition:
                ApplyComposition(composition: composition.Value, principal: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Lever lever:
                ApplySessionLever(lever: lever.Value, principal: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Query query:
                return new WorldSubmissionResult.Query(Answer: AnswerSubmittedQuery(query: query.Value, principal: envelope.Principal));
            case WorldSubmissionPayload.AddonLifecycle lifecycle:
                EnqueueAddonLifecycle(lifecycle: lifecycle.Value, principal: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.ScreenOp screenOp:
                // Synchronous, like Command/Grant/Revoke — never buffered to the tick boundary — so a following
                // WorldCommand.Engage submitted in the same batch (player.engage's auto-insert precheck) observes
                // this op's effect immediately. See WorldScreenOp's own remarks for why.
                TryApplyScreenOp(op: screenOp.Value, principal: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId, expectedContentHash: null);

                return WorldSubmissionResult.Ack.Instance;
            case WorldSubmissionPayload.Designation designation:
                ApplyDesignation(designation: designation.Value, principal: envelope.Principal, connectionId: envelope.ConnectionId, correlationId: envelope.CorrelationId);

                return WorldSubmissionResult.Ack.Instance;
            default:
                // No silent fallback: a new payload kind added without its own arm here would otherwise vanish
                // silently — a build-time authoring gap, surfaced loudly rather than dropped.
                throw new ArgumentOutOfRangeException(paramName: nameof(envelope), actualValue: envelope.Payload, message: $"no ApplyEnvelope arm for submission payload kind '{envelope.Payload.GetType().Name}' — every kind must map to its apply method.");
        }
    }

    /// <summary>Admits one remote-human peer connection through the population door and dispatches the
    /// <see cref="WorldServerEvent.PeerAdmitted"/> event through the same ordered domain every other lifecycle event
    /// drains through — <c>Server.WorldTcpHost</c>'s Hello door is the one caller, and it calls this only from the
    /// tick thread (the population/grant tables carry no lock), only after <see cref="Protocol.WorldAdmissionDoor"/>
    /// has already verified the connecting peer's identity off the tick thread. Refused by name on whichever
    /// capacity bound <see cref="WorldPopulation.TryAdmitRemotePeer"/> names.</summary>
    /// <param name="verdict">What <see cref="Protocol.WorldAdmissionDoor"/> decided this identity is authorized —
    /// the only shape this method accepts, so no ingress can hand it grant rows of its own. Empty templates mint
    /// nothing, which is a legitimate authored outcome (see <see cref="Protocol.WorldAdmissionEntry.Grants"/>).</param>
    /// <param name="expectedAdmissionEntries">The <c>admission</c> section <c>Protocol.WorldAdmissionDoor.TryAdmit</c>
    /// actually consulted to decide <paramref name="verdict"/>, captured by the caller before crossing onto
    /// the tick thread. Identity verification runs off the tick thread against a snapshot of the document, but this
    /// method is where the decision commits, on the tick thread, single-threaded with every mutation and rebuild —
    /// the one place that can prove the policy has not moved in between. Compared by reference against the live
    /// <see cref="Definition"/>'s own <c>Admission</c> list: <c>WorldDefinition</c>'s sections are immutable
    /// records, so an unrelated mutation or rebuild that never touches <c>Admission</c> leaves this exact reference
    /// standing, while one that does (a concurrent <c>world.reset</c>/<c>load</c>/<c>reload</c>, or a live edit to
    /// the section) mints a new list, which this method treats as the policy having moved and asks the peer to
    /// reconnect.</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    internal bool TryAdmitPeerConnection(WorldAdmissionVerdict? verdict, IReadOnlyList<WorldAdmissionEntry>? expectedAdmissionEntries, out WorldPeerEventEntry admitted, out string refusal) {
        if (!ReferenceEquals(objA: m_definition.Admission, objB: expectedAdmissionEntries)) {
            admitted = default;
            refusal = "the world's admission policy changed while this connection was verifying its identity — reconnect to be re-evaluated against the current policy";

            return false;
        }

        return TryAdmitVerifiedParticipant(verdict: verdict, reservedSlot: null, source: IntentSource.Live, authorityTransferred: false, admitted: out admitted, refusal: out refusal);
    }

    /// <summary>Admits one verified participant onto a population body and mints exactly what the admission door's
    /// verdict authorizes — the single entry every authority-materializing ingress crosses.</summary>
    /// <remarks>There is no arm that accepts grant rows. A caller with no verdict is refused by name rather than
    /// admitted with a default seed: an ingress that cannot say who it admitted has nothing to authorize.</remarks>
    /// <param name="verdict">The door's decision.</param>
    /// <param name="reservedSlot">The body index a destination escrow already reserved, or <see langword="null"/> to
    /// take the lowest free peer index.</param>
    /// <param name="source">The admitted body's intent source.</param>
    /// <param name="authorityTransferred">Whether this admission commits a transfer rather than opening a
    /// connection.</param>
    /// <param name="admitted">The admitted peer entry on success.</param>
    /// <param name="refusal">The named refusal on failure.</param>
    /// <returns><see langword="true"/> on success.</returns>
    internal bool TryAdmitVerifiedParticipant(WorldAdmissionVerdict? verdict, int? reservedSlot, IntentSource source, bool authorityTransferred, out WorldPeerEventEntry admitted, out string refusal) {
        if (verdict is not { } decision) {
            admitted = default;
            refusal = "admission carries no door verdict — nothing authorizes this ingress";

            return false;
        }

        var admittedOk = (reservedSlot is { } slot)
            ? m_population.TryAdmitRemotePeerAt(slot: slot, source: source, grantTemplates: decision.Templates, identityDomain: decision.IdentityDomain, identitySubject: decision.IdentitySubject, admitted: out admitted, refusal: out refusal, authorityTransferred: authorityTransferred)
            : m_population.TryAdmitRemotePeer(source: source, grantTemplates: decision.Templates, identityDomain: decision.IdentityDomain, identitySubject: decision.IdentitySubject, admitted: out admitted, refusal: out refusal);

        if (!admittedOk) {
            return false;
        }

        ApplyLifecycleEvents(admitted: [admitted], disconnected: [], ordered: true, mintedGrants: BuildAdmissionGrants(principal: admitted.Identity, bodyIndex: admitted.BodyIndex, templates: decision.Templates));

        return true;
    }

    /// <summary>Disconnects one remote-human peer connection: revokes every grant that generation held and drops the
    /// body, through the same <see cref="WorldServerEvent.PeerDisconnected"/> ordered-domain path a census shrink
    /// uses. <c>Server.WorldTcpHost</c> calls this from the tick thread on socket teardown (graceful or dead).</summary>
    /// <param name="peer">The peer entry <see cref="TryAdmitPeerConnection"/> returned at admission.</param>
    internal void DisconnectPeerConnection(WorldPeerEventEntry peer) {
        ApplyLifecycleEvents(admitted: [], disconnected: [peer], ordered: true);
    }

    /// <summary>Commits a federated transfer into the peer body index destination escrow reserved. Admission assigns
    /// the ordinary <see cref="PrincipalKind.Peer"/> principal and body together; no transfer-only principal exists.</summary>
    /// <param name="slot">The reserved destination body index.</param>
    /// <param name="verdict">The arrival verdict the reservation's own admission decision produced. The traveler's
    /// wire-supplied profile does not reach the identity columns: they name the authenticated authority the verdict
    /// was decided against.</param>
    /// <returns>The admission verdict.</returns>
    internal SessionReply AdmitTransferredPeer(int slot, WorldAdmissionVerdict? verdict) {
        if (!TryAdmitVerifiedParticipant(verdict: verdict, reservedSlot: slot, source: IntentSource.Live, authorityTransferred: true, admitted: out _, refusal: out var refusal)) {
            return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: refusal);
        }

        return new SessionReply(Accepted: true, AssignedIndex: (slot + 1), RosterEcho: string.Empty, Reason: string.Empty);
    }

    /// <summary>Commits an autonomous traveler into the entity-table index reserved by destination escrow. Its
    /// authored intent source survives the crossing; unlike a live peer, it receives no connection route or Drive
    /// grant and remains server-authored.</summary>
    internal SessionReply AdmitTransferredEntity(int slot, IntentSource source, WorldIdentity? identity) {
        if (source.IsLive) {
            return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: "an autonomous transfer cannot carry the Live intent source");
        }
        if (!m_population.TryAdmitTransferredEntityAt(slot: slot, source: source, admitted: out var admitted, refusal: out var refusal)) {
            return new SessionReply(Accepted: false, AssignedIndex: -1, RosterEcho: string.Empty, Reason: refusal);
        }

        ApplyLifecycleEvents(admitted: [admitted], disconnected: [], ordered: true);
        if (identity is not null) {
            m_population.SetSeatProfile(slot: slot, profile: identity);
        }
        return new SessionReply(Accepted: true, AssignedIndex: (slot + 1), RosterEcho: string.Empty, Reason: string.Empty);
    }

    /// <summary>Removes a peer admitted by a transfer whose multi-member commit is rolling back, including every
    /// generation-scoped grant minted with it.</summary>
    internal void RollbackTransferredEntity(int slot) {
        if (m_population.TryCaptureTransferredEntity(index: slot, peer: out var peer)) {
            foreach (var grant in m_grants.Rows(principal: peer.Identity)) {
                Revoke(grant: grant, actor: WorldPrincipal.Console);
            }
        }
        _ = m_population.TryDetachSeatForTransfer(slot: slot, profile: out _);
    }

    // Builds the concrete minted grant rows for one just-admitted peer from its verified admission templates. A
    // template can carry neither the Principal nor a body subject — both are unknowable until admission assigns an
    // index and generation — so those are the only fields this fills in; every other field passes through unchanged.
    private static List<WorldGrant> BuildAdmissionGrants(WorldPrincipal principal, int bodyIndex, IReadOnlyList<WorldAdmissionGrant> templates) {
        var minted = new List<WorldGrant>(capacity: templates.Count);

        foreach (var template in templates) {
            minted.Add(item: new WorldGrant(Principal: principal, Capability: template.Capability, Subject: template.SubjectFor(bodyIndex: bodyIndex), Exclusive: template.Exclusive, Budget: template.Budget, EventBudget: template.EventBudget, KindMask: template.KindMask));
        }

        return minted;
    }

    private void ApplyLifecycleEvents(IReadOnlyList<WorldPeerEventEntry> admitted, IReadOnlyList<WorldPeerEventEntry> disconnected, bool ordered, IReadOnlyList<WorldGrant>? mintedGrants = null) {
        if (disconnected.Count > 0) {
            var revoked = new List<WorldGrant>();

            foreach (var peer in disconnected) {
                revoked.AddRange(collection: m_grants.Rows(principal: peer.Identity));
            }

            DispatchServerEvent(serverEvent: new WorldServerEvent.PeerDisconnected(Entries: [.. disconnected], RevokedGrants: revoked), ordered: ordered);
        }

        if (admitted.Count > 0) {
            // mintedGrants is supplied only by TryAdmitVerifiedParticipant, built from the door's verdict. Every
            // other admitted-list caller (boot inhabitant reconciliation, world.population's SetSimulatedCount, a
            // definition swap's post-Rebuild reconciliation) activates a locally-simulated body with no connecting
            // identity to verify, and mints the census Control/all seed instead.
            var minted = (mintedGrants ?? BuildDefaultPeerControlGrants(admitted: admitted));

            DispatchServerEvent(serverEvent: new WorldServerEvent.PeerAdmitted(Entries: [.. admitted], MintedGrants: minted), ordered: ordered);
        }
    }

    private static List<WorldGrant> BuildDefaultPeerControlGrants(IReadOnlyList<WorldPeerEventEntry> admitted) {
        var minted = new List<WorldGrant>(capacity: admitted.Count);

        foreach (var peer in admitted) {
            minted.Add(item: new WorldGrant(Principal: peer.Identity, Capability: WorldCapability.Control, Subject: GrantSubject.All, Exclusive: false));
        }

        return minted;
    }

    private void DispatchServerEvent(WorldServerEvent serverEvent, bool ordered) {
        if (ordered) {
            m_ordered.Enqueue(item: new OrderedEntry.ServerEvent(Value: serverEvent));
            DrainOrdered();
        } else {
            ApplyServerEvent(serverEvent: serverEvent);
        }
    }

    /// <summary>Re-applies a server-authored event through the population and grant doors. Replay calls this same
    /// method; there is no state-install bypass.</summary>
    /// <param name="serverEvent">The ordered event.</param>
    /// <exception cref="ArgumentNullException"><paramref name="serverEvent"/> is <see langword="null"/>.</exception>
    public void ApplyServerEvent(WorldServerEvent serverEvent) {
        ArgumentNullException.ThrowIfNull(argument: serverEvent);

        switch (serverEvent) {
            case WorldServerEvent.PeerAdmitted admitted:
                foreach (var peer in admitted.Entries) {
                    m_population.ApplyPeerAdmitted(peer: in peer, grantTemplates: []);

                    foreach (var stale in m_grants.StalePeerGenerations(index: peer.BodyIndex, currentGeneration: peer.Generation)) {
                        foreach (var row in m_grants.Rows(principal: stale)) {
                            Revoke(grant: row, actor: WorldPrincipal.Console);
                        }
                    }
                }

                var installedGrants = new List<WorldGrant>();

                foreach (var grant in admitted.MintedGrants) {
                    if (TryApplyGrant(grant: grant, actor: WorldPrincipal.Console)) {
                        installedGrants.Add(item: grant);
                    }
                }

                foreach (var peer in admitted.Entries) {
                    var installedTemplates = AdmissionTemplatesFor(peer: peer, mintedGrants: installedGrants);

                    m_population.SetPeerAdmissionInstalledGrantTemplates(bodyIndex: peer.BodyIndex, grantTemplates: installedTemplates);
                }

                break;
            case WorldServerEvent.PeerDisconnected disconnected:
                foreach (var grant in disconnected.RevokedGrants) {
                    Revoke(grant: grant, actor: WorldPrincipal.Console);
                }

                foreach (var peer in disconnected.Entries) {
                    m_population.ApplyPeerDisconnected(peer: in peer, tick: NextInputTick);
                }

                break;
            default:
                Console.Error.WriteLine(value: $"[world.server-event refused: {serverEvent.GetType().Name} is not declared]");
                return;
        }

        ServerEventTap?.Invoke(obj: serverEvent);
    }

    // PeerAdmitted already records concrete minted rows. Stripping their generated principal reconstructs the exact
    // admission templates without duplicating those rows in the tape; the verified domain/subject on the peer entry
    // distinguishes a genuine zero-grant remote admission from ordinary simulated-population lifecycle events.
    private static IReadOnlyList<WorldAdmissionGrant> AdmissionTemplatesFor(WorldPeerEventEntry peer, IReadOnlyList<WorldGrant> mintedGrants) {
        if (string.IsNullOrEmpty(value: peer.IdentityDomain)) {
            return [];
        }

        var templates = new List<WorldAdmissionGrant>();

        foreach (var grant in mintedGrants) {
            if (grant.Principal != peer.Identity) {
                continue;
            }

            templates.Add(item: new WorldAdmissionGrant(Capability: grant.Capability, Subject: grant.Subject, Exclusive: grant.Exclusive, Budget: grant.Budget, EventBudget: grant.EventBudget, KindMask: grant.KindMask));
        }

        return templates;
    }

    private readonly record struct FederatedIntentState(long LeaseId, WorldPrincipal Principal, IntentSubmission Submission, bool Active);
}

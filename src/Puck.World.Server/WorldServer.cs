using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Defines the memory-peek seam that <c>WorldAddonRuntime</c> reads through, mirroring
/// <c>Puck.Abstractions.Machines.IMachineMemoryPeek</c>'s contract. <see cref="IWorldMachineHost"/>, reached via
/// <see cref="WorldServer.Machines"/>, is the only implementor; callers should not reach past this interface into
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

    /// <summary>A whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>) —
    /// stronger than an ordinary <see cref="Mutation"/>: every section may have moved, the journal always clears,
    /// and admitted peer connections re-mint their admission grant.</summary>
    Rebuild,

    /// <summary>A live screen-machine lifecycle change (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/
    /// <c>.options</c>/<c>.link</c>/<c>.unlink</c>) — runtime machine-host state, not a document edit.</summary>
    ScreenOp,

    /// <summary>A target-register designation outcome.</summary>
    Designation,

    /// <summary>A body motion program switch outcome (<c>body.motion</c>).</summary>
    BodyMotion,
}
/// <summary>One edit-boundary outcome echoed beside the loud stderr line — the payload of
/// <see cref="WorldServer.EchoTap"/>, so a UI surface (the overlay toast, the edit-echo cue lane) narrates outcomes
/// without scraping stderr.</summary>
/// <param name="Message">The human-readable outcome line (no brackets).</param>
/// <param name="Rejected">Whether the outcome is a rejection/denial.</param>
/// <param name="Kind">The edit-boundary class the outcome belongs to.</param>
/// <param name="Mutation">The mutation the outcome answers, when the boundary was a mutation — the at-site position
/// source the applied-cue lane derives from; <see langword="null"/> otherwise.</param>
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
public sealed partial class WorldServer : IWorldServerHost {
    // Federation reserve/commit requests arrive on socket workers. They must be able to settle while this host's
    // simulation thread waits on a different authority, otherwise simultaneous cyclic crossings deadlock.
    private readonly Lock m_authorityGate = new();
    // The per-tick dispatch tally behind TryAdmitMutation's budget gate — ONE meter for every untrusted ingress (a
    // mounted addon's guest and a remote peer alike), opened at the top of Step.
    private readonly WorldMutationBudgetMeter m_mutationBudget = new();
    // The multi-subscriber output hub — supports a local sink plus N future connections. See WorldOutputHub's own remarks.
    private readonly WorldOutputHub m_output = new();
    // The arena host every state effect and resolved transform fires through, rebuilt with the arena.
    private WorldArenaHost m_arenaHost = null!;

    // The tick facade: the intent and ordered queues, the step clock, the contribution fold and its read-back, the
    // engagement/transfer/field/music/response/board/deal per-tick work, and the snapshot it emits.
    private readonly WorldTick m_tick;
    // The persistence facade: journal undo, the authority checkpoint, the state hash, and the replay gate. It owns
    // no state — it walks the others.
    private readonly WorldPersistence m_persistence;
    // The document facade: the live definition, the journal and its base, the buffered live-edit ops, the compose
    // and apply pipeline, the solid field, and the delivery decision.
    private readonly WorldDocument m_document;
    // The rule facade: the evaluator, the compiled rules/groups/tables, the edge latches, the decision and pattern
    // runtimes, and the world facts and effect arms the evaluator reaches through.
    private readonly WorldRuleHost m_ruleHost;

    // The `search` jobs over the arena, rebuilt with the rules on every install.
    private ArenaSearch m_search;

    private readonly Func<IReadOnlyList<ArenaSearchWrite>, bool> m_searchApply;

    // Translates a landed job's writes into the ordinary mutation vocabulary: a single write installs directly, more
    // than one folds into a Batch, so a finished job's observable mutation shape is unchanged by the runtime split.
    private WorldMutation ComposeSearchMutation(IReadOnlyList<ArenaSearchWrite> writes) {
        if (writes.Count == 1) {
            return ToSearchMutation(write: writes[0]);
        }

        var mutations = new WorldMutation[writes.Count];

        for (var index = 0; (index < writes.Count); index++) {
            mutations[index] = ToSearchMutation(write: writes[index]);
        }

        return new WorldMutation.Batch(
            Principal: WorldPrincipal.World,
            Mutations: mutations
        );
    }
    // The one place a search's ordinals become names again: the mutation vocabulary is string-addressed.
    private WorldMutation ToSearchMutation(ArenaSearchWrite write) {
        var catalog = m_arena.Catalog;

        return (write switch {
            ArenaSearchWrite.Cell cell => new WorldMutation.UpsertStateCell(
            Key: m_arena.Keys[cell.Key].Value,
            Kind: WorldDocumentWriteKind.Set,
            Principal: WorldPrincipal.World,
            Row: catalog.Descriptors[cell.RowOrdinal].Name,
            Value: cell.Value
        ),
            ArenaSearchWrite.ClearBoard clear => new WorldMutation.TransformState(
            Principal: WorldPrincipal.World,
            Transform: new StateTransform.BoardCombine(
                Operation: BoardCombineOp.Clear,
                Row: StateChannelRef.OfName(name: catalog.Descriptors[clear.RowOrdinal].Name)
            )
        ),
            _ => throw new InvalidOperationException(message: $"unrecognized search write '{write.GetType().Name}'"),
        });
    }

    // The engagement fold — the seat/peer→screen route decision, its per-tick pad fold, and the
    // screen-removal admin cleanup. Assigned in the constructor (not a field initializer: it needs the constructor's
    // own population/definition parameters), never rebuilt afterward — channels are boot-fixed, so its compiled
    // per-screen translation tables never go stale.
    private readonly WorldEngagement m_engagement;
    private readonly WorldRenderEnvelope m_envelope;
    // The world-scoped event feed (four of the five senses-lane families — see WorldEventFeed's own remarks for why
    // the fifth, machine-memory watches, is addon-scoped instead). Collected once per Step, after the population
    // advances; drained by WorldAddonRuntime.ResolveReads the same tick.
    private readonly WorldEventFeed m_events;
    // The ONE capability table — every write boundary checks it. Seeded permissive for local play (see WorldGrants).
    private readonly WorldGrants m_grants;
    private readonly WorldInputHoldRuntime m_inputHold;
    // The authoritative screen-machine host — a PEER singleton
    // assigned in the constructor, never owned/disposed here (see IWorldMachineHost's own remarks).
    private readonly IWorldMachineHost m_machines;
    private readonly WorldPopulation m_population;
    private readonly WorldOwnedWorlds m_profiles;
    private readonly WorldTransferEscrow m_transferEscrow;

    // The mounted Simulation-lane guests, attached once at composition (see AttachAddons) and pumped at the three
    // pinned points of Step. Null until then, and null for the whole life of a server nobody mounts addons into (the
    // offline replay re-drive attaches its own).
    private IWorldAddonHost? m_addons;

    /// <summary>Gets the attached addon runtime's mount receipts, in mount order — empty when no runtime is attached (a
    /// world that enables no addon, or an offline re-drive read before its own mount). The record side of the replay
    /// tape reads this at record-start so a saved tape pins the guests it will re-run.</summary>
    public IReadOnlyList<WorldAddonReceipt> AddonReceipts => (m_addons?.Receipts ?? Array.Empty<WorldAddonReceipt>());
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
    /// <summary>Gets a value indicating whether any mounted addon has ever had an admitted execution attempted (a real
    /// <c>TickAddons</c> pump, not merely mounting) — a boot-anchored replay arm predicate.
    /// <c>false</c> for a world that mounts no addon, or one whose mounted addons never reached their first tick.
    /// Latched per mounted entry the first time it is pumped and never cleared — a guest's accumulated memory/tick
    /// state before a recording began is exactly what offline replay cannot re-establish (fresh guests, sim
    /// counter zero), so <c>replay.record</c> refuses to arm once this is <see langword="true"/>.</summary>
    public bool AnyAddonEverPumped => (m_addons?.AnyEverPumped ?? false);
    /// <summary>Gets a value indicating whether any booted screen machine has ever had a step/segment actually submitted — the identical
    /// boot-anchored replay arm predicate <see cref="AnyAddonEverPumped"/> applies to addons: offline replay
    /// rehydrates a fresh <see cref="IWorldMachineHost"/> from the tape's embedded
    /// definition, which can reconstruct a machine's boot image but never its accumulated core state (WRAM, CPU
    /// registers) once real ticks have run it. A world with a boot-declared cartridge means recording must arm
    /// before its first step, same as a world that mounts an addon must arm before its first tick.</summary>
    public bool AnyMachineEverPumped => m_machines.AnyEverPumped;
    /// <summary>Gets whether a screen operation reached host dispatch or a named provider operation reached its
    /// runtime commit barrier. This conservative, irreversible latch closes boot-only replay and checkpoint
    /// reconstruction even when a runtime operation faults after changing hardware. Screen operations before
    /// recording are absent from its authority tape; generic provider operations have no entry in the current
    /// replay format and are refused while its screen-operation tap is attached.</summary>
    public bool AnyScreenOpEverApplied { get; private set; }
    /// <summary>Gets the namespace used by durable entity addresses emitted by this authority. A federated
    /// authority uses its declared network identity so two processes whose local instance is named <c>boot</c>
    /// cannot publish colliding addresses; a loopback-only authority uses its process-local instance identity.</summary>
    public string AuthorityIdentity { get; }
    /// <summary>Gets the derived-face screen slots this instance's boot document reserved. The presentation binder
    /// registers exactly that band up front and the render provider key set is frozen there, so a live edit may lower
    /// <see cref="WorldPlacementPolicyDefaults.DerivedFaceScreens"/> but never raise it past this — a raise is refused by
    /// name, in the same family as the boot-allocated population capacity, rather than seating faces at indices no
    /// renderer holds.</summary>
    public int BootDerivedFaceScreens { get; }
    /// <summary>Gets the exact engine-time boundary completed by the latest authoritative step.</summary>
    public ulong CompletedEngineTicks => m_tick.CompletedEngineTicks;
    /// <summary>Gets the live world definition this server runs — swapped in place as buffered edits apply.</summary>
    public WorldDefinition Definition => m_document.Definition;
    /// <summary>Observes each visiting-world durable-state verdict for a tape.</summary>
    public Action<WorldDocumentSubmissionReceipt>? DocumentSubmissionTap { get; set; }
    /// <summary>Gets the optional host sink for the player-keyed durable writes emitted by each completed tick.</summary>
    public Action<IReadOnlyList<DurableStateOutput>>? DurableStateOutputTap { get; set; }
    /// <summary>Gets the durable writes emitted by the most recently completed tick.</summary>
    public IReadOnlyList<DurableStateOutput> DurableStateOutputs => m_population.DurableStateOutputs;
    /// <summary>Gets an optional edit-echo tap invoked beside the loud stderr accept/reject lines — mutation outcomes,
    /// grant/revoke outcomes, and their document-only class — so a UI surface (the overlay toast, the editor HUD)
    /// narrates them without scraping stderr. Fires synchronously inline with the apply, never from a background
    /// thread: at submit-time for the ordered-domain kinds applied inline (grant, revoke, command, designation,
    /// composition, screen op), and inside <see cref="Step"/> for the kinds buffered to the tick boundary (mutation,
    /// rebuild, undo, addon lifecycle) and for a fired world-rule effect.</summary>
    public Action<WorldEditEcho>? EchoTap { get; set; }
    /// <summary>Observes deterministic presentation-neutral cues emitted by world rules. The callback runs
    /// synchronously on the tick thread; consumers must hand off any presentation work without blocking it.</summary>
    public Action<WorldGameplayCue>? GameplayCueTap { get; set; }
    /// <summary>Gets the engagement fold (headless design §1.8) — the seat/peer→screen route decision
    /// (<see cref="WorldCommand.ComposeControl"/>/<see cref="WorldCommand.DissolveControl"/> apply through it, from
    /// <see cref="ApplyCommand"/>), its per-tick pad fold (<see cref="Server.WorldEngagement.FoldTick"/>, folded into
    /// every <see cref="WorldSnapshot"/>), and the screen-removal admin cleanup
    /// (<c>Puck.World.WorldScreenBinder.ReconcileScreens</c> calls <see cref="Server.WorldEngagement.DissolveScreen"/>
    /// directly — loopback-only, like every other client↔server call that has not yet crossed a wire).</summary>
    public WorldEngagement Engagement => m_engagement;
    /// <summary>Gets this instance's own render-capacity oracle — configured by whatever presentation-side content
    /// source renders this instance (the boot world's own <c>WorldFramePresenter</c>, or an observing destination's
    /// session or continuum view), so a document mutation the same instance receives is checked against the same
    /// probed floor a renderer already committed to. Unconfigured (nothing renders this instance yet) reads as
    /// "fits" — <see cref="WorldRenderEnvelope"/>'s own documented default.</summary>
    public WorldRenderEnvelope Envelope => m_envelope;
    /// <summary>Gets the world-scoped event feed — the four senses-lane families collected once per <see cref="Step"/>
    /// (collision pairs, region enter/exit, seat join/leave, route/engagement transitions). Read by
    /// <see cref="IWorldAddonHost"/>'s read pump; a diagnostic/delivery surface, never itself hashed (the
    /// underlying state it derives from already is).</summary>
    public WorldEventFeed Events => m_events;
    /// <summary>Gets the capability table's <see cref="IWorldGrantsView"/> — the one grant primitive the engagement view, the
    /// addon runtime, and the grant/mutation command modules read (plus the two engagement-route writes the view
    /// carries). Reads are loopback-local today; a socket transport moves grant changes onto the wire. Deliberately not
    /// the concrete <see cref="WorldGrants"/>: its <see cref="WorldGrants.TryGrant"/>/<see cref="WorldGrants.Revoke"/>
    /// authority doors stay reachable only through <see cref="Grant"/>/<see cref="Revoke"/> below, which run the actor
    /// check those two methods do not — a caller that only holds this property can never skip it.</summary>
    public IWorldGrantsView Grants => m_grants;
    /// <summary>Gets this server's own running-instance identity — the draw seed ladder's instance rung (see
    /// <c>GeneratorEngine.ComputeSeedState</c>). A live redraw folds the same value the boot/first-fill resolver
    /// used, so a site's first fill and its later redraws share one deterministic stream per instance.</summary>
    public string InstanceIdentity { get; }
    /// <summary>Gets the journal length — the number of applied mutations over the base (the <c>world.status</c> dirty
    /// count).</summary>
    public int JournalLength => m_document.JournalLength;
    /// <summary>Gets the width of the latest authoritative step, or zero before the first step.</summary>
    public ulong LastStepTicks => m_tick.LastStepTicks;
    /// <summary>Gets the authoritative screen-machine host — owns every booted <c>IMachineRuntime</c>, its memory-peek
    /// surface (<see cref="IWorldMachineHost"/> extends <see cref="IWorldMachineMemoryPeek"/> directly), and the
    /// screen-op verb surface's runtime target. Always present (never null): machines are booted and stepped in
    /// every boot shape.</summary>
    public IWorldMachineHost Machines => m_machines;
    /// <summary>Gets or sets an optional durable-journal tap fired with a mutation's own tick and engine tick right
    /// after it is applied and folded into the in-memory undo journal — the same call site, so an entry this tap
    /// sees is exactly the entry a restart's journal-tail replay reapplies. Fires synchronously on the tick thread;
    /// the composition-owned callee is expected to hand the entry off to its own store write without blocking here
    /// (the checkpoint upload's own fire-and-forget shape). <see langword="null"/> (the default) journals nothing —
    /// a desktop row has no durable store to append to.</summary>
    public Action<ulong, ulong, WorldMutation>? MutationJournalTap { get; set; }
    /// <summary>Observes every SUBMITTED document mutation as <see cref="WorldDocument.ApplyEnvelope"/> dispatches it, carrying the
    /// mutation and the envelope's own acting principal. The one ingress every submission kind shares — a local
    /// console/client write over the loopback, an admitted socket peer's, and a traveller's submission forwarded by
    /// its source authority all reach the tape here, with the true actor the envelope stamped. Deliberately NOT the
    /// two internal producers that reach <see cref="EnqueueMutation"/> directly (a mounted guest's decoded act, a
    /// world rule's <c>generate</c> effect): both re-derive during a replay drive, so taping them would apply each
    /// one twice. A mutation the apply pipeline goes on to refuse is still observed, so the refusal reproduces
    /// identically. The replay tape attaches only while armed; clients never receive this submission-only
    /// seam.</summary>
    public Action<WorldMutation, WorldPrincipal>? MutationTap { get; set; }
    /// <summary>Observes the accept/refuse OUTCOME of a mutation <see cref="MutationTap"/> already observed at
    /// submission, invoked once the SAME tick's <see cref="Step"/> has drained and applied it — never for the two
    /// internal producers <see cref="MutationTap"/> itself excludes (a mounted guest's decoded act, a world rule's
    /// <c>generate</c> effect), because only <see cref="WorldDocument.ApplyEnvelope"/>'s own dispatch threads the completion
    /// callback that reaches this tap; those two producers call <see cref="EnqueueMutation"/> directly with none.
    /// This is what lets a recorded mutation's outcome be pinned on tape and a replay's disagreement — accepted live
    /// but refused on replay, or the reverse — be told apart from an ordinary later-tick pose drift. The replay tape
    /// attaches only while armed; clients never receive this submission-only seam.</summary>
    public Action<WorldMutation, bool>? MutationOutcomeTap { get; set; }
    /// <summary>Observes each authored <c>adjacencies</c> row that received a delivered neighbour refresh this tick,
    /// by row name, at the pinned point in <see cref="Step"/> where the adjacency source has just frozen the tick's
    /// projection graph. The one taped link-liveness input — see <see cref="WorldEventFeed"/>'s own remarks. The
    /// replay tape attaches only while armed.</summary>
    public Action<string>? LinkDeliveryTap { get; set; }
    /// <summary>Gets or sets the injected neighbour resolver <see cref="WorldDefinitionValidator.Validate"/> reads
    /// for a cross-document adjacency proof
    /// — the same "the server calls out, the composition root supplies the capability" shape as <see cref="EchoTap"/>/
    /// <see cref="SaveEffectTap"/>, and for the identical reason: <c>Puck.World.Server</c> carries no storage
    /// or filesystem dependency, so it cannot construct either resolver itself. Read only during synchronous
    /// submission/load preparation, before a rebuild is buffered: live wiring supplies a local-first composite whose
    /// second resolver is the storage transport. <see cref="WorldDocument.ApplyRebuild"/> runs later from <see cref="Step"/> and
    /// deliberately performs document-local validation only, so neither the composite nor its blocking storage read
    /// is reachable from the tick path. The resolver is likewise never read from <see cref="TryApplyMutation"/> or
    /// <see cref="WorldPersistence.ApplyUndo"/>: cross-document border compatibility is proved once at load, never re-litigated per
    /// mutation or journal entry. <see langword="null"/> (the default) is correct for an offline replay drive with no
    /// reachable transport and means an authored adjacency refuses by name for want of proof.</summary>
    public IWorldNeighbourResolver? Neighbours { get; set; }
    /// <summary>Gets the tick a durable input submitted during the current command window must name.</summary>
    public ulong NextInputTick => (m_tick.CompletedTick + 1UL);
    /// <summary>Gets the entity table this server advances.</summary>
    public WorldPopulation Population => m_population;
    /// <summary>Gets the profile catalog (the routed store persists through it).</summary>
    public WorldOwnedWorlds Profiles => m_profiles;
    /// <summary>Gets or sets the composition-owned factory for proving a replacement document relative to that
    /// candidate's own origin rather than the currently loaded document. The path is the candidate's full path.</summary>
    public Func<string, IWorldNeighbourResolver?>? RebuildNeighbours { get; set; }
    /// <summary>Gets or sets the composition-owned source a <c>world.load</c>/<c>world.reload</c> document and a replay
    /// re-read of one are read through, so an origin this project cannot parse itself (a <c>.puck</c> source) loads
    /// and pins identically live and on replay. <see langword="null"/> reads JSON files directly.</summary>
    public IWorldDocumentSource? RebuildDocuments { get; set; }
    /// <summary>Observes a whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>)
    /// once <see cref="WorldDocument.ApplyRebuild"/> has resolved its candidate and computed its CAS content hash, but before any
    /// refusal gate (grant check, dirty-journal guard, validate, capacity, solids) runs — so a rebuild the door goes
    /// on to refuse is still taped and reproduces as the identical refusal on replay, matching
    /// <see cref="ServerEventTap"/>/<c>LoopbackTransport</c>'s own taps. Apply-time, not submission-time, because
    /// Reset's hash (the base's own canonical bytes) is only knowable once <see cref="WorldDocument.ApplyRebuild"/> actually reads
    /// <c>m_base</c> — a value private to this server that can move between submission and drain (see
    /// <see cref="EnqueueRebuild"/>'s remarks). The replay tape attaches only while armed; clients never receive this
    /// submission-only seam.</summary>
    public Action<WorldRebuildRequest, WorldPrincipal, string>? RebuildTap { get; set; }
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
    /// than left indistinguishable from a rule that never fired. See <c>WorldEffect.Save</c>'s remarks for why this
    /// effect submits no <see cref="WorldMutation"/> and so needs a seam other than <c>TryApplyMutation</c> at
    /// all.</summary>
    public Action<ulong>? SaveEffectTap { get; set; }
    /// <summary>Observes every screen op (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/<c>.options</c>/
    /// <c>.link</c>/<c>.unlink</c>) after <see cref="WorldDocument.ApplyScreenOp"/> finishes — success or refusal alike, so a
    /// screen op the Control-authority gate refuses is still taped and reproduces as the identical refusal on
    /// replay (the same property <see cref="RebuildTap"/> establishes, reached here by a simpler route: unlike a
    /// rebuild, a screen op applies synchronously with no drain-time gap for its content to move between submission
    /// and apply, so the authority gate runs first, before any content is read). The carried content hash is
    /// non-null only for a successful <see cref="WorldScreenOp.Insert"/> — every other op kind and every refusal
    /// carries <see langword="null"/>, since nothing else on the tape needs a CAS pin (see
    /// <see cref="WorldScreenOp.Insert"/>'s own remarks on why <see cref="WorldScreenOp.Select"/> needs none). The
    /// replay tape attaches only while armed; clients never receive this submission-only seam.</summary>
    public Action<WorldScreenOp, string?, WorldPrincipal>? ScreenOpTap { get; set; }
    /// <summary>Observes server-authored ordered events after they take effect. The replay tape attaches only while
    /// armed; clients never receive this submission-only seam.</summary>
    public Action<WorldServerEvent>? ServerEventTap { get; set; }
    /// <summary>Gets the live SDF contact field under the field provider, or <see langword="null"/> under the analytic
    /// provider — the <c>world.collision.probe</c>/<c>world.collision.status</c> reads' window onto the
    /// surface the simulation itself solves against.</summary>
    public WorldSolidField? SolidField => m_document.SolidField;
    /// <summary>Gets the solid-field revision — bumped each time the field is rebuilt (a solid-affecting edit under the field
    /// provider). The <c>world.collision.status</c> read-back.</summary>
    public int SolidRevision => m_document.SolidRevision;
    /// <summary>Gets or sets the composition-root route for a federated peer that subsequently leaves this
    /// authority. Null when this server is not hosted by a multi-authority composition.</summary>
    public IWorldTransferForwarder? TransferForwarder { get; set; }
    /// <summary>Reads bounded transfer transaction, mobility credential, and in-flight mobility-lease counts.</summary>
    public WorldTransferTableCounts TransferTableCounts =>
        ExecuteAuthorityOperation(operation: () => m_transferEscrow.Counts);

    /// <summary>Returns the body at a 0-based entity index, or <see langword="null"/> when the index holds no live body.</summary>
    /// <param name="index">The 0-based entity index.</param>
    public WorldBody? Body(int index) => ((((uint)index) < ((uint)m_population.Capacity))
        ? m_population.EntryBody(index: index)
        : null
    );
    /// <summary>Compacts the journal: the live definition becomes the new base and the edit history is cleared (the
    /// <c>world.save</c> half — a saved world is clean). Reads/writes only journal state, so it runs on the Immediate
    /// console path behind the stdin barrier.</summary>
    public void Compact() => m_document.Compact();
    /// <summary>Executes a narrow authority operation without racing the fixed-step population fold.</summary>
    /// <exception cref="InvalidOperationException">This activation has frozen for retirement.</exception>
    public T ExecuteAuthorityOperation<T>(Func<T> operation) {
        ArgumentNullException.ThrowIfNull(operation);
        lock (m_authorityGate) { ThrowIfAuthorityRetiring(); return operation(); }
    }
    /// <summary>Executes a void authority operation under the same gate.</summary>
    /// <exception cref="InvalidOperationException">This activation has frozen for retirement.</exception>
    public void ExecuteAuthorityOperation(Action operation) {
        ArgumentNullException.ThrowIfNull(operation);
        lock (m_authorityGate) { ThrowIfAuthorityRetiring(); operation(); }
    }

    /// <summary>Initializes a new instance of the <see cref="WorldServer"/> class over the world it authoritatively owns.</summary>
    /// <param name="definition">The loaded world definition (the initial live definition and journal base).</param>
    /// <param name="population">The entity table (all bodies, seats included).</param>
    /// <param name="profiles">The profile catalog.</param>
    /// <param name="envelope">The render-capacity oracle a scene/screen mutation is checked against at apply time.</param>
    /// <param name="machines">The authoritative screen-machine host (owns every booted <c>IMachineRuntime</c>) — a
    /// peer singleton, not a private field this constructor builds, so the composition root disposes it (see
    /// <see cref="IWorldMachineHost"/>'s own remarks on why).</param>
    /// <param name="instanceIdentity">This server's own running-instance identity — the draw seed ladder's instance
    /// rung (see <c>GeneratorEngine.ComputeSeedState</c> in <c>Puck.World.Schema</c>). Defaults to the boot
    /// instance's own constant name (<c>Puck.World.WorldInstanceHost.BootInstanceName</c>, not referenced directly —
    /// this project sits below <c>Puck.World</c> in the layering).</param>
    /// <param name="narrationSink">A sink attached to this server's narration before construction narrates anything
    /// of its own (the document's authored grants, seeded here) — a composition root that only attaches after
    /// construction returns would miss every line construction itself writes.</param>
    /// <param name="admission">An unchanged definition's validation result from this construction operation,
    /// or null to validate here. It must name this exact definition and the machine host's catalog.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="instanceIdentity"/> is empty, admission or machine
    /// preparation refuses, or the supplied admission names a different definition or machine catalog.</exception>
    public WorldServer(WorldDefinition definition, WorldPopulation population, WorldOwnedWorlds profiles, WorldRenderEnvelope envelope, IWorldMachineHost machines, string instanceIdentity = "boot", IWorldNarrationSink? narrationSink = null,
        WorldDefinitionAdmission? admission = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: envelope);
        ArgumentNullException.ThrowIfNull(argument: machines);
        ArgumentException.ThrowIfNullOrEmpty(argument: instanceIdentity);

        if ((admission is not null) && !admission.AppliesTo(definition: definition, machines: machines.ValidationCatalog)) {
            throw new ArgumentException(message: "The admission result belongs to a different definition or machine catalog.", paramName: nameof(admission));
        }
        if ((admission is null) && !WorldDefinitionValidator.TryAdmitLocally(
            admission: out admission,
            definition: definition,
            machines: machines.ValidationCatalog,
            reason: out var machineAdmissionReason
        )) {
            throw new ArgumentException(
                message: $"World machine admission refused: {machineAdmissionReason}",
                paramName: nameof(definition)
            );
        }
        var compilation = admission.Compilation;

        if (narrationSink is not null) {
            _ = m_output.AttachNarrationSink(sink: narrationSink);
        }

        Extensions = new WorldExtensions(server: this);
        InstanceIdentity = instanceIdentity;
        AuthorityIdentity = ((definition.Host.Authority is { Length: > 0 } authority)
            ? authority
            : instanceIdentity
        );
        BootDerivedFaceScreens = definition.Authoring.DerivedFaceScreens;
        m_machines = machines;
        if (!machines.TryPrepare(
            admission: admission,
            candidate: definition,
            current: null,
            plan: out var machinePlan,
            reason: out var machineReason
        )) {
            throw new ArgumentException(
                message: $"World machine preparation refused: {machineReason}",
                paramName: nameof(definition)
            );
        }
        using (machinePlan) {
            machines.Commit(plan: machinePlan!);
            machines.Finish(plan: machinePlan!);
        }
        // Assigned before the arena is built: the build binds the population's action-state slot lanes.
        m_population = population;
        m_events = new WorldEventFeed(capacity: population.Capacity);
        m_tick = new WorldTick(
            definition: definition,
            host: this
        );

        m_document = new WorldDocument(
            definition: definition,
            host: this
        );
        m_persistence = new WorldPersistence(host: this);
        m_ruleHost = new WorldRuleHost(host: this);
        m_ruleHost.InstallTables(compilation: compilation!);
        BuildArena(definition: definition);
        m_search = CreateSearch(arena: m_arena);
        m_searchApply = writes => TryApplyMutation(
            mutation: ComposeSearchMutation(writes: writes),
            tick: m_searchTick,
            engineTick: CompletedEngineTicks,
            connectionId: SubmissionEnvelope.LocalConnectionId,
            correlationId: 0,
            preMetered: false
        );


        m_grants = new WorldGrants(
            seatCount: population.LocalSeatCount,
            population: population.Capacity,
            routeTransition: m_tick.QueueRouteTransition,
            host: this
        );
        // The group+membership+ownership substrate's own sync, run here (BEFORE the document's shipped grants
        // replay below — a document-authored row naming a group: principal needs the group table settled first)
        // since Install never runs at construction, same reasoning as the RecompileRules/ReconcileLinks calls at the
        // end of this ctor. The drive-gate index (Seam A) syncs alongside it for the identical reason — a boot
        // document may ship an already-gated body (a scenario opening on a downed NPC), and the first Step's intent
        // drain must see it without waiting for a mutation to trigger Install.
        m_grants.SyncGroups(
            groups: (definition.Groups ?? WorldGroupsSection.Empty).Groups,
            kinds: (definition.Groups ?? WorldGroupsSection.Empty).Kinds,
            ownership: (definition.Groups ?? WorldGroupsSection.Empty).Ownership
        );
        m_grants.SyncState(definition: definition);

        // THE BOOT-LOUD CATALOG CHECK: WorldServer is constructed exactly once per world boot (or per replay
        // rehydration), so this is the "at startup" hook the kind catalog's own remarks call for — a broken catalog
        // (a duplicate or out-of-range ordinal, a kind missing its attribute) throws HERE, before any session starts,
        // rather than surfacing lazily the first time something reads it.
        WorldMutationKindCatalog.Validate();

        population.NarrationHub = m_output;
        m_inputHold = new WorldInputHoldRuntime(
            settings: definition.CompiledInputHold,
            capacity: population.Capacity
        );
        m_profiles = profiles;
        m_transferEscrow = new WorldTransferEscrow(server: this);
        m_envelope = envelope;
        // Adopt the population's boot-built field (the field provider compiled it once for the bodies it minted at
        // construction) — the server owns it from here without a second build.
        m_document.RestoreSolidField(
            revision: ((population.SolidField is null)
            ? 0
            : 1),
            solids: population.SolidField
        );
        // The engagement fold — over the population and THIS server's own grant table (m_grants was assigned earlier
        // in this constructor body, at the WorldGrants construction above). Never rebuilt: channels are boot-fixed.
        m_engagement = new WorldEngagement(
            definition: definition,
            grants: m_grants,
            population: population
        );
        // Join the bodies the boot definition's inhabited placements declare into free peer slots (the population
        // constructor activates nothing — the boot census is zero, the whole peer slice is free). Every later Install
        // re-runs this after Rebuild.
        var admittedAtBoot = new List<WorldPeerEventEntry>();
        var disconnectedAtBoot = new List<WorldPeerEventEntry>();

        m_population.ReconcileInhabitants(
            admitted: admittedAtBoot,
            definition: definition,
            disconnected: disconnectedAtBoot
        );
        // A cell-driven inhabit count starts from its cell's authored value, not from zero waiting for a write.
        m_population.ReconcileInhabitCounts(
            admitted: admittedAtBoot,
            definition: definition,
            disconnected: disconnectedAtBoot,
            tick: NextInputTick
        );
        m_grants.ApplyLifecycleEvents(
            admitted: admittedAtBoot,
            disconnected: disconnectedAtBoot,
            ordered: false
        );

        // The document's SHIPPED grants: applied AFTER the permissive seed, in document order, through the identical Grant() path
        // world.grant submits through — so an illegitimate or conflicting authored row prints the same loud accept/
        // reject line an operator would see typing it, rather than being silently seated or silently dropped. Empty
        // (every world authored before this section existed) prints nothing and changes nothing. Applied BEFORE any
        // addon mounts (WorldAddonRuntime.Create runs after this constructor returns), so a mount-time requested-vs-
        // granted report already sees the settled table.
        foreach (var grant in definition.Grants) {
            if (WorldTick.IsDocumentChannelRow(grant: grant)) {
                continue;
            }

            Grant(
                grant: m_grants.WithoutAuthoredConsent(grant: grant),
                actor: WorldPrincipal.Console
            );
        }

        // Establish the boot document's OWN declared cable groups: Install (which ALSO calls this on every later
        // mutation/rebuild) never runs at construction, so a cable port authored in the boot document itself needs
        // this one extra call here, or it would never establish until the first live edit touched ANY section —
        // headless included, since nothing presentation-side ever called it either.
        m_machines.ReconcileLinks(links: definition.MachineCableGroups());

        // The initial arena already settled clocks and inverse boards. Export those results from that same arena,
        // then install it directly: constructing and loading another copy would repeat the entire state seed.
        // Admission's programs remain reusable only when settlement keeps the exact definition unchanged.
        definition = SettleInstalledRows(definition: definition, seeded: m_arena);
        definition = RecompileRules(definition: definition, compilation: compilation, arena: m_arena);
        // The lattice exists (the population allocated it) and the instance identity is known only from here on, so
        // this is the first point a lattice row's draw fill can be seeded through the site ladder and painted.
        m_tick.PaintLatticeDraws(definition: definition);
        // AFTER ReconcileInhabitants above: every boot-declared body (seats and inhabited placements alike) now has
        // a WorldBody to resync bodies.scaleRow's cells onto.
        m_population.SyncBodyScale(definition: definition);
    }

}

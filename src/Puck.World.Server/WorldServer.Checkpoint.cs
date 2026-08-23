using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    // Undo the last `count` applied mutations (default clamps to 1): restore the base and deterministically replay the
    // journal minus its tail through the SAME per-entry gates a live mutation passes — compose, whole-document
    // validate, render-envelope capacity, and solid-field buildability — everything but the authority check (the
    // every-section Mutate hold below already re-proves authority for the whole undo, so no per-entry grant lookup is
    // needed). The replay is ALL-OR-NOTHING: any entry failing any gate refuses the undo outright, names the failing
    // entry's index and reason on stderr, and installs NOTHING — a validated prefix is not a validated document, and no
    // general admissibility invariant lets a partially-replayed journal stand in for one that fully replayed.
    private bool ApplyUndo(int count, WorldPrincipal principal, int connectionId, long correlationId) {
        // Journal control is Mutate territory over every section (a replay can rebuild any).
        if (!m_grants.AllowsAllSections(
            capability: WorldCapability.Mutate,
            denial: out var undoVerdict,
            deniedSection: out var undoSection,
            principal: principal
        )) {
            var denial = $"{principal.Describe()} cannot mutate every section (section:{undoSection.ToString().ToLowerInvariant()} — {undoVerdict.DescribeDenial()}) — world.undo dropped";

            Console.Error.WriteLine(value: $"[world.grant denied: {denial}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: denial,
                Rejected: true,
                Kind: WorldEditEchoKind.Mutation,
                Denied: true,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return false;
        }

        if (m_journal.Count == 0) {
            Console.Error.WriteLine(value: "[world.undo: nothing to undo]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: "undo refused: nothing to undo",
                Rejected: true,
                Kind: WorldEditEchoKind.Mutation,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return false;
        }

        var drop = Math.Clamp(
            value: count,
            min: 1,
            max: m_journal.Count
        );
        var keep = (m_journal.Count - drop);
        var candidate = m_base;
        var kept = new List<JournalEntry>(capacity: keep);

        for (var index = 0; (index < keep); index++) {
            var entry = m_journal[index];

            if (!TryCompose(
                current: candidate,
                mutation: entry.Mutation,
                tick: entry.Tick,
                instanceIdentity: InstanceIdentity,
                candidate: out var next,
                reason: out var composeReason,
                evictedKey: out _
            )) {
                var composeRefusal = $"undo refused: replay failed at journal entry {index} ({Describe(mutation: entry.Mutation)}) — {composeReason}";

                Console.Error.WriteLine(value: $"[world.undo: {composeRefusal}]");
                EchoTap?.Invoke(obj: new WorldEditEcho(
                    Message: composeRefusal,
                    Rejected: true,
                    Kind: WorldEditEchoKind.Mutation,
                    ConnectionId: connectionId,
                    CorrelationId: correlationId
                ));

                return false;
            }

            // An advancing row's epoch re-bases to the ORIGINAL journal tick it was set at, exactly as it did on the
            // live apply this replays — see RebaseAdvanceEpoch's remarks. Doing this BEFORE revalidation is what lets
            // world.undo rewind a regen row's accumulation bit-identically, same as it already does for a generator's
            // $cursor.
            next = RebaseAdvanceEpoch(
                original: candidate,
                candidate: next,
                mutation: entry.Mutation,
                tick: entry.Tick
            );

            // Cross-document claims were proved before the journal was admitted; replay repeats only local checks.
            // Addon preparation joins these all-or-nothing gates: an intermediate candidate this pass builds but
            // never installs still owes proof it COULD have mounted, because a kept entry whose pinned module has
            // since gone missing must refuse the WHOLE undo rather than silently landing on a document that would
            // boot differently than the one it names. The probe plan is disposed immediately either way — see
            // AddonsCanPrepare.
            if (
                !WorldDefinitionValidator.TryValidateLocally(
                definition: next,
                reason: out var reason
            ) ||
                (AffectsRenderEnvelope(mutation: entry.Mutation) && !m_envelope.TryFit(
                candidate: next,
                reason: out reason
            )) ||
                (AffectsSolidField(mutation: entry.Mutation) && !TryBuildSolids(
                definition: next,
                reason: out reason,
                solids: out _
            )) ||
                (AffectsAddons(mutation: entry.Mutation) && !AddonsCanPrepare(
                candidate: next,
                reason: out reason
            ))
            ) {
                var refusal = $"undo refused: replay failed at journal entry {index} ({Describe(mutation: entry.Mutation)}) — {reason}";

                Console.Error.WriteLine(value: $"[world.undo: {refusal}]");
                EchoTap?.Invoke(obj: new WorldEditEcho(
                    Message: refusal,
                    Rejected: true,
                    Kind: WorldEditEchoKind.Mutation,
                    ConnectionId: connectionId,
                    CorrelationId: correlationId
                ));

                return false;
            }

            candidate = next;
            kept.Add(item: entry);
        }

        // The full replay validated every entry above, so this rebuild is expected to succeed; still checked and
        // still loud on failure rather than installing a half-built field, for the same reason the loop above refuses
        // rather than tolerates: no step here is allowed to half-apply.
        if (!TryBuildSolids(
            definition: candidate,
            reason: out var undoSolidReason,
            solids: out var undoSolids
        )) {
            var refusal = $"undo refused: solid field rebuild failed — {undoSolidReason}";

            Console.Error.WriteLine(value: $"[world.undo: {refusal}]");
            EchoTap?.Invoke(obj: new WorldEditEcho(
                Message: refusal,
                Rejected: true,
                Kind: WorldEditEchoKind.Mutation,
                ConnectionId: connectionId,
                CorrelationId: correlationId
            ));

            return false;
        }

        // The final current-to-candidate reconcile: unconditional (never gated on whether the kept journal touched
        // Addons), because TryPrepare's own structural diff against the live m_mounted set already answers "does
        // anything about addons actually differ" cheaply on its own — a restored document whose addon rows are
        // structurally the ones already mounted reuses every guest's memory untouched. Commits only after Install
        // succeeds, mirroring TryApplyMutation's identical gate-then-commit shape.
        IWorldAddonPreparedPlan? addonPlan = null;
        int[]? newTickWrittenEntity = null;
        WorldPrincipal[]? newTickWrittenPrincipal = null;
        bool[]? newTickCollided = null;
        var addonPlanCommitted = false;

        // The whole sequence from here through Commit runs under ONE try/finally — see TryApplyMutation's identical
        // shape for why: addonPlan starts null, so a refusal before TryPrepare ever succeeds leaves the finally a
        // no-op, and a downstream throw from contention-array staging, Install, or Commit alike still disposes an
        // uncommitted plan.
        try {
            if (m_addons is { } addonsForUndo) {
                if (!addonsForUndo.TryPrepare(
                    current: m_definition,
                    candidate: candidate,
                    plan: out addonPlan,
                    reason: out var addonReason
                )) {
                    var refusal = $"undo refused: the restored document's addon {addonReason}";

                    Console.Error.WriteLine(value: $"[world.undo: {refusal}]");
                    EchoTap?.Invoke(obj: new WorldEditEcho(
                        Message: refusal,
                        Rejected: true,
                        Kind: WorldEditEchoKind.Mutation,
                        ConnectionId: connectionId,
                        CorrelationId: correlationId
                    ));

                    return false;
                }

                if (addonPlan is not null) {
                    StageAddonContentionArrays(
                        mountedCount: addonPlan.MountedCount,
                        entity: out newTickWrittenEntity,
                        principal: out newTickWrittenPrincipal,
                        collided: out newTickCollided
                    );
                }
            }

            SwapSolids(solids: undoSolids);
            Install(
                definition: candidate,
                rebuildPopulation: true
            );

            if (addonPlan is not null) {
                m_addons!.Commit(plan: addonPlan);
                addonPlanCommitted = true;

                if (newTickWrittenEntity is not null) {
                    m_tickWrittenEntity = newTickWrittenEntity;
                    m_tickWrittenPrincipal = newTickWrittenPrincipal!;
                    m_tickCollided = newTickCollided!;
                }
            }
        } finally {
            if (!addonPlanCommitted) {
                addonPlan?.Dispose();
            }
        }

        m_journal.Clear();
        m_journal.AddRange(collection: kept);
        Console.Error.WriteLine(value: $"[world.undo: dropped {drop}, {m_journal.Count} remaining]");

        if (addonPlanCommitted) {
            m_addons!.Finish(plan: addonPlan!);
        }

        return true;
    }
    // Undo's own throwaway addon-prepare probe for an INTERMEDIATE journal-replay candidate: proves the row set
    // this candidate carries could still mount, without ever registering, disclosing, or journaling anything — the
    // plan is disposed immediately regardless of outcome. Only the FINAL candidate's prepare (after the loop above)
    // ever actually commits. A server with no addon runtime attached vacuously succeeds.
    private bool AddonsCanPrepare(WorldDefinition candidate, out string reason) {
        if (m_addons is not { } addons) {
            reason = string.Empty;

            return true;
        }

        if (addons.TryPrepare(
            current: m_definition,
            candidate: candidate,
            plan: out var plan,
            reason: out var addonReason
        )) {
            plan?.Dispose();
            reason = string.Empty;

            return true;
        }

        reason = (addonReason ?? string.Empty);

        return false;
    }

    /// <summary>Buffers a journal undo of the last <paramref name="count"/> mutations for the next <see cref="Step"/>.
    /// Retains the submitting envelope's connection/correlation identity — see <see cref="EnqueueMutation"/>'s own
    /// remarks.</summary>
    /// <param name="count">How many trailing mutations to undo (clamped to at least 1 and at most the journal length).</param>
    /// <param name="principal">The acting identity the undo is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    public void EnqueueUndo(int count, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        m_pending.Enqueue(item: new PendingOp.Undo(
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            Count: count,
            Principal: principal
        ));
    }

    /// <summary>This server's own checkpointed fields — journal, base/definition documents, buffered pending ops,
    /// step clock, and the rule-edge latches. Every other subsystem's own section lives beside this one on
    /// <see cref="WorldAuthorityCheckpoint"/>.</summary>
    public sealed record WorldServerCheckpoint(
        byte[] DefinitionJson,
        byte[] BaseDefinitionJson,
        string BaseOrigin,
        IReadOnlyList<(ulong Tick, WorldMutation Mutation)> Journal,
        ulong LastCompletedTick,
        ulong LastCompletedEngineTicks,
        ulong LastStepTicks,
        IReadOnlyList<IntentSubmission> Intents,
        IReadOnlyList<WorldPendingOpCheckpoint> Pending,
        IReadOnlyList<(string Rule, bool Held)> RuleGateHeld,
        IReadOnlyList<(string Interaction, bool Held)> InteractionGateHeld,
        WorldDocumentSubmissionReceipt? LastDocumentReceipt,
        int SolidRevision,
        ulong? MusicClockElapsedTicks,
        string? MusicDirectorCurrentSegmentId,
        Puck.Audio.Simulation.MusicTransition? MusicDirectorArmed,
        ulong MusicDirectorTransitionCount,
        ulong? MusicDirectorLastTransitionTick,
        string? MusicDirectorLastTransitionFromSegmentId,
        string? MusicDirectorLastTransitionToSegmentId,
        IReadOnlyList<(int EntityIndex, string JudgeRef, string? Grade, ulong Tick)> JudgeGrades
    );

    /// <summary>The engine-tick threshold beyond which a checkpoint capture is refused rather than silently taken
    /// against state this record graph cannot represent — see <see cref="TryCaptureCheckpoint"/>.</summary>
    /// <returns><see langword="true"/> when this server's live state is outside what a checkpoint can capture.</returns>
    private bool AnyUncapturableStateEverLatched() => (AnyAddonEverPumped || AnyMachineEverPumped || AnyScreenOpEverApplied);

    /// <summary>Builds a fresh server from a previously captured checkpoint — the sequence
    /// <see cref="WorldReplaySnapshot.Drive"/> already follows for an offline rehydration (population, machine
    /// host, then this server over the checkpoint's own definition), then <see cref="RestoreCheckpoint"/> overwrites
    /// the boot-derived state this constructor otherwise seeds. The returned server has not yet taken a
    /// <see cref="Step"/>.</summary>
    /// <param name="checkpoint">The captured image to restore from.</param>
    /// <param name="profiles">The profile catalog this server's identities resolve against — a fresh instance the
    /// caller loads from this row's own owned-worlds directory, exactly as a boot composition root does.</param>
    /// <param name="machines">The machine host this server steps — empty for a checkpoint the arm gate has already
    /// proven never stepped one.</param>
    /// <param name="instanceIdentity">This row's own running-instance identity.</param>
    /// <returns>The restored server and the population it owns.</returns>
    public static (WorldServer Server, WorldPopulation Population) FromCheckpoint(WorldAuthorityCheckpoint checkpoint, WorldOwnedWorlds profiles, WorldMachineHost machines, string instanceIdentity) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        ArgumentNullException.ThrowIfNull(argument: profiles);
        ArgumentNullException.ThrowIfNull(argument: machines);
        ArgumentException.ThrowIfNullOrEmpty(argument: instanceIdentity);

        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);
        var population = new WorldPopulation(definition: definition);
        var server = new WorldServer(
            definition: definition,
            envelope: new WorldRenderEnvelope(),
            instanceIdentity: instanceIdentity,
            machines: machines,
            population: population,
            profiles: profiles
        );

        server.RestoreCheckpoint(checkpoint: checkpoint);

        return (server, population);
    }
    /// <summary>Captures a full simulation-state image of this server and every subsystem it owns, under
    /// <see cref="m_authorityGate"/>. Refuses by name (returns <see langword="false"/>) when this server has ever
    /// pumped an addon, stepped a machine, or applied a screen op — machine core state and addon guest state are not
    /// capturable today (the arm gate <c>replay.record</c> already reuses,
    /// <see cref="AnyAddonEverPumped"/>/<see cref="AnyMachineEverPumped"/>/<see cref="AnyScreenOpEverApplied"/>) —
    /// or when <see cref="m_pending"/> or <see cref="m_ordered"/> is non-empty at the moment of the call, which the
    /// caller must retry at the NEXT master boundary rather than treat as a hard refusal (a live console submission
    /// landed in the window between this boundary and the last drain).</summary>
    /// <param name="hostRow">This row's own slice of the host engine's cross-instance tables — supplied by
    /// <see cref="WorldInstanceHost"/>, which owns that state.</param>
    /// <param name="checkpoint">The captured image, on success.</param>
    /// <param name="reason">Why capture was refused, on failure.</param>
    /// <returns><see langword="true"/> when capture succeeded.</returns>
    public bool TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint hostRow, out WorldAuthorityCheckpoint? checkpoint, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: hostRow);

        lock (m_authorityGate) {
            if (AnyUncapturableStateEverLatched()) {
                checkpoint = null;
                reason = "a checkpoint cannot capture a server that has ever pumped an addon, stepped a machine, or applied a screen op — machine core and addon guest state are not capturable today";

                return false;
            }
            if (m_pending.Count != 0) {
                checkpoint = null;
                reason = "a checkpoint cannot capture while a buffered live-edit op is pending drain — retry at the next master boundary";

                return false;
            }
            if (m_ordered.Count != 0) {
                checkpoint = null;
                reason = "a checkpoint cannot capture while the ordered submission domain is non-empty — retry at the next master boundary";

                return false;
            }

            m_engagement.AssertCheckpointQuiescent();

            var journal = new (ulong, WorldMutation)[m_journal.Count];

            for (var index = 0; (index < m_journal.Count); index++) {
                journal[index] = (m_journal[index].Tick, m_journal[index].Mutation);
            }

            var ruleGateHeld = new List<(string, bool)>(capacity: m_ruleGateHeld.Count);

            m_ruleGateHeld.Flatten(into: ruleGateHeld);

            var interactionGateHeld = new List<(string, bool)>(capacity: m_interactionGateHeld.Count);

            m_interactionGateHeld.Flatten(into: interactionGateHeld);

            var server = new WorldServerCheckpoint(
                DefinitionJson: WorldDefinitionSerialization.Serialize(definition: m_definition),
                BaseDefinitionJson: WorldDefinitionSerialization.Serialize(definition: m_base),
                BaseOrigin: m_baseOrigin,
                Journal: journal,
                LastCompletedTick: m_lastCompletedTick,
                LastCompletedEngineTicks: m_lastCompletedEngineTicks,
                LastStepTicks: m_lastStepTicks,
                Intents: [.. m_intents],
                Pending: [],
                RuleGateHeld: ruleGateHeld,
                InteractionGateHeld: interactionGateHeld,
                LastDocumentReceipt: m_lastDocumentReceipt,
                SolidRevision: m_solidRevision,
                MusicClockElapsedTicks: m_musicClock?.ElapsedTicks,
                MusicDirectorCurrentSegmentId: m_musicDirector?.CurrentSegmentId,
                MusicDirectorArmed: null,
                MusicDirectorTransitionCount: (m_musicDirector?.TransitionCount ?? 0UL),
                MusicDirectorLastTransitionTick: m_musicDirector?.LastTransitionTick,
                MusicDirectorLastTransitionFromSegmentId: m_musicDirector?.LastTransitionFromSegmentId,
                MusicDirectorLastTransitionToSegmentId: m_musicDirector?.LastTransitionToSegmentId,
                JudgeGrades: [.. m_judgeGrades.Select(selector: pair => (pair.Key.EntityIndex, pair.Key.JudgeRef, pair.Value.Grade, pair.Value.Tick))]
            );

            checkpoint = new WorldAuthorityCheckpoint(
                Server: server,
                Population: m_population.Capture(),
                Grants: m_grants.Capture(),
                Escrow: m_transferEscrow.Capture(),
                InputHold: m_inputHold.Capture(),
                EventFeed: m_events.Capture(),
                OwnedWorlds: m_profiles.Capture(),
                HostRow: hostRow,
                Fields: m_population.Fields?.Capture()
            );
            reason = string.Empty;

            return true;
        }
    }
    /// <summary>Restores this server's own fields and every subsystem it owns from a previously captured
    /// checkpoint. Called immediately after construction and before the first <see cref="Step"/> — the definition
    /// this restore installs is the checkpoint's own, never re-composed by replaying the journal.</summary>
    /// <param name="checkpoint">The captured image to restore.</param>
    public void RestoreCheckpoint(WorldAuthorityCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        var server = checkpoint.Server;

        if ((m_population.Fields is null) != (checkpoint.Fields is null)) {
            throw new InvalidOperationException(message: "the checkpoint's fields-section presence does not match its world definition.");
        }

        m_population.Fields?.ValidateCheckpoint(checkpoint: checkpoint.Fields!);

        m_definition = WorldDefinitionSerialization.Deserialize(utf8Json: server.DefinitionJson);
        m_base = WorldDefinitionSerialization.Deserialize(utf8Json: server.BaseDefinitionJson);
        m_baseOrigin = server.BaseOrigin;
        m_journal.Clear();
        foreach (var (tick, mutation) in server.Journal) {
            m_journal.Add(item: new JournalEntry(
                Mutation: mutation,
                Tick: tick
            ));
        }
        m_lastCompletedTick = server.LastCompletedTick;
        m_lastCompletedEngineTicks = server.LastCompletedEngineTicks;
        m_lastStepTicks = server.LastStepTicks;
        m_intents.Clear();
        foreach (var intent in server.Intents) {
            m_intents.Enqueue(item: intent);
        }
        m_ruleGateHeld.Clear();
        foreach (var (rule, held) in server.RuleGateHeld) {
            m_ruleGateHeld.Restore(
                held: held,
                key: rule
            );
        }
        m_interactionGateHeld.Clear();
        foreach (var (interaction, held) in server.InteractionGateHeld) {
            m_interactionGateHeld.Restore(
                held: held,
                key: interaction
            );
        }
        m_lastDocumentReceipt = server.LastDocumentReceipt;
        m_solidRevision = server.SolidRevision;

        if (
            (m_musicClock is not null) &&
            (server.MusicClockElapsedTicks is { } elapsedTicks)
        ) {
            m_musicClock.RestoreElapsedTicks(elapsedTicks: elapsedTicks);
        }
        if (
            (m_musicDirector is not null) &&
            (server.MusicDirectorCurrentSegmentId is { } segmentId)
        ) {
            m_musicDirector.Restore(
                armed: server.MusicDirectorArmed,
                currentSegmentId: segmentId,
                lastTransitionFromSegmentId: server.MusicDirectorLastTransitionFromSegmentId,
                lastTransitionTick: server.MusicDirectorLastTransitionTick,
                lastTransitionToSegmentId: server.MusicDirectorLastTransitionToSegmentId,
                transitionCount: server.MusicDirectorTransitionCount
            );
        }

        m_judgeGrades.Clear();
        foreach (var (entityIndex, judgeRef, grade, tick) in server.JudgeGrades) {
            m_judgeGrades[(entityIndex, judgeRef)] = (grade, tick);
        }

        m_population.Restore(
            checkpoint: checkpoint.Population,
            defaults: m_definition.PlayerDefaults,
            tick: m_lastCompletedTick
        );
        m_grants.Restore(checkpoint: checkpoint.Grants);

        if (m_population.Fields is { } lattice) {
            lattice.Restore(checkpoint: checkpoint.Fields!);
        }

        // A restored parked PEER generation is released right here, not at its grace deadline: the connection that
        // occupied it did not survive the restore and peer body-resume does not exist, so — exactly as the
        // PeerDisconnected arm argues — its rows and exclusive reservations would only refuse live acquirers while
        // nothing could ever exercise them (forever, at rate 0). The body's own park-with-grace is untouched, and a
        // local seat's rows are untouched (a seat can be resumed onto). Same ordinary Revoke door, same loud lines.
        for (var index = 0; (index < m_population.Capacity); index++) {
            if (
                !m_population.IsParked(index: index) ||
                !m_population.IsAdmittedPeer(bodyIndex: index)
            ) {
                continue;
            }

            foreach (var row in m_grants.Rows(principal: m_population.PeerPrincipal(index: index))) {
                Revoke(
                    grant: row,
                    actor: WorldPrincipal.Console
                );
            }
        }

        m_transferEscrow.Restore(checkpoint: checkpoint.Escrow);
        m_inputHold.Restore(checkpoint: checkpoint.InputHold);
        m_events.Restore(checkpoint: checkpoint.EventFeed);
        m_profiles.Restore(checkpoint: checkpoint.OwnedWorlds);
        RecompileRules(definition: m_definition);
    }
    /// <summary>Re-applies one mutation from a hosted row's persisted journal tail — the mutations recorded after
    /// the checkpoint <see cref="FromCheckpoint"/> restored from, replayed in order to bring the server current. Runs
    /// the same admission/compose/validate path <c>TryApplyMutation</c> takes for any live mutation; a tail entry was
    /// already accepted once, live, so a rejection here means the restored document disagrees with what was
    /// recorded, and the caller should refuse the activation rather than diverge silently.</summary>
    /// <param name="mutation">The recorded mutation.</param>
    /// <param name="tick">The recorded application tick.</param>
    /// <returns><see langword="true"/> when the mutation re-applied.</returns>
    public bool TryApplyJournalTailMutation(WorldMutation mutation, ulong tick) => TryApplyMutation(
        connectionId: -1,
        correlationId: 0L,
        mutation: mutation,
        preMetered: false,
        tick: tick
    );

    // One journal entry — the tick a mutation applied and the mutation itself (the edit history replay reproduces).
    private readonly record struct JournalEntry(ulong Tick, WorldMutation Mutation);
}

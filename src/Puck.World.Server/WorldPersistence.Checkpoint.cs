using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    // Undo the last `count` applied mutations (default clamps to 1): restore the base and deterministically replay the
    // journal minus its tail through the SAME per-entry gates a live mutation passes — compose, whole-document
    // validate, render-envelope capacity, and solid-field buildability — everything but the authority check (the
    // every-section Mutate hold below already re-proves authority for the whole undo, so no per-entry grant lookup is
    // needed). The replay is ALL-OR-NOTHING: any entry failing any gate refuses the undo outright, names the failing
    // entry's index and reason on stderr, and installs NOTHING — a validated prefix is not a validated document, and no
    // general admissibility invariant lets a partially-replayed journal stand in for one that fully replayed.
    internal bool ApplyUndo(int count, WorldPrincipal principal, int connectionId, long correlationId) {
        // Journal control is Mutate territory over every section (a replay can rebuild any).
        if (!Host.GrantTable.AllowsAllSections(
            capability: WorldCapability.Mutate,
            denial: out var undoVerdict,
            deniedSection: out var undoSection,
            principal: principal
        )) {
            Host.Document.DenyGrantTable(
                denial: $"{principal.Describe()} cannot mutate every section (section:{undoSection.ToString().ToLowerInvariant()} — {undoVerdict.DescribeDenial()}) — world.undo dropped",
                connectionId: connectionId,
                correlationId: correlationId,
                echoKind: WorldEditEchoKind.Mutation
            );

            return false;
        }

        if (Host.Document.Journal.Count == 0) {
            return RefuseUndo(
                connectionId: connectionId,
                correlationId: correlationId,
                logged: "nothing to undo",
                refusal: "undo refused: nothing to undo"
            );
        }

        // A bounded journal (host.journalDepth > 0) has already folded anything past the horizon into m_base — the
        // journal itself never holds more than that many entries (see EnforceJournalDepth), so a request past what
        // remains cannot be satisfied by clamping to fewer without silently doing less than asked. An unbounded
        // journal (0, today's behavior) keeps the old clamp: every entry is always still there to reach.
        var journalDepth = Host.Document.Definition.Host.JournalDepth;

        if (
            (journalDepth > 0) &&
            (count > Host.Document.Journal.Count)
        ) {
            return RefuseUndo(
                connectionId: connectionId,
                correlationId: correlationId,
                refusal: $"undo refused: {count} requested, but host.journalDepth {journalDepth} bounds the horizon to the {Host.Document.Journal.Count} entries still in the journal — earlier mutations have already compacted into the base"
            );
        }

        var drop = Math.Clamp(
            value: count,
            min: 1,
            max: Host.Document.Journal.Count
        );
        var keep = (Host.Document.Journal.Count - drop);

        var candidate = Host.Document.Base;
        var kept = new List<WorldJournalEntry>(capacity: keep);

        for (var index = 0; (index < keep); index++) {
            var entry = Host.Document.Journal[index];

            if (!WorldDocument.TryCompose(
                current: candidate,
                mutation: entry.Mutation,
                tick: entry.Tick,
                engineTick: entry.EngineTick,
                instanceIdentity: Host.InstanceIdentity,
                candidate: out var next,
                reason: out var composeReason,
                evictedKey: out _
            )) {
                var composeRefusal = $"undo refused: replay failed at journal entry {index} ({WorldServer.Describe(mutation: entry.Mutation)}) — {composeReason}";

                return RefuseUndo(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    refusal: composeRefusal
                );
            }

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
                (WorldDocument.AffectsRenderEnvelope(mutation: entry.Mutation) && !Host.Envelope.TryFit(
                candidate: next,
                reason: out reason
            )) ||
                (WorldDocument.AffectsSolidField(mutation: entry.Mutation) && !WorldDocument.TryBuildSolids(
                definition: next,
                reason: out reason,
                solids: out _
            )) ||
                (WorldDocument.AffectsAddons(mutation: entry.Mutation) && !AddonsCanPrepare(
                candidate: next,
                reason: out reason
            ))
            ) {
                var refusal = $"undo refused: replay failed at journal entry {index} ({WorldServer.Describe(mutation: entry.Mutation)}) — {reason}";

                return RefuseUndo(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    refusal: refusal
                );
            }

            candidate = next;
            kept.Add(item: entry);
        }

        // Field storage is boot allocated. Prove the final replay result can retain the live lattice before building
        // or swapping any other derived runtime product; InstallFields is then an infallible compatible plan swap.
        if (!Host.Population.CanInstallFields(
            definition: candidate,
            reason: out var undoFieldReason
        )) {
            var refusal = $"undo refused: restored field runtime is incompatible — {undoFieldReason}";

            return RefuseUndo(
                connectionId: connectionId,
                correlationId: correlationId,
                refusal: refusal
            );
        }

        // The full replay validated every entry above, so this rebuild is expected to succeed; still checked and
        // still loud on failure rather than installing a half-built field, for the same reason the loop above refuses
        // rather than tolerates: no step here is allowed to half-apply.
        if (!WorldDocument.TryBuildSolids(
            definition: candidate,
            reason: out var undoSolidReason,
            solids: out var undoSolids
        )) {
            var refusal = $"undo refused: solid field rebuild failed — {undoSolidReason}";

            return RefuseUndo(
                connectionId: connectionId,
                correlationId: correlationId,
                refusal: refusal
            );
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
        IWorldMachinePreparedPlan? machinePlan = null;

        // The whole sequence from here through Commit runs under ONE try/finally — see TryApplyMutation's identical
        // shape for why: addonPlan starts null, so a refusal before TryPrepare ever succeeds leaves the finally a
        // no-op, and a downstream throw from contention-array staging, Install, or Commit alike still disposes an
        // uncommitted plan.
        try {
            if (Host.Addons is { } addonsForUndo) {
                if (!addonsForUndo.TryPrepare(
                    candidate: candidate,
                    current: Host.Document.Definition,
                    plan: out addonPlan,
                    reason: out var addonReason
                )) {
                    var refusal = $"undo refused: the restored document's addon {addonReason}";

                    return RefuseUndo(
                        connectionId: connectionId,
                        correlationId: correlationId,
                        refusal: refusal
                    );
                }

                if (addonPlan is not null) {
                    Host.Document.StageAddonContentionArrays(
                        mountedCount: addonPlan.MountedCount,
                        entity: out newTickWrittenEntity,
                        principal: out newTickWrittenPrincipal,
                        collided: out newTickCollided
                    );
                }
            }

            if (!Host.Machines.TryPrepare(
                candidate: candidate,
                current: Host.Document.Definition,
                plan: out machinePlan,
                reason: out var machineReason
            )) {
                return RefuseUndo(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    refusal: $"undo refused: the restored document's machines could not prepare — {machineReason}"
                );
            }

            var previousDefinition = Host.Document.Definition;

            Host.Document.SwapSolids(solids: undoSolids);
            Host.Document.Install(
                definition: candidate,
                rebuildPopulation: true
            );
            Host.RepaintChangedLatticeDraws(
                current: candidate,
                previous: previousDefinition
            );

            Host.Machines.Commit(plan: machinePlan!);
            Host.Machines.Finish(plan: machinePlan!);

            if (addonPlan is not null) {
                Host.Addons!.Commit(plan: addonPlan);
                addonPlanCommitted = true;

                if (newTickWrittenEntity is not null) {
                    Host.Tick.AdoptContentionArrays(
                        collided: newTickCollided!,
                        entity: newTickWrittenEntity,
                        principal: newTickWrittenPrincipal!
                    );
                }
            }
        } finally {
            machinePlan?.Dispose();
            if (!addonPlanCommitted) {
                addonPlan?.Dispose();
            }
        }

        Host.Document.Journal.Clear();
        Host.Document.Journal.AddRange(collection: kept);
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.undo",
                text: $"[world.undo: dropped {drop}, {Host.Document.Journal.Count} remaining]"
            );
        }

        if (addonPlanCommitted) {
            Host.Addons!.Finish(plan: addonPlan!);
        }

        return true;
    }
    // Undo's own throwaway addon-prepare probe for an INTERMEDIATE journal-replay candidate: proves the row set
    // this candidate carries could still mount, without ever registering, disclosing, or journaling anything — the
    // plan is disposed immediately regardless of outcome. Only the FINAL candidate's prepare (after the loop above)
    // ever actually commits. A server with no addon runtime attached vacuously succeeds.
    // Every ApplyUndo gate refuses identically: loud on stderr under world.undo, echoed to the same tap a rejected
    // live mutation reaches, and false to the caller. logged overrides the stderr body for the one gate whose line
    // predates the echo's own "undo refused:" prefix.
    private bool RefuseUndo(string refusal, int connectionId, long correlationId, string? logged = null) {
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(
                channel: "world.undo",
                text: $"[world.undo: {(logged ?? refusal)}]"
            );
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: refusal,
            Rejected: true,
            Kind: WorldEditEchoKind.Mutation,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));

        return false;
    }
    private bool AddonsCanPrepare(WorldDefinition candidate, out string reason) {
        if (Host.Addons is not { } addons) {
            reason = string.Empty;

            return true;
        }

        if (addons.TryPrepare(
            candidate: candidate,
            current: Host.Document.Definition,
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

    /// <summary>Buffers a journal undo of the last <paramref name="count"/> mutations for the next <see cref="WorldTick.Step"/>.
    /// Retains the submitting envelope's connection/correlation identity — see <see cref="WorldDocument.EnqueueMutation"/>'s own
    /// remarks.</summary>
    /// <param name="count">How many trailing mutations to undo (clamped to at least 1 and at most the journal length).</param>
    /// <param name="principal">The acting identity the undo is checked against.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    internal void EnqueueUndo(int count, WorldPrincipal principal, int connectionId = SubmissionEnvelope.LocalConnectionId, long correlationId = 0) {
        Host.Document.Pending.Enqueue(item: new WorldPendingOp.Undo(
            ConnectionId: connectionId,
            CorrelationId: correlationId,
            Count: count,
            Principal: principal
        ));
    }
    /// <summary>Bounds the journal to at most <c>host.journalDepth</c> trailing entries (0 = unbounded, the default —
    /// a no-op). The oldest entries past the horizon fold forward, in order, into the base the journal already
    /// keeps — the same per-entry compose-and-rebase <see cref="ApplyUndo"/>'s own replay performs, run forward
    /// instead of backward, so a checkpoint captured after this call restores to the identical live definition a
    /// checkpoint captured before it would have. Called once per completed tick.</summary>
    internal void EnforceJournalDepth() {
        lock (Host.AuthorityGate) {
            var depth = Host.Document.Definition.Host.JournalDepth;

            if (
                (depth <= 0) ||
                (Host.Document.Journal.Count <= depth)
            ) {
                return;
            }

            var excess = (Host.Document.Journal.Count - depth);
            var candidate = Host.Document.Base;

            for (var index = 0; (index < excess); index++) {
                var entry = Host.Document.Journal[index];

                // Every entry here already applied live once, against this exact base-and-prefix, so recomposing it
                // is expected to succeed; if it somehow does not, leave the journal exactly as it stood rather than
                // fold onto a candidate that failed to build.
                if (!WorldDocument.TryCompose(
                    current: candidate,
                    mutation: entry.Mutation,
                    tick: entry.Tick,
                    engineTick: entry.EngineTick,
                    instanceIdentity: Host.InstanceIdentity,
                    candidate: out var next,
                    reason: out _,
                    evictedKey: out _
                    )) {
                    return;
                }

                candidate = next;
            }

            Host.Document.AdoptBase(
                definition: candidate,
                origin: $"the journal depth horizon (host.journalDepth {depth})"
            );
            Host.Document.Journal.RemoveRange(
                count: excess,
                index: 0
            );
        }
    }

    // A key-bound latch entry's LatchKey.Left is an ordinal this catalog interned, so it travels as the key's name
    // and is interned again on the way back in. An unnamed entry's Left is a participant index, which the catalog
    // knows nothing about and which travels as itself.
    private WorldRuleLatchEntry[] FlattenLatch(RuleLatch latch, bool named) {
        var flattened = new List<(string Rule, LatchKey Binding, bool Held)>(capacity: latch.Count);

        latch.Flatten(into: flattened);

        var keys = Host.Document.Definition.StateCatalog.Keys;
        var entries = new WorldRuleLatchEntry[flattened.Count];

        for (var index = 0; (index < flattened.Count); index++) {
            var (rule, binding, held) = flattened[index];
            var name = string.Empty;

            if (
                named &&
                (binding.Left >= 0) &&
                (binding.Left < keys.Count)
            ) {
                name = keys.Names[binding.Left].Value;
            }

            entries[index] = new WorldRuleLatchEntry(
                Held: held,
                Key: name,
                Left: ((name.Length == 0)
                    ? binding.Left
                    : -1),
                Right: binding.Right,
                Rule: rule
            );
        }

        return entries;
    }
    private void RestoreLatch(RuleLatch latch, IReadOnlyList<WorldRuleLatchEntry> entries) {
        var keys = Host.Document.Definition.StateCatalog.Keys;

        latch.Clear();
        foreach (var entry in entries) {
            var left = entry.Left;

            if (entry.Key.Length != 0) {
                if (!keys.TryIntern(
                    key: out var key,
                    name: CellName.Parse(candidate: entry.Key),
                    reason: out var reason
                )) {
                    throw new InvalidOperationException(message: $"the checkpoint's latch entry for rule '{entry.Rule}' names cell key '{entry.Key}', which this catalog cannot intern: {reason}");
                }

                left = key.Ordinal;
            }

            latch.Restore(
                binding: new LatchKey(
                    Left: left,
                    Right: entry.Right
                ),
                held: entry.Held,
                name: entry.Rule
            );
        }
    }


    /// <summary>The engine-tick threshold beyond which a checkpoint capture is refused rather than silently taken
    /// against state this record graph cannot represent — see <see cref="TryCaptureCheckpoint"/>.</summary>
    /// <returns><see langword="true"/> when this server's live state is outside what a checkpoint can capture.</returns>
    private bool AnyUncapturableStateEverLatched() => (Host.AnyAddonEverPumped ||
        (Host.AnyMachineEverPumped && (Host.Machines is not IWorldMachineCheckpointHost)) || Host.AnyScreenOpEverApplied);

    /// <summary>Builds a fresh server from a previously captured checkpoint — the sequence
    /// <see cref="WorldReplaySnapshot.Drive"/> already follows for an offline rehydration (population, machine
    /// host, then this server over the checkpoint's own definition), then <see cref="RestoreCheckpoint"/> overwrites
    /// the boot-derived state this constructor otherwise seeds. The returned server has not yet taken a
    /// <see cref="WorldTick.Step"/>.</summary>
    /// <param name="checkpoint">The captured image to restore from.</param>
    /// <param name="profiles">The profile catalog this server's identities resolve against — a fresh instance the
    /// caller loads from this row's own owned-worlds directory, exactly as a boot composition root does.</param>
    /// <param name="machines">A fresh machine host prepared from the captured definition. A nonempty saved machine
    /// inventory requires <see cref="IWorldMachineCheckpointHost"/> support.</param>
    /// <param name="instanceIdentity">This row's own running-instance identity.</param>
    /// <returns>The restored server and the population it owns.</returns>
    internal static (WorldServer Server, WorldPopulation Population) FromCheckpoint(WorldAuthorityCheckpoint checkpoint, WorldOwnedWorlds profiles, IWorldMachineHost machines, string instanceIdentity) {
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
    /// <see cref="WorldServer.AuthorityGate"/>. Refuses by name (returns <see langword="false"/>) when this server has ever
    /// pumped an addon, stepped a machine without durable checkpoint support, or applied a screen operation.
    /// Supported machine hosts drain accepted steps and capture their complete runtime images; unsupported
    /// runtime state, including live coupled links and enabled rewind history, refuses capture by name —
    /// or when <see cref="WorldDocument.Pending"/> or <see cref="WorldTick.Ordered"/> is non-empty at the moment of the call, which the
    /// caller must retry at the NEXT master boundary rather than treat as a hard refusal (a live console submission
    /// landed in the window between this boundary and the last drain).</summary>
    /// <param name="hostRow">This row's own slice of the host engine's cross-instance tables — supplied by
    /// <see cref="WorldInstanceHost"/>, which owns that state.</param>
    /// <param name="checkpoint">The captured image, on success.</param>
    /// <param name="reason">Why capture was refused, on failure.</param>
    /// <returns><see langword="true"/> when capture succeeded.</returns>
    internal bool TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint hostRow, out WorldAuthorityCheckpoint? checkpoint, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: hostRow);

        lock (Host.AuthorityGate) {
            if (AnyUncapturableStateEverLatched()) {
                checkpoint = null;
                reason = "a checkpoint cannot capture pumped addon guests, a stepped machine without durable checkpoint support, or applied screen operations";

                return false;
            }
            if (
                (Host.Document.Pending.Count != 0) ||
                (Host.Extensions.PendingContributionCount != 0)
            ) {
                checkpoint = null;
                reason = "a checkpoint cannot capture while a buffered live-edit op is pending drain — retry at the next master boundary";

                return false;
            }
            if (Host.Tick.Ordered.Count != 0) {
                checkpoint = null;
                reason = "a checkpoint cannot capture while the ordered submission domain is non-empty — retry at the next master boundary";

                return false;
            }

            Host.Engagement.AssertCheckpointQuiescent();

            WorldMachineHostCheckpoint? machines = null;

            if (Host.Machines is IWorldMachineCheckpointHost machineHost) {
                try { machines = machineHost.CaptureCheckpoint(); } catch (Exception error) when ((error is InvalidOperationException or IOException or ArgumentException)) {
                    checkpoint = null;
                    reason = $"machine checkpoint refused: {error.Message}";
                    return false;
                }
            }

            var journal = new (ulong, ulong, WorldMutation)[Host.Document.Journal.Count];

            for (var index = 0; (index < Host.Document.Journal.Count); index++) {
                journal[index] = (Host.Document.Journal[index].Tick, Host.Document.Journal[index].EngineTick, Host.Document.Journal[index].Mutation);
            }

            var ruleGateHeld = FlattenLatch(
                latch: Host.RuleHost.RuleGateHeld,
                named: true
            );
            // An interaction's binding is a pair of participant indices the population owns, never an interned key,
            // so it travels as the indices themselves.
            var interactionGateHeld = FlattenLatch(
                latch: Host.RuleHost.InteractionGateHeld,
                named: false
            );
            var groupProgress = new List<(string, RuleGroupProgress)>(capacity: Host.RuleHost.GroupState.Count);

            Host.RuleHost.GroupState.Flatten(into: groupProgress);

            var ruleGroups = new WorldRuleGroupEntry[groupProgress.Count];

            for (var index = 0; (index < groupProgress.Count); index++) {
                var (name, progress) = groupProgress[index];

                ruleGroups[index] = new WorldRuleGroupEntry(
                    Breached: progress.Breached,
                    Group: name,
                    Running: progress.Running,
                    Step: progress.Step
                );
            }

            var server = new WorldServerCheckpoint(
                DefinitionJson: WorldDefinitionSerialization.Serialize(definition: Host.Document.Definition),
                BaseDefinitionJson: WorldDefinitionSerialization.Serialize(definition: Host.Document.Base),
                BaseOrigin: Host.Document.BaseOrigin,
                Journal: journal,
                LastCompletedTick: Host.Tick.CompletedTick,
                LastCompletedEngineTicks: Host.Tick.CompletedEngineTicks,
                LastStepTicks: Host.Tick.LastStepTicks,
                Intents: [.. Host.Tick.Intents],
                Pending: [],
                Decisions: Decisions.Capture(),
                RuleGateHeld: ruleGateHeld,
                InteractionGateHeld: interactionGateHeld,
                RuleGroups: ruleGroups,
                LastDocumentReceipt: Host.Document.LastDocumentReceipt,
                SolidRevision: Host.Document.SolidRevision,
                MusicClockElapsedTicks: Host.Tick.MusicClock?.ElapsedTicks,
                MusicDirectorCurrentSegmentId: Host.Tick.MusicDirector?.CurrentSegmentId,
                MusicDirectorArmed: null,
                MusicDirectorTransitionCount: (Host.Tick.MusicDirector?.TransitionCount ?? 0UL),
                MusicDirectorLastTransitionTick: Host.Tick.MusicDirector?.LastTransitionTick,
                MusicDirectorLastTransitionFromSegmentId: Host.Tick.MusicDirector?.LastTransitionFromSegmentId,
                MusicDirectorLastTransitionToSegmentId: Host.Tick.MusicDirector?.LastTransitionToSegmentId,
                MusicDirectorLastEmbellishmentPatchId: Host.Tick.MusicDirector?.LastEmbellishmentPatchId,
                MusicDirectorLastEmbellishmentTick: Host.Tick.MusicDirector?.LastEmbellishmentTick
            );

            checkpoint = new WorldAuthorityCheckpoint(
                Server: server,
                Population: Host.Population.Capture(),
                Grants: Host.GrantTable.Capture(),
                Escrow: Host.TransferEscrow.Capture(),
                InputHold: Host.InputHold.Capture(),
                EventFeed: Host.Events.Capture(),
                OwnedWorlds: Host.Profiles.Capture(),
                HostRow: hostRow,
                Fields: Host.Population.Fields?.Capture(),
                Search: Host.Search.Capture(),
                BoardEnforcement: BoardEnforcement.Capture(),
                Machines: machines
            );
            reason = string.Empty;

            return true;
        }
    }
    /// <summary>Restores this server's own fields and every subsystem it owns from a previously captured
    /// checkpoint. Called immediately after construction and before the first <see cref="WorldTick.Step"/> — the definition
    /// this restore installs is the checkpoint's own, never re-composed by replaying the journal.</summary>
    /// <param name="checkpoint">The captured image to restore.</param>
    internal void RestoreCheckpoint(WorldAuthorityCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        var server = checkpoint.Server;

        if ((Host.Population.Fields is null) != (checkpoint.Fields is null)) {
            throw new InvalidOperationException(message: "the checkpoint's fields-section presence does not match its world definition.");
        }

        Host.Population.Fields?.ValidateCheckpoint(checkpoint: checkpoint.Fields!);
        // Population validation is deliberately before any server field changes below. A malformed cached route
        // must refuse the entire restore atomically, not fail after the definition, clocks, or journal were replaced.
        Host.Population.ValidateCheckpoint(checkpoint: checkpoint.Population);

        var restoredDefinition = WorldDefinitionSerialization.Deserialize(utf8Json: server.DefinitionJson);

        Host.Events.ValidateCheckpoint(checkpoint: checkpoint.EventFeed);
        Decisions.ValidateCheckpoint(
            checkpoint: server,
            definition: restoredDefinition
        );

        var machineCheckpoint = (checkpoint.Machines ?? WorldMachineHostCheckpoint.Empty);

        if (Host.Machines is IWorldMachineCheckpointHost machineHost) {
            machineHost.RestoreCheckpoint(checkpoint: machineCheckpoint);
        } else if (
            (machineCheckpoint.Instances.Count != 0) ||
            machineCheckpoint.AnyEverPumped
        ) {
            throw new InvalidOperationException(message: "checkpoint contains machine state that this host cannot restore");
        }

        Host.Document.AdoptDefinition(definition: restoredDefinition);
        Host.Document.AdoptBase(
            definition: WorldDefinitionSerialization.Deserialize(utf8Json: server.BaseDefinitionJson),
            origin: server.BaseOrigin
        );
        Host.Document.Journal.Clear();
        foreach (var (tick, engineTick, mutation) in server.Journal) {
            Host.Document.Journal.Add(item: new WorldJournalEntry(
                EngineTick: engineTick,
                Mutation: mutation,
                Tick: tick
            ));
        }
        Host.Tick.RestoreClock(
            completedEngineTicks: server.LastCompletedEngineTicks,
            completedTick: server.LastCompletedTick,
            lastStepTicks: server.LastStepTicks
        );
        Host.Tick.Intents.Clear();
        foreach (var intent in server.Intents) {
            Host.Tick.Intents.Enqueue(item: intent);
        }
        RestoreLatch(
            entries: server.RuleGateHeld,
            latch: Host.RuleHost.RuleGateHeld
        );
        RestoreLatch(
            entries: server.InteractionGateHeld,
            latch: Host.RuleHost.InteractionGateHeld
        );
        Host.RuleHost.GroupState.Clear();
        foreach (var entry in server.RuleGroups) {
            Host.RuleHost.GroupState.Restore(
                name: entry.Group,
                progress: new RuleGroupProgress(
                    Breached: entry.Breached,
                    Running: entry.Running,
                    Step: entry.Step
                )
            );
        }
        Host.Document.LastDocumentReceipt = server.LastDocumentReceipt;
        Host.Document.RestoreSolidField(
            revision: server.SolidRevision,
            solids: Host.Document.SolidField
        );

        if (
            (Host.Tick.MusicClock is not null) &&
            (server.MusicClockElapsedTicks is { } elapsedTicks)
        ) {
            Host.Tick.MusicClock.RestoreElapsedTicks(elapsedTicks: elapsedTicks);
        }
        if (
            (Host.Tick.MusicDirector is not null) &&
            (server.MusicDirectorCurrentSegmentId is { } segmentId)
        ) {
            Host.Tick.MusicDirector.Restore(
                armed: server.MusicDirectorArmed,
                currentSegmentId: segmentId,
                lastEmbellishmentPatchId: server.MusicDirectorLastEmbellishmentPatchId,
                lastEmbellishmentTick: server.MusicDirectorLastEmbellishmentTick,
                lastTransitionFromSegmentId: server.MusicDirectorLastTransitionFromSegmentId,
                lastTransitionTick: server.MusicDirectorLastTransitionTick,
                lastTransitionToSegmentId: server.MusicDirectorLastTransitionToSegmentId,
                transitionCount: server.MusicDirectorTransitionCount
            );
        }

        if (Host.Population.Fields is { } lattice) {
            lattice.Restore(checkpoint: checkpoint.Fields!);
        }
        Host.Population.Restore(
            checkpoint: checkpoint.Population,
            defaults: Host.Document.Definition.PlayerDefaults,
            tick: Host.Tick.CompletedTick
        );
        // Restore rebuilds every WorldBody at the constructed default (Scale == One) — bodies.scaleRow is document
        // state, not part of WorldPopulationCheckpoint, so it needs the same catch-up every other admission door
        // gives a freshly minted body. m_definition is already the checkpoint's own restored document (set above),
        // so this reads the SAME cells the live server had when it captured.
        Host.Population.SyncBodyScale(definition: Host.Document.Definition);
        Host.GrantTable.Restore(checkpoint: checkpoint.Grants);
        // Group authority is a derived view of the restored definition, not checkpoint payload. Rebuild it before
        // any post-restore admission/read path can consult Allows, so role-specific membership and group ownership
        // cannot remain empty (deny-all) or fall back to a stale role-less projection.
        Host.GrantTable.RestoreGroups(
            groups: (Host.Document.Definition.Groups ?? WorldGroupsSection.Empty).Groups,
            kinds: (Host.Document.Definition.Groups ?? WorldGroupsSection.Empty).Kinds,
            ownership: (Host.Document.Definition.Groups ?? WorldGroupsSection.Empty).Ownership
        );

        // A restored parked PEER generation is released right here, not at its grace deadline: the connection that
        // occupied it did not survive the restore and peer body-resume does not exist, so — exactly as the
        // PeerDisconnected arm argues — its rows and exclusive reservations would only refuse live acquirers while
        // nothing could ever exercise them (forever, at rate 0). The body's own park-with-grace is untouched, and a
        // local seat's rows are untouched (a seat can be resumed onto). Same ordinary Revoke door, same loud lines.
        for (var index = 0; (index < Host.Population.Capacity); index++) {
            if (
                !Host.Population.IsParked(index: index) ||
                !Host.Population.IsAdmittedPeer(bodyIndex: index)
            ) {
                continue;
            }

            foreach (var row in Host.GrantTable.Rows(principal: Host.Population.PeerPrincipal(index: index))) {
                Host.GrantTable.ApplyRevoke(
                    grant: row,
                    actor: WorldPrincipal.Console
                );
            }
        }

        Host.TransferEscrow.Restore(checkpoint: checkpoint.Escrow);
        Host.InputHold.Restore(checkpoint: checkpoint.InputHold);
        Host.Events.Restore(checkpoint: checkpoint.EventFeed);
        Host.Profiles.Restore(checkpoint: checkpoint.OwnedWorlds);
        Host.RecompileRules(definition: Host.Document.Definition);
        Decisions.Restore(checkpoint: server.Decisions);
        if (!Host.Search.TryRestore(
            checkpoint: (checkpoint.Search ?? ArenaSearchCheckpoint.Empty),
            reason: out var searchReason
        )) {
            throw new InvalidOperationException(message: $"the checkpoint's search progress does not restore: {searchReason}");
        }
        BoardEnforcement.Restore(checkpoint: (checkpoint.BoardEnforcement ?? WorldBoardEnforcementCheckpoint.Empty));
    }
    /// <summary>Re-applies one mutation from a hosted row's persisted journal tail — the mutations recorded after
    /// the checkpoint <see cref="FromCheckpoint"/> restored from, replayed in order to bring the server current. Runs
    /// the same admission/compose/validate path <c>TryApplyMutation</c> takes for any live mutation; a tail entry was
    /// already accepted once, live, so a rejection here means the restored document disagrees with what was
    /// recorded, and the caller should refuse the activation rather than diverge silently.</summary>
    /// <param name="mutation">The recorded mutation.</param>
    /// <param name="tick">The recorded application tick.</param>
    /// <param name="engineTick">The recorded application engine tick — the exact coordinate the live apply rebased
    /// an Advance epoch against; never re-derived from <paramref name="tick"/> at any rate.</param>
    /// <returns><see langword="true"/> when the mutation re-applied.</returns>
    internal bool TryApplyJournalTailMutation(WorldMutation mutation, ulong tick, ulong engineTick) => Host.TryApplyMutation(
        connectionId: -1,
        correlationId: 0L,
        engineTick: engineTick,
        mutation: mutation,
        preMetered: false,
        tick: tick
    );

}

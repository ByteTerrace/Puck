using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    /// <summary>Composes, validates and applies one mutation through the ordinary pipeline.</summary>
    /// <remarks>The pipeline is two steps. <see cref="TryPrepareMutation"/> runs every gate that can refuse and
    /// leaves any writes in a reversible scope. <see cref="WorldPreparedMutation.Install"/> commits the result. A caller that
    /// must know a mutation will land before it commits something else prepares first and installs after.</remarks>
    /// <param name="mutation">The mutation.</param>
    /// <param name="tick">The simulation tick it applies at.</param>
    /// <param name="engineTick">The engine tick it applies at.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="preMetered">Whether the ingress already charged the dispatch budget.</param>
    /// <returns>Whether the mutation applied.</returns>
    /// <param name="derived">Whether a guest produced this mutation, so only a checkpoint retains its effects.</param>
    internal bool TryApplyMutation(WorldMutation mutation, ulong tick, ulong engineTick, int connectionId, long correlationId, bool preMetered, bool derived = false) {
        m_lastMutationFailureDetail = null;
        if (!TryPrepareMutation(mutation, tick, engineTick, preMetered, out var prepared, out var reason, out var denied)) {
            if (denied) { DenyMutation(mutation, reason, connectionId, correlationId); }
            else { Reject(connectionId: connectionId, correlationId: correlationId, mutation: mutation, reason: reason); }
            return false;
        }

        prepared.Install(connectionId, correlationId, derived);
        return true;
    }

    private void DenyMutation(WorldMutation mutation, string reason, int connectionId, long correlationId) {
        m_lastMutationFailureDetail = reason;
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.grant denied", text: $"[world.grant denied: {mutation.Principal.Describe()} {reason} — {WorldServer.Describe(mutation: mutation)} dropped]");
        }
        Host.EchoTap?.Invoke(new WorldEditEcho(
            Message: reason,
            Rejected: true,
            Kind: WorldEditEchoKind.Mutation,
            Mutation: mutation,
            Denied: true,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));
    }

    internal bool TryPrepareMutation(WorldMutation mutation, ulong tick, ulong engineTick, bool preMetered,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldPreparedMutation? prepared, out string reason, out bool denied) =>
        WorldPreparedMutation.TryPrepare(this, mutation, tick, engineTick, preMetered, out prepared, out reason, out denied);

    private bool TryStageDocument(WorldMutation mutation, ulong tick, ulong engineTick, out DocumentStage stage, out string reason) {
        if (EditsShadingAlone(mutation, m_definition)) {
            if (!TryStageDocument(m_definition, mutation, tick, engineTick, out stage, out reason)) { return false; }
            if (stage.Kind == WorldInstallKind.Presentation) { return true; }
            stage.Dispose();
        }
        return TryStageDocument(Host.MaterializeArena(), mutation, tick, engineTick, out stage, out reason);
    }

    private bool TryStageDocument(WorldDefinition current, WorldMutation mutation, ulong tick, ulong engineTick,
        out DocumentStage prepared, out string reason) {
        prepared = default;
        if (!TryCompose(
            prepareAuthority: false,
            authority: Host.GrantTable,
            current: current,
            mutation: mutation,
            tick: tick,
            engineTick: engineTick,
            instanceIdentity: Host.InstanceIdentity,
            candidate: out var candidate,
            reason: out reason,
            evictedKey: out var evictedKey,
            refusalCode: out var refusalCode
        )) {
            m_lastMutationFailureCode = (refusalCode ?? "world.mutation.refused");
            return false;
        }

        candidate = WorldContributionState.Declare(definition: candidate);
        // Every row the mutation did not name returns to the installed declaration here, so each install below changes
        // only the rows the mutation names: a value install loads them alone, and a full install keeps the live arena
        // when it named none, or carries every other row's live values into its replacement.
        var edit = NameStateEdit(candidate: candidate, current: current);

        candidate = edit.Candidate;
        var kind = InstallKindOf(
            candidate: candidate,
            mutation: mutation,
            previous: current
        );
        var installsArena = (kind == WorldInstallKind.Full);
        var keepsArena = (installsArena && Host.KeepsArena(candidate: candidate));
        // Pool snapshots include generated storage rows that cannot be admitted through the ordinary row-import
        // door. Prepare their replacement arena before commit even when the mutation keeps every compiled product.
        // A presentation install writes no state at all, so it loads nothing.
        var replacesArena = (
            (installsArena && !keepsArena) ||
            ((kind == WorldInstallKind.Value) && (
            (candidate.StateRaw?.Pools is { Count: > 0 }) ||
            (candidate.StateRaw?.PairPools is { Count: > 0 })))
        );
        var arena = (keepsArena ? Host.Arena : null);

        // A malformed declaration must be named by the validator before StateCatalog or StateArena construction
        // can throw while preparing it. Settling can replace derived cells and clocks; a settlement that replaces
        // nothing returns the very candidate validated here, whose compilation then serves below, and any other
        // settled candidate is validated again and owns the retained compilation.
        WorldDefinition? validated = null;
        WorldRuleCompilation? validatedCompilation = null;

        if (installsArena) {
            if (!TryValidateMutationCandidate(
                candidate: candidate,
                compilation: out validatedCompilation,
                current: current,
                mutation: mutation,
                reason: out reason
            )) {
                return false;
            }
            validated = candidate;
        }
        if (
            replacesArena &&
            !Host.TryPrepareArenaReplacement(
            definition: candidate,
            edit: edit,
            prepared: out arena,
            reason: out reason,
            settled: out candidate
        )) {
            return false;
        }

        if (
            (mutation is WorldMutation.UpsertKit upsertKit) &&
            !Host.Population.CanReplaceKit(
            replacement: upsertKit.Kit,
            refusal: out reason
        )
        ) {
            return false;
        }

        // Cross-document adjacency claims are proved at load, never from this tick path. An edit that can change a
        // standing claim or one of its floor inputs must go through a document reload; unrelated edits revalidate
        // only the facts owned by this document.
        if (
            (candidate.Adjacencies is { Count: > 0 }) &&
            AdjacencyProofInputsChanged(
            candidate: candidate,
            current: current,
            mutation: mutation
        )
        ) {
            reason = "the mutation changes an adjacency overlap input; apply it through world.load/world.reload so the neighbour can be re-proved outside the tick path";

            return false;
        }

        WorldRuleCompilation? compilation;

        // Only a full install installs compiled rules, so only its validation keeps what it compiled.
        if (ReferenceEquals(objA: candidate, objB: validated)) {
            compilation = validatedCompilation;
        } else if (!TryValidateMutationCandidate(candidate: candidate, compilation: out compilation, current: current, mutation: mutation, reason: out reason,
            retainCompilation: installsArena)) {
            return false;
        }
        if (!Host.Input.TryValidateDefinition(candidate, arena, out reason)) { return false; }
        if (!TryAdmitPipelineOverrides(
            candidate: candidate,
            current: current,
            mutation: mutation,
            reason: out reason
        )) {
            return false;
        }
        // A value-only candidate reuses the live arena rather than carrying a prepared replacement. Validate its
        // complete import against that arena now, while a rule firing's scope is still open, so retained orphan keys
        // and visibility payload can refuse before commit. Direct mutations install immediately after preparation;
        // a rule commit closes the same unchanged scope, so the following TryLoad has the same admission inputs.
        if (
            !replacesArena &&
            !keepsArena &&
            !Host.Arena.TryValidateLoad(
            rows: Host.ReplacedRows(definition: candidate, previous: m_definition),
            reason: out reason
        )) {
            return false;
        }

        if (ExceedsBootDerivedFaceReservation(
            candidate: candidate,
            reason: out reason
        )) {
            return false;
        }

        // The compiled products a full install keeps unless a part they were built from changed; a presentation
        // install keeps every product but the envelope's fit, and a value install keeps them all.
        var products = kind switch {
            WorldInstallKind.Full => PlanProducts(previous: current, candidate: candidate),
            WorldInstallKind.Presentation => PlanProducts(previous: current, candidate: candidate).ForPresentation(),
            _ => default(WorldProductPlan),
        };
        WorldDocumentInputs? envelopeInputs = null;

        if (!products.Envelope.IsEmpty) {
            if (!TryFitEnvelope(candidate: candidate, inputs: out var fitted, reason: out reason)) {
                return false;
            }
            envelopeInputs = fitted;
        }

        if (!Host.Population.CanInstallFields(
            definition: candidate,
            reason: out var fieldReason
        )) {
            reason = fieldReason!;

            return false;
        }

        if (!TryStageSolids(candidate: candidate, products: products, reason: out reason, solids: out var solids)) {
            return false;
        }

        // The addon and machine hosts stage their resources against the same candidate, last. A plan is set only on
        // success, and a refusal or a throw after one was obtained disposes it, so nothing staged outlives a
        // mutation that does not install. A server with no addon host attached refuses a candidate holding an
        // enabled addon row by name rather than accepting a no-op.
        IWorldAddonPreparedPlan? addonPlan = null;
        IWorldMachinePreparedPlan? machinePlan = null;
        WorldDocumentInputs? addonInputs = null;
        WorldDocumentInputs? machineInputs = null;
        int[]? tickWrittenEntity = null;
        Principal[]? tickWrittenPrincipal = null;
        bool[]? tickCollided = null;
        var staged = false;

        try {
            if (!products.Addons.IsEmpty) {
                if (Host.Addons is not { } addons) {
                    if (TryFindEnabledAddonRow(candidate: candidate, name: out var enabledRowName)) {
                        reason = $"addon '{enabledRowName}' cannot mount — no addon host is attached to this server";

                        return false;
                    }
                } else {
                    if (!TryPrepareAddons(
                        addons: addons,
                        candidate: candidate,
                        current: current,
                        inputs: out var preparedAddons,
                        plan: out addonPlan,
                        reason: out var addonReason
                    )) {
                        reason = (addonReason ?? "addon preparation refused");

                        return false;
                    }
                    addonInputs = preparedAddons;

                    if (addonPlan is not null) {
                        StageAddonContentionArrays(
                            mountedCount: addonPlan.MountedCount,
                            entity: out tickWrittenEntity,
                            principal: out tickWrittenPrincipal,
                            collided: out tickCollided
                        );
                    }
                }
            }

            if (!products.Machines.IsEmpty) {
                if (!TryPrepareMachines(
                    candidate: candidate,
                    current: current,
                    inputs: out var preparedMachines,
                    plan: out machinePlan,
                    reason: out var machineReason
                )) {
                    reason = (machineReason ?? "screen machine preparation refused");

                    return false;
                }
                machineInputs = preparedMachines;
            }

            if (!Host.GrantTable.TryPrepareDocument(mutation.Principal, current, candidate,
                (WorldMutationKindCatalog.SectionOf(mutation: mutation) == WorldSection.Grants), out var authority, out reason)) { return false; }
            prepared = new DocumentStage(
                addonPlan: addonPlan,
                arena: arena,
                authority: authority!,
                candidate: candidate,
                compilation: compilation,
                engineTick: engineTick,
                evictedKey: evictedKey,
                kind: kind,
                machinePlan: machinePlan,
                mutation: mutation,
                products: products,
                productInputs: (envelopeInputs, addonInputs, machineInputs),
                solids: solids,
                tick: tick,
                tickCollided: tickCollided,
                tickWrittenEntity: tickWrittenEntity,
                tickWrittenPrincipal: tickWrittenPrincipal
            );
            reason = string.Empty;
            staged = true;

            return true;
        } finally {
            if (!staged) {
                addonPlan?.Dispose();
                machinePlan?.Dispose();
            }
        }
    }
    /// <summary>Installs a prepared mutation: swaps the live definition, rebuilds the derived state the mutation
    /// moved, commits the staged addon and machine plans, journals the mutation and echoes it. Nothing here
    /// refuses.</summary>
    /// <param name="prepared">What <see cref="TryPrepareMutation"/> produced, against the document installed
    /// now.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="record">Whether to journal and echo here; a coalesced cell run records each original act.</param>
    /// <param name="derived">Whether the mutation is guest output excluded from the external-edit recovery tail.</param>
    /// <param name="privateWords">Private inputs used by the prepared mutation.</param>
    private void InstallPrepared(DocumentStage prepared, int connectionId, long correlationId, bool record = true, bool derived = false, MutationDrawHistory? privateWords = null) {
        var mutation = prepared.Mutation;
        var candidate = prepared.Candidate;
        var engineTick = prepared.EngineTick;
        var tick = prepared.Tick;
        var addonPlan = prepared.AddonPlan;
        var machinePlan = prepared.MachinePlan;
        var previous = m_definition;

        try {
            // The candidate field is published immediately before the install rebuilds bodies against it.
            if (
                !prepared.Products.Solids.IsEmpty &&
                !ReferenceEquals(
                objA: prepared.Solids,
                objB: m_solids
            )
            ) {
                SetSolids(solids: prepared.Solids);
                m_solidRevision++;
            }

            // The kind the prepare step decided against the document the candidate was composed over
            // (InstallKindOf); a value install carries a prepared arena only for pool storage.
            switch (prepared.Kind) {
                case WorldInstallKind.Value:
                    prepared.Authority.Install();
                    InstallRuntimeStateValue(definition: candidate, mutation: mutation, arena: prepared.Arena);
                    break;
                case WorldInstallKind.Presentation:
                    prepared.Authority.Install();
                    InstallPresentation(definition: candidate);
                    NarrateProducts(plan: prepared.Products, population: false);
                    break;
                default:
                    Install(
                        authority: prepared.Authority,
                        arena: prepared.Arena,
                        definition: candidate,
                        compilation: prepared.Compilation,
                        rebuildPopulation: prepared.Products.RebuildsPopulation
                    );
                    NarrateProducts(plan: prepared.Products, population: prepared.Products.RebuildsPopulation);
                    break;
            }
            AdoptProductInputs(addons: prepared.ProductInputs.Addons, envelope: prepared.ProductInputs.Envelope, machines: prepared.ProductInputs.Machines);

            // Whatever moved a lattice row's draw pass — a Generate, or a whole-row upsert that re-authored its
            // cursor, drawn masks or fill — repaints that row; every other row keeps its evolved cells.
            Host.RepaintChangedLatticeDraws(
                current: candidate,
                previous: previous
            );

            // Registration, disclosure narration and disposal of a superseded guest move only after the install.
            // Finish runs after the journal write below, so nothing it does can still be unwound.
            if (addonPlan is not null) {
                Host.Addons!.Commit(plan: addonPlan);
                prepared.MarkAddonCommitted();

                if (prepared.TickWrittenEntity is not null) {
                    Host.AdoptContentionArrays(
                        collided: prepared.TickCollided!,
                        entity: prepared.TickWrittenEntity,
                        principal: prepared.TickWrittenPrincipal!
                    );
                }
            }

            if (machinePlan is not null) {
                Host.Machines.Commit(plan: machinePlan);
                prepared.MarkMachineCommitted();
            }
        } finally {
            prepared.Dispose();
        }

        if (record) { RecordPrepared(connectionId: connectionId, correlationId: correlationId, derived: derived, mutation: mutation, prepared: prepared, privateWords: privateWords); }
    }

    private void RecordPrepared(DocumentStage prepared, int connectionId, long correlationId, WorldMutation mutation, bool derived, MutationDrawHistory? privateWords) =>
        RecordCommittedMutation(mutation: mutation, tick: prepared.Tick, engineTick: prepared.EngineTick, connectionId: connectionId,
            correlationId: correlationId, evictedKey: prepared.EvictedKey, addonPlan: prepared.AddonPlan, machinePlan: prepared.MachinePlan, derived: derived, privateWords: privateWords);
    private void RecordCommittedMutation(WorldMutation mutation, ulong tick, ulong engineTick, int connectionId, long correlationId,
        CellName? evictedKey = null, IWorldAddonPreparedPlan? addonPlan = null, IWorldMachinePreparedPlan? machinePlan = null, bool derived = false, MutationDrawHistory? privateWords = null) {
        var entry = new WorldJournalEntry(
            EngineTick: engineTick,
            Mutation: mutation,
            Tick: tick
        ) { DrawHistory = privateWords };
        m_journal.Add(entry);
        // Guest decisions belong to the checkpoint's tick, not the external-edit recovery tail.
        if (!derived) { Host.MutationJournalTap?.Invoke(entry); }
        foreach (var scope in Host.ScopedReplaySpan) { scope.Observe(mutation: mutation); }

        if (addonPlan is not null) {
            Host.Addons!.Finish(plan: addonPlan);
        }

        if (machinePlan is not null) {
            Host.Machines.Finish(plan: machinePlan);
        }

        // A defaults-class mutation updates the document while live session levers keep their values (world.save
        // folds them). Fields read from the delivered definition, including authored lighting, still apply live.
        // SetAuthoringDefaults carries both timing classes in one whole-row mutation
        // (WorldPlacementPolicyDefaults' own remarks name which field is which): the headroom/repeat-cap
        // fields are boot-consumed by the frozen render-envelope probe, while candidate/layout/preview fields are
        // re-read live at every use site. The narration spells out the split rather than forcing the mutation into
        // either WorldEditEchoKind bucket; Kind stays Mutation because the live-consumed majority applies NOW.
        var documentOnly = IsDocumentDefaults(mutation: mutation);
        var message = mutation switch {
            WorldMutation.SetAuthoringDefaults => $"{WorldServer.Describe(mutation: mutation)} applied — candidate/layout/preview levers live now; headroom + max-repeat-per-segment apply at next boot",
            // SetPopulationDefaults is a THIRD timing class: the census figures are document defaults (next boot), but
            // the distribution is LIVE for future activations while INERT for bodies already standing — spell out the split.
            WorldMutation.SetPopulationDefaults => $"{WorldServer.Describe(mutation: mutation)} applied — census figures next boot; spawn policy live for future activations, standing bodies unmoved",
            WorldMutation.SetPopulationDistribution => $"{WorldServer.Describe(mutation: mutation)} applied — spawn policy live for future activations, standing bodies unmoved",
            WorldMutation.SetPopulationCensus => $"{WorldServer.Describe(mutation: mutation)} applied — census figures next boot",
            _ => $"{WorldServer.Describe(mutation: mutation)} applied{(documentOnly
            ? " — document default updated; live levers unchanged"
            : string.Empty)}",
        };

        // An Evicts row's overflow policy dropped a cell to make room — named on the SAME echo line rather than a
        // separate one, so an eviction can never scroll past unnoticed the way a second stderr line could.
        if (evictedKey is { } evicted) {
            message = $"{message} (evicted '{evicted}')";
        }

        // A scoped replay's applied input acts as the seat that recorded it, though no seat typed it; the scope's
        // read-back (`replay.scope`) is where its progress is read, so the transcript and the console mirror of it
        // carry no line per tape input.
        if (Host.Output.HasNarrationSink && (connectionId != SubmissionEnvelope.ScopedReplayConnectionId)) {
            Host.Output.Narrate(channel: "world.mutation", text: $"[world.mutation: {message}]");
        }
        if (!HasSubmitter(mutation: mutation)) {
            return;
        }
        Host.EchoTap?.Invoke(obj: new WorldEditEcho(
            Message: message,
            Rejected: false,
            Kind: (documentOnly
            ? WorldEditEchoKind.DocumentDefaults
            : WorldEditEchoKind.Mutation),
            Mutation: mutation,
            ConnectionId: connectionId,
            CorrelationId: correlationId
        ));

    }
    // A mutation the World makes itself — a per-tick sweep's, a rule's document-row effect — was submitted by no
    // session, so it is narrated on the transcript and echoed to none: an echo is a submitter's verdict, and one
    // raised here would reach the toast, the console tape and the edit cue lane as if a seat had typed it.
    private static bool HasSubmitter(WorldMutation mutation) => (mutation.Principal != Principal.World);

    // A state-value mutation can reuse every compiled product only while it keeps the catalog identity and does
    // not rebind a population look. All other mutations install a replacement prepared alongside the document.
    /// <summary>Returns whether installing <paramref name="mutation"/>, composed into <paramref name="candidate"/> over
    /// <paramref name="previous"/>, rebuilds the population: the full install with the population's rebuild, which runs
    /// the structural reconcile of the inhabited placements before the cell-driven counts. Every other install settles
    /// only the cell-driven counts.</summary>
    /// <param name="mutation">The mutation.</param>
    /// <param name="candidate">The document the mutation composes.</param>
    /// <param name="previous">The document it composes over.</param>
    /// <returns><see langword="true"/> when the install rebuilds the population.</returns>
    internal bool RebuildsPopulation(WorldMutation mutation, WorldDefinition candidate, WorldDefinition previous) => (
        (InstallKindOf(
            candidate: candidate,
            mutation: mutation,
            previous: previous
        ) == WorldInstallKind.Full) &&
        PlanProducts(candidate: candidate, previous: previous).RebuildsPopulation
    );

    /// <summary>Returns what installing <paramref name="mutation"/>, composed into <paramref name="candidate"/> over
    /// <paramref name="previous"/>, rebuilds: the one classification the prepare step, the install and the population
    /// forecast read.</summary>
    /// <remarks>A state-value mutation keeps every compiled product only while the candidate carries the state catalog
    /// identity the rules were compiled against and rebinds no population look. A creation edit that changes shading
    /// alone keeps the arena, the rules and the population. Every other mutation takes the full install.</remarks>
    /// <param name="mutation">The mutation.</param>
    /// <param name="candidate">The document the mutation composes.</param>
    /// <param name="previous">The document it composes over.</param>
    /// <returns>The install kind.</returns>
    internal WorldInstallKind InstallKindOf(WorldMutation mutation, WorldDefinition candidate, WorldDefinition previous) {
        if (
            IsValueMutation(candidate: candidate, mutation: mutation, previous: previous) &&
            ReferenceEquals(objA: candidate.StateCatalog, objB: previous.StateCatalog) &&
            !RefreshesLookAssignment(candidate: candidate, mutation: mutation)
        ) {
            return WorldInstallKind.Value;
        }

        return ((
            ReferenceEquals(objA: candidate.StateCatalog, objB: previous.StateCatalog) &&
            (mutation is WorldMutation.UpsertCreation upsert) &&
            (WorldDefinitionRows.FindCreation(creations: candidate.Creations, id: upsert.Creation.Id.Value) is { } after) &&
            EditsShadingAlone(mutation: mutation, previous: previous, replacement: after)
        )
            ? WorldInstallKind.Presentation
            : WorldInstallKind.Full);
    }

    // Whether a creation upsert replaces an installed row with one that differs from it only in what shading reads
    // (WorldPrototype.DiffersInShadingAlone): the submitted row itself before composition, or the composed one after.
    // A row whose values are still unresolved references is not judged before composition resolves it.
    private static bool EditsShadingAlone(WorldMutation mutation, WorldDefinition previous, WorldPrototype? replacement = null) {
        if (
            (mutation is not WorldMutation.UpsertCreation upsert) ||
            (WorldDefinitionRows.FindCreation(creations: previous.Creations, id: upsert.Creation.Id.Value) is not { } before)
        ) {
            return false;
        }

        var after = (replacement ?? upsert.Creation);

        return (
            ((replacement is not null) || !WorldStateDocumentValues.HasReference(graph: after)) &&
            before.DiffersInShadingAlone(other: after)
        );
    }
    // A presentation install changes only what a client draws: the definition is adopted as this server's projection
    // and delivered whole; the arena, every compiled product, the solid field and the population stay as they are.
    private void InstallPresentation(WorldDefinition definition) {
        WorldBootWork.Count(kind: WorldBootWork.PresentationInstalls);
        MarkDefinitionDeliveryPending();
        Host.AdoptBound(definition: definition);
    }
}

/// <summary>What installing a prepared mutation rebuilds (<see cref="WorldDocument.InstallKindOf"/>).</summary>
public enum WorldInstallKind {
    /// <summary>Nothing the live document holds changed: nothing installs.</summary>
    Nothing,
    /// <summary>State values moved alone: the arena loads the rows the write replaced and every compiled product
    /// stays.</summary>
    Value,
    /// <summary>Only what presentation reads changed: the definition is adopted and delivered, and the arena, every
    /// compiled product and the population stay.</summary>
    Presentation,
    /// <summary>The rules, the arena, the fields, the screens and, when the mutation reaches them, the population are
    /// rebuilt against the candidate.</summary>
    Full,
}

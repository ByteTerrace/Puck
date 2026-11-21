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
        if (!TryPrepareMutation(mutation, tick, engineTick, preMetered, out var prepared, out var reason, out var denied, derived)) {
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
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorldPreparedMutation? prepared, out string reason, out bool denied, bool derived = false) =>
        WorldPreparedMutation.TryPrepare(this, mutation, tick, engineTick, preMetered, out prepared, out reason, out denied, derived);

    private bool TryStageDocument(WorldMutation mutation, ulong tick, ulong engineTick, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DocumentStage? stage, out CellName? evictedKey, out string reason) {
        if (EditsShadingAlone(mutation, m_definition)) {
            if (!TryStageDocument(m_definition, mutation, tick, engineTick, out stage, out evictedKey, out reason)) { return false; }
            if (stage.Kind == WorldInstallKind.Presentation) { return true; }
            stage.Dispose();
        }
        return TryStageDocument(Host.MaterializeArena(), mutation, tick, engineTick, out stage, out evictedKey, out reason);
    }

    // A mutation's own gates (composition, classification, validation, the arena its install adopts) before the one
    // install door stages the candidate.
    private bool TryStageDocument(WorldDefinition current, WorldMutation mutation, ulong tick, ulong engineTick,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out DocumentStage? prepared, out CellName? evictedKey, out string reason) {
        prepared = null;
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
            evictedKey: out evictedKey,
            refusalCode: out var refusalCode
        )) {
            m_lastMutationFailureCode = (refusalCode ?? "world.mutation.refused");
            return false;
        }

        var (edit, kind) = ClassifyComposed(composed: candidate, current: current, mutation: mutation);

        candidate = edit.Candidate;
        var validationBase = (edit.Named is not null || ReferenceEquals(objA: candidate.StateRaw, objB: m_definition.StateRaw))
            ? current.WithStateSection(state: m_definition.StateRaw)
            : current;
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
        WorldDefinitionAdmission? validatedAdmission = null;

        if (installsArena) {
            if (!TryValidateMutationCandidate(
                candidate: candidate,
                admission: out validatedAdmission,
                current: validationBase,
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
            current: m_definition
        )
        ) {
            reason = "the mutation changes an adjacency overlap input; apply it through world.load/world.reload so the neighbour can be re-proved outside the tick path";

            return false;
        }

        WorldDefinitionAdmission? admission;

        // Only a full install installs compiled rules, so only its validation keeps what it compiled.
        if (ReferenceEquals(objA: candidate, objB: validated)) {
            admission = validatedAdmission;
        } else if (!TryValidateMutationCandidate(candidate: candidate, admission: out admission, current: (validated ?? validationBase), mutation: mutation, reason: out reason,
            retainCompilation: installsArena)) {
            return false;
        }
        var compilation = (admission ?? validatedAdmission)?.Compilation;
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

        // The compiled products a full install keeps unless a part they were built from changed; a presentation
        // install keeps every product but the envelope's fit, and a value install keeps them all.
        // Planned against the installed document the products were built from, not the arena export the mutation
        // composed over: the candidate holds the installed declaration of every state row it did not name, so only the
        // parts the mutation changed read as changed.
        var products = kind switch {
            WorldInstallKind.Full => PlanProducts(previous: m_definition, candidate: candidate),
            WorldInstallKind.Presentation => PlanProducts(previous: m_definition, candidate: candidate).ForPresentation(),
            _ => default(WorldProductPlan),
        };

        return TryStageInstall(
            request: new InstallRequest(Candidate: candidate, Current: current, Kind: kind, Principal: mutation.Principal, Products: products) {
                Admission = admission,
                Arena = arena,
                Compilation = compilation,
                GrantsWrite = (WorldMutationKindCatalog.SectionOf(mutation: mutation) == WorldSection.Grants),
                Mutation = mutation,
            },
            reason: out reason,
            stage: out prepared
        );
    }

    // Journals and echoes a mutation whose state transaction or staged install just committed, then finishes what the
    // install committed: nothing the hosts do at their finish can unwind the journal entry.
    private void RecordCommittedMutation(WorldMutation mutation, ulong tick, ulong engineTick, int connectionId, long correlationId,
        CellName? evictedKey, DocumentStage? stage, bool derived, MutationDrawHistory? privateWords, Action? commitJournal) {
        var entry = new WorldJournalEntry(
            EngineTick: engineTick,
            Mutation: mutation,
            Tick: tick
        ) { DrawHistory = privateWords, Played = (derived || (connectionId == SubmissionEnvelope.ScopedReplayConnectionId)) };
        AppendJournal(entry: entry);
        commitJournal?.Invoke();
        // Guest decisions belong to the checkpoint's tick, not the external-edit recovery tail.
        if (!derived) { Host.MutationJournalTap?.Invoke(entry); }
        foreach (var scope in Host.ScopedReplaySpan) { scope.Observe(mutation: mutation); }
        if (stage is not null) { FinishInstall(stage: stage); }

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
    internal bool RebuildsPopulation(WorldMutation mutation, WorldDefinition candidate, WorldDefinition previous) {
        var (edit, kind) = ClassifyComposed(composed: candidate, current: previous, mutation: mutation);

        return ((kind == WorldInstallKind.Full) && PlanProducts(candidate: edit.Candidate, previous: m_definition).RebuildsPopulation);
    }
    // The one classification a staged mutation and the population forecast share: the composed candidate with every
    // state row the mutation did not name returned to the installed declaration (NameStateEdit), so each install
    // changes only the rows the mutation names (a value install loads them alone, and a full install keeps the live
    // arena when it named none, or carries every other row's live values into its replacement), and the install it
    // takes over the document it was composed over.
    private (WorldStateEdit Edit, WorldInstallKind Kind) ClassifyComposed(WorldDefinition composed, WorldDefinition current, WorldMutation mutation) {
        var edit = NameStateEdit(candidate: WorldContributionState.Declare(definition: composed), current: current);

        return (edit, InstallKindOf(candidate: edit.Candidate, mutation: mutation, previous: current));
    }

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
    // A presentation install retains the arena and simulation products. The tick delivers its final changed rows.
    private void InstallPresentation(WorldDefinition definition) {
        WorldBootWork.Count(kind: WorldBootWork.PresentationInstalls);
        m_pendingPresentationBasis ??= m_definition;
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

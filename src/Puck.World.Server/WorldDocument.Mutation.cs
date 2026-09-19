using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    /// <summary>Composes, validates and applies one mutation through the ordinary pipeline.</summary>
    /// <remarks>The pipeline is two steps. <see cref="TryPrepareMutation"/> runs every gate that can refuse and
    /// moves nothing; <see cref="InstallPrepared"/> installs what it produced and refuses nothing. A caller that
    /// must know a mutation will land before it commits something else prepares first and installs after.</remarks>
    /// <param name="mutation">The mutation.</param>
    /// <param name="tick">The simulation tick it applies at.</param>
    /// <param name="engineTick">The engine tick it applies at.</param>
    /// <param name="connectionId">The submitting envelope's connection id.</param>
    /// <param name="correlationId">The submitting envelope's correlation id.</param>
    /// <param name="preMetered">Whether the ingress already charged the dispatch budget.</param>
    /// <returns>Whether the mutation applied.</returns>
    internal bool TryApplyMutation(WorldMutation mutation, ulong tick, ulong engineTick, int connectionId, long correlationId, bool preMetered) {
        m_lastMutationFailureDetail = null;

        if (!TryPrepareMutation(
            current: m_definition,
            denied: out var denied,
            engineTick: engineTick,
            mutation: mutation,
            preMetered: preMetered,
            prepared: out var prepared,
            reason: out var reason,
            tick: tick
        )) {
            if (denied) {
                m_lastMutationFailureDetail = reason;

                if (Host.Output.HasNarrationSink) {
                    Host.Output.Narrate(channel: "world.grant denied", text: $"[world.grant denied: {mutation.Principal.Describe()} {reason} — {WorldServer.Describe(mutation: mutation)} dropped]");
                }
                Host.EchoTap?.Invoke(obj: new WorldEditEcho(
                    Message: $"{WorldServer.Describe(mutation: mutation)} denied: {reason}",
                    Rejected: true,
                    Kind: WorldEditEchoKind.Mutation,
                    Mutation: mutation,
                    Denied: true,
                    ConnectionId: connectionId,
                    CorrelationId: correlationId
                ));
            } else {
                Reject(
                    connectionId: connectionId,
                    correlationId: correlationId,
                    mutation: mutation,
                    reason: reason
                );
            }

            return false;
        }

        InstallPrepared(
            connectionId: connectionId,
            correlationId: correlationId,
            prepared: prepared
        );

        return true;
    }
    /// <summary>Runs every gate a mutation can be refused at, against a document the caller names, and moves
    /// nothing: authority through the one admission predicate, composition, whole-document validation, the render
    /// envelope and field capacities, the solid field build, and the addon and machine staging.</summary>
    /// <param name="mutation">The mutation.</param>
    /// <param name="current">The document the mutation composes against: the installed one, or the one that will
    /// be installed when the prepared mutation is.</param>
    /// <param name="tick">The simulation tick it applies at.</param>
    /// <param name="engineTick">The engine tick it applies at.</param>
    /// <param name="preMetered">Whether the ingress already charged the dispatch budget.</param>
    /// <param name="prepared">Everything the install needs. The caller installs it or disposes it.</param>
    /// <param name="reason">Why the mutation was refused.</param>
    /// <param name="denied">Whether the refusal is the admission predicate's.</param>
    /// <returns>Whether the mutation can be installed.</returns>
    internal bool TryPrepareMutation(WorldMutation mutation, WorldDefinition current, ulong tick, ulong engineTick, bool preMetered, out WorldPreparedMutation prepared, out string reason, out bool denied) {
        denied = false;
        prepared = null!;

        // The one admission predicate decides the whole authority question: section hold, the Mutate/section kind
        // mask, the row-scoped Edit hold and its mask, and the untrusted per-tick dispatch budget. Every
        // ordered-domain ingress converges here, so the peer door gets the masks and metering the addon seam has.
        // preMetered says only whether this ingress already charged the dispatch; it never changes which rules run.
        if (!TryAdmitCompleteMutation(admission: out var admission, mutation: mutation, preMetered: preMetered)) {
            denied = true;
            reason = admission.Describe();

            return false;
        }

        if (!TryCompose(
            current: current,
            mutation: mutation,
            tick: tick,
            engineTick: engineTick,
            instanceIdentity: Host.InstanceIdentity,
            candidate: out var candidate,
            reason: out reason,
            evictedKey: out var evictedKey
        )) {
            return false;
        }

        var installsArena = RequiresFullInstall(
            candidate: candidate,
            mutation: mutation,
            previous: current
        );
        StateArena? arena = null;

        // A malformed declaration must be named by the validator before StateCatalog or StateArena construction
        // can throw while preparing it. Settling can replace derived cells and clocks, so the exact settled
        // candidate is validated again below and owns the retained compilation.
        if (
            installsArena &&
            !TryValidateMutationCandidate(
            candidate: candidate,
            compilation: out _,
            mutation: mutation,
            reason: out reason,
            retainCompilation: false
        )) {
            return false;
        }
        if (
            installsArena &&
            !Host.TryPrepareArenaReplacement(
            definition: candidate,
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

        if (!TryValidateMutationCandidate(candidate: candidate, mutation: mutation, reason: out reason, compilation: out var compilation)) {
            return false;
        }
        // A value-only candidate reuses the live arena rather than carrying a prepared replacement. Validate its
        // complete import against that arena now, while a rule firing's scope is still open, so retained orphan keys
        // and visibility payload can refuse before commit. Direct mutations install immediately after preparation;
        // a rule commit closes the same unchanged scope, so the following TryLoad has the same admission inputs.
        if (
            !installsArena &&
            !Host.Arena.TryValidateLoad(
            rows: candidate.State,
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

        if (
            AffectsRenderEnvelope(mutation: mutation) &&
            !Host.Envelope.TryFit(
            candidate: candidate,
            reason: out reason
        )
        ) {
            return false;
        }

        if (!Host.Population.CanInstallFields(
            definition: candidate,
            reason: out var fieldReason
        )) {
            reason = fieldReason!;

            return false;
        }

        // The SDF contact field is built here, before install, so the warp-free evaluator's excluded-op ceiling is
        // a refusal rather than a constructor throw at install. Only a solid-affecting mutation rebuilds it;
        // otherwise the live field carries forward untouched.
        var solids = m_solids;
        var solidAffecting = AffectsSolidField(mutation: mutation);

        if (solidAffecting) {
            // A SetCollision edit touches only the collision tuning row, so the compiled SDF program is
            // byte-identical: when the live field exists and the candidate still needs it, the existing evaluator
            // is re-wrapped with the new scalars instead of recompiled. Every other solid-affecting edit, and a
            // requirement-selection flip, rebuilds from scratch.
            if (
                (mutation is WorldMutation.SetCollision) &&
                (m_solids is { } live) &&
                WorldContactSelection.RequiresField(collision: candidate.Collision)
            ) {
                solids = live.WithTuning(tuning: FixedWorldCollision.Compile(collision: candidate.Collision));
            } else if (!TryBuildSolids(
                definition: candidate,
                reason: out reason,
                solids: out solids
            )) {
                return false;
            }
        }

        // The addon and machine hosts stage their resources against the same candidate, last. A plan is set only on
        // success, and a refusal or a throw after one was obtained disposes it, so nothing staged outlives a
        // mutation that does not install. A server with no addon host attached refuses an addon-affecting mutation
        // by name rather than accepting a no-op.
        IWorldAddonPreparedPlan? addonPlan = null;
        IWorldMachinePreparedPlan? machinePlan = null;
        int[]? tickWrittenEntity = null;
        WorldPrincipal[]? tickWrittenPrincipal = null;
        bool[]? tickCollided = null;
        var staged = false;

        try {
            if (AffectsAddons(mutation: mutation)) {
                if (Host.Addons is not { } addons) {
                    reason = "no addon host is attached to this server — addon-affecting mutations are refused";

                    return false;
                }

                if (!addons.TryPrepare(
                    candidate: candidate,
                    current: current,
                    plan: out addonPlan,
                    reason: out var addonReason
                )) {
                    reason = (addonReason ?? "addon preparation refused");

                    return false;
                }

                if (addonPlan is not null) {
                    StageAddonContentionArrays(
                        mountedCount: addonPlan.MountedCount,
                        entity: out tickWrittenEntity,
                        principal: out tickWrittenPrincipal,
                        collided: out tickCollided
                    );
                }
            }

            if (
                (AffectsScreens(mutation: mutation) || AffectsMachines(mutation: mutation)) &&
                !Host.Machines.TryPrepare(
                    candidate: candidate,
                    current: current,
                    plan: out machinePlan,
                    reason: out var machineReason
                )
            ) {
                reason = (machineReason ?? "screen machine preparation refused");

                return false;
            }

            prepared = new WorldPreparedMutation(
                addonPlan: addonPlan,
                arena: arena,
                candidate: candidate,
                compilation: compilation,
                engineTick: engineTick,
                evictedKey: evictedKey,
                machinePlan: machinePlan,
                mutation: mutation,
                solidAffecting: solidAffecting,
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
    internal void InstallPrepared(WorldPreparedMutation prepared, int connectionId, long correlationId) {
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
                prepared.SolidAffecting &&
                !ReferenceEquals(
                objA: prepared.Solids,
                objB: m_solids
            )
            ) {
                m_solids = prepared.Solids;
                m_solidRevision++;
            }

            // A scalar state write keeps the compiled rules, catalog, groups and machines only while the candidate
            // still carries the state catalog identity the rules were compiled against. A document-value refresh or
            // a slot-to-keyed reshape mints a new one, and a look-assignment rebind needs the population rebuild,
            // so those take the full install like every other mutation.
            if (prepared.Arena is null) {
                InstallRuntimeStateValue(definition: candidate, mutation: mutation);
            } else {
                Install(
                    arena: prepared.Arena,
                    definition: candidate,
                    compilation: prepared.Compilation,
                    rebuildPopulation: (AffectsPopulation(mutation: mutation) || RefreshesLookAssignment(
                    candidate: candidate,
                    mutation: mutation
                ) || (prepared.SolidAffecting && WorldContactSelection.RequiresField(collision: candidate.Collision)))
                );
            }

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

        m_journal.Add(item: new WorldJournalEntry(
            EngineTick: engineTick,
            Mutation: mutation,
            Tick: tick
        ));
        Host.MutationJournalTap?.Invoke(tick, engineTick, mutation);

        if (addonPlan is not null) {
            Host.Addons!.Finish(plan: addonPlan);
        }

        if (machinePlan is not null) {
            Host.Machines.Finish(plan: machinePlan);
        }

        // A defaults-class mutation edits what the NEXT boot wakes on while the live
        // session levers keep their values (world.save folds them); every other mutation applies live on delivery.
        // SetAuthoringDefaults is the honest exception to the binary split: ONE whole-row mutation carries BOTH
        // classes at once (WorldPlacementPolicyDefaults' own remarks name which field is which) — the headroom/repeat-cap
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
            ? " — document default (next boot; live levers unchanged)"
            : string.Empty)}",
        };

        // An Evicts row's overflow policy dropped a cell to make room — named on the SAME echo line rather than a
        // separate one, so an eviction can never scroll past unnoticed the way a second stderr line could.
        if (prepared.EvictedKey is { } evicted) {
            message = $"{message} (evicted '{evicted}')";
        }

        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.mutation", text: $"[world.mutation: {message}]");
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

    // A state-value mutation can reuse every compiled product only while it keeps the catalog identity and does
    // not rebind a population look. All other mutations install a replacement prepared alongside the document.
    private bool RequiresFullInstall(WorldDefinition candidate, WorldMutation mutation, WorldDefinition previous) => !(
        IsStateMutation(mutation: mutation) &&
        ReferenceEquals(objA: candidate.StateCatalog, objB: previous.StateCatalog) &&
        !RefreshesLookAssignment(candidate: candidate, mutation: mutation)
    );
}

using System.Diagnostics.CodeAnalysis;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    /// <summary>What a caller hands the one install door (<see cref="TryStageInstall"/>): a candidate that has passed
    /// every gate of the caller's own (composition, validation, a replay), and what only the caller knows about how it
    /// installs.</summary>
    /// <param name="Candidate">The document to install: settled, when <see cref="Arena"/> is the arena prepared for
    /// it.</param>
    /// <param name="Current">The document the candidate was made from, which the hosts' preparations and the authority
    /// transition compare it against.</param>
    /// <param name="Kind">What the install rebuilds.</param>
    /// <param name="Products">The compiled products the install rebuilds (<see cref="PlanProducts"/>).</param>
    /// <param name="Principal">The principal the authority transition is prepared for.</param>
    internal readonly record struct InstallRequest(WorldDefinition Candidate, WorldDefinition Current, WorldInstallKind Kind, WorldProductPlan Products, Principal Principal) {
        /// <summary>Gets the admission that validated the candidate, or the document <see cref="Arena"/>'s settlement
        /// made the candidate from, which changed its state rows alone. The addon and machine hosts prepare against
        /// the admitted document, so the machine host reuses its proofs. <see langword="null"/> for the hosts to
        /// prepare against <see cref="Candidate"/>, which the machine host then validates itself.</summary>
        public WorldDefinitionAdmission? Admission { get; init; }
        /// <summary>Gets the arena the install adopts: the live arena when the install keeps it, a replacement prepared
        /// for <see cref="Candidate"/>, or <see langword="null"/> for an install that loads the live arena in place.</summary>
        public StateArena? Arena { get; init; }
        /// <summary>Gets the rules compiled for <see cref="Candidate"/>, or <see langword="null"/> to compile them at
        /// the install.</summary>
        public WorldRuleCompilation? Compilation { get; init; }
        /// <summary>Gets what the install keeps of the rule latches and scoped replays; <see cref="WorldArenaContinuity.Load"/>
        /// also restarts every field lattice with the state it replaces.</summary>
        public WorldArenaContinuity Continuity { get; init; }
        /// <summary>Gets whether the change writes the grants section itself.</summary>
        public bool GrantsWrite { get; init; }
        /// <summary>Gets the mutation a value install reads its touched rows from.</summary>
        public WorldMutation? Mutation { get; init; }
        /// <summary>Gets the whole-document rebuild this install is, or <see langword="null"/> for a mutation or an
        /// undo. A load or a reset restarts the input runtime and starts every body's action state afresh.</summary>
        public WorldRebuildKind? Rebuild { get; init; }
        /// <summary>Gets whether the authority transition replaces the grant table (a load, a reset, or a reload that
        /// moves grants or admission) instead of applying the candidate's change to it.</summary>
        public bool ResetAuthority { get; init; }
    }

    /// <summary>A candidate the one install door staged: every refusal has run, nothing live has changed, and the addon
    /// and machine hosts hold the plans they prepared for it. <see cref="CommitInstall"/> installs it, and disposal
    /// releases whichever plan no host committed.</summary>
    /// <remarks>Only <see cref="TryStageInstall"/> makes one, and only <see cref="CommitInstall"/> commits its plans,
    /// so no caller commits a plan before the document installs or disposes one a host already holds.</remarks>
    internal sealed class DocumentStage : IDisposable {
        private bool m_addonCommitted;
        private bool m_disposed;
        private bool m_machineCommitted;

        internal DocumentStage(in InstallRequest request, WorldGrants.PreparedAuthorityTransition authority, WorldSolidField? solids) {
            Request = request;
            Authority = authority;
            Solids = solids;
        }

        internal IWorldAddonPreparedPlan? AddonPlan { get; init; }
        internal WorldGrants.PreparedAuthorityTransition Authority { get; }
        // The per-tick contention arrays sized for the addon plan's mounted count; all null when the count kept its size.
        internal (int[] Entity, Principal[] Principal, bool[] Collided)? Contention { get; init; }
        internal IWorldMachinePreparedPlan? MachinePlan { get; init; }
        // What the envelope's fit and the hosts' preparations read of the candidate, each null when the install keeps
        // that product.
        internal (WorldDocumentInputs? Envelope, WorldDocumentInputs? Addons, WorldDocumentInputs? Machines) ProductInputs { get; init; }
        internal InstallRequest Request { get; }
        internal WorldSolidField? Solids { get; }

        /// <summary>Gets what the install rebuilds, decided against the document the candidate was made from.</summary>
        public WorldInstallKind Kind => Request.Kind;

        internal void Commit(WorldServer host) {
            if (AddonPlan is { } addons) {
                host.Addons!.Commit(plan: addons);
                m_addonCommitted = true;
                if (Contention is { } contention) {
                    host.AdoptContentionArrays(collided: contention.Collided, entity: contention.Entity, principal: contention.Principal);
                }
            }
            if (MachinePlan is { } machines) {
                host.Machines.Commit(plan: machines);
                m_machineCommitted = true;
            }
        }
        internal void Finish(WorldServer host) {
            if (m_addonCommitted) { host.Addons!.Finish(plan: AddonPlan!); }
            if (m_machineCommitted) { host.Machines.Finish(plan: MachinePlan!); }
            if (!ReferenceEquals(objA: Request.Current.Admission, objB: Request.Candidate.Admission)) {
                host.NotifyAdmissionChanged();
            }
        }
        /// <summary>Releases each plan no host committed. A committed plan is its host's, which
        /// <see cref="FinishInstall"/> finishes.</summary>
        public void Dispose() {
            if (m_disposed) { return; }
            m_disposed = true;
            if (!m_addonCommitted) { AddonPlan?.Dispose(); }
            if (!m_machineCommitted) { MachinePlan?.Dispose(); }
        }
    }

    /// <summary>Commits an addon host's prepared plan and finishes it at once: the pair a boot or a restore runs when
    /// no document or journal publication of its own sits between them, as <see cref="DocumentStage"/> does for an
    /// install.</summary>
    /// <param name="host">The addon host that prepared <paramref name="plan"/>.</param>
    /// <param name="plan">A plan the host prepared and no one has committed or disposed.</param>
    internal static void CommitAndFinish(IWorldAddonHost host, IWorldAddonPreparedPlan plan) {
        host.Commit(plan: plan);
        host.Finish(plan: plan);
    }
    /// <summary>Commits a machine host's prepared plan and finishes it at once (see
    /// <see cref="CommitAndFinish(IWorldAddonHost, IWorldAddonPreparedPlan)"/>).</summary>
    /// <param name="host">The machine host that prepared <paramref name="plan"/>.</param>
    /// <param name="plan">A plan the host prepared and no one has committed or disposed.</param>
    internal static void CommitAndFinish(IWorldMachineHost host, IWorldMachinePreparedPlan plan) {
        host.Commit(plan: plan);
        host.Finish(plan: plan);
    }

    /// <summary>Stages a candidate's install: the one door a mutation, a whole-document rebuild and a full undo run
    /// every refusal of an install through, in one order, before anything live changes.</summary>
    /// <remarks>The order is the cheap capacity checks (the boot-reserved face screens, the render envelope's fit, the
    /// boot-allocated field lattices), the solid field's build, the addon host's preparation, the machine host's, the
    /// input runtime's mode selection, and the authority transition. A host's plan exists only once its preparation
    /// accepted, and a later refusal or a throw disposes it, so a refusal leaves nothing staged and nothing
    /// changed.</remarks>
    /// <param name="request">The candidate and how it installs.</param>
    /// <param name="stage">The staged install, for <see cref="CommitInstall"/> or disposal.</param>
    /// <param name="reason">The refusal, naming the step that refused; empty on success.</param>
    /// <returns>Whether every step accepted the candidate.</returns>
    internal bool TryStageInstall(in InstallRequest request, [NotNullWhen(returnValue: true)] out DocumentStage? stage, out string reason) {
        var candidate = request.Candidate;
        // The hosts prepare against the document the admission validated, so its machine proofs apply to it.
        var hosted = (request.Admission?.Definition ?? candidate);
        var products = request.Products;

        stage = null;
        if (ExceedsBootDerivedFaceReservation(candidate: candidate, reason: out reason)) { return false; }

        WorldDocumentInputs? envelopeInputs = null;

        if (!products.Envelope.IsEmpty) {
            if (!TryFitEnvelope(candidate: candidate, inputs: out var fitted, reason: out reason)) { return false; }
            envelopeInputs = fitted;
        }
        if (!Host.Population.CanInstallFields(definition: candidate, reason: out var fieldReason)) {
            reason = $"the field runtime is incompatible — {fieldReason}";
            return false;
        }
        if (!TryStageSolids(candidate: candidate, products: products, reason: out reason, solids: out var solids)) { return false; }

        IWorldAddonPreparedPlan? addonPlan = null;
        IWorldMachinePreparedPlan? machinePlan = null;
        WorldDocumentInputs? addonInputs = null;
        WorldDocumentInputs? machineInputs = null;
        (int[] Entity, Principal[] Principal, bool[] Collided)? contention = null;

        try {
            if (products.Addons.IsEmpty) {
                // The addon host's last preparation read no part this candidate changed.
            } else if (Host.Addons is not { } addons) {
                // A server with no addon host attached refuses a candidate holding an enabled addon row by name
                // rather than installing a document that claims a mounted addon no host can run.
                if (TryFindEnabledAddonRow(candidate: hosted, name: out var enabledRowName)) {
                    reason = $"addon '{enabledRowName}' cannot mount — no addon host is attached to this server";
                    return false;
                }
            } else {
                if (!TryPrepareAddons(addons: addons, candidate: hosted, current: request.Current, inputs: out var preparedAddons, plan: out addonPlan, reason: out var addonReason)) {
                    reason = $"addon {(addonReason ?? "preparation refused")}";
                    return false;
                }
                addonInputs = preparedAddons;
                if (addonPlan is not null) {
                    StageAddonContentionArrays(mountedCount: addonPlan.MountedCount, entity: out var entity, principal: out var principal, collided: out var collided);
                    if (entity is not null) { contention = (entity, principal!, collided!); }
                }
            }
            if (!products.Machines.IsEmpty) {
                if (!TryPrepareMachines(admission: request.Admission, candidate: hosted, current: request.Current, inputs: out var preparedMachines, plan: out machinePlan, reason: out var machineReason)) {
                    reason = $"screen machine {(machineReason ?? "preparation refused")}";
                    return false;
                }
                machineInputs = preparedMachines;
            }
            if (!Host.Input.TryValidateDefinition(candidate, request.Arena, out reason, fresh: (request.Rebuild is WorldRebuildKind.Load or WorldRebuildKind.Reset))) { return false; }
            if (!(request.ResetAuthority
                ? Host.GrantTable.TryPrepareReset(actor: request.Principal, candidate: candidate, current: request.Current, prepared: out var authority, reason: out reason)
                : Host.GrantTable.TryPrepareDocument(actor: request.Principal, candidate: candidate, current: request.Current, grantsWrite: request.GrantsWrite, prepared: out authority, reason: out reason))) {
                return false;
            }

            stage = new DocumentStage(request: in request, authority: authority!, solids: solids) {
                AddonPlan = addonPlan,
                Contention = contention,
                MachinePlan = machinePlan,
                ProductInputs = (envelopeInputs, addonInputs, machineInputs),
            };
            reason = string.Empty;
            return true;
        } finally {
            if (stage is null) {
                addonPlan?.Dispose();
                machinePlan?.Dispose();
            }
        }
    }

    /// <summary>Installs a staged candidate: adopts its solid field, swaps the live definition and rebuilds what the
    /// install's kind rebuilds, repaints or restarts the field lattices, then commits the addon host's plan and the
    /// machine host's, in that order. Nothing here refuses, and every exit releases whichever plan no host
    /// committed.</summary>
    /// <remarks>The caller publishes its own record of the change (a journal entry, a moved base, a shortened journal)
    /// and then calls <see cref="FinishInstall"/>, so nothing a host does at its finish can still be unwound.</remarks>
    /// <param name="stage">What <see cref="TryStageInstall"/> staged, against the document installed now.</param>
    internal void CommitInstall(DocumentStage stage) {
        var request = stage.Request;
        var candidate = request.Candidate;
        var previous = m_definition;

        try {
            if (request.Rebuild is WorldRebuildKind.Load or WorldRebuildKind.Reset) { Host.Input.Restart(); }
            // The candidate's field is published immediately before the install rebuilds bodies against it.
            SwapSolids(solids: stage.Solids);
            switch (request.Kind) {
                case WorldInstallKind.Value:
                    stage.Authority.Install();
                    InstallRuntimeStateValue(definition: candidate, mutation: request.Mutation!, arena: request.Arena);
                    break;
                case WorldInstallKind.Presentation:
                    stage.Authority.Install();
                    InstallPresentation(definition: candidate);
                    NarrateProducts(plan: request.Products, population: false);
                    break;
                default:
                    Install(
                        arena: request.Arena,
                        authority: stage.Authority,
                        compilation: request.Compilation,
                        continuity: request.Continuity,
                        definition: candidate,
                        products: request.Products,
                        rebuildKind: request.Rebuild
                    );
                    NarrateProducts(plan: request.Products, population: request.Products.RebuildsPopulation);
                    break;
            }
            AdoptProductInputs(addons: stage.ProductInputs.Addons, envelope: stage.ProductInputs.Envelope, machines: stage.ProductInputs.Machines);
            // An install that replaces the document's state whole restarts every lattice with it. Any other keeps each
            // lattice's evolved cells and repaints only a row whose draw pass the change moved: a Generate, or a row
            // that re-authored its cursor, drawn masks or fill.
            if (request.Continuity == WorldArenaContinuity.Load) {
                Host.ReseedLatticeFields(definition: candidate);
            } else {
                Host.RepaintChangedLatticeDraws(current: candidate, previous: previous);
            }
            stage.Commit(host: Host);
        } finally {
            stage.Dispose();
        }
    }

    /// <summary>Finishes the plans <see cref="CommitInstall"/> committed, addons then machines: registration,
    /// disclosure narration and the disposal of what the install superseded. Runs after the caller published its own
    /// record of the change and after the candidate's grants installed, so a host's narration reads the live grant
    /// table and nothing it does can still be unwound.</summary>
    /// <param name="stage">The committed stage.</param>
    internal void FinishInstall(DocumentStage stage) => stage.Finish(host: Host);

    /// <summary>Reinstalls the definition a replay's starting checkpoint was captured under, with its solid field, when
    /// the run edited its configuration since. It is a step of that checkpoint's restore, not an install of its own:
    /// the restore that follows replaces the arena and every host's state, and the caller refused everything the
    /// checkpoint can refuse before calling.</summary>
    /// <param name="definition">The checkpoint's definition.</param>
    /// <param name="compilation">The rules compiled for it.</param>
    /// <param name="authority">The prepared authority transition.</param>
    /// <param name="solids">The solid field built for it.</param>
    /// <param name="solidRevision">The captured solid field revision.</param>
    internal void InstallReplayStart(WorldDefinition definition, WorldRuleCompilation compilation, WorldGrants.PreparedAuthorityTransition authority, WorldSolidField? solids, int solidRevision) {
        RestoreSolidField(solids: solids, revision: solidRevision);
        Install(
            arena: null,
            authority: authority,
            compilation: compilation,
            continuity: WorldArenaContinuity.Edit,
            definition: definition,
            products: PlanProducts(candidate: definition, previous: m_definition, whole: true),
            rebuildKind: null
        );
    }

    /// <summary>Prepares the arena an install of a document's state whole adopts: none when the install reloads the
    /// live arena in place, otherwise a replacement born with the whole document.</summary>
    /// <param name="candidate">The document whose state installs whole.</param>
    /// <param name="arena">The prepared replacement, or <see langword="null"/> for an in-place reload.</param>
    /// <param name="settled">The candidate as the prepared arena settles it; the candidate itself for an in-place
    /// reload.</param>
    /// <param name="reason">The refusal, or empty.</param>
    /// <returns>Whether the candidate's state loads into an arena.</returns>
    internal bool TryPrepareWholeArena(WorldDefinition candidate, out StateArena? arena, out WorldDefinition settled, out string reason) {
        arena = null;
        settled = candidate;
        reason = string.Empty;
        if (Host.ReloadsArenaInPlace(definition: candidate)) { return true; }
        if (Host.TryPrepareArenaReplacement(definition: candidate, prepared: out var prepared, reason: out var arenaReason, settled: out settled)) {
            arena = prepared;
            return true;
        }
        reason = $"the state section does not load into an arena: {arenaReason}";
        return false;
    }
}

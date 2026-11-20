namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // What each compiled product was last built from, recorded by the build itself (WorldDocumentReads): the render
    // envelope's fit, the addon host's and the machine host's prepares. Null until a recorded build, which a plan reads
    // as every part, so a product never stands stale for want of a recording. The population records its own
    // (WorldPopulation.BuiltFrom), and the solid field's is derived from the field it holds (SolidInputs).
    private WorldDocumentInputs? m_envelopeInputs;
    private long m_envelopeRevision;
    private WorldDocumentInputs? m_addonInputs;
    private WorldDocumentInputs? m_machineInputs;
    private WorldDocumentInputs? m_solidInputs;

    /// <summary>Returns which compiled products an install of <paramref name="candidate"/> over
    /// <paramref name="previous"/> rebuilds: each product whose recorded inputs hold a part the candidate changed.</summary>
    /// <remarks>The one rule a mutation's install, a reload's install and an undo's replay share. A product's inputs
    /// are what its last build read of the document, so a product built from no part the candidate changed would be
    /// built again into the same product, and is kept.</remarks>
    /// <param name="previous">The document the candidate was made from: the live document, or the document a mutation
    /// composed over.</param>
    /// <param name="candidate">The candidate.</param>
    /// <param name="whole">Whether the install replaces the document whole (a load or a reset), which changes every
    /// part.</param>
    /// <returns>The plan.</returns>
    internal WorldProductPlan PlanProducts(WorldDefinition previous, WorldDefinition candidate, bool whole = false) {
        var changed = (whole ? WorldDocumentInputs.All : WorldDocumentInputs.Changed(before: previous, after: candidate));
        var solids = changed.Intersect(other: SolidInputsOf(definition: previous));

        return new WorldProductPlan(
            Addons: changed.Intersect(other: (m_addonInputs ?? WorldDocumentInputs.All)),
            Changed: changed,
            Envelope: changed.Intersect(other: EnvelopeInputs),
            Input: changed.Intersect(other: Host.Input.BuiltFrom),
            Machines: changed.Intersect(other: (m_machineInputs ?? WorldDocumentInputs.All)),
            Population: changed.Intersect(other: Host.Population.BuiltFrom),
            Solids: solids,
            // A rebuilt field reaches the population, which compiles its contact and line-of-sight consumers from
            // it, whenever the field it builds or replaces is one: the field is built when the candidate needs one,
            // and replaced when the live document holds one.
            SolidsReachPopulation: (!solids.IsEmpty && ((m_solids is not null) || RequiresSolidField(definition: candidate)))
        );
    }

    // What the envelope's last fit read, as long as the measurers that fit consulted are the ones registered now: a
    // measurer registered since reads parts the fit never saw, so the next install fits again.
    private WorldDocumentInputs EnvelopeInputs =>
        ((m_envelopeInputs is { } fitted) && (m_envelopeRevision == Host.Envelope.Revision)) ? fitted : WorldDocumentInputs.All;

    // The solid field's inputs: what deciding whether the document needs a field reads, and what the field held was
    // built from. Derived from the field the document holds, so every door that adopts a field (a mutation, a reload,
    // an undo, a response swap, a checkpoint) keeps them without carrying them.
    private WorldDocumentInputs SolidInputsOf(WorldDefinition definition) {
        if (m_solidInputs is { } held) { return held; }
        _ = WorldDocumentReads.Record(build: () => RequiresSolidField(definition: definition), inputs: out var requirement);

        var inputs = (requirement | (m_solids?.BuiltFrom ?? WorldDocumentInputs.None));

        m_solidInputs = inputs;
        return inputs;
    }

    // The one door a solid field is adopted through, so its inputs are derived again from the field it holds.
    private void SetSolids(WorldSolidField? solids) {
        m_solids = solids;
        m_solidInputs = null;
    }

    // Whether a document needs the SDF solid field: for contact under the field provider, or for line of sight.
    internal static bool RequiresSolidField(WorldDefinition definition) =>
        (WorldContactSelection.RequiresField(collision: definition.Collision) || WorldTargetSelection.RequiresLineOfSight(definition: definition));

    // Builds the solid field an install's plan asks for, before install, so the warp-free evaluator's excluded-op
    // ceiling is a refusal rather than a constructor throw; a plan that keeps the field answers the live one. The
    // collision tuning alone leaves the compiled program as it is: when the live field exists and the candidate still
    // needs one, the existing evaluator is re-wrapped with the new scalars.
    internal bool TryStageSolids(WorldProductPlan products, WorldDefinition candidate, out WorldSolidField? solids, out string reason) {
        solids = m_solids;
        reason = string.Empty;
        if (products.Solids.IsEmpty) { return true; }
        if (products.SolidsRetuneAlone && (m_solids is { } live) && WorldContactSelection.RequiresField(collision: candidate.Collision)) {
            solids = live.WithTuning(tuning: FixedWorldCollision.Compile(collision: candidate.Collision));
            return true;
        }
        return TryBuildSolids(definition: candidate, reason: out reason, solids: out solids);
    }

    // Runs the render envelope's fit over a candidate, recording what it read; the inputs are adopted only when the
    // candidate installs.
    private bool TryFitEnvelope(WorldDefinition candidate, out WorldDocumentInputs inputs, out string reason) {
        var recording = WorldDocumentReads.Begin();
        bool fits;

        try {
            fits = Host.Envelope.TryFit(candidate: candidate, reason: out reason);
        } finally {
            inputs = recording.End();
        }
        return fits;
    }

    // Prepares the addon host against a candidate, recording what it read.
    internal static bool TryPrepareAddons(IWorldAddonHost addons, WorldDefinition? current, WorldDefinition candidate, out IWorldAddonPreparedPlan? plan, out WorldDocumentInputs inputs, out string? reason) {
        var recording = WorldDocumentReads.Begin();

        try {
            return addons.TryPrepare(candidate: candidate, current: current, plan: out plan, reason: out reason);
        } finally {
            inputs = recording.End();
        }
    }

    // Prepares the machine host against a candidate, recording what it read.
    internal bool TryPrepareMachines(WorldDefinition? current, WorldDefinition candidate, out IWorldMachinePreparedPlan? plan, out WorldDocumentInputs inputs, out string? reason, WorldDefinitionAdmission? admission = null) {
        var recording = WorldDocumentReads.Begin();

        try {
            return Host.Machines.TryPrepare(admission: admission, candidate: candidate, current: current, plan: out plan, reason: out reason);
        } finally {
            inputs = recording.End();
        }
    }

    // Adopts what an installed candidate's product builds read. A product the install kept keeps its inputs.
    private void AdoptProductInputs(WorldDocumentInputs? envelope, WorldDocumentInputs? addons, WorldDocumentInputs? machines) {
        if (envelope is { } fitted) {
            m_envelopeInputs = fitted;
            m_envelopeRevision = Host.Envelope.Revision;
        }
        if (addons is { } prepared) { m_addonInputs = prepared; }
        if (machines is { } machine) { m_machineInputs = machine; }
    }

    /// <summary>Records what the machine host's boot preparation read, so the first install judges the machines by
    /// it.</summary>
    /// <param name="inputs">What the boot preparation read.</param>
    internal void AdoptBootMachineInputs(WorldDocumentInputs inputs) => m_machineInputs = inputs;

    /// <summary>Begins checking the documents an undo replays: each must fit the render envelope, compile its solid
    /// field and prepare its addons, and each check runs only when the document changed a part the check last read
    /// (starting from the live document, which the live products already passed).</summary>
    /// <returns>The check.</returns>
    internal WorldProductReplay BeginProductReplay() => new(document: this);

    /// <summary>The checks an undo's replay runs on each document it composes (<see cref="BeginProductReplay"/>).</summary>
    internal sealed class WorldProductReplay {
        private readonly WorldDocument m_document;
        private (WorldDefinition Basis, WorldDocumentInputs Inputs) m_envelope;
        private (WorldDefinition Basis, WorldDocumentInputs Inputs) m_solids;
        private (WorldDefinition Basis, WorldDocumentInputs Inputs) m_addons;

        internal WorldProductReplay(WorldDocument document) {
            var live = document.m_definition;

            m_document = document;
            m_envelope = (live, document.EnvelopeInputs);
            m_solids = (live, document.SolidInputsOf(definition: live));
            m_addons = (live, (document.m_addonInputs ?? WorldDocumentInputs.All));
        }

        /// <summary>Checks one replayed document against the checks whose inputs it changed since the document each
        /// last passed on.</summary>
        /// <param name="next">The replayed document.</param>
        /// <param name="reason">The refusal, or empty.</param>
        /// <returns>Whether the document passes.</returns>
        public bool TryCheck(WorldDefinition next, out string reason) {
            reason = string.Empty;
            if (WorldDocumentInputs.Changed(before: m_envelope.Basis, after: next).Overlaps(other: m_envelope.Inputs)) {
                if (!m_document.TryFitEnvelope(candidate: next, inputs: out var fitted, reason: out reason)) { return false; }
                m_envelope = (next, fitted);
            }
            if (WorldDocumentInputs.Changed(before: m_solids.Basis, after: next).Overlaps(other: m_solids.Inputs)) {
                _ = WorldDocumentReads.Record(build: () => RequiresSolidField(definition: next), inputs: out var requirement);
                if (!TryBuildSolids(definition: next, reason: out reason, solids: out var solids)) { return false; }
                m_solids = (next, (requirement | (solids?.BuiltFrom ?? WorldDocumentInputs.None)));
            }
            // An intermediate document this pass never installs still owes proof it could have mounted, so a kept
            // entry whose pinned module has since gone missing refuses the whole undo. The probe plan is disposed at
            // once.
            if ((m_document.Host.Addons is { } addons) && WorldDocumentInputs.Changed(before: m_addons.Basis, after: next).Overlaps(other: m_addons.Inputs)) {
                if (!TryPrepareAddons(addons: addons, candidate: next, current: m_document.m_definition, inputs: out var prepared, plan: out var plan, reason: out var addonReason)) {
                    reason = (addonReason ?? string.Empty);
                    return false;
                }
                plan?.Dispose();
                m_addons = (next, prepared);
            }
            return true;
        }
    }

    /// <summary>Returns the plan an undo's install follows: the products whose inputs the restored document changed
    /// against the live one, by the rule every install shares.</summary>
    /// <param name="candidate">The restored document.</param>
    /// <returns>The plan.</returns>
    internal WorldProductPlan PlanUndo(WorldDefinition candidate) => PlanProducts(candidate: candidate, previous: m_definition);

    /// <summary>Adopts what an undo's installed product builds read and narrates its plan.</summary>
    /// <param name="products">The plan.</param>
    /// <param name="addons">What the addon preparation read, or <see langword="null"/> when it was kept.</param>
    /// <param name="machines">What the machine preparation read, or <see langword="null"/> when it was kept.</param>
    internal void FinishUndoProducts(WorldProductPlan products, WorldDocumentInputs? addons, WorldDocumentInputs? machines) {
        AdoptProductInputs(addons: addons, envelope: null, machines: machines);
        // The replay fitted the envelope over the documents it composed, not over the one installed, so a restored
        // document that changed what the last fit read is fitted again by the next install.
        if (!products.Envelope.IsEmpty) { m_envelopeInputs = null; }
        NarrateProducts(plan: (products with { Envelope = WorldDocumentInputs.None }), population: products.RebuildsPopulation);
    }

    // Narrates which products an install rebuilt and which parts of the document moved them.
    private void NarrateProducts(WorldProductPlan plan, bool population) {
        WorldBootWork.Count(kind: WorldBootWork.ProductPlans);
        if (!plan.Envelope.IsEmpty) { WorldBootWork.Count(kind: WorldBootWork.EnvelopeFits); }
        if (!plan.Solids.IsEmpty) { WorldBootWork.Count(kind: WorldBootWork.SolidBuilds); }
        if (!plan.Addons.IsEmpty) { WorldBootWork.Count(kind: WorldBootWork.AddonPrepares); }
        if (!plan.Machines.IsEmpty) { WorldBootWork.Count(kind: WorldBootWork.MachinePrepares); }
        if (population) { WorldBootWork.Count(kind: WorldBootWork.PopulationRebuilds); }
        if (Host.Output.HasNarrationSink) {
            Host.Output.Narrate(channel: "world.products", text: $"[world.products] changed {plan.Changed}; {plan.Describe(population: population)}");
        }
    }
}

/// <summary>Which compiled products an install rebuilds (<see cref="WorldDocument.PlanProducts"/>), each named by the
/// parts of the document that moved it: empty for a product the install keeps.</summary>
/// <param name="Changed">The parts the candidate changed.</param>
/// <param name="Envelope">The changed parts the render envelope's last fit read.</param>
/// <param name="Solids">The changed parts the solid field was built from.</param>
/// <param name="Addons">The changed parts the addon host's last preparation read.</param>
/// <param name="Machines">The changed parts the machine host's last preparation read.</param>
/// <param name="Population">The changed parts the population was built from.</param>
/// <param name="Input">The changed parts the input runtime and its owners were built from
/// (<see cref="WorldInputRuntime.BuiltFrom"/>).</param>
/// <param name="SolidsReachPopulation">Whether rebuilding the solid field hands the population a new field.</param>
internal readonly record struct WorldProductPlan(
    WorldDocumentInputs Changed,
    WorldDocumentInputs Envelope,
    WorldDocumentInputs Solids,
    WorldDocumentInputs Addons,
    WorldDocumentInputs Machines,
    WorldDocumentInputs Population,
    WorldDocumentInputs Input,
    bool SolidsReachPopulation
) {
    /// <summary>Gets whether the population rebuilds: a part it was built from changed, or it receives a new solid
    /// field.</summary>
    public bool RebuildsPopulation => (!Population.IsEmpty || SolidsReachPopulation);
    /// <summary>Gets whether the input runtime reconfigures its owners: a part they were built from changed, or the
    /// population rebuilt the bodies and the input catalog they read.</summary>
    public bool ReconfiguresInput => (!Input.IsEmpty || RebuildsPopulation);
    /// <summary>Gets whether the solid field's only changed input is the collision tuning, which leaves its compiled
    /// program as it is.</summary>
    public bool SolidsRetuneAlone => (Solids == WorldDocumentInputs.Of(WorldDocumentInput.Collision));

    /// <summary>Returns this plan for a presentation install, which changes only what shading reads and keeps every
    /// product but the render envelope's fit (<see cref="WorldDocument.InstallKindOf"/>).</summary>
    /// <returns>The plan with only its envelope fit.</returns>
    public WorldProductPlan ForPresentation() => (this with {
        Addons = WorldDocumentInputs.None,
        Input = WorldDocumentInputs.None,
        Machines = WorldDocumentInputs.None,
        Population = WorldDocumentInputs.None,
        Solids = WorldDocumentInputs.None,
        SolidsReachPopulation = false,
    });

    /// <summary>Returns the products the plan rebuilds and what moved each, then those it keeps.</summary>
    /// <param name="population">Whether the population rebuilt.</param>
    /// <returns>The description.</returns>
    public string Describe(bool population) {
        var rebuilt = new List<string>();
        var kept = new List<string>();

        Add(name: "envelope", parts: Envelope);
        Add(name: "solids", parts: Solids);
        Add(name: "addons", parts: Addons);
        Add(name: "machines", parts: Machines);
        if (population) { rebuilt.Add(item: (Population.IsEmpty ? "population (new solid field)" : $"population ({Population})")); }
        else { kept.Add(item: "population"); }
        if (!Input.IsEmpty) { rebuilt.Add(item: $"input ({Input})"); }
        else if (population) { rebuilt.Add(item: "input (rebuilt population)"); }
        else { kept.Add(item: "input"); }
        return $"rebuilt {((rebuilt.Count == 0) ? "nothing" : string.Join(separator: ", ", values: rebuilt))}; kept {((kept.Count == 0) ? "nothing" : string.Join(separator: ", ", values: kept))}";

        void Add(string name, WorldDocumentInputs parts) {
            if (parts.IsEmpty) { kept.Add(item: name); } else { rebuilt.Add(item: $"{name} ({parts})"); }
        }
    }
}

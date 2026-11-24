using Puck.Abstractions.Counting;

namespace Puck.World.Server;

/// <summary>
/// The work a tick spends scanning the population rather than stepping it, counted in one
/// <see cref="WorkCounterSet"/> (<see cref="Process"/>) and read under the source name <see cref="SourceName"/>:
/// the rule host's fact derivations, the continuation folds a state hash takes, and the perception images a flock
/// or a decision freezes.
/// </summary>
/// <remarks>Each count is a monotonic total over the ledger's life, never reset; a reader takes a window by reading
/// twice and subtracting. None is part of simulation state: nothing hashes, exports or checkpoints them. A count
/// that grows with the population while nothing in it changed names a scan that is not proportional to change.</remarks>
public static class WorldTickWork {
    /// <summary>The name a counters report heads this source's section with.</summary>
    public const string SourceName = "world.tick";

    /// <summary>Gets the kind counting the passes in which the rule host compared what bodies answer with what they
    /// last answered (<see cref="WorldRuleHost"/>'s fact versions): one before a rule family's walk, and one after
    /// each firing that delivered an arm.</summary>
    public static WorkKind FactDerives { get; } = new(name: "world.tick.fact-derives", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the population slots whose pose or facts a fact derivation compared.</summary>
    public static WorkKind FactSlots { get; } = new(name: "world.tick.fact-slots", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the occupants a rule's region sweep bound past the number the work sheet priced
    /// it at (<see cref="WorldPlacementRegion.Capacity"/>): one per body past it, per sweep. A count that keeps
    /// growing names a region whose capacity is authored too small.</summary>
    public static WorkKind RegionSweepOverflow { get; } = new(name: "world.tick.region-sweep-overflow", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting continuation folds: one per hash that folded bodies' simulation continuations
    /// (<see cref="WorldPopulation.AppendContinuationHash(ref Puck.Maths.Fnv1aHash, bool)"/>), the whole population's or a replay scope's.</summary>
    public static WorkKind ContinuationFolds { get; } = new(name: "world.tick.continuation-folds", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the population slots a continuation fold visited to find the ones it folds.</summary>
    public static WorkKind ContinuationSlots { get; } = new(name: "world.tick.continuation-slots", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the slots a continuation fold encoded through their sections' encoders.</summary>
    public static WorkKind ContinuationEncodes { get; } = new(name: "world.tick.continuation-encodes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting every byte a continuation fold encoded: the sections', the profiles' and a
    /// replay scope's tails'.</summary>
    public static WorkKind ContinuationBytes { get; } = new(name: "world.tick.continuation-bytes", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the profile projections a continuation fold encoded beside its slots.</summary>
    public static WorkKind ContinuationProfiles { get; } = new(name: "world.tick.continuation-profiles", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the bytes those projections encoded to.</summary>
    public static WorkKind ContinuationProfileBytes { get; } = new(name: "world.tick.continuation-profile-bytes", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the bytes a replay scope's fold encoded for its bodies' tails: the observation,
    /// the profile and the register values, which the whole-population fold reads elsewhere or not at all.</summary>
    public static WorkKind ContinuationTailBytes { get; } = new(name: "world.tick.continuation-tail-bytes", unit: "bytes", workClass: WorkClass.Deterministic);
    /// <summary>Gets, per folded section in <see cref="WorldContinuationSectionKind"/> order, the kind counting the
    /// times a continuation fold encoded that section.</summary>
    public static IReadOnlyList<WorkKind> SectionEncodes { get; } = SectionKinds(measure: "encodes", unit: "count");
    /// <summary>Gets, per folded section in <see cref="WorldContinuationSectionKind"/> order, the kind counting the
    /// bytes that section encoded to.</summary>
    public static IReadOnlyList<WorkKind> SectionBytes { get; } = SectionKinds(measure: "bytes", unit: "bytes");
    /// <summary>Gets, per section a holder keeps, the kind counting the raw bytes a digest read compared with the
    /// bytes the section's digest was taken from.</summary>
    public static IReadOnlyList<WorkKind> SectionComparedBytes { get; } = SectionKinds(measure: "compared-bytes", unit: "bytes");
    /// <summary>Gets, per section a holder keeps, the kind counting the digest reads that found a moved version over
    /// unchanged content: a write that changed nothing, answered by the compare without an encode.</summary>
    public static IReadOnlyList<WorkKind> SectionFilteredCompares { get; } = SectionKinds(measure: "filtered-compares", unit: "count");
    /// <summary>Gets, per section a holder keeps, the kind counting the encodes that produced the digest the section
    /// already had: a section's first move, before its holder keeps the bytes a later move is compared with.</summary>
    public static IReadOnlyList<WorkKind> SectionUnchangedEncodes { get; } = SectionKinds(measure: "unchanged-encodes", unit: "count");
    /// <summary>Gets the kind counting the perception images frozen: one per tick a population with a flock steps, and
    /// one per tick a rule family with a neighbour-reading decision evaluates.</summary>
    public static WorkKind PerceptionFreezes { get; } = new(name: "world.tick.perception-freezes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the bodies those images copied.</summary>
    public static WorkKind PerceptionPoints { get; } = new(name: "world.tick.perception-points", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the spatial neighbourhoods built over a frozen image, one per cell width a query
    /// asked for since the freeze.</summary>
    public static WorkKind PerceptionGrids { get; } = new(name: "world.tick.perception-grids", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the tag-row cells a <c>nearest</c> read walked.</summary>
    public static WorkKind NearestCells { get; } = new(name: "world.tick.nearest-cells", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the deal templates the placement deal sweep dealt again.</summary>
    public static WorkKind DealRedeals { get; } = new(name: "world.tick.deal-redeals", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Body candidates inspected by the recipient spatial grid.</summary>
    public static WorkKind InterestCandidates { get; } = new("world.tick.interest-candidates", "count", WorkClass.Deterministic);
    /// <summary>Body records serialized for distinct projected audiences and acknowledged baselines.</summary>
    public static WorkKind InterestBodies { get; } = new("world.tick.interest-bodies", "count", WorkClass.Deterministic);
    /// <summary>Bytes of body records serialized before sharing across streams.</summary>
    public static WorkKind InterestBytes { get; } = new("world.tick.interest-bytes", "bytes", WorkClass.Deterministic);
    /// <summary>Gets the kind counting the input batches from another process this authority applied.</summary>
    public static WorkKind FederatedBatches { get; } = new(name: "world.tick.federated-batches", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the events in those batches whose claimed capture time this authority moved to
    /// the tick that admitted them.</summary>
    public static WorkKind FederatedRetimed { get; } = new(name: "world.tick.federated-retimed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the batches dropped for naming an input epoch this authority had left.</summary>
    public static WorkKind FederatedStale { get; } = new(name: "world.tick.federated-stale", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the input streams this authority restarted after refusing a batch of the
    /// current epoch.</summary>
    public static WorkKind FederatedRestarts { get; } = new(name: "world.tick.federated-restarts", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the observations of a world's own producer that ran the whole admission:
    /// the route check, the owner lookup, the mode synchronization and the batch
    /// (<see cref="WorldInputRuntime"/>).</summary>
    public static WorkKind InputAdmissions { get; } = new(name: "world.tick.input-admissions", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the observations admitted as a batch over facts that stood: the image had
    /// changed and nothing the permission or selection steps read had.</summary>
    public static WorkKind InputAdmissionsStanding { get; } = new(name: "world.tick.input-admissions-standing", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the observations that repeated the last one over facts that stood, and so
    /// ran no admission.</summary>
    public static WorkKind InputAdmissionsSteady { get; } = new(name: "world.tick.input-admissions-steady", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the owners a permission refresh evaluated.</summary>
    public static WorkKind InputRefreshes { get; } = new(name: "world.tick.input-refreshes", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the owners a permission refresh passed over because their facts stood.</summary>
    public static WorkKind InputRefreshesStanding { get; } = new(name: "world.tick.input-refreshes-standing", unit: "count", workClass: WorkClass.Deterministic);
    /// <summary>Gets the kind counting the calls the host loop's thread makes into a host step: one for each place it
    /// begins, resumes or collects one. Read against <see cref="HostSteps"/>, it is how often the pump visits a
    /// step.</summary>
    public static WorkKind PumpVisits { get; } = new(name: "world.tick.pump-visits", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the host steps completed: the boot world's tick and every other world's ticks
    /// due with it.</summary>
    public static WorkKind HostSteps { get; } = new(name: "world.tick.host-steps", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the collects in which the stepping thread blocked on the host's step thread:
    /// the work it handed over had not finished and the thread asked to wait for it rather than poll.</summary>
    public static WorkKind PumpWaits { get; } = new(name: "world.tick.pump-waits", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the units handed to the host's step thread.</summary>
    public static WorkKind Waves { get; } = new(name: "world.tick.waves", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the tick slices those units executed.</summary>
    public static WorkKind WaveTicks { get; } = new(name: "world.tick.wave-ticks", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the ledger a host's servers count into. Declared after the kinds, which it reads as it is
    /// created.</summary>
    public static WorkCounterSet Process { get; } = new(
        kinds: [
            PumpVisits, HostSteps, PumpWaits, Waves, WaveTicks,
            InterestCandidates, InterestBodies, InterestBytes,
            FederatedBatches, FederatedRetimed, FederatedStale, FederatedRestarts,
            InputAdmissions, InputAdmissionsStanding, InputAdmissionsSteady, InputRefreshes, InputRefreshesStanding,
            FactDerives, FactSlots, RegionSweepOverflow, ContinuationFolds, ContinuationSlots, ContinuationEncodes, ContinuationBytes, PerceptionFreezes, PerceptionPoints, PerceptionGrids, NearestCells, DealRedeals,
            ContinuationProfiles, ContinuationProfileBytes, ContinuationTailBytes, .. SectionEncodes, .. SectionBytes,
            .. SectionComparedBytes, .. SectionFilteredCompares, .. SectionUnchangedEncodes,
        ],
        name: SourceName
    );

    // One kind per section, named by the section: world.tick.section-<measure>.<section>.
    private static WorkKind[] SectionKinds(string measure, string unit) {
        var sections = Enum.GetValues<WorldContinuationSectionKind>();
        var kinds = new WorkKind[sections.Length];

        foreach (var section in sections) {
            kinds[((int)section)] = new WorkKind(name: $"world.tick.section-{measure}.{SectionName(section: section)}", unit: unit, workClass: WorkClass.Deterministic);
        }
        return kinds;
    }
    private static string SectionName(WorldContinuationSectionKind section) => section switch {
        WorldContinuationSectionKind.Pose => "pose",
        WorldContinuationSectionKind.Motion => "motion",
        WorldContinuationSectionKind.Clocks => "clocks",
        WorldContinuationSectionKind.MotionRecency => "motion-recency",
        WorldContinuationSectionKind.InputImages => "input-images",
        WorldContinuationSectionKind.InputReadbacks => "input-readbacks",
        WorldContinuationSectionKind.Action => "action",
        WorldContinuationSectionKind.LaneRecency => "lane-recency",
        WorldContinuationSectionKind.RegisterWrites => "register-writes",
        WorldContinuationSectionKind.DurableInput => "durable-input",
        WorldContinuationSectionKind.ScriptActors => "script-actors",
        WorldContinuationSectionKind.Tape => "tape",
        WorldContinuationSectionKind.Program => "program",
        WorldContinuationSectionKind.Contacts => "contacts",
        WorldContinuationSectionKind.Admission => "admission",
        WorldContinuationSectionKind.Designations => "designations",
        WorldContinuationSectionKind.Look => "look",
        WorldContinuationSectionKind.Producer => "producer",
        WorldContinuationSectionKind.Steering => "steering",
        WorldContinuationSectionKind.Cadence => "cadence",
        WorldContinuationSectionKind.Navigation => "navigation",
        WorldContinuationSectionKind.Route => "route",
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(section)),
    };
}

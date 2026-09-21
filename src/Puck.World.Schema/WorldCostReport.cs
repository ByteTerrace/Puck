using RuleWorkContributor = Puck.State.Rules.RuleWorkContributor;
using CompiledRule = Puck.State.Rules.CompiledRule;
using Puck.Maths;

namespace Puck.World;

/// <summary>Static resource dimensions derivable from a world document without constructing its arena.</summary>
/// <param name="RowCount">The compiled state row count.</param>
/// <param name="TopologyCount">The authored state-topology count.</param>
/// <param name="PopulationCapacity">The authored body-slot capacity.</param>
/// <param name="CellSlotCount">The arena's stored cell slots.</param>
/// <param name="VectorComponentBytes">The arena's packed vector-component bytes.</param>
/// <param name="LaneSlotCount">The arena's participant and identity lane slots.</param>
/// <param name="LaneRosterCount">The arena's participant and identity roster entries.</param>
/// <param name="DrawMaskWordCount">The arena's retained generator-mask words.</param>
/// <param name="LayoutBytes">The measured arena column, index, and fixed-ceiling payload bytes.</param>
/// <param name="RetainedVisibilityBytes">The bounded declaration and live-column visibility bytes.</param>
/// <param name="RetainedKeyBytes">The compiled key table's retained bytes.</param>
/// <param name="ArenaFootprintBytes">The measured layout, visibility, and retained-key sum. It may exceed the
/// admission ceiling when analyzing an unvalidated draft.</param>
/// <param name="ArenaAdmissionCeilingBytes">The arena admission ceiling.</param>
/// <param name="JournalAllowanceBytes">The undo journal allowance. This is a ceiling, not allocated memory.</param>
/// <param name="MeasurementIssue">Why the arena dimensions could not be measured or exceed the admission ceiling,
/// or null.</param>
/// <param name="UnmodeledTotalMemoryReason">Why these dimensions do not constitute a total process-memory bound.</param>
public sealed record WorldResourceDimensions(
    int RowCount,
    int TopologyCount,
    int PopulationCapacity,
    int CellSlotCount,
    int VectorComponentBytes,
    int LaneSlotCount,
    int LaneRosterCount,
    int DrawMaskWordCount,
    long? LayoutBytes,
    long? RetainedVisibilityBytes,
    long? RetainedKeyBytes,
    long? ArenaFootprintBytes,
    long ArenaAdmissionCeilingBytes,
    long JournalAllowanceBytes,
    string? MeasurementIssue,
    string UnmodeledTotalMemoryReason
) {
    /// <summary>Gets the explicit absence used by manually constructed reports that did not run measurement.</summary>
    public static WorldResourceDimensions Unmeasured { get; } = new(
        ArenaAdmissionCeilingBytes: ArenaCapacity.MaxBytes,
        ArenaFootprintBytes: null,
        CellSlotCount: 0,
        DrawMaskWordCount: 0,
        JournalAllowanceBytes: ArenaCapacity.MaxJournalBytes,
        LaneRosterCount: 0,
        LaneSlotCount: 0,
        LayoutBytes: null,
        MeasurementIssue: "Resource dimensions were not generated.",
        PopulationCapacity: 0,
        RetainedKeyBytes: null,
        RetainedVisibilityBytes: null,
        RowCount: 0,
        TopologyCount: 0,
        UnmodeledTotalMemoryReason: "Total memory is unmodeled.",
        VectorComponentBytes: 0
    );

    /// <summary>Measures the resource dimensions derivable from <paramref name="definition"/> without allocating
    /// an arena. A malformed or overflowing layout is retained as <see cref="MeasurementIssue"/>.</summary>
    public static WorldResourceDimensions Measure(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(definition);

        var catalog = definition.StateCatalog;
        var topologyCount = (definition.StateRaw?.Lattices ?? []).Count;
        const string Unmodeled = "Total memory is unmodeled: arena scratch retention, undo overrun by the crossing effect, runtime collections, compiled programs, fields, and host services are outside the measured arena footprint.";

        if (!ArenaLayout.TryBuild(
            catalog: catalog,
            layout: out var layout,
            options: WorldSlotLanes.Options(definition: definition),
            reason: out var layoutReason,
            section: definition.StateRaw
        )) {
            return Failed(issue: layoutReason);
        }
        if (!StateArena.TryMeasureVisibility(
            bytes: out var visibilityBytes,
            reason: out var visibilityReason,
            section: definition.StateRaw
        )) {
            return Failed(issue: visibilityReason);
        }

        try {
            var keyBytes = catalog.Keys.Bytes;
            var footprintBytes = checked((checked((layout.Bytes + visibilityBytes)) + keyBytes));
            var admissionIssue = ((footprintBytes > ArenaCapacity.MaxBytes)
                ? $"The measured arena footprint is {footprintBytes} bytes, past the {ArenaCapacity.MaxBytes}-byte ceiling."
                : null
            );

            return new(
                RowCount: catalog.Count,
                TopologyCount: topologyCount,
                PopulationCapacity: definition.Population.Capacity,
                CellSlotCount: layout.CellSlotCount,
                VectorComponentBytes: layout.VectorByteCount,
                LaneSlotCount: layout.LaneSlotCount,
                LaneRosterCount: layout.LaneRosterCount,
                DrawMaskWordCount: layout.MaskWordCount,
                LayoutBytes: layout.Bytes,
                RetainedVisibilityBytes: visibilityBytes,
                RetainedKeyBytes: keyBytes,
                ArenaFootprintBytes: footprintBytes,
                ArenaAdmissionCeilingBytes: ArenaCapacity.MaxBytes,
                JournalAllowanceBytes: ArenaCapacity.MaxJournalBytes,
                MeasurementIssue: admissionIssue,
                UnmodeledTotalMemoryReason: Unmodeled
            );
        } catch (OverflowException) {
            return Failed(issue: "The measured arena footprint exceeds the Int64 reporting range.");
        }

        WorldResourceDimensions Failed(string issue) => new(
            RowCount: catalog.Count,
            TopologyCount: topologyCount,
            PopulationCapacity: definition.Population.Capacity,
            CellSlotCount: 0,
            VectorComponentBytes: 0,
            LaneSlotCount: 0,
            LaneRosterCount: 0,
            DrawMaskWordCount: 0,
            LayoutBytes: null,
            RetainedVisibilityBytes: null,
            RetainedKeyBytes: null,
            ArenaFootprintBytes: null,
            ArenaAdmissionCeilingBytes: ArenaCapacity.MaxBytes,
            JournalAllowanceBytes: ArenaCapacity.MaxJournalBytes,
            MeasurementIssue: issue,
            UnmodeledTotalMemoryReason: Unmodeled
        );
    }
}
/// <summary>Portable cost analysis status. Existing heuristic totals are exposed separately until calibrated
/// cycle costs and bounded search reservations are available; this report does not replace validation.</summary>
/// <param name="ModelId">The reference cost model identifier.</param>
/// <param name="Scope">The cost model scope ("authored simulation").</param>
/// <param name="SimulationRateHz">The document's simulation rate in Hz.</param>
/// <param name="StepPeriodEngineTicks">The step period in engine clock ticks (50,400 / rate).</param>
/// <param name="StepAllowanceCycles">The authored subsystem reference cycle budget for one step.</param>
/// <param name="RecurringBound">The worst-case recurring simulation cycle bound (rules, decisions, flock affinities).</param>
/// <param name="SearchReservations">The search cycle bound, unresolved until the search walk is fully priced.</param>
/// <param name="TotalBound">The combined cycle bound (RecurringBound + SearchReservations).</param>
/// <param name="ReferenceEngineTicks">Reference time rounded upward, or null when the cycle bound is unresolved.</param>
/// <param name="Admitted">Whether the combined cost satisfies rate-based admission.</param>
/// <param name="Contributors">The individual rule and interaction contributor lines, costliest first.</param>
/// <param name="Issues">Any unmodeled operations or overflow issues encountered during analysis.</param>
/// <param name="HeuristicWorkUnitsPerTick">The heuristic work-unit bound, or why no number bounds it; never
/// interpreted as reference cycles.</param>
public sealed partial record WorldCostReport(
    string ModelId,
    string Scope,
    int SimulationRateHz,
    long StepPeriodEngineTicks,
    long StepAllowanceCycles,
    CostBound RecurringBound,
    CostBound SearchReservations,
    CostBound TotalBound,
    long? ReferenceEngineTicks,
    bool Admitted,
    IReadOnlyList<RuleWorkContributor> Contributors,
    IReadOnlyList<string> Issues,
    RuleWork HeuristicWorkUnitsPerTick
) {
    /// <summary>Gets the exact coefficient evidence identity; the model name alone does not identify its prices.</summary>
    public string? EvidenceDigest { get; init; }
    /// <summary>Gets the heuristic sheet used by the current admission policy. These units are not cycles.</summary>
    public WorldRuleWorkBudget WorkBudget { get; init; }

    /// <summary>Gets the synchronous document-edit cost, unresolved until rebuild paths have reference prices.</summary>
    public CostBound EditBurstBound { get; init; } = CostBound.Unmodeled(reason: "Synchronous document rebuilds have no complete cycle bound.");
    /// <summary>Gets the independently measured static resource dimensions. These do not claim a total-memory
    /// bound and do not reinterpret configured allowances as allocations.</summary>
    public WorldResourceDimensions Resources { get; init; } = WorldResourceDimensions.Unmeasured;

    /// <summary>Generates an analysis report for a structurally compilable world, including unresolved costs.</summary>
    /// <param name="definition">The world definition.</param>
    /// <param name="model">The cost model (defaults to <see cref="CostModel.Default"/>).</param>
    public static WorldCostReport Generate(WorldDefinition definition, CostModel? model = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Generate(compilation: WorldRuleCompilation.Compile(definition: definition), model: model);
    }
    /// <summary>Analyzes an existing compilation without recompiling its rules or interactions. The compilation's
    /// definition and collection contents must remain unchanged, as for installation.</summary>
    /// <param name="compilation">The programs compiled for the exact definition being analyzed.</param>
    /// <param name="model">The reference model, or the portable default.</param>
    public static WorldCostReport Generate(WorldRuleCompilation compilation, CostModel? model = null) {
        ArgumentNullException.ThrowIfNull(compilation);

        return Create(compilation.Definition, compilation.Rules, compilation.WorkContributors, compilation.WorkBudget, (model ?? CostModel.Default));
    }
    /// <summary>Analyzes compiled programs before a preview host filters unsupported rules or groups. This is an
    /// analysis surface, not an installation receipt or validation result.</summary>
    /// <param name="context">The unchanged definition's compile context.</param>
    /// <param name="rules">All authored rules compiled against that context.</param>
    /// <param name="interactions">All authored interactions compiled against that context.</param>
    /// <returns>The same authored report a server compilation produces.</returns>
    public static WorldCostReport AnalyzePrograms(WorldFactsCompileContext context, CompiledRule[] rules, CompiledRule[] interactions) {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(interactions);
        var (budget, contributors) = WorldRuleWorkBudget.Analyze(context.Definition, rules, interactions, context);
        return Create(context.Definition, rules, contributors, budget, CostModel.Default);
    }

    private static WorldCostReport Create(WorldDefinition definition, CompiledRule[] rules, IReadOnlyList<RuleWorkContributor> contributors, WorldRuleWorkBudget budget, CostModel model) {
        var rateHz = definition.SimulationRateHz;
        var profile = model.ReferenceProfile;
        var stepPeriodTicks = profile.StepPeriodEngineTicks(rateHz: rateHz);
        var allowanceCycles = profile.AuthoredBudgetCycles(rateHz: rateHz);

        var recurringBound = CostBound.Unmodeled(reason: "Recurring authored work has heuristic weights, not calibrated portable cycle bounds.");
        var issues = new List<string> { recurringBound.Reason! };

        if (!budget.WorkUnitsPerTick.IsKnown) {
            issues.Add(item: $"Heuristic work per tick has no number: {budget.WorkUnitsPerTick}.");
        }

        // Check search reservations
        var searchReservations = CostBound.Zero;

        if (definition.Search.Rows.Count > 0) {
            searchReservations = CostBound.Unmodeled(reason: "Search traversal, frame copies, and chance expansion have no complete cycle reservation.");
            issues.Add(item: searchReservations.Reason!);
            if (!WorldSearchCompilation.TryPlanAll(
                definition: definition,
                judges: out _,
                plans: out _,
                reason: out var searchReason,
                rules: rules,
                recurringWork: budget.WorkUnitsPerTick,
                scores: out _
            )) {
                issues.Add(item: $"Search planning issue: {searchReason}");
            }
        }

        var totalBound = (recurringBound + searchReservations);
        var referenceEngineTicks = (totalBound.IsKnown
            ? profile.ReferenceEngineTicks(cycles: totalBound.Cycles)
            : (long?)null
        );
        var admitted = ((issues.Count == 0) && profile.Admits(
            bound: totalBound,
            rateHz: rateHz
        ));

        return new WorldCostReport(
            ModelId: model.Id,
            Scope: "authored simulation",
            SimulationRateHz: rateHz,
            StepPeriodEngineTicks: stepPeriodTicks,
            StepAllowanceCycles: allowanceCycles,
            RecurringBound: recurringBound,
            SearchReservations: searchReservations,
            TotalBound: totalBound,
            ReferenceEngineTicks: referenceEngineTicks,
            Admitted: admitted,
            Contributors: contributors,
            Issues: issues.AsReadOnly(),
            HeuristicWorkUnitsPerTick: budget.WorkUnitsPerTick
        ) {
            EvidenceDigest = model.EvidenceDigest,
            ContributorSources = WorldCostSource.From(definition: definition),
            Resources = WorldResourceDimensions.Measure(definition: definition),
            WorkBudget = budget,
        };
    }
}

namespace Puck.World;

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
/// <param name="HeuristicWorkUnitsPerTick">The existing work-unit estimate; never interpreted as reference cycles.</param>
public sealed record WorldCostReport(
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
    long HeuristicWorkUnitsPerTick
) {
    /// <summary>Generates an analysis report for a structurally compilable world, including unresolved costs.</summary>
    /// <param name="definition">The world definition.</param>
    /// <param name="model">The cost model (defaults to <see cref="CostModel.Default"/>).</param>
    public static WorldCostReport Generate(WorldDefinition definition, CostModel? model = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        model ??= CostModel.Default;
        var rateHz = definition.SimulationRateHz;
        var profile = model.ReferenceProfile;
        var stepPeriodTicks = profile.StepPeriodEngineTicks(rateHz: rateHz);
        var allowanceCycles = profile.AuthoredBudgetCycles(rateHz: rateHz);

        var compiledRules = WorldRuleCompiler.CompileAll(definition: definition);
        var compiledInteractions = WorldRuleCompiler.CompileAllInteractions(definition: definition);
        var contributors = WorldRuleWorkBudget.Contributors(definition: definition, rules: compiledRules, interactions: compiledInteractions);
        var budget = WorldRuleWorkBudget.Measure(definition: definition, rules: compiledRules, interactions: compiledInteractions);

        var recurringBound = CostBound.Unmodeled("Recurring authored work has heuristic weights, not calibrated portable cycle bounds.");
        var issues = new List<string> { recurringBound.Reason! };

        // Check search reservations
        var searchReservations = CostBound.Zero;
        if (definition.Search.Rows.Count > 0) {
            searchReservations = CostBound.Unmodeled("Search traversal, frame copies, and chance expansion have no complete cycle reservation.");
            issues.Add(searchReservations.Reason!);
            if (!WorldSearchCompilation.TryPlanAll(definition: definition, rules: compiledRules, plans: out _, judge: out _, reason: out var searchReason)) {
                issues.Add(item: $"Search planning issue: {searchReason}");
            }
        }

        var totalBound = recurringBound + searchReservations;
        var referenceEngineTicks = totalBound.IsKnown ? profile.ReferenceEngineTicks(totalBound.Cycles) : (long?)null;
        var admitted = issues.Count == 0 && profile.Admits(totalBound, rateHz);

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
            Issues: issues,
            HeuristicWorkUnitsPerTick: budget.WorkUnitsPerTick
        );
    }
}

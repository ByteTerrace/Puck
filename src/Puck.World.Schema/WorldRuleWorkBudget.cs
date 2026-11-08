using CompiledExpressionToken = Puck.State.Rules.CompiledExpressionToken;
using CompiledRule = Puck.State.Rules.CompiledRule;
using RuleWorkBudget = Puck.State.Rules.RuleWorkBudget;
using RuleWorkContributor = Puck.State.Rules.RuleWorkContributor;
namespace Puck.World;

/// <summary>The world's work sheet over <see cref="RuleWorkBudget"/>: rules multiplied by their <c>forEach</c> row's
/// capacity, interactions by the pair count their co-occurrence can visit, plus the flock-affinity and
/// decision-perception passes that run outside the rule sweep.</summary>
/// <param name="RuleRows">The rule count.</param>
/// <param name="InteractionRows">The interaction count.</param>
/// <param name="EvaluationSlots">The evaluations per tick, every multiplier summed.</param>
/// <param name="WorkUnitsPerTick">The worst-case work units per tick, or why no number bounds them.</param>
/// <param name="FlockAffinityWorkUnitsPerTick">The flock-affinity share.</param>
/// <param name="DecisionImagePointsPerTick">The pose-image points a decision perception pass copies.</param>
/// <param name="DecisionGridBuildsPerTick">The grids a decision perception pass builds, one per distinct cell width.</param>
/// <param name="DecisionGridPointsPerTick">The points those grids group.</param>
public readonly record struct WorldRuleWorkBudget(int RuleRows, int InteractionRows, long EvaluationSlots, RuleWork WorkUnitsPerTick,
    RuleWork FlockAffinityWorkUnitsPerTick = default, int DecisionImagePointsPerTick = 0, int DecisionGridBuildsPerTick = 0, long DecisionGridPointsPerTick = 0) {
    internal static IReadOnlyList<(string Rule, string Cell)> ContradictoryGates(CompiledRule[] rules, StateCatalog catalog) {
        var result = new List<(string, string)>();

        foreach (var rule in rules) {
            foreach (var pinned in RuleWorkBudget.PinnedCells(
                gate: rule.Gate,
                contradictory: out _
            )) {
                if (pinned.IsEmpty) {
                    result.Add(item: (rule.Name, pinned.Name(catalog: catalog)));
                }
            }
        }

        return result;
    }

    /// <summary>Reads the shared contributor lines without compiling or analyzing the programs again.</summary>
    /// <param name="compilation">The exact definition's unchanged compilation.</param>
    /// <returns>The work contributors, costliest first.</returns>
    public static IReadOnlyList<RuleWorkContributor> Contributors(WorldRuleCompilation compilation) {
        ArgumentNullException.ThrowIfNull(argument: compilation);
        return compilation.WorkContributors;
    }

    private static void Sort(List<RuleWorkContributor> contributors) {
        contributors.Sort(comparison: static (left, right) => {
            var byWork = RuleWork.Compare(
                left: right.WorkUnits,
                right: left.WorkUnits
            );

            return ((byWork != 0)
                ? byWork
                : string.CompareOrdinal(
                    strA: left.Name,
                    strB: right.Name
                )
            );
        });

    }

    // The one computation of a tick's work: admission runs it over the rules and groups it validated, and every
    // other reader takes the sheet a compilation of the whole document retained, so no reader prices a rule the
    // document's own groups have not bound.
    internal static (WorldRuleWorkBudget Budget, IReadOnlyList<RuleWorkContributor> Contributors) Analyze(WorldDefinition definition, CompiledRule[] rules, CompiledRule[] interactions, WorldFactsCompileContext? context = null) {
        context ??= WorldFactsCompiler.Context(definition: definition);
        var lines = Lines(context: context, definition: definition, interactions: interactions, rules: rules);
        var budget = Measure(context: context, definition: definition, interactions: interactions, lines: lines, rules: rules);

        Sort(contributors: lines);
        return (budget, lines.AsReadOnly());
    }

    private static WorldRuleWorkBudget Measure(WorldDefinition definition, CompiledRule[] rules, CompiledRule[] interactions, WorldFactsCompileContext context, List<RuleWorkContributor> lines) {
        var decisionScales = new HashSet<long>();

        foreach (var rule in rules) {
            if ((rule as CompiledWorldFactsRule)?.Decision is { } decision) {
                foreach (var option in decision.Options) {
                    if (option.Neighbors is { } neighbors) { decisionScales.Add(item: neighbors.CellWidth.Value); }
                }
            }
        }

        var (slots, work) = RuleWorkBudget.Tally(
            contributors: lines,
            writers: RuleWorkBudget.CountWriters(rules: Multiplied(
                context: context,
                definition: definition,
                interactions: interactions,
                rules: rules
            ))
        );
        var flockCost = FlockAffinityCost(
            context: context,
            definition: definition
        );
        var imagePoints = ((decisionScales.Count == 0)
            ? 0
            : definition.Population.Capacity
        );
        var gridPoints = (((long)definition.Population.Capacity) * decisionScales.Count);
        // All policies may reconsider together. The pose image is shared and visits every slot once; each distinct
        // grid keys its points, sorts them, and groups the sorted run into cells.
        var perceptionCost = (imagePoints + (decisionScales.Count * ((2L * definition.Population.Capacity) + RuleWorkBudget.IntrosortWork(count: definition.Population.Capacity))));

        return new WorldRuleWorkBudget(
            RuleRows: rules.Length,
            InteractionRows: interactions.Length,
            EvaluationSlots: slots,
            WorkUnitsPerTick: ((work + flockCost) + perceptionCost),
            FlockAffinityWorkUnitsPerTick: flockCost,
            DecisionImagePointsPerTick: imagePoints,
            DecisionGridBuildsPerTick: decisionScales.Count,
            DecisionGridPointsPerTick: gridPoints
        );
    }
    private static string DescribeRegionMultiplier(WorldDefinition definition, string placementId) {
        var (bound, terms) = RegionCapacityBound(
            definition: definition,
            placementId: placementId
        );

        return $"region '{placementId}' {terms} = {bound}";
    }
    private static RuleWork FlockAffinityCost(WorldDefinition definition, WorldFactsCompileContext context) {
        var maximum = RuleWork.Zero;

        foreach (var kit in definition.Kits) {
            foreach (var producer in kit.Producers.Values) {
                if (producer.Flock is not { } profile) { continue; }

                var compiled = new CompiledWorldFlockAffinities(
                    definition: definition,
                    profile: profile
                );

                maximum = RuleWork.Max(
                    left: maximum,
                    right: (profile.MaxNeighbors * compiled.WorkUnitsPerNeighbor)
                );
            }
        }

        // Initial samples and explicit producer changes may align every observer, regardless of normal cadence.
        return (definition.Population.Capacity * maximum);
    }
    // FromCreation carries no statically-derivable footprint; the caller falls back to the population capacity for it.
    private static float? FootprintRadius(WorldCollider? collider) => collider switch {
        WorldCollider.Sphere sphere => sphere.Radius,
        WorldCollider.Capsule capsule => capsule.Radius,
        WorldCollider.Box box => Math.Min(
        val1: box.HalfExtents.Value.X,
        val2: box.HalfExtents.Value.Z
    ),
        _ => null,
    };
    private static List<RuleWorkContributor> Lines(WorldDefinition definition, WorldFactsCompileContext context, CompiledRule[] rules, CompiledRule[] interactions) {
        var contributors = new List<RuleWorkContributor>(capacity: (rules.Length + interactions.Length));
        var gate = GateCommitCost(context: context, definition: definition);
        var writes = new List<Puck.State.CellAccess>();

        foreach (var rule in rules) {
            contributors.Add(item: RuleWorkBudget.Contributor(
                commit: (WritesGate(definition: definition, rule: rule, writes: writes) ? gate : 0L),
                rule: rule,
                multiplier: Multiplier(
                    context: context,
                    definition: definition,
                    rule: rule
                ),
                isInteraction: false,
                context: context
            ));
        }
        foreach (var interaction in interactions) {
            contributors.Add(item: RuleWorkBudget.Contributor(
                commit: (WritesGate(definition: definition, rule: interaction, writes: writes) ? gate : 0L),
                rule: interaction,
                multiplier: Multiplier(
                    context: context,
                    definition: definition,
                    rule: interaction
                ),
                isInteraction: true,
                context: context
            ));
        }

        return contributors;
    }
    // A firing that writes a drive-gate row resyncs the gate index at its commit, a walk of every gate row's cells, and
    // then every input owner, one pass over the input families and the kit selection each to validate and to
    // synchronize, priced as one owner per body the population holds.
    private static long GateCommitCost(WorldDefinition definition, WorldFactsCompileContext context) {
        var cells = 0L;

        for (var ordinal = 0; (ordinal < definition.State.Count); ordinal++) {
            if (definition.State[ordinal].GatesDrive) { cells += context.RowCapacity(rowOrdinal: ordinal); }
        }
        return ((cells == 0L) ? 0L : (cells + (definition.Population.Capacity * (2L * (WorldInputCatalog.For(definition: definition).Selectors.Length + 1L)))));
    }
    private static bool WritesGate(WorldDefinition definition, CompiledRule rule, List<Puck.State.CellAccess> writes) {
        writes.Clear();
        rule.CollectWrites(into: writes);
        foreach (var write in writes) {
            if ((write.RowOrdinal >= 0) && (write.RowOrdinal < definition.State.Count) && definition.State[write.RowOrdinal].GatesDrive) { return true; }
        }
        return false;
    }
    private static List<(CompiledRule Rule, long Multiplier)> Multiplied(WorldDefinition definition, WorldFactsCompileContext context, CompiledRule[] rules, CompiledRule[] interactions) {
        var result = new List<(CompiledRule, long)>(capacity: (rules.Length + interactions.Length));

        foreach (var rule in rules) {
            result.Add(item: (rule, Multiplier(
            context: context,
            definition: definition,
            rule: rule
        )));
        }
        foreach (var interaction in interactions) {
            result.Add(item: (interaction, Multiplier(
            context: context,
            definition: definition,
            rule: interaction
        )));
        }

        return result;
    }
    // A rule carries no forEach at all when it reads only fixed cells — a literal seat's own channel
    // ($channel:<seat>:*) included — so ForEachCount's default of 1 already prices a per-seat rule once, not once
    // per body; nothing here needs to special-case that shape.
    private static long Multiplier(WorldDefinition definition, WorldFactsCompileContext context, CompiledRule rule) {
        if ((rule as CompiledWorldFactsRule)?.Interaction is { } interaction) {
            return WorldInteractionBound.Of(
                context: context,
                interaction: interaction
            ).Evaluations;
        }

        return RuleWorkBudget.ForEachCount(
            context: context,
            rule: rule
        );
    }

    /// <summary>Returns the most carriers a region interaction could ever have inside it, with the terms that
    /// produced it. Membership is by a body's own centre, so the carriers' centres all lie in a disc of radius R and
    /// two carriers of footprint radius r are at least 2r apart; their r-discs are then disjoint and all contained
    /// in a disc of radius R + r, so N·πr² ≤ π(R + r)² and N ≤ ((R + r)/r)². The result is clamped to the population
    /// capacity it replaces. Falls back to the population capacity when the region is absent, or when
    /// <see cref="SmallestKitFootprintRadius"/> cannot answer for it — the packing argument holds only among kits
    /// that mutually exclude each other, and a property (the tag a region interaction's <c>left</c> names) carries
    /// no static link to a kit, so any body of any kit could be the one standing in the region.</summary>
    internal static (long Bound, string Terms) RegionCapacityBound(WorldDefinition definition, string placementId) {
        var capacity = ((long)definition.Population.Capacity);
        var region = WorldDefinitionRows.FindPlacement(
            placements: definition.Placements,
            id: placementId
        )?.Region;

        if (region is null) {
            return (capacity, $"undeclared — population capacity {capacity}");
        }

        if (
            (SmallestKitFootprintRadius(definition: definition) is not { } footprint) ||
            (footprint <= 0f)
        ) {
            return (capacity, $"radius={region.Radius} — no solid-only kit footprint resolvable, population capacity {capacity}");
        }

        var reach = ((((double)region.Radius) + footprint) / footprint);
        var packed = ((long)Math.Floor(d: (reach * reach)));

        return (
            Math.Clamp(
            max: capacity,
            min: 1L,
            value: packed
        ),
            $"radius={region.Radius}, smallest solid-kit footprint={footprint} -> floor(((R+r)/r)^2)={packed}, clamped to population {capacity}"
        );
    }

    // Two bodies depenetrate only when both kits declare Solid contact (WorldBodyContactMode); an Overlap kit could
    // co-locate any number of its own bodies at one point, so its mere presence in the document defeats a
    // circle-packing bound for every region, not only the ones it could occupy.
    private static float? SmallestKitFootprintRadius(WorldDefinition definition) {
        float? smallest = null;

        foreach (var kit in definition.Kits) {
            if (kit.BodyContact != WorldBodyContactMode.Solid) { return null; }
            if (
                (FootprintRadius(collider: kit.Collider) is not { } radius) ||
                (radius <= 0f)
            ) { continue; }
            if (
                (smallest is null) ||
                (radius < smallest)
            ) { smallest = radius; }
        }

        return smallest;
    }

    /// <summary>Lists every rule whose gate pins a cell to an empty range, with that cell.</summary>
    /// <param name="definition">The world.</param>
    public static IReadOnlyList<(string Rule, string Cell)> ContradictoryGates(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return ContradictoryGates(
            catalog: definition.StateCatalog,
            rules: WorldFactsCompiler.CompileAll(definition: definition)
        );
    }
    /// <summary>Lists every contributor line, costliest first, then by name: the lines admission reads, over the
    /// document's rules, groups and interactions compiled together.</summary>
    /// <param name="definition">The world.</param>
    public static IReadOnlyList<RuleWorkContributor> Contributors(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return WorldRuleCompilation.Compile(definition: definition).WorkContributors;
    }
    /// <summary>Returns admission's refusal of a tick's work, or <see langword="null"/> when the work is admitted.
    /// Only a known number at or under <see cref="RuleCapacity.MaxWorkUnitsPerTick"/> is admitted: a bound nothing
    /// prices and a bound that overflowed are refused as having no number, never read as a small one.</summary>
    /// <param name="work">The tick's worst-case work.</param>
    /// <param name="contributors">Lists the sheet's lines, costliest first; called only to word a refusal, which
    /// names the first three.</param>
    /// <param name="rowsAtCeiling">Names, for one line, the rows it reads, writes or iterates that are priced at the
    /// engine's cell ceiling for want of a capacity (<see cref="RowsAtCeiling(Puck.State.Rules.RuleCompileContext, CompiledRule)"/>), or <see langword="null"/> to name
    /// none; called only to word a refusal.</param>
    /// <returns>The refusal, in the author's vocabulary, or <see langword="null"/>.</returns>
    public static string? Refuse(RuleWork work, Func<IReadOnlyList<RuleWorkContributor>> contributors, Func<string, IEnumerable<string>>? rowsAtCeiling = null) {
        ArgumentNullException.ThrowIfNull(argument: contributors);

        if (work.Fits(ceiling: RuleCapacity.MaxWorkUnitsPerTick)) {
            return null;
        }

        var lines = contributors().Take(count: 3).ToArray();
        var costliest = string.Join(
            separator: ", ",
            values: lines.Select(selector: static line => $"'{line.Name}' x{line.Multiplier} = {line.WorkUnits}")
        );
        var uncapped = ((rowsAtCeiling is null) ? [] : lines.SelectMany(selector: line => rowsAtCeiling(arg: line.Name)).Distinct(comparer: StringComparer.Ordinal).ToArray());
        var fix = ((uncapped.Length == 0)
            ? "a forEach line's multiplier is its row's capacity, so author the capacity the row needs"
            : $"{string.Join(separator: ", ", values: uncapped.Select(selector: static name => $"'{name}'"))} declare no capacity and are priced at the {StateCapacity.MaxCellsPerRow}-cell ceiling they may grow to: author each one's capacity, or key it over a capped token domain");

        return $"rules/interactions/flock affinities derive {(work.IsKnown
            ? $"{work} worst-case work units per tick, exceeding"
            : $"no bound on their work per tick ({work}), which cannot be admitted under")} the maximum of {RuleCapacity.MaxWorkUnitsPerTick}; costliest: {costliest} ({fix}; world.budget.rules lists every line).";
    }
    /// <summary>Returns the keyed rows a compiled rule iterates, reads or writes whose price is the engine's cell
    /// ceiling only because they declare no capacity and no capped token domain: the rows whose capacity an author
    /// would write to bring the rule's price down.</summary>
    /// <param name="context">The context the rule was priced in.</param>
    /// <param name="rule">The compiled rule or interaction.</param>
    /// <returns>The row names, in catalog order.</returns>
    public static IReadOnlyList<string> RowsAtCeiling(Puck.State.Rules.RuleCompileContext context, CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);

        return RowsAtCeiling(context: context, ordinals: Puck.State.Rules.RuleDataflow.Reads(rule: rule).Concat(second: Puck.State.Rules.RuleDataflow.Writes(rule: rule))
            .Select(selector: static access => access.RowOrdinal).Append(element: rule.ForEachOrdinal));
    }
    /// <summary>Returns the rows among <paramref name="ordinals"/> priced at the engine's cell ceiling only because
    /// they are keyed and declare no capacity and no capped token domain.</summary>
    /// <param name="context">The context the rows were priced in.</param>
    /// <param name="ordinals">The catalog ordinals to judge; a negative ordinal is skipped.</param>
    /// <returns>The row names, in catalog order, each once.</returns>
    public static IReadOnlyList<string> RowsAtCeiling(Puck.State.Rules.RuleCompileContext context, IEnumerable<int> ordinals) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: ordinals);

        return [.. new SortedSet<int>(collection: ordinals.Where(predicate: static ordinal => (ordinal >= 0)))
            .Select(selector: ordinal => (Ordinal: ordinal, Row: context.FindRowAt(rowOrdinal: ordinal)))
            .Where(predicate: entry => (entry.Row is { Capacity: null, Shape: (RowShape.Keyed or RowShape.Ordered), Generated: false }) &&
                (context.RowCapacity(rowOrdinal: entry.Ordinal) >= StateCapacity.MaxCellsPerRow))
            .Select(selector: static entry => entry.Row!.Name.Value)];
    }
    /// <summary>Describes why one contributor's multiplier is what it is — the forEach row's capacity, the
    /// interaction's carrier/pair-count formula, or the region-capacity bound's own terms — for
    /// <c>world.budget.rules --why</c>.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="rule">The compiled rule or interaction.</param>
    public static string DescribeMultiplier(WorldDefinition definition, CompiledRule rule) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: rule);

        if ((rule as CompiledWorldFactsRule)?.Interaction is { } interaction) {
            var bound = WorldInteractionBound.Of(
                context: WorldFactsCompiler.Context(definition: definition),
                interaction: interaction
            );

            if ((interaction.CoOccurrence == WorldInteractionCoOccurrence.Region) && (interaction.LeftPool is not null)) {
                return $"region '{interaction.Right}': {bound.Lefts} logical pool instances, each independently eligible even when they share a body";
            }

            return ((interaction.CoOccurrence == WorldInteractionCoOccurrence.Region)
                ? $"{DescribeRegionMultiplier(
                    definition: definition,
                    placementId: interaction.Right
                )}, of {bound.Lefts} left carriers"
                : $"distance: {bound.Lefts} left carriers x min(neighbours {((interaction.Neighbours > 0)
                    ? interaction.Neighbours.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                    : "unbounded")}, {bound.Rights} right carriers, eligible partners) = {bound.Evaluations} evaluations, after {bound.Lefts} x {bound.Rights} distance tests charged as setup"
            );
        }

        var firings = ((rule.MaxFirings > 0)
            ? $", firings priced at maxFirings={rule.MaxFirings}"
            : string.Empty);

        return ((rule.ForEachOrdinal >= 0)
            ? $"forEach '{definition.StateCatalog.Descriptors[rule.ForEachOrdinal].Name}' row capacity={RuleWorkBudget.ForEachCount(
                rule: rule,
                context: WorldFactsCompiler.Context(definition: definition)
            )}{firings}"
            : (rule.ForEachBodies
                ? $"forEach {RuleFacts.ForEachBodies} body capacity={definition.Population.Capacity}{firings}"
                : ((rule.PoolForEach is { } pool)
                    ? $"pool sweep over '{pool.Name}' capacity={pool.Capacity}{firings}"
                    : "no forEach — priced once"))
        );
    }
    /// <summary>Returns the work units an expression costs against a document.</summary>
    public static RuleWork ExpressionCost(CompiledExpressionToken[] tokens, WorldDefinition definition) => RuleWorkBudget.ExpressionCost(
        tokens: tokens,
        context: WorldFactsCompiler.Context(definition: definition)
    );
    /// <summary>Measures a document's worst-case per-tick rule work: the sheet admission holds against the ceiling,
    /// over the document's rules, groups and interactions compiled together.</summary>
    /// <param name="definition">The world.</param>
    public static WorldRuleWorkBudget Measure(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return WorldRuleCompilation.Compile(definition: definition).WorkBudget;
    }
    /// <summary>Reads the shared work sheet without compiling the programs or analyzing them again.</summary>
    /// <param name="compilation">The exact definition's unchanged compilation.</param>
    /// <returns>The worst-case per-tick rule work.</returns>
    public static WorldRuleWorkBudget Measure(WorldRuleCompilation compilation) {
        ArgumentNullException.ThrowIfNull(argument: compilation);
        return compilation.WorkBudget;
    }
}

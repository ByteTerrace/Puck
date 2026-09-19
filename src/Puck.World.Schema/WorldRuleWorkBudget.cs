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
    internal static IReadOnlyList<RuleWorkContributor> Contributors(WorldDefinition definition, CompiledRule[] rules, CompiledRule[] interactions) {
        var context = WorldFactsCompiler.Context(definition: definition);
        var contributors = Lines(
            context: context,
            definition: definition,
            interactions: interactions,
            rules: rules
        );

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

        return contributors;
    }
    internal static WorldRuleWorkBudget Measure(WorldDefinition definition, CompiledRule[] rules, CompiledRule[] interactions) {
        var context = WorldFactsCompiler.Context(definition: definition);
        var decisionScales = new HashSet<long>();

        foreach (var rule in rules) {
            if ((rule as CompiledWorldFactsRule)?.Decision is { } decision) {
                foreach (var option in decision.Options) {
                    if (option.Neighbors is { } neighbors) { decisionScales.Add(item: neighbors.CellWidth.Value); }
                }
            }
        }

        var lines = Lines(
            context: context,
            definition: definition,
            interactions: interactions,
            rules: rules
        );

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

        foreach (var rule in rules) {
            contributors.Add(item: RuleWorkBudget.Contributor(
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
    /// <summary>Lists every contributor line, costliest first, then by name.</summary>
    /// <param name="definition">The world.</param>
    public static IReadOnlyList<RuleWorkContributor> Contributors(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Contributors(
            definition,
            WorldFactsCompiler.CompileAll(definition: definition),
            WorldFactsCompiler.CompileAllInteractions(definition: definition)
        );
    }
    /// <summary>Returns admission's refusal of a tick's work, or <see langword="null"/> when the work is admitted.
    /// Only a known number at or under <see cref="RuleCapacity.MaxWorkUnitsPerTick"/> is admitted: a bound nothing
    /// prices and a bound that overflowed are refused as having no number, never read as a small one.</summary>
    /// <param name="work">The tick's worst-case work.</param>
    /// <param name="contributors">Lists the sheet's lines, costliest first; called only to word a refusal, which
    /// names the first three.</param>
    /// <returns>The refusal, in the author's vocabulary, or <see langword="null"/>.</returns>
    public static string? Refuse(RuleWork work, Func<IReadOnlyList<RuleWorkContributor>> contributors) {
        ArgumentNullException.ThrowIfNull(argument: contributors);

        if (work.Fits(ceiling: RuleCapacity.MaxWorkUnitsPerTick)) {
            return null;
        }

        var costliest = string.Join(
            separator: ", ",
            values: contributors().Take(count: 3).Select(selector: static line => $"'{line.Name}' x{line.Multiplier} = {line.WorkUnits}")
        );

        return $"rules/interactions/flock affinities derive {(work.IsKnown
            ? $"{work} worst-case work units per tick, exceeding"
            : $"no bound on their work per tick ({work}), which cannot be admitted under")} the maximum of {RuleCapacity.MaxWorkUnitsPerTick}; costliest: {costliest} (a forEach line's multiplier is its row's capacity, so author the capacity the row needs; world.budget.rules lists every line).";
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

            return ((interaction.CoOccurrence == WorldInteractionCoOccurrence.Region)
                ? $"{DescribeRegionMultiplier(
                    definition: definition,
                    placementId: interaction.Right
                )}, of {bound.Lefts} left carriers"
                : $"distance: {bound.Lefts} left carriers x min(neighbours {((interaction.Neighbours > 0)
                    ? interaction.Neighbours.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
                    : "unbounded")}, {bound.Rights} right carriers, every other body) = {bound.Evaluations} evaluations, after {bound.Lefts} x {bound.Rights} distance tests charged as setup"
            );
        }

        return ((rule.ForEachOrdinal >= 0)
            ? $"forEach '{definition.StateCatalog.Descriptors[rule.ForEachOrdinal].Name}' row capacity={RuleWorkBudget.ForEachCount(
                rule: rule,
                context: WorldFactsCompiler.Context(definition: definition)
            )}"
            : "no forEach — priced once"
        );
    }
    /// <summary>Returns the work units an expression costs against a document.</summary>
    public static RuleWork ExpressionCost(CompiledExpressionToken[] tokens, WorldDefinition definition) => RuleWorkBudget.ExpressionCost(
        tokens: tokens,
        context: WorldFactsCompiler.Context(definition: definition)
    );
    /// <summary>Measures a document's worst-case per-tick rule work.</summary>
    /// <param name="definition">The world.</param>
    public static WorldRuleWorkBudget Measure(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Measure(
            definition,
            WorldFactsCompiler.CompileAll(definition: definition),
            WorldFactsCompiler.CompileAllInteractions(definition: definition)
        );
    }
}

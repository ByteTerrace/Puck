namespace Puck.World;

/// <summary>The world's work sheet over <see cref="RuleWorkBudget"/>: rules multiplied by their <c>forEach</c> row's
/// capacity, interactions by the pair count their co-occurrence can visit, plus the flock-affinity and
/// decision-perception passes that run outside the rule sweep.</summary>
/// <param name="RuleRows">The rule count.</param>
/// <param name="InteractionRows">The interaction count.</param>
/// <param name="EvaluationSlots">The evaluations per tick, every multiplier summed.</param>
/// <param name="WorkUnitsPerTick">The worst-case work units per tick.</param>
/// <param name="FlockAffinityWorkUnitsPerTick">The flock-affinity share.</param>
/// <param name="DecisionImagePointsPerTick">The pose-image points a decision perception pass copies.</param>
/// <param name="DecisionGridBuildsPerTick">The grids a decision perception pass builds, one per distinct cell width.</param>
/// <param name="DecisionGridPointsPerTick">The points those grids group.</param>
public readonly record struct WorldRuleWorkBudget(int RuleRows, int InteractionRows, long EvaluationSlots, long WorkUnitsPerTick,
    long FlockAffinityWorkUnitsPerTick = 0, int DecisionImagePointsPerTick = 0, int DecisionGridBuildsPerTick = 0, long DecisionGridPointsPerTick = 0) {
    /// <summary>Measures a document's worst-case per-tick rule work.</summary>
    /// <param name="definition">The world.</param>
    public static WorldRuleWorkBudget Measure(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Measure(definition, WorldRuleCompiler.CompileAll(definition), WorldRuleCompiler.CompileAllInteractions(definition));
    }

    internal static WorldRuleWorkBudget Measure(WorldDefinition definition, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var context = WorldRuleCompiler.Context(definition: definition);
        var decisionScales = new HashSet<long>();

        foreach (var rule in rules) {
            if (rule.Decision is { } decision) {
                foreach (var option in decision.Options) {
                    if (option.Neighbors is { } neighbors) { decisionScales.Add(item: neighbors.CellWidth.Value); }
                }
            }
        }

        var lines = Lines(definition: definition, context: context, rules: rules, interactions: interactions);
        var (slots, work) = RuleWorkBudget.Tally(contributors: lines, writers: RuleWorkBudget.CountWriters(rules: Multiplied(definition: definition, context: context, rules: rules, interactions: interactions)));
        var flockCost = FlockAffinityCost(definition: definition, context: context);
        var imagePoints = ((decisionScales.Count == 0) ? 0 : definition.Population.Capacity);
        var gridPoints = ((long)definition.Population.Capacity * decisionScales.Count);
        // All policies may reconsider together. A pose image is shared; each grid copies and groups its points.
        var perceptionCost = RuleWorkBudget.SaturatingAdd(left: imagePoints, right: RuleWorkBudget.SaturatingMultiply(left: 2, right: gridPoints));

        return new WorldRuleWorkBudget(
            RuleRows: rules.Length,
            InteractionRows: interactions.Length,
            EvaluationSlots: slots,
            WorkUnitsPerTick: RuleWorkBudget.SaturatingAdd(left: RuleWorkBudget.SaturatingAdd(left: work, right: flockCost), right: perceptionCost),
            FlockAffinityWorkUnitsPerTick: flockCost,
            DecisionImagePointsPerTick: imagePoints,
            DecisionGridBuildsPerTick: decisionScales.Count,
            DecisionGridPointsPerTick: gridPoints
        );
    }

    /// <summary>Lists every contributor line, costliest first, then by name.</summary>
    /// <param name="definition">The world.</param>
    public static IReadOnlyList<RuleWorkContributor> Contributors(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return Contributors(definition, WorldRuleCompiler.CompileAll(definition), WorldRuleCompiler.CompileAllInteractions(definition));
    }

    internal static IReadOnlyList<RuleWorkContributor> Contributors(WorldDefinition definition, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var context = WorldRuleCompiler.Context(definition: definition);
        var contributors = Lines(definition: definition, context: context, rules: rules, interactions: interactions);

        contributors.Sort(comparison: static (left, right) => {
            var byWork = right.WorkUnits.CompareTo(value: left.WorkUnits);

            return ((byWork != 0) ? byWork : string.CompareOrdinal(strA: left.Name, strB: right.Name));
        });

        return contributors;
    }

    /// <summary>Describes why one contributor's multiplier is what it is — the forEach row's capacity, the
    /// interaction's carrier/pair-count formula, or the region-capacity bound's own terms — for
    /// <c>world.budget.rules --why</c>.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="rule">The compiled rule or interaction.</param>
    public static string DescribeMultiplier(WorldDefinition definition, CompiledWorldRule rule) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: rule);

        var capacity = definition.Population.Capacity;

        if (rule.Interaction is { } interaction) {
            var others = Math.Max(val1: 0, val2: (capacity - 1));

            return (interaction.CoOccurrence switch {
                WorldInteractionCoOccurrence.Distance => $"distance carrier pairs: population {capacity} x min(neighbours {((interaction.Neighbours > 0) ? interaction.Neighbours.ToString(provider: System.Globalization.CultureInfo.InvariantCulture) : "unbounded")}, others {others})",
                WorldInteractionCoOccurrence.Region => DescribeRegionMultiplier(definition: definition, placementId: interaction.Right),
                _ => $"population capacity {capacity}",
            });
        }

        return ((rule.ForEach is { } forEach)
            ? $"forEach '{forEach}' row capacity={RuleWorkBudget.ForEachCount(rule: rule, context: WorldRuleCompiler.Context(definition: definition))}"
            : "no forEach — priced once"
        );
    }

    private static string DescribeRegionMultiplier(WorldDefinition definition, string placementId) {
        var (bound, terms) = RegionCapacityBound(
            definition: definition,
            placementId: placementId
        );

        return $"region '{placementId}' {terms} = {bound}";
    }

    /// <summary>Lists every rule whose gate pins a cell to an empty range, with that cell.</summary>
    /// <param name="definition">The world.</param>
    public static IReadOnlyList<(string Rule, string Cell)> ContradictoryGates(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return ContradictoryGates(WorldRuleCompiler.CompileAll(definition));
    }

    internal static IReadOnlyList<(string Rule, string Cell)> ContradictoryGates(CompiledWorldRule[] rules) {
        var result = new List<(string, string)>();

        foreach (var rule in rules) {
            foreach (var pinned in RuleWorkBudget.PinnedCells(gate: rule.Gate, contradictory: out _)) {
                if (pinned.IsEmpty) {
                    result.Add(item: (rule.Name, pinned.Cell));
                }
            }
        }

        return result;
    }

    /// <summary>Returns the work units an expression costs against a document.</summary>
    public static long ExpressionCost(CompiledExpressionToken[] tokens, WorldDefinition definition) => RuleWorkBudget.ExpressionCost(tokens: tokens, context: WorldRuleCompiler.Context(definition: definition));

    // A rule carries no forEach at all when it reads only fixed cells — a literal seat's own channel
    // ($channel:<seat>:*) included — so ForEachCount's default of 1 already prices a per-seat rule once, not once
    // per body; nothing here needs to special-case that shape.
    private static long Multiplier(WorldDefinition definition, WorldRuleCompileContext context, CompiledWorldRule rule) {
        if (rule.Interaction is { } interaction) {
            var others = Math.Max(val1: 0, val2: (definition.Population.Capacity - 1));

            return (interaction.CoOccurrence switch {
                WorldInteractionCoOccurrence.Distance => ((long)definition.Population.Capacity * ((interaction.Neighbours > 0) ? Math.Min(val1: interaction.Neighbours, val2: others) : others)),
                WorldInteractionCoOccurrence.Region => RegionCapacityBound(definition: definition, placementId: interaction.Right).Bound,
                _ => definition.Population.Capacity,
            });
        }

        return RuleWorkBudget.ForEachCount(rule: rule, context: context);
    }

    /// <summary>Returns the most carriers a region interaction could ever have inside it, with the terms that
    /// produced it. Membership is by a body's own centre, so the carriers' centres all lie in a disc of radius R and
    /// two carriers of footprint radius r are at least 2r apart; their r-discs are then disjoint and all contained
    /// in a disc of radius R + r, so N·πr² ≤ π(R + r)² and N ≤ ((R + r)/r)². The result is clamped to the population
    /// capacity it replaces. Falls back to the population capacity when the region is absent, or when
    /// <see cref="SmallestKitFootprintRadius"/> cannot answer for it — the packing argument holds only among kits
    /// that mutually exclude each other, and a property (the tag a region interaction's <c>left</c> names) carries
    /// no static link to a kit, so any body of any kit could be the one standing in the region.</summary>
    private static (long Bound, string Terms) RegionCapacityBound(WorldDefinition definition, string placementId) {
        var capacity = (long)definition.Population.Capacity;
        var region = WorldDefinitionRows.FindPlacement(placements: definition.Placements, id: placementId)?.Region;

        if (region is null) {
            return (capacity, $"undeclared — population capacity {capacity}");
        }

        if ((SmallestKitFootprintRadius(definition: definition) is not { } footprint) || (footprint <= 0f)) {
            return (capacity, $"radius={region.Radius} — no solid-only kit footprint resolvable, population capacity {capacity}");
        }

        var reach = (((double)region.Radius + footprint) / footprint);
        var packed = (long)Math.Floor(d: (reach * reach));

        return (
            Math.Clamp(value: packed, min: 1L, max: capacity),
            $"radius={region.Radius}, smallest solid-kit footprint={footprint} -> floor(((R+r)/r)^2)={packed}, clamped to population {capacity}"
        );
    }

    // Two bodies depenetrate only when BOTH kits declare Solid contact (WorldBodyContactMode); an Overlap kit could
    // co-locate any number of its own bodies at one point, so its mere presence in the document defeats a
    // circle-packing bound for every region, not only the ones it could occupy.
    private static float? SmallestKitFootprintRadius(WorldDefinition definition) {
        float? smallest = null;

        foreach (var kit in definition.Kits) {
            if (kit.BodyContact != WorldBodyContactMode.Solid) { return null; }
            if (FootprintRadius(collider: kit.Collider) is not { } radius || (radius <= 0f)) { continue; }
            if ((smallest is null) || (radius < smallest)) { smallest = radius; }
        }

        return smallest;
    }

    // FromCreation carries no statically-derivable footprint; the caller falls back to the population capacity for it.
    private static float? FootprintRadius(WorldCollider? collider) => collider switch {
        WorldCollider.Sphere sphere => sphere.Radius,
        WorldCollider.Capsule capsule => capsule.Radius,
        WorldCollider.Box box => Math.Min(val1: box.HalfExtents.Value.X, val2: box.HalfExtents.Value.Z),
        _ => null,
    };

    private static List<(CompiledRule Rule, long Multiplier)> Multiplied(WorldDefinition definition, WorldRuleCompileContext context, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var result = new List<(CompiledRule, long)>(capacity: (rules.Length + interactions.Length));

        foreach (var rule in rules) { result.Add(item: (rule, Multiplier(definition: definition, context: context, rule: rule))); }
        foreach (var interaction in interactions) { result.Add(item: (interaction, Multiplier(definition: definition, context: context, rule: interaction))); }

        return result;
    }

    private static List<RuleWorkContributor> Lines(WorldDefinition definition, WorldRuleCompileContext context, CompiledWorldRule[] rules, CompiledWorldRule[] interactions) {
        var contributors = new List<RuleWorkContributor>(capacity: (rules.Length + interactions.Length));

        foreach (var rule in rules) {
            contributors.Add(item: RuleWorkBudget.Contributor(rule: rule, multiplier: Multiplier(definition: definition, context: context, rule: rule), isInteraction: false, context: context));
        }
        foreach (var interaction in interactions) {
            contributors.Add(item: RuleWorkBudget.Contributor(rule: interaction, multiplier: Multiplier(definition: definition, context: context, rule: interaction), isInteraction: true, context: context));
        }

        return contributors;
    }

    private static long FlockAffinityCost(WorldDefinition definition, WorldRuleCompileContext context) {
        var maximum = 0L;

        foreach (var kit in definition.Kits) {
            foreach (var producer in kit.Producers.Values) {
                if (producer.Flock is not { } profile) { continue; }

                var compiled = new CompiledWorldFlockAffinities(profile, definition);

                maximum = Math.Max(val1: maximum, val2: RuleWorkBudget.SaturatingMultiply(left: profile.MaxNeighbors, right: compiled.WorkUnitsPerNeighbor));
            }
        }

        // Initial samples and explicit producer changes may align every observer, regardless of normal cadence.
        return RuleWorkBudget.SaturatingMultiply(left: definition.Population.Capacity, right: maximum);
    }
}

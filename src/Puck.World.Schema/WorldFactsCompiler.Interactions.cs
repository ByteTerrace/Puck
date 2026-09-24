using CompiledExpressionToken = Puck.State.Rules.CompiledExpressionToken;
using CompiledRule = Puck.State.Rules.CompiledRule;
using RuleCompiler = Puck.State.Rules.RuleCompiler;

namespace Puck.World;

public static partial class WorldFactsCompiler {
    /// <summary>Compiles every interaction the document declares, in document order.</summary>
    /// <param name="definition">The world.</param>
    /// <returns>The compiled interactions.</returns>
    /// <exception cref="RuleException">An interaction is malformed.</exception>
    public static CompiledRule[] CompileAllInteractions(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return ((definition.Interactions?.Interactions is { Count: > 0 })
            ? CompileAllInteractions(
                context: Context(definition: definition),
                definition: definition
            )
            : []
        );
    }
    /// <summary>Compiles every interaction the document declares against an existing context.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="context">The compile context, shared with the rules and tables of the same document.</param>
    /// <returns>The compiled interactions.</returns>
    /// <exception cref="RuleException">An interaction is malformed.</exception>
    public static CompiledRule[] CompileAllInteractions(WorldDefinition definition, WorldFactsCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: definition);

        var interactions = (definition.Interactions?.Interactions ?? []);

        if (interactions.Count == 0) {
            return [];
        }
        if (interactions.Count > WorldInteractionCapacity.MaxInteractions) {
            throw new RuleException(
                detail: $"declares {interactions.Count} rows, exceeding the {WorldInteractionCapacity.MaxInteractions}-interaction ceiling",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: "<interactions>",
                subject: "interaction"
            );
        }

        var registry = new HashSet<string>(
            collection: (definition.Properties?.Names ?? []),
            comparer: StringComparer.Ordinal
        );
        var seen = new HashSet<string>(
            capacity: interactions.Count,
            comparer: StringComparer.Ordinal
        );
        var compiled = new CompiledRule[interactions.Count];

        for (var index = 0; (index < interactions.Count); index++) {
            compiled[index] = CompileInteraction(
                context: context,
                registry: registry,
                row: (interactions[index] ?? throw new RuleException(
                    detail: "declares a null row",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: "<interactions>",
                    subject: "interaction"
                )),
                seen: seen
            );
        }

        return compiled;
    }

    private static CompiledRule CompileInteraction(WorldInteraction row, HashSet<string> registry, HashSet<string> seen, WorldFactsCompileContext context) {
        var name = row.Name.Value;

        RuleCompiler.RequireName(
            name: name,
            seen: seen,
            subject: "interaction"
        );

        var leftPool = FindInteractionPool(context: context, name: row.Left);
        var rightPool = ((row.CoOccurrence == WorldInteractionCoOccurrence.Distance) ? FindInteractionPool(context: context, name: row.Right) : null);

        if ((leftPool is null) && !registry.Contains(item: row.Left)) {
            throw new RuleException(
                detail: $"'left' names '{row.Left}', which is not a registered property (see the 'properties' section)",
                refusal: WorldRuleRefusal.PropertyUnknown,
                ruleName: name,
                subject: "interaction"
            );
        }

        if (leftPool is null) {
            _ = RuleCompiler.ResolveNumericRow(
            channel: "left",
            context: context,
            malformed: WorldRuleRefusal.PropertyUnknown,
            name: row.Left,
            requireKeyed: true,
            ruleName: name
        );
        }
        context.ClearScope();

        switch (row.CoOccurrence) {
            case WorldInteractionCoOccurrence.Distance:
                if ((rightPool is null) && !registry.Contains(item: row.Right)) {
                    throw new RuleException(
                        detail: $"'right' names '{row.Right}', which is not a registered property (see the 'properties' section)",
                        refusal: WorldRuleRefusal.PropertyUnknown,
                        ruleName: name,
                        subject: "interaction"
                    );
                }

                if (rightPool is null) {
                    _ = RuleCompiler.ResolveNumericRow(
                    channel: "right",
                    context: context,
                    malformed: WorldRuleRefusal.PropertyUnknown,
                    name: row.Right,
                    requireKeyed: true,
                    ruleName: name
                );
                }

                if (row.Range < decimal.Zero) {
                    throw new RuleException(
                        detail: $"'range' {row.Range} is not a non-negative distance",
                        refusal: WorldRuleRefusal.SpatialChannelMalformed,
                        ruleName: name,
                        subject: "interaction"
                    );
                }
                if (
                    (row.Neighbours is { } neighbours) &&
                    ((neighbours < 1) || (neighbours > WorldInteractionCapacity.MaxNeighbours))
                ) {
                    throw new RuleException(
                        detail: $"'neighbours' is {neighbours}; 1..{WorldInteractionCapacity.MaxNeighbours} are admitted",
                        refusal: RuleRefusal.PredicateKindInadmissible,
                        ruleName: name,
                        subject: "interaction"
                    );
                }

                context.BindingScope = [BoundKey.Left, BoundKey.Right];

                break;
            case WorldInteractionCoOccurrence.Region:
                if (row.Neighbours is not null) {
                    throw new RuleException(
                        detail: "'neighbours' budgets a distance interaction's pairs; a region interaction has no pairs",
                        refusal: RuleRefusal.PredicateKindInadmissible,
                        ruleName: name,
                        subject: "interaction"
                    );
                }
                if (!context.HasRegion(placementId: row.Right)) {
                    throw new RuleException(
                        detail: $"'right' names placement '{row.Right}', which declares no region facet",
                        refusal: WorldRuleRefusal.RegionUnknown,
                        ruleName: name,
                        subject: "interaction"
                    );
                }

                context.BindingScope = [BoundKey.Left];

                break;
            default:
                throw new RuleException(
                    detail: $"'coOccurrence' value '{row.CoOccurrence}' is not a defined WorldInteractionCoOccurrence",
                    refusal: RuleRefusal.PredicateKindInadmissible,
                    ruleName: name,
                    subject: "interaction"
                );
        }

        try {
            var leftBinding = ((leftPool is null) ? -1 : context.PushInstanceBinding(name: CellName.Parse(candidate: "left"), pool: leftPool).Slot);
            var rightBinding = ((rightPool is null) ? -1 : context.PushInstanceBinding(name: CellName.Parse(candidate: "right"), pool: rightPool).Slot);
            var effects = RuleCompiler.CompileEffects(
                context: context,
                effects: row.Effects,
                ruleName: name,
                subject: "interaction"
            );

            return new CompiledWorldFactsRule(
                decision: null,
                interaction: new CompiledInteraction(
                    CoOccurrence: row.CoOccurrence,
                    Left: row.Left,
                    Neighbours: (row.Neighbours ?? 0),
                    Range: NumericLiteral.ToFixed(value: row.Range),
                    Right: row.Right,
                    LeftPool: leftPool,
                    RightPool: rightPool,
                    LeftBinding: leftBinding,
                    RightBinding: rightBinding,
                    LeftCarrier: ((leftPool is null) ? null : WorldPoolBodyBindings.Compile(definition: context.Definition, pool: leftPool)),
                    RightCarrier: ((rightPool is null) ? null : WorldPoolBodyBindings.Compile(definition: context.Definition, pool: rightPool))
                ),
                original: new CompiledRule(
                    Locals: RuleCompiler.AllLocals(
                        context: context,
                        declared: []
                    ),
                    Describe: $"interaction {name}",
                    Effects: effects,
                    Gate: [],
                    Mode: row.Mode,
                    Name: name,
                    Needs: context.Needs.Build()
                )
            );
        } finally {
            context.ClearScope();
        }
    }
    private static StatePoolDescriptor? FindInteractionPool(WorldFactsCompileContext context, string name) =>
        ((CellName.TryParse(candidate: name, name: out var parsed, reason: out _) && context.Catalog.TryGetPool(name: parsed, pool: out var pool)) ? pool : null);

    /// <summary>Compiles a flock-affinity expression over <c>$left</c>/<c>$right</c>, refusing an operand whose
    /// value is not state-backed.</summary>
    /// <param name="expression">The authored expression.</param>
    /// <param name="definition">The world.</param>
    /// <returns>The compiled program.</returns>
    /// <exception cref="RuleException">An operand is not a state-backed fact.</exception>
    /// <remarks>A movement-pass observation must be order-independent, so a host fact is refused here.</remarks>
    public static CompiledExpressionToken[] CompileFlockAffinity(ExpressionProgram expression, WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: expression);

        var context = Context(definition: definition);

        context.BindingScope = [BoundKey.Left, BoundKey.Right];

        try {
            var tokens = RuleCompiler.CompileExpression(
                context: context,
                expression: expression,
                kind: CellKind.Fixed,
                ruleName: "flock affinity",
                verb: "affinity"
            );
            var needs = new RuleNeedsBuilder();

            Puck.State.Rules.RuleDataflow.CollectExpressionFacts(
                into: needs,
                tokens: tokens
            );
            if (needs.Build().Facets.Count != 0) {
                throw new RuleException(
                    detail: "a host fact is not state-backed; movement-pass observations must be order-independent",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: "flock affinity"
                );
            }

            return tokens;
        } finally {
            context.ClearScope();
        }
    }
    /// <summary>Compiles a pattern row's value expression over its token domain.</summary>
    /// <param name="definition">The world.</param>
    /// <param name="pattern">The pattern row.</param>
    /// <param name="tokenDomain">The row the pattern's tokens are read from.</param>
    /// <param name="ruleName">The rule a refusal names.</param>
    /// <param name="tokens">The compiled program.</param>
    /// <param name="reason">Why the expression was refused, or empty.</param>
    /// <returns><see langword="true"/> when the expression compiled.</returns>
    public static bool TryCompilePatternValue(WorldDefinition definition, PatternRow pattern, string tokenDomain, string ruleName, out CompiledExpressionToken[]? tokens, out string reason) => RuleCompiler.TryCompilePatternValue(
        context: Context(definition: definition),
        pattern: pattern,
        reason: out reason,
        ruleName: ruleName,
        tokenDomain: tokenDomain,
        tokens: out tokens
    );
}

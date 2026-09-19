using System.Numerics;
using System.Text.Json;
using Puck.Maths;
using CompiledExpressionToken = Puck.State.Rules.CompiledExpressionToken;
using CompiledRule = Puck.State.Rules.CompiledRule;
using GateToken = Puck.State.Rules.GateToken;
using IRuleCostContext = Puck.State.IRuleCostContext;
using IRuleEffect = Puck.State.Rules.IRuleEffect;
using RuleCompiler = Puck.State.Rules.RuleCompiler;
using RuleCost = Puck.State.Rules.RuleCost;
using RuleDataflow = Puck.State.Rules.RuleDataflow;
using RuleRefusal = Puck.State.Rules.RuleRefusal;
using RuleWorkBudget = Puck.State.Rules.RuleWorkBudget;

namespace Puck.World;

/// <summary>Validated, fixed-point neighbour perception parameters for an arena-addressed decision option.</summary>
/// <param name="Source">The authored parameters.</param>
/// <param name="Range">The inclusive spherical radius.</param>
/// <param name="MinimumDot">The cosine of the forward cone's half-angle.</param>
public sealed record CompiledWorldFactsDecisionNeighbors(WorldDecisionNeighbors Source, FixedQ4816 Range, FixedQ4816 MinimumDot) {
    /// <summary>Gets the power-of-two raw grid width shared by ranges of the same scale, which bounds each query to
    /// 27 cells.</summary>
    public FixedQ4816 CellWidth => FixedQ4816.FromRawBits(value: checked((long)BitOperations.RoundUpToPowerOf2(value: ((ulong)Range.Value))));
}
/// <summary>One decision option compiled through the arena-addressed gate, expression, and effect compilers.</summary>
/// <param name="Name">The option's authored name.</param>
/// <param name="Gate">The eligibility gate; empty means always eligible.</param>
/// <param name="Score">The score program, in the decision's score kind.</param>
/// <param name="Effects">The effects fired on entering the option.</param>
/// <param name="Neighbors">The bounded perception parameters, or <see langword="null"/>.</param>
public sealed record CompiledWorldFactsDecisionOption(string Name, GateToken[] Gate, CompiledExpressionToken[] Score, IRuleEffect[] Effects, CompiledWorldFactsDecisionNeighbors? Neighbors = null);
/// <summary>An arena-addressed choice policy. <paramref name="PolicyIdentity"/> is the canonical source rule and
/// seeds lifecycle reconciliation, never randomness.</summary>
/// <param name="Options">The options, in authored tie-break order.</param>
/// <param name="Mode">Highest score or weighted choice.</param>
/// <param name="ScoreKind">The kind every score is read in.</param>
/// <param name="PeriodTicks">The engine ticks between reconsiderations.</param>
/// <param name="CommitmentTicks">The engine ticks a fresh selection is protected for.</param>
/// <param name="IncumbentBonus">The raw score bonus for retaining the current option.</param>
/// <param name="Seed">The authored seed.</param>
/// <param name="Interrupt">The gate that bypasses period and commitment, or <see langword="null"/>.</param>
/// <param name="OnNoChoice">The effects fired when the decision holds no choice.</param>
/// <param name="PolicyIdentity">The canonical source rule.</param>
public sealed record CompiledWorldFactsDecision(
    CompiledWorldFactsDecisionOption[] Options, WorldDecisionMode Mode, CellKind ScoreKind,
    ulong PeriodTicks, ulong CommitmentTicks, long IncumbentBonus, ulong Seed,
    GateToken[]? Interrupt, IRuleEffect[] OnNoChoice, string PolicyIdentity
);
/// <summary>The library's compiled rule plus the two policies only a world evaluates: the choice policy a decision
/// rule carries and the co-occurrence an interaction sweeps.</summary>
public sealed record CompiledWorldFactsRule : CompiledRule {
    /// <summary>Initializes the rule over the library's own compiled form.</summary>
    /// <param name="original">The library's compiled rule.</param>
    /// <param name="decision">The compiled choice policy, or <see langword="null"/>.</param>
    /// <param name="interaction">The compiled co-occurrence, or <see langword="null"/>.</param>
    public CompiledWorldFactsRule(CompiledRule original, CompiledWorldFactsDecision? decision, CompiledInteraction? interaction = null) : base(original: original) {
        Decision = decision;
        Interaction = interaction;
    }

    /// <summary>Gets the compiled choice policy, or <see langword="null"/>.</summary>
    public CompiledWorldFactsDecision? Decision { get; init; }
    /// <summary>Gets the co-occurrence an interaction sweeps, or <see langword="null"/> for a rule.</summary>
    public CompiledInteraction? Interaction { get; init; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        base.CollectReads(into: into);
        if (Decision is not { } decision) {
            return;
        }

        RuleDataflow.CollectGate(
            gate: (decision.Interrupt ?? []),
            into: into
        );
        foreach (var effect in decision.OnNoChoice) {
            effect.CollectReads(into: into);
        }
        foreach (var option in decision.Options) {
            RuleDataflow.CollectGate(
                gate: option.Gate,
                into: into
            );
            RuleDataflow.CollectExpression(
                into: into,
                tokens: option.Score
            );
            foreach (var effect in option.Effects) {
                effect.CollectReads(into: into);
            }
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        base.CollectWrites(into: into);
        if (Decision is not { } decision) {
            return;
        }

        foreach (var effect in decision.OnNoChoice) {
            effect.CollectWrites(into: into);
        }
        foreach (var option in decision.Options) {
            foreach (var effect in option.Effects) {
                effect.CollectWrites(into: into);
            }
        }
    }
    /// <summary>Prices what only a world evaluates beside the library's rule: an interaction's carrier sweep as
    /// setup, and a decision's perception, scoring, and selection as check, with its costliest branch as effects.</summary>
    /// <inheritdoc/>
    public override RuleCost CostBreakdown(IRuleCostContext context) {
        var baseBreakdown = base.CostBreakdown(context: context);

        if (Interaction is { } interaction) {
            return (baseBreakdown with {
                Setup = (baseBreakdown.Setup + WorldInteractionBound.Of(
                    context: ((WorldFactsCompileContext)context),
                    interaction: interaction
                ).Setup),
            });
        }
        if (Decision is not { } decision) {
            return baseBreakdown;
        }

        var capacity = ((long)((WorldFactsCompileContext)context).Definition.Population.Capacity);
        var check = baseBreakdown.Check;
        var choices = 0L;
        var currentGate = RuleWork.Zero;
        var branch = RuleWorkBudget.EffectsCost(
            context: context,
            effects: decision.OnNoChoice
        );

        foreach (var option in decision.Options) {
            var gate = RuleWorkBudget.GateCost(
                context: context,
                tokens: option.Gate
            );
            var score = RuleWorkBudget.ExpressionCost(
                context: context,
                kind: decision.ScoreKind,
                tokens: option.Score
            );

            currentGate = RuleWork.Max(
                left: currentGate,
                right: gate
            );
            if (option.Neighbors is { } neighbors) {
                var budget = ((long)neighbors.Source.CandidateBudget);
                var retained = ((long)neighbors.Source.MaxCandidates);
                var sift = RuleWorkBudget.SearchSteps(count: budget);

                // The 27 cells around the observer, each found by a binary search of the grid's cells.
                check += (27L * (1L + RuleWorkBudget.SearchSteps(count: capacity)));
                // The query's cursor visits every found cell between two examinations once the small cells are
                // spent; each examination is a distance test and one sift into the retained heap, and the heap is
                // drained by one sift per retained neighbour.
                check += (budget * RuleWork.Known(units: ((27L + 1L) + (2L * sift))));
                // Each neighbour the query wrote is tested for perception, then against the option's gate.
                check += (budget * ((neighbors.Source.RequiresLineOfSight
                    ? (1L + WorldRuleCapacity.SightTestWork)
                    : 1L
                ) + gate));
                check += (retained * (1L + score));
                check += RuleWorkBudget.IntrosortWork(count: retained);
                choices += retained;
            } else {
                check += ((1L + gate) + score);
                choices++;
            }

            branch = RuleWork.Max(
                left: branch,
                right: RuleWorkBudget.EffectsCost(
                    context: context,
                    effects: option.Effects
                )
            );
        }

        // The incumbent's gate is read again to learn whether it lost eligibility, the interrupt gate bypasses the
        // period, and selection walks every gathered choice twice: once to total or rank, once to pick.
        check += currentGate;
        check += RuleWorkBudget.GateCost(
            context: context,
            tokens: (decision.Interrupt ?? [])
        );
        check += (2L * choices);

        return new RuleCost(
            Check: check,
            Effects: (baseBreakdown.Effects + branch),
            Setup: (baseBreakdown.Setup + DecisionSetup(
                context: context,
                rule: this
            ))
        );
    }

    // A decision's sweep gathers the iterated row's integer keys and sorts them, drops the bindings of keys that
    // left by a binary search each, and copies the distinct keys. The library's own forEach setup already pays for
    // the key snapshot.
    private static RuleWork DecisionSetup(CompiledRule rule, IRuleCostContext context) {
        if (rule.ForEachOrdinal < 0) {
            return RuleWork.Known(units: 1L);
        }

        var keys = context.RowCapacity(rowOrdinal: rule.ForEachOrdinal);

        return (RuleWorkBudget.IntrosortWork(count: keys) + (keys * RuleWork.Known(units: (1L + RuleWorkBudget.SearchSteps(count: keys)))));
    }
}
public static partial class WorldFactsCompiler {
    /// <summary>Compiles a world rule's choice policy inside the rule's own compile scope, or returns
    /// <see langword="null"/> when the rule declares none.</summary>
    /// <param name="rule">The authored rule.</param>
    /// <param name="context">The compile context, with the rule's scope open.</param>
    /// <returns>The compiled policy, or <see langword="null"/>.</returns>
    /// <exception cref="RuleException">The policy is malformed.</exception>
    public static CompiledWorldFactsDecision? CompileDecision(WorldRule rule, WorldFactsCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rule);

        if (rule.Decision is not { } decision) {
            return null;
        }

        RuleException Refuse(string detail) => new(
            detail: $"decision {detail}",
            path: "decision",
            refusal: RuleRefusal.EffectKindInadmissible,
            ruleName: rule.Name
        );

        if (rule.Mode != ActionTriggerMode.Level) {
            throw Refuse(detail: "requires Level rule mode");
        }
        if (string.Equals(
            a: rule.ForEach,
            b: RuleFacts.ForEachZones,
            comparisonType: StringComparison.Ordinal
        )) {
            throw Refuse(detail: "iterates bodies, never a zone table — forEach names a keyed row");
        }
        if (
            !Enum.IsDefined(value: decision.Mode) ||
            (decision.ScoreKind is not (CellKind.Int or CellKind.Fixed))
        ) {
            throw Refuse(detail: "requires a defined mode and an Int or Fixed scoreKind");
        }
        if (
            !FixedTickConversion.TryDurationEngineTicksExact(
            seconds: decision.PeriodSeconds,
            ticks: out var period
        ) ||
            (period == 0) ||
            !FixedTickConversion.TryDurationEngineTicksExact(
            seconds: decision.CommitmentSeconds,
            ticks: out var commitment
        )
        ) {
            throw Refuse(detail: "periodSeconds must be positive and commitmentSeconds non-negative; both must fit exact engine ticks");
        }
        if (decision.IncumbentBonus < 0) {
            throw Refuse(detail: "incumbentBonus must be non-negative");
        }

        var bonus = RuleCompiler.LiteralToRaw(
            kind: decision.ScoreKind,
            literal: decision.IncumbentBonus,
            ruleName: rule.Name,
            verb: "decision incumbentBonus"
        );

        if (decision.Options is not { Count: > 0 and <= WorldRuleCapacity.MaxDecisionOptions }) {
            throw Refuse(detail: $"must carry 1..{WorldRuleCapacity.MaxDecisionOptions} options");
        }

        var options = new CompiledWorldFactsDecisionOption[decision.Options.Count];
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < options.Length); index++) {
            var option = decision.Options[index];

            if (
                (option is null) ||
                string.IsNullOrWhiteSpace(value: option.Name.Value) ||
                !names.Add(item: option.Name.Value)
            ) {
                throw Refuse(detail: "option names must be present and unique");
            }

            try {
                options[index] = CompileOption(
                    context: context,
                    decision: decision,
                    option: option,
                    refuse: Refuse,
                    rule: rule
                );
            } catch (RuleException error) { throw error.Within(segment: $"decision.options[{index}]"); }
        }

        IRuleEffect[] onNoChoice;
        GateToken[]? interrupt;

        try {
            onNoChoice = CompileDecisionEffects(
                context: context,
                effects: (decision.OnNoChoice ?? []),
                ruleName: rule.Name
            );
        } catch (RuleException error) { throw error.Within(segment: "decision.onNoChoice"); }
        try {
            interrupt = ((decision.Interrupt is null)
                ? null
                : RuleCompiler.CompileGate(
                    context: context,
                    predicate: decision.Interrupt,
                    ruleName: rule.Name
                )
            );
        } catch (RuleException error) { throw error.Within(segment: "decision.interrupt"); }

        // The policy's own branches carry facts, so the rule's needs are read off them too.
        RuleDataflow.CollectGateFacts(
            gate: (interrupt ?? []),
            into: context.Needs
        );
        RuleDataflow.CollectEffectFacts(
            effects: onNoChoice,
            into: context.Needs
        );
        foreach (var option in options) {
            RuleDataflow.CollectEffectFacts(
                effects: option.Effects,
                into: context.Needs
            );
            RuleDataflow.CollectExpressionFacts(
                into: context.Needs,
                tokens: option.Score
            );
            RuleDataflow.CollectGateFacts(
                gate: option.Gate,
                into: context.Needs
            );
        }

        return new CompiledWorldFactsDecision(
            CommitmentTicks: commitment,
            IncumbentBonus: bonus,
            Interrupt: interrupt,
            Mode: decision.Mode,
            OnNoChoice: onNoChoice,
            Options: options,
            PeriodTicks: period,
            PolicyIdentity: JsonSerializer.Serialize(
                jsonTypeInfo: WorldJsonContext.Default.WorldRule,
                value: rule
            ),
            ScoreKind: decision.ScoreKind,
            Seed: decision.Seed
        );
    }

    private static IRuleEffect[] CompileDecisionEffects(IReadOnlyList<ActionEffect>? effects, string ruleName, WorldFactsCompileContext context) {
        if (effects is null) {
            throw new RuleException(
                detail: "decision effects must be an array",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        return RuleCompiler.CompileEffects(
            allowEmpty: true,
            context: context,
            effects: effects,
            ruleName: ruleName,
            subject: "decision"
        );
    }
    private static CompiledWorldFactsDecisionOption CompileOption(WorldDecisionOption option, WorldRule rule, WorldDecision decision, WorldFactsCompileContext context, Func<string, RuleException> refuse) {
        CompiledWorldFactsDecisionNeighbors? neighbors = null;

        if (option.Neighbors is { } source) {
            if (rule.ForEach is null) {
                throw refuse(arg: "neighbors requires forEach body keys");
            }
            if (
                (source.Range < (1m / 65536m)) ||
                (source.Range > 1_000_000m) ||
                (source.HalfAngleDegrees <= 0) ||
                (source.HalfAngleDegrees > 180) ||
                (source.CandidateBudget < 1) ||
                (source.CandidateBudget > WorldBodiesLimits.CapacityCeiling) ||
                (source.MaxCandidates < 1) ||
                (source.MaxCandidates > WorldRuleCapacity.MaxDecisionCandidates) ||
                (source.MaxCandidates > source.CandidateBudget)
            ) {
                throw refuse(arg: "neighbors requires range [1/65536,1000000], halfAngleDegrees (0,180], and 1 <= maxCandidates <= min(32,candidateBudget), with candidateBudget within body capacity ceiling");
            }

            var radians = ((source.HalfAngleDegrees * FixedQ4816.PiQ61) / (180m * (1UL << FixedQ4816.PiQ61FractionBitCount)));

            neighbors = new CompiledWorldFactsDecisionNeighbors(
                MinimumDot: FixedQ4816.Cos(angle: FixedQ4816.FromRawBits(value: RuleCompiler.LiteralToRaw(
                    kind: CellKind.Fixed,
                    literal: radians,
                    ruleName: rule.Name,
                    verb: "decision neighbors angle"
                ))),
                Range: FixedQ4816.FromRawBits(value: RuleCompiler.LiteralToRaw(
                    kind: CellKind.Fixed,
                    literal: source.Range,
                    ruleName: rule.Name,
                    verb: "decision neighbors range"
                )),
                Source: source
            );
        }

        var scope = context.BindingScope;

        if (neighbors is not null) {
            context.BindingScope = [BoundKey.Each, BoundKey.Left, BoundKey.Right];
        }

        try {
            var effects = CompileDecisionEffects(
                context: context,
                effects: option.Effects,
                ruleName: rule.Name
            );

            return new CompiledWorldFactsDecisionOption(
                Effects: effects,
                Gate: RuleCompiler.CompileGate(
                    context: context,
                    predicate: option.Gate,
                    ruleName: rule.Name
                ),
                Name: option.Name.Value,
                Neighbors: neighbors,
                Score: RuleCompiler.CompileExpression(
                    context: context,
                    expression: option.Score,
                    kind: decision.ScoreKind,
                    ruleName: rule.Name,
                    verb: "decision score"
                )
            );
        } finally {
            context.BindingScope = scope;
        }
    }
}

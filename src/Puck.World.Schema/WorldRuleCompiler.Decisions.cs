using System.Text.Json;
using Puck.Maths;

namespace Puck.World;

public static partial class WorldRuleCompiler {
    private static EffectFact[] CompileDecisionEffects(IReadOnlyList<ActionEffect>? effects, string ruleName, WorldRuleCompileContext context) {
        if (effects is null) {
            throw new RuleException(refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName, detail: "decision effects must be an array");
        }

        return ((effects.Count == 0) ? [] : RuleCompiler.CompileEffects(effects: effects, ruleName: ruleName, context: context, subject: "decision"));
    }

    private static CompiledWorldDecision? CompileDecision(WorldRule rule, WorldRuleCompileContext context) {
        if (rule.Decision is not { } decision) {
            return null;
        }

        RuleException Refuse(string detail) => new(refusal: RuleRefusal.EffectKindInadmissible, ruleName: rule.Name, detail: $"decision {detail}");

        if (rule.Mode != ActionTriggerMode.Level) { throw Refuse("requires Level rule mode"); }
        if (string.Equals(a: rule.ForEach, b: RuleFacts.ForEachZones, comparisonType: StringComparison.Ordinal)) { throw Refuse("iterates bodies, never a zone table — forEach names a keyed row"); }
        if (!Enum.IsDefined(value: decision.Mode) || decision.ScoreKind is not (CellKind.Int or CellKind.Fixed)) {
            throw Refuse("requires a defined mode and an Int or Fixed scoreKind");
        }
        if (
            !FixedTickConversion.TryDurationEngineTicksExact(seconds: decision.PeriodSeconds, ticks: out var period) || (period == 0) ||
            !FixedTickConversion.TryDurationEngineTicksExact(seconds: decision.CommitmentSeconds, ticks: out var commitment)
        ) {
            throw Refuse("periodSeconds must be positive and commitmentSeconds non-negative; both must fit exact engine ticks");
        }
        if (decision.IncumbentBonus < 0) { throw Refuse("incumbentBonus must be non-negative"); }

        var bonus = RuleCompiler.LiteralToRaw(kind: decision.ScoreKind, literal: decision.IncumbentBonus, ruleName: rule.Name, verb: "decision incumbentBonus");

        if (decision.Options is not { Count: > 0 and <= WorldRuleCapacity.MaxDecisionOptions }) {
            throw Refuse($"must carry 1..{WorldRuleCapacity.MaxDecisionOptions} options");
        }

        var options = new CompiledWorldDecisionOption[decision.Options.Count];
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; index < options.Length; index++) {
            var option = decision.Options[index];

            if ((option is null) || string.IsNullOrWhiteSpace(value: option.Name.Value) || !names.Add(item: option.Name.Value)) {
                throw Refuse("option names must be present and unique");
            }

            CompiledWorldDecisionNeighbors? neighbors = null;

            if (option.Neighbors is { } source) {
                if (rule.ForEach is null) { throw Refuse("neighbors requires forEach body keys"); }
                if (
                    (source.Range < (1m / 65536m)) || (source.Range > 1_000_000m) ||
                    (source.HalfAngleDegrees <= 0) || (source.HalfAngleDegrees > 180) ||
                    (source.CandidateBudget < 1) || (source.CandidateBudget > WorldBodiesLimits.CapacityCeiling) ||
                    (source.MaxCandidates < 1) || (source.MaxCandidates > WorldRuleCapacity.MaxDecisionCandidates) ||
                    (source.MaxCandidates > source.CandidateBudget)
                ) {
                    throw Refuse("neighbors requires range [1/65536,1000000], halfAngleDegrees (0,180], and 1 <= maxCandidates <= min(32,candidateBudget), with candidateBudget within body capacity ceiling");
                }

                var range = FixedQ4816.FromRawBits(value: RuleCompiler.LiteralToRaw(kind: CellKind.Fixed, literal: source.Range, ruleName: rule.Name, verb: "decision neighbors range"));
                var radians = (source.HalfAngleDegrees * FixedQ4816.PiQ61 / (180m * (1UL << FixedQ4816.PiQ61FractionBitCount)));
                var angle = FixedQ4816.FromRawBits(value: RuleCompiler.LiteralToRaw(kind: CellKind.Fixed, literal: radians, ruleName: rule.Name, verb: "decision neighbors angle"));

                neighbors = new CompiledWorldDecisionNeighbors(Source: source, Range: range, MinimumDot: FixedQ4816.Cos(angle));
            }

            var scope = context.BindingScope;

            if (neighbors is not null) { context.BindingScope = [BoundKey.Each, BoundKey.Left, BoundKey.Right]; }

            try {
                options[index] = new CompiledWorldDecisionOption(
                    Name: option.Name,
                    Gate: RuleCompiler.CompileGate(predicate: option.Gate, ruleName: rule.Name, context: context),
                    Score: RuleCompiler.CompileExpression(expression: option.Score, kind: decision.ScoreKind, ruleName: rule.Name, verb: "decision score", context: context),
                    Effects: CompileDecisionEffects(effects: option.Effects, ruleName: rule.Name, context: context),
                    Neighbors: neighbors
                );
            } finally {
                context.BindingScope = scope;
            }
        }

        return new CompiledWorldDecision(
            Options: options,
            Mode: decision.Mode,
            ScoreKind: decision.ScoreKind,
            PeriodTicks: period,
            CommitmentTicks: commitment,
            IncumbentBonus: bonus,
            Seed: decision.Seed,
            Interrupt: ((decision.Interrupt is null) ? null : RuleCompiler.CompileGate(predicate: decision.Interrupt, ruleName: rule.Name, context: context)),
            OnNoChoice: CompileDecisionEffects(effects: (decision.OnNoChoice ?? []), ruleName: rule.Name, context: context),
            PolicyIdentity: JsonSerializer.Serialize(value: rule, jsonTypeInfo: WorldJsonContext.Default.WorldRule)
        );
    }
}

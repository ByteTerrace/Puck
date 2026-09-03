using System.Text.Json;
using Puck.Maths;
using Puck.Physics.Motion;

namespace Puck.World;

public static partial class WorldRuleCompiler {
    private static CompiledWorldEffect[] CompileDecisionEffects(IReadOnlyList<ActionEffect>? effects, string ruleName, WorldDefinition definition) {
        if (effects is null) {
            throw new WorldRuleException(WorldRuleRefusal.EffectKindInadmissible, ruleName, "decision effects must be an array");
        }
        return effects.Count == 0 ? [] : CompileEffects(effects, ruleName, definition, "decision");
    }

    private static CompiledWorldDecision? CompileDecision(WorldRule rule, WorldDefinition definition) {
        if (rule.Decision is not { } decision) { return null; }
        WorldRuleException Refuse(string detail) => new(WorldRuleRefusal.EffectKindInadmissible, rule.Name, $"decision {detail}");
        if (rule.Mode != ActionTriggerMode.Level) { throw Refuse("requires Level rule mode"); }
        if (!Enum.IsDefined(decision.Mode) || decision.ScoreKind is not (CellKind.Int or CellKind.Fixed)) {
            throw Refuse("requires a defined mode and an Int or Fixed scoreKind");
        }
        if (!FixedTickConversion.TryDurationEngineTicksExact(decision.PeriodSeconds, out var period) || period == 0 ||
            !FixedTickConversion.TryDurationEngineTicksExact(decision.CommitmentSeconds, out var commitment)) {
            throw Refuse("periodSeconds must be positive and commitmentSeconds non-negative; both must fit exact engine ticks");
        }
        if (decision.IncumbentBonus < 0) { throw Refuse("incumbentBonus must be non-negative"); }
        var bonus = LiteralToRaw(decision.ScoreKind, decision.IncumbentBonus, rule.Name, "decision incumbentBonus");
        if (decision.Options is not { Count: > 0 and <= WorldRuleCapacity.MaxDecisionOptions }) {
            throw Refuse($"must carry 1..{WorldRuleCapacity.MaxDecisionOptions} options");
        }
        CompiledWorldPredicate[] Gate(ActionPredicate? source) {
            var tokens = new List<CompiledWorldPredicate>();
            FlattenPredicate(source, tokens, rule.Name, definition);
            if (tokens.Count > WorldRuleCapacity.MaxPredicateTokens) { throw Refuse("gate exceeds the predicate token ceiling"); }
            return tokens.ToArray();
        }
        var options = new CompiledWorldDecisionOption[decision.Options.Count];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < options.Length; index++) {
            var option = decision.Options[index];
            if (option is null || string.IsNullOrWhiteSpace(option.Name.Value) || !names.Add(option.Name.Value)) {
                throw Refuse("option names must be present and unique");
            }
            CompiledWorldDecisionNeighbors? neighbors = null;
            if (option.Neighbors is { } source) {
                if (rule.ForEach is null) { throw Refuse("neighbors requires forEach body keys"); }
                if (source.Range < 1m / 65536m || source.Range > 1_000_000m ||
                    source.HalfAngleDegrees <= 0 || source.HalfAngleDegrees > 180 ||
                    source.CandidateBudget < 1 || source.CandidateBudget > WorldBodiesLimits.CapacityCeiling ||
                    source.MaxCandidates < 1 || source.MaxCandidates > WorldRuleCapacity.MaxDecisionCandidates ||
                    source.MaxCandidates > source.CandidateBudget) {
                    throw Refuse("neighbors requires range [1/65536,1000000], halfAngleDegrees (0,180], and 1 <= maxCandidates <= min(32,candidateBudget), with candidateBudget within body capacity ceiling");
                }
                var range = FixedQ4816.FromRawBits(LiteralToRaw(CellKind.Fixed, source.Range, rule.Name, "decision neighbors range"));
                // Convert from the library's high-precision pi at the authoring boundary; no binary floating point.
                var radians = source.HalfAngleDegrees * FixedQ4816.PiQ61 / (180m * (1UL << FixedQ4816.PiQ61FractionBitCount));
                var angle = FixedQ4816.FromRawBits(LiteralToRaw(CellKind.Fixed, radians, rule.Name, "decision neighbors angle"));
                neighbors = new(source, range, FixedQ4816.Cos(angle));
            }
            var scope = s_bindingScope;
            if (neighbors is not null) { s_bindingScope = [RuleBinding.Each, RuleBinding.Left, RuleBinding.Right]; }
            try {
                options[index] = new(option.Name, Gate(option.Gate),
                    CompileExpression(option.Score, decision.ScoreKind, rule.Name, "decision score", definition),
                    CompileDecisionEffects(option.Effects, rule.Name, definition), neighbors);
            } finally { s_bindingScope = scope; }
        }
        return new(options, decision.Mode, decision.ScoreKind, period, commitment, bonus, decision.Seed,
            decision.Interrupt is null ? null : Gate(decision.Interrupt),
            CompileDecisionEffects(decision.OnNoChoice ?? [], rule.Name, definition),
            JsonSerializer.Serialize(rule, WorldJsonContext.Default.WorldRule));
    }
}

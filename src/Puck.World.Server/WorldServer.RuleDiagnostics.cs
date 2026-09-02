namespace Puck.World.Server;

/// <summary>One bounded runtime rule-refusal counter and its most recent occurrence.</summary>
/// <param name="Refusal">The stable refusal category.</param>
/// <param name="Count">How many occurrences the running server has observed.</param>
/// <param name="LastTick">The latest simulation tick that observed it.</param>
/// <param name="Rule">The latest rule or interaction name.</param>
/// <param name="Effect">The latest compiled effect description.</param>
/// <param name="Detail">The latest concrete runtime reason.</param>
public readonly record struct WorldRuleRuntimeDiagnostic(WorldRuleEffectRefusal Refusal, ulong Count, ulong LastTick, string Rule, string Effect, string Detail);

public sealed partial class WorldServer {
    private readonly ulong[] m_ruleRefusalCounts = new ulong[Enum.GetValues<WorldRuleEffectRefusal>().Length];
    private readonly WorldRuleRuntimeDiagnostic?[] m_ruleRefusalLatest = new WorldRuleRuntimeDiagnostic?[Enum.GetValues<WorldRuleEffectRefusal>().Length];

    /// <summary>Returns the fixed-size refusal summary; only categories that have occurred are included.</summary>
    public IReadOnlyList<WorldRuleRuntimeDiagnostic> RuleRuntimeDiagnostics() {
        var result = new List<WorldRuleRuntimeDiagnostic>(capacity: m_ruleRefusalLatest.Length);
        foreach (var diagnostic in m_ruleRefusalLatest) {
            if (diagnostic is { } value) {
                result.Add(item: value);
            }
        }
        return result;
    }

    /// <summary>Formats the runtime refusal summary for <c>world.rule.failures</c>.</summary>
    public string DescribeRuleRuntimeDiagnostics() {
        var diagnostics = RuleRuntimeDiagnostics();
        if (diagnostics.Count == 0) {
            return "[world.rule.failures: none]";
        }

        return $"[world.rule.failures: {string.Join(
            separator: " | ",
            values: diagnostics.Select(static value => $"{value.Refusal} count={value.Count} lastTick={value.LastTick} rule='{value.Rule}' effect='{value.Effect}' ({value.Detail})")
        )}]";
    }

    private void ReportRuleEffectRefusal(WorldRuleEffectRefusal refusal, string ruleName, in CompiledWorldEffect effect, ulong tick, string detail) {
        var index = (int)refusal;
        var count = m_ruleRefusalCounts[index];
        count = ((count == ulong.MaxValue) ? count : (count + 1UL));
        m_ruleRefusalCounts[index] = count;
        m_ruleRefusalLatest[index] = new WorldRuleRuntimeDiagnostic(
            Refusal: refusal,
            Count: count,
            LastTick: tick,
            Rule: ruleName,
            Effect: effect.Describe,
            Detail: detail
        );

        // One line per category per server lifetime. The structured counter remains exact without a Level rule
        // turning an expected live refusal into an unbounded stderr stream.
        if (count == 1UL) {
            Console.Error.WriteLine(value: $"[world.rule: effect refused ({refusal}) — rule '{ruleName}', '{effect.Describe}': {detail}; world.rule.failures carries the running count]");
        }
    }
}

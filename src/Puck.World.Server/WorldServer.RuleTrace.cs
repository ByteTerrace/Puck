namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Arms a capture of the next evaluations of one rule or interaction, replacing any earlier capture.</summary>
    /// <param name="rule">The rule or interaction name.</param>
    /// <param name="evaluations">How many evaluations to capture, 1..<see cref="RuleEvaluator.MaxTraceEvaluations"/>.</param>
    /// <param name="refusal">Why arming was refused, or empty.</param>
    /// <returns><see langword="true"/> when armed.</returns>
    public bool TryArmRuleTrace(string rule, int evaluations, out string refusal) {
        if ((evaluations < 1) || (evaluations > RuleEvaluator.MaxTraceEvaluations)) {
            refusal = $"[world.rule.trace: evaluations must be 1..{RuleEvaluator.MaxTraceEvaluations}]";
            return false;
        }
        var compiled = (FindCompiledRule(rules: m_rules, name: rule) ?? FindCompiledRule(rules: m_interactions, name: rule));
        if (compiled is null) {
            refusal = $"[world.rule.trace: no rule or interaction named '{rule}' — world.rules and world.interactions list them]";
            return false;
        }
        if (compiled.Decision is not null) {
            refusal = $"[world.rule.trace: '{rule}' is a decision rule; world.decisions echoes its choices and timers]";
            return false;
        }
        _ = m_evaluator.ArmTrace(rule: rule, evaluations: evaluations);
        refusal = string.Empty;
        return true;
    }

    /// <summary>Disarms the capture and discards what it captured.</summary>
    /// <returns><see langword="true"/> when a capture was armed.</returns>
    public bool DisarmRuleTrace() => m_evaluator.DisarmTrace();

    /// <summary>Formats the capture for <c>world.rule.trace</c>: a header line, then one line per evaluation.</summary>
    public string DescribeRuleTrace() =>
        (m_evaluator.DescribeTrace(verb: "world.rule.trace") ?? "[world.rule.trace: none armed — world.rule.trace <rule> [evaluations] arms one]");

    private static CompiledWorldRule? FindCompiledRule(CompiledWorldRule[] rules, string name) {
        foreach (var rule in rules) {
            if (string.Equals(a: rule.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                return rule;
            }
        }
        return null;
    }
}

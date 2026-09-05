namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Returns the evaluator's refusal ledger; only categories that have occurred are included.</summary>
    public IReadOnlyList<RuleRuntimeDiagnostic> RuleRuntimeDiagnostics() => m_evaluator.Diagnostics();

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
}

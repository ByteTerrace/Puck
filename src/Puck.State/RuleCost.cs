using Puck.Maths;

namespace Puck.State;

/// <summary>Heuristic work-unit components of a rule: per-rule setup, per-evaluation check, and per-firing effects.
/// These counts are not calibrated reference cycles.</summary>
/// <param name="Setup">The setup cost incurred once per rule invocation.</param>
/// <param name="Check">The check cost incurred per candidate evaluation (bindings, gate).</param>
/// <param name="Effects">The effect cost incurred only when the rule fires.</param>
public readonly record struct RuleCost(long Setup, long Check, long Effects) {
    /// <summary>The total cost of one evaluation and firing in isolation: Setup + Check + Effects, saturating.</summary>
    public long Total => RuleWorkBudget.SaturatingAdd(left: RuleWorkBudget.SaturatingAdd(left: Setup, right: Check), right: Effects);

    /// <summary>Reports the unresolved conversion from heuristic work units to reference cycles.</summary>
    public CostBound ToBound() => CostBound.Unmodeled("Heuristic rule work has no calibrated reference-cycle conversion.");
}

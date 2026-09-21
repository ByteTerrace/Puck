using Puck.Maths;

namespace Puck.State;

/// <summary>Portable semantic operation pricing, read from the reference schedule's one owning evidence manifest.</summary>
public static class ReferenceSchedule {
    private static readonly Dictionary<string, CostCoefficient> Expressions = Load();

    private static Dictionary<string, CostCoefficient> Load() {
        var coefficients = new Dictionary<string, CostCoefficient>(comparer: StringComparer.Ordinal);

        foreach (var coefficient in ReferenceScheduleManifest.Coefficients) {
            if (!string.Equals(
                a: coefficient.Vocabulary,
                b: "expression",
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }
            if (!coefficients.TryAdd(
                key: coefficient.Operation,
                value: coefficient
            )) {
                throw new InvalidOperationException(message: $"The reference-schedule manifest registers '{coefficient.Operation}' more than once.");
            }
        }
        return coefficients;
    }

    /// <summary>Returns the semantic reference-cycle price of one expression operation, or an unresolved bound when
    /// the manifest records no evidence for it. A registered heuristic weight is never an answer here.</summary>
    /// <param name="operation">The semantic operation, not a host instruction mnemonic.</param>
    /// <param name="kind">The numeric kind of the expression.</param>
    public static CostBound OperationCostBound(ExpressionOp operation, CellKind kind = CellKind.Int) => ((Expressions.TryGetValue(
        key: operation.ToString(),
        value: out var coefficient
    ) && coefficient.NumericKinds.Contains(
        value: kind.ToString(),
        comparer: StringComparer.Ordinal
    ))
        ? coefficient.Bound
        : CostBound.Unmodeled(reason: $"ExpressionOp.{operation} ({kind}) is not a registered operation of the reference schedule."));
}

using Puck.Maths;

namespace Puck.State;

/// <summary>One registered vocabulary's pricing coverage. <paramref name="Unmodeled"/> names the operations a
/// document can still reach without a reference-cycle price, which is what withholds certification of the scoped
/// deadline; an empty list is the certifiable state.</summary>
/// <param name="Vocabulary">The registered vocabulary.</param>
/// <param name="Registered">How many operations it registers.</param>
/// <param name="Priced">How many of them carry a reference-cycle price.</param>
/// <param name="Unmodeled">The operations carrying an explicit unmodeled reason instead of a price.</param>
public sealed record CostCoverage(string Vocabulary, int Registered, int Priced, IReadOnlyList<string> Unmodeled);
/// <summary>Portable semantic operation pricing, read from the reference schedule's one owning evidence manifest.</summary>
public static class ReferenceSchedule {
    private static readonly Dictionary<string, CostCoefficient> Expressions = Load();

    /// <summary>Gets each registered vocabulary's pricing coverage, ordered by vocabulary. A reachable operation
    /// absent from the manifest is absent here too: completeness of the registry is the vocabulary laws' subject,
    /// not this summary's.</summary>
    public static IReadOnlyList<CostCoverage> Coverage { get; } = [.. ReferenceScheduleManifest.Coefficients
        .GroupBy(keySelector: coefficient => coefficient.Vocabulary, comparer: StringComparer.Ordinal)
        .OrderBy(keySelector: group => group.Key, comparer: StringComparer.Ordinal)
        .Select(selector: group => new CostCoverage(
            Priced: group.Count(predicate: coefficient => coefficient.Bound.IsKnown),
            Registered: group.Count(),
            Unmodeled: [.. group.Where(predicate: coefficient => !coefficient.Bound.IsKnown).Select(selector: coefficient => coefficient.Operation)],
            Vocabulary: group.Key
        ))];

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

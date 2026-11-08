namespace Puck.Analyzers;

/// <summary>
/// The one list of members that may discard a rule outcome (<see cref="OutcomeAnalyzer"/>, OUTCOME001): a top-level host
/// call that evaluates a whole family and answers only whether the arena moved. Each entry states why nothing is left
/// to decide from the answer it drops.
/// </summary>
public static class OutcomeDiscardAllowlist {
    /// <summary>Gets every allowlisted member.</summary>
    public static IReadOnlyList<OutcomeDiscardEntry> Entries { get; } = [
        new(
            Assembly: "Puck.State.Rules",
            Type: "RuleEvaluator",
            Member: "Evaluate",
            Reason: "The host entry answers whether any firing moved the arena. A fault the aggregate carries has already closed its gate or refused its firing, held its latch and been counted as a refusal, and nothing above a family reads it; the overload with an out parameter answers the aggregate to a group."
        ),
        new(
            Assembly: "Puck.State.Rules",
            Type: "RuleEvaluator",
            Member: "EvaluateRule",
            Reason: "The host entry answers whether the rule's firings moved the arena, on the same terms as Evaluate."
        ),
    ];

    /// <summary>Returns whether a member may drop a rule outcome.</summary>
    /// <param name="assembly">The compiling assembly's name.</param>
    /// <param name="type">The simple name of the type declaring the member.</param>
    /// <param name="member">The member's name.</param>
    /// <returns><see langword="true"/> when an entry names the assembly, the type and the member.</returns>
    public static bool Admits(string assembly, string type, string member) {
        foreach (var entry in Entries) {
            if (
                string.Equals(
                    a: entry.Assembly,
                    b: assembly,
                    comparisonType: StringComparison.Ordinal
                ) &&
                string.Equals(
                    a: entry.Type,
                    b: type,
                    comparisonType: StringComparison.Ordinal
                ) &&
                string.Equals(
                    a: entry.Member,
                    b: member,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                return true;
            }
        }

        return false;
    }
}
/// <summary>One member allowed to drop a rule outcome.</summary>
/// <param name="Assembly">The assembly that compiles the member.</param>
/// <param name="Type">The simple name of the type declaring the member.</param>
/// <param name="Member">The member's name.</param>
/// <param name="Reason">Why nothing is left to decide from the answer the member drops.</param>
public sealed record OutcomeDiscardEntry(string Assembly, string Type, string Member, string Reason);

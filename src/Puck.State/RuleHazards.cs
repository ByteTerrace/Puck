namespace Puck.State;

/// <summary>The two orderings the document sequence decides silently.</summary>
public enum RuleHazardKind : byte {
    /// <summary>An earlier rule reads a cell a later rule writes, so the reader sees the previous tick's value.</summary>
    WriteAfterRead,
    /// <summary>Two rules write one cell in the same tick and at least one replaces it, so the later one wins.</summary>
    WriteAfterWrite,
}

/// <summary>One document-order hazard between two rules.</summary>
/// <param name="Kind">Which ordering the sequence decides.</param>
/// <param name="First">The earlier rule in document order.</param>
/// <param name="Second">The later rule.</param>
/// <param name="Cell">The cell they meet on, as <c>row.key</c> or <c>row.*</c>.</param>
/// <param name="Detail">What the order means for the author.</param>
public readonly record struct RuleHazard(RuleHazardKind Kind, string First, string Second, string Cell, string Detail);

/// <summary>Finds the read-after-later-write and write-after-write pairs the rules' document order decides. A pair
/// whose gates pin one literal cell to disjoint ranges is skipped: it never fires on one tick, so its order decides
/// nothing. Rules run once per tick in document order with effects applying immediately, which is what makes each
/// pairing a fact rather than a scheduling question.</summary>
public static class RuleHazards {
    /// <summary>Analyzes compiled rules in document order.</summary>
    /// <param name="rules">The rules, in document order.</param>
    /// <returns>Every hazard, earliest pair first.</returns>
    public static IReadOnlyList<RuleHazard> Analyze(IReadOnlyList<CompiledRule> rules) {
        ArgumentNullException.ThrowIfNull(argument: rules);

        var reads = new IReadOnlyList<RuleAccess>[rules.Count];
        var writes = new IReadOnlyList<RuleAccess>[rules.Count];
        var pinned = new IReadOnlyList<RulePinnedCell>[rules.Count];

        for (var index = 0; index < rules.Count; index++) {
            reads[index] = RuleDataflow.Reads(rule: rules[index]);
            writes[index] = RuleDataflow.Writes(rule: rules[index]);
            pinned[index] = RuleWorkBudget.PinnedCells(gate: rules[index].Gate, contradictory: out _);
        }

        var hazards = new List<RuleHazard>();
        var seen = new HashSet<(int, int, RuleHazardKind, string)>();

        for (var first = 0; first < rules.Count; first++) {
            for (var second = first + 1; second < rules.Count; second++) {
                if (Exclusive(left: pinned[first], right: pinned[second])) {
                    continue;
                }
                foreach (var write in writes[second]) {
                    foreach (var read in reads[first]) {
                        if (read.Overlaps(other: write) && seen.Add(item: (first, second, RuleHazardKind.WriteAfterRead, read.Describe()))) {
                            hazards.Add(item: new RuleHazard(
                                Kind: RuleHazardKind.WriteAfterRead,
                                First: rules[first].Name,
                                Second: rules[second].Name,
                                Cell: read.Describe(),
                                Detail: $"'{rules[first].Name}' reads {read.Describe()} before '{rules[second].Name}' writes it, so it sees the previous tick's value; declare '{rules[second].Name}' first if the read should see the new one"
                            ));
                        }
                    }
                    foreach (var earlier in writes[first]) {
                        if (earlier.Overlaps(other: write) && (earlier.IsSet || write.IsSet) && seen.Add(item: (first, second, RuleHazardKind.WriteAfterWrite, write.Describe()))) {
                            hazards.Add(item: new RuleHazard(
                                Kind: RuleHazardKind.WriteAfterWrite,
                                First: rules[first].Name,
                                Second: rules[second].Name,
                                Cell: write.Describe(),
                                Detail: (earlier.IsSet && write.IsSet)
                                    ? $"'{rules[first].Name}' and '{rules[second].Name}' both set {write.Describe()} in one tick; '{rules[second].Name}' wins"
                                    : (write.IsSet
                                        ? $"'{rules[second].Name}' sets {write.Describe()} after '{rules[first].Name}' adds to it; the add is discarded"
                                        : $"'{rules[second].Name}' adds to {write.Describe()} after '{rules[first].Name}' sets it; the add lands on the new value")
                            ));
                        }
                    }
                }
            }
        }

        return hazards;
    }

    private static bool Exclusive(IReadOnlyList<RulePinnedCell> left, IReadOnlyList<RulePinnedCell> right) {
        foreach (var a in left) {
            foreach (var b in right) {
                if (string.Equals(a: a.Cell, b: b.Cell, comparisonType: StringComparison.Ordinal) && a.Disjoint(other: b)) {
                    return true;
                }
            }
        }

        return false;
    }
}

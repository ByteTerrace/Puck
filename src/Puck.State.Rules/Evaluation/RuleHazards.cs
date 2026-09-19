namespace Puck.State.Rules;

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
/// nothing.</summary>
public static class RuleHazards {
    private static string Describe(StateCatalog catalog, CellAccess access) {
        var row = ((((uint)access.RowOrdinal) < ((uint)catalog.Descriptors.Count))
            ? catalog.Descriptors[access.RowOrdinal].Name
            : access.RowOrdinal.ToString(provider: System.Globalization.CultureInfo.InvariantCulture)
        );

        return (catalog.Keys.TryGetName(
            key: access.Key,
            name: out var key
        )
            ? $"{row}.{key.Value}"
            : $"{row}.*"
        );
    }
    private static bool Exclusive(IReadOnlyList<RulePinnedCell> left, IReadOnlyList<RulePinnedCell> right) {
        foreach (var a in left) {
            foreach (var b in right) {
                if (
                    (a.RowOrdinal == b.RowOrdinal) &&
                    (a.Key == b.Key) &&
                    a.Disjoint(other: b)
                ) {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Analyzes compiled rules in document order.</summary>
    /// <param name="rules">The rules, in document order.</param>
    /// <param name="catalog">The catalog the ordinals and keys were minted against.</param>
    /// <returns>Every hazard, earliest pair first.</returns>
    public static IReadOnlyList<RuleHazard> Analyze(IReadOnlyList<CompiledRule> rules, StateCatalog catalog) {
        ArgumentNullException.ThrowIfNull(argument: catalog);
        ArgumentNullException.ThrowIfNull(argument: rules);

        var pinned = new IReadOnlyList<RulePinnedCell>[rules.Count];
        var reads = new List<CellAccess>[rules.Count];
        var writes = new List<CellAccess>[rules.Count];

        for (var index = 0; (index < rules.Count); index++) {
            pinned[index] = RuleWorkBudget.PinnedCells(
                contradictory: out _,
                gate: rules[index].Gate
            );
            reads[index] = RuleDataflow.Reads(rule: rules[index]);
            writes[index] = RuleDataflow.Writes(rule: rules[index]);
        }

        var hazards = new List<RuleHazard>();
        var seen = new HashSet<(int, int, RuleHazardKind, string)>();

        for (var first = 0; (first < rules.Count); first++) {
            for (var second = (first + 1); (second < rules.Count); second++) {
                if (Exclusive(
                    left: pinned[first],
                    right: pinned[second]
                )) {
                    continue;
                }

                foreach (var write in writes[second]) {
                    foreach (var read in reads[first]) {
                        var cell = Describe(
                            access: read,
                            catalog: catalog
                        );

                        if (
                            read.Overlaps(other: write) &&
                            seen.Add(item: (first, second, RuleHazardKind.WriteAfterRead, cell))
                        ) {
                            hazards.Add(item: new RuleHazard(
                                Cell: cell,
                                Detail: $"'{rules[first].Name}' reads {cell} before '{rules[second].Name}' writes it, so it sees the previous tick's value; declare '{rules[second].Name}' first if the read should see the new one",
                                First: rules[first].Name,
                                Kind: RuleHazardKind.WriteAfterRead,
                                Second: rules[second].Name
                            ));
                        }
                    }

                    foreach (var earlier in writes[first]) {
                        var cell = Describe(
                            access: write,
                            catalog: catalog
                        );

                        if (
                            earlier.Overlaps(other: write) &&
                            (earlier.IsSet || write.IsSet) &&
                            seen.Add(item: (first, second, RuleHazardKind.WriteAfterWrite, cell))
                        ) {
                            hazards.Add(item: new RuleHazard(
                                Cell: cell,
                                Detail: ((earlier.IsSet && write.IsSet)
                                ? $"'{rules[first].Name}' and '{rules[second].Name}' both set {cell} in one tick; '{rules[second].Name}' wins"
                                : (write.IsSet
                                    ? $"'{rules[second].Name}' sets {cell} after '{rules[first].Name}' adds to it; the add is discarded"
                                    : $"'{rules[second].Name}' adds to {cell} after '{rules[first].Name}' sets it; the add lands on the new value")),
                                First: rules[first].Name,
                                Kind: RuleHazardKind.WriteAfterWrite,
                                Second: rules[second].Name
                            ));
                        }
                    }
                }
            }
        }

        return hazards;
    }
}

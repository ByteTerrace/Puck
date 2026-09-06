namespace Puck.World;

/// <summary>The one pass a section's read-back verb (<c>world.curves</c>, <c>world.dynamics</c>) takes over the whole
/// document to count who references each of its rows, grouped by the referenced row name — every row's census then
/// looks itself up rather than each re-walking the document. The bucket record is the verb's own; this only folds
/// each reference's section into it.</summary>
internal static class WorldReferenceCensus {
    /// <summary>Counts references per row name.</summary>
    /// <typeparam name="TCounts">The verb's per-section bucket record.</typeparam>
    /// <param name="references">Every (section, path, row name) reference the document holds.</param>
    /// <param name="increment">Folds one reference's section into a bucket.</param>
    public static Dictionary<string, TCounts> CountByRow<TCounts>(IEnumerable<(string Section, string Path, string RowName)> references, Func<TCounts, string, TCounts> increment) where TCounts : struct {
        var counts = new Dictionary<string, TCounts>(comparer: StringComparer.Ordinal);

        foreach (var reference in references) {
            counts[reference.RowName] = increment(
                (counts.TryGetValue(
                    key: reference.RowName,
                    value: out var existing
                )
                    ? existing
                    : default
                ),
                reference.Section
            );
        }

        return counts;
    }
}

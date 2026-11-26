namespace Puck.Abstractions.Collections;

/// <summary>Reads lists of names without allocating: the scan a hot path or a validator runs over a published
/// <see cref="IReadOnlyList{T}"/> of strings, where LINQ's <c>Contains</c> or <c>IndexOf</c> would box the list's
/// enumerator.</summary>
public static class StringLists {
    /// <summary>Returns the position of the first entry of <paramref name="items"/> that equals <paramref name="value"/>
    /// under <paramref name="comparison"/>.</summary>
    /// <param name="items">The list to scan.</param>
    /// <param name="value">The name to find, or <see langword="null"/>, which only a <see langword="null"/> entry
    /// matches.</param>
    /// <param name="comparison">The comparison each entry is tested with; ordinal by default.</param>
    /// <returns>The zero-based position, or -1 when no entry equals <paramref name="value"/>.</returns>
    public static int IndexOf(IReadOnlyList<string> items, string? value, StringComparison comparison = StringComparison.Ordinal) {
        for (var index = 0; (index < items.Count); index++) {
            if (string.Equals(
                a: items[index],
                b: value,
                comparisonType: comparison
            )) {
                return index;
            }
        }

        return -1;
    }
    /// <summary>Determines whether <paramref name="items"/> holds <paramref name="value"/> under
    /// <paramref name="comparison"/>.</summary>
    /// <param name="items">The list to scan.</param>
    /// <param name="value">The name to find, or <see langword="null"/>, which only a <see langword="null"/> entry
    /// matches.</param>
    /// <param name="comparison">The comparison each entry is tested with; ordinal by default.</param>
    /// <returns><see langword="true"/> when an entry equals <paramref name="value"/>.</returns>
    public static bool Holds(IReadOnlyList<string> items, string? value, StringComparison comparison = StringComparison.Ordinal) =>
        (IndexOf(comparison: comparison, items: items, value: value) >= 0);
}

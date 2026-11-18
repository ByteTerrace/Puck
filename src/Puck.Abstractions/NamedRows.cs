namespace Puck.Abstractions;

/// <summary>Ordinal name lookup over immutable declaration lists.</summary>
public static class NamedRows {
    /// <summary>Returns the first non-null row with the exact name, or null. A static selector keeps a hot
    /// lookup allocation-free; callers decide whether the matching declaration is admissible.</summary>
    public static T? Find<T>(IReadOnlyList<T>? rows, string name, Func<T, string> selector) where T : class {
        if (rows is null) { return null; }
        for (var index = 0; index < rows.Count; index++) {
            var row = rows[index];
            if (row is not null && string.Equals(selector(row), name, StringComparison.Ordinal)) { return row; }
        }
        return null;
    }
}

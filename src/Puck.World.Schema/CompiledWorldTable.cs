namespace Puck.World;

/// <summary>A loaded table: keys sorted ascending, one value column per declared column (a single-value table has
/// one unnamed column), values in the row's raw cell encoding, read by binary search.</summary>
public sealed class CompiledWorldTable {
    private readonly long[] m_keys;
    private readonly long[][] m_columns;
    private readonly string[] m_columnNames;

    private CompiledWorldTable(string name, CellKind kind, long[] keys, long[][] columns, string[] columnNames) {
        Name = name;
        Kind = kind;
        m_keys = keys;
        m_columns = columns;
        m_columnNames = columnNames;
    }

    /// <summary>Gets the declared column names; empty for a single-value table.</summary>
    public IReadOnlyList<string> ColumnNames => m_columnNames;

    /// <summary>Finds a declared column.</summary>
    /// <param name="name">The column name.</param>
    /// <returns>The column index, or -1.</returns>
    public int Column(string name) => Array.IndexOf(array: m_columnNames, value: name);

    /// <summary>Gets the table's authored name.</summary>
    public string Name { get; }
    /// <summary>Gets the kind every value is encoded in.</summary>
    public CellKind Kind { get; }
    /// <summary>Gets the entry count.</summary>
    public int Count => m_keys.Length;

    /// <summary>Looks a key up in one column.</summary>
    /// <param name="key">The integer key.</param>
    /// <param name="column">The column index; 0 for a single-value table.</param>
    /// <param name="raw">The value in the table's raw encoding, when found.</param>
    /// <returns>Whether the key is present.</returns>
    public bool TryLookup(long key, int column, out long raw) {
        var index = Array.BinarySearch(array: m_keys, value: key);
        if (index < 0) {
            raw = 0L;
            return false;
        }
        raw = m_columns[column][index];
        return true;
    }

    /// <summary>Loads and compiles a table row's document.</summary>
    /// <param name="row">The reference row.</param>
    /// <param name="table">The compiled table, when this method returns <see langword="true"/>.</param>
    /// <param name="error">The failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryCompile(TableRow row, out CompiledWorldTable? table, out string? error) {
        table = null;
        if (!WorldAssetRowLoader.TryLoadTable(row: row, document: out var document, error: out error)) {
            return false;
        }
        var violations = TableCanonicalizer.Validate(document: document!);
        if (violations.Count > 0) {
            error = $"{violations[0].Path}: {violations[0].Message}";
            return false;
        }
        var normalized = TableCanonicalizer.Normalize(document: document!);
        var isFixed = string.Equals(a: normalized.Kind, b: TableDocument.FixedKind, comparisonType: StringComparison.Ordinal);
        var columnNames = (normalized.Columns ?? []).ToArray();
        var columnCount = Math.Max(columnNames.Length, 1);
        var keys = new long[normalized.Entries.Count];
        var columns = new long[columnCount][];
        for (var column = 0; column < columnCount; column++) {
            columns[column] = new long[keys.Length];
        }
        for (var index = 0; index < keys.Length; index++) {
            var entry = normalized.Entries[index];
            keys[index] = entry.Key;
            for (var column = 0; column < columnCount; column++) {
                var value = ((columnNames.Length == 0) ? entry.Value!.Value : entry.Values![column]);
                if (isFixed) {
                    if (!NumericLiteral.TryToFixed(value: value, result: out var fixedValue)) {
                        error = $"entries[{index}] value {value} is not representable in Q48.16.";
                        return false;
                    }
                    columns[column][index] = fixedValue.Value;
                } else {
                    columns[column][index] = (long)value;
                }
            }
        }
        table = new CompiledWorldTable(name: row.Name, kind: isFixed ? CellKind.Fixed : CellKind.Int, keys: keys, columns: columns, columnNames: columnNames);
        error = null;
        return true;
    }
}

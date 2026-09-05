namespace Puck.World;

/// <summary>Loads a document's <c>tables</c> row off disk and compiles it — the document-anchored entrance to
/// <see cref="CompiledTable"/>.</summary>
public static class WorldTables {
    /// <summary>Loads and compiles a table row's document.</summary>
    /// <param name="row">The reference row.</param>
    /// <param name="table">The compiled table, when this method returns <see langword="true"/>.</param>
    /// <param name="error">The failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryCompile(TableRow row, out CompiledTable? table, out string? error) {
        ArgumentNullException.ThrowIfNull(argument: row);

        table = null;
        if (!WorldAssetRowLoader.TryLoadTable(row: row, document: out var document, error: out error)) {
            return false;
        }

        return CompiledTable.TryCompile(name: row.Name, document: document!, table: out table, error: out error);
    }
}

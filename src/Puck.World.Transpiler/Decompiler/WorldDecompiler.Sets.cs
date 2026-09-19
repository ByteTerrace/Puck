using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.State;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Decompiler;

// A `sets` row prints as `set <name>: <expression>` only when the algebra can read the row back and print it. A row
// the algebra refuses, or one carrying a key the declaration has no place for, sends the whole section through the
// generic value path, which carries every row unchanged.
public static partial class WorldDecompiler {
    private static readonly JsonTypeInfo<CellSetExpression> CellSetTypeInfo =
        ((JsonTypeInfo<CellSetExpression>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(CellSetExpression)));
    // The keys a `set` declaration can carry are its construct's own description.
    private static readonly HashSet<string> SetNodeKeys = WorldConstructs.NodeKeysOf(enclosing: null, keyword: "set");

    private static bool CanSugarCellSets(JsonArray sets) {
        foreach (var item in sets) {
            if (
                (item is not JsonObject row) ||
                !row.All(predicate: pair => SetNodeKeys.Contains(item: pair.Key)) ||
                (row.Count != SetNodeKeys.Count) ||
                (row["name"]?.ToString() is not { Length: > 0 } name) ||
                !IsSpellableName(name: name) ||
                (row["set"] is null) ||
                (TryPrintCellSet(row: row) is null)
            ) {
                return false;
            }
        }

        return true;
    }
    private static void DecompileCellSetsBlock(StringBuilder sb, JsonArray sets, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        foreach (var item in sets) {
            if (item is not JsonObject row) {
                continue;
            }

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}set {QuotedName(name: (row["name"]?.ToString() ?? string.Empty))}: {TryPrintCellSet(row: row)}"
            );
        }
    }
    private static string? TryPrintCellSet(JsonObject row) {
        CellSetExpression? expression;

        try {
            expression = row["set"].Deserialize(jsonTypeInfo: CellSetTypeInfo);
        } catch (JsonException) {
            return null;
        }

        return (CellSetSpelling.TryPrint(
            expression: expression,
            text: out var text
        )
            ? text
            : null);
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lowering;

namespace Puck.World.Transpiler.Decompiler;

// `shape Type "name" { }` inverse (§4.1) — the renamed successor to the old dead `solid`/`"solids"` collector,
// targeting the real `CreationDocument.Shapes` wire key. Elides a field only on an exact match against
// `CreationCanonicalizer.Normalize`'s own defaults, mirroring the emitter's `LowerShapeBlock`.
public static partial class WorldDecompiler {
    // Whether every row in a `shapes` array is a whole shape the `shape Type "name" { }` grammar can carry — one
    // with its own `type` — rather than a basis-merge patch (an `{ id, ... }` row naming only the facets it
    // overrides on a base document's shape). A row with no `type` prints through the generic value path instead of
    // guessing one from its `name`.
    private static bool CanSugarShapes(JsonArray shapes) {
        foreach (var item in shapes) {
            if (item is not JsonObject shape || shape["type"] is not JsonValue typeVal || !typeVal.TryGetValue<string>(out var type) || (type.Length == 0)) {
                return false;
            }
        }
        return true;
    }

    private static void DecompileShapesBlock(StringBuilder sb, JsonArray shapes, int indentLevel) {
        var first = true;
        var index = 0;
        foreach (var item in shapes) {
            if (item is JsonObject shapeObj) {
                if (!first) {
                    sb.AppendLine();
                }
                first = false;
                AppendShapeBlock(sb, shapeObj, index, indentLevel);
            }
            index++;
        }
    }

    private static void AppendShapeBlock(StringBuilder sb, JsonObject shape, int index, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        var inner = new string(' ', (indentLevel + 1) * 4);
        var type = shape["type"]?.ToString() ?? "";
        var name = shape["name"]?.ToString();
        // An absent `name` and an explicit `"name": null` both read back `null` through the indexer; only the
        // latter carries a key to preserve, as an ordinary `name: null` property alongside the unnamed header form.
        var hasExplicitNullName = (name is null) && shape.ContainsKey("name");

        if (name is not null) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}shape {type} \"{EscapeString(name)}\" {{");
        } else {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}shape {type} {{");
        }

        var elide = new HashSet<string>(StringComparer.Ordinal) { "type" };
        if (!hasExplicitNullName) {
            elide.Add("name");
        }

        foreach (var key in WorldDocumentRowDefaults.ShapeKeys) {
            if (WorldDocumentRowDefaults.IsDefaultValue(key, shape[key], index, shape: true)) {
                elide.Add(key);
            }
        }

        foreach (var (k, v) in shape) {
            if (elide.Contains(k)) {
                continue;
            }
            if (v is null) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}{k}: null");
                continue;
            }
            if (v is JsonValue numeric && UnitSuffixFor(k, numeric) is { } unit) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}{k}: {FormatValue(numeric, indentLevel + 1)}{unit}");
                continue;
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}{k}: {FormatValue(v, indentLevel + 1)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

}

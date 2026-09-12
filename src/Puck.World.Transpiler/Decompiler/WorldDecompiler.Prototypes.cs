using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.World.Transpiler.Decompiler;

// `prototypes { prototype "id" { document { ... } } }` inverse, mirroring `LowerPrototypesBlock`/`LowerPrototypeRow`:
// `id` maps back to the block's quoted name (`WorldPrototype.Id`, the same way `WorldPlacement.Id` maps for a
// `placement` row), and `document` prints through `DecompileNamedBlock`, which routes its own `shapes` key through
// the same `shape Type "name" { }` sugar a root-level creation document uses.
public static partial class WorldDecompiler {
    // Whether every row in a `prototypes` array is one the `prototype "id" { }` grammar can carry. A row with no
    // `id` prints through the generic value path instead of guessing one, and so does a row whose `document` key is
    // present but not an object: `AppendPrototypeRow` has a spelling only for a nested block, and the residual
    // property loop skips the key outright.
    private static bool CanSugarPrototypes(JsonArray prototypes) {
        foreach (var item in prototypes) {
            if (item is not JsonObject proto || proto["id"] is not JsonValue idVal || !idVal.TryGetValue<string>(out var id) || (id.Length == 0)) {
                return false;
            }
            if (proto.ContainsKey("document") && proto["document"] is not JsonObject) {
                return false;
            }
        }
        return true;
    }

    private static void DecompilePrototypesBlock(StringBuilder sb, JsonArray prototypes, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}prototypes {{");

        var first = true;
        foreach (var item in prototypes) {
            if (item is not JsonObject proto) {
                continue;
            }
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            AppendPrototypeRow(sb, proto, indentLevel + 1);
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    private static void AppendPrototypeRow(StringBuilder sb, JsonObject proto, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        var inner = indentLevel + 1;
        var innerIndent = new string(' ', inner * 4);
        var id = proto["id"]?.ToString() ?? "";
        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}prototype \"{EscapeString(id)}\" {{");

        if (proto["document"] is JsonObject documentObj) {
            DecompileNamedBlock(sb, "document", null, documentObj, inner);
        }

        foreach (var (k, v) in proto) {
            if (k is "id" or "document") {
                continue;
            }
            if (v is null) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{innerIndent}{k}: null");
                continue;
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{innerIndent}{k}: {FormatValue(v, inner)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }
}

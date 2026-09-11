using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.World.Transpiler.Decompiler;

// `shape Type "name" { }` inverse (§4.1) — the renamed successor to the old dead `solid`/`"solids"` collector,
// targeting the real `CreationDocument.Shapes` wire key. Elides a field only on an exact match against
// `CreationCanonicalizer.Normalize`'s own defaults, mirroring the emitter's `LowerShapeBlock`.
public static partial class WorldDecompiler {
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

        if (name is not null) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}shape {type} \"{EscapeString(name)}\" {{");
        } else {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}shape {type} {{");
        }

        var elide = new HashSet<string>(StringComparer.Ordinal) { "type", "name" };

        if (shape["id"] is JsonValue idVal && IsNumberEqualTo(idVal, index)) {
            elide.Add("id");
        }
        if (shape["blend"] is JsonValue blendVal && string.Equals(blendVal.ToString(), "Union", StringComparison.Ordinal)) {
            elide.Add("blend");
        }
        if (shape["smooth"] is JsonValue smoothVal && IsNumberEqualTo(smoothVal, 0.0)) {
            elide.Add("smooth");
        }
        if (shape["rotation"] is JsonArray rotationArr && IsIdentityRotation(rotationArr)) {
            elide.Add("rotation");
        }
        if (shape["scale"] is JsonArray scaleArr && IsUnitVector(scaleArr)) {
            elide.Add("scale");
        }
        if (shape["group"] is JsonValue groupVal && IsNumberEqualTo(groupVal, 0.0)) {
            elide.Add("group");
        }

        foreach (var (k, v) in shape) {
            if (elide.Contains(k) || v is null) {
                continue;
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{inner}{k}: {FormatValue(v, indentLevel + 1)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    // `JsonValue<T>.TryGetValue<TValue>` only converts across a handful of numeric CLR types reliably, and which
    // one a node carries depends on how it was constructed — a node parsed from JSON text (the decompiler's own
    // production path) converts to any of these; a node built directly from a C# numeric literal (`int`, e.g.
    // `JsonValue.Create(0)`) converts only to that same exact CLR type. Every numeric comparison in this file tries
    // long, then double, then int, matching `FormatValue`'s own fallback chain plus that last case.
    private static bool IsNumberEqualTo(JsonValue value, double expected) =>
        (value.TryGetValue<long>(out var l) && (l == expected))
        || (value.TryGetValue<double>(out var d) && (d == expected))
        || (value.TryGetValue<int>(out var i) && (i == expected));

    private static bool IsIdentityRotation(JsonArray rotation) {
        if (rotation.Count != 4) {
            return false;
        }
        Span<double> expected = [0.0, 0.0, 0.0, 1.0];
        for (var i = 0; i < 4; i++) {
            if (rotation[i] is not JsonValue v || !IsNumberEqualTo(v, expected[i])) {
                return false;
            }
        }
        return true;
    }

    private static bool IsUnitVector(JsonArray vector) {
        if (vector.Count == 0) {
            return false;
        }
        foreach (var element in vector) {
            if (element is not JsonValue v || !IsNumberEqualTo(v, 1.0)) {
                return false;
            }
        }
        return true;
    }
}

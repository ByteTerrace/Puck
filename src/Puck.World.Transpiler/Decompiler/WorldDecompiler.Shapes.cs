using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Decompiler;

// `shape Type "name" { }` inverse (§4.1) — the renamed successor to the old dead `solid`/`"solids"` collector,
// targeting the real `CreationDocument.Shapes` wire key. Elides a field only on an exact match against
// `CreationCanonicalizer.Normalize`'s own defaults, mirroring the emitter's `LowerShapeBlock`.
public static partial class WorldDecompiler {
    // The keys a `shape` row must carry before the `shape Type "name" { }` grammar applies are its construct's own
    // description. A row missing one is a basis-merge patch (an `{ id, ... }` row naming only the facets it
    // overrides on a base document's shape) and prints through the generic value path rather than having a type
    // guessed from its name.
    private static readonly IReadOnlyList<string> ShapeRequiredKeys = WorldConstructs.Table.TryGet(
        construct: out var shapeConstruct,
        keyword: "shape"
    )
        ? shapeConstruct!.RequiredKeys
        : throw new InvalidOperationException(message: "'shape' is not a described construct.");

    private static bool CanSugarShapes(JsonArray shapes) {
        foreach (var item in shapes) {
            if (item is not JsonObject shape) {
                return false;
            }
            foreach (var key in ShapeRequiredKeys) {
                if (
                    (shape[propertyName: key] is not JsonValue value) ||
                    !value.TryGetValue<string>(value: out var text) ||
                    (text.Length == 0)
                ) {
                    return false;
                }
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
                AppendShapeBlock(
                    indentLevel: indentLevel,
                    index: index,
                    sb: sb,
                    shape: shapeObj
                );
            }
            index++;
        }
    }
    private static void AppendShapeBlock(StringBuilder sb, JsonObject shape, int index, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var inner = new string(
            c: ' ',
            count: ((indentLevel + 1) * 4)
        );
        var type = (shape["type"]?.ToString() ?? "");
        var name = shape["name"]?.ToString();
        // An absent `name` and an explicit `"name": null` both read back `null` through the indexer; only the
        // latter carries a key to preserve, as an ordinary `name: null` property alongside the unnamed header form.
        var hasExplicitNullName = ((name is null) && shape.ContainsKey(propertyName: "name"));

        if (name is not null) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}shape {type} \"{EscapeString(s: name)}\" {{"
            );
        } else {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}shape {type} {{"
            );
        }

        var elide = new HashSet<string>(comparer: StringComparer.Ordinal) { "type" };

        if (!hasExplicitNullName) {
            elide.Add(item: "name");
        }

        foreach (var key in WorldDocumentRowDefaults.ShapeKeys) {
            if (WorldDocumentRowDefaults.IsDefaultValue(
                key,
                shape[key],
                index,
                shape: true
            )) {
                elide.Add(item: key);
            }
        }

        foreach (var (k, v) in shape) {
            if (elide.Contains(item: k)) {
                continue;
            }
            if (v is null) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{inner}{k}: null"
                );
                continue;
            }
            if (
                (v is JsonValue numeric) &&
                (UnitSuffixFor(
                fieldKey: k,
                value: numeric
            ) is { } unit)
            ) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{inner}{k}: {FormatValue(
                        indentLevel: (indentLevel + 1),
                        node: numeric
                    )}{unit}"
                );
                continue;
            }
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{inner}{k}{FieldSeparator(value: v)}{FormatValue(
                    indentLevel: (indentLevel + 1),
                    node: v
                )}"
            );
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }

}

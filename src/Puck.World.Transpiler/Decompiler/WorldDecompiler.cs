using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.World.Transpiler.Decompiler;

/// <summary>Decompiles canonical Puck JSON definitions back into declarative Puck authoring DSL (.puck).</summary>
public static partial class WorldDecompiler {
    /// <summary>Decompiles a JSON text string into formatted Puck source code.</summary>
    /// <param name="jsonText">The raw or canonical JSON text.</param>
    /// <returns>Clean, idiomatic Puck DSL source code.</returns>
    public static string Decompile(string jsonText) {
        ArgumentNullException.ThrowIfNull(jsonText);

        var node = JsonNode.Parse(jsonText);
        if (node is not JsonObject rootObj) {
            throw new ArgumentException("Root JSON must be an object", nameof(jsonText));
        }

        return Decompile(rootObj);
    }

    /// <summary>Decompiles a root <see cref="JsonObject"/> into formatted Puck source code.</summary>
    /// <param name="root">The root JSON object representing the world definition.</param>
    /// <returns>Clean, idiomatic Puck DSL source code.</returns>
    public static string Decompile(JsonObject root) {
        ArgumentNullException.ThrowIfNull(root);

        var sb = new StringBuilder();

        // 0. Header: every decompiled file is a one-time import — 'let'/'template' cannot be recovered, and
        // re-running the decompiler will not preserve hand-authored constants or templates added after this file
        // was generated.
        sb.AppendLine("// Decompiled from a canonical Puck world document — a one-time import.");
        sb.AppendLine("// 'let'/'template' cannot be recovered; re-running the decompiler will not");
        sb.AppendLine("// preserve hand-authored constants or templates added after this file was");
        sb.AppendLine("// generated. Treat this file as a starting point, not a synced mirror.");
        sb.AppendLine();

        // 1. Headers: schema, basis, documentId
        if (root.TryGetPropertyValue("schema", out var schemaNode) && schemaNode is not null) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"schema: {FormatValue(schemaNode, 0)}");
        }
        if (root.TryGetPropertyValue("basis", out var basisNode) && basisNode is not null) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"basis: {FormatValue(basisNode, 0)}");
        }
        if (root.TryGetPropertyValue("documentId", out var docIdNode) && docIdNode is not null) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"documentId: {FormatValue(docIdNode, 0)}");
        }

        var hasHeaders = sb.Length > 0;

        // 2. Imports
        if (root.TryGetPropertyValue("imports", out var importsNode) && importsNode is JsonArray importsArr && importsArr.Count > 0) {
            if (hasHeaders) {
                sb.AppendLine();
            }
            foreach (var item in importsArr) {
                if (item is JsonObject importObj && importObj.TryGetPropertyValue("document", out var docPathNode)) {
                    var docPath = docPathNode?.ToString() ?? "";
                    if (importObj.TryGetPropertyValue("as", out var asNode) && asNode is not null) {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"import \"{EscapeString(docPath)}\" as {asNode}");
                    } else {
                        sb.AppendLine(CultureInfo.InvariantCulture, $"import \"{EscapeString(docPath)}\"");
                    }
                }
            }
            hasHeaders = true;
        }

        // 3. Exports
        if (root.TryGetPropertyValue("exports", out var exportsNode) && exportsNode is JsonObject exportsObj && exportsObj.Count > 0) {
            if (hasHeaders) {
                sb.AppendLine();
            }
            // Emit actions, bindings, reads
            EmitExportFacet(sb, exportsObj, "actions", "action");
            EmitExportFacet(sb, exportsObj, "bindings", "binding");
            EmitExportFacet(sb, exportsObj, "reads", "read");

            // Any other export facets
            foreach (var (k, v) in exportsObj) {
                if (k is not "actions" and not "bindings" and not "reads" && v is JsonArray arr) {
                    EmitExportNames(sb, k, arr);
                }
            }
            hasHeaders = true;
        }

        // 4. Sections & Blocks
        var knownRootKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "schema", "basis", "documentId", "imports", "exports"
        };

        foreach (var (key, value) in root) {
            if (knownRootKeys.Contains(key)) {
                continue;
            }

            if (sb.Length > 0) {
                sb.AppendLine();
            }

            // An explicit JSON null is a value a basis-merge document authored deliberately; dropping it would
            // change what the composed document says about that section.
            if (value is null) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{key}: null");
                continue;
            }

            if (string.Equals(key, "views", StringComparison.OrdinalIgnoreCase) && value is JsonObject viewsObj) {
                DecompileViewsBlock(sb, viewsObj);
            } else if (string.Equals(key, "addons", StringComparison.OrdinalIgnoreCase) && value is JsonArray addonsArr) {
                DecompileAddonsBlock(sb, addonsArr);
            } else if (string.Equals(key, "shapes", StringComparison.OrdinalIgnoreCase) && value is JsonArray shapesArr && shapesArr.Count > 0 && CanSugarShapes(shapesArr)) {
                DecompileShapesBlock(sb, shapesArr, indentLevel: 0);
            } else if (string.Equals(key, "materials", StringComparison.OrdinalIgnoreCase) && value is JsonArray materialsArr) {
                DecompileMaterialsBlock(sb, materialsArr);
            } else if (string.Equals(key, "rules", StringComparison.OrdinalIgnoreCase) && value is JsonArray rulesArr && CanSugarRules(rulesArr)) {
                DecompileRulesBlock(sb, rulesArr, indentLevel: 0);
            } else if (string.Equals(key, "placements", StringComparison.OrdinalIgnoreCase) && value is JsonObject placementsObj && CanSugarPlacements(placementsObj)) {
                DecompilePlacementsBlock(sb, placementsObj, indentLevel: 0);
            } else if (string.Equals(key, "prototypes", StringComparison.OrdinalIgnoreCase) && value is JsonArray prototypesArr && CanSugarPrototypes(prototypesArr)) {
                DecompilePrototypesBlock(sb, prototypesArr, indentLevel: 0);
            } else {
                EmitField(sb, key, value, indentLevel: 0);
            }
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static void EmitExportFacet(StringBuilder sb, JsonObject exportsObj, string facetKey, string keyword) {
        if (exportsObj.TryGetPropertyValue(facetKey, out var node) && node is JsonArray arr) {
            EmitExportNames(sb, keyword, arr);
        }
    }

    // An empty array still prints its facet keyword, with no names, so the facet's presence (as
    // opposed to its absence) survives the round trip.
    private static void EmitExportNames(StringBuilder sb, string keyword, JsonArray arr) {
        if (arr.Count == 0) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"export {keyword}");
            return;
        }

        var names = string.Join(", ", arr.Select(n => n?.ToString() ?? ""));
        sb.AppendLine(CultureInfo.InvariantCulture, $"export {keyword} {names}");
    }

    private static void DecompileViewsBlock(StringBuilder sb, JsonObject views) {
        sb.AppendLine("views {");

        var first = true;

        // Layouts
        if (views.TryGetPropertyValue("layouts", out var layoutsNode) && layoutsNode is JsonArray layoutsArr) {
            foreach (var layoutItem in layoutsArr) {
                if (layoutItem is JsonObject layoutObj) {
                    if (!first) {
                        sb.AppendLine();
                    }
                    first = false;

                    var name = layoutObj.TryGetPropertyValue("name", out var n) ? n?.ToString() : null;
                    DecompileNamedBlock(sb, "layout", name, layoutObj, indentLevel: 1, excludedKeys: ["name"]);
                }
            }
        }

        // SeatControl
        if (views.TryGetPropertyValue("seatControl", out var scNode) && scNode is JsonObject scObj) {
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            DecompileNamedBlock(sb, "seatControl", null, scObj, indentLevel: 1);
        }

        // SeatRig
        if (views.TryGetPropertyValue("seatRig", out var srNode) && srNode is JsonObject srObj) {
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            var name = srObj.TryGetPropertyValue("name", out var n) ? n?.ToString() : null;
            DecompileSeatRigBlock(sb, name, srObj, indentLevel: 1);
        }

        // Studies
        if (views.TryGetPropertyValue("studies", out var studiesNode) && studiesNode is JsonArray studiesArr) {
            foreach (var studyItem in studiesArr) {
                if (studyItem is JsonObject studyObj) {
                    if (!first) {
                        sb.AppendLine();
                    }
                    first = false;
                    var name = studyObj.TryGetPropertyValue("name", out var n) ? n?.ToString() : null;
                    DecompileNamedBlock(sb, "study", name, studyObj, indentLevel: 1, excludedKeys: ["name"]);
                }
            }
        }

        // Other views properties
        var handledViewsKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "layouts", "seatControl", "seatRig", "studies"
        };

        foreach (var (k, v) in views) {
            if (handledViewsKeys.Contains(k)) {
                continue;
            }
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            if (v is JsonObject subObj) {
                DecompileNamedBlock(sb, k, null, subObj, indentLevel: 1);
            } else {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    {k}: {FormatValue(v, 1)}");
            }
        }

        sb.AppendLine("}");
    }

    private static void DecompileSeatRigBlock(StringBuilder sb, string? name, JsonObject srObj, int indentLevel) {
        var indent = new string(' ', indentLevel * 4);
        if (name is not null) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}seatRig \"{EscapeString(name)}\" {{");
        } else {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}seatRig {{");
        }

        var innerIndent = new string(' ', (indentLevel + 1) * 4);

        if (srObj.TryGetPropertyValue("version", out var verNode) && verNode is not null) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{innerIndent}version: {FormatValue(verNode, indentLevel + 1)}");
        }

        if (srObj.TryGetPropertyValue("operations", out var opsNode) && opsNode is JsonArray opsArr) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{innerIndent}operations [");
            var opIndent = new string(' ', (indentLevel + 2) * 4);
            foreach (var opItem in opsArr) {
                if (opItem is JsonObject opObj && opObj.ContainsKey("$type")) {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{opIndent}{FormatCallForm(opObj, indentLevel + 2)}");
                } else {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"{opIndent}{FormatValue(opItem, indentLevel + 2)}");
                }
            }
            sb.AppendLine(CultureInfo.InvariantCulture, $"{innerIndent}]");
        }

        // Remaining properties
        foreach (var (k, v) in srObj) {
            if (k is "name" or "version" or "operations") {
                continue;
            }
            EmitField(sb, k, v, indentLevel + 1);
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    private static void DecompileAddonsBlock(StringBuilder sb, JsonArray addons) {
        foreach (var item in addons) {
            if (item is JsonObject addonObj) {
                var name = addonObj.TryGetPropertyValue("name", out var n) ? n?.ToString() : null;
                DecompileNamedBlock(sb, "addon", name, addonObj, indentLevel: 0, excludedKeys: ["name"]);
                sb.AppendLine();
            }
        }
    }

    private static void DecompileMaterialsBlock(StringBuilder sb, JsonArray materials) {
        foreach (var item in materials) {
            if (item is JsonObject matObj) {
                var name = matObj.TryGetPropertyValue("name", out var n) ? n?.ToString() : null;
                DecompileNamedBlock(sb, "material", name, matObj, indentLevel: 0, excludedKeys: ["name"]);
                sb.AppendLine();
            }
        }
    }

    // Whether a value PRINTS as a container. Not the same question as whether it IS one: an object carrying a
    // `$type` discriminator prints as a call (`compare(left: …)`), which is leaf-shaped and keeps its colon.
    private static bool RendersAsContainer(JsonNode? value) => value switch {
        JsonArray => true,
        JsonObject obj => obj["$type"] is not JsonValue typeVal || !typeVal.TryGetValue<string>(out _),
        _ => false,
    };

    // The separator a field writes before its value: none in front of a container, ": " in front of a leaf. The
    // colon is what tells a reader "this is a leaf", so it never appears in front of a '{' or a '['.
    internal static string FieldSeparator(JsonNode? value) => RendersAsContainer(value) ? " " : ": ";

    // One field, in the one spelling its value's shape calls for.
    internal static void EmitField(StringBuilder sb, string key, JsonNode? value, int indentLevel) {
        var indent = new string(' ', (indentLevel * 4));

        if (value is JsonObject obj && RendersAsContainer(value)) {
            DecompileNamedBlock(sb, key, null, obj, indentLevel);

            return;
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{key}{FieldSeparator(value)}{FormatValue(value, indentLevel)}");
    }

    private static void DecompileNamedBlock(
        StringBuilder sb,
        string identifier,
        string? name,
        JsonObject blockObj,
        int indentLevel,
        HashSet<string>? excludedKeys = null
    ) {
        var indent = new string(' ', indentLevel * 4);
        if (name is not null) {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{identifier} \"{EscapeString(name)}\" {{");
        } else {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}{identifier} {{");
        }

        var innerIndent = new string(' ', (indentLevel + 1) * 4);
        foreach (var (k, v) in blockObj) {
            if (excludedKeys is not null && excludedKeys.Contains(k)) {
                continue;
            }
            if (string.Equals(k, "shapes", StringComparison.OrdinalIgnoreCase) && v is JsonArray shapesArr && shapesArr.Count > 0 && CanSugarShapes(shapesArr)) {
                DecompileShapesBlock(sb, shapesArr, indentLevel + 1);
            } else {
                EmitField(sb, k, v, indentLevel + 1);
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"{indent}}}");
    }

    private static string FormatValue(JsonNode? node, int indentLevel) {
        if (node is null) {
            return "null";
        }

        if (node is JsonValue val) {
            if (val.TryGetValue<bool>(out var b)) {
                return b ? "true" : "false";
            }
            if (val.TryGetValue<long>(out var l)) {
                return l.ToString(CultureInfo.InvariantCulture);
            }
            if (val.TryGetValue<double>(out var d)) {
                return d.ToString(CultureInfo.InvariantCulture);
            }
            if (val.TryGetValue<string>(out var s)) {
                return $"\"{EscapeString(s)}\"";
            }
            return val.ToString();
        }

        if (node is JsonArray arr) {
            if (arr.Count == 0) {
                return "[]";
            }

            // If array of numbers or small primitives, format inline
            var isSmallPrimitive = arr.All(item => item is JsonValue jv && !jv.TryGetValue<string>(out _));
            if (isSmallPrimitive && arr.Count <= 4) {
                var items = string.Join(", ", arr.Select(i => FormatValue(i, 0)));
                return $"[{items}]";
            }

            var indent = new string(' ', indentLevel * 4);
            var itemIndent = new string(' ', (indentLevel + 1) * 4);
            var sb = new StringBuilder();
            sb.AppendLine("[");
            foreach (var item in arr) {
                sb.AppendLine(CultureInfo.InvariantCulture, $"{itemIndent}{FormatValue(item, indentLevel + 1)}");
            }
            sb.Append(CultureInfo.InvariantCulture, $"{indent}]");
            return sb.ToString();
        }

        if (node is JsonObject obj) {
            if (obj.Count == 0) {
                return "{}";
            }

            // The call-form escape hatch (§7): any object carrying a `$type` discriminator prints as
            // `type(k: v, ...)` — the ActionPredicate/ActionEffect/StateTransform family and every extension arm
            // the DSL's dedicated sugar does not otherwise cover, wherever it appears in the document.
            if (obj["$type"] is JsonValue typeVal && typeVal.TryGetValue<string>(out _)) {
                return FormatCallForm(obj, indentLevel);
            }

            var indent = new string(' ', indentLevel * 4);
            var itemIndent = new string(' ', (indentLevel + 1) * 4);
            var sb = new StringBuilder();
            sb.AppendLine("{");
            foreach (var (k, v) in obj) {
                // An object literal follows the same one-spelling rule a block does: no colon in front of a
                // container. The literal grammar admits `key { }` and `key [ ]` directly.
                sb.AppendLine(CultureInfo.InvariantCulture, $"{itemIndent}{k}{FieldSeparator(v)}{FormatValue(v, indentLevel + 1)}");
            }
            sb.Append(CultureInfo.InvariantCulture, $"{indent}}}");
            return sb.ToString();
        }

        return node.ToString();
    }

    // The universal call-form printer: `type(k: v, ...)`, always with NAMED arguments (never positional — the
    // emitter's positional heuristics only exist for `orbit`/`fov`, so a named spelling is the only one guaranteed
    // to round-trip any `$type` object). Recurses through `FormatValue` for nested arguments, so a `$type` object
    // nested inside another call's argument prints as a nested call too.
    private static string FormatCallForm(JsonObject obj, int indentLevel) {
        var type = obj["$type"]?.ToString() ?? "";
        var args = new List<string>();
        foreach (var (k, v) in obj) {
            if (string.Equals(k, "$type", StringComparison.Ordinal)) {
                continue;
            }
            args.Add($"{k}: {FormatValue(v, indentLevel)}");
        }
        return $"{type}({string.Join(", ", args)})";
    }

    private static string EscapeString(string s) {
        return s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}

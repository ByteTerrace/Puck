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
            throw new ArgumentException(
                message: "Root JSON must be an object",
                paramName: nameof(jsonText)
            );
        }

        return Decompile(root: rootObj);
    }
    /// <summary>Decompiles a root <see cref="JsonObject"/> into formatted Puck source code.</summary>
    /// <param name="root">The root JSON object representing the world definition.</param>
    /// <returns>Clean, idiomatic Puck DSL source code.</returns>
    public static string Decompile(JsonObject root) {
        ArgumentNullException.ThrowIfNull(root);

        var sb = new StringBuilder();

        // 0. Header: the source is what a reader edits, so the file says which direction is authoritative. A
        // decompile BOOTSTRAPS a source from a document and is one-way -- `let`, `template` and `for` are a source
        // idea the JSON no longer carries, so re-running it over an edited source discards that work.
        sb.AppendLine(value: "// Bootstrapped from a Puck world document. The '.puck' source is canonical: edit it and");
        sb.AppendLine(value: "// compile, never the '.world.json' beside it.");
        sb.AppendLine(value: "//");
        sb.AppendLine(value: "// Decompiling again would OVERWRITE this file and discard every 'let', 'template' and");
        sb.AppendLine(value: "// 'for' in it, none of which the document carries.");
        sb.AppendLine();

        // 1. Headers: schema, basis, documentId
        if (
            root.TryGetPropertyValue(
            jsonNode: out var schemaNode,
            propertyName: "schema"
        ) &&
            (schemaNode is not null)
        ) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"schema: {FormatValue(
                    indentLevel: 0,
                    node: schemaNode
                )}"
            );
        }
        if (
            root.TryGetPropertyValue(
            jsonNode: out var basisNode,
            propertyName: "basis"
        ) &&
            (basisNode is not null)
        ) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"basis: {FormatValue(
                    indentLevel: 0,
                    node: basisNode
                )}"
            );
        }
        if (
            root.TryGetPropertyValue(
            jsonNode: out var docIdNode,
            propertyName: "documentId"
        ) &&
            (docIdNode is not null)
        ) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"documentId: {FormatValue(
                    indentLevel: 0,
                    node: docIdNode
                )}"
            );
        }

        var hasHeaders = (sb.Length > 0);

        // 2. Imports
        if (
            root.TryGetPropertyValue(
            jsonNode: out var importsNode,
            propertyName: "imports"
        ) &&
            (importsNode is JsonArray importsArr) &&
            (importsArr.Count > 0)
        ) {
            if (hasHeaders) {
                sb.AppendLine();
            }
            foreach (var item in importsArr) {
                if (
                    (item is JsonObject importObj) &&
                    importObj.TryGetPropertyValue(
                    jsonNode: out var docPathNode,
                    propertyName: "document"
                )
                ) {
                    var docPath = (docPathNode?.ToString() ?? "");

                    if (
                        importObj.TryGetPropertyValue(
                        jsonNode: out var asNode,
                        propertyName: "as"
                    ) &&
                        (asNode is not null)
                    ) {
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"import \"{EscapeString(s: docPath)}\" as {asNode}"
                        );
                    } else {
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"import \"{EscapeString(s: docPath)}\""
                        );
                    }
                }
            }
            hasHeaders = true;
        }

        // 3. Exports
        if (
            root.TryGetPropertyValue(
            jsonNode: out var exportsNode,
            propertyName: "exports"
        ) &&
            (exportsNode is JsonObject exportsObj) &&
            (exportsObj.Count > 0)
        ) {
            if (hasHeaders) {
                sb.AppendLine();
            }
            // Emit actions, bindings, reads
            EmitExportFacet(
                exportsObj: exportsObj,
                facetKey: "actions",
                keyword: "action",
                sb: sb
            );
            EmitExportFacet(
                exportsObj: exportsObj,
                facetKey: "bindings",
                keyword: "binding",
                sb: sb
            );
            EmitExportFacet(
                exportsObj: exportsObj,
                facetKey: "reads",
                keyword: "read",
                sb: sb
            );

            // Any other export facets
            foreach (var (k, v) in exportsObj) {
                if (
                    (k is not "actions" and not "bindings" and not "reads") &&
                    (v is JsonArray arr)
                ) {
                    EmitExportNames(
                        arr: arr,
                        keyword: k,
                        sb: sb
                    );
                }
            }
            hasHeaders = true;
        }

        // 4. Sections & Blocks
        var knownRootKeys = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) {
            "schema", "basis", "documentId", "imports", "exports",
        };

        foreach (var (key, value) in root) {
            if (knownRootKeys.Contains(item: key)) {
                continue;
            }

            if (sb.Length > 0) {
                sb.AppendLine();
            }

            // An explicit JSON null is a value a basis-merge document authored deliberately; dropping it would
            // change what the composed document says about that section.
            if (value is null) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{key}: null"
                );
                continue;
            }

            if (
                string.Equals(
                a: key,
                b: "state",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (value is JsonObject stateObj)
            ) {
                DecompileStateBlock(
                    indentLevel: 0,
                    sb: sb,
                    state: stateObj
                );
            } else if (
                string.Equals(
                a: key,
                b: "views",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (value is JsonObject viewsObj)
            ) {
                DecompileViewsBlock(
                    sb: sb,
                    views: viewsObj
                );
            } else if (
                string.Equals(
                a: key,
                b: "addons",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (value is JsonArray addonsArr)
            ) {
                DecompileAddonsBlock(
                    addons: addonsArr,
                    sb: sb
                );
            } else if (
                string.Equals(
                a: key,
                b: "shapes",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (value is JsonArray shapesArr) &&
                (shapesArr.Count > 0) &&
                CanSugarShapes(shapes: shapesArr)
            ) {
                DecompileShapesBlock(
                    sb,
                    shapesArr,
                    indentLevel: 0
                );
            } else if (
                string.Equals(
                a: key,
                b: "materials",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (value is JsonArray materialsArr)
            ) {
                DecompileMaterialsBlock(
                    materials: materialsArr,
                    sb: sb
                );
            } else if (
                string.Equals(
                a: key,
                b: "rules",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (value is JsonArray rulesArr) &&
                CanSugarRules(rules: rulesArr)
            ) {
                DecompileRulesBlock(
                    sb,
                    rulesArr,
                    indentLevel: 0
                );
            } else if (
                string.Equals(
                a: key,
                b: "placements",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (value is JsonObject placementsObj) &&
                CanSugarPlacements(placements: placementsObj)
            ) {
                DecompilePlacementsBlock(
                    sb,
                    placementsObj,
                    indentLevel: 0
                );
            } else if (
                string.Equals(
                a: key,
                b: "prototypes",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (value is JsonArray prototypesArr) &&
                CanSugarPrototypes(prototypes: prototypesArr)
            ) {
                DecompilePrototypesBlock(
                    sb,
                    prototypesArr,
                    indentLevel: 0
                );
            } else {
                EmitField(
                    sb,
                    key,
                    value,
                    indentLevel: 0
                );
            }
        }

        return (sb.ToString().TrimEnd() + "\n");
    }

    private static void EmitExportFacet(StringBuilder sb, JsonObject exportsObj, string facetKey, string keyword) {
        if (
            exportsObj.TryGetPropertyValue(
            jsonNode: out var node,
            propertyName: facetKey
        ) &&
            (node is JsonArray arr)
        ) {
            EmitExportNames(
                arr: arr,
                keyword: keyword,
                sb: sb
            );
        }
    }
    // An empty array still prints its facet keyword, with no names, so the facet's presence (as
    // opposed to its absence) survives the round trip.
    private static void EmitExportNames(StringBuilder sb, string keyword, JsonArray arr) {
        if (arr.Count == 0) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"export {keyword}"
            );
            return;
        }

        var names = string.Join(
            separator: ", ",
            values: arr.Select(selector: n => (n?.ToString() ?? ""))
        );

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"export {keyword} {names}"
        );
    }
    private static void DecompileViewsBlock(StringBuilder sb, JsonObject views) {
        sb.AppendLine(value: "views {");

        var first = true;

        // Layouts
        if (
            views.TryGetPropertyValue(
            jsonNode: out var layoutsNode,
            propertyName: "layouts"
        ) &&
            (layoutsNode is JsonArray layoutsArr)
        ) {
            foreach (var layoutItem in layoutsArr) {
                if (layoutItem is JsonObject layoutObj) {
                    if (!first) {
                        sb.AppendLine();
                    }
                    first = false;

                    var name = (layoutObj.TryGetPropertyValue(
                        jsonNode: out var n,
                        propertyName: "name"
                    )
                        ? n?.ToString()
                        : null
                    );

                    DecompileNamedBlock(
                        sb,
                        "layout",
                        name,
                        layoutObj,
                        indentLevel: 1,
                        excludedKeys: ["name"]
                    );
                }
            }
        }

        // SeatControl
        if (
            views.TryGetPropertyValue(
            jsonNode: out var scNode,
            propertyName: "seatControl"
        ) &&
            (scNode is JsonObject scObj)
        ) {
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            DecompileNamedBlock(
                sb,
                "seatControl",
                null,
                scObj,
                indentLevel: 1
            );
        }

        // SeatRig
        if (
            views.TryGetPropertyValue(
            jsonNode: out var srNode,
            propertyName: "seatRig"
        ) &&
            (srNode is JsonObject srObj)
        ) {
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            var name = (srObj.TryGetPropertyValue(
                jsonNode: out var n,
                propertyName: "name"
            )
                ? n?.ToString()
                : null
            );

            DecompileSeatRigBlock(
                sb,
                name,
                srObj,
                indentLevel: 1
            );
        }

        // Pipelines
        if (
            views.TryGetPropertyValue(
            jsonNode: out var pipelinesNode,
            propertyName: "pipelines"
        ) &&
            (pipelinesNode is JsonArray pipelinesArr)
        ) {
            foreach (var pipelineItem in pipelinesArr) {
                if (pipelineItem is JsonObject pipelineObj) {
                    if (!first) {
                        sb.AppendLine();
                    }
                    first = false;
                    var name = (pipelineObj.TryGetPropertyValue(
                        jsonNode: out var n,
                        propertyName: "name"
                    )
                        ? n?.ToString()
                        : null
                    );

                    DecompileNamedBlock(
                        sb,
                        "pipeline",
                        name,
                        pipelineObj,
                        indentLevel: 1,
                        excludedKeys: ["name"]
                    );
                }
            }
        }

        // Other views properties
        var handledViewsKeys = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) {
            "layouts", "seatControl", "seatRig", "pipelines",
        };

        foreach (var (k, v) in views) {
            if (handledViewsKeys.Contains(item: k)) {
                continue;
            }
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            if (v is JsonObject subObj) {
                DecompileNamedBlock(
                    sb,
                    k,
                    null,
                    subObj,
                    indentLevel: 1
                );
            } else {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"    {k}: {FormatValue(
                        indentLevel: 1,
                        node: v
                    )}"
                );
            }
        }

        sb.AppendLine(value: "}");
    }
    private static void DecompileSeatRigBlock(StringBuilder sb, string? name, JsonObject srObj, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        if (name is not null) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}seatRig \"{EscapeString(s: name)}\" {{"
            );
        } else {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}seatRig {{"
            );
        }

        var innerIndent = new string(
            c: ' ',
            count: ((indentLevel + 1) * 4)
        );

        if (
            srObj.TryGetPropertyValue(
            jsonNode: out var verNode,
            propertyName: "version"
        ) &&
            (verNode is not null)
        ) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{innerIndent}version: {FormatValue(
                    indentLevel: (indentLevel + 1),
                    node: verNode
                )}"
            );
        }

        if (
            srObj.TryGetPropertyValue(
            jsonNode: out var opsNode,
            propertyName: "operations"
        ) &&
            (opsNode is JsonArray opsArr)
        ) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{innerIndent}operations ["
            );
            var opIndent = new string(
                c: ' ',
                count: ((indentLevel + 2) * 4)
            );

            foreach (var opItem in opsArr) {
                if (
                    (opItem is JsonObject opObj) &&
                    opObj.ContainsKey(propertyName: "$type")
                ) {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"{opIndent}{FormatCallForm(
                            indentLevel: (indentLevel + 2),
                            obj: opObj
                        )}"
                    );
                } else {
                    sb.AppendLine(
                        CultureInfo.InvariantCulture,
                        $"{opIndent}{FormatValue(
                            indentLevel: (indentLevel + 2),
                            node: opItem
                        )}"
                    );
                }
            }
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{innerIndent}]"
            );
        }

        // Remaining properties
        foreach (var (k, v) in srObj) {
            if (k is "name" or "version" or "operations") {
                continue;
            }
            EmitField(
                indentLevel: (indentLevel + 1),
                key: k,
                sb: sb,
                value: v
            );
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static void DecompileAddonsBlock(StringBuilder sb, JsonArray addons) {
        foreach (var item in addons) {
            if (item is JsonObject addonObj) {
                var name = (addonObj.TryGetPropertyValue(
                    jsonNode: out var n,
                    propertyName: "name"
                )
                    ? n?.ToString()
                    : null
                );

                DecompileNamedBlock(
                    sb,
                    "addon",
                    name,
                    addonObj,
                    indentLevel: 0,
                    excludedKeys: ["name"]
                );
                sb.AppendLine();
            }
        }
    }
    private static void DecompileMaterialsBlock(StringBuilder sb, JsonArray materials) {
        foreach (var item in materials) {
            if (item is JsonObject matObj) {
                var name = (matObj.TryGetPropertyValue(
                    jsonNode: out var n,
                    propertyName: "name"
                )
                    ? n?.ToString()
                    : null
                );

                DecompileNamedBlock(
                    sb,
                    "material",
                    name,
                    matObj,
                    indentLevel: 0,
                    excludedKeys: ["name"]
                );
                sb.AppendLine();
            }
        }
    }
    // Whether a value PRINTS as a container. Not the same question as whether it IS one: an object carrying a
    // `$type` discriminator prints as a call (`compare(left: …)`), which is leaf-shaped and keeps its colon.
    private static bool RendersAsContainer(JsonNode? value) => value switch {
        JsonArray => true,
        JsonObject obj => ((obj["$type"] is not JsonValue typeVal) || !typeVal.TryGetValue<string>(value: out _)),
        _ => false,
    };

    // The separator a field writes before its value: none in front of a container, ": " in front of a leaf. The
    // colon is what tells a reader "this is a leaf", so it never appears in front of a '{' or a '['.
    internal static string FieldSeparator(JsonNode? value) => (RendersAsContainer(value: value)
        ? " "
        : ": "
    );
    // One field, in the one spelling its value's shape calls for.
    internal static void EmitField(StringBuilder sb, string key, JsonNode? value, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        if (
            (value is JsonObject obj) &&
            RendersAsContainer(value: value)
        ) {
            DecompileNamedBlock(
                sb,
                key,
                null,
                obj,
                indentLevel
            );

            return;
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}{key}{FieldSeparator(value: value)}{FormatValue(
                indentLevel: indentLevel,
                node: value
            )}"
        );
    }

    private static void DecompileNamedBlock(
        StringBuilder sb,
        string identifier,
        string? name,
        JsonObject blockObj,
        int indentLevel,
        HashSet<string>? excludedKeys = null
    ) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        if (name is not null) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}{identifier} \"{EscapeString(s: name)}\" {{"
            );
        } else {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}{identifier} {{"
            );
        }

        var innerIndent = new string(
            c: ' ',
            count: ((indentLevel + 1) * 4)
        );

        foreach (var (k, v) in blockObj) {
            if (
                (excludedKeys is not null) &&
                excludedKeys.Contains(item: k)
            ) {
                continue;
            }
            if (
                string.Equals(
                a: k,
                b: "shapes",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (v is JsonArray shapesArr) &&
                (shapesArr.Count > 0) &&
                CanSugarShapes(shapes: shapesArr)
            ) {
                DecompileShapesBlock(
                    indentLevel: (indentLevel + 1),
                    sb: sb,
                    shapes: shapesArr
                );
            } else {
                EmitField(
                    indentLevel: (indentLevel + 1),
                    key: k,
                    sb: sb,
                    value: v
                );
            }
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static string FormatValue(JsonNode? node, int indentLevel) {
        if (node is null) {
            return "null";
        }

        if (node is JsonValue val) {
            if (val.TryGetValue<bool>(value: out var b)) {
                return (b
                    ? "true"
                    : "false"
                );
            }
            if (val.TryGetValue<long>(value: out var l)) {
                return l.ToString(provider: CultureInfo.InvariantCulture);
            }
            if (val.TryGetValue<double>(value: out var d)) {
                return d.ToString(provider: CultureInfo.InvariantCulture);
            }
            if (val.TryGetValue<string>(value: out var s)) {
                return $"\"{EscapeString(s: s)}\"";
            }
            return val.ToString();
        }

        if (node is JsonArray arr) {
            if (arr.Count == 0) {
                return "[]";
            }

            // If array of numbers or small primitives, format inline
            var isSmallPrimitive = arr.All(predicate: item => ((item is JsonValue jv) && !jv.TryGetValue<string>(value: out _)));

            if (
                isSmallPrimitive &&
                (arr.Count <= 4)
            ) {
                var items = string.Join(
                    separator: ", ",
                    values: arr.Select(selector: i => FormatValue(
                        indentLevel: 0,
                        node: i
                    ))
                );

                return $"[{items}]";
            }

            var indent = new string(
                c: ' ',
                count: (indentLevel * 4)
            );
            var itemIndent = new string(
                c: ' ',
                count: ((indentLevel + 1) * 4)
            );
            var sb = new StringBuilder();

            sb.AppendLine(value: "[");
            foreach (var item in arr) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{itemIndent}{FormatValue(
                        indentLevel: (indentLevel + 1),
                        node: item
                    )}"
                );
            }
            sb.Append(
                CultureInfo.InvariantCulture,
                $"{indent}]"
            );
            return sb.ToString();
        }

        if (node is JsonObject obj) {
            if (obj.Count == 0) {
                return "{}";
            }

            // The call-form escape hatch (§7): any object carrying a `$type` discriminator prints as
            // `type(k: v, ...)` — the ActionPredicate/ActionEffect/StateTransform family and every extension arm
            // the DSL's dedicated sugar does not otherwise cover, wherever it appears in the document.
            if (
                (obj["$type"] is JsonValue typeVal) &&
                typeVal.TryGetValue<string>(value: out _)
            ) {
                return FormatCallForm(
                    indentLevel: indentLevel,
                    obj: obj
                );
            }

            var indent = new string(
                c: ' ',
                count: (indentLevel * 4)
            );
            var itemIndent = new string(
                c: ' ',
                count: ((indentLevel + 1) * 4)
            );
            var sb = new StringBuilder();

            sb.AppendLine(value: "{");
            foreach (var (k, v) in obj) {
                // An object literal follows the same one-spelling rule a block does: no colon in front of a
                // container. The literal grammar admits `key { }` and `key [ ]` directly.
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{itemIndent}{k}{FieldSeparator(value: v)}{FormatValue(
                        indentLevel: (indentLevel + 1),
                        node: v
                    )}"
                );
            }
            sb.Append(
                CultureInfo.InvariantCulture,
                $"{indent}}}"
            );
            return sb.ToString();
        }

        return node.ToString();
    }
    // The universal call-form printer: `type(k: v, ...)`, always with NAMED arguments (never positional — the
    // emitter's positional heuristics only exist for `orbit`/`fov`, so a named spelling is the only one guaranteed
    // to round-trip any `$type` object). Recurses through `FormatValue` for nested arguments, so a `$type` object
    // nested inside another call's argument prints as a nested call too.
    private static string FormatCallForm(JsonObject obj, int indentLevel) {
        var type = (obj["$type"]?.ToString() ?? "");
        var args = new List<string>();

        foreach (var (k, v) in obj) {
            if (string.Equals(
                a: k,
                b: "$type",
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }
            args.Add(item: $"{k}: {FormatValue(
                indentLevel: indentLevel,
                node: v
            )}");
        }
        return $"{type}({string.Join(
            separator: ", ",
            values: args
        )})";
    }
    private static string EscapeString(string s) {
        return s.Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: "\\\\",
            oldValue: "\\"
        ).Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: "\\\"",
            oldValue: "\""
        );
    }
}

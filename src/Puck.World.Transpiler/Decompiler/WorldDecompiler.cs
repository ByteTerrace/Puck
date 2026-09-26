using Puck.State;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Transpiler.Formatting;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Vocabulary;

namespace Puck.World.Transpiler.Decompiler;

/// <summary>Decompiles canonical Puck JSON definitions back into declarative Puck authoring DSL (.puck).</summary>
public static partial class WorldDecompiler {
    /// <summary>Decompiles a JSON text string into formatted Puck source code.</summary>
    /// <param name="jsonText">The raw or canonical JSON text.</param>
    /// <param name="embeddings">Optional companion embedding lock file for resolving vector literals.</param>
    /// <returns>Clean, idiomatic Puck DSL source code.</returns>
    public static string Decompile(string jsonText, EmbeddingLock? embeddings = null) {
        ArgumentNullException.ThrowIfNull(jsonText);

        var node = JsonNode.Parse(jsonText);

        if (node is not JsonObject rootObj) {
            throw new ArgumentException(
                message: "Root JSON must be an object",
                paramName: nameof(jsonText)
            );
        }

        return Decompile(embeddings: embeddings, root: rootObj);
    }

    // Every section prints the document's own spelling of an expression. The finished text is respelled once as an
    // author writes it, outside its string literals and comments: a string argument of a document-form call holds
    // the document's spelling by definition.
    private static string Respell(string text) {
        var into = new StringBuilder(capacity: text.Length);
        var code = 0;

        void Flush(int end) {
            if (end > code) {
                into.Append(value: ExpressionSpelling.ToSourceDialect(text: text[code..end]));
            }
        }

        for (var index = 0; (index < text.Length); index++) {
            var character = text[index];

            if (character == '"') {
                var close = (index + 1);

                while ((close < text.Length) && (text[close] != '"') && (text[close] != '\n')) {
                    close += ((text[close] == '\\')
                        ? 2
                        : 1
                    );
                }

                close = Math.Min(
                    val1: close,
                    val2: (text.Length - 1)
                );
                // The `$` of an interpolated string belongs to the literal, not to the code before it.
                var open = (((index > 0) && (text[(index - 1)] == '$'))
                    ? (index - 1)
                    : index
                );

                Flush(end: open);
                into.Append(value: text[open..(close + 1)]);
                index = close;
                code = (close + 1);
            } else if ((character == '/') && ((index + 1) < text.Length) && (text[(index + 1)] == '/')) {
                var end = text.IndexOf(
                    startIndex: index,
                    value: '\n'
                );

                end = ((end < 0)
                    ? text.Length
                    : end
                );
                Flush(end: index);
                into.Append(value: text[index..end]);
                index = (end - 1);
                code = end;
            }
        }

        Flush(end: text.Length);

        return into.ToString();
    }

    /// <summary>Decompiles a root <see cref="JsonObject"/> into formatted Puck source code.</summary>
    /// <param name="root">The root JSON object representing the world definition.</param>
    /// <param name="embeddings">Optional companion embedding lock file for resolving vector literals.</param>
    /// <returns>Clean, idiomatic Puck DSL source code.</returns>
    /// <exception cref="WorldDecompileRefusedException">The document declares a name in the generated form that no
    /// construct the decompiler prints would generate.</exception>
    public static string Decompile(JsonObject root, EmbeddingLock? embeddings = null) {
        ArgumentNullException.ThrowIfNull(root);

        // 0. Header: the source is what a reader edits, so the file says which direction is authoritative. A
        // decompile BOOTSTRAPS a source from a document and is one-way -- `let`, `template` and `for` are a source
        // idea the JSON no longer carries, so re-running it over an edited source discards that work.
        return (Header + DecompileDocument(
            embeddings: embeddings,
            moduleBody: false,
            root: root
        ));
    }

    private const string Header = """
        // Bootstrapped from a Puck world document. The '.puck' source is canonical: edit it and
        // compile, never the '.world.json' beside it.
        //
        // Decompiling again would OVERWRITE this file and discard every 'let', 'template' and
        // 'for' in it, none of which the document carries.


        """;

    // A document's statements. A module body that a `world name = module()` declaration expands carries neither
    // `schema` nor `documentId`: the declaration writes both.
    private static string DecompileDocument(JsonObject root, EmbeddingLock? embeddings, bool moduleBody) {
        // Every section prints a reference from its colon spelling, so the copy printed from holds that spelling
        // where the document holds a call node.
        root = ((JsonObject)root.DeepClone());
        WorldChannelNodes.Raise(
            document: root,
            type: typeof(WorldDefinition)
        );

        if (moduleBody) {
            _ = root.Remove(propertyName: "documentId");

            if (root["schema"]?.GetValue<string>() == WorldDocumentVocabulary.Schema) {
                _ = root.Remove(propertyName: "schema");
            }
        }

        var names = NamesToSweep(root: root);
        var generated = ReverseGenerated(root: root);
        var sb = new StringBuilder();

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
                            $"import {PuckStrings.Write(value: docPath)} as {asNode}"
                        );
                    } else {
                        sb.AppendLine(
                            CultureInfo.InvariantCulture,
                            $"import {PuckStrings.Write(value: docPath)}"
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

        // A group and the rules it claims are one construct in source, so the claim is resolved once, before either
        // section prints, and a claimed rule never prints on its own.
        var groupedRules = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);
        var sugaredGroups = (
            (root["ruleGroups"] is JsonArray declaredGroups) &&
            (declaredGroups.Count > 0) &&
            CanSugarRuleGroups(
                claimed: out groupedRules,
                groups: declaredGroups,
                rules: (root["rules"] as JsonArray)
            )
        );

        RefuseUnprintedGeneratedNames(
            consumed: generated.Consumed,
            groupsPrintAsSugar: sugaredGroups,
            names: names,
            rulesPrintAsSugar: ((root["rules"] is JsonArray printedRules) && CanSugarRules(rules: printedRules))
        );

        // A ground block prints after the prototypes and placements written before it and ahead of every other
        // statement, so both sections come back in the order they were written.
        foreach (var leading in generated.Leading) {
            if (sb.Length > 0) {
                sb.AppendLine();
            }

            leading(obj: sb);
        }

        foreach (var (key, value) in root) {
            if (knownRootKeys.Contains(item: key)) {
                continue;
            }
            if (sugaredGroups && string.Equals(
                a: key,
                b: "ruleGroups",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                // Where the rules print as sugar, each group prints among them, where its members stand.
                if (
                    (root["rules"] is JsonArray sugaredRules) &&
                    CanSugarRules(rules: sugaredRules)
                ) {
                    continue;
                }
                if (sb.Length > 0) {
                    sb.AppendLine();
                }

                DecompileRuleGroupsBlock(
                    claimed: groupedRules,
                    groups: ((JsonArray)value!),
                    indentLevel: 0,
                    sb: sb
                );
                hasHeaders = true;

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
                    $"{PuckPrinter.PrintPropertyName(level: 0, name: key)}: null"
                );
                continue;
            }

            // The table names which arm a root construct's section takes, read off the document key rather than
            // off a keyword the printer does not have. An arm whose guard does not hold prints as an ordinary
            // field, which is the fallback every row's description already names.
            var printed = (WorldConstructs.Table.RootArmWriting(documentKey: key) ?? WorldRootArm.Field) switch {
                WorldRootArm.Addons => TryDecompileAddons(
                    sb: sb,
                    value: value
                ),
                WorldRootArm.Materials => TryDecompileMaterials(
                    sb: sb,
                    value: value
                ),
                WorldRootArm.Patterns => TryDecompilePatterns(
                    sb: sb,
                    value: value
                ),
                WorldRootArm.Placements => TryDecompilePlacements(
                    sb: sb,
                    value: value
                ),
                WorldRootArm.Prototypes => TryDecompilePrototypes(
                    sb: sb,
                    value: value
                ),
                WorldRootArm.Rules => TryDecompileRules(
                    claimed: groupedRules,
                    groups: (sugaredGroups
                        ? (root["ruleGroups"] as JsonArray)
                        : null),
                    sb: sb,
                    value: value
                ),
                WorldRootArm.Sets => TryDecompileCellSets(
                    sb: sb,
                    value: value
                ),
                WorldRootArm.Shapes => TryDecompileShapes(
                    sb: sb,
                    value: value
                ),
                WorldRootArm.State => TryDecompileState(
                    embeddings: embeddings,
                    sb: sb,
                    value: value
                ),
                WorldRootArm.Views => TryDecompileViews(
                    sb: sb,
                    value: value
                ),
                // A `ruleGroups` array is resolved with the rules it claims before either section prints, above;
                // unsugared, it prints as an ordinary field, which is also what `cartridge`, `host` and the
                // compile-time layer take.
                WorldRootArm.Cartridge or WorldRootArm.CompileTime or WorldRootArm.Field or WorldRootArm.RuleGroups => false,
                // An arm added to the description with no printer here throws by name at the first document that
                // takes it, which is what `ConstructRootArmLawTests` drives one probe per root construct to reach.
                _ => throw new NotSupportedException(message: $"'{key}' takes a root arm this printer has no case for."),
            };

            if (!printed) {
                EmitField(
                    sb,
                    key,
                    value,
                    holder: typeof(WorldDefinition),
                    indentLevel: 0
                );
            }
        }

        var text = Respell(text: (sb.ToString().TrimEnd() + "\n"));

        // A test's `when` lines carry console commands, which are not expressions to respell.
        return ((generated.Test is { } test)
            ? $"{text}\n{test}"
            : text
        );
    }
    // One printer per root arm. Each answers whether it printed the section as its construct: a node whose shape
    // or sugar requirement does not hold prints as an ordinary field instead, which is the fallback the row's own
    // description names.
    private static bool TryDecompileAddons(StringBuilder sb, JsonNode? value) {
        if (value is not JsonArray addons) {
            return false;
        }
        DecompileAddonsBlock(
            addons: addons,
            sb: sb
        );

        return true;
    }
    private static bool TryDecompileCellSets(StringBuilder sb, JsonNode? value) {
        if (
            (value is not JsonArray sets) ||
            !CanSugarCellSets(sets: sets)
        ) {
            return false;
        }
        DecompileCellSetsBlock(
            indentLevel: 0,
            sb: sb,
            sets: sets
        );

        return true;
    }
    private static bool TryDecompileMaterials(StringBuilder sb, JsonNode? value) {
        if (value is not JsonArray materials) {
            return false;
        }
        DecompileMaterialsBlock(
            materials: materials,
            sb: sb
        );

        return true;
    }
    private static bool TryDecompilePatterns(StringBuilder sb, JsonNode? value) {
        if (
            (value is not JsonArray patterns) ||
            !CanSugarPatterns(patterns: patterns)
        ) {
            return false;
        }
        DecompilePatternsBlock(
            patterns: patterns,
            sb: sb
        );

        return true;
    }
    private static bool TryDecompilePlacements(StringBuilder sb, JsonNode? value) {
        if (
            (value is not JsonObject placements) ||
            !CanSugarPlacements(placements: placements)
        ) {
            return false;
        }
        DecompilePlacementsBlock(
            sb,
            placements,
            indentLevel: 0
        );

        return true;
    }
    private static bool TryDecompilePrototypes(StringBuilder sb, JsonNode? value) {
        if (
            (value is not JsonArray prototypes) ||
            !CanSugarPrototypes(prototypes: prototypes)
        ) {
            return false;
        }
        DecompilePrototypesBlock(
            sb,
            prototypes,
            indentLevel: 0
        );

        return true;
    }
    // A group prints where its first member stands among the rules, so a source that interleaves groups and rules
    // compiles back to the same rules array.
    private static bool TryDecompileRules(StringBuilder sb, JsonNode? value, IReadOnlyDictionary<string, JsonObject> claimed, JsonArray? groups) {
        if (
            (value is not JsonArray rules) ||
            !CanSugarRules(rules: rules)
        ) {
            return false;
        }
        if (groups is null) {
            DecompileRulesBlock(
                sb,
                Unclaimed(
                    claimed: claimed,
                    rules: rules
                ),
                indentLevel: 0
            );

            return true;
        }

        var groupOf = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        foreach (var item in groups) {
            if (
                (item is JsonObject group) &&
                (group["steps"] is JsonArray steps)
            ) {
                foreach (var step in steps) {
                    if ((step as JsonObject)?["rule"]?.ToString() is { } member) {
                        groupOf[member] = group;
                    }
                }
            }
        }

        var printed = new HashSet<JsonObject>(comparer: ReferenceEqualityComparer.Instance);
        var first = true;

        foreach (var item in rules) {
            if (item is not JsonObject rule) {
                continue;
            }

            var name = (rule["name"]?.ToString() ?? "");

            if (claimed.ContainsKey(key: name)) {
                if (
                    !groupOf.TryGetValue(key: name, value: out var group) ||
                    !printed.Add(item: group)
                ) {
                    continue;
                }
                if (!first) {
                    sb.AppendLine();
                }

                first = false;
                AppendRuleGroupBlock(
                    claimed: claimed,
                    group: group,
                    indentLevel: 0,
                    sb: sb
                );

                continue;
            }
            if (!first) {
                sb.AppendLine();
            }

            first = false;
            AppendRuleBlock(
                indentLevel: 0,
                rule: rule,
                sb: sb
            );
        }

        return true;
    }
    private static bool TryDecompileShapes(StringBuilder sb, JsonNode? value) {
        if (
            (value is not JsonArray shapes) ||
            (shapes.Count == 0) ||
            !CanSugarShapes(shapes: shapes)
        ) {
            return false;
        }
        DecompileShapesBlock(
            sb,
            shapes,
            indentLevel: 0
        );

        return true;
    }
    private static bool TryDecompileState(StringBuilder sb, JsonNode? value, EmbeddingLock? embeddings) {
        if (value is not JsonObject state) {
            return false;
        }
        DecompileStateBlock(
            embeddings: embeddings,
            indentLevel: 0,
            sb: sb,
            state: state
        );

        return true;
    }
    private static bool TryDecompileViews(StringBuilder sb, JsonNode? value) {
        if (value is not JsonObject views) {
            return false;
        }
        DecompileViewsBlock(
            sb: sb,
            views: views
        );

        return true;
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

        // Graph instances and post passes: each row prints as its named block.
        foreach (var (rowsKey, keyword) in ((ReadOnlySpan<(string, string)>)[("graphs", "graph"), ("post", "post")])) {
            if (
                !views.TryGetPropertyValue(
                jsonNode: out var rowsNode,
                propertyName: rowsKey
            ) ||
                (rowsNode is not JsonArray rows)
            ) {
                continue;
            }

            foreach (var row in rows) {
                if (row is JsonObject rowObj) {
                    if (!first) {
                        sb.AppendLine();
                    }
                    first = false;
                    var name = (rowObj.TryGetPropertyValue(
                        jsonNode: out var n,
                        propertyName: "name"
                    )
                        ? n?.ToString()
                        : null
                    );

                    DecompileNamedBlock(
                        sb,
                        keyword,
                        name,
                        rowObj,
                        indentLevel: 1,
                        excludedKeys: ["name"]
                    );
                }
            }
        }

        // Other views properties
        var handledViewsKeys = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase) {
            "layouts", "seatControl", "seatRig", "graphs", "post",
        };

        foreach (var (k, v) in views) {
            if (handledViewsKeys.Contains(item: k)) {
                continue;
            }
            if (!first) {
                sb.AppendLine();
            }
            first = false;
            EmitField(
                indentLevel: 1,
                key: k,
                sb: sb,
                value: v
            );
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
                $"{indent}seatRig {PuckStrings.Write(value: name)} {{"
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
    // The rules no group claims, in document order. A group prints its own members inside its block.
    private static JsonArray Unclaimed(JsonArray rules, IReadOnlyDictionary<string, JsonObject> claimed) {
        if (claimed.Count == 0) {
            return rules;
        }

        var kept = new JsonArray();

        foreach (var item in rules) {
            if (
                (item is JsonObject rule) &&
                (rule["name"]?.ToString() is { } name) &&
                claimed.ContainsKey(key: name)
            ) {
                continue;
            }

            kept.AppendNode(item: item?.DeepClone());
        }

        return kept;
    }

    internal static string FieldSeparator(JsonNode? value) => (RendersAsContainer(value: value)
        ? " "
        : ": "
    );
    // One field, in the one spelling its value's shape calls for.
    internal static void EmitField(StringBuilder sb, string key, JsonNode? value, int indentLevel, Type? holder = null) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var position = ((holder is null) ? null : WorldCallArguments.MemberType(member: key, owner: holder));
        var form = ((holder is null) ? WorldArgumentForm.Unclassified : WorldCallArguments.Classify(member: key, owner: holder));
        var text = PrintsAsText(form: form, node: value);

        if (
            !text &&
            (value is JsonObject obj) &&
            RendersAsContainer(value: value)
        ) {
            DecompileNamedBlock(
                sb,
                key,
                null,
                obj,
                indentLevel,
                holder: position
            );

            return;
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}{PuckPrinter.PrintPropertyName(level: indentLevel, name: key)}{(text ? ": " : FieldSeparator(value: value))}{FormatArgument(
                context: position,
                form: form,
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
        HashSet<string>? excludedKeys = null,
        Type? holder = null
    ) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        if (name is not null) {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}{identifier} {PuckStrings.Write(value: name)} {{"
            );
        } else {
            // With no name the block is a document member, whose key is spelled as a property's.
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}{PuckPrinter.PrintPropertyName(level: indentLevel, name: identifier)} {{"
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
                    holder: holder,
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
    private static string FormatValue(JsonNode? node, int indentLevel, Type? context = null) {
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
                return PuckStrings.Write(value: s);
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
                        context: context,
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
                        context: context,
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
                    context: context,
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
                    $"{itemIndent}{PuckPrinter.PrintPropertyName(level: (indentLevel + 1), name: k)}{(PrintsAsText(form: ((context is null) ? WorldArgumentForm.Unclassified : WorldCallArguments.Classify(member: k, owner: context)), node: v) ? ": " : FieldSeparator(value: v))}{FormatArgument(
                        context: ((context is null) ? null : WorldCallArguments.MemberType(member: k, owner: context)),
                        form: ((context is null) ? WorldArgumentForm.Unclassified : WorldCallArguments.Classify(member: k, owner: context)),
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
    private static string FormatCallForm(JsonObject obj, int indentLevel, Type? context = null) {
        var type = (obj["$type"]?.ToString() ?? "");
        var args = new List<string>();
        var arm = WorldCallArguments.ArmType(baseType: context, discriminator: type);

        foreach (var (k, v) in obj) {
            if (string.Equals(
                a: k,
                b: "$type",
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }
            args.Add(item: $"{k}: {FormatArgument(
                context: ((arm is null) ? null : WorldCallArguments.MemberType(member: k, owner: arm)),
                form: ((arm is null) ? WorldArgumentForm.Unclassified : WorldCallArguments.Classify(member: k, owner: arm)),
                indentLevel: indentLevel,
                node: v
            )}");
        }
        return $"{type}({string.Join(
            separator: ", ",
            values: args
        )})";
    }
    // An expression program prints as its infix text where the model says the member holds an expression, which is
    // where a source's bare text lowers back to the program; anywhere else it prints as the object it is.
    private static bool PrintsAsText(JsonNode? node, WorldArgumentForm form) => (
        (form == WorldArgumentForm.Expression) &&
        (node is JsonObject { Count: > 0 } program) &&
        (program["instructions"] is JsonArray) &&
        (WorldExpressionJson.Text(node: node) is { Length: > 0 })
    );
    // An argument is printed in its one spelling: a name, a key, an expression and a closed word bare, text quoted.
    // The bare forms print the document's spelling and the finished text is respelled with the rest of the source;
    // a key is the exception, since its document spelling reads as an operand anywhere outside brackets.
    private static string FormatArgument(JsonNode? node, WorldArgumentForm form, int indentLevel, Type? context = null) {
        if (PrintsAsText(form: form, node: node)) {
            return WorldExpressionJson.Text(node: node);
        }
        if ((node is JsonObject reference) && (form == WorldArgumentForm.Name) && (reference["field"] is not null)) {
            return StateChannelRefJsonConverter.FromNode(node: reference).Spelling;
        }
        if ((node is JsonArray array) && (form is WorldArgumentForm.Name or WorldArgumentForm.Choice or WorldArgumentForm.Key or WorldArgumentForm.Expression) && array.All(predicate: static item => (item is JsonValue))) {
            return $"[{string.Join(
                separator: ", ",
                values: array.Select(selector: item => FormatArgument(context: context, form: form, indentLevel: indentLevel, node: item))
            )}]";
        }
        if ((node is JsonValue value) && value.TryGetValue<string>(value: out var written) && (written.Length > 0)) {
            switch (form) {
                case WorldArgumentForm.Name:
                    return DocumentLowering.BareSpelling(form: DocumentValueForm.Name, written: written);
                case WorldArgumentForm.Expression or WorldArgumentForm.Choice:
                    return written;
                case WorldArgumentForm.Key:
                    return DocumentLowering.BareSpelling(form: DocumentValueForm.Key, written: written);
            }
        }

        return FormatValue(
            context: context,
            indentLevel: indentLevel,
            node: node
        );
    }
}

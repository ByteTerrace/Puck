using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Addons;
using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Extensions;

namespace Puck.World.Transpiler.Lowering;

/// <summary>Lowers a Puck authoring AST into a canonical JSON document according to puck.world.def.v1.</summary>
public static partial class WorldDocumentEmitter {
    /// <summary>Lowers the AST <see cref="DocumentNode"/> into a mutable <see cref="JsonObject"/> with diagnostic reporting.</summary>
    /// <param name="document">The document AST to lower.</param>
    /// <param name="basePath">The optional base directory for resolving relative assets such as WASM addons.</param>
    /// <param name="sourceMap">Optional SourceMap to populate with JSON pointer mappings.</param>
    /// <param name="diagnostics">Optional DiagnosticBag to collect lowering diagnostics.</param>
    /// <returns>A CompilationResult carrying the structured JsonObject and diagnostics.</returns>
    public static CompilationResult<JsonObject> LowerWithDiagnostics(
        DocumentNode document,
        string? basePath = null,
        SourceMap? sourceMap = null,
        DiagnosticBag? diagnostics = null
    ) {
        ArgumentNullException.ThrowIfNull(document);

        diagnostics ??= new DiagnosticBag();
        sourceMap ??= new SourceMap();

        var root = new JsonObject();
        if (document.Schema is not null) {
            root["schema"] = document.Schema;
            sourceMap.Register("/schema", document.Span);
        }

        if (document.Basis is not null) {
            root["basis"] = document.Basis;
            sourceMap.Register("/basis", document.BasisSpan);
        }

        var scope = new EvaluationScope(basePath, sourceMap: sourceMap, diagnostics: diagnostics, schema: document.Schema);

        // Pre-scan let constants and templates
        foreach (var statement in document.Statements) {
            if (statement is LetNode letNode) {
                scope.Constants[letNode.Name] = letNode.Value;
            } else if (statement is TemplateNode templateNode) {
                scope.Templates[templateNode.Name] = templateNode;
            }
        }

        // Process statements
        foreach (var statement in document.Statements) {
            ProcessStatement(statement, root, scope);
        }

        var canonicalRoot = (JsonObject)Canonicalize(root)!;
        return new CompilationResult<JsonObject>(canonicalRoot, diagnostics);
    }

    /// <summary>Recursively canonicalizes a JSON node by sorting all object properties ordinally.</summary>
    /// <param name="node">The node to canonicalize.</param>
    /// <returns>A new canonicalized JSON node with sorted keys, or null if input was null.</returns>
    public static JsonNode? Canonicalize(JsonNode? node) {
        if (node is null) {
            return null;
        }

        if (node is JsonObject obj) {
            var sorted = new JsonObject();
            var orderedProps = obj.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal).ToList();
            foreach (var kvp in orderedProps) {
                sorted[kvp.Key] = Canonicalize(kvp.Value);
            }
            return sorted;
        }

        if (node is JsonArray arr) {
            var canonicalArr = new JsonArray();
            foreach (var item in arr) {
                canonicalArr.Add(Canonicalize(item));
            }
            return canonicalArr;
        }

        return node.DeepClone();
    }

    /// <summary>Lowers the AST <see cref="DocumentNode"/> into a mutable <see cref="JsonObject"/>.</summary>
    /// <param name="document">The document AST to lower.</param>
    /// <param name="basePath">The optional base directory for resolving relative assets such as WASM addons.</param>
    /// <returns>A structured <see cref="JsonObject"/> representation of the world definition.</returns>
    public static JsonObject Lower(DocumentNode document, string? basePath = null) =>
        LowerWithDiagnostics(document, basePath).Value!;

    /// <summary>Compiles the document AST into canonical UTF-8 JSON bytes.</summary>
    /// <param name="document">The document AST.</param>
    /// <param name="basePath">The optional base directory for relative assets.</param>
    /// <returns>Deterministic canonical UTF-8 bytes.</returns>
    public static byte[] CompileToUtf8Bytes(DocumentNode document, string? basePath = null) {
        var node = Lower(document, basePath);
        return CanonicalJsonDocument.Serialize(node);
    }

    /// <summary>Compiles the document AST into canonical JSON string.</summary>
    /// <param name="document">The document AST.</param>
    /// <param name="basePath">The optional base directory for relative assets.</param>
    /// <returns>Deterministic canonical JSON string.</returns>
    public static string CompileToJson(DocumentNode document, string? basePath = null) {
        var bytes = CompileToUtf8Bytes(document, basePath);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void ProcessStatement(StatementNode statement, JsonObject target, EvaluationScope scope) {
        switch (statement) {
            case LetNode:
            case TemplateNode:
                // Already indexed in pre-scan
                break;

            case ImportNode importNode: {
                if (target["imports"] is not JsonArray importsArray) {
                    importsArray = [];
                    target["imports"] = importsArray;
                }

                var entry = new JsonObject {
                    ["document"] = importNode.Path
                };
                if (importNode.Alias is not null) {
                    entry["as"] = importNode.Alias;
                }
                importsArray.AppendNode(entry);
                break;
            }

            case ExportNode exportNode: {
                if (target["exports"] is not JsonObject exportsObj) {
                    exportsObj = [];
                    target["exports"] = exportsObj;
                }

                var facet = exportNode.Facet.ToLowerInvariant();
                var facetKey = facet switch {
                    "read" or "reads" => "reads",
                    "action" or "actions" => "actions",
                    "binding" or "bindings" => "bindings",
                    _ => facet
                };

                if (exportsObj[facetKey] is not JsonArray facetArr) {
                    facetArr = [];
                    exportsObj[facetKey] = facetArr;
                }

                foreach (var name in exportNode.Names) {
                    facetArr.AppendNode(JsonValue.Create(name));
                }
                break;
            }

            case ExpressionStatementNode exprStmt: {
                if (exprStmt.Expression is CallExpressionNode call) {
                    ExpandTemplateInvocation(call, target, scope);
                }
                break;
            }

            case PropertyNode propNode: {
                var childPointer = $"{scope.CurrentPointer}/{propNode.Name}";
                scope.SourceMap?.Register(childPointer, propNode.Span);
                target[propNode.Name] = LowerExpression(propNode.Value, scope, propNode.Name);
                break;
            }

            case RuleBlockNode ruleNode: {
                LowerRuleBlock(ruleNode, target, scope);
                break;
            }

            case AddonRequestNode reqNode: {
                if (target["requests"] is not JsonArray reqArr) {
                    reqArr = [];
                    target["requests"] = reqArr;
                }
                var reqIdx = reqArr.Count;
                var childPointer = $"{scope.CurrentPointer}/requests/{reqIdx}";
                scope.SourceMap?.Register(childPointer, reqNode.Span);
                var reqObj = new JsonObject {
                    ["capability"] = reqNode.Capability,
                    ["subject"] = reqNode.Subject
                };
                reqArr.AppendNode(reqObj);
                break;
            }

            case AddonMemoryWatchNode watchNode: {
                if (target["memoryWatches"] is not JsonArray watchArr) {
                    watchArr = [];
                    target["memoryWatches"] = watchArr;
                }
                var watchIdx = watchArr.Count;
                var childPointer = $"{scope.CurrentPointer}/memoryWatches/{watchIdx}";
                scope.SourceMap?.Register(childPointer, watchNode.Span);
                var watchObj = new JsonObject {
                    ["screen"] = watchNode.Screen,
                    ["address"] = watchNode.Address,
                    ["length"] = watchNode.WatchLength
                };
                watchArr.AppendNode(watchObj);
                break;
            }

            case ErrorStatementNode:
                // Parser error recovery placeholder; already recorded in DiagnosticBag
                break;

            case BlockNode blockNode: {
                LowerBlock(blockNode, target, scope);
                break;
            }
        }
    }

    private static void LowerBlock(BlockNode block, JsonObject parent, EvaluationScope scope) {
        var id = block.Identifier;

        // Check if an installed extension handles this block
        var ext = scope.Schema is not null ? TranspilerExtensionRegistry.Default.FindHandler(scope.Schema) : null;
        if (ext is not null && ext.TryLowerSection(id, block, parent, scope.Diagnostics)) {
            return;
        }

        var blockPointer = $"{scope.CurrentPointer}/{id}";
        scope.SourceMap?.Register(blockPointer, block.Span);

        // Views block has specialized semantic mappings
        if (string.Equals(id, "views", StringComparison.OrdinalIgnoreCase)) {
            var viewsObj = parent["views"] as JsonObject ?? [];
            parent["views"] = viewsObj;

            var oldPointer = scope.CurrentPointer;
            scope.CurrentPointer = blockPointer;
            foreach (var stmt in block.Statements) {
                ProcessViewsStatement(stmt, viewsObj, scope);
            }
            scope.CurrentPointer = oldPointer;
            return;
        }

        // Addon collection block
        if (string.Equals(id, "addon", StringComparison.OrdinalIgnoreCase)) {
            if (parent["addons"] is not JsonArray addonsArr) {
                addonsArr = [];
                parent["addons"] = addonsArr;
            }
            var addonIdx = addonsArr.Count;
            var addonPointer = $"{scope.CurrentPointer}/addons/{addonIdx}";
            scope.SourceMap?.Register(addonPointer, block.Span);

            var oldPointer = scope.CurrentPointer;
            scope.CurrentPointer = addonPointer;
            var addonObj = LowerBlockToObject(block, scope);
            scope.CurrentPointer = oldPointer;

            if (block.Name is not null) {
                addonObj["name"] = block.Name;
            }
            ResolveAddonHash(addonObj, scope, block.Span);
            addonsArr.AppendNode(addonObj);
            return;
        }

        // Shape collection block (puck.creation.v1) — CreationDocument.Shapes.
        if (string.Equals(id, "shape", StringComparison.OrdinalIgnoreCase)) {
            LowerShapeBlock(block, parent, scope);
            return;
        }

        // Placements section — WorldPlacementsSection { policy, rows }, with `placement "id" { }` sub-blocks.
        if (string.Equals(id, "placements", StringComparison.OrdinalIgnoreCase)) {
            LowerPlacementsBlock(block, parent, scope);
            return;
        }

        // Prototypes section — an array of WorldPrototype { id, document } rows, with `prototype "id" { document { } }`
        // sub-blocks. `document` is an ordinary nested block (falls through to the general-block case below), so a
        // `shape` statement inside it reaches the same LowerShapeBlock path a root-level creation document uses.
        if (string.Equals(id, "prototypes", StringComparison.OrdinalIgnoreCase)) {
            LowerPrototypesBlock(block, parent, scope);
            return;
        }

        // Material collection block (puck.creation.v1)
        if (string.Equals(id, "material", StringComparison.OrdinalIgnoreCase)) {
            if (parent["materials"] is not JsonArray materialsArr) {
                materialsArr = [];
                parent["materials"] = materialsArr;
            }
            var matIdx = materialsArr.Count;
            var matPointer = $"{scope.CurrentPointer}/materials/{matIdx}";
            scope.SourceMap?.Register(matPointer, block.Span);

            var oldPointer = scope.CurrentPointer;
            scope.CurrentPointer = matPointer;
            var matObj = LowerBlockToObject(block, scope);
            scope.CurrentPointer = oldPointer;

            if (block.Name is not null) {
                matObj["name"] = block.Name;
            }
            materialsArr.AppendNode(matObj);
            return;
        }

        // Cartridge block (puck.cartridge.v1)
        if (string.Equals(id, "cartridge", StringComparison.OrdinalIgnoreCase)) {
            var oldPointer = scope.CurrentPointer;
            scope.CurrentPointer = blockPointer;
            var cartObj = LowerBlockToObject(block, scope);
            scope.CurrentPointer = oldPointer;

            ResolveAddonHash(cartObj, scope, block.Span);
            parent["cartridge"] = cartObj;
            return;
        }

        // General block
        {
            var oldPointer = scope.CurrentPointer;
            scope.CurrentPointer = blockPointer;
            var blockObj = LowerBlockToObject(block, scope);
            scope.CurrentPointer = oldPointer;

            if (block.Name is not null) {
                blockObj["name"] = block.Name;
            }
            if (block.Target is not null) {
                blockObj["target"] = block.Target;
            }

            parent[id] = blockObj;
        }
    }

    private static JsonObject LowerBlockToObject(BlockNode block, EvaluationScope scope) {
        var obj = new JsonObject();
        foreach (var stmt in block.Statements) {
            ProcessStatement(stmt, obj, scope);
        }
        return obj;
    }

    /// <summary><paramref name="fieldKey"/> is the enclosing JSON key this expression fills — a property name, or a
    /// call argument's resolved name — threaded down so a unit-suffixed literal reachable from it (directly, or
    /// through an array/object/range/binary/`let` indirection) can validate against <see cref="WorldDocumentEmitterUnits"/>'s
    /// field-dimension table (§5 of the sugar wave). <see langword="null"/> means no such key applies (a bare
    /// top-level expression, an unnamed call argument that fell to the <c>argN</c> fallback).</summary>
    private static JsonNode? LowerExpression(ExpressionNode expr, EvaluationScope scope, string? fieldKey = null) {
        switch (expr) {
            case LiteralExpressionNode lit:
                return LowerLiteral(lit, fieldKey, scope);

            case ColorExpressionNode color:
                return JsonValue.Create(color.Hex);

            case IdentifierExpressionNode ident:
                if (scope.Constants.TryGetValue(ident.Name, out var constExpr)) {
                    return LowerExpression(constExpr, scope, fieldKey);
                }
                if (string.Equals(ident.Name, "null", StringComparison.Ordinal)) {
                    return null;
                }
                if (string.Equals(ident.Name, "true", StringComparison.Ordinal)) {
                    return JsonValue.Create(true);
                }
                if (string.Equals(ident.Name, "false", StringComparison.Ordinal)) {
                    return JsonValue.Create(false);
                }
                if (string.Equals(ident.Name, "auto", StringComparison.Ordinal)) {
                    return JsonValue.Create("auto");
                }
                return JsonValue.Create(ident.Name);

            case ArrayExpressionNode arr: {
                var jsonArr = new JsonArray();
                foreach (var elem in arr.Elements) {
                    jsonArr.AppendNode(LowerExpression(elem, scope, fieldKey));
                }
                return jsonArr;
            }

            case ObjectExpressionNode obj: {
                var jsonObj = new JsonObject();
                foreach (var prop in obj.Properties) {
                    jsonObj[prop.Name] = LowerExpression(prop.Value, scope, prop.Name);
                }
                return jsonObj;
            }

            case CallExpressionNode call: {
                var jsonObj = new JsonObject {
                    ["$type"] = call.Name
                };

                // Map arguments
                var positionalIndex = 0;
                foreach (var arg in call.Arguments) {
                    var key = arg.Name;
                    if (key is null) {
                        // Positional heuristics for common ops
                        if (string.Equals(call.Name, "orbit", StringComparison.OrdinalIgnoreCase)) {
                            key = positionalIndex switch {
                                0 => "distance",
                                1 => "pitch",
                                2 => "yaw",
                                _ => $"arg{positionalIndex}"
                            };
                        } else if (string.Equals(call.Name, "fov", StringComparison.OrdinalIgnoreCase)) {
                            key = "fieldOfViewRadians";
                        } else {
                            key = $"arg{positionalIndex}";
                        }
                    }

                    // Classified by the qualified `call.argument` key, so a unit reads against the argument's own
                    // dimension rather than whatever a same-named block property elsewhere means.
                    jsonObj[key] = LowerExpression(arg.Value, scope, $"{call.Name}.{key}");
                    positionalIndex++;
                }
                return jsonObj;
            }

            case BinaryExpressionNode bin: {
                return EvaluateBinary(bin, scope, fieldKey);
            }

            case RangeExpressionNode range: {
                var rangeArr = new JsonArray();
                rangeArr.AppendNode(LowerExpression(range.Start, scope, fieldKey));
                rangeArr.AppendNode(LowerExpression(range.End, scope, fieldKey));
                return rangeArr;
            }

            default:
                return null;
        }
    }

    private static JsonNode? LowerLiteral(LiteralExpressionNode lit, string? fieldKey, EvaluationScope scope) {
        if (lit.Value is null) {
            return null;
        }

        if (lit.Value is bool b) {
            return JsonValue.Create(b);
        }

        if (lit.Value is string s) {
            return JsonValue.Create(s);
        }

        // Numeric values with optional unit
        double numVal;
        if (lit.Value is long l) {
            numVal = l;
        } else if (lit.Value is double d) {
            numVal = d;
        } else if (lit.Value is int i) {
            numVal = i;
        } else {
            numVal = Convert.ToDouble(lit.Value, CultureInfo.InvariantCulture);
        }

        if (lit.Unit is not null) {
            return LowerUnitLiteral(numVal, lit.Unit, fieldKey, lit.Span, scope);
        }

        if (lit.Value is long longVal) {
            return JsonValue.Create(longVal);
        }

        return JsonValue.Create(numVal);
    }

    // Every unit is checked against WorldDocumentEmitterUnits' field-dimension table, `%`/`pct` included: a field
    // absent from the table is PUCK024, a unit the field's own dimension does not accept is PUCK025. No unit
    // converts outside the table, so a suffix can never silently change a value the table says nothing about.
    private static JsonNode LowerUnitLiteral(double numVal, string unit, string? fieldKey, SourceSpan span, EvaluationScope scope) {
        if (fieldKey is not null && WorldDocumentEmitterUnits.TryConvert(fieldKey, numVal, unit, out var converted)) {
            if (Math.Abs(converted % 1) < double.Epsilon) {
                return JsonValue.Create((long)converted);
            }
            return JsonValue.Create(converted);
        }

        var kind = (fieldKey is null) ? WorldDocumentEmitterUnits.FieldDimensionKind.Unknown : WorldDocumentEmitterUnits.Classify(fieldKey);
        if (kind == WorldDocumentEmitterUnits.FieldDimensionKind.Unknown) {
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.UnitOnUnknownField, $"'{fieldKey ?? "this field"}' admits no unit — remove the '{unit}' suffix", span);
        } else {
            var accepted = string.Join("/", WorldDocumentEmitterUnits.AcceptedUnitsFor(kind));
            scope.Diagnostics.ReportError(PuckDiagnosticCodes.UnitNotAdmitted, $"'{fieldKey}' accepts {accepted}, not '{unit}'", span);
        }
        return JsonValue.Create(numVal);
    }

    private static JsonNode? EvaluateBinary(BinaryExpressionNode bin, EvaluationScope scope, string? fieldKey) {
        var leftNode = LowerExpression(bin.Left, scope, fieldKey);
        var rightNode = LowerExpression(bin.Right, scope, fieldKey);

        if (leftNode is JsonValue leftVal && rightNode is JsonValue rightVal) {
            if (leftVal.TryGetValue<double>(out var lNum) && rightVal.TryGetValue<double>(out var rNum)) {
                var res = bin.Operator switch {
                    "+" => lNum + rNum,
                    "-" => lNum - rNum,
                    "*" => lNum * rNum,
                    "/" => rNum != 0 ? lNum / rNum : 0,
                    _ => 0
                };
                if (Math.Abs(res % 1) < double.Epsilon) {
                    return JsonValue.Create((long)res);
                }
                return JsonValue.Create(res);
            }
        }

        return null;
    }

    private static void ExpandTemplateInvocation(CallExpressionNode call, JsonObject target, EvaluationScope scope, bool isViewsContext = false) {
        if (!scope.Templates.TryGetValue(call.Name, out var template)) {
            return;
        }

        // Bind arguments
        var localConstants = new Dictionary<string, ExpressionNode>(scope.Constants);
        for (var i = 0; i < template.Parameters.Count; i++) {
            var param = template.Parameters[i];
            ExpressionNode? boundValue = null;

            // Search by name
            foreach (var arg in call.Arguments) {
                if (string.Equals(arg.Name, param.Name, StringComparison.Ordinal)) {
                    boundValue = arg.Value;
                    break;
                }
            }

            // Fallback to positional
            if (boundValue is null && i < call.Arguments.Count && call.Arguments[i].Name is null) {
                boundValue = call.Arguments[i].Value;
            }

            // Fallback to default
            boundValue ??= param.DefaultValue;

            if (boundValue is not null) {
                localConstants[param.Name] = boundValue;
            }
        }

        var invocationScope = new EvaluationScope(scope.BasePath, localConstants, scope.Templates, scope.SourceMap, scope.Diagnostics, scope.Schema, scope.CurrentPointer);
        foreach (var stmt in template.Body.Statements) {
            var expandedStmt = stmt;
            if (stmt is BlockNode blockStmt && blockStmt.Name is not null && invocationScope.Constants.TryGetValue(blockStmt.Name, out var nameExpr)) {
                var evaluatedName = LowerExpression(nameExpr, invocationScope)?.ToString() ?? blockStmt.Name;
                expandedStmt = blockStmt with { Name = evaluatedName };
            }

            if (isViewsContext) {
                ProcessViewsStatement(expandedStmt, target, invocationScope);
            } else {
                ProcessStatement(expandedStmt, target, invocationScope);
            }
        }
    }

    private static void ProcessViewsStatement(StatementNode stmt, JsonObject viewsObj, EvaluationScope scope) {
        if (stmt is BlockNode subBlock) {
            var subId = subBlock.Identifier.ToLowerInvariant();
            if (subId is "layout" or "layouts") {
                if (viewsObj["layouts"] is not JsonArray layoutsArr) {
                    layoutsArr = [];
                    viewsObj["layouts"] = layoutsArr;
                }
                var layoutObj = LowerBlockToObject(subBlock, scope);
                if (subBlock.Name is not null) {
                    layoutObj["name"] = subBlock.Name;
                }
                layoutsArr.AppendNode(layoutObj);
            } else if (subId is "study" or "studies") {
                if (viewsObj["studies"] is not JsonArray studiesArr) {
                    studiesArr = [];
                    viewsObj["studies"] = studiesArr;
                }
                var studyObj = LowerBlockToObject(subBlock, scope);
                if (subBlock.Name is not null) {
                    studyObj["name"] = subBlock.Name;
                }
                studiesArr.AppendNode(studyObj);
            } else if (subId is "seatrig") {
                var seatRigObj = LowerBlockToObject(subBlock, scope);
                if (subBlock.Name is not null) {
                    seatRigObj["name"] = subBlock.Name;
                }
                viewsObj["seatRig"] = seatRigObj;
            } else if (subId is "seatcontrol") {
                viewsObj["seatControl"] = LowerBlockToObject(subBlock, scope);
            } else {
                viewsObj[subBlock.Identifier] = LowerBlockToObject(subBlock, scope);
            }
        } else if (stmt is PropertyNode prop) {
            viewsObj[prop.Name] = LowerExpression(prop.Value, scope);
        } else if (stmt is ExpressionStatementNode exprStmt && exprStmt.Expression is CallExpressionNode call) {
            ExpandTemplateInvocation(call, viewsObj, scope, isViewsContext: true);
        }
    }

    private static void ResolveAddonHash(JsonObject addon, EvaluationScope scope, SourceSpan span = default) {
        var sourcePath = addon["modulePath"]?.ToString() ?? addon["source"]?.ToString() ?? addon["path"]?.ToString() ?? addon["rom"]?.ToString() ?? addon["name"]?.ToString() ?? "addon";
        var hashToken = addon["hash"]?.ToString();

        if (string.IsNullOrEmpty(sourcePath)) {
            return;
        }

        byte[]? wasmBytes = null;
        if (scope.BasePath is not null) {
            var fullPath = Path.Combine(scope.BasePath, sourcePath);
            if (File.Exists(fullPath)) {
                wasmBytes = File.ReadAllBytes(fullPath);
            }
        }

        if (wasmBytes is null && File.Exists(sourcePath)) {
            wasmBytes = File.ReadAllBytes(sourcePath);
        }

        if (wasmBytes is not null) {
            // Inspect WebAssembly binary
            var inspection = WasmModuleInspector.Inspect(wasmBytes);

            // Collect declared capability requests
            var declaredCapabilities = new List<string>();
            if (addon["requests"] is JsonArray reqArr) {
                foreach (var item in reqArr) {
                    if (item is JsonObject rObj && rObj.TryGetPropertyValue("capability", out var capVal)) {
                        declaredCapabilities.Add(capVal?.ToString() ?? "");
                    }
                }
            }

            WasmModuleInspector.ValidateContract(inspection, sourcePath, hashToken, declaredCapabilities, scope.Diagnostics, span);

            if (string.Equals(hashToken, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(hashToken)) {
                addon["hash"] = inspection.ContentHash;
            }
        } else if (string.Equals(hashToken, "auto", StringComparison.OrdinalIgnoreCase)) {
            addon["hash"] = WorldDefinitionFileSource.ComputeContentHash(Encoding.UTF8.GetBytes(sourcePath));
        }
    }

    private sealed class EvaluationScope(
        string? basePath,
        Dictionary<string, ExpressionNode>? constants = null,
        Dictionary<string, TemplateNode>? templates = null,
        SourceMap? sourceMap = null,
        DiagnosticBag? diagnostics = null,
        string? schema = null,
        string currentPointer = ""
    ) {
        public string? BasePath { get; } = basePath;
        public Dictionary<string, ExpressionNode> Constants { get; } = constants ?? [];
        public Dictionary<string, TemplateNode> Templates { get; } = templates ?? [];
        public SourceMap? SourceMap { get; } = sourceMap;
        public DiagnosticBag Diagnostics { get; } = diagnostics ?? new DiagnosticBag();
        public string? Schema { get; } = schema;
        public string CurrentPointer { get; set; } = currentPointer;
    }

    private static void AppendNode(this JsonArray array, JsonNode? item) {
        ((IList<JsonNode?>)array).Add(item);
    }
}


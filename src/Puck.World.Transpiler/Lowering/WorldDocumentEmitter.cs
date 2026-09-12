using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Addons;
using Puck.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Lowering;

/// <summary>Lowers a Puck authoring AST into a canonical JSON document according to puck.world.def.v1.</summary>
public static partial class WorldDocumentEmitter {
    /// <summary>Lowers the AST <see cref="DocumentNode"/> into a mutable <see cref="JsonObject"/> with diagnostic reporting.</summary>
    /// <param name="document">The document AST to lower.</param>
    /// <param name="basePath">The optional base directory for resolving relative assets such as WASM addons.</param>
    /// <param name="sourceMap">Optional SourceMap to populate with JSON pointer mappings.</param>
    /// <param name="diagnostics">Optional DiagnosticBag to collect lowering diagnostics.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <returns>A CompilationResult carrying the structured JsonObject and diagnostics.</returns>
    public static CompilationResult<JsonObject> LowerWithDiagnostics(
        DocumentNode document,
        string? basePath = null,
        SourceMap? sourceMap = null,
        DiagnosticBag? diagnostics = null,
        CancellationToken cancellationToken = default
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

        var scope = new DocumentScope(WorldDocumentVocabulary.Instance, basePath, sourceMap: sourceMap, diagnostics: diagnostics, schema: document.Schema) {
            Budget = new DocumentEvaluationBudget { CancellationToken = cancellationToken },
        };

        scope.IndexDeclarations(document.Statements);
        try {
            foreach (var statement in document.Statements) {
                ProcessStatement(statement, root, scope);
            }
        } catch (DocumentEvaluationException error) {
            diagnostics.ReportError(error.Code, error.Message, error.Span);
        }

        var canonicalRoot = (JsonObject)Canonicalize(root)!;
        return new CompilationResult<JsonObject>(canonicalRoot, diagnostics);
    }

    /// <summary>Recursively canonicalizes a JSON node by sorting every object's properties ordinally.</summary>
    /// <param name="node">The node to canonicalize.</param>
    /// <returns>A new canonicalized node, or <see langword="null"/> when the input was null.</returns>
    public static JsonNode? Canonicalize(JsonNode? node) => DocumentLowering.Canonicalize(node: node);

    /// <summary>Lowers the AST <see cref="DocumentNode"/> into a mutable <see cref="JsonObject"/>.</summary>
    /// <param name="document">The document AST to lower.</param>
    /// <param name="basePath">The optional base directory for resolving relative assets such as WASM addons.</param>
    /// <returns>A structured <see cref="JsonObject"/> representation of the world definition.</returns>
    /// <exception cref="InvalidOperationException">Lowering reports an error.</exception>
    public static JsonObject Lower(DocumentNode document, string? basePath = null) =>
        LowerWithDiagnostics(document, basePath).RequireValue();

    /// <summary>Compiles the document AST into canonical UTF-8 JSON bytes.</summary>
    /// <param name="document">The document AST.</param>
    /// <param name="basePath">The optional base directory for relative assets.</param>
    /// <returns>Deterministic canonical UTF-8 bytes.</returns>
    /// <exception cref="InvalidOperationException">Lowering reports an error.</exception>
    public static byte[] CompileToUtf8Bytes(DocumentNode document, string? basePath = null) {
        var node = Lower(document, basePath);
        return CanonicalJsonDocument.Serialize(node);
    }

    /// <summary>Compiles the document AST into canonical JSON string.</summary>
    /// <param name="document">The document AST.</param>
    /// <param name="basePath">The optional base directory for relative assets.</param>
    /// <returns>Deterministic canonical JSON string.</returns>
    /// <exception cref="InvalidOperationException">Lowering reports an error.</exception>
    public static string CompileToJson(DocumentNode document, string? basePath = null) {
        var bytes = CompileToUtf8Bytes(document, basePath);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void ProcessStatement(StatementNode statement, JsonObject target, DocumentScope scope) {
        using var evaluation = scope.Budget.Enter(statement.Span);
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

            case ForStatementNode loop:
                DocumentLowering.ExpandFor(loop, target, scope, ProcessStatement);

                break;

            case PropertyNode propNode: {
                var childPointer = $"{scope.CurrentPointer}/{propNode.Name}";
                scope.SourceMap?.Register(childPointer, propNode.Span);
                DocumentLowering.AssignOrExtend(target, propNode.Name, LowerExpression(propNode.Value, scope, propNode.Name));
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

    private static void LowerBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
        var id = block.Identifier;

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

            if (DocumentLowering.ResolveBlockName(block, scope) is { } resolvedaddonObj) {
                addonObj["name"] = resolvedaddonObj;
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

            if (DocumentLowering.ResolveBlockName(block, scope) is { } resolvedmatObj) {
                matObj["name"] = resolvedmatObj;
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

            if (DocumentLowering.ResolveBlockName(block, scope) is { } resolvedblockObj) {
                blockObj["name"] = resolvedblockObj;
            }
            if (block.Target is not null) {
                blockObj["target"] = block.Target;
            }

            parent[id] = blockObj;
        }
    }

    private static JsonObject LowerBlockToObject(BlockNode block, DocumentScope scope) {
        var obj = new JsonObject();
        foreach (var stmt in block.Statements) {
            ProcessStatement(stmt, obj, scope);
        }
        return obj;
    }

    private static JsonNode? LowerExpression(ExpressionNode expr, DocumentScope scope, string? fieldKey = null) =>
        DocumentLowering.LowerValue(expr: expr, scope: scope, fieldKey: fieldKey);

    private static void ExpandTemplateInvocation(CallExpressionNode call, JsonObject target, DocumentScope scope, bool isViewsContext = false) {
        DocumentLowering.ExpandTemplate(
            call: call,
            target: target,
            scope: scope,
            sink: (isViewsContext ? ProcessViewsStatement : ProcessStatement)
        );
    }

    private static void ProcessViewsStatement(StatementNode stmt, JsonObject viewsObj, DocumentScope scope) {
        if (stmt is ForStatementNode loop) {
            DocumentLowering.ExpandFor(loop, viewsObj, scope, ProcessViewsStatement);

            return;
        }

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
            DocumentLowering.AssignOrExtend(viewsObj, prop.Name, LowerExpression(prop.Value, scope));
        } else if (stmt is ExpressionStatementNode exprStmt && exprStmt.Expression is CallExpressionNode call) {
            ExpandTemplateInvocation(call, viewsObj, scope, isViewsContext: true);
        }
    }

    private static void ResolveAddonHash(JsonObject addon, DocumentScope scope, SourceSpan span = default) {
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

}

using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World.Transpiler.Addons;
using Puck.World.Transpiler.Embeddings;
using Puck.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Lowering;

/// <summary>Lowers a Puck authoring AST into a canonical JSON document according to puck.world.definition.v1.</summary>
public static partial class WorldDocumentEmitter {
    /// <summary>Lowers the AST <see cref="DocumentNode"/> into a mutable <see cref="JsonObject"/> with diagnostic reporting.</summary>
    /// <param name="document">The document AST to lower.</param>
    /// <param name="basePath">The optional base directory for resolving relative assets such as WASM addons.</param>
    /// <param name="sourceMap">Optional SourceMap to populate with JSON pointer mappings.</param>
    /// <param name="diagnostics">Optional DiagnosticBag to collect lowering diagnostics.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <param name="embeddings">Optional embedding lock resolving embed literals to vectors.</param>
    /// <returns>A CompilationResult carrying the structured JsonObject and diagnostics.</returns>
    public static CompilationResult<JsonObject> LowerWithDiagnostics(
        DocumentNode document,
        string? basePath = null,
        SourceMap? sourceMap = null,
        DiagnosticBag? diagnostics = null,
        CancellationToken cancellationToken = default,
        EmbeddingLock? embeddings = null
    ) =>
        LowerWithDiagnostics(
            basePath: basePath,
            cancellationToken: cancellationToken,
            diagnostics: diagnostics,
            discoveredEmbeddings: out _,
            document: document,
            embeddings: embeddings,
            sourceMap: sourceMap
        );

    /// <summary>Lowers a DocumentNode with diagnostics and reports all discovered embedded texts per space.</summary>
    public static CompilationResult<JsonObject> LowerWithDiagnostics(
        DocumentNode document,
        string? basePath,
        SourceMap? sourceMap,
        DiagnosticBag? diagnostics,
        CancellationToken cancellationToken,
        EmbeddingLock? embeddings,
        out IReadOnlyDictionary<string, HashSet<string>> discoveredEmbeddings
    ) {
        ArgumentNullException.ThrowIfNull(document);

        diagnostics ??= new DiagnosticBag();
        sourceMap ??= new SourceMap();

        var root = new JsonObject();

        if (document.Schema is not null) {
            root["schema"] = document.Schema;
            sourceMap.Register(
                jsonPointer: "/schema",
                span: document.Span
            );
        }

        if (document.Basis is not null) {
            root["basis"] = document.Basis;
            sourceMap.Register(
                jsonPointer: "/basis",
                span: document.BasisSpan
            );
        }

        var scope = new DocumentScope(
            WorldDocumentVocabulary.Instance,
            basePath,
            sourceMap: sourceMap,
            diagnostics: diagnostics,
            schema: document.Schema
        ) {
            Budget = new DocumentEvaluationBudget { CancellationToken = cancellationToken },
        };

        var textsMap = new Dictionary<string, HashSet<string>>(comparer: StringComparer.Ordinal);
        scope.Annotations["DiscoveredEmbeddings"] = textsMap;

        if (embeddings is not null) {
            scope.Annotations["EmbeddingLock"] = embeddings;
        }

        scope.Annotations["WorldDocumentRoot"] = root;

        scope.IndexDeclarations(statements: document.Statements);
        try {
            foreach (var statement in document.Statements) {
                ProcessStatement(
                    scope: scope,
                    statement: statement,
                    target: root
                );
            }
        } catch (DocumentEvaluationException error) {
            diagnostics.ReportError(
                code: error.Code,
                message: error.Message,
                span: error.Span
            );
        }

        discoveredEmbeddings = textsMap;
        var canonicalRoot = ((JsonObject)Canonicalize(node: root)!);

        return new CompilationResult<JsonObject>(
            Diagnostics: diagnostics,
            Value: canonicalRoot
        );
    }

    /// <summary>Records an embedded text discovered during lowering for a space.</summary>
    public static void RecordDiscoveredEmbeddingText(DocumentScope scope, string spaceName, string text) {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(spaceName);
        ArgumentNullException.ThrowIfNull(text);

        if (scope.Annotations.TryGetValue("DiscoveredEmbeddings", out var obj) &&
            obj is Dictionary<string, HashSet<string>> map) {
            if (!map.TryGetValue(key: spaceName, value: out var set)) {
                set = new HashSet<string>(comparer: StringComparer.Ordinal);
                map[spaceName] = set;
            }
            set.Add(item: text);
        }
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
        LowerWithDiagnostics(
            document,
            basePath
        ).RequireValue();
    /// <summary>Compiles the document AST into canonical UTF-8 JSON bytes.</summary>
    /// <param name="document">The document AST.</param>
    /// <param name="basePath">The optional base directory for relative assets.</param>
    /// <returns>Deterministic canonical UTF-8 bytes.</returns>
    /// <exception cref="InvalidOperationException">Lowering reports an error.</exception>
    public static byte[] CompileToUtf8Bytes(DocumentNode document, string? basePath = null) {
        var node = Lower(
            basePath: basePath,
            document: document
        );

        return CanonicalJsonDocument.Serialize(node: node);
    }
    /// <summary>Compiles the document AST into canonical JSON string.</summary>
    /// <param name="document">The document AST.</param>
    /// <param name="basePath">The optional base directory for relative assets.</param>
    /// <returns>Deterministic canonical JSON string.</returns>
    /// <exception cref="InvalidOperationException">Lowering reports an error.</exception>
    public static string CompileToJson(DocumentNode document, string? basePath = null) {
        var bytes = CompileToUtf8Bytes(
            basePath: basePath,
            document: document
        );

        return Encoding.UTF8.GetString(bytes: bytes);
    }

    private static void ProcessStatement(StatementNode statement, JsonObject target, DocumentScope scope) {
        using var evaluation = scope.Budget.Enter(span: statement.Span);

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
                        ["document"] = importNode.Path,
                    };

                    if (importNode.Alias is not null) {
                        entry["as"] = importNode.Alias;
                    }
                    importsArray.AppendNode(item: entry);
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
                        facetArr.AppendNode(item: JsonValue.Create(name));
                    }
                    break;
                }

            case ExpressionStatementNode exprStmt: {
                    if (exprStmt.Expression is CallExpressionNode call) {
                        ExpandTemplateInvocation(
                            call,
                            target,
                            scope
                        );
                    }
                    break;
                }

            case ForStatementNode loop:
                DocumentLowering.ExpandFor(
                    loop: loop,
                    scope: scope,
                    sink: ProcessStatement,
                    target: target
                );

                break;

            case PropertyNode propNode: {
                    var childPointer = $"{scope.CurrentPointer}/{propNode.Name}";

                    scope.SourceMap?.Register(
                        jsonPointer: childPointer,
                        span: propNode.Span
                    );
                    DocumentLowering.AssignOrExtend(
                        target,
                        propNode.Name,
                        LowerExpression(
                            propNode.Value,
                            scope,
                            propNode.Name
                        )
                    );
                    break;
                }

            case RuleBlockNode ruleNode: {
                    LowerRuleBlock(
                        parent: target,
                        rule: ruleNode,
                        scope: scope
                    );
                    break;
                }

            case AddonRequestNode reqNode: {
                    if (target["requests"] is not JsonArray reqArr) {
                        reqArr = [];
                        target["requests"] = reqArr;
                    }
                    var reqIdx = reqArr.Count;
                    var childPointer = $"{scope.CurrentPointer}/requests/{reqIdx}";

                    scope.SourceMap?.Register(
                        jsonPointer: childPointer,
                        span: reqNode.Span
                    );
                    var reqObj = new JsonObject {
                        ["capability"] = reqNode.Capability,
                        ["subject"] = reqNode.Subject,
                    };

                    reqArr.AppendNode(item: reqObj);
                    break;
                }

            case AddonMemoryWatchNode watchNode: {
                    if (target["memoryWatches"] is not JsonArray watchArr) {
                        watchArr = [];
                        target["memoryWatches"] = watchArr;
                    }
                    var watchIdx = watchArr.Count;
                    var childPointer = $"{scope.CurrentPointer}/memoryWatches/{watchIdx}";

                    scope.SourceMap?.Register(
                        jsonPointer: childPointer,
                        span: watchNode.Span
                    );
                    var watchObj = new JsonObject {
                        ["screen"] = watchNode.Screen,
                        ["address"] = watchNode.Address,
                        ["length"] = watchNode.WatchLength,
                    };

                    watchArr.AppendNode(item: watchObj);
                    break;
                }

            case ErrorStatementNode:
                // Parser error recovery placeholder; already recorded in DiagnosticBag
                break;

            case StateTableDeclarationNode or StateSlotDeclarationNode or StatePileDeclarationNode or StateGridDeclarationNode: {
                    var keyword = (statement switch {
                        StateTableDeclarationNode => "table",
                        StateSlotDeclarationNode => "slot",
                        StatePileDeclarationNode => "pile",
                        _ => "grid",
                    });

                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.StateDeclarationOutsideWorld,
                        message: $"'{keyword}' is only legal directly inside 'state.world' — declarations in 'state.body', 'state.identity', or anywhere else are refused",
                        span: statement.Span
                    );
                    break;
                }

            case EmbeddedBlockNode embeddedBlock: {
                    if (string.Equals(embeddedBlock.Language, "sql", StringComparison.OrdinalIgnoreCase)) {
                        LowerStateSqlBlock(
                            block: embeddedBlock,
                            parent: target,
                            scope: scope
                        );
                    } else {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.UnrecognizedSectionStatement,
                            message: $"Unrecognized embedded language '{embeddedBlock.Language}'",
                            span: embeddedBlock.Span
                        );
                    }
                    break;
                }

            case BlockNode blockNode: {
                    LowerBlock(
                        block: blockNode,
                        parent: target,
                        scope: scope
                    );
                    break;
                }
        }
    }
    private static void LowerBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
        var id = block.Identifier;

        var blockPointer = $"{scope.CurrentPointer}/{id}";

        scope.SourceMap?.Register(
            jsonPointer: blockPointer,
            span: block.Span
        );

        // Views block has specialized semantic mappings
        if (string.Equals(
            a: id,
            b: "views",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            var viewsObj = ((parent["views"] as JsonObject) ?? []);

            parent["views"] = viewsObj;

            var oldPointer = scope.CurrentPointer;

            scope.CurrentPointer = blockPointer;
            foreach (var stmt in block.Statements) {
                ProcessViewsStatement(
                    scope: scope,
                    stmt: stmt,
                    viewsObj: viewsObj
                );
            }
            scope.CurrentPointer = oldPointer;
            return;
        }

        // Addon collection block
        if (string.Equals(
            a: id,
            b: "addon",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            if (parent["addons"] is not JsonArray addonsArr) {
                addonsArr = [];
                parent["addons"] = addonsArr;
            }
            var addonIdx = addonsArr.Count;
            var addonPointer = $"{scope.CurrentPointer}/addons/{addonIdx}";

            scope.SourceMap?.Register(
                jsonPointer: addonPointer,
                span: block.Span
            );

            var oldPointer = scope.CurrentPointer;

            scope.CurrentPointer = addonPointer;
            var addonObj = LowerBlockToObject(
                block: block,
                scope: scope
            );

            scope.CurrentPointer = oldPointer;

            if (DocumentLowering.ResolveBlockName(
                block: block,
                scope: scope
            ) is { } resolvedaddonObj) {
                addonObj["name"] = resolvedaddonObj;
            }
            ResolveAddonHash(
                addon: addonObj,
                scope: scope,
                span: block.Span
            );
            addonsArr.AppendNode(item: addonObj);
            return;
        }

        // Shape collection block (puck.creation.v1) — CreationDocument.Shapes.
        if (string.Equals(
            a: id,
            b: "shape",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            LowerShapeBlock(
                block: block,
                parent: parent,
                scope: scope
            );
            return;
        }

        // State section — `state { world { table/slot/row declarations } body [...] identity [...] }`. Only
        // `world` gets the declaration-block treatment; `body`/`identity`/`lattices` fall through to the general
        // per-child handling below exactly as before (an array, or an ordinary nested block).
        if (string.Equals(
            a: id,
            b: "state",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            LowerStateSectionBlock(
                block: block,
                parent: parent,
                scope: scope
            );
            return;
        }

        // Placements section — WorldPlacementsSection { policy, rows }, with `placement "id" { }` sub-blocks.
        if (string.Equals(
            a: id,
            b: "placements",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            LowerPlacementsBlock(
                block: block,
                parent: parent,
                scope: scope
            );
            return;
        }

        // Prototypes section — an array of WorldPrototype { id, document } rows, with `prototype "id" { document { } }`
        // sub-blocks. `document` is an ordinary nested block (falls through to the general-block case below), so a
        // `shape` statement inside it reaches the same LowerShapeBlock path a root-level creation document uses.
        if (string.Equals(
            a: id,
            b: "prototypes",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            LowerPrototypesBlock(
                block: block,
                parent: parent,
                scope: scope
            );
            return;
        }

        // Material collection block (puck.creation.v1)
        if (string.Equals(
            a: id,
            b: "material",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            if (parent["materials"] is not JsonArray materialsArr) {
                materialsArr = [];
                parent["materials"] = materialsArr;
            }
            var matIdx = materialsArr.Count;
            var matPointer = $"{scope.CurrentPointer}/materials/{matIdx}";

            scope.SourceMap?.Register(
                jsonPointer: matPointer,
                span: block.Span
            );

            var oldPointer = scope.CurrentPointer;

            scope.CurrentPointer = matPointer;
            var matObj = LowerBlockToObject(
                block: block,
                scope: scope
            );

            scope.CurrentPointer = oldPointer;

            if (DocumentLowering.ResolveBlockName(
                block: block,
                scope: scope
            ) is { } resolvedmatObj) {
                matObj["name"] = resolvedmatObj;
            }
            materialsArr.AppendNode(item: matObj);
            return;
        }

        // Cartridge block (puck.cartridge.v1)
        if (string.Equals(
            a: id,
            b: "cartridge",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            var oldPointer = scope.CurrentPointer;

            scope.CurrentPointer = blockPointer;
            var cartObj = LowerBlockToObject(
                block: block,
                scope: scope
            );

            scope.CurrentPointer = oldPointer;

            ResolveAddonHash(
                addon: cartObj,
                scope: scope,
                span: block.Span
            );
            parent["cartridge"] = cartObj;
            return;
        }

        // General block
        {
            var oldPointer = scope.CurrentPointer;

            scope.CurrentPointer = blockPointer;
            var blockObj = LowerBlockToObject(
                block: block,
                scope: scope
            );

            scope.CurrentPointer = oldPointer;

            if (DocumentLowering.ResolveBlockName(
                block: block,
                scope: scope
            ) is { } resolvedblockObj) {
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
            ProcessStatement(
                scope: scope,
                statement: stmt,
                target: obj
            );
        }
        return obj;
    }
    private static JsonNode? LowerExpression(ExpressionNode expr, DocumentScope scope, string? fieldKey = null) =>
        DocumentLowering.LowerValue(
            expr: expr,
            fieldKey: fieldKey,
            scope: scope
        );
    private static void ExpandTemplateInvocation(CallExpressionNode call, JsonObject target, DocumentScope scope, bool isViewsContext = false) {
        DocumentLowering.ExpandTemplate(
            call: call,
            scope: scope,
            sink: (isViewsContext
            ? ProcessViewsStatement
            : ProcessStatement),
            target: target
        );
    }
    private static void ProcessViewsStatement(StatementNode stmt, JsonObject viewsObj, DocumentScope scope) {
        if (stmt is ForStatementNode loop) {
            DocumentLowering.ExpandFor(
                loop: loop,
                scope: scope,
                sink: ProcessViewsStatement,
                target: viewsObj
            );

            return;
        }

        if (stmt is BlockNode subBlock) {
            var subId = subBlock.Identifier.ToLowerInvariant();

            if (subId is "layout" or "layouts") {
                if (viewsObj["layouts"] is not JsonArray layoutsArr) {
                    layoutsArr = [];
                    viewsObj["layouts"] = layoutsArr;
                }
                var layoutObj = LowerBlockToObject(
                    block: subBlock,
                    scope: scope
                );

                if (subBlock.Name is not null) {
                    layoutObj["name"] = subBlock.Name;
                }
                layoutsArr.AppendNode(item: layoutObj);
            } else if (subId is "pipeline" or "pipelines") {
                if (viewsObj["pipelines"] is not JsonArray pipelinesArr) {
                    pipelinesArr = [];
                    viewsObj["pipelines"] = pipelinesArr;
                }
                var pipelineObj = LowerBlockToObject(
                    block: subBlock,
                    scope: scope
                );

                if (subBlock.Name is not null) {
                    pipelineObj["name"] = subBlock.Name;
                }
                pipelinesArr.AppendNode(item: pipelineObj);
            } else if (subId is "seatrig") {
                var seatRigObj = LowerBlockToObject(
                    block: subBlock,
                    scope: scope
                );

                if (subBlock.Name is not null) {
                    seatRigObj["name"] = subBlock.Name;
                }
                viewsObj["seatRig"] = seatRigObj;
            } else if (subId is "seatcontrol") {
                viewsObj["seatControl"] = LowerBlockToObject(
                    block: subBlock,
                    scope: scope
                );
            } else {
                viewsObj[subBlock.Identifier] = LowerBlockToObject(
                    block: subBlock,
                    scope: scope
                );
            }
        } else if (stmt is PropertyNode prop) {
            DocumentLowering.AssignOrExtend(
                viewsObj,
                prop.Name,
                LowerExpression(
                    prop.Value,
                    scope
                )
            );
        } else if (
            (stmt is ExpressionStatementNode exprStmt) &&
            (exprStmt.Expression is CallExpressionNode call)
        ) {
            ExpandTemplateInvocation(
                call,
                viewsObj,
                scope,
                isViewsContext: true
            );
        }
    }
    private static void ResolveAddonHash(JsonObject addon, DocumentScope scope, SourceSpan span = default) {
        var sourcePath = (addon["modulePath"]?.ToString() ?? (addon["source"]?.ToString() ?? (addon["path"]?.ToString() ?? (addon["rom"]?.ToString() ?? (addon["name"]?.ToString() ?? "addon")))));
        var hashToken = addon["hash"]?.ToString();

        if (string.IsNullOrEmpty(value: sourcePath)) {
            return;
        }

        byte[]? wasmBytes = null;

        if (scope.BasePath is not null) {
            var fullPath = Path.Combine(
                path1: scope.BasePath,
                path2: sourcePath
            );

            if (File.Exists(path: fullPath)) {
                wasmBytes = File.ReadAllBytes(path: fullPath);
            }
        }

        if (
            (wasmBytes is null) &&
            File.Exists(path: sourcePath)
        ) {
            wasmBytes = File.ReadAllBytes(path: sourcePath);
        }

        if (wasmBytes is not null) {
            // Inspect WebAssembly binary
            var inspection = WasmModuleInspector.Inspect(wasmBytes: wasmBytes);

            // Collect declared capability requests
            var declaredCapabilities = new List<string>();

            if (addon["requests"] is JsonArray reqArr) {
                foreach (var item in reqArr) {
                    if (
                        (item is JsonObject rObj) &&
                        rObj.TryGetPropertyValue(
                        jsonNode: out var capVal,
                        propertyName: "capability"
                    )
                    ) {
                        declaredCapabilities.Add(item: (capVal?.ToString() ?? ""));
                    }
                }
            }

            WasmModuleInspector.ValidateContract(
                inspection,
                sourcePath,
                hashToken,
                declaredCapabilities,
                scope.Diagnostics,
                span
            );

            if (
                string.Equals(
                a: hashToken,
                b: "auto",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) ||
                string.IsNullOrEmpty(value: hashToken)
            ) {
                addon["hash"] = inspection.ContentHash;
            }
        } else if (string.Equals(
            a: hashToken,
            b: "auto",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            addon["hash"] = WorldDefinitionFileSource.ComputeContentHash(content: Encoding.UTF8.GetBytes(s: sourcePath));
        }
    }

}

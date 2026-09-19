using System.Text;
using System.Text.Json.Nodes;
using Puck.World.Transpiler.Addons;
using Puck.World.Transpiler.Embeddings;
using Puck.World.Transpiler.Vocabulary;
using Puck.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Lowering;

/// <summary>Lowers a Puck authoring AST into a canonical JSON document according to puck.world.definition.v1.</summary>
public static partial class WorldDocumentEmitter {
    // The described vocabulary this pass admits from. Read off the pass's own vocabulary rather than the shipped
    // table, so a law can hand the lowering a description it invented and watch the refusal appear.
    private static WorldConstructTable Constructs(DocumentScope scope) => ((scope.Vocabulary is WorldDocumentVocabulary world)
        ? world.Constructs
        : WorldConstructs.Table
    );
    // The cell kinds a described member is admitted on, and whether one kind is among them. An undescribed member,
    // or one whose admission does not turn on the kind, throws rather than folding to an empty set: an empty set
    // would silently refuse every kind.
    private static IReadOnlyList<string> AdmittedKindsOf(string? enclosing, string keyword, string member, WorldMemberPosition position, DocumentScope scope) {
        var described = (Constructs(scope: scope).TryGet(
            construct: out var construct,
            enclosing: enclosing,
            keyword: keyword
        )
            ? construct!.Members.FirstOrDefault(predicate: candidate => (
                (candidate.Position == position) &&
                string.Equals(
                a: candidate.Name,
                b: member,
                comparisonType: StringComparison.Ordinal
            )
            ))
            : null
        );

        return (((described is not null) && (described.AdmittedKinds.Count > 0))
            ? described.AdmittedKinds
            : throw new InvalidOperationException(message: $"'{keyword}' describes no {position.ToString().ToLowerInvariant()} '{member}' whose admission turns on the row's kind.")
        );
    }
    private static bool AdmitsKind(string? enclosing, string keyword, string member, WorldMemberPosition position, string kind, DocumentScope scope) => AdmittedKindsOf(
        enclosing: enclosing,
        keyword: keyword,
        member: member,
        position: position,
        scope: scope
    ).Contains(
        comparer: StringComparer.Ordinal,
        value: kind
    );
    // The kinds a refusal names, in the order the description lists them.
    private static string KindList(IReadOnlyList<string> kinds) => string.Join(
        separator: " or ",
        values: kinds
    );
    // An undescribed construct throws rather than folding to an empty set, which would silently admit nothing.
    private static HashSet<string> ModifiersOf(string? enclosing, string keyword, DocumentScope scope) => (Constructs(scope: scope).TryGet(
        construct: out var construct,
        enclosing: enclosing,
        keyword: keyword
    )
        ? new HashSet<string>(collection: construct!.ModifierNames, comparer: StringComparer.Ordinal)
        : throw new InvalidOperationException(message: $"'{keyword}' is not a construct described inside {(enclosing is null
            ? "the document"
            : $"'{enclosing}'")}.")
    );

    /// <summary>Lowers the AST <see cref="DocumentNode"/> into a mutable <see cref="JsonObject"/> with diagnostic reporting.</summary>
    /// <param name="document">The document AST to lower.</param>
    /// <param name="basePath">The optional base directory for resolving relative assets such as WASM addons.</param>
    /// <param name="sourceMap">Optional SourceMap to populate with JSON pointer mappings.</param>
    /// <param name="diagnostics">Optional DiagnosticBag to collect lowering diagnostics.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <param name="embeddings">Optional embedding lock resolving embed literals to vectors.</param>
    /// <param name="vocabulary">The described vocabulary to lower against; the shipped one when omitted.</param>
    /// <returns>A CompilationResult carrying the structured JsonObject and diagnostics.</returns>
    /// <remarks>One stage of a compile, not a compile: it neither walks the import graph nor finds the embedding
    /// lock beside the source. <see cref="WorldCompiler"/> is what callers outside this assembly reach for.</remarks>
    internal static CompilationResult<JsonObject> LowerWithDiagnostics(
        DocumentNode document,
        string? basePath = null,
        SourceMap? sourceMap = null,
        DiagnosticBag? diagnostics = null,
        CancellationToken cancellationToken = default,
        EmbeddingLock? embeddings = null,
        WorldDocumentVocabulary? vocabulary = null
    ) =>
        LowerWithDiagnostics(
            basePath: basePath,
            cancellationToken: cancellationToken,
            diagnostics: diagnostics,
            discoveredEmbeddings: out _,
            document: document,
            embeddings: embeddings,
            sourceMap: sourceMap,
            testWorlds: out _,
            vocabulary: vocabulary
        );
    /// <summary>Lowers a DocumentNode with diagnostics and reports all discovered embedded texts per space.</summary>
    internal static CompilationResult<JsonObject> LowerWithDiagnostics(
        DocumentNode document,
        string? basePath,
        SourceMap? sourceMap,
        DiagnosticBag? diagnostics,
        CancellationToken cancellationToken,
        EmbeddingLock? embeddings,
        out IReadOnlyDictionary<string, HashSet<string>> discoveredEmbeddings,
        out IReadOnlyList<WorldTestWorld> testWorlds,
        string? testStem = null,
        WorldDocumentVocabulary? vocabulary = null
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
            (vocabulary ?? WorldDocumentVocabulary.Instance),
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
        IndexStateFamilies(statements: document.Statements, scope: scope);
        IndexTypesAndDerivedState(statements: document.Statements, scope: scope);
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
        EmitStateFamilies(
            root: root,
            scope: scope
        );
        WorldExpressionJson.Lower(
            diagnostics: diagnostics,
            node: root
        );

        var canonicalRoot = ((JsonObject)Canonicalize(node: root)!);

        testWorlds = LowerTests(
            document: canonicalRoot,
            scope: scope,
            stem: (testStem ?? "world")
        );

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

        if (scope.Annotations.TryGetValue(key: "DiscoveredEmbeddings", value: out var obj) &&
            (obj is Dictionary<string, HashSet<string>> map)) {
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

            case CellSetDeclarationNode setNode: {
                    LowerCellSetDeclaration(
                        parent: target,
                        scope: scope,
                        setNode: setNode
                    );
                    break;
                }

            case PatternDeclarationNode patternNode: {
                    LowerPatternDeclaration(
                        parent: target,
                        pattern: patternNode,
                        scope: scope
                    );
                    break;
                }

            case RuleScopeNode scopeNode: {
                    LowerRuleScope(
                        parent: target,
                        scope: scope,
                        scopeNode: scopeNode
                    );
                    break;
                }

            case StabilizeGroupNode stabilizeNode: {
                    LowerStabilizeGroup(
                        parent: target,
                        scope: scope,
                        stabilizeNode: stabilizeNode
                    );
                    break;
                }

            case WorkflowNode workflowNode: {
                    LowerWorkflow(
                        parent: target,
                        scope: scope,
                        workflowNode: workflowNode
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

            case TestDeclarationNode test: {
                    // A test reaches no member of the document it is written in. It is collected against the
                    // scope and lowered once the document is finished, since a generated test world is that
                    // finished document plus what the test asked for.
                    if (!ReferenceEquals(
                        objA: target,
                        objB: (scope.Annotations.TryGetValue(
                        key: "WorldDocumentRoot",
                        value: out var documentRoot
                    )
                            ? documentRoot
                            : null)
                    )) {
                        scope.Diagnostics.ReportError(
                            code: PuckDiagnosticCodes.TestShapeInadmissible,
                            message: $"test '{test.Name}' is written inside another construct — a test states a whole world's behaviour, so it stands at the document's own root",
                            span: test.Span
                        );

                        break;
                    }
                    GetOrCreateTests(scope: scope).Add(item: test);
                    break;
                }

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
                    if (string.Equals(a: embeddedBlock.Language, b: "sql", comparisonType: StringComparison.OrdinalIgnoreCase)) {
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
    // The table names which arm a root construct's block takes; a block whose identifier names no root construct
    // — every block nested inside another construct — takes the generic path.
    private static void LowerBlock(BlockNode block, JsonObject parent, DocumentScope scope) {
        var id = block.Identifier;

        var blockPointer = $"{scope.CurrentPointer}/{id}";

        scope.SourceMap?.Register(
            jsonPointer: blockPointer,
            span: block.Span
        );

        var lowered = (WorldConstructs.Table.RootArmOf(keyword: id) ?? WorldRootArm.Field) switch {
            WorldRootArm.Addons => LowerRowCollectionBlock(
                block: block,
                hashed: true,
                member: "addons",
                parent: parent,
                scope: scope
            ),
            WorldRootArm.Cartridge => LowerCartridgeBlock(
                block: block,
                blockPointer: blockPointer,
                parent: parent,
                scope: scope
            ),
            WorldRootArm.Materials => LowerRowCollectionBlock(
                block: block,
                hashed: false,
                member: "materials",
                parent: parent,
                scope: scope
            ),
            WorldRootArm.Placements => LowerPlacementsBlock(
                block: block,
                parent: parent,
                scope: scope
            ),
            WorldRootArm.Prototypes => LowerPrototypesBlock(
                block: block,
                parent: parent,
                scope: scope
            ),
            WorldRootArm.Shapes => LowerShapeBlock(
                block: block,
                parent: parent,
                scope: scope
            ),
            WorldRootArm.State => LowerStateSectionBlock(
                block: block,
                parent: parent,
                scope: scope
            ),
            WorldRootArm.Views => LowerViewsSectionBlock(
                block: block,
                blockPointer: blockPointer,
                parent: parent,
                scope: scope
            ),
            // Written as a statement of its own kind rather than as a block, so no block reaches these; the
            // compile-time layer reaches no document member at all, and `Field` IS the generic path.
            WorldRootArm.CompileTime or WorldRootArm.Field or WorldRootArm.Patterns or WorldRootArm.RuleGroups or WorldRootArm.Rules or WorldRootArm.Sets => false,
            // An arm added to the description with no lowering here throws by name at the first document that
            // takes it, which is what `ConstructRootArmLawTests` drives one probe per root construct to reach.
            _ => throw new NotSupportedException(message: $"'{id}' takes a root arm this lowering has no case for."),
        };

        if (lowered) {
            return;
        }

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
    // `views { layout … pipeline … seatRig … seatControl { … } }` — each child has its own semantic mapping, so
    // the section's body is walked rather than lowered key by key.
    private static bool LowerViewsSectionBlock(BlockNode block, JsonObject parent, DocumentScope scope, string blockPointer) {
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

        return true;
    }
    // One `addon { }`/`material { }` block, appended to the named root array under its own pointer. An addon
    // additionally resolves its content hash.
    private static bool LowerRowCollectionBlock(BlockNode block, JsonObject parent, DocumentScope scope, string member, bool hashed) {
        if (parent[member] is not JsonArray rows) {
            rows = [];
            parent[member] = rows;
        }

        var rowPointer = $"{scope.CurrentPointer}/{member}/{rows.Count}";

        scope.SourceMap?.Register(
            jsonPointer: rowPointer,
            span: block.Span
        );

        var oldPointer = scope.CurrentPointer;

        scope.CurrentPointer = rowPointer;
        var rowObj = LowerBlockToObject(
            block: block,
            scope: scope
        );

        scope.CurrentPointer = oldPointer;

        if (DocumentLowering.ResolveBlockName(
            block: block,
            scope: scope
        ) is { } resolved) {
            rowObj["name"] = resolved;
        }
        if (hashed) {
            ResolveAddonHash(
                addon: rowObj,
                scope: scope,
                span: block.Span
            );
        }
        rows.AppendNode(item: rowObj);

        return true;
    }
    private static bool LowerCartridgeBlock(BlockNode block, JsonObject parent, DocumentScope scope, string blockPointer) {
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

        return true;
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
                scope.SourceMap?.Register(
                    jsonPointer: $"{scope.CurrentPointer}/layouts/{layoutsArr.Count}",
                    span: subBlock.Span
                );
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
                scope.SourceMap?.Register(
                    jsonPointer: $"{scope.CurrentPointer}/pipelines/{pipelinesArr.Count}",
                    span: subBlock.Span
                );
                var pipelineObj = LowerBlockToObject(
                    block: subBlock,
                    scope: scope
                );

                if (subBlock.Name is not null) {
                    pipelineObj["name"] = subBlock.Name;
                }
                pipelinesArr.AppendNode(item: pipelineObj);
            } else if (subId is "seatrig") {
                scope.SourceMap?.Register(
                    jsonPointer: $"{scope.CurrentPointer}/seatRig",
                    span: subBlock.Span
                );
                var seatRigObj = LowerBlockToObject(
                    block: subBlock,
                    scope: scope
                );

                if (subBlock.Name is not null) {
                    seatRigObj["name"] = subBlock.Name;
                }
                viewsObj["seatRig"] = seatRigObj;
            } else if (subId is "seatcontrol") {
                scope.SourceMap?.Register(
                    jsonPointer: $"{scope.CurrentPointer}/seatControl",
                    span: subBlock.Span
                );
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

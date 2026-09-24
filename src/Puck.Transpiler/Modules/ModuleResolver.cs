using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Modules;

/// <summary>Resolves multi-document import dependency graphs, detects cycles, and supports optional bundling.</summary>
public static class ModuleResolver {
    private sealed class ImportContext(IDocumentVocabulary? vocabulary, CancellationToken cancellationToken) {
        public Dictionary<string, DocumentNode> Documents { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SourceLengths { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);
        public IDocumentVocabulary? Vocabulary { get; } = vocabulary;
        public CancellationToken CancellationToken { get; } = cancellationToken;

        public DocumentNode Read(string path) {
            CancellationToken.ThrowIfCancellationRequested();
            if (!Documents.TryGetValue(key: path, value: out var document)) {
                var source = CompileInputs.ReadAllText(path: path);

                document = PuckParser.ParseDocument(source, vocabulary: Vocabulary);
                Documents.Add(key: path, value: document);
                SourceLengths.Add(key: path, value: source.Length);
            }
            return document;
        }
    }
    private readonly record struct ImportCost(long Declarations, long RewriteWork, long Work, int Depth);
    private readonly record struct ModuleImportIdentity(string Path, string Namespace);
    private sealed class ModuleImportIdentityComparer : IEqualityComparer<ModuleImportIdentity> {
        public static ModuleImportIdentityComparer Instance { get; } = new();

        public bool Equals(ModuleImportIdentity x, ModuleImportIdentity y) =>
            (StringComparer.OrdinalIgnoreCase.Equals(x: x.Path, y: y.Path) && StringComparer.Ordinal.Equals(x: x.Namespace, y: y.Namespace));
        public int GetHashCode(ModuleImportIdentity obj) => HashCode.Combine(
            value1: StringComparer.OrdinalIgnoreCase.GetHashCode(obj: obj.Path),
            value2: StringComparer.Ordinal.GetHashCode(obj: obj.Namespace)
        );
    }
    // The vocabulary-free reading of a runtime document import: the name is a file path beside the importer.
    private sealed class FileDocuments : IDocumentVocabulary {
        public static FileDocuments Instance { get; } = new();

        public UnitDimension ClassifyField(string fieldKey) => UnitDimension.None;
        public string? NameCallArgument(string callName, int positionalIndex) => null;
    }

    private static long AddBounded(long left, long right) => Math.Min(
        val1: (DocumentEvaluationBudget.WorkLimit + 1L),
        val2: (left + right)
    );
    private static void CollectStatements(
        string currentDir,
        DocumentNode doc,
        HashSet<string> loadedFiles,
        List<StatementNode> outputStatements,
        DiagnosticBag diagnostics,
        IDocumentVocabulary? vocabulary = null
    ) {
        foreach (var stmt in doc.Statements) {
            if (stmt is ImportNode importNode) {
                var resolvedTarget = Path.GetFullPath(path: Path.Combine(
                    path1: currentDir,
                    path2: importNode.Path
                ));

                if (
                    loadedFiles.Add(item: resolvedTarget) &&
                    resolvedTarget.EndsWith(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: ".puck"
                )
                ) {
                    if (CompileInputs.Exists(path: resolvedTarget)) {
                        var subText = CompileInputs.ReadAllText(path: resolvedTarget);
                        var subDoc = PuckParser.ParseDocument(source: subText, vocabulary: vocabulary);
                        var subDir = (Path.GetDirectoryName(path: resolvedTarget) ?? "");
                        var importedStatements = new List<StatementNode>();

                        CollectStatements(
                            currentDir: subDir,
                            diagnostics: diagnostics,
                            doc: subDoc,
                            loadedFiles: loadedFiles,
                            outputStatements: importedStatements,
                            vocabulary: vocabulary
                        );
                        outputStatements.AddRange(collection: ((importNode.Alias is { } alias)
                            ? PrefixModuleSymbols(alias: alias, statements: importedStatements)
                            : importedStatements));
                    }
                }
            } else {
                outputStatements.Add(item: stmt);
            }
        }
    }
    private static IReadOnlyList<StatementNode> PrefixModuleSymbols(IReadOnlyList<StatementNode> statements, string alias) {
        var symbols = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var statement in statements) {
            var name = statement switch { LetNode let => let.Name, TemplateNode template => template.Name, _ => null };

            if (name is not null) { symbols.TryAdd(key: name, value: QualifiedName.Parse(text: alias).Append(member: name).ToString()); }
        }

        return new ImportedSymbolRewriter(symbols: symbols).RewriteStatements(statements: statements);
    }

    private sealed class ImportedSymbolRewriter(IReadOnlyDictionary<string, string> symbols) : PuckSyntaxRewriter {
        public IReadOnlyList<StatementNode> RewriteStatements(IReadOnlyList<StatementNode> statements) =>
            Rewrite(document: new DocumentNode(null, null, statements)).Statements;

        private ExpressionNode RewriteOne(ExpressionNode expression) =>
            ((LetNode)RewriteStatements(statements: [new LetNode("__value", expression)])[0]).Value;
        private ImportedSymbolRewriter Without(IEnumerable<string?> names) {
            var hidden = names.Where(predicate: static name => (name is not null)).ToHashSet(comparer: StringComparer.Ordinal);

            return new ImportedSymbolRewriter(symbols: symbols.Where(predicate: pair => !hidden.Contains(item: pair.Key))
                .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal));
        }

        protected override Puck.State.ExpressionSpelling.SyntaxNode RewriteOperandSyntax(Puck.State.ExpressionSpelling.SyntaxNode node, DocumentValueForm form) {
            if (node is Puck.State.ExpressionSpelling.SourceLambda lambda) {
                return lambda with { Body = Without(names: [lambda.Binder]).RewriteOperandSyntax(lambda.Body, DocumentValueForm.Expression) };
            }
            var rewritten = base.RewriteOperandSyntax(form: form, node: node);

            return (((rewritten is Puck.State.ExpressionSpelling.SourceName { Quoted: false } name) &&
                (form != DocumentValueForm.Key) && symbols.TryGetValue(key: name.Name, value: out var replacement))
                ? name with { Name = replacement } : rewritten);
        }
        protected override ExpressionNode RewriteExpression(ExpressionNode expression) {
            if (expression is LambdaExpressionNode lambda) {
                return lambda with { Body = Without(names: lambda.Parameters).RewriteOne(expression: lambda.Body) };
            }
            var rewritten = base.RewriteExpression(expression: expression);

            return rewritten switch {
                IdentifierExpressionNode identifier when symbols.TryGetValue(key: identifier.Name, value: out var replacement) => identifier with { Name = replacement },
                CallExpressionNode call when symbols.TryGetValue(key: call.Name, value: out var replacement) => call with { Name = replacement },
                _ => rewritten,
            };
        }
        protected override StatementNode? RewriteStatement(StatementNode statement) {
            if (statement is TemplateNode template) {
                var nested = Without(names: template.Parameters.Select(selector: static parameter => parameter.Name));
                var earlierParameters = new List<string>();
                var rewritten = template with {
                    Body = template.Body with { Statements = nested.RewriteStatements(statements: template.Body.Statements) },
                    Parameters = [.. template.Parameters.Select(selector: parameter => {
                        var defaultValue = ((parameter.DefaultValue is null)
                            ? null
                            : Without(names: earlierParameters).RewriteOne(expression: parameter.DefaultValue)
                        );

                        earlierParameters.Add(item: parameter.Name);
                        return parameter with { DefaultValue = defaultValue };
                    })],
                };

                return (symbols.TryGetValue(key: template.Name, value: out var templateName)
                    ? rewritten with { Name = templateName }
                    : rewritten);
            }
            if (statement is ForStatementNode loop) {
                return loop with {
                    Body = Without(names: [loop.Item, loop.Index]).RewriteStatements(statements: loop.Body),
                    Sequence = RewriteOne(expression: loop.Sequence),
                };
            }
            var descended = base.RewriteStatement(statement: statement);

            return (((descended is LetNode let) && symbols.TryGetValue(key: let.Name, value: out var letName))
                ? let with { Name = letName }
                : descended);
        }
    }

    private static bool TraverseImports(
        DocumentNode doc,
        string currentPath,
        HashSet<string> visited,
        List<string> activeChain,
        DiagnosticBag diagnostics,
        ImportContext context
    ) {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (activeChain.Count >= DocumentEvaluationBudget.DepthLimit) {
            diagnostics.ReportError(code: PuckDiagnosticCodes.EvaluationLimit, message: $"An import graph nests at most {DocumentEvaluationBudget.DepthLimit} source documents.", span: SourceSpan.None);
            return false;
        }
        var fullPath = Path.GetFullPath(path: currentPath);
        var currentDir = (Path.GetDirectoryName(path: fullPath) ?? "");

        if (activeChain.Contains(
            fullPath,
            StringComparer.OrdinalIgnoreCase
        )) {
            var cycle = string.Join(
                separator: " -> ",
                values: activeChain.Concat(second: [fullPath]).Select(selector: Path.GetFileName)
            );

            diagnostics.ReportError(
                code: PuckDiagnosticCodes.UnresolvedLet,
                message: $"Circular import dependency detected: {cycle}",
                span: SourceSpan.None
            );
            return false;
        }

        if (visited.Contains(item: fullPath)) {
            return true;
        }

        visited.Add(item: fullPath);
        activeChain.Add(item: fullPath);

        var success = true;

        foreach (var stmt in doc.Statements) {
            if (stmt is ImportNode importNode) {
                var resolvedTarget = Path.GetFullPath(path: Path.Combine(
                    path1: currentDir,
                    path2: importNode.Path
                ));
                var module = resolvedTarget.EndsWith(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: ".puck"
                );

                if (!module) {
                    // A runtime document import: which files can carry the named document is the vocabulary's own rule.
                    if (!(context.Vocabulary ?? FileDocuments.Instance).TryFindDocumentImport(
                        directory: currentDir,
                        name: importNode.Path,
                        reason: out var missing
                    )) {
                        diagnostics.ReportError(
                            code: PuckDiagnosticCodes.ImportTargetMissing,
                            message: missing,
                            span: importNode.Span
                        );
                        success = false;
                    }

                    continue;
                }

                if (!CompileInputs.Exists(path: resolvedTarget)) {
                    diagnostics.ReportError(
                        code: PuckDiagnosticCodes.ImportTargetMissing,
                        message: $"Imported module '{importNode.Path}' could not be found at '{resolvedTarget}'.",
                        span: importNode.Span
                    );
                    success = false;
                    continue;
                }

                try {
                    var subDoc = context.Read(path: resolvedTarget);

                    if (!TraverseImports(
                        activeChain: activeChain,
                        context: context,
                        currentPath: resolvedTarget,
                        diagnostics: diagnostics,
                        doc: subDoc,
                        visited: visited
                    )) {
                        success = false;
                    }
                } catch (Exception ex) when ((ex is not OperationCanceledException)) {
                    diagnostics.ReportError(
                        code: PuckDiagnosticCodes.ImportGraph,
                        message: $"Failed to parse imported document '{importNode.Path}': {ex.Message}",
                        span: importNode.Span
                    );
                    success = false;
                }
            }
        }

        activeChain.RemoveAt(index: (activeChain.Count - 1));
        return success;
    }

    /// <summary>Recursively inlines imported AST statements into a single bundled document.</summary>
    /// <param name="rootDoc">The root document AST.</param>
    /// <param name="rootPath">The root document filesystem path.</param>
    /// <param name="diagnostics">The DiagnosticBag to report resolution errors into.</param>
    /// <param name="vocabulary">Optional document vocabulary providing schema-specific lexical rules.</param>
    /// <param name="cancellationToken">Stops import traversal and bundling.</param>
    /// <returns>A bundled DocumentNode with inlined statements, or null if errors occurred.</returns>
    public static DocumentNode? BundleDocument(
        DocumentNode rootDoc,
        string rootPath,
        DiagnosticBag diagnostics,
        IDocumentVocabulary? vocabulary = null,
        CancellationToken cancellationToken = default
    ) {
        if (!ValidateImportGraph(
            cancellationToken: cancellationToken,
            diagnostics: diagnostics,
            rootDoc: rootDoc,
            rootPath: rootPath,
            vocabulary: vocabulary
        )) {
            return null;
        }

        var bundledStatements = new List<StatementNode>();
        var loadedFiles = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var rootDir = (Path.GetDirectoryName(path: Path.GetFullPath(path: rootPath)) ?? "");

        CollectStatements(
            currentDir: rootDir,
            diagnostics: diagnostics,
            doc: rootDoc,
            loadedFiles: loadedFiles,
            outputStatements: bundledStatements,
            vocabulary: vocabulary
        );

        return new DocumentNode(
            Schema: rootDoc.Schema,
            Basis: rootDoc.Basis,
            Statements: bundledStatements,
            Offset: rootDoc.Offset,
            Length: rootDoc.Length,
            Line: rootDoc.Line,
            Column: rootDoc.Column
        ) {
            BasisSpan = rootDoc.BasisSpan,
        };
    }
    /// <summary>Loads module, template, and constant declarations from source imports.</summary>
    /// <remarks>An import makes source modules available to <c>use</c>, but it does not instantiate the imported
    /// document's ordinary statements. World lowering removes source imports; they are not runtime composition entries.</remarks>
    public static DocumentNode? ImportModules(
        DocumentNode rootDoc,
        string rootPath,
        DiagnosticBag diagnostics,
        IDocumentVocabulary? vocabulary = null,
        CancellationToken cancellationToken = default
    ) {
        var context = new ImportContext(cancellationToken: cancellationToken, vocabulary: vocabulary);

        if (!ValidateImportGraph(context: context, diagnostics: diagnostics, rootDoc: rootDoc, rootPath: rootPath)) {
            return null;
        }
        var rootDir = (Path.GetDirectoryName(path: Path.GetFullPath(path: rootPath)) ?? "");

        try {
            var cost = MeasureImportedModules(
                context: context,
                currentDir: rootDir,
                document: rootDoc,
                memo: new(comparer: StringComparer.OrdinalIgnoreCase)
            );

            new DocumentEvaluationBudget { CancellationToken = cancellationToken }.Spend(
                count: cost.Work,
                span: SourceSpan.None
            );
        } catch (DocumentEvaluationException ex) {
            diagnostics.ReportError(code: ex.Code, message: ex.Message, span: ex.Span);
            return null;
        }
        var collected = new List<StatementNode>();

        CollectModules(rootDoc, rootDir, "", new(comparer: ModuleImportIdentityComparer.Instance), collected, context);
        return rootDoc with { Statements = [.. collected, .. rootDoc.Statements] };
    }

    private static ImportCost MeasureImportedModules(
        DocumentNode document,
        string currentDir,
        Dictionary<string, ImportCost> memo,
        ImportContext context,
        int traversalDepth = 1
    ) {
        if (traversalDepth > DocumentEvaluationBudget.DepthLimit) {
            throw new DocumentEvaluationException(
                $"An import graph nests at most {DocumentEvaluationBudget.DepthLimit} source documents.",
                document.Span
            );
        }
        var declarations = 0L;
        var rewriteWork = 0L;
        var work = 0L;
        var depth = 1;

        foreach (var import in document.Statements.OfType<ImportNode>()) {
            context.CancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(path: Path.Combine(path1: currentDir, path2: import.Path));

            if (!path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".puck")) { continue; }
            if (!memo.TryGetValue(key: path, value: out var child)) {
                var imported = context.Read(path: path);
                var ownDeclarations = imported.Statements.LongCount(predicate: static statement => (statement is LetNode or TemplateNode));
                var descendants = MeasureImportedModules(imported, (Path.GetDirectoryName(path: path) ?? ""), memo, context, (traversalDepth + 1));
                var ownRewriteWork = ((ownDeclarations == 0)
                    ? 0L
                    : Math.Max(val1: 1, val2: context.SourceLengths[path])
                );

                child = new ImportCost(
                    Declarations: AddBounded(left: ownDeclarations, right: descendants.Declarations),
                    RewriteWork: AddBounded(left: ownRewriteWork, right: descendants.RewriteWork),
                    Work: AddBounded(left: AddBounded(left: 1, right: ownRewriteWork), right: descendants.Work),
                    Depth: descendants.Depth
                );
                memo.Add(key: path, value: child);
            }
            if (((traversalDepth - 1) + child.Depth) >= DocumentEvaluationBudget.DepthLimit) {
                throw new DocumentEvaluationException(
                    $"An import graph nests at most {DocumentEvaluationBudget.DepthLimit} source documents.",
                    import.Span
                );
            }
            declarations = AddBounded(left: declarations, right: child.Declarations);
            rewriteWork = AddBounded(left: rewriteWork, right: child.RewriteWork);
            work = AddBounded(left: work, right: child.Work);
            if (import.Alias is not null) { work = AddBounded(left: work, right: child.RewriteWork); }
            depth = Math.Max(val1: depth, val2: checked((child.Depth + 1)));
        }
        return new ImportCost(Declarations: declarations, Depth: depth, RewriteWork: rewriteWork, Work: work);
    }
    private static void CollectModules(DocumentNode document, string currentDir, string namespacePath, HashSet<ModuleImportIdentity> loaded, List<StatementNode> output, ImportContext context) {
        context.CancellationToken.ThrowIfCancellationRequested();
        foreach (var import in document.Statements.OfType<ImportNode>()) {
            context.CancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(path: Path.Combine(path1: currentDir, path2: import.Path));
            var nextNamespace = (string.IsNullOrEmpty(value: namespacePath) ? (import.Alias ?? "") : QualifiedName.Parse(text: namespacePath).Append(member: (import.Alias ?? "")).ToString());
            var identity = new ModuleImportIdentity(Namespace: nextNamespace, Path: path);

            if (!path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".puck") || !loaded.Add(item: identity)) { continue; }
            var imported = context.Read(path: path);
            var nested = new List<StatementNode>();

            CollectModules(imported, (Path.GetDirectoryName(path: path) ?? ""), nextNamespace, loaded, nested, context);
            nested.AddRange(collection: imported.Statements.Where(predicate: static statement => (statement is LetNode or TemplateNode))
                .Select(selector: statement => (statement switch {
                    TemplateNode template => template with { DefinitionPath = path },
                    LetNode let => let with { DefinitionPath = path },
                    _ => statement,
                })));
            output.AddRange(collection: ((import.Alias is { } alias) ? PrefixModuleSymbols(alias: alias, statements: nested) : nested));
        }
    }

    /// <summary>Validates the import dependency graph starting from the root document.</summary>
    /// <param name="rootDoc">The parsed root document node.</param>
    /// <param name="rootPath">The filesystem path to the root document.</param>
    /// <param name="diagnostics">The DiagnosticBag to report resolution errors into.</param>
    /// <param name="vocabulary">Optional document vocabulary providing schema-specific lexical rules.</param>
    /// <param name="cancellationToken">Stops import traversal.</param>
    /// <returns>True if all imports resolve successfully without cycles.</returns>
    public static bool ValidateImportGraph(
        DocumentNode rootDoc,
        string rootPath,
        DiagnosticBag diagnostics,
        IDocumentVocabulary? vocabulary = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(rootDoc);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var context = new ImportContext(cancellationToken: cancellationToken, vocabulary: vocabulary);

        return ValidateImportGraph(context: context, diagnostics: diagnostics, rootDoc: rootDoc, rootPath: rootPath);
    }

    private static bool ValidateImportGraph(DocumentNode rootDoc, string rootPath, DiagnosticBag diagnostics, ImportContext context) {
        var visited = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var activeChain = new List<string>();

        return TraverseImports(
            activeChain: activeChain,
            context: context,
            currentPath: rootPath,
            diagnostics: diagnostics,
            doc: rootDoc,
            visited: visited
        );
    }
}

using Puck.Assets;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;
using Puck.Transpiler.Rewriting;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Modules;

/// <summary>Resolves a source's import graph: each import names a module library, whose <c>let</c> and template
/// declarations it brings into the importer's compile, or a document, which the importer's document composes at load
/// (<see cref="IDocumentVocabulary.TryResolveImport"/>). The walk reports a name that resolves to nothing, a cycle
/// among libraries, and a library importing a document, which would compose nowhere.</summary>
public static class ModuleResolver {
    private sealed class ImportContext(IDocumentVocabulary? vocabulary, CancellationToken cancellationToken) {
        private readonly Dictionary<(string Directory, string Name), string?> m_libraries = [];

        public Dictionary<string, DocumentNode> Documents { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SourceLengths { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);
        public IDocumentVocabulary? Vocabulary { get; } = vocabulary;
        public CancellationToken CancellationToken { get; } = cancellationToken;

        // Resolves a name once per walk; library is the library's full path, or null for a document.
        public bool TryResolve(string directory, string name, out string? library, out string reason) {
            reason = string.Empty;

            if (m_libraries.TryGetValue(key: (directory, name), value: out library)) {
                return true;
            }
            if (!(Vocabulary ?? FileDocuments.Instance).TryResolveImport(
                directory: directory,
                library: out library,
                name: name,
                reason: out reason
            )) {
                return false;
            }

            library = ((library is null) ? null : Path.GetFullPath(path: library));
            m_libraries.Add(key: (directory, name), value: library);

            return true;
        }
        // The library an import brings declarations from, or null when it names a document or nothing.
        public string? Library(string directory, ImportNode import) => ((!import.NamesFile && TryResolve(
            directory: directory,
            library: out var library,
            name: import.Path,
            reason: out _
        )) ? library : null);
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
    // The vocabulary-free reading of an import: the name is a library beside the importer.
    private sealed class FileDocuments : IDocumentVocabulary {
        public static FileDocuments Instance { get; } = new();

        public UnitDimension ClassifyField(string fieldKey) => UnitDimension.None;
        public string? NameCallArgument(string callName, int positionalIndex) => null;
    }

    private static long AddBounded(long left, long right) => Math.Min(
        val1: (DocumentEvaluationBudget.WorkLimit + 1L),
        val2: (left + right)
    );
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

        // Every source the walk reads past the root is a library: a document import is never walked here.
        var inLibrary = (activeChain.Count > 1);

        foreach (var importNode in doc.Statements.OfType<ImportNode>()) {
            // The parser refuses a file spelling wherever it reads one: into the root's diagnostics, and as a parse
            // failure of the library that holds it.
            if (importNode.NamesFile) {
                continue;
            }

            if (!context.TryResolve(
                directory: currentDir,
                library: out var library,
                name: importNode.Path,
                reason: out var missing
            )) {
                diagnostics.ReportError(
                    code: PuckDiagnosticCodes.ImportTargetMissing,
                    message: missing,
                    span: importNode.Span
                );
                success = false;

                continue;
            }

            if (library is null) {
                if (inLibrary) {
                    diagnostics.ReportError(
                        code: PuckDiagnosticCodes.ImportGraph,
                        message: $"Module library '{Path.GetFileName(path: fullPath)}' imports the document '{importNode.Path}'. A library emits no document, so the document would compose into nothing; import '{importNode.Path}' from the document that composes it.",
                        span: importNode.Span
                    );
                    success = false;
                }

                continue;
            }

            try {
                var subDoc = context.Read(path: library);

                if (!TraverseImports(
                    activeChain: activeChain,
                    context: context,
                    currentPath: library,
                    diagnostics: diagnostics,
                    doc: subDoc,
                    visited: visited
                )) {
                    success = false;
                }
            } catch (Exception ex) when ((ex is not OperationCanceledException)) {
                diagnostics.ReportError(
                    code: PuckDiagnosticCodes.ImportGraph,
                    message: $"Failed to parse imported module '{importNode.Path}': {ex.Message}",
                    span: importNode.Span
                );
                success = false;
            }
        }

        activeChain.RemoveAt(index: (activeChain.Count - 1));
        return success;
    }

    /// <summary>Walks the root's import graph and brings every module library's <c>let</c> and template
    /// declarations into the root, ahead of its own statements, each under its import's alias when it has one, and
    /// removes the library's import. A library's other statements stay where they are written, and a document import
    /// stays in the root for the vocabulary to lower as a composition entry.</summary>
    /// <param name="rootDoc">The root source's parsed tree.</param>
    /// <param name="rootPath">The root source's path; its imports resolve beside it.</param>
    /// <param name="diagnostics">The bag the walk reports into.</param>
    /// <param name="vocabulary">The vocabulary that resolves an import's name; names are libraries when omitted.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <returns>The root with the imported declarations, or <see langword="null"/> when the graph does not
    /// resolve.</returns>
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
        // A library's import is spent once its declarations are in, so the imports left each name a document.
        return rootDoc with {
            Statements = [
                .. collected,
                .. rootDoc.Statements.Where(predicate: statement => !((statement is ImportNode import) && (context.Library(directory: rootDir, import: import) is not null))),
            ],
        };
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
            if (context.Library(directory: currentDir, import: import) is not { } path) { continue; }
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
            if (context.Library(directory: currentDir, import: import) is not { } path) { continue; }
            var nextNamespace = (string.IsNullOrEmpty(value: namespacePath) ? (import.Alias ?? "") : QualifiedName.Parse(text: namespacePath).Append(member: (import.Alias ?? "")).ToString());

            if (!loaded.Add(item: new ModuleImportIdentity(Namespace: nextNamespace, Path: path))) { continue; }
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

using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Rewriting;
using Puck.Transpiler.Units;

namespace Puck.Transpiler.Modules;

/// <summary>Resolves a source's import graph: each import names a module library, whose <c>let</c> and template
/// declarations it brings into the importer's compile, or a document, which the importer's document composes at load
/// (<see cref="IDocumentVocabulary.TryResolveImport"/>). The walk reports a name that resolves to nothing, a cycle
/// among libraries, and a library importing a document, which would compose nowhere.</summary>
public static class ModuleResolver {
    private sealed class ImportContext(PuckSourceTable sources, IDocumentVocabulary? vocabulary, CancellationToken cancellationToken) {
        private readonly Dictionary<(string Directory, string Name), string?> m_libraries = [];
        // Each library this walk read, by full path: a walk reads one file's bytes once, so an edit made while it runs
        // cannot hand two of its steps different trees.
        private readonly Dictionary<string, PuckSourceTable.Parsed> m_read = new(comparer: Puck.Abstractions.PuckPaths.Comparer);

        public IDocumentVocabulary? Vocabulary { get; } = vocabulary;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        // The libraries this walk parsed rather than found held in the table.
        public int Parsed { get; private set; }

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
        public DocumentNode Read(string path) => ReadParsed(path: path).Document;
        public int SourceLength(string path) => ReadParsed(path: path).SourceLength;

        private PuckSourceTable.Parsed ReadParsed(string path) {
            CancellationToken.ThrowIfCancellationRequested();
            if (!m_read.TryGetValue(key: path, value: out var read)) {
                read = sources.Read(parsed: out var parsed, path: path, vocabulary: Vocabulary);
                m_read.Add(key: path, value: read);
                if (parsed) { Parsed++; }
            }
            return read;
        }
    }
    private readonly record struct ImportCost(long Declarations, long RewriteWork, long Work, int Depth);
    private readonly record struct ModuleImportIdentity(string Path, string Namespace);
    private sealed class ModuleImportIdentityComparer : IEqualityComparer<ModuleImportIdentity> {
        public static ModuleImportIdentityComparer Instance { get; } = new();

        public bool Equals(ModuleImportIdentity x, ModuleImportIdentity y) =>
            (Puck.Abstractions.PuckPaths.Comparer.Equals(x: x.Path, y: y.Path) && StringComparer.Ordinal.Equals(x: x.Namespace, y: y.Namespace));
        public int GetHashCode(ModuleImportIdentity obj) => HashCode.Combine(
            value1: Puck.Abstractions.PuckPaths.Comparer.GetHashCode(obj: obj.Path),
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
    // Renames a module's own declarations, which are exactly its top-level `let`s and templates, and every read of
    // them, to the names its importer sees.
    private static IReadOnlyList<StatementNode> PrefixModuleSymbols(IReadOnlyList<StatementNode> statements, string alias) {
        var symbols = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

        foreach (var statement in statements) {
            var name = statement switch { LetNode let => let.Name, TemplateNode template => template.Name, _ => null };

            if (name is not null) { symbols.TryAdd(key: name, value: QualifiedName.Parse(text: alias).Append(member: name).ToString()); }
        }

        return [.. new ImportedSymbolRewriter(symbols: symbols).Rewrite(document: new DocumentNode(null, null, statements)).Statements.Select(selector: statement => (statement switch {
            LetNode let => let with { Name = symbols[let.Name] },
            TemplateNode template => template with { Name = symbols[template.Name] },
            _ => statement,
        }))];
    }

    // Rewrites each read of a module declaration to its imported name. A name a node binds for a child
    // (SyntaxScopes.Binds) is that binding there, as it is when lowered, so the child is rewritten without it.
    private sealed class ImportedSymbolRewriter(IReadOnlyDictionary<string, string> symbols) : PuckSyntaxRewriter {
        private ImportedSymbolRewriter Without(IEnumerable<string> names) {
            Dictionary<string, string>? visible = null;

            foreach (var name in names) {
                if ((visible ?? symbols).ContainsKey(key: name)) {
                    visible ??= new Dictionary<string, string>(collection: symbols, comparer: StringComparer.Ordinal);
                    visible.Remove(key: name);
                }
            }

            return ((visible is null) ? this : new ImportedSymbolRewriter(symbols: visible));
        }

        protected override PuckSyntaxRewriter Within(SyntaxNode parent, SyntaxNode child) =>
            Without(names: SyntaxScopes.Binds(child: child, node: parent).Select(selector: static binder => binder.Name));
        protected override Puck.State.ExpressionSpelling.SyntaxNode RewriteOperandSyntax(Puck.State.ExpressionSpelling.SyntaxNode node, DocumentValueForm form) {
            if (node is Puck.State.ExpressionSpelling.SourceLambda lambda) {
                return lambda with { Body = Without(names: [lambda.Binder]).RewriteOperandSyntax(lambda.Body, DocumentValueForm.Expression) };
            }
            var rewritten = base.RewriteOperandSyntax(form: form, node: node);

            return (((rewritten is Puck.State.ExpressionSpelling.SourceName { Quoted: false } name) &&
                (form != DocumentValueForm.Key) && symbols.TryGetValue(key: name.Name, value: out var replacement))
                ? name with { Name = replacement } : rewritten);
        }
        protected override ExpressionNode RewriteExpression(ExpressionNode expression) => (base.RewriteExpression(expression: expression) switch {
            IdentifierExpressionNode identifier when symbols.TryGetValue(key: identifier.Name, value: out var replacement) => identifier with { Name = replacement },
            CallExpressionNode call when symbols.TryGetValue(key: call.Name, value: out var replacement) => call with { Name = replacement },
            var rewritten => rewritten,
        });
    }

    private static bool TraverseImports(
        DocumentNode doc,
        string currentPath,
        HashSet<string> visited,
        List<string> activeChain,
        DiagnosticBag diagnostics,
        ImportContext context,
        DiagnosticRelated? importedFrom = null
    ) {
        context.CancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path: currentPath);
        var currentDir = (Path.GetDirectoryName(path: fullPath) ?? "");

        if (visited.Contains(item: fullPath)) {
            return true;
        }

        visited.Add(item: fullPath);
        activeChain.Add(item: fullPath);

        var success = true;

        // Every source the walk reads past the root is a library: a document import is never walked here.
        var inLibrary = (activeChain.Count > 1);
        var currentSourcePath = (inLibrary ? fullPath : null);
        using var sourceScope = diagnostics.PushSource(related: importedFrom, sourcePath: currentSourcePath);

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

            if (activeChain.Contains(
                library,
                Puck.Abstractions.PuckPaths.Comparer
            )) {
                var cycle = string.Join(
                    separator: " -> ",
                    values: activeChain.Concat(second: [library]).Select(selector: Path.GetFileName)
                );

                diagnostics.ReportError(
                    code: PuckDiagnosticCodes.ImportGraph,
                    message: $"Circular import dependency detected: {cycle}",
                    span: importNode.Span
                );
                success = false;

                continue;
            }

            if (activeChain.Count >= DocumentEvaluationBudget.DepthLimit) {
                diagnostics.ReportError(
                    code: PuckDiagnosticCodes.EvaluationLimit,
                    message: $"An import graph nests at most {DocumentEvaluationBudget.DepthLimit} source documents.",
                    span: importNode.Span
                );
                success = false;

                continue;
            }

            try {
                var subDoc = context.Read(path: library);
                var nextRelated = new DiagnosticRelated(
                    Message: $"Imported '{importNode.Path}' here.",
                    SourcePath: currentSourcePath,
                    Span: importNode.Span
                );

                if (!TraverseImports(
                    activeChain: activeChain,
                    context: context,
                    currentPath: library,
                    diagnostics: diagnostics,
                    doc: subDoc,
                    importedFrom: nextRelated,
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
    /// <param name="budget">The compile's one evaluation budget, which the walk charges for the declarations it brings
    /// in and the lowering that follows goes on charging; its cancellation token stops the walk.</param>
    /// <param name="sources">The compile session's parses, which every library the walk reads comes through.</param>
    /// <param name="parsed">The number of libraries the walk parsed rather than found held in
    /// <paramref name="sources"/>.</param>
    /// <param name="vocabulary">The vocabulary that resolves an import's name; names are libraries when omitted.</param>
    /// <returns>The root with the imported declarations, or <see langword="null"/> when the graph does not
    /// resolve.</returns>
    public static DocumentNode? ImportModules(
        DocumentNode rootDoc,
        string rootPath,
        DiagnosticBag diagnostics,
        DocumentEvaluationBudget budget,
        PuckSourceTable sources,
        out int parsed,
        IDocumentVocabulary? vocabulary = null
    ) {
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sources);

        var context = new ImportContext(cancellationToken: budget.CancellationToken, sources: sources, vocabulary: vocabulary);

        try {
            return Import(budget: budget, context: context, diagnostics: diagnostics, rootDoc: rootDoc, rootPath: rootPath);
        } finally {
            parsed = context.Parsed;
        }
    }

    private static DocumentNode? Import(DocumentNode rootDoc, string rootPath, DiagnosticBag diagnostics, DocumentEvaluationBudget budget, ImportContext context) {
        if (!ValidateImportGraph(context: context, diagnostics: diagnostics, rootDoc: rootDoc, rootPath: rootPath)) {
            return null;
        }
        var rootDir = (Path.GetDirectoryName(path: Path.GetFullPath(path: rootPath)) ?? "");

        try {
            var cost = MeasureImportedModules(
                context: context,
                currentDir: rootDir,
                document: rootDoc,
                memo: new(comparer: Puck.Abstractions.PuckPaths.Comparer)
            );

            budget.Spend(
                count: cost.Work,
                span: SourceSpan.None
            );
        } catch (DocumentEvaluationException ex) {
            diagnostics.Report(diagnostic: ex.ToDiagnostic());
            return null;
        }
        // A library's import is spent once its declarations are in, so the imports left each name a document.
        return rootDoc with {
            Statements = [
                .. CollectModules(context: context, rootDoc: rootDoc, rootPath: rootPath),
                .. rootDoc.Statements.Where(predicate: statement => !((statement is ImportNode import) && (context.Library(directory: rootDir, import: import) is not null))),
            ],
        };
    }

    private static ImportCost MeasureImportedModules(
        DocumentNode document,
        string currentDir,
        Dictionary<string, ImportCost> memo,
        ImportContext context,
        int traversalDepth = 1,
        string? currentSourcePath = null
    ) {
        if (traversalDepth > DocumentEvaluationBudget.DepthLimit) {
            throw new DocumentEvaluationException(
                $"An import graph nests at most {DocumentEvaluationBudget.DepthLimit} source documents.",
                document.Span
            ) {
                SourceAttributed = true,
                SourcePath = currentSourcePath,
            };
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
                var descendants = MeasureImportedModules(imported, (Path.GetDirectoryName(path: path) ?? ""), memo, context, (traversalDepth + 1), currentSourcePath: path);
                var ownRewriteWork = ((ownDeclarations == 0)
                    ? 0L
                    : Math.Max(val1: 1, val2: context.SourceLength(path: path))
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
                ) {
                    SourceAttributed = true,
                    SourcePath = currentSourcePath,
                };
            }
            declarations = AddBounded(left: declarations, right: child.Declarations);
            rewriteWork = AddBounded(left: rewriteWork, right: child.RewriteWork);
            work = AddBounded(left: work, right: child.Work);
            if (import.Alias is not null) { work = AddBounded(left: work, right: child.RewriteWork); }
            depth = Math.Max(val1: depth, val2: checked((child.Depth + 1)));
        }
        return new ImportCost(Declarations: declarations, Depth: depth, RewriteWork: rewriteWork, Work: work);
    }
    /// <summary>Returns the <c>let</c> and template declarations the libraries <paramref name="rootDoc"/> imports hand
    /// it, named as its compile names them: each under the aliases it was imported through, and carrying the path of
    /// the library that declares it.</summary>
    /// <param name="rootDoc">The importing source's parsed tree.</param>
    /// <param name="rootPath">The importing source's path; its imports resolve beside it.</param>
    /// <param name="sources">The compile session's parses, which every library the walk reads comes through.</param>
    /// <param name="vocabulary">The vocabulary that resolves an import's name; names are libraries when omitted.</param>
    /// <param name="cancellationToken">Stops the walk.</param>
    /// <returns>The declarations, a library's own imports' ahead of its own. An import that names nothing, a library
    /// that does not parse and an import that would close a cycle hand over nothing, so an editor still reads the rest
    /// of a graph a compile refuses.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rootDoc"/>, <paramref name="rootPath"/> or
    /// <paramref name="sources"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<StatementNode> ImportedDeclarations(
        DocumentNode rootDoc,
        string rootPath,
        PuckSourceTable sources,
        IDocumentVocabulary? vocabulary = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(rootDoc);
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(sources);

        return CollectModules(context: new ImportContext(cancellationToken: cancellationToken, sources: sources, vocabulary: vocabulary), rootDoc: rootDoc, rootPath: rootPath);
    }

    private static List<StatementNode> CollectModules(DocumentNode rootDoc, string rootPath, ImportContext context) {
        var fullPath = Path.GetFullPath(path: rootPath);
        var collected = new List<StatementNode>();

        CollectModules(rootDoc, (Path.GetDirectoryName(path: fullPath) ?? ""), "", new(comparer: ModuleImportIdentityComparer.Instance), collected, context, [fullPath]);

        return collected;
    }
    // A library is loaded once per namespace, which is the chain of aliases it was imported through: an import
    // without one adds nothing to the chain, so two unaliased routes to one library under one alias are one load.
    private static void CollectModules(DocumentNode document, string currentDir, string namespacePath, HashSet<ModuleImportIdentity> loaded, List<StatementNode> output, ImportContext context, List<string> chain) {
        foreach (var import in document.Statements.OfType<ImportNode>()) {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (context.Library(directory: currentDir, import: import) is not { } path) { continue; }
            // A compile validates its graph first; an editor's walk of a refused one stops where it would recur.
            if ((chain.Count >= DocumentEvaluationBudget.DepthLimit) || chain.Contains(path, Puck.Abstractions.PuckPaths.Comparer)) { continue; }
            var nextNamespace = ((import.Alias is not { } alias) ? namespacePath : ((namespacePath.Length == 0) ? alias : QualifiedName.Parse(text: namespacePath).Append(member: alias).ToString()));

            if (!loaded.Add(item: new ModuleImportIdentity(Namespace: nextNamespace, Path: path))) { continue; }
            DocumentNode imported;

            try {
                imported = context.Read(path: path);
            } catch (Exception exception) when ((exception is not OperationCanceledException)) {
                continue;
            }
            var nested = new List<StatementNode>();

            chain.Add(item: path);
            CollectModules(imported, (Path.GetDirectoryName(path: path) ?? ""), nextNamespace, loaded, nested, context, chain);
            chain.RemoveAt(index: (chain.Count - 1));
            nested.AddRange(collection: imported.Statements.Where(predicate: static statement => (statement is LetNode or TemplateNode))
                .Select(selector: statement => (statement switch {
                    TemplateNode template => template with { DefinitionPath = path },
                    LetNode let => let with { DefinitionPath = path },
                    _ => statement,
                })));
            output.AddRange(collection: ((import.Alias is null) ? nested : PrefixModuleSymbols(alias: import.Alias, statements: nested)));
        }
    }

    /// <summary>Validates the import dependency graph starting from the root document.</summary>
    /// <param name="rootDoc">The parsed root document node.</param>
    /// <param name="rootPath">The filesystem path to the root document.</param>
    /// <param name="diagnostics">The DiagnosticBag to report resolution errors into.</param>
    /// <param name="sources">The compile session's parses, which every library the walk reads comes through.</param>
    /// <param name="vocabulary">Optional document vocabulary providing schema-specific lexical rules.</param>
    /// <param name="cancellationToken">Stops import traversal.</param>
    /// <returns>True if all imports resolve successfully without cycles.</returns>
    public static bool ValidateImportGraph(
        DocumentNode rootDoc,
        string rootPath,
        DiagnosticBag diagnostics,
        PuckSourceTable sources,
        IDocumentVocabulary? vocabulary = null,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(rootDoc);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(sources);

        var context = new ImportContext(cancellationToken: cancellationToken, sources: sources, vocabulary: vocabulary);

        return ValidateImportGraph(context: context, diagnostics: diagnostics, rootDoc: rootDoc, rootPath: rootPath);
    }

    private static bool ValidateImportGraph(DocumentNode rootDoc, string rootPath, DiagnosticBag diagnostics, ImportContext context) {
        var visited = new HashSet<string>(comparer: Puck.Abstractions.PuckPaths.Comparer);
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

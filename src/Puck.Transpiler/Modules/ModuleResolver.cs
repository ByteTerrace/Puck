using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Parsing;

namespace Puck.Transpiler.Modules;

/// <summary>Resolves multi-document import dependency graphs, detects cycles, and supports optional bundling.</summary>
public static class ModuleResolver {
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
                    if (File.Exists(path: resolvedTarget)) {
                        var subText = File.ReadAllText(path: resolvedTarget);
                        var subDoc = PuckParser.ParseDocument(source: subText, vocabulary: vocabulary);
                        var subDir = (Path.GetDirectoryName(path: resolvedTarget) ?? "");

                        CollectStatements(
                            currentDir: subDir,
                            diagnostics: diagnostics,
                            doc: subDoc,
                            loadedFiles: loadedFiles,
                            outputStatements: outputStatements,
                            vocabulary: vocabulary
                        );
                    }
                }
            } else {
                outputStatements.Add(item: stmt);
            }
        }
    }
    private static bool TraverseImports(
        DocumentNode doc,
        string currentPath,
        HashSet<string> visited,
        List<string> activeChain,
        DiagnosticBag diagnostics,
        IDocumentVocabulary? vocabulary = null
    ) {
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

                if (!File.Exists(path: resolvedTarget)) {
                    diagnostics.ReportError(
                        code: PuckDiagnosticCodes.ImportTargetMissing,
                        message: $"Imported document '{importNode.Path}' could not be found at '{resolvedTarget}'.",
                        span: importNode.Span
                    );
                    success = false;
                    continue;
                }

                // If importing another .puck document, parse and recurse
                if (resolvedTarget.EndsWith(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: ".puck"
                )) {
                    try {
                        var text = File.ReadAllText(path: resolvedTarget);
                        var subDoc = PuckParser.ParseDocument(source: text, vocabulary: vocabulary);

                        if (!TraverseImports(
                            activeChain: activeChain,
                            currentPath: resolvedTarget,
                            diagnostics: diagnostics,
                            doc: subDoc,
                            visited: visited,
                            vocabulary: vocabulary
                        )) {
                            success = false;
                        }
                    } catch (Exception ex) {
                        diagnostics.ReportError(
                            code: PuckDiagnosticCodes.ImportGraph,
                            message: $"Failed to parse imported document '{importNode.Path}': {ex.Message}",
                            span: importNode.Span
                        );
                        success = false;
                    }
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
    /// <returns>A bundled DocumentNode with inlined statements, or null if errors occurred.</returns>
    public static DocumentNode? BundleDocument(
        DocumentNode rootDoc,
        string rootPath,
        DiagnosticBag diagnostics,
        IDocumentVocabulary? vocabulary = null
    ) {
        if (!ValidateImportGraph(
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
    /// <summary>Validates the import dependency graph starting from the root document.</summary>
    /// <param name="rootDoc">The parsed root document node.</param>
    /// <param name="rootPath">The filesystem path to the root document.</param>
    /// <param name="diagnostics">The DiagnosticBag to report resolution errors into.</param>
    /// <param name="vocabulary">Optional document vocabulary providing schema-specific lexical rules.</param>
    /// <returns>True if all imports resolve successfully without cycles.</returns>
    public static bool ValidateImportGraph(
        DocumentNode rootDoc,
        string rootPath,
        DiagnosticBag diagnostics,
        IDocumentVocabulary? vocabulary = null
    ) {
        ArgumentNullException.ThrowIfNull(rootDoc);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var visited = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var activeChain = new List<string>();

        return TraverseImports(
            activeChain: activeChain,
            currentPath: rootPath,
            diagnostics: diagnostics,
            doc: rootDoc,
            visited: visited,
            vocabulary: vocabulary
        );
    }
}

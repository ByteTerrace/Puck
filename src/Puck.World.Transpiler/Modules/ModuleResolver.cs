using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Parsing;

namespace Puck.World.Transpiler.Modules;

/// <summary>Resolves multi-document import dependency graphs, detects cycles, and supports optional bundling.</summary>
public static class ModuleResolver {
    /// <summary>Validates the import dependency graph starting from the root document.</summary>
    /// <param name="rootDoc">The parsed root document node.</param>
    /// <param name="rootPath">The filesystem path to the root document.</param>
    /// <param name="diagnostics">The DiagnosticBag to report resolution errors into.</param>
    /// <returns>True if all imports resolve successfully without cycles.</returns>
    public static bool ValidateImportGraph(DocumentNode rootDoc, string rootPath, DiagnosticBag diagnostics) {
        ArgumentNullException.ThrowIfNull(rootDoc);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeChain = new List<string>();

        return TraverseImports(rootDoc, rootPath, visited, activeChain, diagnostics);
    }

    /// <summary>Recursively inlines imported AST statements into a single bundled document.</summary>
    /// <param name="rootDoc">The root document AST.</param>
    /// <param name="rootPath">The root document filesystem path.</param>
    /// <param name="diagnostics">The DiagnosticBag to report resolution errors into.</param>
    /// <returns>A bundled DocumentNode with inlined statements, or null if errors occurred.</returns>
    public static DocumentNode? BundleDocument(DocumentNode rootDoc, string rootPath, DiagnosticBag diagnostics) {
        if (!ValidateImportGraph(rootDoc, rootPath, diagnostics)) {
            return null;
        }

        var bundledStatements = new List<StatementNode>();
        var loadedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootDir = Path.GetDirectoryName(Path.GetFullPath(rootPath)) ?? "";

        CollectStatements(rootDoc, rootDir, bundledStatements, loadedFiles, diagnostics);

        return new DocumentNode(
            Schema: rootDoc.Schema,
            Basis: rootDoc.Basis,
            Statements: bundledStatements,
            Offset: rootDoc.Offset,
            Length: rootDoc.Length,
            Line: rootDoc.Line,
            Column: rootDoc.Column
        );
    }

    private static bool TraverseImports(
        DocumentNode doc,
        string currentPath,
        HashSet<string> visited,
        List<string> activeChain,
        DiagnosticBag diagnostics
    ) {
        var fullPath = Path.GetFullPath(currentPath);
        var currentDir = Path.GetDirectoryName(fullPath) ?? "";

        if (activeChain.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) {
            var cycle = string.Join(" -> ", activeChain.Concat([fullPath]).Select(Path.GetFileName));
            diagnostics.ReportError("PUCK015", $"Circular import dependency detected: {cycle}", SourceSpan.None);
            return false;
        }

        if (visited.Contains(fullPath)) {
            return true;
        }

        visited.Add(fullPath);
        activeChain.Add(fullPath);

        var success = true;
        foreach (var stmt in doc.Statements) {
            if (stmt is ImportNode importNode) {
                var resolvedTarget = Path.GetFullPath(Path.Combine(currentDir, importNode.Path));

                if (!File.Exists(resolvedTarget)) {
                    diagnostics.ReportError(
                        "PUCK016",
                        $"Imported document '{importNode.Path}' could not be found at '{resolvedTarget}'.",
                        importNode.Span
                    );
                    success = false;
                    continue;
                }

                // If importing another .puck document, parse and recurse
                if (resolvedTarget.EndsWith(".puck", StringComparison.OrdinalIgnoreCase)) {
                    try {
                        var text = File.ReadAllText(resolvedTarget);
                        var subDoc = PuckParser.ParseDocument(text);
                        if (!TraverseImports(subDoc, resolvedTarget, visited, activeChain, diagnostics)) {
                            success = false;
                        }
                    } catch (Exception ex) {
                        diagnostics.ReportError("PUCK017", $"Failed to parse imported document '{importNode.Path}': {ex.Message}", importNode.Span);
                        success = false;
                    }
                }
            }
        }

        activeChain.RemoveAt(activeChain.Count - 1);
        return success;
    }

    private static void CollectStatements(
        DocumentNode doc,
        string currentDir,
        List<StatementNode> outputStatements,
        HashSet<string> loadedFiles,
        DiagnosticBag diagnostics
    ) {
        foreach (var stmt in doc.Statements) {
            if (stmt is ImportNode importNode) {
                var resolvedTarget = Path.GetFullPath(Path.Combine(currentDir, importNode.Path));

                if (loadedFiles.Add(resolvedTarget) && resolvedTarget.EndsWith(".puck", StringComparison.OrdinalIgnoreCase)) {
                    if (File.Exists(resolvedTarget)) {
                        var subText = File.ReadAllText(resolvedTarget);
                        var subDoc = PuckParser.ParseDocument(subText);
                        var subDir = Path.GetDirectoryName(resolvedTarget) ?? "";
                        CollectStatements(subDoc, subDir, outputStatements, loadedFiles, diagnostics);
                    }
                }
            } else {
                outputStatements.Add(stmt);
            }
        }
    }
}

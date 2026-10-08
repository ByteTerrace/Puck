using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Puck.Cli.Format.Rewriters;

namespace Puck.Cli.Format;

// The semantic `named-args` pass, run by SemanticPhases against each target file's owning-project compilation (so a
// call's method symbol — framework or in-repo — resolves against that project's real compile closure), then
// NamedArgsRewriter against each file's semantic model. Symbol binding tolerates unrelated errors elsewhere, so a call
// is named whenever its own method resolves. Preserves source newline trivia like SourceRewrite; --check
// reports drift only.
internal static class NamedArgsPhase {
    // Coverage probe: a call whose symbol binds to nothing (no symbol, no candidate) is one named-args
    // must leave positional. A nonzero count in a project whose closure was accepted means the compilation
    // is missing something the build has: generator output the project does not emit to disk, or an assembly
    // MSBuild dropped from the closure because it could not resolve it.
    // `nameof(...)` is syntactically an invocation but a contextual operator with no method symbol, so it
    // is excluded — counting it would be a false positive.
    private static int CountUnresolvedCalls(SyntaxNode root, SemanticModel model) =>
        root.DescendantNodes().Count(predicate: node =>
            ((node is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)
            && (node is not InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
            && (model.GetSymbolInfo(node: node) is { Symbol: null, CandidateSymbols.IsEmpty: true })));

    // Names the target files of one project against its compilation, accumulating drift, corruption and the count of
    // calls that could not be resolved (left positional) into `outcome`.
    internal static void Process(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> treesByPath,
        IEnumerable<string> targets,
        bool check,
        SemanticOutcome outcome
    ) {
        foreach (var file in targets) {
            if (!treesByPath.TryGetValue(
                key: Path.GetFullPath(path: file),
                value: out var tree
            )) {
                continue;
            }

            var model = compilation.GetSemanticModel(syntaxTree: tree);

            outcome.Unresolved += CountUnresolvedCalls(
                root: tree.GetRoot(),
                model: model
            );

            var rewritten = new NamedArgsRewriter(model: model).Visit(node: tree.GetRoot())!.ToFullString();
            var original = File.ReadAllText(path: file);

            if (RewriteIo.ContentEquals(
                a: rewritten,
                b: original
            )) {
                continue;
            }

            var relative = CliPaths.ToDisplay(fullPath: file);

            if (RewriteIo.HasSyntaxErrors(
                original: original,
                rewritten: rewritten
            )) {
                outcome.Corrupted.Add(item: relative);

                continue;
            }

            outcome.Drifted.Add(item: relative);

            // named-args only adds names where absent, so a second run is a no-op — idempotent by
            // construction. --check still audits (reports drift, never writes) plus the guard.
            if (
                !check
            ) {
                RewriteIo.WriteText(
                    file: file,
                    text: rewritten
                );
            }
        }
    }
    internal static int Report(SemanticOutcome outcome, string configuration, int fileCount, bool check) {
        // A file with no owning project is counted and fails the run, so a clean report never covers source
        // the pass did not examine.
        if (outcome.Ungrouped > 0) {
            Console.Error.WriteLine(value: $"named-args: {outcome.Ungrouped} file(s) are compiled by no project the run found — skipped");
        }

        foreach (var (project, reason) in outcome.RefusedProjects) {
            Console.Error.WriteLine(value: $"named-args: {project}: {reason} — its files were left alone. Build it in {configuration} and run again.");
        }

        // Only files that were fully analysed reach this count, so it cannot point at a refused project for
        // its explanation: a call left positional in a bound project is a reference the closure missed.
        if (outcome.Unresolved > 0) {
            Console.Error.WriteLine(value: $"named-args: {outcome.Unresolved} call(s) could not be resolved and were left positional (their types come from a source the compilation does not carry).");
        }

        return Math.Max(
            val1: ((outcome.Ungrouped > 0)
            ? 1
            : 0),
            val2: RewriteIo.Report(
                label: "named-args",
                fileCount: fileCount,
                drifted: outcome.Drifted,
                check: check,
                problems: [
                ("have syntax errors before or after rewriting — SKIPPED", outcome.Corrupted),
                ($"belong to a project whose {configuration} compile closure is not trustworthy — SKIPPED", outcome.RefusedFiles),
            ]
            )
        );
    }
}

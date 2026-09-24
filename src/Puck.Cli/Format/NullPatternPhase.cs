using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Format.Rewriters;

namespace Puck.Cli.Format;

// Compiler-backed pass behind `null-pattern`, run by SemanticPhases against each project's shared compilation. A
// syntax-only rewrite cannot distinguish reference equality from an overloaded operator, dynamic binding, or a pointer
// comparison; the semantic model supplies that boundary before any source is changed.
internal static class NullPatternPhase {
    private static string Apply(SyntaxTree tree, CSharpCompilation compilation) {
        var model = compilation.GetSemanticModel(syntaxTree: tree);

        return new NullPatternRewriter(model: model).Visit(node: tree.GetRoot())!.ToFullString();
    }

    // Rewrites one project's target files against its compilation, accumulating drift, corruption and non-convergence
    // into `outcome`. Returns each file it wrote with the text it wrote, so the caller can carry the compilation forward.
    internal static List<(string Path, string Text)> Process(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, SyntaxTree> treesByPath,
        IEnumerable<string> targets,
        CSharpParseOptions parseOptions,
        bool check,
        SemanticOutcome outcome
    ) {
        var written = new List<(string Path, string Text)>();

        foreach (var file in targets) {
            var path = Path.GetFullPath(path: file);

            if (!treesByPath.TryGetValue(
                key: path,
                value: out var tree
            )) {
                continue;
            }

            var original = File.ReadAllText(path: file);
            var rewritten = Apply(
                compilation: compilation,
                tree: tree
            );

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

            if (check) {
                var secondTree = CSharpSyntaxTree.ParseText(
                    text: rewritten,
                    options: parseOptions,
                    path: file
                );
                var secondCompilation = compilation.ReplaceSyntaxTree(
                    newTree: secondTree,
                    oldTree: tree
                );

                if (!RewriteIo.ContentEquals(
                    a: Apply(
                        compilation: secondCompilation,
                        tree: secondTree
                    ),
                    b: rewritten
                )) {
                    outcome.NonConvergent.Add(item: relative);

                    continue;
                }
            }

            outcome.Drifted.Add(item: relative);

            if (
                !check
            ) {
                RewriteIo.WriteText(
                    file: file,
                    text: rewritten
                );
                written.Add(item: (path, rewritten));
            }
        }

        return written;
    }
    internal static int Report(SemanticOutcome outcome, string configuration, int fileCount, bool check) {
        if (outcome.Ungrouped > 0) {
            Console.Error.WriteLine(value: $"null-pattern: {outcome.Ungrouped} file(s) are compiled by no project the run found — skipped");
        }

        foreach (var (project, reason) in outcome.RefusedProjects) {
            Console.Error.WriteLine(value: $"null-pattern: {project}: {reason} — its source was skipped. Build it in {configuration} before formatting.");
        }

        return Math.Max(
            val1: (((outcome.Ungrouped > 0) || (outcome.RefusedProjects.Count > 0))
            ? 1
            : 0),
            val2: RewriteIo.Report(
                label: "null-pattern",
                fileCount: fileCount,
                drifted: outcome.Drifted,
                check: check,
                problems: [
                ("have syntax errors before or after rewriting — SKIPPED", outcome.Corrupted),
                ("do not converge — SKIPPED", outcome.NonConvergent),
            ]
            )
        );
    }
}

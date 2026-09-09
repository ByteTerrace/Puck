using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Format.Rewriters;
using Puck.Cli.Source;

namespace Puck.Cli.Format;

// Compiler-backed disk phase for `null-pattern`. A syntax-only rewrite cannot distinguish reference
// equality from an overloaded operator, dynamic binding, or a pointer comparison; the semantic model
// supplies that boundary before any source is changed.
internal static class NullPatternPhase {
    public static int Run(string rootArgument, bool whatIf, bool verify) {
        if (!SourceFiles.TryEnumerate(files: out var targetFiles, rootArgument: rootArgument, scanRoot: out _)) {
            return 2;
        }

        var parseOptions = new CSharpParseOptions(languageVersion: LanguageVersion.Preview);
        var byProject = targetFiles.GroupBy(
            keySelector: static file => (SourceFiles.FindOwningProjectDirectory(start: Path.GetDirectoryName(path: Path.GetFullPath(path: file))!) ?? ""),
            comparer: StringComparer.OrdinalIgnoreCase);
        var drifted = new List<string>();
        var corrupted = new List<string>();
        var nonConvergent = new List<string>();
        var degradedProjects = new List<string>();
        var ungrouped = 0;

        foreach (var projectGroup in byProject) {
            if ((projectGroup.Key.Length == 0)
                || !SourceFiles.TryEnumerate(rootArgument: projectGroup.Key, scanRoot: out _, files: out var compilationFiles)) {
                ungrouped += projectGroup.Count();

                continue;
            }

            ProcessProject(
                projectRoot: projectGroup.Key,
                targets: projectGroup,
                compilationFiles: compilationFiles,
                parseOptions: parseOptions,
                whatIf: whatIf,
                verify: verify,
                drifted: drifted,
                corrupted: corrupted,
                nonConvergent: nonConvergent,
                degradedProjects: degradedProjects);
        }

        if (ungrouped > 0) {
            Console.Error.WriteLine(value: $"null-pattern: {ungrouped} file(s) had no owning project — skipped");
        }

        if (degradedProjects.Count > 0) {
            Console.Error.WriteLine(
                value: $"null-pattern: {degradedProjects.Count} project(s) not built ({string.Join(separator: ", ", values: degradedProjects)}) — unresolved comparisons stay unchanged. Build for full coverage.");
        }

        return RewriteIo.Report(
            label: "null-pattern",
            fileCount: targetFiles.Length,
            drifted: drifted,
            whatIf: (whatIf || verify),
            problems: [
                ("have syntax errors before or after rewriting — SKIPPED", corrupted),
                ("do not converge — SKIPPED", nonConvergent),
            ]);
    }

    private static void ProcessProject(
        string projectRoot,
        IEnumerable<string> targets,
        string[] compilationFiles,
        CSharpParseOptions parseOptions,
        bool whatIf,
        bool verify,
        List<string> drifted,
        List<string> corrupted,
        List<string> nonConvergent,
        List<string> degradedProjects
    ) {
        var treesByPath = new Dictionary<string, SyntaxTree>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var file in compilationFiles) {
            treesByPath[Path.GetFullPath(path: file)] = CSharpSyntaxTree.ParseText(text: File.ReadAllText(path: file), options: parseOptions, path: file);
        }

        var compilation = NamedArgsPhase.BuildProjectCompilation(
            projectRoot: projectRoot,
            trees: treesByPath.Values,
            parseOptions: parseOptions,
            degraded: out var degraded);

        if (degraded) {
            degradedProjects.Add(item: Path.GetFileName(path: projectRoot));
        }

        foreach (var file in targets) {
            if (!treesByPath.TryGetValue(key: Path.GetFullPath(path: file), value: out var tree)) {
                continue;
            }

            var original = File.ReadAllText(path: file);
            var rewritten = Apply(compilation: compilation, tree: tree);

            if (RewriteIo.ContentEquals(a: rewritten, b: original)) {
                continue;
            }

            var relative = CliPaths.ToDisplay(fullPath: file);

            if (RewriteIo.HasSyntaxErrors(original: original, rewritten: rewritten)) {
                corrupted.Add(item: relative);

                continue;
            }

            if (verify) {
                var secondTree = CSharpSyntaxTree.ParseText(text: rewritten, options: parseOptions, path: file);
                var secondCompilation = compilation.ReplaceSyntaxTree(newTree: secondTree, oldTree: tree);

                if (!RewriteIo.ContentEquals(a: Apply(compilation: secondCompilation, tree: secondTree), b: rewritten)) {
                    nonConvergent.Add(item: relative);

                    continue;
                }
            }

            drifted.Add(item: relative);

            if (!whatIf && !verify) {
                RewriteIo.WriteText(file: file, text: rewritten);
            }
        }
    }
    private static string Apply(SyntaxTree tree, CSharpCompilation compilation) {
        var model = compilation.GetSemanticModel(syntaxTree: tree);

        return new NullPatternRewriter(model: model).Visit(node: tree.GetRoot())!.ToFullString();
    }
}

using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Source;

namespace Puck.Cli.Format;

// The enumerate / parse / rewrite / check-or-write / drift-summary skeleton behind the syntactic format
// passes. Passes run in sequence with a re-parse between them, so chaining is identical to running them
// back to back — except each file is written once.
internal static class SourceRewrite {
    public static int Run(string label, string rootArgument, bool whatIf, bool verify, IReadOnlyList<FormatPass> passes) {
        if (!SourceFiles.TryEnumerate(rootArgument: rootArgument, scanRoot: out _, files: out var files)) {
            return 2;
        }

        // -Verify audits the passes without touching the tree: it asserts every rewrite is a fixed point
        // (a formatter run twice must equal running it once), and never writes.
        var writing = (!whatIf && !verify);
        var drifted = new List<string>();
        var corrupted = new List<string>();
        var nonConvergent = new List<string>();

        foreach (var file in files) {
            var original = File.ReadAllText(path: file);
            var current = ApplyAll(text: original, passes: passes);

            if (RewriteIo.ContentEquals(a: current, b: original)) {
                continue;
            }

            var relative = CliPaths.ToDisplay(fullPath: file);

            if (RewriteIo.IntroducesErrors(original: original, rewritten: current)) {
                corrupted.Add(item: relative);

                continue;
            }

            if (verify && !RewriteIo.ContentEquals(a: ApplyAll(text: current, passes: passes), b: current)) {
                nonConvergent.Add(item: relative);

                continue;
            }

            drifted.Add(item: relative);

            if (writing) {
                RewriteIo.WriteCrlf(file: file, text: current);
            }
        }

        return RewriteIo.Report(
            label: label,
            fileCount: files.Length,
            drifted: drifted,
            whatIf: (whatIf || verify),
            problems: [
                ("would introduce syntax errors — SKIPPED", corrupted),
                ("do not converge (a pass is not idempotent) — SKIPPED", nonConvergent),
            ]);
    }

    // Applies the pass pipeline once, re-parsing between passes so each sees the prior's output.
    private static string ApplyAll(string text, IReadOnlyList<FormatPass> passes) {
        foreach (var pass in passes) {
            var node = CSharpSyntaxTree.ParseText(text: text).GetRoot();

            text = pass.Apply!(arg: node).ToFullString();
        }

        return text;
    }
}

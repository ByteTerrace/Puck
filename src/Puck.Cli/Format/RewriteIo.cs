using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Puck.Cli.Format;

// Shared IO and safety for the disk rewrite phases (SourceRewrite and NamedArgsPhase), so the drift
// tracking, the write guard, source-preserving write, and the summary live once.
internal static class RewriteIo {
    // Newline-insensitive equality, so a pass that only reflows whitespace reads as a no-op regardless
    // of the working tree's line endings.
    public static bool ContentEquals(string a, string b) =>
        (a.ReplaceLineEndings(replacementText: "\n") == b.ReplaceLineEndings(replacementText: "\n"));
    // The write guard: syntactically invalid input is not a safe rewrite target, and valid input must
    // remain valid. Declining an already-invalid file is deliberately stricter than comparing error
    // counts: replacing one diagnostic with another is still corruption.
    public static bool HasSyntaxErrors(string original, string rewritten) =>
        ((ErrorCount(text: original) > 0) || (ErrorCount(text: rewritten) > 0));
    // Roslyn preserves the source's existing newline trivia. Write that text verbatim: normalizing the
    // whole string would also rewrite newlines INSIDE verbatim/raw literals and change runtime values.
    // Phase 0 owns ordinary whitespace and line-ending policy.
    public static void WriteText(string file, string text) =>
        File.WriteAllText(contents: text, path: file);
    // The shared drift/normalize summary plus any number of labelled problem buckets (corruption,
    // non-convergence, ...). Exit code is 1 on any problem or on drift in check mode, else 0.
    public static int Report(string label, int fileCount, IReadOnlyList<string> drifted, bool whatIf, params ReadOnlySpan<(string Reason, IReadOnlyList<string> Files)> problems) {
        Console.Error.WriteLine(
            value: (whatIf
                ? ((drifted.Count == 0) ? $"{label}: consistent across {fileCount} files." : $"{label}: {drifted.Count} file(s) drifted from the convention:")
                : $"{label}: normalized {drifted.Count} of {fileCount} files."));

        foreach (var path in drifted) {
            Console.Error.WriteLine(value: $"  {path}");
        }

        var hadProblem = false;

        foreach (var (reason, files) in problems) {
            if (files.Count == 0) {
                continue;
            }

            hadProblem = true;
            Console.Error.WriteLine(value: $"{label}: {files.Count} file(s) {reason}:");

            foreach (var path in files) {
                Console.Error.WriteLine(value: $"  {path}");
            }
        }

        return ((hadProblem || (whatIf && (drifted.Count > 0))) ? 1 : 0);
    }

    private static int ErrorCount(string text) =>
        CSharpSyntaxTree.ParseText(text: text).GetDiagnostics().Count(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error));
}

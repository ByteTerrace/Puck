using Puck.Cli.Transpiler;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Formatting;

namespace Puck.Cli.Format;

// The `puck` pass: every selected .puck source is parsed and printed back by PuckPrinter, the one printer the language
// server's formatting request uses as well. A source that does not parse is refused by name and left alone, which
// fails the run with exit 2 in every mode; drift under --check exits 1.
internal static class PuckSourcePhase {
    public static int Run(IReadOnlyList<string> files, bool check) {
        var drifted = new List<string>();
        var refused = 0;

        foreach (var file in files) {
            var display = CliPaths.ToDisplay(fullPath: file);
            string original;

            try {
                original = File.ReadAllText(path: file);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                refused++;
                CliExit.Refuse(
                    verb: "format",
                    what: display,
                    why: exception.Message
                );

                continue;
            }

            var result = PuckPrinter.Format(
                source: original,
                vocabulary: CliVocabularyResolver.Instance.Resolve(source: original)
            );

            if (result.Value is not { } formatted) {
                refused++;

                foreach (var diagnostic in result.Diagnostics.Where(predicate: static diagnostic => (diagnostic.Severity == DiagnosticSeverity.Error))) {
                    CliExit.Refuse(
                        verb: "format",
                        what: $"{display}({diagnostic.Span.Line},{diagnostic.Span.Column})",
                        why: $"{diagnostic.Code}: {diagnostic.Message}"
                    );
                }

                continue;
            }

            if (string.Equals(
                a: original,
                b: formatted,
                comparisonType: StringComparison.Ordinal
            )) {
                continue;
            }

            drifted.Add(item: display);

            if (!check) {
                RewriteIo.WriteText(
                    file: file,
                    text: formatted
                );
            }
        }

        var code = RewriteIo.Report(
            check: check,
            drifted: drifted,
            fileCount: files.Count,
            label: "puck",
            problems: []
        );

        return ((refused > 0)
            ? CliExit.Refused
            : code
        );
    }
}

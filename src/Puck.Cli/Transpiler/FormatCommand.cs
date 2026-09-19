using System.CommandLine;
using Puck.Transpiler.Formatting;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck fmt</c> — formats Puck DSL files according to opinionated repository style.</summary>
internal static class PuckFmtCommand {
    private static int FormatFile(string filePath, bool check, int tabSize, bool insertSpaces) {
        string original;

        try {
            original = File.ReadAllText(path: filePath);
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Could not read file '{filePath}': {ex.Message}");
            return 2;
        }

        var result = PuckPrinter.Format(
            options: new PuckPrintOptions { InsertSpaces = insertSpaces, TabSize = tabSize },
            source: original,
            vocabulary: CliVocabularyResolver.Instance.Resolve(source: original)
        );

        if (result.Value is not { } formatted) {
            foreach (var diagnostic in result.Diagnostics.Where(predicate: static diagnostic => (diagnostic.Severity == Puck.Transpiler.Diagnostics.DiagnosticSeverity.Error))) {
                Console.Error.WriteLine(value: $"error: {filePath}({diagnostic.Span.Line},{diagnostic.Span.Column}): {diagnostic.Code}: {diagnostic.Message}");
            }
            return 2;
        }

        if (string.Equals(
            a: original,
            b: formatted,
            comparisonType: StringComparison.Ordinal
        )) {
            return 0;
        }

        if (check) {
            Console.WriteLine(value: $"unformatted: {filePath}");
            return 1;
        }

        try {
            File.WriteAllText(
                contents: formatted,
                path: filePath
            );
            Console.WriteLine(value: $"formatted: {filePath}");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Could not write file '{filePath}': {ex.Message}");
            return 2;
        }
    }

    public static Command Create() {
        var pathArgument = new Argument<string>(name: "path") {
            Description = "The .puck file or directory to format.",
        };

        var checkOption = new Option<bool>(
            name: "--check",
            aliases: ["-c"]
        ) {
            Description = "Check if files are formatted without modifying them (exit code 1 if unformatted).",
        };

        var indentOption = new Option<int>("--indent-size") { DefaultValueFactory = _ => 2, Description = "Spaces per indentation level (positive integer)." };
        var tabsOption = new Option<bool>("--tabs") { Description = "Indent with tabs instead of spaces." };

        var command = new Command(
            description: "Format Puck source files (.puck) according to opinionated style rules (2-space indent, expanded objects).",
            name: "fmt"
        ) {
            pathArgument,
            checkOption,
            indentOption,
            tabsOption,
        };

        command.SetAction(action: parseResult => {
            var path = parseResult.GetValue(argument: pathArgument)!;
            var check = parseResult.GetValue(option: checkOption);

            return Execute(
                path: path,
                check: check,
                tabSize: parseResult.GetValue(option: indentOption),
                insertSpaces: !parseResult.GetValue(option: tabsOption)
            );
        });

        return command;
    }
    public static int Execute(string path, bool check, int tabSize = 2, bool insertSpaces = true) {
        if (tabSize <= 0) { Console.Error.WriteLine(value: "error: --indent-size must be positive."); return 2; }
        var fullPath = Path.GetFullPath(path: path);

        if (Directory.Exists(path: fullPath)) {
            var files = Directory.GetFiles(
                path: fullPath,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.puck"
            );
            var unformattedCount = 0;
            var failedCount = 0;

            foreach (var file in files) {
                var code = FormatFile(
                    check: check,
                    filePath: file,
                    insertSpaces: insertSpaces,
                    tabSize: tabSize
                );

                if (code == 1) { unformattedCount++; } else if (code != 0) { failedCount++; }
            }

            // A file the verb could not read or could not parse is a failure whatever mode it ran in: the sweep
            // reports it and exits non-zero rather than leaving the caller to believe the tree was swept.
            if (failedCount > 0) {
                Console.Error.WriteLine(value: $"fmt: {failedCount} file(s) could not be formatted.");
                return 2;
            }
            if (
                check &&
                (unformattedCount > 0)
            ) {
                Console.Error.WriteLine(value: $"fmt: {unformattedCount} file(s) require formatting.");
                return 1;
            }

            return 0;
        }

        if (File.Exists(path: fullPath)) {
            return FormatFile(
                check: check,
                filePath: fullPath,
                insertSpaces: insertSpaces,
                tabSize: tabSize
            );
        }

        Console.Error.WriteLine(value: $"error: Path '{path}' not found.");
        return 2;
    }
}

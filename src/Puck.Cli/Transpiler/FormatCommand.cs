using System.CommandLine;
using Puck.Transpiler.Formatting;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck fmt</c> — formats Puck DSL files according to opinionated repository style.</summary>
internal static class PuckFmtCommand {
    public static Command Create() {
        var pathArgument = new Argument<string>(
            name: "path"
        ) {
            Description = "The .puck file or directory to format."
        };

        var checkOption = new Option<bool>(
            name: "--check",
            aliases: ["-c"]
        ) {
            Description = "Check if files are formatted without modifying them (exit code 1 if unformatted)."
        };

        var indentOption = new Option<int>("--indent-size") { DefaultValueFactory = _ => 2, Description = "Spaces per indentation level (positive integer)." };
        var tabsOption = new Option<bool>("--tabs") { Description = "Indent with tabs instead of spaces." };

        var command = new Command(
            name: "fmt",
            description: "Format Puck source files (.puck) according to opinionated style rules (2-space indent, expanded objects)."
        ) {
            pathArgument,
            checkOption,
            indentOption,
            tabsOption
        };

        command.SetAction(parseResult => {
            var path = parseResult.GetValue(pathArgument)!;
            var check = parseResult.GetValue(checkOption);

            return Execute(path: path, check: check, tabSize: parseResult.GetValue(indentOption), insertSpaces: !parseResult.GetValue(tabsOption));
        });

        return command;
    }

    public static int Execute(string path, bool check, int tabSize = 2, bool insertSpaces = true) {
        if (tabSize <= 0) { Console.Error.WriteLine("error: --indent-size must be positive."); return 2; }
        var fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath)) {
            var files = Directory.GetFiles(fullPath, "*.puck", SearchOption.AllDirectories);
            var unformattedCount = 0;

            foreach (var file in files) {
                var code = FormatFile(file, check, tabSize, insertSpaces);
                if (code != 0) {
                    unformattedCount++;
                }
            }

            if (check && unformattedCount > 0) {
                Console.Error.WriteLine($"fmt: {unformattedCount} file(s) require formatting.");
                return 1;
            }

            return 0;
        }

        if (File.Exists(fullPath)) {
            return FormatFile(fullPath, check, tabSize, insertSpaces);
        }

        Console.Error.WriteLine($"error: Path '{path}' not found.");
        return 2;
    }

    private static int FormatFile(string filePath, bool check, int tabSize, bool insertSpaces) {
        string original;
        try {
            original = File.ReadAllText(filePath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: Could not read file '{filePath}': {ex.Message}");
            return 2;
        }

        var formatted = PuckFormatter.Format(original, tabSize, insertSpaces);

        if (string.Equals(original, formatted, StringComparison.Ordinal)) {
            return 0;
        }

        if (check) {
            Console.WriteLine($"unformatted: {filePath}");
            return 1;
        }

        try {
            File.WriteAllText(filePath, formatted);
            Console.WriteLine($"formatted: {filePath}");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: Could not write file '{filePath}': {ex.Message}");
            return 2;
        }
    }
}

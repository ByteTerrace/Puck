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

        var command = new Command(
            name: "fmt",
            description: "Format Puck source files (.puck) according to opinionated style rules (4-space indent, Egyptian braces)."
        ) {
            pathArgument,
            checkOption
        };

        command.SetAction(parseResult => {
            var path = parseResult.GetValue(pathArgument)!;
            var check = parseResult.GetValue(checkOption);

            return Execute(path: path, check: check);
        });

        return command;
    }

    public static int Execute(string path, bool check) {
        var fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath)) {
            var files = Directory.GetFiles(fullPath, "*.puck", SearchOption.AllDirectories);
            var unformattedCount = 0;

            foreach (var file in files) {
                var code = FormatFile(file, check);
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
            return FormatFile(fullPath, check);
        }

        Console.Error.WriteLine($"error: Path '{path}' not found.");
        return 2;
    }

    private static int FormatFile(string filePath, bool check) {
        string original;
        try {
            original = File.ReadAllText(filePath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: Could not read file '{filePath}': {ex.Message}");
            return 2;
        }

        var formatted = PuckFormatter.Format(original);

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

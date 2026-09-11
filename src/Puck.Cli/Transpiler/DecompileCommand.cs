using System.CommandLine;
using Puck.World.Transpiler.Decompiler;

namespace Puck.Cli.Transpiler;

/// <summary>
/// The <c>puck decompile</c> verb: decompiles a canonical JSON world definition into idiomatic <c>.puck</c> DSL source code.
/// </summary>
internal static class DecompileCommand {
    public static Command Create() {
        var pathArgument = new Argument<string>(name: "path") { Description = "Path to the JSON world definition file to decompile." };
        var outputOption = new Option<string?>(name: "--output", aliases: ["-o"]) { Description = "Destination output .puck path (defaults to <path>.puck)." };
        var overwriteOption = new Option<bool>(name: "--overwrite") { Description = "Overwrite destination file if it already exists." };

        var command = new Command(description: "Decompile a JSON world definition into idiomatic .puck DSL source.", name: "decompile") {
            pathArgument,
            outputOption,
            overwriteOption,
        };

        command.SetAction(action: parseResult => Run(
            output: parseResult.GetValue(option: outputOption),
            overwrite: parseResult.GetValue(option: overwriteOption),
            path: parseResult.GetRequiredValue(argument: pathArgument)
        ));

        return command;
    }

    internal static int Run(
        string path,
        string? output,
        bool overwrite
    ) {
        var fullPath = Path.GetFullPath(path: path);

        if (!File.Exists(path: fullPath)) {
            Console.Error.WriteLine(value: $"error: JSON world definition file not found: '{fullPath}'");
            return 2;
        }

        var outputPath = output is not null
            ? Path.GetFullPath(path: output)
            : ComputeDefaultOutputPath(sourcePath: fullPath);

        if (File.Exists(path: outputPath) && !overwrite) {
            Console.Error.WriteLine(value: $"error: Destination file '{outputPath}' already exists. Use --overwrite to overwrite.");
            return 1;
        }

        string json;

        try {
            json = File.ReadAllText(path: fullPath);
        }
        catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Could not read JSON file '{fullPath}': {ex.Message}");
            return 2;
        }

        string puckSource;

        try {
            puckSource = WorldDecompiler.Decompile(jsonText: json);
        }
        catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Failed to decompile world definition '{fullPath}': {ex.Message}");
            return 1;
        }

        try {
            var outputDirectory = Path.GetDirectoryName(path: outputPath);

            if (!string.IsNullOrEmpty(value: outputDirectory) && !Directory.Exists(path: outputDirectory)) {
                Directory.CreateDirectory(path: outputDirectory);
            }

            File.WriteAllText(path: outputPath, contents: puckSource);
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(s: puckSource);
            Console.WriteLine(value: $"Successfully decompiled '{Path.GetFileName(path: fullPath)}' -> '{outputPath}' ({byteCount:N0} bytes).");
            return 0;
        }
        catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Failed to write output file '{outputPath}': {ex.Message}");
            return 2;
        }
    }

    private static string ComputeDefaultOutputPath(string sourcePath) {
        var dir = Path.GetDirectoryName(path: sourcePath) ?? "";
        var fileName = Path.GetFileName(path: sourcePath);

        if (fileName.EndsWith(value: ".world.json", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            var baseName = fileName[..^11];

            return Path.Combine(path1: dir, path2: baseName + ".puck");
        }

        if (fileName.EndsWith(value: ".json", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            var baseName = fileName[..^5];

            return Path.Combine(path1: dir, path2: baseName + ".puck");
        }

        return Path.Combine(path1: dir, path2: Path.GetFileNameWithoutExtension(path: sourcePath) + ".puck");
    }
}

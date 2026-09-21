using Puck.World.Machines;
using Puck.World.Transpiler.Validation;
using System.CommandLine;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck lint</c> — runs static analysis and semantic linting over Puck DSL files.</summary>
internal static class LintCommand {
    private static int LintFile(string filePath, bool strict, WorldMachineCatalog machines) {
        var catalogFingerprint = Puck.Cli.CliWorldVocabulary.Fingerprint(catalog: machines);
        string sourceText;

        try {
            sourceText = File.ReadAllText(path: filePath);
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Could not read file '{filePath}': {ex.Message}");
            return 2;
        }

        var diagnostics = WorldSourceDiagnostics.Diagnose(
            catalogFingerprint: catalogFingerprint,
            foreign: Puck.GamingBricks.Transpiler.CartridgeLanguageServices.Diagnose,
            machines: machines,
            source: sourceText,
            sourcePath: filePath,
            vocabularies: CliVocabularyResolver.Instance
        );

        if (diagnostics.Count > 0) {
            Console.WriteLine(value: diagnostics.FormatReport(
                filePath: filePath,
                sourceText: sourceText
            ));
        }

        var hasErrors = diagnostics.HasErrors;
        var hasWarnings = diagnostics.HasWarnings;

        if (
            hasErrors ||
            (strict && hasWarnings)
        ) {
            return 1;
        }

        return 0;
    }

    public static Command Create() {
        var pathArgument = new Argument<string>(name: "path") {
            Description = "The .puck file or directory to lint.",
        };

        var strictOption = new Option<bool>(
            name: "--strict",
            aliases: ["-s"]
        ) {
            Description = "Treat warnings as errors (exit code 1).",
        };

        var command = new Command(
            description: "Run static analysis and semantic linting on Puck source files (.puck).",
            name: "lint"
        ) {
            pathArgument,
            strictOption,
        };

        command.SetAction(action: parseResult => {
            var path = parseResult.GetValue(argument: pathArgument)!;
            var strict = parseResult.GetValue(option: strictOption);

            return Execute(
                path: path,
                strict: strict
            );
        });

        return command;
    }
    public static int Execute(string path, bool strict) {
        var machines = CliWorldVocabulary.EnsureInstalled();
        var fullPath = Path.GetFullPath(path: path);

        if (Directory.Exists(path: fullPath)) {
            var files = Directory.GetFiles(
                path: fullPath,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.puck"
            );
            var failureCount = 0;

            foreach (var file in files) {
                var code = LintFile(
                    filePath: file,
                    machines: machines,
                    strict: strict
                );

                if (code != 0) {
                    failureCount++;
                }
            }

            return ((failureCount > 0)
                ? 1
                : 0
            );
        }

        if (File.Exists(path: fullPath)) {
            return LintFile(
                filePath: fullPath,
                machines: machines,
                strict: strict
            );
        }

        Console.Error.WriteLine(value: $"error: Path '{path}' not found.");
        return 2;
    }
}

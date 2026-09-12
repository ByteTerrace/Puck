using System.CommandLine;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck lint</c> — runs static analysis and semantic linting over Puck DSL files.</summary>
internal static class LintCommand {
    public static Command Create() {
        var pathArgument = new Argument<string>(
            name: "path"
        ) {
            Description = "The .puck file or directory to lint."
        };

        var strictOption = new Option<bool>(
            name: "--strict",
            aliases: ["-s"]
        ) {
            Description = "Treat warnings as errors (exit code 1)."
        };

        var command = new Command(
            name: "lint",
            description: "Run static analysis and semantic linting on Puck source files (.puck)."
        ) {
            pathArgument,
            strictOption
        };

        command.SetAction(parseResult => {
            var path = parseResult.GetValue(pathArgument)!;
            var strict = parseResult.GetValue(strictOption);

            return Execute(path: path, strict: strict);
        });

        return command;
    }

    public static int Execute(string path, bool strict) {
        CliWorldVocabulary.EnsureInstalled();
        var fullPath = Path.GetFullPath(path);

        if (Directory.Exists(fullPath)) {
            var files = Directory.GetFiles(fullPath, "*.puck", SearchOption.AllDirectories);
            var failureCount = 0;

            foreach (var file in files) {
                var code = LintFile(file, strict);
                if (code != 0) {
                    failureCount++;
                }
            }

            return failureCount > 0 ? 1 : 0;
        }

        if (File.Exists(fullPath)) {
            return LintFile(fullPath, strict);
        }

        Console.Error.WriteLine($"error: Path '{path}' not found.");
        return 2;
    }

    private static int LintFile(string filePath, bool strict) {
        string sourceText;
        try {
            sourceText = File.ReadAllText(filePath);
        } catch (Exception ex) {
            Console.Error.WriteLine($"error: Could not read file '{filePath}': {ex.Message}");
            return 2;
        }

        var diagnostics = new DiagnosticBag();
        var parseResult = PuckParser.ParseDocumentWithDiagnostics(sourceText, diagnostics: diagnostics);
        var documentNode = parseResult.Value;

        if (documentNode is not null) {
            PuckLinter.Lint(documentNode, diagnostics);

            // Attempt lowering for semantic check
            var loweringDiags = new DiagnosticBag();
            var sourceMap = new SourceMap();
            var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
                document: documentNode,
                basePath: Path.GetDirectoryName(filePath),
                sourceMap: sourceMap,
                diagnostics: loweringDiags
            );

            diagnostics.AddRange(loweringDiags);

            if (loweringResult.Value is not null && !diagnostics.HasErrors) {
                // Only a root composes to a full engine schema; validating a module as one reports as missing
                // every field whichever root imports it supplies.
                if (WorldSemanticValidator.IsRootDocument(loweringResult.Value)) {
                    WorldSemanticValidator.ValidateComposedWorld(loweringResult.Value, sourceMap, diagnostics, sourcePath: filePath);
                }
                PuckLinter.LintReferences(loweringResult.Value, sourceMap, diagnostics, sourcePath: filePath);
            }
        }

        if (diagnostics.Count > 0) {
            Console.WriteLine(diagnostics.FormatReport(sourceText, filePath));
        }

        var hasErrors = diagnostics.HasErrors;
        var hasWarnings = diagnostics.HasWarnings;

        if (hasErrors || (strict && hasWarnings)) {
            return 1;
        }

        return 0;
    }
}

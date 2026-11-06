using Puck.Cli.Format;
using Puck.World.Machines;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using System.CommandLine;

namespace Puck.Cli.Transpiler;

/// <summary><c>puck lint</c> — runs static analysis and semantic linting over Puck DSL files.</summary>
public static class LintCommand {
    // A finding placed in another file (an import's declaration, a basis's row) is that file's, so a directory lint
    // prints it once, under the first source that composes it, however many sources compose that file.
    private static string ReportedKey(Diagnostic diagnostic) => string.Join(
        separator: '\n',
        diagnostic.Code,
        diagnostic.Severity,
        Puck.Abstractions.PuckPaths.Normalize(path: diagnostic.SourcePath!),
        diagnostic.Span.Offset,
        diagnostic.Message
    );
    private static int LintFile(string filePath, bool strict, WorldMachineCatalog machines, HashSet<string>? reported = null) {
        var catalogFingerprint = Puck.Cli.CliWorldVocabulary.Fingerprint(catalog: machines);
        string sourceText;

        try {
            sourceText = File.ReadAllText(path: filePath);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return CliExit.Refuse(verb: "lint", what: CliPaths.ToDisplay(fullPath: filePath), why: ex.Message);
        }

        var diagnostics = WorldSourceDiagnostics.Diagnose(
            catalogFingerprint: catalogFingerprint,
            foreign: Puck.GamingBricks.Transpiler.CartridgeLanguageServices.Diagnose,
            machines: machines,
            source: sourceText,
            sourcePath: filePath,
            vocabularies: CliVocabularyResolver.Instance
        );

        if (reported is not null) {
            _ = diagnostics.RemoveAll(match: diagnostic => ((diagnostic.SourcePath is not null) && !reported.Add(item: ReportedKey(diagnostic: diagnostic))));
        }

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

        var strictOption = new Option<bool>(name: "--strict") {
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
            ).Order(comparer: StringComparer.Ordinal).ToArray();

            if (files.Length == 0) {
                return CliExit.Empty(verb: "lint", selection: CliPaths.ToDisplay(fullPath: fullPath), why: "holds no .puck source");
            }

            var failureCount = 0;
            var reported = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var file in files) {
                var code = LintFile(
                    filePath: file,
                    machines: machines,
                    reported: reported,
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
            if (!string.Equals(a: Path.GetExtension(path: fullPath), b: ".puck", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return CliExit.Refuse(verb: "lint", what: CliPaths.ToDisplay(fullPath: fullPath), why: "is not a .puck source.");
            }

            return LintFile(
                filePath: fullPath,
                machines: machines,
                strict: strict
            );
        }

        return CliExit.Refuse(verb: "lint", what: CliPaths.ToDisplay(fullPath: fullPath), why: "does not exist.");
    }
}

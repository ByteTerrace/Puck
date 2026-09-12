using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.GamingBricks.Transpiler;
using Puck.World.Transpiler.Lowering;
using Puck.Transpiler.Modules;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;

namespace Puck.Cli.Transpiler;

/// <summary>
/// The <c>puck compile</c> verb: compiles a <c>.puck</c> DSL source file into a canonical JSON world definition.
/// Supports resilient diagnostics, dependency resolution, engine schema validation, and watch mode.
/// </summary>
internal static class CompileCommand {
    public static Command Create() {
        var pathArgument = new Argument<string>(name: "path") { Description = "Path to the .puck source file to compile." };
        var outputOption = new Option<string?>(name: "--output", aliases: ["-o"]) { Description = "Destination JSON path (defaults to .cartridge.json for cartridges, .world.json for worlds)." };
        var watchOption = new Option<bool>(name: "--watch", aliases: ["-w"]) { Description = "Watch the source file and its imported dependencies for changes and recompile automatically." };
        var strictOption = new Option<bool>(name: "--strict") { Description = "Treat warnings as errors." };
        var validateOption = new Option<bool>(name: "--validate") { Description = "Validate semantic engine schema rules on the emitted document." };
        var bundleOption = new Option<bool>(name: "--bundle") { Description = "Inline and bundle all imported .puck module ASTs into a single standalone document." };

        var command = new Command(description: "Compile a .puck source file into a canonical JSON world definition.", name: "compile") {
            pathArgument,
            outputOption,
            watchOption,
            strictOption,
            validateOption,
            bundleOption,
        };

        command.SetAction(action: parseResult => Run(
            bundle: parseResult.GetValue(option: bundleOption),
            output: parseResult.GetValue(option: outputOption),
            path: parseResult.GetRequiredValue(argument: pathArgument),
            strict: parseResult.GetValue(option: strictOption),
            validate: parseResult.GetValue(option: validateOption),
            watch: parseResult.GetValue(option: watchOption)
        ));

        return command;
    }

    internal static int Run(
        string path,
        string? output,
        bool watch,
        bool strict,
        bool validate,
        bool bundle
    ) {
        var fullPath = Path.GetFullPath(path: path);

        if (!File.Exists(path: fullPath)) {
            Console.Error.WriteLine(value: $"error: Source file not found: '{fullPath}'");
            return 2;
        }

        var outputPath = output is not null ? Path.GetFullPath(path: output) : null;

        if (!watch) {
            return ExecuteCompilation(
                bundle: bundle,
                outputPath: outputPath,
                sourcePath: fullPath,
                strict: strict,
                validate: validate
            );
        }

        return RunWatchMode(
            bundle: bundle,
            outputPath: outputPath,
            sourcePath: fullPath,
            strict: strict,
            validate: validate
        );
    }

    private static int ExecuteCompilation(
        string sourcePath,
        string? outputPath,
        bool strict,
        bool validate,
        bool bundle
    ) {
        var diagnostics = new DiagnosticBag();
        string sourceText;

        try {
            sourceText = File.ReadAllText(path: sourcePath);
        }
        catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Could not read source file '{sourcePath}': {ex.Message}");
            return 2;
        }

        var parseResult = PuckParser.ParseDocumentWithDiagnostics(source: sourceText, diagnostics: diagnostics);
        var documentNode = parseResult.Value;

        if (documentNode is null) {
            PrintDiagnostics(diagnostics: diagnostics, filePath: sourcePath, sourceText: sourceText);
            return 1;
        }

        var baseDirectory = Path.GetDirectoryName(path: sourcePath) ?? Directory.GetCurrentDirectory();
        var effectiveAst = documentNode;

        if (bundle) {
            var bundledDoc = ModuleResolver.BundleDocument(
                diagnostics: diagnostics,
                rootDoc: documentNode,
                rootPath: sourcePath
            );

            if (bundledDoc is not null) {
                effectiveAst = bundledDoc;
            }
        }
        else {
            ModuleResolver.ValidateImportGraph(
                diagnostics: diagnostics,
                rootDoc: documentNode,
                rootPath: sourcePath
            );
        }

        var sourceMap = new SourceMap();
        // The document's own `schema:` line picks the vocabulary that knows what its sections mean. The language is
        // the same either way; only the lowering differs.
        var isCartridge = string.Equals(a: effectiveAst.Schema, b: CartridgeVocabulary.Schema, comparisonType: StringComparison.Ordinal);
        var loweringResult = (isCartridge
            ? CartridgeDocumentEmitter.LowerWithDiagnostics(
                diagnostics: diagnostics,
                document: effectiveAst,
                sourceMap: sourceMap
            )
            : WorldDocumentEmitter.LowerWithDiagnostics(
                basePath: baseDirectory,
                diagnostics: diagnostics,
                document: effectiveAst,
                sourceMap: sourceMap
            ));

        var jsonObject = loweringResult.Value ?? new JsonObject();
        var jsonBytes = CanonicalJsonDocument.Serialize(node: jsonObject);

        // A module is a fragment whichever root imports it supplies fields for, so validating one as a world
        // reports refusals that belong to that root, not to this file. `lint` applies the same rule.
        if (validate && !diagnostics.HasErrors && isCartridge) {
            CartridgeLanguageServices.Validate(jsonObject, sourceMap, diagnostics, effectiveAst.Span);
        }

        if (validate && !diagnostics.HasErrors && !isCartridge && WorldSemanticValidator.IsRootDocument(jsonObject)) {
            var machineCatalog = CliWorldVocabulary.EnsureInstalled();
            var catalogFingerprint = CliWorldVocabulary.Fingerprint(machineCatalog);
            WorldSemanticValidator.ValidateComposedWorld(
                machines: machineCatalog,
                diagnostics: diagnostics,
                loweredJson: jsonObject,
                sourceMap: sourceMap,
                sourcePath: sourcePath,
                catalogFingerprint: catalogFingerprint
            );
        }

        PrintDiagnostics(diagnostics: diagnostics, filePath: sourcePath, sourceText: sourceText);

        var hasErrors = diagnostics.HasErrors;
        var hasWarnings = diagnostics.HasWarnings;

        if (hasErrors || (strict && hasWarnings)) {
            var errCount = diagnostics.Count(predicate: d => (d.Severity == DiagnosticSeverity.Error));
            var warnCount = diagnostics.Count(predicate: d => (d.Severity == DiagnosticSeverity.Warning));
            Console.Error.WriteLine(value: $"Compilation failed with {errCount} error(s) and {warnCount} warning(s).");
            return 1;
        }

        try {
            outputPath ??= Path.ChangeExtension(sourcePath, isCartridge ? ".cartridge.json" : ".world.json");
            var outputDirectory = Path.GetDirectoryName(path: outputPath);

            if (!string.IsNullOrEmpty(value: outputDirectory) && !Directory.Exists(path: outputDirectory)) {
                Directory.CreateDirectory(path: outputDirectory);
            }

            File.WriteAllBytes(bytes: jsonBytes, path: outputPath);
            Console.WriteLine(value: $"Successfully compiled '{Path.GetFileName(path: sourcePath)}' -> '{outputPath}' ({jsonBytes.Length:N0} bytes).");
            return 0;
        }
        catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Failed to write output file '{outputPath}': {ex.Message}");
            return 2;
        }
    }

    private static int RunWatchMode(
        string sourcePath,
        string? outputPath,
        bool strict,
        bool validate,
        bool bundle
    ) {
        Console.WriteLine(value: $"[puck watch] Monitoring '{sourcePath}' for changes (Ctrl+C to stop)...");

        // Initial run
        ExecuteCompilation(
            bundle: bundle,
            outputPath: outputPath,
            sourcePath: sourcePath,
            strict: strict,
            validate: validate
        );

        var directory = Path.GetDirectoryName(path: sourcePath) ?? Directory.GetCurrentDirectory();
        using var watcher = new FileSystemWatcher(path: directory) {
            Filter = "*.puck",
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };

        var debounceLock = new object();
        var lastTrigger = DateTime.MinValue;

        void OnChanged(object sender, FileSystemEventArgs e) {
            lock (debounceLock) {
                var now = DateTime.UtcNow;

                if ((now - lastTrigger).TotalMilliseconds < 250) {
                    return;
                }

                lastTrigger = now;
            }

            Console.WriteLine(value: $"[{DateTime.Now:HH:mm:ss}] Change detected ({e.Name}), recompiling...");
            ExecuteCompilation(
                bundle: bundle,
                outputPath: outputPath,
                sourcePath: sourcePath,
                strict: strict,
                validate: validate
            );
        }

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += (s, e) => OnChanged(s, e);
        watcher.EnableRaisingEvents = true;

        using var cancelEvent = new ManualResetEvent(initialState: false);
        Console.CancelKeyPress += (sender, eventArgs) => {
            eventArgs.Cancel = true;
            cancelEvent.Set();
        };

        cancelEvent.WaitOne();
        Console.WriteLine(value: "[puck watch] Stopped.");
        return 0;
    }

    private static void PrintDiagnostics(DiagnosticBag diagnostics, string filePath, string sourceText) {
        if (!diagnostics.Any()) {
            return;
        }

        var report = diagnostics.FormatReport(filePath: filePath, sourceText: sourceText);
        Console.Error.Write(value: report);
    }

}

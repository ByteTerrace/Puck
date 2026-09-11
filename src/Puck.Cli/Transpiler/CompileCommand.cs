using System.CommandLine;
using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.World;
using Puck.World.Transpiler.Diagnostics;
using Puck.World.Transpiler.Lowering;
using Puck.World.Transpiler.Modules;
using Puck.World.Transpiler.Parsing;
using Puck.World.Transpiler.Validation;

namespace Puck.Cli.Transpiler;

/// <summary>
/// The <c>puck compile</c> verb: compiles a <c>.puck</c> DSL source file into a canonical JSON world definition.
/// Supports resilient diagnostics, dependency resolution, engine schema validation, and watch mode.
/// </summary>
internal static class CompileCommand {
    public static Command Create() {
        var pathArgument = new Argument<string>(name: "path") { Description = "Path to the .puck source file to compile." };
        var outputOption = new Option<string?>(name: "--output", aliases: ["-o"]) { Description = "Destination output JSON path (defaults to <path>.world.json)." };
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

        var outputPath = output is not null
            ? Path.GetFullPath(path: output)
            : ComputeDefaultOutputPath(sourcePath: fullPath);

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
        string outputPath,
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
        var loweringResult = WorldDocumentEmitter.LowerWithDiagnostics(
            basePath: baseDirectory,
            diagnostics: diagnostics,
            document: effectiveAst,
            sourceMap: sourceMap
        );

        var jsonObject = loweringResult.Value ?? new JsonObject();
        var jsonBytes = CanonicalJsonDocument.Serialize(node: jsonObject);
        var json = Encoding.UTF8.GetString(bytes: jsonBytes);

        if (validate && !diagnostics.HasErrors) {
            EnsureVocabularyHooksInstalled();
            WorldSemanticValidator.ValidateComposedWorld(
                diagnostics: diagnostics,
                loweredJson: jsonObject,
                sourceMap: sourceMap,
                sourcePath: sourcePath
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
        string outputPath,
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

    // WorldDefinitionValidator reads several catalogs (screen-machine engines, post-render extensions, probe kinds)
    // through Puck.World.Schema's injection hooks rather than referencing their owning projects directly; some
    // process root must wire them before the first validation runs (see Puck.World.Client.WorldSchemaVocabularyHooks
    // and Puck.Cli.Automation.WorldPrepareCommand, which wires the same hooks for `puck world prepare`). Installing
    // is idempotent (it only reassigns delegates), so a guard here is an optimization, not a correctness need.
    private static bool s_vocabularyHooksInstalled;

    private static void EnsureVocabularyHooksInstalled() {
        if (s_vocabularyHooksInstalled) {
            return;
        }

        Puck.World.Client.WorldSchemaVocabularyHooks.Install(
            postRenderExtensionCheck: WorldPostRenderExtensions.IsShipped,
            probeKindCheck: WorldProbeKinds.IsShipped,
            screenMachineCartridgeCheck: WorldScreenMachineEngines.CompilesCartridges,
            screenMachineEngineCheck: WorldScreenMachineEngines.IsRegistered
        );
        s_vocabularyHooksInstalled = true;
    }

    private static void PrintDiagnostics(DiagnosticBag diagnostics, string filePath, string sourceText) {
        if (!diagnostics.Any()) {
            return;
        }

        var report = diagnostics.FormatReport(filePath: filePath, sourceText: sourceText);
        Console.Error.Write(value: report);
    }

    private static string ComputeDefaultOutputPath(string sourcePath) {
        var dir = Path.GetDirectoryName(path: sourcePath) ?? "";
        var fileName = Path.GetFileName(path: sourcePath);

        if (fileName.EndsWith(value: ".puck", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            var withoutExt = fileName[..^5];

            return Path.Combine(path1: dir, path2: withoutExt + ".world.json");
        }

        return Path.Combine(path1: dir, path2: Path.GetFileNameWithoutExtension(path: sourcePath) + ".world.json");
    }
}

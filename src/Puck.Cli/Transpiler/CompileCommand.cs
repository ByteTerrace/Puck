using System.CommandLine;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.GamingBricks.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;
using Puck.Transpiler.Modules;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler;
using Puck.World;

namespace Puck.Cli.Transpiler;

/// <summary>
/// The <c>puck compile</c> verb: compiles <c>.puck</c> DSL sources into canonical JSON definitions in input order, and
/// writes each world document's compiled world (<c>Puck.World.CompiledWorld</c>) beside it; a <c>.world.json</c>
/// document given as a path contributes its compiled world alone. Supports resilient diagnostics, dependency
/// resolution, engine schema validation, and watch mode.
/// </summary>
internal static partial class CompileCommand {
    internal static int Run(
        string path,
        string? output,
        bool watch,
        bool strict,
        bool validate,
        bool bundle,
        bool updateAssets = false,
        IDictionary<string, string>? written = null,
        BakePackPlan? pack = null
    ) {
        var fullPath = Path.GetFullPath(path: path);

        if (!File.Exists(path: fullPath)) {
            Console.Error.WriteLine(value: $"error: Source file not found: '{fullPath}'");
            return 2;
        }

        var outputPath = ((output is not null)
            ? Path.GetFullPath(path: output)
            : null
        );

        if (WorldDocumentName.IsDocumentFile(path: fullPath)) {
            if (watch || updateAssets) {
                Console.Error.WriteLine(value: "error: --watch and --update-assets apply to .puck sources; a world document compiles only to its compiled world.");
                return 1;
            }

            return CompileDocument(
                besidePath: (outputPath ?? fullPath),
                documentPath: fullPath,
                pack: pack,
                written: written
            );
        }

        if (!watch) {
            return ExecuteCompilation(
                bundle: bundle,
                outputPath: outputPath,
                pack: pack,
                sourcePath: fullPath,
                strict: strict,
                updateAssets: updateAssets,
                validate: validate,
                written: written
            );
        }

        return RunWatchMode(
            bundle: bundle,
            outputPath: outputPath,
            sourcePath: fullPath,
            strict: strict,
            updateAssets: updateAssets,
            validate: validate
        );
    }

    private static int ExecuteCompilation(
        string sourcePath,
        string? outputPath,
        bool strict,
        bool validate,
        bool bundle,
        bool updateAssets = false,
        IDictionary<string, string>? written = null,
        BakePackPlan? pack = null
    ) {
        var diagnostics = new DiagnosticBag();
        string sourceText;

        try {
            sourceText = File.ReadAllText(path: sourcePath);
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Could not read source file '{sourcePath}': {ex.Message}");
            return 2;
        }

        // The document's own `schema:` line picks the vocabulary that knows what its sections mean. The language is
        // the same either way; only the lowering differs.
        var vocabulary = CliVocabularyResolver.Instance.Resolve(source: sourceText);
        var isCartridge = ReferenceEquals(
            objA: vocabulary,
            objB: CartridgeVocabulary.Instance
        );
        var sourceMap = new SourceMap();
        var imports = (bundle
            ? ImportHandling.Bundle
            : ImportHandling.Validate
        );

        if (!isCartridge) {
            return ExecuteWorldCompilation(imports: imports, outputPath: outputPath, pack: pack, source: sourceText, sourcePath: sourcePath, strict: strict, updateAssets: updateAssets, validate: validate, written: written);
        }
        if (updateAssets) {
            Console.Error.WriteLine(value: "error: --update-assets applies to world asset references; cartridge sources do not use a world asset lock.");
            return 1;
        }

        var (effectiveAst, loweredJson) = CompileSource(
            diagnostics: diagnostics,
            imports: imports,
            sourceMap: sourceMap,
            sourcePath: sourcePath,
            sourceText: sourceText,
            vocabulary: vocabulary
        );

        if (effectiveAst is null) {
            PrintDiagnostics(
                diagnostics: diagnostics,
                filePath: sourcePath,
                sourceText: sourceText
            );
            return 1;
        }

        var jsonObject = (loweredJson ?? new JsonObject());
        var jsonBytes = CanonicalJsonDocument.Serialize(node: jsonObject);

        // A module is a fragment whichever root imports it supplies fields for, so validating one as a world
        // reports refusals that belong to that root, not to this file. `lint` applies the same rule.
        if (validate && !diagnostics.HasErrors) {
            CartridgeLanguageServices.Validate(
                jsonObject,
                sourceMap,
                diagnostics,
                effectiveAst.Span
            );
        }

        PrintDiagnostics(
            diagnostics: diagnostics,
            filePath: sourcePath,
            sourceText: sourceText
        );

        var hasErrors = diagnostics.HasErrors;
        var hasWarnings = diagnostics.HasWarnings;

        if (
            hasErrors ||
            (strict && hasWarnings)
        ) {
            var errCount = diagnostics.Count(predicate: d => (d.Severity == DiagnosticSeverity.Error));
            var warnCount = diagnostics.Count(predicate: d => (d.Severity == DiagnosticSeverity.Warning));

            Console.Error.WriteLine(value: $"Compilation failed with {errCount} error(s) and {warnCount} warning(s).");
            return 1;
        }

        try {
            outputPath ??= Path.ChangeExtension(
                extension: (isCartridge
                ? ".cartridge.json"
                : WorldDocumentName.DocumentSuffix),
                path: sourcePath
            );
            var outputDirectory = Path.GetDirectoryName(path: outputPath);

            if (
                !string.IsNullOrEmpty(value: outputDirectory) &&
                !Directory.Exists(path: outputDirectory)
            ) {
                Directory.CreateDirectory(path: outputDirectory);
            }

            File.WriteAllBytes(
                bytes: jsonBytes,
                path: outputPath
            );
            Console.WriteLine(value: $"Successfully compiled '{Path.GetFileName(path: sourcePath)}' -> '{outputPath}' ({jsonBytes.Length:N0} bytes).");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Failed to write output file '{outputPath}': {ex.Message}");
            return 2;
        }
    }

    // The cartridge vocabulary is `Puck.World.Transpiler`'s peer, so its lowering is not reachable from behind
    // `WorldCompiler`; the stage order is the same one that door runs.
    /// <summary>Compiles <paramref name="sourceText"/> as if it were the file at <paramref name="sourcePath"/>,
    /// through whichever vocabulary its own <c>schema:</c> names. Nothing is written.</summary>
    /// <param name="sourceText">The source to compile.</param>
    /// <param name="sourcePath">The file the text stands for; it roots relative assets, the import walk, and the
    /// embedding lock beside it.</param>
    /// <param name="vocabulary">The vocabulary the source's schema resolves to.</param>
    /// <param name="imports">What the import graph is worth to this compile.</param>
    /// <param name="diagnostics">The bag every stage reports into.</param>
    /// <param name="sourceMap">The map the lowering registers pointers in.</param>
    /// <returns>The lowered document and its JSON, either of which is null when a stage refused.</returns>
    internal static (DocumentNode? Document, JsonObject? Json) CompileSource(
        string sourceText,
        string sourcePath,
        IDocumentVocabulary vocabulary,
        ImportHandling imports,
        DiagnosticBag diagnostics,
        SourceMap sourceMap
    ) {
        if (!string.Equals(
            a: (PuckParser.TryReadDocumentSchema(schema: out var schema, source: sourceText)
                ? schema
                : null),
            b: CartridgeVocabulary.Schema,
            comparisonType: StringComparison.Ordinal
        )) {
            var compilation = WorldCompiler.Compile(
                diagnostics: diagnostics,
                imports: imports,
                source: sourceText,
                sourceMap: sourceMap,
                sourcePath: sourcePath
            );

            return (compilation.Document, compilation.Json);
        }

        return (CompileCartridge(
            diagnostics: diagnostics,
            imports: imports,
            json: out var cartridgeJson,
            sourceMap: sourceMap,
            sourcePath: sourcePath,
            sourceText: sourceText,
            vocabulary: vocabulary
        ), cartridgeJson);
    }

    private static DocumentNode? CompileCartridge(
        string sourceText,
        string sourcePath,
        IDocumentVocabulary vocabulary,
        ImportHandling imports,
        DiagnosticBag diagnostics,
        SourceMap sourceMap,
        out JsonObject? json
    ) {
        json = null;

        var documentNode = PuckParser.ParseDocumentWithDiagnostics(
            source: sourceText,
            diagnostics: diagnostics,
            vocabulary: vocabulary
        ).Value;

        if (documentNode is null) {
            return null;
        }

        if (imports == ImportHandling.Bundle) {
            var bundledDoc = ModuleResolver.BundleDocument(
                diagnostics: diagnostics,
                rootDoc: documentNode,
                rootPath: sourcePath,
                vocabulary: vocabulary
            );

            if (bundledDoc is not null) {
                documentNode = bundledDoc;
            }
        } else if (imports == ImportHandling.Validate) {
            ModuleResolver.ValidateImportGraph(
                diagnostics: diagnostics,
                rootDoc: documentNode,
                rootPath: sourcePath,
                vocabulary: vocabulary
            );
        }

        json = CartridgeDocumentEmitter.LowerWithDiagnostics(
            diagnostics: diagnostics,
            document: documentNode,
            sourceMap: sourceMap
        ).Value;

        return documentNode;
    }
    private static void PrintDiagnostics(DiagnosticBag diagnostics, string filePath, string sourceText) {
        if (!diagnostics.Any()) {
            return;
        }

        var report = diagnostics.FormatReport(
            filePath: filePath,
            sourceText: sourceText
        );

        Console.Error.Write(value: report);
    }
    private static int RunWatchMode(
        string sourcePath,
        string? outputPath,
        bool strict,
        bool validate,
        bool bundle,
        bool updateAssets = false
    ) {
        Console.WriteLine(value: $"[puck watch] Monitoring '{sourcePath}' for changes (Ctrl+C to stop)...");

        // Initial run
        ExecuteCompilation(
            bundle: bundle,
            outputPath: outputPath,
            sourcePath: sourcePath,
            strict: strict,
            updateAssets: updateAssets,
            validate: validate
        );

        var directory = (Path.GetDirectoryName(path: sourcePath) ?? Directory.GetCurrentDirectory());
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
                updateAssets: updateAssets,
                validate: validate
            );
        }

        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Renamed += (s, e) => OnChanged(
            e: e,
            sender: s
        );
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

    public static Command Create() {
        var pathArgument = new Argument<string[]>(name: "paths") { Arity = ArgumentArity.OneOrMore, Description = "Paths to .puck sources, or .world.json documents to write only the compiled world of, compiled in input order in one process. Stops on the first failure." };
        var outputOption = new Option<string?>(
            name: "--output",
            aliases: ["-o"]
        ) { Description = "Destination JSON path, or destination directory for a source with world declarations; for a .world.json document, the path its compiled world sits beside (or the .puckb file itself)." };
        var watchOption = new Option<bool>(
            name: "--watch",
            aliases: ["-w"]
        ) { Description = "Watch the source file and its imported dependencies for changes and recompile automatically." };
        var strictOption = new Option<bool>(name: "--strict") { Description = "Treat warnings as errors." };
        var validateOption = new Option<bool>(name: "--validate") { Description = "Validate semantic engine schema rules on the emitted document." };
        var bundleOption = new Option<bool>(name: "--bundle") { Description = "Inline and bundle all imported .puck module ASTs into a single standalone document." };
        var assetsOption = new Option<bool>(name: "--update-assets") { Description = "Explicitly refresh the source's asset hash lock after successful compilation and validation." };
        var treeOption = new Option<string?>(name: "--tree") { Description = "Mirror the sources, which must lie under this directory, into the --output directory: each world document is written where its source sits relative to the tree with its compiled world (.puckb) beside it, the bakes they name ship once in one bake pack (bakes.puckbake) at the output's root, every pipeline source they name ships compiled as a shader package in the store (packages/) at the output's root, a .world.json source ships as it stands with its compiled world unless the .puck source of its exact name emits that name, and every other *.world.json, *.puckb, *.puckbake and stored package under --output is removed, so the output holds exactly this run's worlds." };
        var checkOption = new Option<bool>(name: "--check") { Description = "With --tree, compile into a scratch directory and compare it with --output instead of writing there: exit 1 naming every file a fresh run writes that --output lacks or holds with other bytes, and every document, compiled world, bake pack or stored package --output holds that the run does not write. Writes nothing under --output." };
        var writtenOption = new Option<string?>(name: "--written") { Description = "With --tree, write the files the run left under --output to this file once it succeeds, one output-relative path with forward slashes per line, and remove it before the run starts, so it exists only for a whole run." };

        var command = new Command(
            description: "Compile .puck source files into canonical JSON world or cartridge definitions, and each world document into its compiled world (.puckb) beside it.",
            name: "compile"
        ) {
            pathArgument,
            outputOption,
            watchOption,
            strictOption,
            validateOption,
            bundleOption,
            assetsOption,
            treeOption,
            writtenOption,
            checkOption,
        };

        command.Validators.Add(item: result => {
            if (result.GetValue(option: assetsOption) && result.GetValue(option: watchOption)) { result.AddError(errorMessage: "--update-assets cannot be combined with --watch; asset changes must be accepted explicitly."); }
            if (result.GetValue(option: treeOption) is not null) {
                if ((result.GetValue(option: outputOption) is null) || result.GetValue(option: watchOption) || result.GetValue(option: assetsOption)) {
                    result.AddError(errorMessage: "--tree requires --output (the mirrored tree's directory) and cannot be combined with --watch or --update-assets.");
                }
                if (result.GetValue(option: checkOption) && (result.GetValue(option: writtenOption) is not null)) {
                    result.AddError(errorMessage: "--check writes nothing, so it takes no --written report.");
                }
            } else if (result.GetValue(option: checkOption)) {
                result.AddError(errorMessage: "--check compares a --tree run with its --output and requires --tree.");
            } else if (result.GetValue(option: writtenOption) is not null) {
                result.AddError(errorMessage: "--written reports a --tree run and requires --tree.");
            } else if ((result.GetValue(argument: pathArgument)?.Length > 1) &&
                ((result.GetValue(option: outputOption) is not null) || result.GetValue(option: watchOption))) {
                result.AddError(errorMessage: "--output and --watch require exactly one source file.");
            }
        });
        command.SetAction(action: parseResult => {
            if ((parseResult.GetValue(option: treeOption) is { } checkedTree) && parseResult.GetValue(option: checkOption)) {
                return RunTreeCheck(
                    bundle: parseResult.GetValue(option: bundleOption),
                    output: parseResult.GetRequiredValue(option: outputOption)!,
                    paths: parseResult.GetRequiredValue(argument: pathArgument),
                    strict: parseResult.GetValue(option: strictOption),
                    tree: checkedTree,
                    validate: parseResult.GetValue(option: validateOption)
                );
            }

            if (parseResult.GetValue(option: treeOption) is { } tree) {
                return RunTree(
                    bundle: parseResult.GetValue(option: bundleOption),
                    output: parseResult.GetRequiredValue(option: outputOption)!,
                    paths: parseResult.GetRequiredValue(argument: pathArgument),
                    report: parseResult.GetValue(option: writtenOption),
                    strict: parseResult.GetValue(option: strictOption),
                    tree: tree,
                    validate: parseResult.GetValue(option: validateOption)
                );
            }

            foreach (var path in parseResult.GetRequiredValue(argument: pathArgument)) {
                var exitCode = Run(
                    bundle: parseResult.GetValue(option: bundleOption),
                    output: parseResult.GetValue(option: outputOption),
                    path: path,
                    strict: parseResult.GetValue(option: strictOption),
                    validate: parseResult.GetValue(option: validateOption),
                    watch: parseResult.GetValue(option: watchOption),
                    updateAssets: parseResult.GetValue(option: assetsOption)
                );

                if (exitCode != 0) { return exitCode; }
            }
            return 0;
        });

        return command;
    }

}

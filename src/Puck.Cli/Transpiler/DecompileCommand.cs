using System.CommandLine;
using Puck.World.Transpiler.Decompiler;
using Puck.World.Transpiler.Embeddings;
using Puck.World;

namespace Puck.Cli.Transpiler;

/// <summary>
/// The <c>puck decompile</c> verb: decompiles a canonical JSON world definition into idiomatic <c>.puck</c> DSL source code,
/// or the worlds one composition emitted into the composition source that emits them.
/// </summary>
internal static class DecompileCommand {
    // Several documents are the worlds of one composition, each named by its file stem: the links between them are
    // generated rows in both worlds they join, so they print back only together, as one source.
    private static int RunComposition(IReadOnlyList<string> paths, string? output, bool overwrite) {
        if (output is null) {
            Console.Error.WriteLine(value: "error: decompiling several worlds writes one composition source; name it with --output.");
            return 2;
        }

        var outputPath = Path.GetFullPath(path: output);

        if (File.Exists(path: outputPath) && !overwrite) {
            Console.Error.WriteLine(value: $"error: Destination file '{outputPath}' already exists. Use --overwrite to overwrite.");
            return 1;
        }

        var worlds = new List<KeyValuePair<string, System.Text.Json.Nodes.JsonObject>>(capacity: paths.Count);

        foreach (var path in paths) {
            var fullPath = Path.GetFullPath(path: path);
            var name = Path.GetFileName(path: fullPath);

            if (!WorldDocumentName.IsDocumentFile(path: name)) {
                Console.Error.WriteLine(value: $"error: '{fullPath}' is not a '<world>.world.json' document; a composition names each world by its file stem.");
                return 2;
            }

            try {
                if (System.Text.Json.Nodes.JsonNode.Parse(json: File.ReadAllText(path: fullPath)) is not System.Text.Json.Nodes.JsonObject document) {
                    Console.Error.WriteLine(value: $"error: '{fullPath}' is not a JSON object.");
                    return 2;
                }

                worlds.Add(item: KeyValuePair.Create(key: WorldDocumentName.OfDocumentFile(path: name), value: document));
            } catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)) {
                Console.Error.WriteLine(value: $"error: Could not read JSON file '{fullPath}': {ex.Message}");
                return 2;
            }
        }

        string source;

        try {
            source = WorldDecompiler.DecompileComposition(worlds: worlds);
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Failed to decompile the composition: {ex.Message}");
            return 1;
        }

        try {
            if (Path.GetDirectoryName(path: outputPath) is { Length: > 0 } directory) {
                _ = Directory.CreateDirectory(path: directory);
            }

            File.WriteAllText(contents: source, path: outputPath);
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Failed to write output file '{outputPath}': {ex.Message}");
            return 2;
        }

        Console.WriteLine(value: $"Successfully decompiled {worlds.Count} worlds -> '{outputPath}' ({System.Text.Encoding.UTF8.GetByteCount(s: source):N0} bytes).");
        return 0;
    }

    internal static int Run(
        IReadOnlyList<string> paths,
        string? output,
        bool overwrite,
        bool sql = false,
        string? embeddings = null
    ) {
        if (paths.Count > 1) {
            if (sql || (embeddings is not null)) {
                Console.Error.WriteLine(value: "error: --sql and --embeddings decompile one document; name one path.");
                return 2;
            }

            return RunComposition(
                output: output,
                overwrite: overwrite,
                paths: paths
            );
        }

        var fullPath = Path.GetFullPath(path: paths[0]);

        if (!File.Exists(path: fullPath)) {
            Console.Error.WriteLine(value: $"error: JSON world definition file not found: '{fullPath}'");
            return 2;
        }

        var outputPath = ((output is not null)
            ? Path.GetFullPath(path: output)
            : ComputeDefaultOutputPath(sourcePath: fullPath)
        );

        if (
            File.Exists(path: outputPath) &&
            !overwrite
        ) {
            Console.Error.WriteLine(value: $"error: Destination file '{outputPath}' already exists. Use --overwrite to overwrite.");
            return 1;
        }

        EmbeddingLock? lockFile = null;

        if (!string.IsNullOrEmpty(value: embeddings)) {
            var lockPath = Path.GetFullPath(path: embeddings);

            if (!File.Exists(path: lockPath)) {
                Console.Error.WriteLine(value: $"error: Embedding lock file not found: '{lockPath}'");
                return 2;
            }

            try {
                lockFile = EmbeddingLock.Parse(json: File.ReadAllText(path: lockPath));
            } catch (Exception ex) {
                Console.Error.WriteLine(value: $"error: Failed to parse embedding lock file '{lockPath}': {ex.Message}");
                return 2;
            }
        } else {
            lockFile = (EmbeddingLock.TryLoad(rootSourcePath: outputPath) ?? EmbeddingLock.TryLoad(rootSourcePath: fullPath));
        }

        string json;

        try {
            json = File.ReadAllText(path: fullPath);
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Could not read JSON file '{fullPath}': {ex.Message}");
            return 2;
        }

        string puckSource;

        try {
            // The document's own schema picks the vocabulary, the same way compiling does.
            puckSource = (((System.Text.Json.Nodes.JsonNode.Parse(json: json) is System.Text.Json.Nodes.JsonObject document) && DecompileCartridge.Handles(document: document))
                ? DecompileCartridge.Run(document: document)
                : WorldDecompiler.Decompile(embeddings: lockFile, jsonText: json, sql: sql)
            );
            var printed = Puck.Transpiler.Formatting.PuckPrinter.Format(
                source: puckSource,
                vocabulary: CliVocabularyResolver.Instance.Resolve(source: puckSource)
            );

            // Decompiled source that will not parse back is a defect in the decompiler, never something to write out.
            puckSource = (printed.Value ?? throw new InvalidOperationException(message: $"the decompiled source does not parse: {printed.Diagnostics.First(predicate: static diagnostic => (diagnostic.Severity == Puck.Transpiler.Diagnostics.DiagnosticSeverity.Error)).Message}"));
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Failed to decompile world definition '{fullPath}': {ex.Message}");
            return 1;
        }

        try {
            var outputDirectory = Path.GetDirectoryName(path: outputPath);

            if (
                !string.IsNullOrEmpty(value: outputDirectory) &&
                !Directory.Exists(path: outputDirectory)
            ) {
                Directory.CreateDirectory(path: outputDirectory);
            }

            File.WriteAllText(
                contents: puckSource,
                path: outputPath
            );
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(s: puckSource);

            Console.WriteLine(value: $"Successfully decompiled '{Path.GetFileName(path: fullPath)}' -> '{outputPath}' ({byteCount:N0} bytes).");
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine(value: $"error: Failed to write output file '{outputPath}': {ex.Message}");
            return 2;
        }
    }

    private static string ComputeDefaultOutputPath(string sourcePath) {
        var dir = (Path.GetDirectoryName(path: sourcePath) ?? "");
        var fileName = Path.GetFileName(path: sourcePath);

        if (WorldDocumentName.IsDocumentFile(path: fileName)) {
            return Path.Combine(
                path1: dir,
                path2: WorldDocumentName.SourceFile(name: WorldDocumentName.OfDocumentFile(path: fileName))
            );
        }

        if (fileName.EndsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".json"
        )) {
            var baseName = fileName[..^5];

            return Path.Combine(
                path1: dir,
                path2: (baseName + ".puck")
            );
        }

        return Path.Combine(
            path1: dir,
            path2: (Path.GetFileNameWithoutExtension(path: sourcePath) + ".puck")
        );
    }

    public static Command Create() {
        var pathArgument = new Argument<string[]>(name: "paths") {
            Arity = ArgumentArity.OneOrMore,
            Description = "Path to the JSON world definition file to decompile, or the '<world>.world.json' documents of one composition, which decompile together into the one source that emits them.",
        };
        var outputOption = new Option<string?>(
            name: "--output",
            aliases: ["-o"]
        ) { Description = "Destination output .puck path (defaults to <path>.puck)." };
        var overwriteOption = new Option<bool>(name: "--overwrite") { Description = "Overwrite destination file if it already exists." };
        var sqlOption = new Option<bool>(name: "--sql") { Description = "Project representable state tables, slots, and rules into an embedded SQL block." };
        var embeddingsOption = new Option<string?>(name: "--embeddings") { Description = "Optional companion embedding lock file (.embeddings.json) for resolving vector literals." };

        var command = new Command(
            description: "Decompile a JSON world definition, or the worlds of one composition, into idiomatic .puck DSL source.",
            name: "decompile"
        ) {
            pathArgument,
            outputOption,
            overwriteOption,
            sqlOption,
            embeddingsOption,
        };

        command.SetAction(action: parseResult => Run(
            embeddings: parseResult.GetValue(option: embeddingsOption),
            output: parseResult.GetValue(option: outputOption),
            overwrite: parseResult.GetValue(option: overwriteOption),
            paths: parseResult.GetRequiredValue(argument: pathArgument),
            sql: parseResult.GetValue(option: sqlOption)
        ));

        return command;
    }
}

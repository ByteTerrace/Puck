using Puck.Abstractions;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Validation;
using Puck.World;

namespace Puck.Cli.Transpiler;

internal static partial class CompileCommand {
    private static int ExecuteWorldCompilation(string source, string sourcePath, string? outputPath, ImportHandling imports, bool strict, bool validate, bool updateAssets, IDictionary<string, string>? written = null, BakePackPlan? pack = null) {
        var compilation = WorldCompiler.Compile(source, sourcePath: sourcePath, imports: imports, allowMultiple: true, updateAssets: updateAssets);
        var diagnostics = compilation.Diagnostics;
        // One output per document name the source emits (WorldCompilation.DocumentNames): each world a composition
        // declares, or the one document an ordinary source lowers to, under its stem. A module library emits none.
        var outputs = compilation.DocumentNames(sourcePath: sourcePath).Select(selector: name => (compilation.Worlds.FirstOrDefault(predicate: world => string.Equals(a: world.Name, b: name, comparisonType: StringComparison.Ordinal))
            ?? new WorldOutput(name, compilation.Json!, compilation.SourceMap, compilation.TestWorlds))).ToArray();

        if ((validate || updateAssets) && !diagnostics.HasErrors) {
            var machines = CliWorldVocabulary.EnsureInstalled();
            var fingerprint = CliWorldVocabulary.Fingerprint(catalog: machines);

            foreach (var output in outputs) {
                if (WorldSemanticValidator.IsRootDocument(loweredJson: output.Json)) {
                    WorldSemanticValidator.ValidateComposedWorld(output.Json, output.SourceMap, diagnostics, sourcePath, machines, fingerprint);
                }
            }
        }
        PrintDiagnostics(diagnostics: diagnostics, filePath: sourcePath, sourceText: source);
        if (diagnostics.HasErrors || (strict && diagnostics.HasWarnings) || !compilation.Success) { return 1; }

        // A module library declares no world and lowers to an empty document, so it has no document to write; the
        // sources that import it carry what it defines. This is the design, not a notable outcome, so a tree build
        // compiling every module fragment beside its worlds prints nothing for it.
        if (outputs.Length == 0) {
            return 0;
        }

        // Each document lands under the name it is emitted by, in one directory: beside the source, where a mirrored
        // tree writes the source's own directory, or in the directory a composition's -o names. Only an ordinary
        // source's -o outside a tree run names its document's file itself. Declared names are validated as portable
        // filename stems.
        var composed = (compilation.Worlds.Count > 0);
        var named = (!composed && (written is null) && (outputPath is not null));
        var directory = ((written is not null)
            ? Path.GetDirectoryName(path: outputPath!)!
            : (composed
                ? (outputPath ?? Path.GetDirectoryName(path: sourcePath)!)
                : Path.GetDirectoryName(path: (outputPath ?? sourcePath))!)
        );

        // A document name is unique ignoring case, so no two outputs of one run may land on one document file: two
        // worlds of this source, or a world of this source and one an earlier source of the same tree run wrote. Both
        // are two files carrying one document name, refused in the words every door refuses that in.
        var destinations = outputs.Select(selector: output => Path.GetFullPath(path: (named ? outputPath! : Path.Combine(path1: directory, path2: WorldDocumentName.DocumentFile(name: output.Name))))).ToArray();
        // Each destination held so far, spelled as it was claimed, and the source that claimed it.
        var claimed = new Dictionary<string, (string Destination, string Owner)>(comparer: DocumentName.Comparer);

        foreach (var (key, owner) in (written ?? new Dictionary<string, string>())) {
            claimed[key] = (key, owner);
        }
        foreach (var destination in destinations) {
            if (claimed.TryGetValue(key: destination, value: out var held)) {
                var collision = DocumentName.Collision(
                    heldFile: PuckPaths.Normalize(path: held.Owner),
                    heldName: WorldDocumentName.OfDocumentFile(path: Path.GetFileName(path: held.Destination)),
                    otherFile: PuckPaths.Normalize(path: sourcePath),
                    otherName: WorldDocumentName.OfDocumentFile(path: Path.GetFileName(path: destination))
                );

                Console.Error.WriteLine(value: $"error: {collision} Nothing from '{Path.GetFileName(path: sourcePath)}' was written.");
                return 1;
            }
            claimed.Add(key: destination, value: (destination, sourcePath));
        }
        var staged = new List<(string Temporary, string Destination)>();
        var documents = new byte[destinations.Length][];
        // Each compiled world composes where its source sits, so it reads the document as the source wrote it.
        var composedDocuments = outputs.Select(selector: static output => CanonicalJsonDocument.Serialize(node: output.Json)).ToArray();
        var sourceDirectory = Path.GetDirectoryName(path: Path.GetFullPath(path: sourcePath))!;

        // A document written away from its source names every file it references from where it lands; a mirrored tree
        // keeps the source's layout, so its documents land where their paths already hold.
        if ((written is null) && !PuckPaths.Comparer.Equals(x: Path.GetFullPath(path: directory), y: sourceDirectory)) {
            var machines = CliWorldVocabulary.EnsureInstalled();

            for (var index = 0; (index < destinations.Length); index++) {
                var authored = Path.Combine(path1: sourceDirectory, path2: WorldDocumentName.DocumentFile(name: outputs[index].Name));

                WorldDocumentPaths.RelocateDocumentFields(module: outputs[index].Json, sourceDocumentPath: authored, targetDocumentPath: destinations[index]);
                if (!WorldModuleNamespace.TryRelocateConfigurationAssets(
                    catalog: machines,
                    module: outputs[index].Json,
                    reason: out var reason,
                    sourceDocumentPath: authored,
                    targetDocumentPath: destinations[index]
                )) {
                    Console.Error.WriteLine(value: $"error: '{outputs[index].Name}' cannot be written away from its source: {reason}");
                    return 1;
                }
            }
        }

        try {
            for (var index = 0; (index < destinations.Length); index++) {
                var destination = destinations[index];

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                var temporary = (((destination + ".") + Guid.NewGuid().ToString(format: "N")) + ".compile-tmp");

                staged.Add(item: (temporary, destination));
                documents[index] = CanonicalJsonDocument.Serialize(node: outputs[index].Json);
                File.WriteAllBytes(temporary, documents[index]);
            }
            if (compilation.Assets?.SaveUpdatedLock(diagnostics: diagnostics) == false) {
                PrintDiagnostics(diagnostics: diagnostics, filePath: sourcePath, sourceText: source);
                return 1;
            }
            foreach (var (temporary, destination) in staged) {
                File.Move(temporary, destination, overwrite: true);
                written?.Add(
                    key: destination,
                    value: sourcePath
                );
                Console.WriteLine(value: $"Successfully compiled '{Path.GetFileName(path: sourcePath)}' -> '{destination}'.");
            }
            // Each document's compiled world sits beside it, composed where its source sits, as a boot of the source
            // composes it.
            for (var index = 0; (index < destinations.Length); index++) {
                var exitCode = WriteCompiledWorld(
                    besidePath: destinations[index],
                    composeAt: Path.Combine(
                        path1: Path.GetDirectoryName(path: sourcePath)!,
                        path2: WorldDocumentName.DocumentFile(name: outputs[index].Name)
                    ),
                    document: composedDocuments[index],
                    pack: pack,
                    written: written
                );

                if (exitCode != 0) {
                    return exitCode;
                }
            }
            return 0;
        } catch (IOException error) {
            Console.Error.WriteLine(value: $"error: Could not publish compiled worlds: {error.Message}");
            return 2;
        } catch (UnauthorizedAccessException error) {
            Console.Error.WriteLine(value: $"error: Could not publish compiled worlds: {error.Message}");
            return 2;
        } finally {
            foreach (var (temporary, _) in staged) {
                try { File.Delete(path: temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}

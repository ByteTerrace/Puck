using Puck.Abstractions.Documents;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Validation;

namespace Puck.Cli.Transpiler;

internal static partial class CompileCommand {
    private static int ExecuteWorldCompilation(string source, string sourcePath, string? outputPath, ImportHandling imports, bool strict, bool validate, bool updateAssets) {
        var compilation = WorldCompiler.Compile(source, sourcePath: sourcePath, imports: imports, allowMultiple: true, updateAssets: updateAssets);
        var diagnostics = compilation.Diagnostics;
        var outputs = ((compilation.Worlds.Count > 0) ? compilation.Worlds : ((compilation.Json is { } json)
            ? [new WorldOutput(Path.GetFileNameWithoutExtension(path: sourcePath), json, compilation.SourceMap, compilation.TestWorlds)]
            : Array.Empty<WorldOutput>()));

        if ((validate || updateAssets) && !diagnostics.HasErrors) {
            var machines = CliWorldVocabulary.EnsureInstalled();
            var fingerprint = CliWorldVocabulary.Fingerprint(catalog: machines);

            foreach (var output in outputs) {
                if (WorldSemanticValidator.IsRootDocument(output.Json)) {
                    WorldSemanticValidator.ValidateComposedWorld(output.Json, output.SourceMap, diagnostics, sourcePath, machines, fingerprint);
                }
            }
        }
        PrintDiagnostics(diagnostics: diagnostics, filePath: sourcePath, sourceText: source);
        if (diagnostics.HasErrors || (strict && diagnostics.HasWarnings) || (outputs.Count == 0)) { return 1; }

        // A composition's -o names a directory. Its declared names are validated as portable filename stems.
        var composed = (compilation.Worlds.Count > 0);
        var directory = (composed ? (outputPath ?? Path.GetDirectoryName(path: sourcePath)!) : null);

        if ((compilation.Assets is { PendingReferences.Count: > 0 }) && !string.Equals(
            a: Path.GetFullPath(path: (composed ? directory! : Path.GetDirectoryName(path: (outputPath ?? sourcePath))!)),
            b: Path.GetDirectoryName(path: sourcePath), comparisonType: (OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) {
            Console.Error.WriteLine(value: "error: A source with asset references must be emitted beside its source so its pinned relative paths keep their meaning.");
            return 1;
        }
        var staged = new List<(string Temporary, string Destination)>();

        try {
            foreach (var output in outputs) {
                var destination = (composed ? Path.Combine(path1: directory!, path2: $"{output.Name}.world.json") : (outputPath ?? Path.ChangeExtension(extension: ".world.json", path: sourcePath)));

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                var temporary = (((destination + ".") + Guid.NewGuid().ToString(format: "N")) + ".compile-tmp");

                staged.Add(item: (temporary, destination));
                File.WriteAllBytes(temporary, CanonicalJsonDocument.Serialize(output.Json));
            }
            if (compilation.Assets?.SaveUpdatedLock(diagnostics) == false) {
                PrintDiagnostics(diagnostics: diagnostics, filePath: sourcePath, sourceText: source);
                return 1;
            }
            foreach (var (temporary, destination) in staged) {
                File.Move(temporary, destination, overwrite: true);
                Console.WriteLine(value: $"Successfully compiled '{Path.GetFileName(path: sourcePath)}' -> '{destination}'.");
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

using Puck.Assets;
using Puck.World;
using Puck.World.Machines;
using Puck.World.Transpiler.Composition;

namespace Puck.Cli.Transpiler;

public static partial class CompileCommand {
    private const int MaximumReasonLength = 240;

    // One catalog per process: the compiled world's header records its fingerprint, and a tree run compiles many
    // documents under it.
    private static readonly Lazy<(WorldMachineCatalog Catalog, string Fingerprint)> CompiledWorldMachines = new(valueFactory: static () => {
        var catalog = CliWorldVocabulary.EnsureInstalled();

        return (catalog, CliWorldVocabulary.Fingerprint(catalog: catalog));
    });
    // `puck compile <name>.world.json`: a document has nothing to lower, so compiling it writes only its compiled
    // world. `besidePath` is where the document stands for the compiled world's name: the document itself, or its
    // mirrored place under a tree run's output.
    private static int CompileDocument(string documentPath, string besidePath, IDictionary<string, string>? written) {
        byte[] document;

        try {
            document = File.ReadAllBytes(path: documentPath);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"error: Could not read document '{CliPaths.ToDisplay(fullPath: documentPath)}': {exception.Message}");
            return 2;
        }

        return WriteCompiledWorld(
            besidePath: besidePath,
            composeAt: documentPath,
            document: document,
            written: written
        );
    }
    // Writes the compiled world of `document`, composed as the file `composeAt` (where its basis and imports resolve),
    // beside `besidePath`. A document that does not draw as a world on its own, such as a module fragment, has no
    // compiled world; that is reported and is not a failure.
    /// <summary>The line a document with no compiled world is reported by. The refusal comes from the world loader and
    /// names the source it composed, which crosses the output boundary as its display path; a fragment's refusal can
    /// quote the whole payload it could not parse, so only its head is kept.</summary>
    /// <param name="besidePath">The document the compiled world would sit beside.</param>
    /// <param name="composeAt">The source the loader composed, which its refusal may name.</param>
    /// <param name="reason">The loader's refusal.</param>
    /// <returns>The line.</returns>
    public static string NoCompiledWorld(string besidePath, string composeAt, string reason) {
        // The loader prefixes its refusal with the source exactly as supplied; later occurrences can be quoted data.
        var cause = (reason.StartsWith(comparisonType: StringComparison.Ordinal, value: (composeAt + " "))
            ? (CliPaths.ToDisplay(fullPath: composeAt) + reason[composeAt.Length..])
            : reason).ReplaceLineEndings(replacementText: " ");

        return $"No compiled world for '{CliPaths.ToDisplay(fullPath: besidePath)}': {((cause.Length > MaximumReasonLength) ? (cause[..MaximumReasonLength] + "...") : cause)}";
    }

    private static int WriteCompiledWorld(string composeAt, byte[] document, string besidePath, IDictionary<string, string>? written, WorldCompiledSource? sourceCompilation = null) {
        var (catalog, fingerprint) = CompiledWorldMachines.Value;
        var destination = CompiledWorld.Beside(documentPath: besidePath);

        if (!WorldSourceLoader.TryCompileWorld(
            catalog: catalog,
            catalogFingerprint: fingerprint,
            compiledWorld: out var bytes,
            document: document,
            sourceCompilation: sourceCompilation,
            path: composeAt,
            reason: out var reason
        )) {
            Console.WriteLine(value: NoCompiledWorld(besidePath: besidePath, composeAt: composeAt, reason: reason));
            return 0;
        }

        try {
            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            AtomicFile.WriteAllBytes(
                bytes: bytes,
                path: destination
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            Console.Error.WriteLine(value: $"error: Could not write compiled world '{CliPaths.ToDisplay(fullPath: destination)}': {exception.Message}");
            return 2;
        }

        if (written is not null) {
            written[destination] = composeAt;
        }

        Console.WriteLine(value: $"Compiled world '{CliPaths.ToDisplay(fullPath: destination)}' ({bytes.Length:N0} bytes).");

        return 0;
    }
}

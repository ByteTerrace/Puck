using System.Text.Json;
using Puck.Assets.Documents;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>
/// Resolves a name/source/hash asset reference row's document off disk — <see cref="WorldMusicRow.Source"/>, <see
/// cref="TableRow.Source"/>, <see cref="WorldTune.Source"/> and <see cref="WorldPatch.Source"/> resolve beside the
/// document that authored the row (<see cref="WorldDocumentPaths"/>). Shared by <see cref="WorldDefinitionValidator"/>
/// (which also hash-pins and vocabulary-checks the loaded document) and <c>Server.WorldServer</c> (which compiles it)
/// so the load step has exactly one source of truth.
/// </summary>
public static class WorldAssetRowLoader {
    private const string InlineJson = "data:application/json;base64,";

    private static string? EmbeddedSource<T>(CanonicalDocument<T> content, string expectedHash) =>
        string.Equals(content.Hash, expectedHash, StringComparison.Ordinal)
            ? InlineJson + Convert.ToBase64String(content.Bytes) : null;

    /// <summary>Whether the source is a self-contained audio asset rather than a filesystem path.</summary>
    public static bool IsEmbeddedAudio(string source) => source.StartsWith(InlineJson, StringComparison.Ordinal);

    /// <summary>Materializes the published audio assets so a remote listener needs no authority filesystem path.</summary>
    public static WorldDefinition EmbedAudio(WorldDefinition definition) => definition with {
        PatchesRaw = [.. definition.Patches.Select(patch => TryLoadPatch(definition.DocumentDirectory, patch, out var document, out _) &&
            EmbeddedSource(SynthPatchCanonicalizer.Canonicalize(document!), patch.Hash) is { } source ? patch with { Source = source } : null).OfType<WorldPatch>()],
        TunesRaw = [.. definition.Tunes.Select(tune => TryLoadTune(definition.DocumentDirectory, tune, out var document, out _) &&
            EmbeddedSource(AudioCanonicalizer.Canonicalize(document!), tune.Hash) is { } source ? tune with { Source = source } : null).OfType<WorldTune>()],
        Music = definition.Music?.Select(row => TryLoadMusic(definition.DocumentDirectory, row, out var document, out _) &&
            EmbeddedSource(MusicCanonicalizer.Canonicalize(document!), row.Hash) is { } source ? row with { Source = source } : null).OfType<WorldMusicRow>().ToArray(),
    };

    private static bool TryLoad<TDocument>(string? documentDirectory, string source, out TDocument? document, out string? error, string? hash = null) where TDocument : class {
        document = null;

        if (string.IsNullOrWhiteSpace(value: source)) {
            error = "source is required.";

            return false;
        }

        if (IsEmbeddedAudio(source)) {
            try {
                var embedded = Convert.FromBase64String(source[InlineJson.Length..]);
                if (!string.Equals(Puck.Assets.ContentPin.Compute(embedded).Hex, hash, StringComparison.Ordinal)) {
                    error = "embedded audio source does not match its content pin.";
                    return false;
                }
                document = JsonSerializer.Deserialize<TDocument>(embedded, DocumentJsonOptions.Shared);
                error = document is null ? "embedded audio source parsed to no document." : null;
                return document is not null;
            } catch (Exception exception) when (exception is FormatException or JsonException) {
                error = "embedded audio source is malformed.";
                return false;
            }
        }

        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: documentDirectory,
            path: source,
            reason: out var unresolved,
            resolved: out var resolved
        )) {
            error = $"source {unresolved}.";

            return false;
        }

        if (!File.Exists(path: resolved)) {
            error = $"source '{source}' does not exist.";

            return false;
        }

        byte[] bytes;

        try {
            bytes = File.ReadAllBytes(path: resolved);
        } catch (IOException exception) {
            error = $"source '{source}' could not be read: {exception.Message}";

            return false;
        }

        WorldBootWork.Count(kind: WorldBootWork.AssetLoads);

        try {
            document = JsonSerializer.Deserialize<TDocument>(
                utf8Json: bytes,
                options: Puck.Assets.Documents.DocumentJsonOptions.Shared
            );
        } catch (JsonException exception) {
            error = $"source '{source}' is not valid {typeof(TDocument).Name} JSON: {exception.Message}";

            return false;
        }

        if (document is null) {
            error = $"source '{source}' parsed to no document.";

            return false;
        }

        error = null;

        return true;
    }

    /// <summary>Lists every asset row a definition declares, each with the file family it reads: <c>music</c>,
    /// <c>table</c>, <c>tune</c>, and <c>patch</c>, in that order and in declaration order within a family.</summary>
    /// <param name="definition">The definition to list.</param>
    /// <returns>The family, name, and authored source of each row; a <see langword="null"/> row is skipped.</returns>
    public static IEnumerable<(string Family, string Name, string Source)> Rows(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        foreach (var row in (definition.Music ?? [])) {
            if (row is not null) { yield return ("music", row.Name, row.Source); }
        }
        foreach (var row in (definition.Tables ?? [])) {
            if (row is not null) { yield return ("table", row.Name, row.Source); }
        }
        foreach (var row in definition.Tunes) {
            if (row is not null) { yield return ("tune", row.Name, row.Source); }
        }
        foreach (var row in definition.Patches) {
            if (row is not null) { yield return ("patch", row.Name, row.Source); }
        }
    }
    /// <summary>Loads a <see cref="WorldMusicRow"/>'s referenced <c>puck.music.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, which
    /// refuses a relative source by name.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadMusic(string? documentDirectory, WorldMusicRow row, out MusicDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source,
            hash: row.Hash
        );
    /// <summary>Loads a <see cref="WorldPatch"/>'s referenced <c>puck.synthesizer-patch.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, which
    /// refuses a relative source by name.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadPatch(string? documentDirectory, WorldPatch row, out SynthPatchDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source,
            hash: row.Hash
        );
    /// <summary>Loads a <see cref="TableRow"/>'s referenced <c>puck.table.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, which
    /// refuses a relative source by name.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadTable(string? documentDirectory, TableRow row, out TableDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source
        );
    /// <summary>Loads a <see cref="WorldTune"/>'s referenced <c>puck.tune.v1</c> document.</summary>
    /// <param name="documentDirectory">The directory of the document that authored the row
    /// (<see cref="WorldDefinition.DocumentDirectory"/>), or <see langword="null"/> for a document with none, which
    /// refuses a relative source by name.</param>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadTune(string? documentDirectory, WorldTune row, out AudioDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            documentDirectory: documentDirectory,
            error: out error,
            source: row.Source,
            hash: row.Hash
        );
}

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
    private static bool TryLoad<TDocument>(string? documentDirectory, string source, out TDocument? document, out string? error) where TDocument : class {
        document = null;

        if (string.IsNullOrWhiteSpace(value: source)) {
            error = "source is required.";

            return false;
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
            source: row.Source
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
            source: row.Source
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
            source: row.Source
        );
}

using System.Text.Json;
using Puck.Forge.Authoring;

namespace Puck.World;

/// <summary>
/// Resolves a name/source/hash asset reference row's document off disk — <see cref="WorldMusicRow.Source"/>/<see
/// cref="WorldJudgeRow.Source"/>/<see cref="WorldTune.Source"/>/<see cref="WorldPatch.Source"/> resolved against <see
/// cref="AppContext.BaseDirectory"/> when relative (the same convention <c>WorldAddonRow.ModulePath</c> already
/// resolves against — Assets are Content-copied beside the built executable). Shared by
/// <see cref="WorldDefinitionValidator"/> (which also hash-pins and vocabulary-checks the loaded document) and
/// <c>Server.WorldServer</c> (which compiles it) so the load step has exactly one source of truth.
/// </summary>
public static class WorldAssetRowLoader {
    private static bool TryLoad<TDocument>(string source, out TDocument? document, out string? error) where TDocument : class {
        document = null;

        if (string.IsNullOrWhiteSpace(value: source)) {
            error = "source is required.";

            return false;
        }

        var resolved = (Path.IsPathRooted(path: source)
            ? source
            : Path.Combine(
                path1: AppContext.BaseDirectory,
                path2: source
            )
        );

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

    /// <summary>Loads a <see cref="WorldJudgeRow"/>'s referenced <c>puck.judge.v1</c> document.</summary>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadJudge(WorldJudgeRow row, out JudgeDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            error: out error,
            source: row.Source
        );
    /// <summary>Loads a <see cref="WorldMusicRow"/>'s referenced <c>puck.music.v1</c> document.</summary>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadMusic(WorldMusicRow row, out MusicDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            error: out error,
            source: row.Source
        );
    /// <summary>Loads a <see cref="WorldTune"/>'s referenced <c>puck.audio.v1</c> document.</summary>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadTune(WorldTune row, out AudioDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            error: out error,
            source: row.Source
        );
    /// <summary>Loads a <see cref="WorldPatch"/>'s referenced <c>puck.synth.v1</c> document.</summary>
    /// <param name="row">The reference row.</param>
    /// <param name="document">The loaded document, when this method returns <see langword="true"/>.</param>
    /// <param name="error">A human-readable failure reason, when this method returns <see langword="false"/>.</param>
    public static bool TryLoadPatch(WorldPatch row, out SynthPatchDocument? document, out string? error) =>
        TryLoad(
            document: out document,
            error: out error,
            source: row.Source
        );
}

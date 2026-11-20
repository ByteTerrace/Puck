using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Launcher.Release;

/// <summary>The names inside a player build (<c>puck world player</c>'s folder, and so a player release's payload) that its
/// producer, its publisher and its installer agree on.</summary>
public static class PlayerBuildLayout {
    /// <summary>The build's record: its world, cache directory, backends, targets and how each boot went.</summary>
    public const string RecordName = "player.json";
    /// <summary>The device caches a boot reads and fills (<c>--cache-dir</c>), relative to the build.</summary>
    public const string CacheDirectoryName = "cache";
    /// <summary>The baked-mesh store inside <see cref="CacheDirectoryName"/>.</summary>
    public const string MeshesDirectoryName = "meshes";
    /// <summary>The launcher: the stub and the installer, each one self-contained file, which the installer lays beside
    /// an install's release record.</summary>
    public const string LauncherDirectoryName = "launcher";
    /// <summary>The producing boots' logs, which a release never ships.</summary>
    public const string LogsDirectoryName = "player-logs";

    /// <summary>Reads a folder's player-build record (<see cref="RecordName"/>) and whether it says the build is
    /// complete. The producer writes it incomplete first, so its own baking boots read as incomplete, and complete last;
    /// an installed version's directory holds the complete record its release carried.</summary>
    /// <param name="folder">The folder that may hold a player build.</param>
    /// <param name="record">The record, or an empty object when none could be read.</param>
    /// <param name="why">Why the folder is not a complete player build, or <see langword="null"/> when it is.</param>
    /// <returns>Whether the folder holds a complete player build.</returns>
    public static bool TryReadComplete(string folder, out JsonNode record, out string? why) {
        record = new JsonObject();
        try {
            record = (JsonNode.Parse(utf8Json: File.ReadAllBytes(path: Path.Combine(path1: folder, path2: RecordName))) ?? new JsonObject());
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) {
            why = $"holds no readable {RecordName}: {exception.Message}";
            return false;
        }
        why = (((record is JsonObject) && (record["complete"] is JsonValue complete) && complete.TryGetValue(value: out bool done) && done) ? null : $"is not a complete player build ({RecordName} says so)");
        return (why is null);
    }
    /// <summary>Returns whether this process runs from a complete player build: an installed player release or a
    /// finished <c>puck world player</c> folder, the build a player runs, which never bakes.</summary>
    /// <returns>Whether the process's base directory holds a complete player build.</returns>
    public static bool ProcessRunsAsPlayer() => TryReadComplete(folder: AppContext.BaseDirectory, record: out _, why: out _);
}

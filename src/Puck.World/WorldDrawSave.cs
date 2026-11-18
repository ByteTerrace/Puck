using Puck.Assets;
using System.Text;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Confinement;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The local authority portion of a world save. Packages and authored source carry only declarations.</summary>
internal static class WorldDrawSave {
    private sealed record Image(byte[] Seed, WorldDrawPositions? Positions, byte[]? Rows);

    private static string Location(WorldStateRoot root, string source, string? entry) {
        var origin = PuckPaths.Relative(Environment.CurrentDirectory, Path.GetFullPath(source));
        var id = ContentPin.Compute(Encoding.UTF8.GetBytes(origin + "\0" + entry));
        var directory = root.PathOf("world-authorities");
        if (!OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return Path.Combine(directory, id.Hex + ".json");
    }

    internal static bool TryOpen(bool authority, WorldStateRoot root, string source, string? entry, out WorldPrivateDrawKeys keys, out string failure) {
        try {
            keys = authority ? Open(root, source, entry) : WorldPrivateDrawKeys.Preview;
            failure = string.Empty;
            return true;
        } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) {
            keys = null!;
            failure = $"[world] authority save refused: {error.Message}";
            return false;
        }
    }

    private static WorldPrivateDrawKeys Open(WorldStateRoot root, string source, string? entry) {
        var path = Location(root, source, entry);
        using var held = FileLock.Acquire(path + ".lock");
        if (!File.Exists(path)) {
            var created = WorldPrivateDrawKeys.Create();
            Write(path, new(created.SerializeAuthorityState()!, null, null));
            return created;
        }
        var image = JsonSerializer.Deserialize<Image>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("Empty world authority save.");
        var keys = WorldPrivateDrawKeys.FromAuthorityState(image.Seed);
        if (image.Positions is { } positions) {
            var rows = JsonSerializer.Deserialize(image.Rows!, WorldJsonContext.Default.WorldStateSection)
                ?? throw new InvalidDataException("Missing private rows in world save.");
            keys.ResumeSave(rows, positions);
        }
        return keys;
    }

    internal static void Save(WorldStateRoot root, string target, WorldDefinitionSource source, WorldServer server, WorldDefinition snapshot) {
        if (server.DrawKeys.SerializeAuthorityState() is not { } seed) { return; }
        var path = source.CompositionOrigin is { } origin && PuckPaths.Comparer.Equals(Path.GetFullPath(target), Path.GetFullPath(origin.StagedPath))
            ? Location(root, origin.Source, origin.Entry) : Location(root, target, null);
        var positions = WorldPrivateDrawKeys.Positions(snapshot);
        var rows = positions is null ? null : JsonSerializer.SerializeToUtf8Bytes(
            new WorldStateSection(World: snapshot.AuthoredState.Where(r => r.Draw?.PrivateStream == true).ToArray()),
            WorldJsonContext.Default.WorldStateSection);
        using var held = FileLock.Acquire(path + ".lock");
        Write(path, new(seed, positions, rows));
    }

    private static void Write(string path, Image image) {
        using var pending = AtomicFile.StageAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(image), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        pending.CommitDurably();
    }
}

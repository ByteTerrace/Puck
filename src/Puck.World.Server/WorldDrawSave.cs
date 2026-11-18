using System.Text;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Confinement;

namespace Puck.World.Server;

/// <summary>The local authority root. A saved document owns its cells, masks and cursors.
/// A boot reserves its own timeline before it may sample, including after an interrupted save.</summary>
public static class WorldDrawSave {
    /// <summary>Canonical checkout and entry identity for local authority storage and composition staging.</summary>
    public static string WorldIdentity(string source, string? entry = null) {
        var path = PuckPaths.Slashed(Path.GetFullPath(source));
        if (OperatingSystem.IsWindows()) { path = path.ToUpperInvariant(); }
        return ContentPin.Compute(Encoding.UTF8.GetBytes(path + "\0" + entry)).Hex;
    }

    private static readonly JsonSerializerOptions SaveJson = new() { AllowDuplicateProperties = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    private static WorldDrawSaveState Read(string path) {
        WorldDrawSaveState? image;
        try { image = JsonSerializer.Deserialize<WorldDrawSaveState>(File.ReadAllBytes(path), SaveJson); }
        catch (JsonException error) { throw new InvalidDataException("PrivateDrawSaveMalformed: " + error.Message, error); }
        if (image is not { Seed.Length: 32, Generation: >= 0 and < long.MaxValue, Positions.IsValid: true }
            || image.Seed.AsSpan().IndexOfAnyExcept((byte)0) < 0) {
            throw new InvalidDataException("PrivateDrawSaveMalformed: invalid seed, generation or site positions.");
        }
        return image;
    }

    /// <summary>Reserves a saved timeline on the first private installation. Unsaved boots write nothing.</summary>
    public static WorldPrivateDrawKeys Open(WorldStateRoot root, string source, string? entry) {
        var identity = WorldIdentity(source, entry);
        var path = Path.Combine(root.PathOf("world-authorities"), identity + ".json");
        return WorldPrivateDrawKeys.Local(identity, () => {
            try {
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                using var held = FileLock.Acquire(path + ".lock") ?? throw new IOException("Could not acquire the private draw save lock.");
                var saved = Read(path);
                var next = saved with { Generation = checked(saved.Generation + 1) };
                Write(path, next);
                return next;
            } catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) {
                throw new InvalidDataException($"authority save refused: {error.Message}", error);
            }
        });
    }

    /// <summary>Publishes the authority portion first and holds the save lock until the document writer finishes.</summary>
    public static IDisposable? BeginSave(WorldStateRoot root, string target, WorldDefinitionSource source, WorldServer server, WorldDefinition snapshot) {
        if (!snapshot.AuthoredState.Any(row => row.Draw?.PrivateStream == true)) { return null; }
        var positions = WorldPrivateDrawKeys.Positions(snapshot) ?? throw new InvalidDataException("PrivateDrawPositionsMissing");
        var seed = server.DrawKeys.SerializeAuthorityState() ?? throw new InvalidDataException("PrivateDrawSeedMissing");
        var identity = source.CompositionOrigin is { } origin && PuckPaths.Comparer.Equals(Path.GetFullPath(target), Path.GetFullPath(origin.StagedPath))
            ? WorldIdentity(origin.Source, origin.Entry) : WorldIdentity(target);
        var path = Path.Combine(root.PathOf("world-authorities"), identity + ".json");
        if (!OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var held = FileLock.Acquire(path + ".lock") ?? throw new IOException("Could not acquire the private draw save lock.");
        try {
            var generation = Math.Max(server.DrawKeys.Generation, server.DrawKeys.RootGeneration);
            if (File.Exists(path)) {
                var existing = Read(path);
                generation = Math.Max(generation, existing.Generation);
                positions = positions with { Last = Math.Max(positions.Last, existing.Positions.Last) };
            }
            Write(path, new(seed, generation, positions));
            return held;
        } catch {
            held.Dispose();
            throw;
        }
    }

    private static void Write(string path, WorldDrawSaveState image) =>
        AtomicFile.WriteAllBytesDurably(path, JsonSerializer.SerializeToUtf8Bytes(image), UnixFileMode.UserRead | UnixFileMode.UserWrite);
}

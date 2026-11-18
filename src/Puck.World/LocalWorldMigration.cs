using Puck.Abstractions;
using Puck.Abstractions.Waiting;
using Puck.Assets;
using Puck.Confinement;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Retires files written by the old local authority at the next local activation.</summary>
internal static class LocalWorldMigration {
    internal static void RemoveRetiredPrivateDrawFiles(WorldStateRoot state) {
        var seeds = state.PathOf("world-authorities");
        var compositions = state.PathOf("compositions");
        if (!Directory.Exists(seeds) && !Directory.Exists(compositions)) { return; }

        var deadline = WaitDeadline.In(TimeSpan.FromSeconds(5));
        using var gate = FileLock.Acquire(state.PathOf("private-draw-migration.lock"), expired: () => deadline.HasPassed)
            ?? throw new IOException("Timed out waiting for the local private-draw migration lock.");
        using var root = ConfinedDirectory.OpenAnchor(state.FullPath, create: false);
        if (root is null) { return; }

        using (var directory = root.Descend("world-authorities", create: false)) {
            if (directory is not null) {
                foreach (var file in Directory.EnumerateFiles(seeds, "*.json")) {
                    var name = Path.GetFileName(file);
                    if (ContentPin.TryParseHex(Path.GetFileNameWithoutExtension(name), out _) && directory.DeleteFile(name)) {
                        Console.Error.WriteLine($"[world] removed retired private-draw seed '{PuckPaths.Normalize(file)}'");
                    }
                }
            }
        }
        using var oldCompositions = root.Descend("compositions", create: false);
        if (oldCompositions is null) { return; }

        const string ArchiveName = "retired-composition-saves";
        foreach (var directory in Directory.EnumerateDirectories(compositions)) {
            var identity = Path.GetFileName(directory);
            if (!ContentPin.TryParseHex(identity, out _) || !Directory.Exists(Path.Combine(directory, "staged"))) { continue; }
            using var old = oldCompositions.TryOpenChildForRemoval(identity);
            if (old is null) {
                Console.Error.WriteLine($"[world] old composition at '{PuckPaths.Normalize(directory)}' is held. Its saved entry remains there.");
                continue;
            }
            using var archive = root.Descend(ArchiveName, create: true)!;
            old.MoveTo(oldCompositions, identity, archive, identity);
            var saved = state.PathOf($"{ArchiveName}/{identity}");
            Console.Error.WriteLine($"[world] archived old composition at '{PuckPaths.Normalize(saved)}'; move the saved entry document beside its source as .<entry>.save.world.json to resume it.");
        }
    }
}

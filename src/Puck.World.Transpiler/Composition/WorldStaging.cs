using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Assets;

namespace Puck.World.Transpiler.Composition;

/// <summary>Writes a world a source compiled into a directory outside the tree the source sits in: the one staging
/// step shared by <c>puck test</c>, which boots the worlds a source's tests generate, and the game's boot of a
/// composition source, which boots one world of the set a source declares. Worlds staged into one directory reach each
/// other by document name, as they did beside their source.</summary>
public static class WorldStaging {
    /// <summary>Stages every world a composition source declares into <paramref name="directory"/>, restaged whole so
    /// a world the source no longer declares cannot be reached, and answers the staged document of the world a boot
    /// starts in: the one <paramref name="entry"/> names, else the one the source declares its entry. The worlds reach
    /// each other across their borders by document name, so they share one staging directory.</summary>
    /// <param name="path">The composition source, as the refusals name it.</param>
    /// <param name="worlds">The worlds the source's compile declared.</param>
    /// <param name="entry">The world a boot was asked to start in, or <see langword="null"/> for the declared entry.</param>
    /// <param name="directory">The directory the source's worlds are staged into; deleted and recreated.</param>
    /// <param name="entryPath">The full path of the entry world's staged document on success.</param>
    /// <param name="entryName">The entry world's declared name on success.</param>
    /// <param name="reason">The named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when an entry was chosen and every world composed and was written.</returns>
    public static bool TryStageComposition(string path, IReadOnlyList<WorldCompiledWorld> worlds, string? entry, string directory, out string entryPath, out string entryName, out string reason) {
        entryPath = string.Empty;
        entryName = string.Empty;

        var declared = string.Join(
            separator: ", ",
            values: worlds.Select(selector: static world => world.Name)
        );
        WorldCompiledWorld chosen;

        if (entry is not null) {
            if (worlds.FirstOrDefault(predicate: world => string.Equals(
                a: world.Name,
                b: entry,
                comparisonType: StringComparison.Ordinal
            )) is not { } named) {
                reason = $"--entry '{entry}' names no world '{path}' declares; it declares {declared}.";

                return false;
            }

            chosen = named;
        } else if (worlds.FirstOrDefault(predicate: static world => world.Entry) is { } declaredEntry) {
            chosen = declaredEntry;
        } else {
            reason = $"'{path}' declares worlds {declared} and none is its entry — write `entry world <name> = ...` for the world a boot starts in, or name one with --entry.";

            return false;
        }

        try {
            directory = Path.GetFullPath(path: directory);
            var sourcePath = Path.GetFullPath(path: path);
            var sourceDirectory = Path.GetDirectoryName(path: sourcePath)!;
            var relativeSource = Path.GetRelativePath(path: sourcePath, relativeTo: directory).Replace(newChar: '/', oldChar: '\\');

            if (
                (Path.GetDirectoryName(path: Path.TrimEndingDirectorySeparator(path: directory)) is null) ||
                (!Path.IsPathRooted(path: relativeSource) && (relativeSource != "..") && !relativeSource.StartsWith(comparisonType: StringComparison.Ordinal, value: "../"))
            ) {
                reason = $"'{path}' cannot be staged under {directory}: the staging directory must not be a filesystem root or contain the composition source.";
                return false;
            }
            if (Directory.Exists(path: directory)) {
                Directory.Delete(
                    path: directory,
                    recursive: true
                );
            }

            foreach (var world in worlds) {
                if (!TryWrite(
                    directory: directory,
                    name: world.Name,
                    path: out var staged,
                    reason: out var composeReason,
                    sourceDirectory: sourceDirectory,
                    world: ((JsonObject)JsonNode.Parse(utf8Json: world.Json)!)
                )) {
                    reason = $"'{path}' world '{world.Name}' does not compose: {composeReason}";

                    return false;
                }

                if (world == chosen) {
                    entryPath = staged;
                }
            }
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"'{path}' could not be staged under {directory}: {exception.Message}";

            return false;
        }

        entryName = chosen.Name;
        reason = string.Empty;

        return true;
    }
    /// <summary>Writes one compiled world into <paramref name="directory"/> as its document file, its basis and
    /// imports composed beside its source first. File references are relocated once, and fonts that cannot retain a
    /// relative path across drives are stored beneath the staging directory without changing their declared pins.</summary>
    /// <param name="world">The compiled world document.</param>
    /// <param name="name">The world's document name, which names the file it is written to.</param>
    /// <param name="sourceDirectory">The full path of the directory of the source the world was compiled from.</param>
    /// <param name="directory">The directory the world is staged into; created when absent.</param>
    /// <param name="path">The full path of the written document on success.</param>
    /// <param name="reason">The composition or font-staging refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the world composed and was written.</returns>
    public static bool TryWrite(JsonObject world, string name, string sourceDirectory, string directory, out string path, out string reason) {
        _ = Directory.CreateDirectory(path: directory);
        path = Path.GetFullPath(path: Path.Combine(
            path1: directory,
            path2: WorldDocumentName.DocumentFile(name: name)
        ));
        var authored = Path.Combine(
            path1: sourceDirectory,
            // This root is generated, not the source's same-named basis file. The resolver returns only .puck or
            // .world.json carriers, so this virtual identity cannot falsely cycle with a real chain member.
            path2: (WorldDocumentName.DocumentFile(name: name) + ".staged")
        );
        var origins = new WorldDocumentOrigins();

        if (!PuckDocumentComposer.TryComposeWorldDocument(
            origins: origins,
            chainBytes: out _,
            composed: out var composed,
            reason: out reason,
            rootBytes: CanonicalJsonDocument.Serialize(node: world),
            rootResolvedPath: authored
        )) {
            return false;
        }

        var document = (composed ?? world);

        WorldDocumentPaths.RelocateDocumentFields(module: document, origins: origins, sourceDocumentPath: authored, targetDocumentPath: path);
        ContentAddressedStore? assets = null;

        foreach (var row in ((document["text"]?["fonts"] as JsonArray) ?? []).OfType<JsonObject>()) {
            // The winning scalar's authored spelling survives basis/import merges and every relocation. An authored
            // absolute path stays invalid, even when it happens to name the same file as an inherited relative path.
            if (!origins.TryGetOrigin(value: row["source"], origin: out var origin) ||
                (JsonNode.Parse(json: origin.Json) is not JsonValue original) || !original.TryGetValue<string>(value: out var source)) {
                continue;
            }
            if (!WorldDocumentPaths.IsPortableRelativeFilePath(path: source)) {
                row["source"] = source;
                continue;
            }
            if ((row["source"] is not JsonValue current) ||
                !current.TryGetValue<string>(value: out var relocated) || !Path.IsPathRooted(path: relocated)) {
                continue;
            }
            try {
                assets ??= new ContentAddressedStore(root: Path.Combine(path1: directory, path2: "assets"));
                var pin = assets.Put(content: File.ReadAllBytes(path: relocated));

                row["source"] = $"assets/{ContentAddressedStore.ObjectRelativePath(pin: pin)}";
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                reason = $"font source '{source}' could not be staged: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
                return false;
            }
        }

        File.WriteAllBytes(
            bytes: CanonicalJsonDocument.Serialize(node: document),
            path: path
        );

        return true;
    }
}

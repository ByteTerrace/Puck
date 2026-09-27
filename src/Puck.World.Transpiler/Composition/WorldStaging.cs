using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;

namespace Puck.World.Transpiler.Composition;

/// <summary>Writes a world a source compiled into a directory outside the tree the source sits in: the one staging
/// step shared by <c>puck test</c>, which boots the worlds a source's tests generate, and the game's boot of a
/// composition source, which boots one world of the set a source declares. Worlds staged into one directory reach each
/// other by document name, as they did beside their source.</summary>
public static class WorldStaging {
    // A basis and an imports[].document resolve against the document's own directory, so they are rooted at the
    // source's directory before the document moves.
    private static void Reroot(JsonObject world, string sourceDirectory) {
        if (
            (world[propertyName: WorldDocumentBasis.BasisMemberName]?.GetValue<string>() is { } basis) &&
            !Path.IsPathRooted(path: basis)
        ) {
            world[propertyName: WorldDocumentBasis.BasisMemberName] = Rooted(
                relative: basis,
                sourceDirectory: sourceDirectory
            );
        }

        foreach (var entry in ((world[propertyName: WorldDocumentBasis.ImportsMemberName] as JsonArray) ?? [])) {
            if (
                (entry is JsonObject import) &&
                (import[propertyName: WorldImport.DocumentMemberName]?.GetValue<string>() is { } document) &&
                !Path.IsPathRooted(path: document)
            ) {
                import[propertyName: WorldImport.DocumentMemberName] = Rooted(
                    relative: document,
                    sourceDirectory: sourceDirectory
                );
            }
        }
    }
    private static string Rooted(string sourceDirectory, string relative) => Path.GetFullPath(path: Path.Combine(
        path1: sourceDirectory,
        path2: relative
    )).Replace(
        newChar: '/',
        oldChar: '\\'
    );

    /// <summary>Stages every world a composition source declares into <paramref name="directory"/>, restaged whole so
    /// a world the source no longer declares cannot be reached, and answers the staged document of the world a boot
    /// starts in: the one <paramref name="entry"/> names, else the one the source declares its entry. The worlds reach
    /// each other across their borders by document name, so they are staged together or not at all.</summary>
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

        var sourceDirectory = Path.GetDirectoryName(path: Path.GetFullPath(path: path))!;

        try {
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
    /// imports composed into it first so the staged document depends on nothing but its staged siblings.</summary>
    /// <param name="world">The compiled world document. Its <c>basis</c> and <c>imports</c> are rewritten in place.</param>
    /// <param name="name">The world's document name, which names the file it is written to.</param>
    /// <param name="sourceDirectory">The full path of the directory of the source the world was compiled from.</param>
    /// <param name="directory">The directory the world is staged into; created when absent.</param>
    /// <param name="path">The full path of the written document on success.</param>
    /// <param name="reason">The composer's named refusal, or empty on success.</param>
    /// <returns><see langword="true"/> when the world composed and was written.</returns>
    public static bool TryWrite(JsonObject world, string name, string sourceDirectory, string directory, out string path, out string reason) {
        _ = Directory.CreateDirectory(path: directory);
        path = Path.GetFullPath(path: Path.Combine(
            path1: directory,
            path2: WorldDocumentName.DocumentFile(name: name)
        ));
        Reroot(
            sourceDirectory: sourceDirectory,
            world: world
        );
        // Every other relative path the world authors resolves beside its document, so it is re-expressed from the
        // source's directory to the staged one.
        WorldDocumentPaths.RelocateDocumentFields(
            module: world,
            sourceDocumentPath: Path.Combine(
                path1: sourceDirectory,
                path2: WorldDocumentName.DocumentFile(name: name)
            ),
            targetDocumentPath: path
        );

        if (!PuckDocumentComposer.TryComposeWorldDocument(
            chainBytes: out _,
            composed: out var composed,
            reason: out reason,
            rootBytes: CanonicalJsonDocument.Serialize(node: world),
            rootResolvedPath: path
        )) {
            return false;
        }

        File.WriteAllBytes(
            bytes: CanonicalJsonDocument.Serialize(node: (composed ?? world)),
            path: path
        );

        return true;
    }
}

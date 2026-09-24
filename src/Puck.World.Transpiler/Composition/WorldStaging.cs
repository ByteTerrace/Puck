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

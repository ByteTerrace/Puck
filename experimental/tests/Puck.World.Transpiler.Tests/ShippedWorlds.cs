using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The one enumeration of the shipped world corpus, the one compilation of each committed source, and the one
/// structural JSON comparison, that every corpus-wide gate in this assembly runs over.</summary>
/// <remarks>Enumeration is recursive. <see cref="Sources"/> is every committed <c>.puck</c> world source, the corpus of
/// gates that judge a source as committed text — reference linting, formatter idempotence, formatting preserving what
/// a source compiles to. <see cref="Files"/> is every hand-authored world document, gated through its own
/// decompilation. Three private copies of the walk could disagree about which documents each gate covers.</remarks>
internal static class ShippedWorlds {
    /// <summary>Returns every hand-authored <c>*.world.json</c> under the worlds directory, recursively, as
    /// forward-slashed paths relative to it, in ordinal order: the documents no source carries
    /// (<see cref="Composition.PuckDocumentComposer.TryCarriers"/>), since a document file sharing the name of a source
    /// that emits a document is never read in the source's place.</summary>
    /// <returns>The decompilation corpus.</returns>
    public static IEnumerable<string> FilePaths() {
        var worldsDirectory = FindDirectory();

        if (!Composition.PuckDocumentComposer.TryCarriers(
            carriers: out var carriers,
            directory: worldsDirectory,
            libraries: out _,
            option: SearchOption.AllDirectories,
            reason: out var reason
        )) {
            throw new InvalidDataException(message: reason);
        }

        return carriers
            .Where(predicate: static carrier => !carrier.IsSource)
            .Select(selector: carrier => Path.GetRelativePath(
                path: carrier.Path,
                relativeTo: worldsDirectory
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            ));
    }
    public static TheoryData<string> Files() => new(values: FilePaths());
    /// <summary>Returns every <c>*.puck</c> source of the asset packages under the repository's <c>worlds</c>
    /// directory, recursively, as forward-slashed absolute paths in ordinal order.</summary>
    /// <returns>The package sources' absolute paths.</returns>
    public static IEnumerable<string> PackageSourcePaths() {
        var packages = Path.GetFullPath(path: Path.Combine(
            FindDirectory(),
            "..",
            "..",
            "..",
            "..",
            "worlds"
        ));

        return (Directory.Exists(path: packages)
            ? Directory.EnumerateFiles(
                path: packages,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*.puck"
            ).Select(selector: static path => path.Replace(
                newChar: '/',
                oldChar: '\\'
            )).Order(comparer: StringComparer.Ordinal)
            : []
        );
    }
}

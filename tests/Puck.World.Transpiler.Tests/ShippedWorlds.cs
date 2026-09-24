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
    // A committed source is the largest thing the suite compiles and several gates judge the same compilation, so
    // each source compiles once per run.
    private static readonly ConcurrentDictionary<string, Lazy<WorldCompilation>> Compilations = new(comparer: StringComparer.Ordinal);

    private static IEnumerable<string> Enumerate(string pattern) {
        var worldsDirectory = FindDirectory();

        return Directory.GetFiles(
            path: worldsDirectory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: pattern
        )
            .Select(selector: path => Path.GetRelativePath(
            path: path,
            relativeTo: worldsDirectory
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        ))
            .Order(comparer: StringComparer.Ordinal);
    }

    /// <summary>Returns a committed source compiled as <c>puck compile</c> compiles it, from its own path, failing the
    /// test when it reports an error.</summary>
    /// <param name="relativePath">A forward-slashed source path relative to the worlds directory.</param>
    /// <returns>The compilation. Its document is the caller's own copy; its source map and diagnostics are shared
    /// by every caller and are read, never written.</returns>
    public static WorldCompilation Compile(string relativePath) {
        var shared = Compilations.GetOrAdd(
            key: relativePath,
            valueFactory: static path => new Lazy<WorldCompilation>(valueFactory: () => {
                var sourcePath = PathOf(relativePath: path);

                return WorldCompiler.Compile(
                    source: File.ReadAllText(path: sourcePath),
                    sourcePath: sourcePath
                );
            })
        ).Value;

        Assert.False(
            condition: shared.Diagnostics.HasErrors,
            userMessage: $"{relativePath} does not compile:{Environment.NewLine}{shared.Diagnostics.FormatReport(File.ReadAllText(path: PathOf(relativePath: relativePath)))}"
        );

        return (shared with { Json = ((JsonObject?)shared.Json?.DeepClone()) });
    }
    /// <summary>Returns the absolute path of a file under the worlds directory.</summary>
    /// <param name="relativePath">A forward-slashed path relative to the worlds directory.</param>
    /// <returns>The absolute path.</returns>
    public static string PathOf(string relativePath) => Path.Combine(
        path1: FindDirectory(),
        path2: relativePath
    );
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
    /// <summary>Returns the absolute path of <c>src/Puck.World/Assets/worlds</c>, walked up from the test
    /// runner's own directory.</summary>
    /// <returns>The worlds directory.</returns>
    public static string FindDirectory() {
        var dir = AppContext.BaseDirectory;

        while (dir is not null) {
            var candidate = Path.Combine(
                dir,
                "src",
                "Puck.World",
                "Assets",
                "worlds"
            );

            if (Directory.Exists(path: candidate)) {
                return candidate;
            }

            dir = Path.GetDirectoryName(path: dir);
        }

        throw new DirectoryNotFoundException(message: "Could not locate src/Puck.World/Assets/worlds directory from test runner.");
    }
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
    /// <summary>Returns every <c>*.puck</c> world source under the worlds directory, recursively, as forward-slashed
    /// paths relative to it, in ordinal order.</summary>
    /// <returns>The committed source corpus.</returns>
    public static IEnumerable<string> SourcePaths() => Enumerate(pattern: "*.puck");
    /// <summary>Returns the committed source corpus as xUnit theory data.</summary>
    /// <returns>The source corpus as xUnit theory data.</returns>
    public static TheoryData<string> Sources() => new(values: SourcePaths());
}
/// <summary>Structural JSON comparison that distinguishes a missing property from an extra one.</summary>
internal static class JsonMismatch {
    /// <summary>Returns the first structural difference between two JSON trees, or <see langword="null"/> when
    /// they are identical.</summary>
    /// <param name="expected">The reference tree.</param>
    /// <param name="actual">The tree under test.</param>
    /// <param name="path">The JSON-pointer prefix reported differences are rooted at.</param>
    /// <returns>A one-line description of the first difference, or <see langword="null"/>.</returns>
    public static string? Find(JsonNode? expected, JsonNode? actual, string path) {
        if (
            (expected is null) &&
            (actual is null)
        ) {
            return null;
        }
        if (expected is null) {
            return $"{path}: expected null, actual '{actual?.ToJsonString()}'";
        }
        if (actual is null) {
            return $"{path}: expected '{expected.ToJsonString()}', actual null";
        }
        if (expected.GetValueKind() != actual.GetValueKind()) {
            return $"{path}: kinds differ: expected {expected.GetValueKind()}, actual {actual.GetValueKind()}";
        }

        if (
            (expected is JsonObject expectedObject) &&
            (actual is JsonObject actualObject)
        ) {
            foreach (var (key, value) in expectedObject) {
                if (!actualObject.ContainsKey(propertyName: key)) {
                    return $"{path}/{key}: missing property";
                }
                var diff = Find(
                    value,
                    actualObject[key],
                    $"{path}/{key}"
                );

                if (diff is not null) {
                    return diff;
                }
            }
            foreach (var (key, _) in actualObject) {
                if (!expectedObject.ContainsKey(propertyName: key)) {
                    return $"{path}/{key}: unexpected extra property";
                }
            }
            return null;
        }

        if (
            (expected is JsonArray expectedArray) &&
            (actual is JsonArray actualArray)
        ) {
            if (expectedArray.Count != actualArray.Count) {
                return $"{path}: array length expected {expectedArray.Count}, actual {actualArray.Count}";
            }
            for (var i = 0; (i < expectedArray.Count); i++) {
                var diff = Find(
                    expectedArray[i],
                    actualArray[i],
                    $"{path}[{i}]"
                );

                if (diff is not null) {
                    return diff;
                }
            }
            return null;
        }

        if (
            (expected is JsonValue expectedValue) &&
            (actual is JsonValue actualValue)
        ) {
            return (JsonNode.DeepEquals(
                node1: expectedValue,
                node2: actualValue
            )
                ? null
                : $"{path}: value expected '{expectedValue.ToJsonString()}', actual '{actualValue.ToJsonString()}'"
            );
        }

        return $"{path}: kinds differ";
    }
}

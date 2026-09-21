using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The one enumeration of the shipped world corpus, and the one structural JSON comparison, that every
/// corpus-wide gate in this assembly runs over.</summary>
/// <remarks>Enumeration is recursive. <see cref="Sources"/> is every committed <c>.puck</c> world source; it is both
/// the corpus of gates that judge a source as committed text — reference linting, formatter idempotence, formatting
/// preserving what a source compiles to — and the corpus of the byte-identity regeneration gate, because every
/// source's document is the compiler's own output. <see cref="Files"/> is every other committed world document, gated
/// through its own decompilation: a generated document is the expansion of its source, so decompiling it tests an
/// import nobody performs and can exceed the source-length limit. Three private copies of the walk could disagree
/// about which documents each gate covers.</remarks>
internal static class ShippedWorlds {
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

    /// <summary>Returns the committed document a world source compiles to: the path <c>puck compile</c> writes
    /// when no output is named.</summary>
    /// <param name="sourcePath">A forward-slashed source path relative to the worlds directory.</param>
    /// <returns>The forward-slashed document path relative to the worlds directory.</returns>
    public static string DocumentOf(string sourcePath) =>
        Path.ChangeExtension(
            extension: ".world.json",
            path: sourcePath
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        );
    /// <summary>Returns every <c>*.world.json</c> under the worlds directory that is not a source's generated
    /// output, recursively, as forward-slashed paths relative to it, in ordinal order.</summary>
    /// <returns>The decompilation corpus as xUnit theory data.</returns>
    public static IEnumerable<string> FilePaths() {
        var generated = SourcePaths().Select(selector: DocumentOf).ToHashSet(comparer: StringComparer.Ordinal);

        return Enumerate(pattern: "*.world.json").Where(predicate: path => !generated.Contains(item: path));
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

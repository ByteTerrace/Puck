using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>The one enumeration of the shipped world corpus, and the one structural JSON comparison, that every
/// corpus-wide gate in this assembly runs over.</summary>
/// <remarks>Enumeration is recursive, and every committed world document belongs to exactly one of the two corpora: a
/// document with a <c>.puck</c> source beside it is gated through that source (<see cref="Sources"/>), every other
/// document through its own decompilation (<see cref="Files"/>). A source-authored document is the expansion of its
/// source, so decompiling it tests an import nobody performs and can exceed the source-length limit. Three private
/// copies of the walk could disagree about which documents each gate covers.</remarks>
internal static class ShippedWorlds {
    /// <summary>Returns the absolute path of <c>src/Puck.World/Assets/worlds</c>, walked up from the test
    /// runner's own directory.</summary>
    /// <returns>The worlds directory.</returns>
    public static string FindDirectory() {
        var dir = AppContext.BaseDirectory;

        while (dir is not null) {
            var candidate = Path.Combine(dir, "src", "Puck.World", "Assets", "worlds");

            if (Directory.Exists(candidate)) {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("Could not locate src/Puck.World/Assets/worlds directory from test runner.");
    }

    /// <summary>Returns every <c>*.world.json</c> under the worlds directory that has no <c>.puck</c> source,
    /// recursively, as forward-slashed paths relative to it, in ordinal order.</summary>
    /// <returns>The decompilation corpus as xUnit theory data.</returns>
    public static TheoryData<string> Files() {
        var sourceDocuments = Enumerate("*.puck").Select(DocumentOf).ToHashSet(StringComparer.Ordinal);

        return new TheoryData<string>(Enumerate("*.world.json").Where(path => !sourceDocuments.Contains(path)));
    }

    /// <summary>Returns every <c>*.puck</c> world source under the worlds directory, recursively, as forward-slashed
    /// paths relative to it, in ordinal order.</summary>
    /// <returns>The source corpus as xUnit theory data.</returns>
    public static TheoryData<string> Sources() => new(Enumerate("*.puck"));

    /// <summary>Returns the committed document a world source compiles to: the path <c>puck compile</c> writes
    /// when no output is named.</summary>
    /// <param name="sourcePath">A forward-slashed source path relative to the worlds directory.</param>
    /// <returns>The forward-slashed document path relative to the worlds directory.</returns>
    public static string DocumentOf(string sourcePath) =>
        Path.ChangeExtension(sourcePath, ".world.json").Replace('\\', '/');

    private static IEnumerable<string> Enumerate(string pattern) {
        var worldsDirectory = FindDirectory();

        return Directory.GetFiles(worldsDirectory, pattern, SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(worldsDirectory, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
    }
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
        if (expected is null && actual is null) {
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

        if (expected is JsonObject expectedObject && actual is JsonObject actualObject) {
            foreach (var (key, value) in expectedObject) {
                if (!actualObject.ContainsKey(key)) {
                    return $"{path}/{key}: missing property";
                }
                var diff = Find(value, actualObject[key], $"{path}/{key}");
                if (diff is not null) {
                    return diff;
                }
            }
            foreach (var (key, _) in actualObject) {
                if (!expectedObject.ContainsKey(key)) {
                    return $"{path}/{key}: unexpected extra property";
                }
            }
            return null;
        }

        if (expected is JsonArray expectedArray && actual is JsonArray actualArray) {
            if (expectedArray.Count != actualArray.Count) {
                return $"{path}: array length expected {expectedArray.Count}, actual {actualArray.Count}";
            }
            for (var i = 0; i < expectedArray.Count; i++) {
                var diff = Find(expectedArray[i], actualArray[i], $"{path}[{i}]");
                if (diff is not null) {
                    return diff;
                }
            }
            return null;
        }

        if (expected is JsonValue expectedValue && actual is JsonValue actualValue) {
            return JsonNode.DeepEquals(expectedValue, actualValue)
                ? null
                : $"{path}: value expected '{expectedValue.ToJsonString()}', actual '{actualValue.ToJsonString()}'";
        }

        return $"{path}: kinds differ";
    }
}

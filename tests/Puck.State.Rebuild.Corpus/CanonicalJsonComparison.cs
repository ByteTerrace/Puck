using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;

namespace Puck.State.Rebuild.Corpus;

/// <summary>Canonical equality of two world documents: both written through <see cref="CanonicalJsonDocument"/>,
/// then compared structurally so a missing property is distinguished from an extra one.</summary>
/// <remarks>Object member order is not part of the comparison; array order is. This is the comparison the transpiler
/// assembly's shipped-world parity gate uses, reached here through the same canonical writer rather than through a
/// second canonical form.</remarks>
public static class CanonicalJsonComparison {
    private static string? Find(JsonNode? expected, JsonNode? actual, string path) {
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

        return (JsonNode.DeepEquals(
            node1: expected,
            node2: actual
        )
            ? null
            : $"{path}: value expected '{expected.ToJsonString()}', actual '{actual.ToJsonString()}'"
        );
    }

    /// <summary>Returns the first canonical difference between two documents, or <see langword="null"/> when they
    /// are canonically equal.</summary>
    /// <param name="expected">The reference document.</param>
    /// <param name="actual">The document under test.</param>
    /// <param name="label">The prefix reported differences are rooted at.</param>
    /// <returns>A one-line description of the first difference, or <see langword="null"/>.</returns>
    public static string? FirstDifference(JsonNode expected, JsonNode actual, string label) =>
        Find(
            Reparse(node: expected),
            Reparse(node: actual),
            label
        );

    private static JsonNode? Reparse(JsonNode node) =>
        JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: node)));
}

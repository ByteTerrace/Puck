using System.Text.Json.Nodes;

namespace Puck.World.Server;

/// <summary>Names where two JSON documents first part, for a reader comparing two runs' exports or deliveries.</summary>
public static class WorldJsonDifference {
    /// <summary>Returns the first JSON path, in document order, where two documents differ, with both values.</summary>
    /// <param name="left">The first document.</param>
    /// <param name="right">The second document.</param>
    /// <param name="path">The path of <paramref name="left"/> and <paramref name="right"/> within their documents.</param>
    /// <returns>The path and both values; <c>member order</c> when the members agree and only their order differs.</returns>
    public static string First(JsonNode? left, JsonNode? right, string path = "$") {
        switch (left, right) {
            case (JsonObject leftObject, JsonObject rightObject):
                foreach (var (key, value) in leftObject) {
                    if (!rightObject.TryGetPropertyValue(propertyName: key, jsonNode: out var other)) { return $"{path}.{key}: present against absent"; }
                    if (!JsonNode.DeepEquals(node1: value, node2: other)) { return First(left: value, path: $"{path}.{key}", right: other); }
                }
                return ((rightObject.FirstOrDefault(predicate: pair => !leftObject.ContainsKey(propertyName: pair.Key)) is { Key: { } extra })
                    ? $"{path}.{extra}: absent against present"
                    : $"{path}: member order");
            case (JsonArray leftArray, JsonArray rightArray):
                for (var index = 0; (index < Math.Min(val1: leftArray.Count, val2: rightArray.Count)); index++) {
                    if (!JsonNode.DeepEquals(node1: leftArray[index], node2: rightArray[index])) { return First(left: leftArray[index], path: $"{path}[{index}]", right: rightArray[index]); }
                }
                return $"{path}: {leftArray.Count} elements against {rightArray.Count}";
            default:
                return $"{path}: {Show(node: left)} against {Show(node: right)}";
        }
    }
    private static string Show(JsonNode? node) {
        var text = (node?.ToJsonString() ?? "null");

        return ((text.Length > 120) ? $"{text[..117]}..." : text);
    }
}

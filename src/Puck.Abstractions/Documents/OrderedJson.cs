using System.Text.Json.Nodes;

namespace Puck.Abstractions.Documents;

/// <summary>JSON value equality that also preserves object member order, for byte-identified documents.</summary>
public static class OrderedJson {
    /// <summary>Compares values recursively, including the ordinal sequence of each object's member names.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether values and object member order agree; number equality follows <see cref="JsonNode.DeepEquals"/>.</returns>
    public static bool Equals(JsonNode? left, JsonNode? right) {
        if (ReferenceEquals(left, right)) { return true; }
        if (left is JsonObject leftObject && right is JsonObject rightObject) {
            if (leftObject.Count != rightObject.Count) { return false; }
            using var other = rightObject.GetEnumerator();
            foreach (var member in leftObject) {
                if (!other.MoveNext() || member.Key != other.Current.Key || !Equals(member.Value, other.Current.Value)) { return false; }
            }
            return true;
        }
        if (left is JsonArray leftArray && right is JsonArray rightArray) {
            if (leftArray.Count != rightArray.Count) { return false; }
            for (var index = 0; index < leftArray.Count; index++) {
                if (!Equals(leftArray[index], rightArray[index])) { return false; }
            }
            return true;
        }
        return JsonNode.DeepEquals(left, right);
    }
}

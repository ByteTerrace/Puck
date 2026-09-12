using System.Text.Json.Nodes;

namespace Puck.Transpiler.Lowering;

/// <summary>Structural value equality and matching hashes for compile-time collection operations.</summary>
public sealed class DocumentValueEqualityComparer : IEqualityComparer<JsonNode?> {
    /// <summary>Gets the shared comparer.</summary>
    public static DocumentValueEqualityComparer Instance { get; } = new();

    /// <inheritdoc />
    public bool Equals(JsonNode? x, JsonNode? y) {
        if (ReferenceEquals(x, y)) {
            return true;
        }
        if (x is JsonArray first && y is JsonArray second) {
            return first.Count == second.Count && first.Zip(second).All(pair => Equals(pair.First, pair.Second));
        }
        if (x is JsonObject left && y is JsonObject right) {
            return left.Count == right.Count && left.All(pair => right.TryGetPropertyValue(pair.Key, out var value) && Equals(pair.Value, value));
        }
        if (DocumentLowering.TryReadNumber(x, out _) && DocumentLowering.TryReadNumber(y, out _)) {
            return DocumentNumbers.Compare(x, y) == 0;
        }
        return JsonNode.DeepEquals(x, y);
    }

    /// <inheritdoc />
    public int GetHashCode(JsonNode? obj) {
        if (obj is null) {
            return 0;
        }
        if (obj is JsonObject map) {
            var hash = 0;
            foreach (var pair in map) {
                hash ^= HashCode.Combine(StringComparer.Ordinal.GetHashCode(pair.Key), GetHashCode(pair.Value));
            }
            return hash;
        }
        if (obj is JsonArray array) {
            var hash = new HashCode();
            foreach (var item in array) {
                hash.Add(GetHashCode(item));
            }
            return hash.ToHashCode();
        }
        if (DocumentNumbers.TryInteger(obj, out var integer)) {
            return integer.GetHashCode();
        }
        if (DocumentLowering.TryReadNumber(obj, out var number)) {
            return number.GetHashCode();
        }
        return StringComparer.Ordinal.GetHashCode(obj.ToJsonString());
    }
}

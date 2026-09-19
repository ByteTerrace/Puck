using System.Text.Json.Nodes;

namespace Puck.Transpiler.Lowering;

/// <summary>Structural value equality and matching hashes for compile-time collection operations.</summary>
public sealed class DocumentValueEqualityComparer : IEqualityComparer<JsonNode?> {
    /// <summary>Gets the shared comparer.</summary>
    public static DocumentValueEqualityComparer Instance { get; } = new();

    /// <inheritdoc />
    public bool Equals(JsonNode? x, JsonNode? y) {
        if (ReferenceEquals(
            objA: x,
            objB: y
        )) {
            return true;
        }
        if (
            (x is JsonArray first) &&
            (y is JsonArray second)
        ) {
            return (
                (first.Count == second.Count) &&
                first.Zip(second: second).All(predicate: pair => Equals(
                x: pair.First,
                y: pair.Second
            ))
            );
        }
        if (
            (x is JsonObject left) &&
            (y is JsonObject right)
        ) {
            return (
                (left.Count == right.Count) &&
                left.All(predicate: pair => (right.TryGetPropertyValue(
                pair.Key,
                out var value
            ) && Equals(
                x: pair.Value,
                y: value
            )))
            );
        }
        if (
            DocumentLowering.TryReadNumber(
            node: x,
            number: out _
        ) &&
            DocumentLowering.TryReadNumber(
            node: y,
            number: out _
        )
        ) {
            return (DocumentNumbers.Compare(
                left: x,
                right: y
            ) == 0);
        }
        return JsonNode.DeepEquals(
            node1: x,
            node2: y
        );
    }
    /// <inheritdoc />
    public int GetHashCode(JsonNode? obj) {
        if (obj is null) {
            return 0;
        }
        if (obj is JsonObject map) {
            var hash = 0;

            foreach (var pair in map) {
                hash ^= HashCode.Combine(
                    value1: StringComparer.Ordinal.GetHashCode(obj: pair.Key),
                    value2: GetHashCode(obj: pair.Value)
                );
            }
            return hash;
        }
        if (obj is JsonArray array) {
            var hash = new HashCode();

            foreach (var item in array) {
                hash.Add(value: GetHashCode(obj: item));
            }
            return hash.ToHashCode();
        }
        if (DocumentNumbers.TryHash(
            hash: out var number,
            node: obj
        )) {
            return number;
        }
        return StringComparer.Ordinal.GetHashCode(obj: obj.ToJsonString());
    }
}

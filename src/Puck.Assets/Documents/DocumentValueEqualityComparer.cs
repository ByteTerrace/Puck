using System.Text.Json.Nodes;

namespace Puck.Assets.Documents;

/// <summary>Structural value equality and matching hashes for document nodes: the compile-time collection
/// operations' equality and the identity of a keyed row during composition. Objects compare member by member whatever
/// their order, arrays element by element in order, and numbers by the exact rational each holds
/// (<see cref="DocumentNumbers"/>), so <c>1</c>, <c>1.0</c> and a computed <c>1.0</c> are one value however each is
/// backed. Every other node compares by <see cref="JsonNode.DeepEquals"/>.</summary>
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
            if (first.Count != second.Count) {
                return false;
            }
            for (var index = 0; (index < first.Count); index++) {
                if (!Equals(
                    x: first[index],
                    y: second[index]
                )) {
                    return false;
                }
            }

            return true;
        }
        if (
            (x is JsonObject left) &&
            (y is JsonObject right)
        ) {
            if (left.Count != right.Count) {
                return false;
            }
            foreach (var pair in left) {
                if (
                    !right.TryGetPropertyValue(
                        jsonNode: out var value,
                        propertyName: pair.Key
                    ) ||
                    !Equals(
                        x: pair.Value,
                        y: value
                    )
                ) {
                    return false;
                }
            }

            return true;
        }
        if (
            DocumentNumbers.TryRational(
                node: x,
                number: out _
            ) &&
            DocumentNumbers.TryRational(
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
        if (
            (obj is JsonValue scalar) &&
            scalar.TryGetValue<string>(value: out var text)
        ) {
            return StringComparer.Ordinal.GetHashCode(obj: text);
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

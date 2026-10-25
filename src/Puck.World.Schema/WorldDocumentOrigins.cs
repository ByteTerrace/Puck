using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>The authored origin of one effective scalar in a composed document.</summary>
/// <param name="DocumentPath">The resolved document that authored the value.</param>
/// <param name="JsonPointer">The scalar's JSON pointer in that document.</param>
/// <param name="Json">The original scalar serialized as JSON, before relocation or rewriting.</param>
public readonly record struct WorldDocumentValueOrigin(string DocumentPath, string JsonPointer, string Json);
/// <summary>Opt-in scalar provenance for one composition. The existing merge carries origins alongside the values
/// it retains, so removed or overridden values cannot become effective origins. Nothing is serialized into the world.
/// Composed images retain effective scalar origins in memory and attach them to each reader's fresh tree.</summary>
public sealed class WorldDocumentOrigins {
    private readonly Dictionary<JsonNode, WorldDocumentValueOrigin> m_values = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Finds the original authored value of an effective scalar.</summary>
    /// <param name="value">The composed scalar node.</param>
    /// <param name="origin">The authored origin on success; default otherwise.</param>
    /// <returns>Whether this composition tracked the scalar.</returns>
    public bool TryGetOrigin(JsonNode? value, out WorldDocumentValueOrigin origin) {
        origin = default;
        return ((value is not null) && m_values.TryGetValue(key: value, value: out origin));
    }

    internal IReadOnlyDictionary<string, WorldDocumentValueOrigin> Snapshot(JsonNode node) {
        var snapshot = new Dictionary<string, WorldDocumentValueOrigin>(comparer: StringComparer.Ordinal);

        Walk(node: node, pointer: "", visit: (value, pointer) => {
            if (m_values.TryGetValue(key: value, value: out var origin)) {
                snapshot.Add(key: pointer, value: origin);
            }
        });
        return snapshot.ToFrozenDictionary(comparer: StringComparer.Ordinal);
    }
    internal void Restore(JsonNode node, IReadOnlyDictionary<string, WorldDocumentValueOrigin> snapshot) =>
        Walk(node: node, pointer: "", visit: (value, pointer) => {
            if (snapshot.TryGetValue(key: pointer, value: out var origin)) {
                m_values[value] = origin;
            }
        });

    private static void Walk(JsonNode? node, string pointer, Action<JsonValue, string> visit) {
        if (node is JsonValue value) {
            visit(value, pointer);
        } else if (node is JsonObject obj) {
            foreach (var (name, child) in obj) {
                Walk(node: child, pointer: $"{pointer}/{name.Replace(comparisonType: StringComparison.Ordinal, newValue: "~0", oldValue: "~").Replace(comparisonType: StringComparison.Ordinal, newValue: "~1", oldValue: "/")}", visit: visit);
            }
        } else if (node is JsonArray array) {
            for (var index = 0; (index < array.Count); index++) {
                Walk(node: array[index], pointer: $"{pointer}/{index}", visit: visit);
            }
        }
    }

    internal void Capture(JsonNode? node, string documentPath, string pointer = "") =>
        Walk(node: node, pointer: pointer, visit: (value, path) =>
            m_values[value] = new WorldDocumentValueOrigin(DocumentPath: documentPath, JsonPointer: path, Json: value.ToJsonString()));
    internal static JsonNode? Clone(JsonNode? node, WorldDocumentOrigins? origins) {
        var clone = node?.DeepClone();

        origins?.Copy(source: node, target: clone);
        return clone;
    }
    internal void Copy(JsonNode? source, JsonNode? target) {
        if ((source is null) || (target is null)) {
            return;
        }
        if (m_values.TryGetValue(key: source, value: out var origin)) {
            m_values[target] = origin;
        } else if ((source is JsonObject sourceObject) && (target is JsonObject targetObject)) {
            foreach (var (name, child) in sourceObject) {
                Copy(source: child, target: targetObject[name]);
            }
        } else if ((source is JsonArray sourceArray) && (target is JsonArray targetArray)) {
            for (var index = 0; (index < sourceArray.Count); index++) {
                Copy(source: sourceArray[index], target: targetArray[index]);
            }
        }
    }
}

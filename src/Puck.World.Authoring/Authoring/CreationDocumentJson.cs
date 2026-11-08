using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World.Authoring;

/// <summary>The one door a composed creation document is read through: it completes what a shape row omits, so a
/// layer that refines a row by naming only the members it changes composes first and is completed after.</summary>
public static class CreationDocumentJson {
    /// <summary>Reads a creation document from <paramref name="reader"/>, completing each shape row that omits
    /// <c>id</c> (its position in the list) or a member of <see cref="ShapeDocument.OmittedMembers"/>.</summary>
    /// <param name="reader">The reader positioned at the document's first token; it is left after the document.</param>
    /// <returns>The document, or <see langword="null"/> when the token is JSON <c>null</c>.</returns>
    /// <exception cref="JsonException">The document is not a valid creation document.</exception>
    public static CreationDocument? Read(ref Utf8JsonReader reader) {
        using var parsed = JsonDocument.ParseValue(reader: ref reader);
        var root = parsed.RootElement;

        if (!Omits(root: root)) {
            return root.Deserialize(jsonTypeInfo: CreationJsonContext.Document.CreationDocument);
        }

        var tree = JsonNode.Parse(json: root.GetRawText())!.AsObject();

        if (tree["shapes"] is JsonArray shapes) {
            for (var index = 0; (index < shapes.Count); index++) {
                if (shapes[index] is not JsonObject shape) {
                    continue;
                }
                if (!shape.ContainsKey(propertyName: "id")) {
                    shape["id"] = index;
                }
                foreach (var (key, json) in ShapeDocument.OmittedMembers) {
                    if (!shape.ContainsKey(propertyName: key)) {
                        shape[key] = JsonNode.Parse(json: json);
                    }
                }
            }
        }

        return tree.Deserialize(jsonTypeInfo: CreationJsonContext.Document.CreationDocument);
    }

    private static bool Omits(JsonElement root) {
        if ((root.ValueKind != JsonValueKind.Object) || !root.TryGetProperty(propertyName: "shapes", value: out var shapes) || (shapes.ValueKind != JsonValueKind.Array)) {
            return false;
        }

        foreach (var shape in shapes.EnumerateArray()) {
            if (shape.ValueKind != JsonValueKind.Object) {
                continue;
            }
            if (!shape.TryGetProperty(propertyName: "id", value: out _)) {
                return true;
            }
            foreach (var (key, _) in ShapeDocument.OmittedMembers) {
                if (!shape.TryGetProperty(propertyName: key, value: out _)) {
                    return true;
                }
            }
        }

        return false;
    }
}

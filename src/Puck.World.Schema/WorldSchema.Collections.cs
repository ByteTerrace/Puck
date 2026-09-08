using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Xml.Linq;

namespace Puck.World;

// The exporter's own item schema for an Enumerable/Dictionary node is sometimes dropped entirely — no
// "items"/"additionalProperties" key at all, never even the fully permissive `true` a converter-hidden shape is
// otherwise promoted to (see ApplyConverterVocabulary) — whenever the element/value type's JsonTypeInfo sits
// outside the exporter's own closed metadata walk (a JsonConverter<T> declared directly on a type WorldJsonContext
// never lists as [JsonSerializable], e.g. DocumentIdentifier/WorldPrincipal). Fixed the same way
// ApplyConverterVocabulary fixes a hidden scalar: ask the RESOLVED converter for the element/value type directly
// rather than trusting the exporter to have walked there on its own.
public static partial class WorldSchema {
    private static void ApplyCollectionVocabulary(JsonObject obj, JsonTypeInfo typeInfo, IReadOnlyDictionary<string, XElement>? index, Dictionary<JsonNode, Type> typesByNode, NestedExports? nested) {
        if (
            (typeInfo.Kind == JsonTypeInfoKind.Enumerable) &&
            !obj.ContainsKey(propertyName: "items") &&
            (typeInfo.ElementType is { } elementType)
        ) {
            obj["items"] = BuildCollectionMemberSchema(
                elementType: elementType,
                index: index,
                nested: nested,
                typesByNode: typesByNode
            );

            return;
        }

        if (
            (typeInfo.Kind == JsonTypeInfoKind.Dictionary) &&
            !obj.ContainsKey(propertyName: "additionalProperties") &&
            (typeInfo.ElementType is { } valueType)
        ) {
            obj["additionalProperties"] = BuildCollectionMemberSchema(
                elementType: valueType,
                index: index,
                nested: nested,
                typesByNode: typesByNode
            );
        }
    }
    // RestoreSkippedPropertyAnnotations has a reflected PropertyType but no JsonSchemaExporterContext (the exporter
    // never called back for the node it is fixing up), so it resolves the JsonTypeInfo itself; a type the context
    // carries no metadata for (never reached here in practice — the property already parsed through this same
    // context) leaves the node exactly as the exporter produced it rather than throwing.
    private static void ApplyCollectionVocabulary(JsonObject obj, Type propertyType, IReadOnlyDictionary<string, XElement>? index, Dictionary<JsonNode, Type> typesByNode, NestedExports? nested) {
        JsonTypeInfo typeInfo;

        try {
            typeInfo = WorldJsonContext.Default.Options.GetTypeInfo(type: propertyType);
        } catch (NotSupportedException) {
            return;
        }

        ApplyCollectionVocabulary(
            index: index,
            nested: nested,
            obj: obj,
            typeInfo: typeInfo,
            typesByNode: typesByNode
        );
    }
    // A freshly promoted `{}` node for a collection's element/value type, annotated exactly as
    // ApplyConverterVocabulary annotates any other converter-hidden member.
    private static JsonObject BuildCollectionMemberSchema(Type elementType, IReadOnlyDictionary<string, XElement>? index, Dictionary<JsonNode, Type> typesByNode, NestedExports? nested) {
        var obj = new JsonObject();

        typesByNode[obj] = elementType;
        StampTitle(
            obj: obj,
            type: elementType
        );

        ApplyConverterVocabulary(
            index: index,
            nested: nested,
            obj: obj,
            propertyType: elementType,
            typesByNode: typesByNode
        );

        return obj;
    }
}

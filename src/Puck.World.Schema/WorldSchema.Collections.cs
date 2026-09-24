using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Puck.World;

// The exporter's own item schema for an Enumerable/Dictionary node is sometimes dropped entirely — no
// "items"/"additionalProperties" key at all, never even the fully permissive `true` a converter-hidden shape is
// otherwise promoted to (see ApplyConverterVocabulary) — whenever the element/value type's JsonTypeInfo sits
// outside the exporter's own closed metadata walk (a JsonConverter<T> declared directly on a type WorldJsonContext
// never lists as [JsonSerializable], e.g. DocumentIdentifier/Principal). Fixed the same way
// ApplyConverterVocabulary fixes a hidden scalar: ask the RESOLVED converter for the element/value type directly
// rather than trusting the exporter to have walked there on its own.
public static partial class WorldSchema {
    private static void ApplyCollectionVocabulary(JsonObject obj, JsonTypeInfo typeInfo, ExportRun run) {
        if (
            (typeInfo.Kind == JsonTypeInfoKind.Enumerable) &&
            !obj.ContainsKey(propertyName: "items") &&
            (typeInfo.ElementType is { } elementType)
        ) {
            obj["items"] = BuildCollectionMemberSchema(
                elementType: elementType,
                run: run
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
                run: run
            );
        }
    }
    // RestoreSkippedPropertyAnnotations has a reflected PropertyType but no JsonSchemaExporterContext (the exporter
    // never called back for the node it is fixing up), so it resolves the JsonTypeInfo itself; a type the context
    // carries no metadata for (never reached here in practice — the property already parsed through this same
    // context) leaves the node exactly as the exporter produced it rather than throwing.
    private static void ApplyCollectionVocabulary(JsonObject obj, Type propertyType, ExportRun run) {
        if (ResolveTypeInfo(type: propertyType) is not { } typeInfo) {
            return;
        }

        ApplyCollectionVocabulary(
            obj: obj,
            run: run,
            typeInfo: typeInfo
        );
    }
    // A freshly promoted `{}` node for a collection's element/value type, annotated exactly as
    // ApplyConverterVocabulary annotates any other converter-hidden member.
    private static JsonObject BuildCollectionMemberSchema(Type elementType, ExportRun run) {
        var obj = new JsonObject();

        run.TypesByNode[obj] = elementType;
        StampTitle(
            obj: obj,
            type: elementType
        );

        ApplyConverterVocabulary(
            obj: obj,
            propertyType: elementType,
            run: run
        );

        return obj;
    }
}

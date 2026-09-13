using System.Text.Json.Nodes;
using System.Text.Json.Schema;

namespace Puck.World;

public static partial class WorldSchema {
    // Converter-owned schema exports run outside the main transform, but use the same documentation index.
    internal static JsonNode AnnotateCreationDocumentation(JsonSchemaExporterContext context, JsonNode node) {
        var index = XmlDocIndex.Value;
        var unconstrained = ((node is JsonValue value) && value.TryGetValue<bool>(value: out var permissive) && permissive);
        var obj = AsObjectNode(node: ref node);

        if (unconstrained && (obj is not null)) {
            obj["$comment"] = "Converter-defined value. This schema supplies documentation but leaves value validation to the creation document loader.";
        }

        if ((obj is null) || (index is null)) {
            return node;
        }
        if (ResolveDescription(context: context, index: index) is { } description) {
            obj["description"] = description;
        }
        if (obj["properties"] is JsonObject properties) {
            foreach (var property in context.TypeInfo.Properties) {
                if ((properties[property.Name] is not { } child) || (property.AttributeProvider is not System.Reflection.MemberInfo member)) {
                    continue;
                }
                var childObject = AsObjectNode(node: ref child);

                if ((childObject is not null) && (ResolveDescriptionForMember(index: index, member: member) is { } propertyDescription)) {
                    childObject["description"] = propertyDescription;
                    if (childObject.Parent is null) {
                        properties[property.Name] = childObject;
                    }
                }
            }
        }
        var enumType = (Nullable.GetUnderlyingType(nullableType: context.TypeInfo.Type) ?? context.TypeInfo.Type);

        if (enumType.IsEnum) {
            var descriptions = new JsonObject();

            foreach (var name in Enum.GetNames(enumType: enumType)) {
                if (TryGetSummary(index: index, memberDocId: $"F:{FormatDeclaringType(type: enumType)}.{name}", text: out var summary)) {
                    descriptions[name] = summary;
                }
            }
            if (descriptions.Count > 0) {
                obj["x-enumDescriptions"] = descriptions;
            }
        }
        return node;
    }
}

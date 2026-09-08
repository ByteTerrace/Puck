using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World.Protocol;

/// <summary>Reads and writes a list of <see cref="WorldMutation"/> members nested inside another mutation — each as
/// <c>{"kind": &lt;ordinal&gt;, "value": {...}}</c>, the same ordinal the wire codec prefixes a top-level mutation
/// with, so a member's concrete record is recovered without the base type carrying a discriminator of its own.</summary>
public sealed class WorldMutationListJsonConverter : JsonConverter<IReadOnlyList<WorldMutation>> {
    /// <inheritdoc/>
    public override IReadOnlyList<WorldMutation> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException(message: "a mutation list is a JSON array");
        }
        var members = new List<WorldMutation>();
        while (reader.Read() && (reader.TokenType != JsonTokenType.EndArray)) {
            if (reader.TokenType != JsonTokenType.StartObject) {
                throw new JsonException(message: "a mutation list member is an object");
            }
            var ordinal = -1;
            WorldMutation? member = null;
            while (reader.Read() && (reader.TokenType != JsonTokenType.EndObject)) {
                var property = reader.GetString();
                _ = reader.Read();
                switch (property) {
                    case "kind":
                        ordinal = reader.GetInt32();
                        break;
                    case "value": {
                        var type = TypeOf(ordinal: ordinal) ?? throw new JsonException(message: $"mutation kind ordinal {ordinal} is not cataloged (kind must precede value)");
                        member = (JsonSerializer.Deserialize(ref reader, returnType: type, options: options) as WorldMutation) ?? throw new JsonException(message: $"{type.Name} decoded as null");
                        break;
                    }
                    default:
                        throw new JsonException(message: $"unexpected mutation list member property '{property}'");
                }
            }
            members.Add(item: member ?? throw new JsonException(message: "a mutation list member carries no value"));
        }
        return members;
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<WorldMutation> value, JsonSerializerOptions options) {
        writer.WriteStartArray();
        foreach (var member in value) {
            writer.WriteStartObject();
            writer.WriteNumber(propertyName: "kind", value: WorldMutationKindCatalog.OrdinalOf(mutation: member));
            writer.WritePropertyName(propertyName: "value");
            JsonSerializer.Serialize(writer, value: member, inputType: member.GetType(), options: options);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
    private static Type? TypeOf(int ordinal) {
        foreach (var entry in WorldMutationKindCatalog.All()) {
            if (entry.Ordinal == ordinal) {
                return entry.Type;
            }
        }
        return null;
    }
}

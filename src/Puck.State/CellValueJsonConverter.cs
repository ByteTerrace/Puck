using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>Reads and writes a cell value as a tagged object, or JSON null for the valueless carrier.</summary>
public sealed class CellValueJsonConverter : JsonConverter<CellValue>, IJsonSchemaNodeConverter {
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) {
        ArgumentNullException.ThrowIfNull(argument: exportType);
        return new JsonObject {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "null" },
                exportType(typeof(IntShape)),
                exportType(typeof(FixedShape)),
                exportType(typeof(BoolShape)),
                exportType(typeof(TextShape)),
                exportType(typeof(VectorShape))),
        };
    }
    /// <inheritdoc/>
    public override CellValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType == JsonTokenType.Null) {
            return default;
        }
        using var document = JsonDocument.ParseValue(reader: ref reader);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object) {
            throw new JsonException(message: "A cell value is null or an object carrying exactly 'kind' and 'value'.");
        }
        JsonElement kind = default;
        JsonElement value = default;
        var count = 0;

        foreach (var property in root.EnumerateObject()) {
            count++;
            if (property.NameEquals(utf8Text: "kind"u8)) {
                kind = property.Value;
            } else if (property.NameEquals(utf8Text: "value"u8)) {
                value = property.Value;
            } else {
                throw new JsonException(message: $"A cell value carries no member '{property.Name}'.");
            }
        }
        if ((count != 2) || (kind.ValueKind != JsonValueKind.String) || (value.ValueKind == JsonValueKind.Undefined)) {
            throw new JsonException(message: "A cell value requires exactly string 'kind' and member 'value'.");
        }
        return kind.GetString() switch {
            "Int" when ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt64(value: out var number)) => CellValue.Int(value: number),
            "Fixed" when ((value.ValueKind == JsonValueKind.Number) && value.TryGetInt64(value: out var fixedBits)) => CellValue.Fixed(rawBits: fixedBits),
            "Bool" when (value.ValueKind is JsonValueKind.True or JsonValueKind.False) => CellValue.Bool(value: value.GetBoolean()),
            "Text" when ((value.ValueKind == JsonValueKind.String) && (value.GetString() is { } text) && (text.Length <= StateCapacity.MaxTextValueLength)) => CellValue.Text(value: text),
            "Vector" when (value.ValueKind == JsonValueKind.Array) => CellValue.Vector(components: ReadVector(element: value)),
            var spelling => throw new JsonException(message: $"Cell value kind '{spelling}' does not agree with its value."),
        };
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, CellValue value, JsonSerializerOptions options) {
        if (!value.HasValue) {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString(propertyName: "kind", value: value.Kind.ToString());
        writer.WritePropertyName(propertyName: "value");
        switch (value.Kind) {
            case CellKind.Bool: writer.WriteBooleanValue(value: value.AsBool); break;
            case CellKind.Fixed: writer.WriteNumberValue(value: value.AsFixed); break;
            case CellKind.Int: writer.WriteNumberValue(value: value.AsInt); break;
            case CellKind.Text: writer.WriteStringValue(value: value.AsText); break;
            case CellKind.Vector:
                writer.WriteStartArray();
                foreach (var component in value.AsVector.Span) {
                    writer.WriteNumberValue(value: component);
                }
                writer.WriteEndArray();
                break;
            default: throw new JsonException(message: $"Unknown cell value kind '{value.Kind}'.");
        }
        writer.WriteEndObject();
    }

    private static sbyte[] ReadVector(JsonElement element) {
        if (element.GetArrayLength() > StateCapacity.MaxVectorDimensions) {
            throw new JsonException(message: $"A cell vector exceeds {StateCapacity.MaxVectorDimensions} components.");
        }
        var result = new sbyte[element.GetArrayLength()];
        var index = 0;

        foreach (var component in element.EnumerateArray()) {
            if ((component.ValueKind != JsonValueKind.Number) || !component.TryGetSByte(value: out result[index++])) {
                throw new JsonException(message: "Every cell vector component must fit signed 8-bit storage.");
            }
        }
        return result;
    }

    /// <summary>The schema-only Int wire arm.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record IntShape(IntKind Kind, long Value);
    /// <summary>The schema-only Fixed wire arm.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record FixedShape(FixedKind Kind, long Value);
    /// <summary>The schema-only Bool wire arm.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record BoolShape(BoolKind Kind, bool Value);
    /// <summary>The schema-only Text wire arm.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record TextShape(TextKind Kind, [property: StringLength(StateCapacity.MaxTextValueLength)] string Value);
    /// <summary>The schema-only Vector wire arm.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record VectorShape(VectorKind Kind, IReadOnlyList<sbyte> Value);
    /// <summary>The Int arm's sole kind token.</summary>
    [JsonConverter(typeof(StrictEnumConverter<IntKind>))]
    public enum IntKind { Int }
    /// <summary>The Fixed arm's sole kind token.</summary>
    [JsonConverter(typeof(StrictEnumConverter<FixedKind>))]
    public enum FixedKind { Fixed }
    /// <summary>The Bool arm's sole kind token.</summary>
    [JsonConverter(typeof(StrictEnumConverter<BoolKind>))]
    public enum BoolKind { Bool }
    /// <summary>The Text arm's sole kind token.</summary>
    [JsonConverter(typeof(StrictEnumConverter<TextKind>))]
    public enum TextKind { Text }
    /// <summary>The Vector arm's sole kind token.</summary>
    [JsonConverter(typeof(StrictEnumConverter<VectorKind>))]
    public enum VectorKind { Vector }
}

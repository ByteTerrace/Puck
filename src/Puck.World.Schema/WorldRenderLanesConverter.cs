using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>Reads the four render expressions and omits trailing empty entries when writing.</summary>
public sealed class WorldRenderLanesConverter : JsonConverter<IReadOnlyList<ValueExpression?>> {
    /// <inheritdoc/>
    public override IReadOnlyList<ValueExpression?>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.StartArray) {
            throw new JsonException("Render lanes must be an array.");
        }
        var lanes = new List<ValueExpression?>(4);
        while (reader.Read()) {
            if (reader.TokenType == JsonTokenType.EndArray) { return lanes; }
            if (lanes.Count == 4) { throw new JsonException("Render lanes may contain at most four expressions."); }
            lanes.Add(reader.TokenType == JsonTokenType.Null ? null : JsonSerializer.Deserialize<ValueExpression>(ref reader, options));
        }
        throw new JsonException("Render lanes array is incomplete.");
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ValueExpression?> value, JsonSerializerOptions options) {
        if (value.Count > 4) {
            throw new JsonException("Render lanes may contain at most four expressions.");
        }
        var count = value.Count;
        while ((count > 0) && (value[count - 1] is null)) {
            count--;
        }
        writer.WriteStartArray();
        for (var index = 0; index < count; index++) {
            JsonSerializer.Serialize(writer, value[index], options);
        }
        writer.WriteEndArray();
    }
}

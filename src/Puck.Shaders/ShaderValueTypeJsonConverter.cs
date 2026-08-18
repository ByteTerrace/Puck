using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Shaders;

/// <summary>Converts <see cref="ShaderValueType"/> to/from its HLSL spelling. Hand-written (not a reflection-based
/// <c>JsonStringEnumConverter</c> naming policy) to stay AOT/trim-safe.</summary>
public sealed class ShaderValueTypeJsonConverter : JsonConverter<ShaderValueType> {
    /// <inheritdoc/>
    public override ShaderValueType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var text = reader.GetString();

        return (ShaderValueTypes.TryParse(spelling: text, type: out var type)
            ? type
            : throw new JsonException(message: $"Unrecognized shader value type '{text}'; expected float|float2|float3|float4|uint|uint2|uint3|uint4|int|int2|int3|int4.")
        );
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ShaderValueType value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: value.Spelling());
    }
}

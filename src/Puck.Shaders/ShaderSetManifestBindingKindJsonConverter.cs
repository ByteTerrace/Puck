using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Shaders;

/// <summary>Converts <see cref="ShaderSetManifestBindingKind"/> to/from its lower camelCase JSON string. Hand-written
/// (not a reflection-based <c>JsonStringEnumConverter</c> naming policy) to stay AOT/trim-safe.</summary>
public sealed class ShaderSetManifestBindingKindJsonConverter : JsonConverter<ShaderSetManifestBindingKind> {
    /// <inheritdoc/>
    public override ShaderSetManifestBindingKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var text = reader.GetString();

        return text switch {
            "storageBuffer" => ShaderSetManifestBindingKind.StorageBuffer,
            "sampledImage" => ShaderSetManifestBindingKind.SampledImage,
            "storageImage" => ShaderSetManifestBindingKind.StorageImage,
            "accelerationStructure" => ShaderSetManifestBindingKind.AccelerationStructure,
            _ => throw new JsonException(message: $"Unrecognized shader-set manifest binding kind '{text}'."),
        };
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ShaderSetManifestBindingKind value, JsonSerializerOptions options) {
        writer.WriteStringValue(value: value switch {
            ShaderSetManifestBindingKind.StorageBuffer => "storageBuffer",
            ShaderSetManifestBindingKind.SampledImage => "sampledImage",
            ShaderSetManifestBindingKind.StorageImage => "storageImage",
            ShaderSetManifestBindingKind.AccelerationStructure => "accelerationStructure",
            _ => throw new ArgumentOutOfRangeException(nameof(value), value, "The binding kind is not defined."),
        });
    }
}

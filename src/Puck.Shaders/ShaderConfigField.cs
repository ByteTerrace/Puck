using System.Text.Json;

namespace Puck.Shaders;

/// <summary>One field of a <see cref="ShaderSetManifest"/>'s <c>config</c> schema — what a document may author for
/// this shader set, validated by <see cref="ShaderSetManifest.TryBindConfig"/> and emitted as JSON Schema by
/// <see cref="ShaderSetManifest.ConfigJsonSchema"/>.</summary>
/// <param name="Type">The value type; a document value is a number for a scalar type and an array of
/// <see cref="ShaderValueTypes.ComponentCount"/> numbers for a vector type.</param>
/// <param name="Default">The value an absent document field resolves to, or <see langword="null"/> when the field is
/// required.</param>
/// <param name="Min">The inclusive per-component minimum, or <see langword="null"/> for none.</param>
/// <param name="Max">The inclusive per-component maximum, or <see langword="null"/> for none.</param>
/// <param name="Description">The field's description, carried into the emitted JSON Schema.</param>
public sealed record ShaderConfigField(
    ShaderValueType Type,
    JsonElement? Default = null,
    double? Min = null,
    double? Max = null,
    string? Description = null
);

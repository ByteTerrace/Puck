using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// The System.Text.Json SOURCE-GENERATION context for the run document — the only sanctioned entry point for
/// (de)serializing a <see cref="PuckRunDocument"/>. Source-gen (not runtime reflection) keeps deserialization fast and
/// trimming/AOT-clean, honoring the toolbox's reflection-JSON canary. Enums serialize BY NAME (the SDF opcodes are
/// non-sequential and must match the shader), and the camelCase policy matches the authored JSON.
/// </summary>
[JsonSerializable(typeof(PuckRunDocument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true
)]
internal sealed partial class PuckSceneJsonContext : JsonSerializerContext {
}

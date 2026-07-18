using System.Text.Json.Serialization;

namespace Puck.Recording.Document;

/// <summary>
/// The System.Text.Json source-generation context for the recording document (<c>puck.recording.v1</c>) — the
/// only sanctioned entry point for (de)serializing a <see cref="RecordingDocument"/>. Source-gen (not runtime
/// reflection) keeps the boundary trimming/AOT-clean; enums serialize by name and the camelCase policy matches the
/// authored JSON. The individual row types are registered so an editor or verb can parse one inline-JSON row with
/// the same grammar as the document section.
/// </summary>
[JsonSerializable(typeof(RecordingDocument))]
[JsonSerializable(typeof(RecordingVideo))]
[JsonSerializable(typeof(RecordingAudioRow))]
[JsonSerializable(typeof(OverlayRow))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true
)]
internal sealed partial class RecordingJsonContext : JsonSerializerContext {
}

using System.Text.Json.Serialization;

namespace Puck.Recording.Document;

/// <summary>
/// The System.Text.Json source-generation context for the recording document (<c>puck.recording.v1</c>) — the
/// only sanctioned entry point for (de)serializing a <see cref="RecordingDocument"/>. Source-gen (not runtime
/// reflection) keeps the boundary trimming/AOT-clean; every enum the document carries declares its own strict
/// by-name conversion at the enum declaration (<c>[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter{TEnum}))]</c>
/// on <see cref="RecordingClock"/>, <see cref="RecordingAudioKind"/>, <see cref="RecordingAudioTrackMode"/>,
/// <see cref="OverlayKind"/>, <see cref="OverlayAnchor"/>, and <see cref="OverlayClock"/> — writes by name and
/// REFUSES a numeric token on read; <c>UseStringEnumConverter</c> writes by name too but has no
/// <c>allowIntegerValues</c> knob, so it still accepts a numeric wire value on read), and the camelCase policy
/// matches the authored JSON. The individual row types are registered so an editor or verb can parse one inline-JSON
/// row with the same grammar as the document section.
/// </summary>
[JsonSerializable(typeof(RecordingDocument))]
[JsonSerializable(typeof(RecordingVideo))]
[JsonSerializable(typeof(RecordingAudioRow))]
[JsonSerializable(typeof(OverlayRow))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true
)]
internal sealed partial class RecordingJsonContext : JsonSerializerContext {
}

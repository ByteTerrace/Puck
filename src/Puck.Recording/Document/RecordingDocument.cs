using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Recording.Document;

/// <summary>
/// The video lane configuration: the ordered codec preference ladder, the output extent (null components take the
/// source size), the nominal frame rate, and the target bitrate.
/// </summary>
/// <param name="CodecLadder">Codec preferences in order (tokens <c>av1</c>, <c>h264</c>); the first encodable one lands.</param>
/// <param name="Width">The output width, or <see langword="null"/> to match the source.</param>
/// <param name="Height">The output height, or <see langword="null"/> to match the source.</param>
/// <param name="FrameRate">The nominal frames per second.</param>
/// <param name="BitrateKbps">The target bitrate in kilobits per second.</param>
public sealed record RecordingVideo(
    IReadOnlyList<string>? CodecLadder = null,
    int? Width = null,
    int? Height = null,
    int FrameRate = 60,
    int BitrateKbps = 12000
);

/// <summary>
/// One audio capture row: which source to open, its gain, and whether it mixes into the default stereo track or
/// lands on its own isolated track.
/// </summary>
/// <param name="Id">A stable identifier, unique among the document's audio rows.</param>
/// <param name="Kind">The capture source kind.</param>
/// <param name="Device">The device identifier, or <see langword="null"/> for the system default.</param>
/// <param name="Gain">The linear gain applied before mixing.</param>
/// <param name="Track">The container routing (mixed or isolated).</param>
public sealed record RecordingAudioRow(
    string? Id = null,
    RecordingAudioKind Kind = RecordingAudioKind.Microphone,
    string? Device = null,
    float Gain = 1.0f,
    RecordingAudioTrackMode Track = RecordingAudioTrackMode.Mix
);

/// <summary>
/// One capture-only overlay row composited onto the recorded frame after the capture tap — it exists in the
/// recording and never in the game window. The fields relevant to a row depend on its <see cref="Kind"/>; the
/// document validator enforces the per-kind requirements. Positions are normalized (0..1) and measured from
/// <see cref="Anchor"/>.
/// </summary>
/// <param name="Kind">The overlay kind, which selects the relevant fields.</param>
/// <param name="X">The normalized horizontal position (0..1).</param>
/// <param name="Y">The normalized vertical position (0..1).</param>
/// <param name="Width">The normalized width (rectangles).</param>
/// <param name="Height">The normalized height (rectangles).</param>
/// <param name="PixelHeight">The text cap height in pixels (text and timecode).</param>
/// <param name="Content">The text content (text rows).</param>
/// <param name="Color">The fill color as <c>#RRGGBBAA</c> (or <c>#RRGGBB</c>, alpha opaque).</param>
/// <param name="OutlineColor">The outline color as <c>#RRGGBBAA</c> (rectangles), or <see langword="null"/> for no outline.</param>
/// <param name="Anchor">The anchor the normalized position is measured from.</param>
/// <param name="Clock">Which clock a timecode row reads.</param>
public sealed record OverlayRow(
    OverlayKind Kind = OverlayKind.Text,
    float X = 0.0f,
    float Y = 0.0f,
    float Width = 0.0f,
    float Height = 0.0f,
    float PixelHeight = 24.0f,
    string? Content = null,
    string? Color = "#FFFFFFFF",
    string? OutlineColor = null,
    OverlayAnchor Anchor = OverlayAnchor.TopLeft,
    OverlayClock Clock = OverlayClock.Session
);

/// <summary>
/// The recording graph document (<c>puck.recording.v1</c>): a versioned, data-defined description of one capture
/// — the output path, the clock model, the video lane, the audio rows, and the capture-only overlays. It is the
/// primitive that lives above any one game; World, the demo, or a headless render all drive the same document.
/// </summary>
/// <param name="Schema">The schema tag; must equal <see cref="SchemaVersion"/>.</param>
/// <param name="Output">The output file path, or <see langword="null"/> to compose one (extension chosen by the landed codec).</param>
/// <param name="Clock">The timestamp source.</param>
/// <param name="Video">The video lane, or <see langword="null"/> for the baked default.</param>
/// <param name="Audio">The audio rows; empty or <see langword="null"/> for none.</param>
/// <param name="Overlays">The capture-only overlays; empty or <see langword="null"/> for none.</param>
public sealed record RecordingDocument(
    string? Schema = RecordingDocument.SchemaVersion,
    string? Output = null,
    RecordingClock Clock = RecordingClock.Wall,
    RecordingVideo? Video = null,
    IReadOnlyList<RecordingAudioRow>? Audio = null,
    IReadOnlyList<OverlayRow>? Overlays = null
) {
    /// <summary>The only accepted schema tag.</summary>
    public const string SchemaVersion = "puck.recording.v1";

    /// <summary>Gets or sets the unrecognized top-level members preserved verbatim across a load/save round trip. A
    /// settable (not <c>init</c>) accessor is required: System.Text.Json appends to it during deserialization, and an
    /// <c>init</c> accessor makes the source generator try to bind it through the positional constructor and throw.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? Extensions { get; set; }

    /// <summary>Builds the baked default: mic + loopback mixed to one track, the av1 to h264 ladder, no overlays.</summary>
    /// <returns>The default document.</returns>
    public static RecordingDocument CreateDefault() {
        return new RecordingDocument(
            Audio: [
                new RecordingAudioRow(Id: "microphone", Kind: RecordingAudioKind.Microphone),
                new RecordingAudioRow(Id: "loopback", Kind: RecordingAudioKind.Loopback),
            ],
            Video: new RecordingVideo(CodecLadder: ["av1", "h264"])
        );
    }
}

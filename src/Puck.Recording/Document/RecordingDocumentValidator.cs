using Puck.Recording.Overlay;

namespace Puck.Recording.Document;

/// <summary>
/// The one thick gate for a <see cref="RecordingDocument"/>. It rejects a malformed document structurally — the
/// schema tag, a non-empty codec ladder of known tokens, a positive frame rate and bitrate, a positive extent when
/// given, unique non-empty audio ids, finite gains, the sim-clock-implies-no-audio rule, and every overlay row's
/// per-kind requirements (content, a parseable color, normalized position, positive sizes). A load that fails is
/// rejected loudly; the loader never half-accepts.
/// </summary>
public static class RecordingDocumentValidator {
    private static readonly string[] s_knownCodecs = ["av1", "h264"];

    /// <summary>Validates a document without throwing.</summary>
    /// <param name="document">The candidate document.</param>
    /// <param name="reason">The collapsed one-line failure reason, or empty on success.</param>
    /// <returns><see langword="true"/> when the document is valid.</returns>
    public static bool TryValidate(RecordingDocument document, out string reason) {
        try {
            Validate(document: document);
            reason = string.Empty;

            return true;
        } catch (InvalidOperationException exception) {
            reason = exception.Message.ReplaceLineEndings(replacementText: " ");

            return false;
        }
    }

    /// <summary>Validates a document, throwing with the collapsed error list on any failure.</summary>
    /// <param name="document">The candidate document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The document is invalid.</exception>
    public static void Validate(RecordingDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var errors = new List<string>();

        if (!string.Equals(a: document.Schema, b: RecordingDocument.SchemaVersion, comparisonType: StringComparison.Ordinal)) {
            errors.Add(item: $"schema '{document.Schema ?? "(absent)"}' is not '{RecordingDocument.SchemaVersion}'.");
        }

        ValidateVideo(video: document.Video, errors: errors);
        ValidateAudio(document: document, errors: errors);
        ValidateOverlays(overlays: document.Overlays, errors: errors);

        if (errors.Count > 0) {
            throw new InvalidOperationException(message: $"Invalid RecordingDocument:{Environment.NewLine} - {string.Join(separator: $"{Environment.NewLine} - ", values: errors)}");
        }
    }

    private static void ValidateVideo(RecordingVideo? video, List<string> errors) {
        if (video is null) {
            return;
        }

        var ladder = (video.CodecLadder ?? []);

        if (ladder.Count == 0) {
            errors.Add(item: "video.codecLadder requires at least one codec token.");
        } else {
            foreach (var codec in ladder) {
                if (Array.IndexOf(array: s_knownCodecs, value: codec) < 0) {
                    errors.Add(item: $"video.codecLadder token '{codec ?? "(null)"}' is not one of av1, h264.");
                }
            }
        }

        if (video.FrameRate <= 0) {
            errors.Add(item: $"video.frameRate {video.FrameRate} must be positive.");
        }

        if (video.BitrateKbps <= 0) {
            errors.Add(item: $"video.bitrateKbps {video.BitrateKbps} must be positive.");
        }

        if (video.Width is { } width && (width <= 0)) {
            errors.Add(item: $"video.width {width} must be positive when set.");
        }

        if (video.Height is { } height && (height <= 0)) {
            errors.Add(item: $"video.height {height} must be positive when set.");
        }
    }

    private static void ValidateAudio(RecordingDocument document, List<string> errors) {
        var audio = document.Audio;

        if (audio is not { Count: > 0 }) {
            return;
        }

        if (document.Clock == RecordingClock.Sim) {
            errors.Add(item: "audio rows are not allowed with the sim clock (deterministic renders carry no live audio).");
        }

        var ids = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < audio.Count); index++) {
            var row = audio[index];
            var path = $"audio[{index}]";

            if (string.IsNullOrWhiteSpace(value: row.Id)) {
                errors.Add(item: $"{path}.id is required.");
            } else if (!ids.Add(item: row.Id)) {
                errors.Add(item: $"{path}.id '{row.Id}' is duplicated.");
            }

            if (!float.IsFinite(f: row.Gain) || (row.Gain < 0.0f)) {
                errors.Add(item: $"{path}.gain {row.Gain} must be finite and non-negative.");
            }
        }
    }

    private static void ValidateOverlays(IReadOnlyList<OverlayRow>? overlays, List<string> errors) {
        if (overlays is not { Count: > 0 }) {
            return;
        }

        for (var index = 0; (index < overlays.Count); index++) {
            var row = overlays[index];
            var path = $"overlays[{index}]";

            if (!IsNormalized(value: row.X) || !IsNormalized(value: row.Y)) {
                errors.Add(item: $"{path} position ({row.X}, {row.Y}) must be normalized to 0..1.");
            }

            if (!Rgba32.TryParse(value: row.Color, color: out _)) {
                errors.Add(item: $"{path}.color '{row.Color ?? "(absent)"}' is not a #RRGGBBAA hex color.");
            }

            switch (row.Kind) {
                case OverlayKind.Text:
                    if (string.IsNullOrEmpty(value: row.Content)) {
                        errors.Add(item: $"{path}.content is required for a text overlay.");
                    }

                    if (!(row.PixelHeight > 0.0f)) {
                        errors.Add(item: $"{path}.pixelHeight {row.PixelHeight} must be positive.");
                    }

                    break;
                case OverlayKind.Timecode:
                    if (!(row.PixelHeight > 0.0f)) {
                        errors.Add(item: $"{path}.pixelHeight {row.PixelHeight} must be positive.");
                    }

                    break;
                case OverlayKind.Rect:
                    if (!(row.Width > 0.0f) || !(row.Height > 0.0f)) {
                        errors.Add(item: $"{path} rectangle size ({row.Width}, {row.Height}) must be positive.");
                    }

                    if ((row.OutlineColor is { } outline) && !Rgba32.TryParse(value: outline, color: out _)) {
                        errors.Add(item: $"{path}.outlineColor '{outline}' is not a #RRGGBBAA hex color.");
                    }

                    break;
                default:
                    errors.Add(item: $"{path}.kind {row.Kind} is not a known overlay kind.");

                    break;
            }
        }
    }

    private static bool IsNormalized(float value) =>
        (float.IsFinite(f: value) && (value >= 0.0f) && (value <= 1.0f));
}

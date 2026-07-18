using Puck.Abstractions.Recording;
using Puck.Recording.Document;

namespace Puck.Recording.Session;

/// <summary>
/// The wiring surface for a <see cref="RecordingSession"/>: the recording document plus the platform's two
/// Abstractions factories (video encoders, audio sources) and the source resolution the capture tap delivers. A
/// consumer (World, the demo) fills this and calls <see cref="RecordingSession.TryCreate"/>; the session resolves
/// the codec ladder and audio rows against real hardware, opening only what this machine can encode and capture.
/// </summary>
public sealed class RecordingSessionOptions {
    /// <summary>Gets or sets the recording document that describes the capture.</summary>
    public required RecordingDocument Document { get; set; }

    /// <summary>Gets or sets the video encoder factory, or <see langword="null"/> to record audio only.</summary>
    public IVideoEncoderFactory? VideoEncoderFactory { get; set; }

    /// <summary>Gets or sets the audio capture source factory, or <see langword="null"/> to record video only.</summary>
    public IAudioCaptureSourceFactory? AudioSourceFactory { get; set; }

    /// <summary>Gets or sets the source frame width the capture tap delivers, in pixels.</summary>
    public required int SourceWidth { get; set; }

    /// <summary>Gets or sets the source frame height the capture tap delivers, in pixels.</summary>
    public required int SourceHeight { get; set; }

    /// <summary>Gets or sets the encode queue depth (frames buffered between the render and encode threads).</summary>
    public int EncodeQueueCapacity { get; set; } = 8;

    /// <summary>Gets or sets the file-name prefix used when the document supplies no output path.</summary>
    public string FileNamePrefix { get; set; } = "puck";

    /// <summary>Gets or sets the directory used when the document supplies no output path.</summary>
    public string OutputDirectory { get; set; } = "recordings";
}

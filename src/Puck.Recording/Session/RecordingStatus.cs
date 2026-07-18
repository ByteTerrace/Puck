namespace Puck.Recording.Session;

/// <summary>
/// An honest point-in-time snapshot of a <see cref="RecordingSession"/>'s progress — the numbers a consumer's
/// status verb echoes: frames captured and dropped, audio samples dropped to overflow, bytes written, the codec
/// that landed, and the output path.
/// </summary>
/// <param name="FramesCaptured">The frames accepted into the encode pipeline.</param>
/// <param name="FramesDropped">The frames dropped because the encode queue was full.</param>
/// <param name="AudioSamplesDropped">The audio samples dropped to source-ring overflow.</param>
/// <param name="BytesWritten">The bytes committed to the output so far.</param>
/// <param name="CodecLanded">The Matroska codec id that landed for video, or a marker when audio-only.</param>
/// <param name="OutputPath">The output file path.</param>
/// <param name="VideoEnabled">Whether a video track is being recorded.</param>
/// <param name="AudioTrackCount">The number of audio tracks being recorded.</param>
public readonly record struct RecordingStatus(
    long FramesCaptured,
    long FramesDropped,
    long AudioSamplesDropped,
    long BytesWritten,
    string CodecLanded,
    string OutputPath,
    bool VideoEnabled,
    int AudioTrackCount
);

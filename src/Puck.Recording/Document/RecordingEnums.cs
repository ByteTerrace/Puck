namespace Puck.Recording.Document;

/// <summary>The recording's timestamp source.</summary>
public enum RecordingClock {
    /// <summary>Live capture: frames and audio are stamped from the wall clock (QPC) at consume time.</summary>
    Wall,
    /// <summary>Deterministic offline re-render: frames are stamped from the engine tick clock; audio must be empty.</summary>
    Sim,
}

/// <summary>The kind of an audio capture row.</summary>
public enum RecordingAudioKind {
    /// <summary>A capture device (the microphone).</summary>
    Microphone,
    /// <summary>The system output loopback (what the machine is playing).</summary>
    Loopback,
}

/// <summary>How an audio row lands in the container.</summary>
public enum RecordingAudioTrackMode {
    /// <summary>Summed into the single default stereo track (what a service such as YouTube reads).</summary>
    Mix,
    /// <summary>Its own Matroska track (archival multitrack).</summary>
    Isolated,
}

/// <summary>The kind of a capture-only overlay row.</summary>
public enum OverlayKind {
    /// <summary>A run of styled text.</summary>
    Text,
    /// <summary>A filled and/or outlined rectangle.</summary>
    Rect,
    /// <summary>A running timecode rendered as text.</summary>
    Timecode,
}

/// <summary>The anchor a normalized overlay position is measured from.</summary>
public enum OverlayAnchor {
    /// <summary>The top-left corner.</summary>
    TopLeft,
    /// <summary>The top edge, horizontally centered.</summary>
    TopCenter,
    /// <summary>The top-right corner.</summary>
    TopRight,
    /// <summary>The left edge, vertically centered.</summary>
    MiddleLeft,
    /// <summary>The center.</summary>
    Center,
    /// <summary>The right edge, vertically centered.</summary>
    MiddleRight,
    /// <summary>The bottom-left corner.</summary>
    BottomLeft,
    /// <summary>The bottom edge, horizontally centered.</summary>
    BottomCenter,
    /// <summary>The bottom-right corner.</summary>
    BottomRight,
}

/// <summary>Which clock a <see cref="OverlayKind.Timecode"/> row reads.</summary>
public enum OverlayClock {
    /// <summary>The wall-clock session time since capture began.</summary>
    Session,
    /// <summary>The simulation tick time of the frame being composited.</summary>
    Sim,
}

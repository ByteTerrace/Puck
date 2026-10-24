using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Recording.Document;

/// <summary>
/// The recording's timestamp source — the clock a player's timeline is measured in. The block timestamps written to
/// the container come from this choice, so it decides what a playback position MEANS: <see cref="Wall"/> plays back
/// against real elapsed time, <see cref="Sim"/> against simulated time. The two diverge whenever the simulation is
/// not keeping real time, which capture itself provokes (the frame readback is synchronous).
/// </summary>
/// <remarks>
/// Whichever is chosen, the container timeline is REBASED so the first written block sits at zero. Any latency
/// between arming a capture and the first encoded packet is therefore absent from playback time but present in an
/// <see cref="OverlayKind.Timecode"/> overlay, which reads the frame's own unrebased clock — a burnt-in timecode and
/// the player's position differ by that startup latency, constant for the whole take. An overlay row also chooses
/// its clock independently (<see cref="OverlayClock"/>), so a document pairing <see cref="Sim"/> here with
/// <see cref="OverlayClock.Session"/> there burns in a timecode measuring something the timeline does not.
/// </remarks>
[JsonConverter(typeof(StrictEnumConverter<RecordingClock>))]
public enum RecordingClock {
    /// <summary>Live capture (the shipped default): frames and audio are stamped from the wall clock (QPC) at consume
    /// time, NOT from the engine tick clock. Playback time is real elapsed capture time.</summary>
    Wall,
    /// <summary>Deterministic offline re-render: frames are stamped from the engine tick clock; audio must be empty.
    /// Playback time is simulated time, which runs slower than real time whenever the engine cannot keep up.</summary>
    Sim,
}
/// <summary>The kind of an audio capture row.</summary>
[JsonConverter(typeof(StrictEnumConverter<RecordingAudioKind>))]
public enum RecordingAudioKind {
    /// <summary>A capture device (the microphone).</summary>
    Microphone,
    /// <summary>The system output loopback (what the machine is playing).</summary>
    Loopback,
}
/// <summary>How an audio row lands in the container.</summary>
[JsonConverter(typeof(StrictEnumConverter<RecordingAudioTrackMode>))]
public enum RecordingAudioTrackMode {
    /// <summary>Summed into the single default stereo track (what a service such as YouTube reads).</summary>
    Mix,
    /// <summary>Its own Matroska track (archival multitrack).</summary>
    Isolated,
}
/// <summary>The kind of a capture-only overlay row.</summary>
[JsonConverter(typeof(StrictEnumConverter<OverlayKind>))]
public enum OverlayKind {
    /// <summary>A run of styled text.</summary>
    Text,
    /// <summary>A filled and/or outlined rectangle.</summary>
    Rect,
    /// <summary>A running timecode rendered as text.</summary>
    Timecode,
}
/// <summary>The anchor a normalized overlay position is measured from.</summary>
[JsonConverter(typeof(StrictEnumConverter<OverlayAnchor>))]
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
/// <summary>Which clock a <see cref="OverlayKind.Timecode"/> row reads. This is chosen independently of
/// <see cref="RecordingClock"/>, which is what the container's own timeline measures — see that type's remarks for
/// why a burnt-in timecode and a player's position are not the same number.</summary>
[JsonConverter(typeof(StrictEnumConverter<OverlayClock>))]
public enum OverlayClock {
    /// <summary>The wall-clock session time since capture began, unrebased.</summary>
    Session,
    /// <summary>The simulation tick time of the frame being composited.</summary>
    Sim,
}

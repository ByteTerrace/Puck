using System.Text.Json.Serialization;

using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// One seat's control feel — how its mouse-look orbit responds: sensitivity, inversion, the pitch clamp, and what
/// arms the drag. Authored on <see cref="WorldPlayerDefaults.SeatLook"/> and read from whichever document owns the
/// seat, so feel is per-seat rather than per-world: an unclaimed seat reads the world's, and a joined seat reads its
/// own identity document's, arriving on the same seat-document recompose that carries that identity's bindings and
/// HUD. A player's feel therefore travels with their profile across worlds and machines, and two people sharing a
/// couch can want opposite things without one of them losing.
/// </summary>
/// <remarks>Presentation-only. Nothing here rides a <c>CommandSnapshot</c> or feeds the deterministic simulation —
/// it shapes the local camera and nothing else, so a seat's feel can differ across two machines watching the same
/// world without their simulations diverging. There is no engine fallback: the member is required, so a document
/// either states a seat's feel or fails validation, and no seat is ever silently handed a number nobody chose.</remarks>
/// <param name="YawSensitivity">The yaw response in radians per pixel of raw (un-accelerated) pointer motion along
/// the X axis.</param>
/// <param name="PitchSensitivity">The pitch response in radians per pixel of raw pointer motion along the Y
/// axis.</param>
/// <param name="InvertYaw">Whether the yaw response is inverted.</param>
/// <param name="InvertPitch">Whether the pitch response is inverted.</param>
/// <param name="MinPitch">The pitch clamp floor in radians.</param>
/// <param name="MaxPitch">The pitch clamp ceiling in radians.</param>
/// <param name="Arming">What enables the orbit drag.</param>
/// <param name="WorldAxes">Whether the live orbit composes onto world axes rather than the seat body's own facing
/// (an absolute orbit versus one that rides the body's yaw).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSeatLook(
    float YawSensitivity,
    float PitchSensitivity,
    bool InvertYaw,
    bool InvertPitch,
    float MinPitch,
    float MaxPitch,
    WorldSeatLookArming Arming,
    bool WorldAxes
);

/// <summary>Identifies what arms a <see cref="WorldSeatLook"/> orbit drag.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSeatLookArming>))]
public enum WorldSeatLookArming : byte {
    /// <summary>Disables the orbit drag entirely.</summary>
    None,
    /// <summary>Orbits continuously from raw pointer motion, with no arming button.</summary>
    Always,
    /// <summary>Arms while the left mouse button is held.</summary>
    LeftButton,
    /// <summary>Arms while the right mouse button is held.</summary>
    RightButton,
    /// <summary>Arms while the middle mouse button is held.</summary>
    MiddleButton,
}

using System.Text.Json.Serialization;

using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// One seat's input feel — pointer/right-stick sensitivity, inversion, and pointer arming. Camera structure does not
/// travel with a profile: the framing world's <see cref="WorldSeatViewControl"/> owns pitch limits and yaw reference.
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
/// <param name="Arming">What enables the orbit drag.</param>
/// <param name="StickLookRate">The look stick's yaw/pitch rate in radians per second at full deflection.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSeatLook(
    float YawSensitivity,
    float PitchSensitivity,
    bool InvertYaw,
    bool InvertPitch,
    WorldSeatLookArming Arming,
    float StickLookRate
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

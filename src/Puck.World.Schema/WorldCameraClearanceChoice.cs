namespace Puck.World;

/// <summary>How a seat's chase view handles a room that cannot fit its boom and body.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldCameraClearanceChoice>))]
public enum WorldCameraClearanceChoice {
    /// <summary>Inherit the world's authored choice (player preferences only).</summary>
    Auto,
    /// <summary>Keep the body solid, rising and cutting away intervening walls.</summary>
    Cutaway,
    /// <summary>Keep the eye in the room at the requested pitch, fading only this view's own body.</summary>
    Fade,
}
/// <summary>Full-size distances for the room-first chase eye, scaled by the perceived body's live scale.</summary>
/// <param name="StartDistance">The boom distance at which the body starts dissolving.</param>
/// <param name="EndDistance">The distance at which the body is completely hidden.</param>
/// <param name="MinimumBoom">The preferred shortest boom; a nearer wall takes precedence.</param>
public sealed record WorldCameraCharacterFade(float StartDistance = 1.25f, float EndDistance = 0.45f, float MinimumBoom = 0.35f);

namespace Puck.World;

/// <summary>How a seat's chase view handles a room that cannot fit its boom and body.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldCameraClearanceChoice>))]
public enum WorldCameraClearanceChoice {
    /// <summary>Inherit the world's authored choice (player preferences only).</summary>
    Auto,
    /// <summary>Keep the body solid, rising and cutting away intervening walls.</summary>
    Cutaway,
    /// <summary>Keep the eye in the room on the requested heading, fading only this view's own body.</summary>
    Fade,
}
/// <summary>The most a chase boom rises above the rig's own elevation, in radians, per treatment of a close
/// obstruction. A boom rises only when no level pull-in keeps the eye far enough out: past the body in cutaway, past the
/// character fade's minimum boom in fade.</summary>
/// <param name="Fade">The rise ceiling while the seat fades its body, in [0, π/2].</param>
/// <param name="Cutaway">The rise ceiling while the seat cuts walls away, in [0, π/2].</param>
public sealed record WorldCameraClearanceRise(float Fade, float Cutaway) {
    /// <summary>Returns the ceiling one treatment rises to.</summary>
    /// <param name="choice">The resolved treatment; anything but <see cref="WorldCameraClearanceChoice.Cutaway"/>
    /// reads <see cref="Fade"/>.</param>
    /// <returns>The ceiling, in radians.</returns>
    public float Of(WorldCameraClearanceChoice choice) => ((choice == WorldCameraClearanceChoice.Cutaway) ? Cutaway : Fade);
}
/// <summary>The target a chase boom's look migrates to as geometry shortens it, per treatment of a close obstruction,
/// in the axes of the look-at's own target offset.</summary>
/// <param name="Fade">The close target while the seat fades its body.</param>
/// <param name="Cutaway">The close target while the seat cuts walls away.</param>
public sealed record WorldCameraCloseTarget(DocumentVector3 Fade, DocumentVector3 Cutaway);
/// <summary>Full-size distances for the room-first chase eye, scaled by the perceived body's live scale. Every value is
/// the author's.</summary>
/// <param name="StartDistance">The boom distance at which the body starts dissolving.</param>
/// <param name="EndDistance">The distance at which the body is completely hidden.</param>
/// <param name="MinimumBoom">The preferred shortest boom; a nearer wall takes precedence.</param>
public sealed record WorldCameraCharacterFade(float StartDistance, float EndDistance, float MinimumBoom);

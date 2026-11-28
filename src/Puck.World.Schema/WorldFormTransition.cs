using Puck.Abstractions.Documents;
using System.Text.Json.Serialization;
using Puck.Assets.Documents;

namespace Puck.World;

/// <summary>How a look's body changes form on screen: what plays when the look starts to be worn (a body arriving in it,
/// or switching to it from another form) and when it stops (a body leaving, or switching away). Presentation only: read
/// by the client's stamp pool and rigid copies, never by the simulation, and every random draw it makes is seeded by the
/// body, so every client plays the same effect. A look with no transition switches at once.</summary>
/// <param name="Arrive">What plays as the look starts to be worn, or <see langword="null"/> for an instant start.</param>
/// <param name="Leave">What plays as the look stops being worn, or <see langword="null"/> for an instant end. While it
/// plays, the outgoing form is drawn beside the incoming one.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFormTransition(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFormEffect? Arrive = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFormEffect? Leave = null
);

/// <summary>One side of a form transition: a band that sweeps over the form's real surface, building it behind the band
/// as it arrives or dissolving it as it leaves, with an emissive edge and motes that settle onto or rise off the surface
/// the band crosses; and, for a form shed or assembled piece by piece, each moving part of it launched off with a seeded
/// impulse (bouncing off the world's real surfaces) or flown in from a scattered start to lock into place. Every value
/// has a neutral default, so an author states only what the effect needs; the standard package's form transition module
/// (<c>worlds/standard/form-transition.puck</c>) chooses the values its effects use.</summary>
/// <param name="Seconds">How long the band takes to sweep the form, seconds; positive.</param>
/// <param name="Delay">How long after the switch the effect starts, seconds; not negative. A leaving form waits drawn
/// whole, an arriving one waits undrawn.</param>
/// <param name="Direction">The way the band sweeps, in the creation's own frame, or <see langword="null"/> for up.</param>
/// <param name="EdgeColor">The edge's hex colour (<c>#RRGGBB</c>), or <see langword="null"/> for white.</param>
/// <param name="EdgeLuminance">The edge's luminance, nits; zero draws no edge.</param>
/// <param name="EdgeWidth">How far behind the band the edge glows, metres.</param>
/// <param name="Roughness">How far the band's front wanders along its sweep by the lattice noise at each surface point,
/// metres; zero is a flat front.</param>
/// <param name="RoughnessScale">The wavelength of that noise, metres; positive.</param>
/// <param name="Motes">How many motes rise off or settle onto the form; zero for none.</param>
/// <param name="MoteColor">The motes' hex colour, or <see langword="null"/> for the edge's.</param>
/// <param name="MoteLuminance">The motes' luminance at their brightest, nits.</param>
/// <param name="MoteSize">A mote's diameter, metres.</param>
/// <param name="MoteSeconds">A mote's life, seconds.</param>
/// <param name="MoteSpeed">How fast a mote drifts off the surface, metres a second.</param>
/// <param name="MoteRise">The acceleration a mote takes along world up, metres a second squared; negative falls.</param>
/// <param name="MoteSwirl">How fast a mote's drift turns about the sweep, radians a second.</param>
/// <param name="Pieces">Whether the form's moving parts fly apart (leaving) or fly together (arriving) as separate
/// pieces, each with a band of its own, rather than the whole form under one band.</param>
/// <param name="PieceSpeed">How fast a shed piece is launched off the body, metres a second; an assembling piece's
/// flight is timed to <see cref="PieceSeconds"/> instead.</param>
/// <param name="PieceLift">How much of a shed piece's launch leans up, from zero (straight out from the body) to one.</param>
/// <param name="PieceSpin">How fast a piece tumbles, radians a second.</param>
/// <param name="PieceGravity">The acceleration a shed piece falls by, metres a second squared.</param>
/// <param name="PieceBounce">The share of its speed a shed piece keeps off a surface it strikes, from zero to one.</param>
/// <param name="PieceReach">How far from its place an assembling piece starts, metres.</param>
/// <param name="PieceSeconds">How long a piece flies before its band sweeps it (shed) or how long an assembling piece takes
/// to reach its place, seconds.</param>
/// <param name="PieceStagger">The most a piece's start is delayed past the effect's, seconds, drawn per piece.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFormEffect(
    float Seconds = 1f,
    float Delay = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Direction = null,
    [property: DocumentText("a color literal")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EdgeColor = null,
    float EdgeLuminance = 0f,
    float EdgeWidth = 0.05f,
    float Roughness = 0f,
    float RoughnessScale = 0.1f,
    int Motes = 0,
    [property: DocumentText("a color literal")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? MoteColor = null,
    float MoteLuminance = 0f,
    float MoteSize = 0.01f,
    float MoteSeconds = 0.6f,
    float MoteSpeed = 0.3f,
    float MoteRise = 0f,
    float MoteSwirl = 0f,
    bool Pieces = false,
    float PieceSpeed = 2f,
    float PieceLift = 0.5f,
    float PieceSpin = 4f,
    float PieceGravity = 9.81f,
    float PieceBounce = 0.3f,
    float PieceReach = 0.5f,
    float PieceSeconds = 0.5f,
    float PieceStagger = 0f
) {
    /// <summary>Gets the longest any part of the effect plays from the switch, seconds: its delay, its staggered pieces'
    /// flights and its sweep, and the last motes' lives after it; a form is let go once this has passed.</summary>
    public float TotalSeconds => (MathF.Max(x: Delay, y: 0f) + (Pieces ? (MathF.Max(x: PieceStagger, y: 0f) + MathF.Max(x: PieceSeconds, y: 0f)) : 0f) +
        MathF.Max(x: Seconds, y: 0f) + ((Motes > 0) ? MathF.Max(x: MoteSeconds, y: 0f) : 0f));
}

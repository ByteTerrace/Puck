using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Physics.Motion;

/// <summary>The population subset a sensed target source considers.</summary>
[JsonConverter(typeof(StrictEnumConverter<BodyTargetScope>))]
public enum BodyTargetScope : byte {
    /// <summary>Active local-seat bodies.</summary>
    Seats,

    /// <summary>Every active body other than the sensing body.</summary>
    Bodies,
}
/// <summary>The one target source a producer program declares.</summary>
[JsonDerivedType(typeof(BodyTargetSource.Sensed), typeDiscriminator: "sensed")]
[JsonDerivedType(typeof(BodyTargetSource.Designated), typeDiscriminator: "designated")]
[JsonDerivedType(typeof(BodyTargetSource.CurveFollow), typeDiscriminator: "curve")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record BodyTargetSource {
    private BodyTargetSource() {
    }

    /// <summary>Selects the nearest member of <paramref name="Scope"/> inside a body-forward cone.</summary>
    /// <param name="Scope">The population subset considered.</param>
    /// <param name="Range">The cone's maximum world-space distance.</param>
    /// <param name="HalfAngleDegrees">The cone half-angle in degrees.</param>
    /// <param name="RequiresLineOfSight">Whether solid world geometry must leave the segment unobstructed.</param>
    public sealed record Sensed(BodyTargetScope Scope, float Range, float HalfAngleDegrees, bool RequiresLineOfSight) : BodyTargetSource;
    /// <summary>Reads the named target register owned by the body running the producer.</summary>
    /// <param name="Register">The authored target-register name.</param>
    public sealed record Designated(string Register) : BodyTargetSource;
    /// <summary>Feeds the producer a point that travels along a named <c>curves</c> row at a constant authored arc
    /// rate — the deterministic, fixed-point curve-follow target the kart-track charter inherits. The travelled arc
    /// position is per-body simulation state (see <c>Puck.World.Server.BodyProducerSupport.BodyProducerState.
    /// CurveArcRaw</c>), snapshot- and replay-covered exactly like every other producer accumulator.</summary>
    /// <param name="Curve">The named <c>curves</c> row to travel — a dangling name refuses at validation, exactly
    /// like a dangling <see cref="Designated.Register"/>.</param>
    /// <param name="Rate">The signed arc-length rate, in the curve's own units per second. Negative travels the
    /// curve in reverse. A closed row wraps at the loop point; an open row clamps at either end and holds the
    /// endpoint target rather than overshooting it.</param>
    public sealed record CurveFollow(string Curve, float Rate) : BodyTargetSource;
}

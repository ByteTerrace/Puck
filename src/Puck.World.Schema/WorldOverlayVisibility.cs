using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>A presentation fact an overlay element's <see cref="OverlayPredicate"/> reads. Every fact is per local
/// seat and never enters the simulation; a world-scope element (a <c>hud.panels</c> row) reads a fact as true when it
/// holds for any joined local seat.</summary>
[JsonConverter(typeof(StrictEnumConverter<OverlayFact>))]
public enum OverlayFact : byte {
    /// <summary>The seat sent any input (a routed signal) this tick.</summary>
    SeatInput,
    /// <summary>The seat's pointer moved this tick.</summary>
    PointerMotion,
    /// <summary>The seat's binding wheel is open.</summary>
    WheelOpen,
    /// <summary>The seat's console is open.</summary>
    ConsoleOpen,
    /// <summary>The seat's camera control application is active (see <see cref="WorldSeatModeState.Target"/>).</summary>
    SeatCameraApplication,
}
/// <summary>An overlay element's visibility condition — the presentation twin of <see cref="ActionPredicate"/>, over
/// <see cref="OverlayFact"/>s. Absent on an element means always visible.</summary>
[JsonDerivedType(typeof(OverlayPredicate.Now), typeDiscriminator: "now")]
[JsonDerivedType(typeof(OverlayPredicate.Recently), typeDiscriminator: "recently")]
[JsonDerivedType(typeof(OverlayPredicate.All), typeDiscriminator: "all")]
[JsonDerivedType(typeof(OverlayPredicate.Any), typeDiscriminator: "any")]
[JsonDerivedType(typeof(OverlayPredicate.Not), typeDiscriminator: "not")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record OverlayPredicate {
    /// <summary>The fact holds this frame.</summary>
    public sealed record Now(OverlayFact Fact) : OverlayPredicate;
    /// <summary>The fact held within the last <paramref name="WindowSeconds"/>.</summary>
    public sealed record Recently(OverlayFact Fact, float WindowSeconds) : OverlayPredicate;
    /// <summary>Every predicate holds.</summary>
    public sealed record All(IReadOnlyList<OverlayPredicate> Predicates) : OverlayPredicate;
    /// <summary>At least one predicate holds.</summary>
    public sealed record Any(IReadOnlyList<OverlayPredicate> Predicates) : OverlayPredicate;
    /// <summary>The predicate does not hold.</summary>
    public sealed record Not(OverlayPredicate Predicate) : OverlayPredicate;
}

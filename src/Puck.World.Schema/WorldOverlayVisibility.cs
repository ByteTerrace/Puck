using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Physics.Motion;

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
/// <summary>Who a subject-bearing presentation predicate is about — a seat's avatar, a placement, an entity, or a
/// quantifier over seats or speakers. Presentation-only: a subject resolves to a body through the seat's perceived
/// body (so possession follows) and never enters the simulation.</summary>
[JsonDerivedType(typeof(OverlaySubject.Seat), typeDiscriminator: "seat")]
[JsonDerivedType(typeof(OverlaySubject.Placement), typeDiscriminator: "placement")]
[JsonDerivedType(typeof(OverlaySubject.Entity), typeDiscriminator: "entity")]
[JsonDerivedType(typeof(OverlaySubject.AnySeat), typeDiscriminator: "anySeat")]
[JsonDerivedType(typeof(OverlaySubject.RecentSpeaker), typeDiscriminator: "recentSpeaker")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record OverlaySubject {
    private OverlaySubject() {
    }

    /// <summary>A local seat's avatar — <see langword="null"/> is the enclosing seat scope (the seat whose panel,
    /// bar, or camera is being evaluated), an explicit number is 1-based like <c>Camera.Seat</c>.</summary>
    public sealed record Seat([property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Number = null) : OverlaySubject;
    /// <summary>A placement instance, optionally one of its shapes.</summary>
    public sealed record Placement(string PlacementId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ShapeId = null) : OverlaySubject;
    /// <summary>A population entity by 0-based index.</summary>
    public sealed record Entity(int Index) : OverlaySubject;
    /// <summary>Any joined local seat's avatar — the predicate holds when it holds for at least one.</summary>
    public sealed record AnySeat : OverlaySubject;
    /// <summary>The body that most recently spoke (see <see cref="OverlayPredicate.Speaking"/>); nothing until
    /// something has.</summary>
    public sealed record RecentSpeaker : OverlaySubject;
}
/// <summary>An overlay element's visibility condition — the presentation twin of <see cref="ActionPredicate"/>, over
/// <see cref="OverlayFact"/>s. Absent on an element means always visible.</summary>
[JsonDerivedType(typeof(OverlayPredicate.Now), typeDiscriminator: "now")]
[JsonDerivedType(typeof(OverlayPredicate.Recently), typeDiscriminator: "recently")]
[JsonDerivedType(typeof(OverlayPredicate.All), typeDiscriminator: "all")]
[JsonDerivedType(typeof(OverlayPredicate.Any), typeDiscriminator: "any")]
[JsonDerivedType(typeof(OverlayPredicate.Not), typeDiscriminator: "not")]
[JsonDerivedType(typeof(OverlayPredicate.Speaking), typeDiscriminator: "speaking")]
[JsonDerivedType(typeof(OverlayPredicate.Near), typeDiscriminator: "near")]
[JsonDerivedType(typeof(OverlayPredicate.State), typeDiscriminator: "state")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record OverlayPredicate {
    /// <summary>The fact holds this frame.</summary>
    public sealed record Now(OverlayFact Fact) : OverlayPredicate;
    /// <summary>The fact held within the last <paramref name="WindowSeconds"/>.</summary>
    /// <param name="Fact">The fact whose recency is tested.</param>
    /// <param name="WindowSeconds">How long after the fact last held the predicate still holds in full.</param>
    /// <param name="FadeSeconds">How long after the window the predicate's PRESENCE eases from 1 to 0 — a surface
    /// reading presence rather than the boolean fades out instead of cutting, and the predicate still HOLDS
    /// (presence above 0) throughout the fade. 0 cuts at the window's end.</param>
    public sealed record Recently(OverlayFact Fact, float WindowSeconds, float FadeSeconds = 0f) : OverlayPredicate;
    /// <summary>Every predicate holds.</summary>
    public sealed record All(IReadOnlyList<OverlayPredicate> Predicates) : OverlayPredicate;
    /// <summary>At least one predicate holds.</summary>
    public sealed record Any(IReadOnlyList<OverlayPredicate> Predicates) : OverlayPredicate;
    /// <summary>The predicate does not hold.</summary>
    public sealed record Not(OverlayPredicate Predicate) : OverlayPredicate;
    /// <summary>The subject spoke within the last <paramref name="WindowSeconds"/> — a chat line, a dialogue line, a
    /// live voice: every speech path stamps the same presentation clock, so the predicate reads one fact whatever
    /// produced it. Presence eases across <paramref name="FadeSeconds"/> after the window like
    /// <see cref="Recently"/>.</summary>
    public sealed record Speaking(OverlaySubject Subject, float WindowSeconds, float FadeSeconds = 0f) : OverlayPredicate;
    /// <summary>The subject is within <paramref name="Distance"/> world units of <paramref name="Of"/> (the enclosing
    /// seat's avatar when <see langword="null"/>), read off the presentation poses each frame; an unresolvable subject
    /// is infinitely far.</summary>
    public sealed record Near(OverlaySubject Subject, float Distance, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlaySubject? Of = null) : OverlayPredicate;
    /// <summary>A state cell compares to a literal: <paramref name="Binding"/> is <c>state.&lt;row&gt;[.&lt;key&gt;]</c>,
    /// and exactly one of <paramref name="Value"/> (numeric rows) or <paramref name="Text"/> (text rows, compared
    /// ordinally; only <see cref="ActionStateComparison.Equal"/>/<see cref="ActionStateComparison.NotEqual"/>) is
    /// authored. The same cell a bar's <c>layoutCell</c> or a wheel sector writes.</summary>
    public sealed record State(
        string Binding,
        ActionStateComparison Comparison = ActionStateComparison.Equal,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Value = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null
    ) : OverlayPredicate;
}

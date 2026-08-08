using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>How a <see cref="WorldInteraction"/> detects that its two operands have come together — LOWERED to head
/// 5's entity-addressable spatial rules (<see cref="WorldRuleFacts.DistancePrefix"/>/<see cref="WorldRuleFacts.RegionPrefix"/>),
/// never a second co-occurrence engine.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldInteractionCoOccurrence>))]
public enum WorldInteractionCoOccurrence : byte {
    /// <summary>The carrier most strongly tagged <see cref="WorldInteraction.Left"/> and the carrier most strongly
    /// tagged <see cref="WorldInteraction.Right"/> (each resolved the SAME way a standalone <c>$argmax:</c> operand
    /// would) sit within <see cref="WorldInteraction.Range"/> of one another — <c>property x property</c>.</summary>
    Distance,

    /// <summary>SOME carrier is tagged <see cref="WorldInteraction.Left"/>, AND <see cref="WorldInteraction.Right"/>
    /// (a placement id carrying a region facet) currently has at least one occupant — <c>property x region</c>. An
    /// aggregate co-occurrence, on the SAME deliberate terms <see cref="WorldRuleFacts.RegionPrefix"/>'s own remarks
    /// give for staying an occupant COUNT rather than a per-body membership test (there is no "for every active body"
    /// quantifier in the rule vocabulary this lowers to).</summary>
    Region,
}

/// <summary>
/// One row of the world's <c>interactions</c> section — an authorable <c>property x property</c> (or
/// <c>property x region</c>) <c>-&gt; effect</c> table entry: the generalized A x B -&gt; F emergent-chemistry
/// primitive. NOT a second rule engine: <c>WorldRuleCompiler.CompileAllInteractions</c> DESUGARS every row into a
/// synthesized <see cref="WorldRule"/> — its co-occurrence spelled as an ordinary <see cref="ActionPredicate.CompareState"/>/
/// <see cref="ActionPredicate.All"/> gate over the SAME reserved channels a hand-authored rule already reads — and
/// compiles it through the identical <c>WorldRuleCompiler.Compile</c> path, so it rides the SAME per-tick evaluation,
/// EDGE/LEVEL latch, journal, and undo a rule already has. There is exactly one evaluation engine; this is a second
/// AUTHORING SURFACE over it.
/// </summary>
/// <remarks>
/// <para><b>Effects reuse the rule effect set, unchanged.</b> <see cref="Effects"/> carries the SAME
/// <see cref="ActionEffect"/> vocabulary a <see cref="WorldRule"/> admits at world scope — a state write
/// (<see cref="ActionEffect.SetState"/>/<see cref="ActionEffect.AddState"/>, the natural way to transform or clear a
/// carrier's OWN property tag by writing its cell with a literal key) and the spawn/despawn-carrier effect
/// (<see cref="ActionEffect.UpsertPlacement"/>/<see cref="ActionEffect.RemovePlacement"/>) — never a new effect kind.
/// An effect names its target row/cell LITERALLY (exactly as a hand-authored rule's effect always has): the gate can
/// address "whichever carrier is most hot" via <c>$argmax:</c>, but no effect in this substrate — rule OR interaction
/// — can yet write to "whichever cell the gate resolved"; author the interacting carriers' own cells directly.</para>
/// <para><b>Same-tick ordering is DECLARATION ORDER — the SAME tiebreak the state-lifetime refute proved for rules.</b>
/// Interactions evaluate as a whole array, in document order, AFTER every ordinary rule (see
/// <c>WorldServer.EvaluateWorldRules</c>) — so a cascade (interaction A tags a carrier that interaction B's gate then
/// reads) composes deterministically within one tick exactly as a rule chain does, and the array itself is
/// snapshotted before iterating so INSTALLING effects can never make an interaction skip its own siblings mid-tick.
/// </para>
/// <para><b>Cascades are not authored — they compose.</b> Nothing about <see cref="WorldInteraction"/> knows about a
/// SECOND interaction; a two-step cascade (A melts a cold carrier into a wet one; B shocks a wet, charged carrier)
/// falls out of A's effect changing the properties B's gate reads, on the same deterministic per-tick evaluation
/// order every other world-rule chain already rides — fixed-point and seeded, hence replayable.</para>
/// </remarks>
/// <param name="Name">The interaction's stable name — unique within the section (a SEPARATE namespace from
/// <see cref="WorldRule.Name"/>: an interaction desugars into its OWN synthesized rule rather than sharing the
/// authored rule list, so the two may coincide without colliding). A <see cref="WorldCellName"/>, never
/// <c>$</c>-prefixed — that prefix marks what the ENGINE mints, and nothing mints an interaction.</param>
/// <param name="Left">A property name, validated against the declared <c>properties</c> registry — the carrier a
/// candidate co-occurrence is searched FROM (the <c>$argmax:</c> resolution — "the carrier most strongly tagged
/// Left").</param>
/// <param name="Right">For <see cref="WorldInteractionCoOccurrence.Distance"/>, a SECOND property name, validated the
/// same way as <see cref="Left"/>. For <see cref="WorldInteractionCoOccurrence.Region"/>, a placement id carrying a
/// region facet — validated against the declared <c>placements</c> section instead, never against the property
/// registry.</param>
/// <param name="CoOccurrence">How the two operands' coming-together is detected.</param>
/// <param name="Range">The distance threshold, for <see cref="WorldInteractionCoOccurrence.Distance"/> alone —
/// ignored (and unchecked) under <see cref="WorldInteractionCoOccurrence.Region"/>.</param>
/// <param name="Effects">The effects applied in order when the interaction fires — see this type's remarks.</param>
/// <param name="Mode">Whether the interaction fires every tick the co-occurrence holds
/// (<see cref="ActionTriggerMode.Level"/>) or once per crossing (<see cref="ActionTriggerMode.Edge"/>, the default —
/// an interaction that transforms/spawns/despawns almost always wants Edge, for the SAME reason a rule that writes a
/// row does: level-firing a spawn is a journal entry every tick the co-occurrence holds).</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInteraction(
    WorldCellName Name,
    string Left,
    string Right,
    WorldInteractionCoOccurrence CoOccurrence,
    float Range,
    IReadOnlyList<ActionEffect> Effects,
    ActionTriggerMode Mode = ActionTriggerMode.Edge
);

/// <summary>The <c>interactions</c> document section — the generalized property-interaction table's document shape.
/// OPTIONAL (like <c>rules</c>/<c>groups</c>): a document declaring none carries a <see langword="null"/> section
/// here rather than an empty one, so adding this section never refuses an existing world at boot.</summary>
/// <param name="Interactions">The declared interaction rows, in authoring/evaluation order.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInteractionsSection(IReadOnlyList<WorldInteraction> Interactions) {
    /// <summary>Gets the empty section — every mutation composer's fallback for a document that declared no
    /// <c>interactions</c> section at all (<c>current.Interactions ?? Empty</c>).</summary>
    public static WorldInteractionsSection Empty { get; } = new(Interactions: []);
}

/// <summary>Capacity constants for the interaction table — a made-up, sensible fixture ceiling (this is a generic
/// engine primitive; a genre world authors its own interaction count, never a size drawn from a specific game).
/// </summary>
public static class WorldInteractionCapacity {
    /// <summary>The maximum declared interaction rows a document may carry.</summary>
    public const int MaxInteractions = 128;
}

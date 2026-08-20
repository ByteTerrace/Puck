using System.Text.Json.Serialization;
using Puck.Physics.Motion;

namespace Puck.World;

/// <summary>How a <see cref="WorldInteraction"/> detects that its two operands have come together — lowered to head
/// 5's entity-addressable spatial rules (<see cref="WorldRuleFacts.DistancePrefix"/>/<see cref="WorldRuleFacts.RegionPrefix"/>),
/// never a second co-occurrence engine.</summary>
[JsonConverter(typeof(Puck.Abstractions.Documents.StrictEnumConverter<WorldInteractionCoOccurrence>))]
public enum WorldInteractionCoOccurrence : byte {
    /// <summary>The carrier most strongly tagged <see cref="WorldInteraction.Left"/> and the carrier most strongly
    /// tagged <see cref="WorldInteraction.Right"/> (each resolved the same way a standalone <c>$argmax:</c> operand
    /// would) sit within <see cref="WorldInteraction.Range"/> of one another — <c>property x property</c>.</summary>
    Distance,

    /// <summary>Some carrier is tagged <see cref="WorldInteraction.Left"/>, and <see cref="WorldInteraction.Right"/>
    /// (a placement id carrying a region facet) currently has at least one occupant — <c>property x region</c>. An
    /// aggregate co-occurrence, on the same deliberate terms <see cref="WorldRuleFacts.RegionPrefix"/>'s own remarks
    /// give for staying an occupant count rather than a per-body membership test (there is no "for every active body"
    /// quantifier in the rule vocabulary this lowers to).</summary>
    Region,
}
/// <summary>
/// One row of the world's <c>interactions</c> section — an authorable <c>property x property</c> (or
/// <c>property x region</c>) <c>-&gt; effect</c> table entry, the generalized A x B -&gt; F emergent-chemistry
/// primitive. Not a second rule engine: <c>WorldRuleCompiler.CompileAllInteractions</c> desugars every row into a
/// synthesized <see cref="WorldRule"/> — its co-occurrence spelled as an ordinary
/// <see cref="ActionPredicate.CompareState"/>/<see cref="ActionPredicate.All"/> gate over the same reserved channels
/// a hand-authored rule already reads — and compiles it through the identical <c>WorldRuleCompiler.Compile</c> path,
/// so it rides the same per-tick evaluation, edge/level latch, journal, and undo a rule already has. This is a
/// second authoring surface over one evaluation engine.
/// </summary>
/// <remarks>
/// <para><see cref="Effects"/> carries the same <see cref="ActionEffect"/> vocabulary a <see cref="WorldRule"/>
/// admits at world scope — a state write (<see cref="ActionEffect.SetState"/>/<see cref="ActionEffect.AddState"/>)
/// and the spawn/despawn-carrier effect (<see cref="ActionEffect.UpsertPlacement"/>/<see cref="ActionEffect.RemovePlacement"/>).
/// An effect names its target row/cell literally: the gate can address "whichever carrier is most hot" via
/// <c>$argmax:</c>, but no effect can yet write to "whichever cell the gate resolved" — author the interacting
/// carriers' own cells directly.</para>
/// <para>Same-tick ordering is declaration order. Interactions evaluate as a whole array, in document order, after
/// every ordinary rule (see <c>WorldServer.EvaluateWorldRules</c>), and the array is snapshotted before iterating so
/// installing effects can never make an interaction skip its own siblings mid-tick.</para>
/// <para>Cascades are not authored — they compose. Nothing about <see cref="WorldInteraction"/> knows about a
/// second interaction; a cascade falls out of one interaction's effect changing the properties another's gate
/// reads, on the same deterministic, replayable per-tick evaluation order every other world-rule chain rides.</para>
/// </remarks>
/// <param name="Name">The interaction's stable name — unique within the section (a separate namespace from
/// <see cref="WorldRule.Name"/>: an interaction desugars into its own synthesized rule rather than sharing the
/// authored rule list, so the two may coincide without colliding). A <see cref="WorldCellName"/>, never
/// <c>$</c>-prefixed — that prefix marks what the engine mints, and nothing mints an interaction.</param>
/// <param name="Left">A property name, validated against the declared <c>properties</c> registry — the carrier a
/// candidate co-occurrence is searched from (the <c>$argmax:</c> resolution — "the carrier most strongly tagged
/// Left").</param>
/// <param name="Right">For <see cref="WorldInteractionCoOccurrence.Distance"/>, a second property name, validated the
/// same way as <see cref="Left"/>. For <see cref="WorldInteractionCoOccurrence.Region"/>, a placement id carrying a
/// region facet — validated against the declared <c>placements</c> section instead, never against the property
/// registry.</param>
/// <param name="CoOccurrence">How the two operands' coming-together is detected.</param>
/// <param name="Range">The distance threshold, for <see cref="WorldInteractionCoOccurrence.Distance"/> alone —
/// ignored (and unchecked) under <see cref="WorldInteractionCoOccurrence.Region"/>.</param>
/// <param name="Effects">The effects applied in order when the interaction fires — see this type's remarks.</param>
/// <param name="Mode">Whether the interaction fires every tick the co-occurrence holds
/// (<see cref="ActionTriggerMode.Level"/>) or once per crossing (<see cref="ActionTriggerMode.Edge"/>, the default —
/// an interaction that transforms/spawns/despawns almost always wants Edge, for the same reason a rule that writes a
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
/// Optional (like <c>rules</c>/<c>groups</c>): a document declaring none carries a <see langword="null"/> section
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

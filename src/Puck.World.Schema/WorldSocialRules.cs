using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Exactly one way to name an individual: a live body reference resolved to its mobility incarnation,
/// or a literal stable identity that remains addressable while absent.</summary>
/// <param name="Body">The ordinary world-rule body-reference grammar, such as body:0, each, or cell:row:key.</param>
/// <param name="Identity">An original authority/index/generation address, not a current transfer destination.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSocialEntityReference(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Body = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEntityAddress? Identity = null
);

/// <summary>One directed contextual relationship, shared by queries, evidence, and forgetting.</summary>
/// <param name="Observer">The individual whose belief is addressed.</param>
/// <param name="Subject">The individual the belief concerns.</param>
/// <param name="Dimension">The exact dimension name declared in state.social.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSocialRelationship(WorldSocialEntityReference Observer, WorldSocialEntityReference Subject, string Dimension);

/// <summary>One numeric view of a remembered impression.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldSocialFacet>))]
public enum WorldSocialFacet : byte {
    /// <summary>The learned or unknown-baseline value, Fixed.</summary>
    Value,
    /// <summary>The bounded heuristic confidence, Fixed in [0,1].</summary>
    Confidence,
    /// <summary>Unresolved uncertainty, Fixed in [0,1].</summary>
    Uncertainty,
    /// <summary>Accumulated evidence weight, Fixed.</summary>
    Weight,
    /// <summary>Whether an impression is retained, Int 0 or 1.</summary>
    Known,
    /// <summary>Independent events in the current remembered history, Int saturated at Int64.MaxValue.</summary>
    EventCount,
    /// <summary>Engine ticks since the last accepted update, Int saturated at Int64.MaxValue.</summary>
    Age,
}

/// <summary>A read-only belief query. Unknown valid identities return the authored baseline and zero confidence;
/// an inactive body reference is unresolved and cannot inherit a former occupant's belief.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSocialQuery(WorldSocialRelationship Relationship, WorldSocialFacet Facet = WorldSocialFacet.Value);

/// <summary>One world-authorized evidence delivery, not a sensor or a claim of objective truth. A world rule must
/// explicitly gate who can perceive it. Relays retain the original event identity and occurrence tick.</summary>
/// <param name="Relationship">The directed belief to update.</param>
/// <param name="Origin">The stable identity that minted the underlying event, unchanged across relays.</param>
/// <param name="Aspect">An authored event-aspect token of 1..64 characters, such as help.attempt or help.outcome.</param>
/// <param name="Sequence">Non-negative Int event sequence; the minting rule must not reuse IDs for different events.</param>
/// <param name="OccurredAt">Non-negative Int original engine tick, not delivery time. socialClock supplies the current clock.</param>
/// <param name="Value">Fixed evidence value within the relationship dimension's bounds.</param>
/// <param name="Quality">Fixed [0,1] evidence quality; absent means one.</param>
/// <param name="Source">Report source, or null for direct observation. Private intent is never inferred or exposed by this effect.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldSocialObservation(
    WorldSocialRelationship Relationship, WorldSocialEntityReference Origin, string Aspect,
    WorldValueExpression Sequence, WorldValueExpression OccurredAt, WorldValueExpression Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldValueExpression? Quality = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSocialEntityReference? Source = null
);

/// <summary>A social entity reference with either a compiled live-body resolver or a stable identity.</summary>
public readonly record struct CompiledWorldSocialEntity(CompiledBodyRef? Body, WorldEntityAddress? Identity);
/// <summary>A directed social relationship compiled against the dimension catalog.</summary>
public readonly record struct CompiledWorldSocialRelationship(CompiledWorldSocialEntity Observer, CompiledWorldSocialEntity Subject, int Dimension);
/// <summary>A compiled read-only social query and its numeric result kind.</summary>
public readonly record struct CompiledWorldSocialQuery(CompiledWorldSocialRelationship Relationship, WorldSocialFacet Facet, CellKind Kind);
/// <summary>A compiled evidence delivery. Every expression uses the ordinary bounded world-rule evaluator.</summary>
public sealed record CompiledWorldSocialObservation(
    CompiledWorldSocialRelationship Relationship, CompiledWorldSocialEntity Origin, string Aspect,
    CompiledWorldExpressionToken[] Sequence, CompiledWorldExpressionToken[] OccurredAt, CompiledWorldExpressionToken[] Value,
    CompiledWorldExpressionToken[] Quality, CompiledWorldSocialEntity? Source
);

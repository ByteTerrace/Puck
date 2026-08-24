using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>How long a filled <see cref="WorldPlacementContribution"/> slot keeps the piece a federation partner put
/// in it.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldContributionTenure>))]
public enum WorldContributionTenure : byte {
    /// <summary>The piece stands while <see cref="WorldPlacementContribution.Link"/> is reachable. The per-tick
    /// sweep stamps <see cref="WorldPlacementContribution.RetractDeadlineTick"/> at
    /// <see cref="WorldPlacementContribution.GraceSeconds"/> out when the link first reads unreachable, clears the
    /// stamp when it comes back, and retracts once the deadline passes.</summary>
    Presence,

    /// <summary>The piece persists; the sweep never touches the row. <see cref="WorldPlacementContribution.Link"/>
    /// and a positive <see cref="WorldPlacementContribution.GraceSeconds"/> are refused beside this tenure — the
    /// disjoint-field-set rule <c>WorldGenerator</c>'s <c>source</c> arms follow.</summary>
    Endowed,
}
/// <summary>
/// A placement's contribution facet: the row is a slot whose frame (id, pose, scale, and these lifecycle terms) is
/// host-authored and whose creation a federation partner supplies through ordinary mutations. Null is an ordinary
/// placement.
/// </summary>
/// <remarks>
/// <para><see cref="Tenure"/>, <see cref="SlotCreationId"/>, <see cref="Link"/> and <see cref="GraceSeconds"/> are
/// authored. <see cref="Contributor"/> and <see cref="RetractDeadlineTick"/> are server-stamped: the compose boundary
/// reads the contributor off the submitting envelope's acting principal and the per-tick sweep owns the deadline. A
/// submission naming either is refused by name — taking an identity from a payload rather than from the stamp is the
/// laundering the acting-principal rule refuses.</para>
/// <para>An unfilled slot shows its own <see cref="SlotCreationId"/>, so no creationless placement has to be
/// representable. <see cref="Contributor"/> is the filled discriminator, and the validator pins the pair: unfilled
/// requires <c>placement.prototypeId == slotCreationId</c>, filled requires them to differ. Retraction re-points
/// <c>prototypeId</c> back and clears the stamp through one <c>WorldMutation.UpsertPlacement</c>.</para>
/// </remarks>
/// <param name="Tenure">The authored lifecycle.</param>
/// <param name="SlotCreationId">The host-owned <see cref="WorldPrototype.Id"/> the slot shows while unfilled and
/// returns to on retraction. Must resolve to a declared creation row.</param>
/// <param name="Link">The authored <c>adjacencies</c> row name the presence check watches — the same key the
/// <c>$link:&lt;adjacencyName&gt;</c> reserved rule channel reads. Required for
/// <see cref="WorldContributionTenure.Presence"/>, refused beside <see cref="WorldContributionTenure.Endowed"/>.</param>
/// <param name="GraceSeconds">Seconds the watched link may stay unreachable before the piece retracts, compiled to
/// simulation ticks through <see cref="CompiledGrace"/> at each sweep observation and stamped once into
/// <see cref="RetractDeadlineTick"/>. Zero disables the grace (retract on the first observed outage); a rate-0 world
/// compiles to <see cref="CompiledTickDuration.Never"/>, so nothing retracts. Refused beside
/// <see cref="WorldContributionTenure.Endowed"/> unless zero.</param>
/// <param name="Contributor">Server-stamped. The acting principal that filled the slot; null while unfilled. Never
/// accepted from a submitted payload or an authored document.</param>
/// <param name="RetractDeadlineTick">Server-stamped. The simulation tick at or after which the presence sweep
/// retracts this slot; null while the link is reachable.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldPlacementContribution(
    WorldContributionTenure Tenure,
    string SlotCreationId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSafeName? Link = null,
    float GraceSeconds = 0f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldPrincipal? Contributor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? RetractDeadlineTick = null
) {
    /// <summary>Returns <see cref="GraceSeconds"/> compiled against a world's simulation rate. A
    /// <see cref="CompiledTickDuration"/> rather than a tick count so an authored-disabled zero stays distinguishable
    /// from a rate-0 world's absent tick mapping.</summary>
    /// <param name="simulationRateHz">The world's <see cref="WorldDefinition.SimulationRateHz"/>; a negative rate is
    /// read as 0.</param>
    public CompiledTickDuration CompiledGrace(int simulationRateHz) => WorldSimulationTickConversion.CompiledDuration(
        ratePerSecond: ((uint)((simulationRateHz > 0)
        ? simulationRateHz
        : 0
    )),
        seconds: GraceSeconds
    );
    /// <summary>Gets a value indicating whether a partner currently occupies this slot.</summary>
    [JsonIgnore]
    public bool IsFilled => (Contributor is not null);
}
/// <summary>The <see cref="WorldPlacementContribution"/> bounds <see cref="WorldDefinitionValidator"/> reads.</summary>
public static class WorldContributionCapacity {
    /// <summary>The greatest legal <see cref="WorldPlacementContribution.GraceSeconds"/>, in seconds (one day).</summary>
    public const float MaxGraceSeconds = 86_400f;
}

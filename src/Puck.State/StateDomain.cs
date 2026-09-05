using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>
/// A <see cref="StateRow"/>'s declared cell domain — the closed answer to "which keys does this row's storage
/// admit" that <see cref="StateRow.IsKeyed"/>/<see cref="StateRow.IsSlot"/>/<see cref="StateRow.CellCeiling"/>
/// switch over, replacing the five hand-kept discriminators (a null <c>Board</c>, <c>Tokens</c>, <c>Zone</c>,
/// <c>KeysFrom</c>, or <c>History</c> facet) inference used to read the same shape off of. Orthogonal traits — a
/// row's <see cref="StateRow.Advance"/>/<see cref="StateRow.Dynamics"/>/<see cref="StateRow.Cycle"/>/
/// <see cref="StateRow.Draw"/>/<see cref="StateRow.Visibility"/>/<see cref="StateRow.Knowledge"/>/
/// <see cref="StateRow.Phase"/>/<see cref="StateRow.PhaseOf"/>/
/// <see cref="StateRow.Evicts"/>/<see cref="StateRow.Min"/>/<see cref="StateRow.Max"/>/
/// <see cref="StateRow.NonNegative"/> — are unaffected by which case a row declares; every combination the
/// validator already refused (a lattice row carrying <c>advance</c>, a phase row carrying <c>capacity</c>) is refused
/// the identical way with the case substituted for the old field.
/// </summary>
/// <remarks>
/// A row that omits <c>domain</c> entirely infers one from its authored <c>cells</c>/<c>value</c>/<c>capacity</c>
/// exactly as an unauthored row always has (<see cref="StateRow.InferDomain"/>) — a plain row authors nothing
/// new. An explicit <c>domain</c> always wins.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(Slot), "slot")]
[JsonDerivedType(typeof(Keys), "keys")]
[JsonDerivedType(typeof(KeysOf), "keysOf")]
[JsonDerivedType(typeof(CellsOf), "cellsOf")]
[JsonDerivedType(typeof(Ring), "ring")]
[Union]
public abstract record StateDomain {
    private StateDomain() { }

    /// <summary>One cell, keyed <see cref="StateRow.SlotKey"/> — a scalar row. An omitted key addresses it.
    /// Carries no data, so <see cref="StateRow.InferDomain"/> answers with the single <see cref="Instance"/>
    /// rather than allocating one per unauthored row.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record Slot : StateDomain {
        /// <summary>The shared instance every inferred slot row answers with.</summary>
        public static readonly Slot Instance = new();
    }

    /// <summary>Explicit author-keyed cells (<c>StateRow.Capacity</c> and <c>Cells</c> carry the shape); no
    /// omitted-key address exists. The token-domain declaration row (the old dedicated <c>tokens</c> facet) is simply
    /// a row of this case whose <see cref="StateRow.Capacity"/> is the domain's token-count ceiling — other rows
    /// address its keys through <see cref="KeysOf"/> rather than through a second facet of their own. Carries no
    /// data, so <see cref="StateRow.InferDomain"/> answers with the single <see cref="Instance"/> rather than
    /// allocating one per unauthored keyed row.</summary>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record Keys : StateDomain {
        /// <summary>The shared instance every inferred keyed row answers with.</summary>
        public static readonly Keys Instance = new();
    }

    /// <summary>The row's key domain is another row's keys (see <see cref="Keys"/>). <see cref="Ordered"/> true gives
    /// the old <c>zone</c> semantics — cell order is pile order, first/last selection has gameplay meaning — while
    /// false gives the old plain <c>keysFrom</c> attribute-row semantics (a numeric fact per token, no pile order).
    /// The one shape two different authored roles used to spell as two different facets.</summary>
    /// <param name="Row">The row whose keys this row's own keys are drawn from.</param>
    /// <param name="Ordered">Whether cell order carries gameplay meaning (a pile) rather than being incidental.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record KeysOf(CellName Row, bool Ordered = false) : StateDomain;

    /// <summary>One value per cell of a named <c>state.lattices</c> topology — the old <c>board</c> and <c>lattice</c>
    /// facets' shared shape. Which of the two storage strategies a row actually gets (sparse cells overlaying
    /// <see cref="Empty"/>, or the dense physical-field composite) is an implementation choice keyed on the row's own
    /// <see cref="StateRow.Kind"/> — a <see cref="CellKind.Fixed"/> row over a <c>Field</c>-kind topology
    /// compiles into the host's dense field storage, every other combination is the sparse board —
    /// never two authored traits.</summary>
    /// <param name="Topology">The <c>state.lattices</c> topology this row lies over.</param>
    /// <param name="Empty">The value a sparse (board) cell reads before it is ever written; meaningless for the dense
    /// physical-field case, whose unwritten value is the field trait's own initial instead.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CellsOf(string Topology, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Empty = 0) : StateDomain;

    /// <summary>The history ring: the row keeps the last <see cref="Capacity"/> pushed values in slots keyed
    /// <c>0..Capacity-1</c>, oldest overwritten first — the old <c>history</c> facet.</summary>
    /// <param name="Capacity">How many pushes the ring keeps, 1..128.</param>
    /// <param name="Empty">The value read for an age older than the ring holds.</param>
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record Ring(int Capacity, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] long Empty = 0) : StateDomain;
}

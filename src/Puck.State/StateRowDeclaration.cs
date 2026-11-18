namespace Puck.State;

/// <summary>A detached, sealed row declaration. It is not a stored row and cannot be cast to one.
/// Its inherited shape implements the same domain and value rules that the authoritative store uses.</summary>
/// <param name="Name">The stable row name.</param>
/// <param name="Kind">The kind of values the row accepts.</param>
/// <param name="Min">The lower bound in the kind's raw encoding, when declared.</param>
/// <param name="Max">The upper bound in the kind's raw encoding, when declared.</param>
/// <param name="Capacity">The declared cell limit, when present.</param>
/// <param name="Overflow">Whether an out-of-envelope write refuses or saturates.</param>
/// <param name="Evicts">Whether growth at capacity evicts the oldest member.</param>
/// <param name="Advance">The declared continuous accumulation rate.</param>
/// <param name="Draw">The draw program and timing, without its secret or position.</param>
/// <param name="Dynamics">The symbolic dynamics binding.</param>
/// <param name="Cycle">The declared periodic value program.</param>
/// <param name="Domain">The declared cell domain; detached descriptors resolve source shorthand before copying.</param>
/// <param name="ValuesFrom">The topology whose cell ordinals the values name.</param>
/// <param name="Inverse">The token and code rows deriving this board.</param>
/// <param name="HasPhase">Whether the row declares a phase protocol; its live sequence is not declaration data.</param>
/// <param name="Visibility">The row's declared audience policy.</param>
/// <param name="Knowledge">The source and mask bindings of a remembered layer.</param>
/// <param name="PhaseOf">The required phase protocol's row name.</param>
/// <param name="Space">The vector space name.</param>
/// <param name="Enum">The integer values' symbolic domain.</param>
/// <param name="HostOwned">Whether a host facet serves the row.</param>
/// <param name="CellVisibility">Whether the declaration permits per-cell audience policies.</param>
public sealed record StateRowDeclaration(
    CellName Name,
    CellKind Kind,
    long? Min = null,
    long? Max = null,
    int? Capacity = null,
    StateOverflow Overflow = StateOverflow.Refuse,
    bool Evicts = false,
    StateAdvance? Advance = null,
    StateDrawDeclaration? Draw = null,
    StateDynamics? Dynamics = null,
    StateCycle? Cycle = null,
    StateDomain? Domain = null,
    string? ValuesFrom = null,
    StateInverse? Inverse = null,
    bool HasPhase = false,
    StateVisibility? Visibility = null,
    StateKnowledge? Knowledge = null,
    string? PhaseOf = null,
    string? Space = null,
    CellName? Enum = null,
    bool HostOwned = false,
    bool CellVisibility = false
) : StateRowShape(Name, Kind, Min, Max, Capacity, Overflow, Evicts, Advance, Dynamics, Cycle, Domain, ValuesFrom, Inverse, Visibility, Knowledge, PhaseOf, Space, Enum, HostOwned, CellVisibility) {
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StateRow, StateRowDeclaration> Declarations = new();
    /// <inheritdoc/>
    public override bool IsDraw => Draw is not null;
    /// <inheritdoc/>
    public override StateDomain EffectiveDomain => Domain ?? StateDomain.Slot.Instance;
    /// <summary>Copies declaration data, resolving inferred shape once without retaining the stored row.</summary>
    /// <param name="row">The source row at a trusted declaration boundary.</param>
    /// <returns>A declaration carrying no source row, cells or storage state.</returns>
    public static StateRowDeclaration From(StateRow row) {
        ArgumentNullException.ThrowIfNull(row);
        return Declarations.GetValue(row, static source => Create(source));
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<StateRow>, IReadOnlyList<StateRowDeclaration>> RowLists = new();
    /// <summary>Projects an immutable expanded row list in catalog order.</summary>
    public static IReadOnlyList<StateRowDeclaration> FromRows(IReadOnlyList<StateRow> rows) =>
        RowLists.GetValue(rows, static source => Array.AsReadOnly(source.Select(From).ToArray()));

    private static StateRowDeclaration Create(StateRow row) {
        return new(
            Name: row.Name,
            Kind: row.Kind,
            Min: row.Min,
            Max: row.Max,
            Capacity: row.Capacity,
            Overflow: row.Overflow,
            Evicts: row.Evicts,
            Advance: row.Advance,
            Draw: row.Draw is { } draw ? new StateDrawDeclaration(draw.Source, draw.Generator, draw.Timing, draw.Skip, draw.PrivateStream) : null,
            Dynamics: row.Dynamics,
            Cycle: row.Cycle,
            Domain: row.EffectiveDomain,
            ValuesFrom: row.ValuesFrom,
            Inverse: row.Inverse,
            HasPhase: row.Phase is not null,
            Visibility: row.Visibility,
            Knowledge: row.Knowledge,
            PhaseOf: row.PhaseOf,
            Space: row.Space,
            Enum: row.Enum,
            HostOwned: row.HostOwned,
            CellVisibility: row.CellVisibility
        ) { Generated = row.Generated };
    }
}

/// <summary>A draw site's declared program and timing, without its authority-provisioned secret or stream position.</summary>
/// <param name="Source">The named source, or null for an inline generator.</param>
/// <param name="Generator">The inline generator declaration, when present.</param>
/// <param name="Timing">When the site may draw.</param>
/// <param name="Skip">The authored seek.</param>
/// <param name="PrivateStream">Whether the site declares keyed sampling.</param>
public sealed record StateDrawDeclaration(CellName? Source, StateGenerator? Generator, DrawTiming Timing, long Skip, bool PrivateStream = false);

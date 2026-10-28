using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>Finite authoritative input families and their allowed compositions.</summary>
/// <param name="Global">Actions eligible in every tuple.</param>
/// <param name="Families">Independent selectors, ordered canonically by name at compilation.</param>
/// <param name="Combinations">The complete allowed tuples, keyed by family; absence admits the Cartesian product.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputModes(
    IReadOnlyList<string> Global,
    IReadOnlyList<WorldInputModeFamily> Families,
    IReadOnlyList<IReadOnlyDictionary<string, string>>? Combinations = null
);
/// <summary>One selected state and directed continuation policy.</summary>
/// <param name="Name">Family name.</param>
/// <param name="Selector">Authoritative source of the selection.</param>
/// <param name="Default">Selection for an owner without the selector's subject.</param>
/// <param name="States">Finite named states.</param>
/// <param name="SuspendedBy">Selections which suppress this family's actions.</param>
/// <param name="Transitions">Directed keep sets; omission releases every action.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputModeFamily(
    string Name,
    WorldInputModeSelector Selector,
    string Default,
    IReadOnlyList<WorldInputModeState> States,
    IReadOnlyList<WorldInputModeSuppression>? SuspendedBy = null,
    IReadOnlyList<WorldInputModeTransition>? Transitions = null
);
/// <summary>An authoritative selector; never a binding page or a presentation predicate.</summary>
/// <param name="Kind">The state owner.</param>
/// <param name="Name">Body register or seat state family, when required by the kind.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputModeSelector(WorldInputModeSelectorKind Kind, string? Name = null);
/// <summary>Sources whose finite envelopes can be proved when a document is admitted.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldInputModeSelectorKind>))]
public enum WorldInputModeSelectorKind : byte {
    /// <summary>The controlled body's selected kit.</summary>
    ActiveKit,
    /// <summary>The owner's authoritative engagement kind.</summary>
    Engagement,
    /// <summary>A finite or exhaustively partitioned body register.</summary>
    BodyState,
    /// <summary>A finite authoritative seat/session state family.</summary>
    SeatState,
}
/// <summary>One state admits either discrete selector values or disjoint fixed-point ranges.</summary>
/// <param name="Name">Stable state name.</param>
/// <param name="Channels">Eligible logical action names.</param>
/// <param name="Values">Discrete selector values.</param>
/// <param name="Ranges">Inclusive Q48.16 ranges; adjacent endpoints differ by one raw unit.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputModeState(
    string Name,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string>? Values = null,
    IReadOnlyList<WorldInputModeRange>? Ranges = null
);
/// <summary>An inclusive, exactly quantized selector interval.</summary>
/// <param name="Minimum">Lower endpoint in the register's units.</param>
/// <param name="Maximum">Upper endpoint in the register's units.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputModeRange(decimal Minimum, decimal Maximum);
/// <summary>Suppresses channels by another family's selection, independently of its suppression.</summary>
/// <param name="Family">Other family.</param>
/// <param name="States">Selections which suppress this family's channels.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputModeSuppression(string Family, IReadOnlyList<string> States);
/// <summary>Directed continuity, intersected across every changed family.</summary>
/// <param name="From">Old state.</param>
/// <param name="To">New state.</param>
/// <param name="Keep">Same-meaning held actions allowed to continue.</param>
/// <param name="ConditionalKeep">Additional continuing actions when the old and new tuples match authored selections.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputModeTransition(string From, string To, IReadOnlyList<string> Keep,
    IReadOnlyList<WorldInputConditionalKeep>? ConditionalKeep = null);
/// <summary>Adds continuity for named actions under finite source and destination tuple conditions.</summary>
/// <param name="Keep">Additional same-meaning actions.</param>
/// <param name="From">Required selections in the old tuple; omitted families are unconstrained.</param>
/// <param name="To">Required selections in the new tuple; omitted families are unconstrained.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldInputConditionalKeep(IReadOnlyList<string> Keep,
    IReadOnlyDictionary<string, string>? From = null, IReadOnlyDictionary<string, string>? To = null);

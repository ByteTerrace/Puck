using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>One per-participant state slot a host declares beside the document rows — a counter or a timer that
/// belongs to one participant (or one durable identity) rather than to the document. The catalog compiles each into
/// a descriptor on its own lane; what a participant is, and how the slot is reset, is the host's.</summary>
public interface IStateSlot {
    /// <summary>Gets the stable slot name.</summary>
    string Name { get; }
    /// <summary>Gets the slot's semantic role — <see cref="StateParticipantRole.Counter"/> or
    /// <see cref="StateParticipantRole.Timer"/>. The role decides the <see cref="CellKind"/> the lane stores the
    /// slot in; a slot lane never declares a kind of its own.</summary>
    StateParticipantRole Role { get; }
}
/// <summary>The state section as the engine reads it: the document-owned rows, the lattice topologies the
/// lattice-shaped rows lie over, and the per-participant slot lanes a host declares. A document project's own
/// section record implements this so the catalog, the reader, and the rule compiler consume it without knowing the
/// document.</summary>
/// <remarks>Implementations expose immutable snapshots. Replacing declarations or values requires a new section
/// instance; compilation and expanded-row caches are keyed by section identity.</remarks>
public interface IStateSection {
    /// <summary>Gets the declared vector embedding spaces, or <see langword="null"/> for none.</summary>
    IReadOnlyList<StateSpace>? Spaces => null;
    /// <summary>Gets the document-owned cell rows.</summary>
    IReadOnlyList<StateRow>? Rows { get; }
    /// <summary>Gets the lattice topologies the section's lattice-shaped rows lie over.</summary>
    IReadOnlyList<LatticeTopology>? Lattices { get; }
    /// <summary>Gets the per-participant ephemeral slots, or <see langword="null"/> for none.</summary>
    IReadOnlyList<IStateSlot>? ParticipantSlots { get; }
    /// <summary>Gets the per-identity durable slots, or <see langword="null"/> for none.</summary>
    IReadOnlyList<IStateSlot>? IdentitySlots { get; }
    /// <summary>Gets the declared symbolic value domains a row may name, or <see langword="null"/> for none.</summary>
    IReadOnlyList<StateEnum>? Enums => null;
    /// <summary>Gets the declared row families, or <see langword="null"/> for none.</summary>
    IReadOnlyList<StateFamily>? Families => null;
    /// <summary>Gets the declared record types, or <see langword="null"/> for none.</summary>
    IReadOnlyList<StateRecord>? Records => null;
    /// <summary>Gets the declared bounded instance pools, or <see langword="null"/> for none.</summary>
    IReadOnlyList<StatePool>? Pools => null;
    /// <summary>Gets the declared bounded pair pools, or <see langword="null"/> for none.</summary>
    IReadOnlyList<StatePairPool>? PairPools => null;
}
/// <summary>A per-participant slot declaration as the standalone state document spells it.</summary>
/// <param name="Name">The stable slot name.</param>
/// <param name="Role">The slot's semantic role, <see cref="StateParticipantRole.Counter"/> or
/// <see cref="StateParticipantRole.Timer"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateSlot(string Name, StateParticipantRole Role) : IStateSlot;
/// <summary>The standalone state document's <c>state</c> section: rows, lattices, and the two slot lanes. A document
/// project that embeds the state engine declares its own section record over <see cref="IStateSection"/> instead;
/// this record is the shape a host with no document of its own reads and writes. The section owns an immutable
/// snapshot of every list it carries, taken at construction and again on every <c>with</c>, so a caller's array or
/// list can never be mutated in place through the section afterward.</summary>
/// <param name="Spaces">The declared vector embedding spaces.</param>
/// <param name="Rows">The document-owned cell rows.</param>
/// <param name="Lattices">The lattice topologies the lattice-shaped rows lie over.</param>
/// <param name="ParticipantSlots">The per-participant ephemeral slots.</param>
/// <param name="IdentitySlots">The per-identity durable slots.</param>
/// <param name="Enums">The declared symbolic value domains a row may name.</param>
/// <param name="Families">The declared row families.</param>
/// <param name="Records">The typed record declarations pools instantiate.</param>
/// <param name="Pools">The bounded deterministic instance pools.</param>
/// <param name="PairPools">The bounded deterministic pair pools.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateSection(
    IReadOnlyList<StateSpace>? Spaces = null,
    IReadOnlyList<StateRow>? Rows = null,
    IReadOnlyList<LatticeTopology>? Lattices = null,
    IReadOnlyList<StateSlot>? ParticipantSlots = null,
    IReadOnlyList<StateSlot>? IdentitySlots = null,
    IReadOnlyList<StateEnum>? Enums = null,
    IReadOnlyList<StateFamily>? Families = null,
    IReadOnlyList<StateRecord>? Records = null,
    IReadOnlyList<StatePool>? Pools = null,
    IReadOnlyList<StatePairPool>? PairPools = null
) : IStateSection {
    /// <inheritdoc cref="Spaces"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateSpace>? Spaces { get => field; init => field = Freeze(items: value); } = Freeze(items: Spaces);
    /// <inheritdoc cref="Rows"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateRow>? Rows { get => field; init => field = Freeze(items: value); } = Freeze(items: Rows);
    /// <inheritdoc cref="Lattices"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LatticeTopology>? Lattices { get => field; init => field = Freeze(items: value); } = Freeze(items: Lattices);
    /// <inheritdoc cref="ParticipantSlots"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateSlot>? ParticipantSlots { get => field; init => field = Freeze(items: value); } = Freeze(items: ParticipantSlots);
    /// <inheritdoc cref="IdentitySlots"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateSlot>? IdentitySlots { get => field; init => field = Freeze(items: value); } = Freeze(items: IdentitySlots);
    /// <inheritdoc cref="Enums"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateEnum>? Enums { get => field; init => field = Freeze(items: value); } = Freeze(items: Enums);
    /// <inheritdoc cref="Families"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateFamily>? Families { get => field; init => field = Freeze(items: value); } = Freeze(items: Families);
    /// <inheritdoc cref="Records"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateRecord>? Records { get => field; init => field = Freeze(items: value); } = Freeze(items: Records);
    /// <inheritdoc cref="Pools"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StatePool>? Pools { get => field; init => field = Freeze(items: value); } = Freeze(items: Pools);
    /// <inheritdoc cref="PairPools"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StatePairPool>? PairPools { get => field; init => field = Freeze(items: value); } = Freeze(items: PairPools);

    IReadOnlyList<IStateSlot>? IStateSection.ParticipantSlots => ParticipantSlots;
    IReadOnlyList<IStateSlot>? IStateSection.IdentitySlots => IdentitySlots;

    // A list that is already an immutable array is handed straight back, box and all: copying it would produce an
    // equal value, and re-boxing it would allocate on every `with` for each member the caller did not change.
    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? items) => (items switch {
        null => null,
        ImmutableArray<T> => items,
        _ => items.ToImmutableArray(),
    });
}

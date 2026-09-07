using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>One per-participant state slot a host declares beside the document rows — a counter or a timer that
/// belongs to one participant (or one durable identity) rather than to the document. The catalog compiles each into
/// a descriptor on its own lane; what a participant is, and how the slot is reset, is the host's.</summary>
public interface IStateSlot {
    /// <summary>Gets the stable slot name.</summary>
    string Name { get; }
    /// <summary>Gets the value domain the slot is stored in — <see cref="StateValueKind.Counter"/> or
    /// <see cref="StateValueKind.Timer"/>.</summary>
    StateValueKind ValueKind { get; }
}
/// <summary>The state section as the engine reads it: the document-owned rows, the lattice topologies the
/// lattice-shaped rows lie over, and the per-participant slot lanes a host declares. A document project's own
/// section record implements this so the catalog, the reader, and the rule compiler consume it without knowing the
/// document.</summary>
public interface IStateSection {
    /// <summary>Gets the document-owned cell rows.</summary>
    IReadOnlyList<StateRow>? Rows { get; }
    /// <summary>Gets the lattice topologies the section's lattice-shaped rows lie over.</summary>
    IReadOnlyList<LatticeTopology>? Lattices { get; }
    /// <summary>Gets the per-participant ephemeral slots, or <see langword="null"/> for none.</summary>
    IReadOnlyList<IStateSlot>? ParticipantSlots { get; }
    /// <summary>Gets the per-identity durable slots, or <see langword="null"/> for none.</summary>
    IReadOnlyList<IStateSlot>? IdentitySlots { get; }
}
/// <summary>A per-participant slot declaration as the standalone state document spells it.</summary>
/// <param name="Name">The stable slot name.</param>
/// <param name="ValueKind">The slot's storage domain, <see cref="StateValueKind.Counter"/> or
/// <see cref="StateValueKind.Timer"/>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateSlot(string Name, StateValueKind ValueKind) : IStateSlot;
/// <summary>The standalone state document's <c>state</c> section: rows, lattices, and the two slot lanes. A document
/// project that embeds the state engine declares its own section record over <see cref="IStateSection"/> instead;
/// this record is the shape a host with no document of its own reads and writes. The section owns an immutable
/// snapshot of every list it carries, taken at construction and again on every <c>with</c>, so a caller's array or
/// list can never be mutated in place through the section afterward.</summary>
/// <param name="Rows">The document-owned cell rows.</param>
/// <param name="Lattices">The lattice topologies the lattice-shaped rows lie over.</param>
/// <param name="ParticipantSlots">The per-participant ephemeral slots.</param>
/// <param name="IdentitySlots">The per-identity durable slots.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateSection(
    IReadOnlyList<StateRow>? Rows = null,
    IReadOnlyList<LatticeTopology>? Lattices = null,
    IReadOnlyList<StateSlot>? ParticipantSlots = null,
    IReadOnlyList<StateSlot>? IdentitySlots = null
) : IStateSection {
    /// <inheritdoc cref="Rows"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateRow>? Rows { get => field; init => field = Freeze(value); } = Freeze(Rows);
    /// <inheritdoc cref="Lattices"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<LatticeTopology>? Lattices { get => field; init => field = Freeze(value); } = Freeze(Lattices);
    /// <inheritdoc cref="ParticipantSlots"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateSlot>? ParticipantSlots { get => field; init => field = Freeze(value); } = Freeze(ParticipantSlots);
    /// <inheritdoc cref="IdentitySlots"/>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<StateSlot>? IdentitySlots { get => field; init => field = Freeze(value); } = Freeze(IdentitySlots);

    IReadOnlyList<IStateSlot>? IStateSection.ParticipantSlots => ParticipantSlots;
    IReadOnlyList<IStateSlot>? IStateSection.IdentitySlots => IdentitySlots;

    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? items) => ((items is null) ? null : items.ToImmutableArray());
}

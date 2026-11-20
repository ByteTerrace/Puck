using Puck.Abstractions.Documents;
using Puck.Abstractions.Collections;
using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>Declares the typed fields shared by every instance of a state pool.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateRecord(
    [property: DocumentName(WorldNameKind.Record, WorldNameRole.Declares)] CellName Name,
    IReadOnlyList<StatePoolField>? Fields = null
) {
    /// <summary>Gets the immutable field declarations in authored order.</summary>
    public IReadOnlyList<StatePoolField>? Fields { get => field; init => field = OwnedLists.Freeze(items: value); } = OwnedLists.Freeze(items: Fields);

}
/// <summary>One typed field of a <see cref="StateRecord"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePoolField(
    [property: DocumentText("a field local to its record")] CellName Name,
    CellKind Kind = CellKind.Int,
    CellValue Default = default,
    long? Min = null,
    long? Max = null,
    StateOverflow Overflow = StateOverflow.Refuse,
    [property: DocumentText("an enum name is local to the document")] CellName? Enum = null,
    [property: DocumentText("a vector-space identifier, not a module declaration")] CellName? Space = null,
    int? Dimensions = null,
    StateAdvance? Advance = null
) {
    /// <summary>Gets an immutable snapshot of the declared default.</summary>
    public CellValue Default { get => field; init => field = value.DetachedCopy(); } = Default.DetachedCopy();
}
/// <summary>One field override in a statically seeded pool instance.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePoolValue([property: DocumentText("a field local to its record")] CellName Field, CellValue Value, StateCellClock? Clock = null) {
    /// <summary>Gets an immutable snapshot of the field value.</summary>
    public CellValue Value { get => field; init => field = value.DetachedCopy(); } = Value.DetachedCopy();
}
/// <summary>One statically live pool slot and its field overrides.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePoolSeed(int Slot, IReadOnlyList<StatePoolValue>? Values = null, int? LeftSlot = null, int? RightSlot = null) {
    /// <summary>Gets the immutable field overrides.</summary>
    public IReadOnlyList<StatePoolValue>? Values { get => field; init => field = OwnedLists.Freeze(items: value); } = OwnedLists.Freeze(items: Values);

}
/// <summary>A bounded deterministic allocator over instances of one declared record.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePool(
    [property: DocumentName(WorldNameKind.Pool, WorldNameRole.Declares)] CellName Name,
    [property: DocumentName(WorldNameKind.Record, WorldNameRole.Names)] CellName Record,
    int Capacity,
    IReadOnlyList<StatePoolSeed>? Initial = null,
    StatePoolSnapshot? Snapshot = null
) {
    /// <summary>Gets the immutable statically live instances.</summary>
    public IReadOnlyList<StatePoolSeed>? Initial { get => field; init => field = OwnedLists.Freeze(items: value); } = OwnedLists.Freeze(items: Initial);

}
/// <summary>A bounded pool whose stable slots identify pairs of live instances from two endpoint pools.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePairPool(
    [property: DocumentName(WorldNameKind.Pool, WorldNameRole.Declares)] CellName Name,
    [property: DocumentName(WorldNameKind.Record, WorldNameRole.Names)] CellName Record,
    [property: DocumentName(WorldNameKind.Pool, WorldNameRole.Names)] CellName LeftPool,
    [property: DocumentName(WorldNameKind.Pool, WorldNameRole.Names)] CellName RightPool,
    int MaxLive,
    bool Directed = true,
    bool AllowSelf = false,
    IReadOnlyList<StatePoolSeed>? Initial = null,
    StatePoolSnapshot? Snapshot = null
) {
    /// <summary>Gets the immutable statically live pair slots.</summary>
    public IReadOnlyList<StatePoolSeed>? Initial { get => field; init => field = OwnedLists.Freeze(items: value); } = OwnedLists.Freeze(items: Initial);

}
/// <summary>The complete persistent allocator continuation of one pool: every slot generation, including dead
/// slots, plus the currently live instances and their field values.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePoolSnapshot(IReadOnlyList<long> Generations, IReadOnlyList<StatePoolSeed>? Live = null) {
    /// <summary>Gets one generation per pool slot.</summary>
    public IReadOnlyList<long> Generations { get => field; init => field = OwnedLists.Freeze(items: value)!; } = OwnedLists.Freeze(items: Generations)!;
    /// <summary>Gets the immutable live instances in ascending slot order.</summary>
    public IReadOnlyList<StatePoolSeed>? Live { get => field; init => field = OwnedLists.Freeze(items: value); } = OwnedLists.Freeze(items: Live);
}
/// <summary>A catalog-bound, generation-checked reference to one lifetime of one pool slot.</summary>
public readonly record struct StateInstanceHandle {
    private readonly object? m_catalogIdentity;

    internal StateInstanceHandle(int PoolOrdinal, int Slot, long Generation, object? catalogIdentity) {
        this.PoolOrdinal = PoolOrdinal;
        this.Slot = Slot;
        this.Generation = Generation;
        m_catalogIdentity = catalogIdentity;
    }

    /// <summary>Gets the catalog-relative pool ordinal.</summary>
    public int PoolOrdinal { get; }
    /// <summary>Gets the identity slot.</summary>
    public int Slot { get; }
    /// <summary>Gets the slot lifetime generation.</summary>
    public long Generation { get; }

    internal bool BelongsTo(object catalogIdentity) => ReferenceEquals(objA: m_catalogIdentity, objB: catalogIdentity);
}
/// <summary>Compiled storage metadata for one pool field.</summary>
public readonly record struct StatePoolFieldDescriptor(int Ordinal, CellName Name, CellKind Kind, CellValue Default, int RowOrdinal, StatePoolField Declaration);
/// <summary>Compiled storage metadata for one state pool.</summary>
public sealed record StatePoolDescriptor(
    int Ordinal,
    CellName Name,
    CellName Record,
    int Capacity,
    int DomainRowOrdinal,
    int GenerationRowOrdinal,
    IReadOnlyList<StatePoolFieldDescriptor> Fields,
    bool IsPair = false,
    int LeftPoolOrdinal = -1,
    int RightPoolOrdinal = -1,
    int MaxLive = 0,
    bool Directed = true,
    bool AllowSelf = false,
    int LeftRowOrdinal = -1,
    int RightRowOrdinal = -1
);

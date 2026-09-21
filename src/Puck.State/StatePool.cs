using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Puck.State;

/// <summary>Declares the typed fields shared by every instance of a state pool.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StateRecord(CellName Name, IReadOnlyList<StatePoolField>? Fields = null) {
    /// <summary>Gets the immutable field declarations in authored order.</summary>
    public IReadOnlyList<StatePoolField>? Fields { get => field; init => field = Freeze(values: value); } = Freeze(values: Fields);

    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? values) => (values switch {
        null => null,
        ImmutableArray<T> => values,
        _ => values.ToImmutableArray(),
    });
}
/// <summary>One typed field of a <see cref="StateRecord"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePoolField(
    CellName Name,
    CellKind Kind = CellKind.Int,
    CellValue Default = default,
    long? Min = null,
    long? Max = null,
    StateOverflow Overflow = StateOverflow.Refuse,
    CellName? Enum = null,
    CellName? Space = null,
    int? Dimensions = null,
    StateAdvance? Advance = null
) {
    /// <summary>Gets an immutable snapshot of the declared default.</summary>
    public CellValue Default { get => field; init => field = Freeze(value: value); } = Freeze(value: Default);

    private static CellValue Freeze(CellValue value) => ((value.HasValue && (value.Kind == CellKind.Vector))
        ? CellValue.Vector(components: value.AsVector.ToArray())
        : value);
}
/// <summary>One field override in a statically seeded pool instance.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePoolValue(CellName Field, CellValue Value, StateCellClock? Clock = null) {
    /// <summary>Gets an immutable snapshot of the field value.</summary>
    public CellValue Value { get => field; init => field = Freeze(value: value); } = Freeze(value: Value);

    private static CellValue Freeze(CellValue value) => ((value.HasValue && (value.Kind == CellKind.Vector))
        ? CellValue.Vector(components: value.AsVector.ToArray())
        : value);
}
/// <summary>One statically live pool slot and its field overrides.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePoolSeed(int Slot, IReadOnlyList<StatePoolValue>? Values = null) {
    /// <summary>Gets the immutable field overrides.</summary>
    public IReadOnlyList<StatePoolValue>? Values { get => field; init => field = Freeze(values: value); } = Freeze(values: Values);

    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? values) => (values switch {
        null => null,
        ImmutableArray<T> => values,
        _ => values.ToImmutableArray(),
    });
}
/// <summary>A bounded deterministic allocator over instances of one declared record.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePool(CellName Name, CellName Record, int Capacity, IReadOnlyList<StatePoolSeed>? Initial = null, StatePoolSnapshot? Snapshot = null) {
    /// <summary>Gets the immutable statically live instances.</summary>
    public IReadOnlyList<StatePoolSeed>? Initial { get => field; init => field = Freeze(values: value); } = Freeze(values: Initial);

    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? values) => (values switch {
        null => null,
        ImmutableArray<T> => values,
        _ => values.ToImmutableArray(),
    });
}
/// <summary>A bounded pool whose stable slots identify pairs of live instances from two endpoint pools.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePairPool(
    CellName Name,
    CellName Record,
    CellName LeftPool,
    CellName RightPool,
    int MaxLive,
    bool Directed = true,
    bool AllowSelf = false,
    IReadOnlyList<StatePoolSeed>? Initial = null,
    StatePoolSnapshot? Snapshot = null
) {
    /// <summary>Gets the immutable statically live pair slots.</summary>
    public IReadOnlyList<StatePoolSeed>? Initial { get => field; init => field = Freeze(values: value); } = Freeze(values: Initial);

    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? values) => (values switch {
        null => null,
        ImmutableArray<T> => values,
        _ => values.ToImmutableArray(),
    });
}
/// <summary>The complete persistent allocator continuation of one pool: every slot generation, including dead
/// slots, plus the currently live instances and their field values.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record StatePoolSnapshot(IReadOnlyList<long> Generations, IReadOnlyList<StatePoolSeed>? Live = null) {
    /// <summary>Gets one generation per pool slot.</summary>
    public IReadOnlyList<long> Generations { get => field; init => field = FreezeRequired(values: value); } = FreezeRequired(values: Generations);
    /// <summary>Gets the immutable live instances in ascending slot order.</summary>
    public IReadOnlyList<StatePoolSeed>? Live { get => field; init => field = Freeze(values: value); } = Freeze(values: Live);

    private static IReadOnlyList<T> FreezeRequired<T>(IReadOnlyList<T> values) => (values switch {
        ImmutableArray<T> => values,
        _ => values.ToImmutableArray(),
    });
    private static IReadOnlyList<T>? Freeze<T>(IReadOnlyList<T>? values) => (values switch {
        null => null,
        ImmutableArray<T> => values,
        _ => values.ToImmutableArray(),
    });
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
    bool AllowSelf = false
);

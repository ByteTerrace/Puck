namespace Puck.State.Rules;

/// <summary>One weighted term of a vector mix.</summary>
/// <param name="Source">The term's vector operand.</param>
/// <param name="Weight">The term's weight, in [-1000, 1000] and never zero.</param>
public readonly record struct MixTermFact(CompiledVector Source, int Weight);
/// <summary>The shared shape of the four vector effects: one destination cell addressed by ordinal and interned
/// key, and the dimensions its space declares.</summary>
public abstract class VectorEffect : RuleEffect, IStateAddressedEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="dimensions">The space's dimension count.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    protected VectorEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, int dimensions, string describe) : base(describe: describe) {
        Dimensions = dimensions;
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
    }

    /// <summary>Gets the space's dimension count.</summary>
    public int Dimensions { get; }
    /// <inheritdoc/>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: KeyFrom
    );
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) =>
        CollectWriteAccess(effect: this, into: into);
}
/// <summary>Copies one vector into a vector cell.</summary>
public sealed class VectorCopyEffect : VectorEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="source">The source vector.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public VectorCopyEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, CompiledVector source, string describe) : base(
        describe: describe,
        dimensions: (source?.Space.Identity.Dimensions ?? 0),
        key: key,
        keyFrom: keyFrom,
        rowOrdinal: rowOrdinal
    ) {
        ArgumentNullException.ThrowIfNull(argument: source);

        Source = source;
    }

    /// <summary>Gets the source vector.</summary>
    public CompiledVector Source { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        base.CollectReads(into: into);
        Source.CollectReads(into: into);
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (512L + Dimensions);
}
/// <summary>Writes the weighted mix of up to <see cref="StateCapacity.MaxMixTerms"/> vectors into a vector cell.</summary>
public sealed class VectorMixEffect : VectorEffect {
    private readonly MixTermFact[] m_terms;

    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="terms">The weighted terms.</param>
    /// <param name="dimensions">The space's dimension count.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public VectorMixEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, MixTermFact[] terms, int dimensions, string describe) : base(
        describe: describe,
        dimensions: dimensions,
        key: key,
        keyFrom: keyFrom,
        rowOrdinal: rowOrdinal
    ) {
        ArgumentNullException.ThrowIfNull(argument: terms);

        m_terms = terms;
    }

    /// <summary>Gets the weighted terms, in authored order.</summary>
    public IReadOnlyList<MixTermFact> Terms => m_terms;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        base.CollectReads(into: into);
        foreach (var term in m_terms) {
            term.Source.CollectReads(into: into);
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (512L + (((long)m_terms.Length) * Dimensions));
}
/// <summary>Writes the mean of a keyed vector table's admitted rows into a vector cell.</summary>
public sealed class VectorMeanEffect : VectorEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="fromRowOrdinal">The source table's catalog ordinal.</param>
    /// <param name="whereRowOrdinal">The keyed Bool filter row's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="dimensions">The space's dimension count.</param>
    /// <param name="fromCapacity">The source table's capacity, for pricing the scan.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public VectorMeanEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, int fromRowOrdinal, int whereRowOrdinal, int dimensions, int fromCapacity, string describe) : base(
        describe: describe,
        dimensions: dimensions,
        key: key,
        keyFrom: keyFrom,
        rowOrdinal: rowOrdinal
    ) {
        FromCapacity = fromCapacity;
        FromRowOrdinal = fromRowOrdinal;
        WhereRowOrdinal = whereRowOrdinal;
    }

    /// <summary>Gets the source table's capacity.</summary>
    public int FromCapacity { get; }
    /// <summary>Gets the source table's catalog ordinal.</summary>
    public int FromRowOrdinal { get; }
    /// <summary>Gets the keyed Bool filter row's catalog ordinal, or <c>-1</c>.</summary>
    public int WhereRowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        base.CollectReads(into: into);
        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: FromRowOrdinal
        ));
        if (WhereRowOrdinal >= 0) {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: WhereRowOrdinal
            ));
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (512L + (((long)FromCapacity) * Dimensions));
}
/// <summary>Ranks a keyed vector table against a query and writes the nearest keys or scores.</summary>
public sealed class VectorNearestEffect : VectorEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="intoKind">The destination row's cell kind.</param>
    /// <param name="isIntoSlot">Whether the destination is a slot row.</param>
    /// <param name="fromRowOrdinal">The source table's catalog ordinal.</param>
    /// <param name="whereRowOrdinal">The keyed Bool filter row's catalog ordinal, or <c>-1</c>.</param>
    /// <param name="dimensions">The space's dimension count.</param>
    /// <param name="fromCapacity">The source table's capacity, for pricing the scan.</param>
    /// <param name="query">The query vector.</param>
    /// <param name="k">How many results the ranking writes.</param>
    /// <param name="threshold">The inclusive score cutoff, or <see langword="null"/>.</param>
    /// <param name="farthest">Whether the ranking takes the farthest rather than the nearest.</param>
    /// <param name="excludeKey">The key excluded from the ranking, or the invalid default.</param>
    /// <param name="excludeKeyFrom">The live indirection for the excluded key, or <see langword="null"/>.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public VectorNearestEffect(int rowOrdinal, CellKind intoKind, bool isIntoSlot, int fromRowOrdinal, int whereRowOrdinal, int dimensions, int fromCapacity, CompiledVector query, int k, long? threshold, bool farthest, CellKey excludeKey, CompiledCellRef? excludeKeyFrom, string describe) : base(
        describe: describe,
        dimensions: dimensions,
        key: default,
        keyFrom: null,
        rowOrdinal: rowOrdinal
    ) {
        ArgumentNullException.ThrowIfNull(argument: query);

        ExcludeKey = excludeKey;
        ExcludeKeyFrom = excludeKeyFrom;
        Farthest = farthest;
        FromCapacity = fromCapacity;
        FromRowOrdinal = fromRowOrdinal;
        IntoKind = intoKind;
        IsIntoSlot = isIntoSlot;
        K = k;
        Query = query;
        Threshold = threshold;
        WhereRowOrdinal = whereRowOrdinal;
    }

    /// <summary>Gets the key excluded from the ranking, or the invalid default.</summary>
    public CellKey ExcludeKey { get; }
    /// <summary>Gets the live indirection for the excluded key, or <see langword="null"/>.</summary>
    public CompiledCellRef? ExcludeKeyFrom { get; }
    /// <summary>Gets a value indicating whether the ranking takes the farthest rather than the nearest.</summary>
    public bool Farthest { get; }
    /// <summary>Gets the source table's capacity.</summary>
    public int FromCapacity { get; }
    /// <summary>Gets the source table's catalog ordinal.</summary>
    public int FromRowOrdinal { get; }
    /// <summary>Gets the destination row's cell kind.</summary>
    public CellKind IntoKind { get; }
    /// <summary>Gets a value indicating whether the destination is a slot row.</summary>
    public bool IsIntoSlot { get; }
    /// <summary>Gets how many results the ranking writes.</summary>
    public int K { get; }
    /// <summary>Gets the query vector.</summary>
    public CompiledVector Query { get; }
    /// <summary>Gets the inclusive score cutoff, or <see langword="null"/>.</summary>
    public long? Threshold { get; }
    /// <summary>Gets the keyed Bool filter row's catalog ordinal, or <c>-1</c>.</summary>
    public int WhereRowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        base.CollectReads(into: into);
        Query.CollectReads(into: into);
        CompiledCellRef.CollectReference(
            into: into,
            reference: ExcludeKeyFrom
        );
        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: FromRowOrdinal
        ));
        if (WhereRowOrdinal >= 0) {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: WhereRowOrdinal
            ));
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (512L + (((long)FromCapacity) * (Dimensions + 1)));
}
/// <summary>Stores a vector into a keyed table unless a near duplicate is already there.</summary>
public sealed class VectorRememberEffect : VectorEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination table's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="source">The stored vector.</param>
    /// <param name="capacity">The destination table's capacity, for pricing the scan.</param>
    /// <param name="unlessWithinQ16">The cosine ceiling in Q48.16, in [0, 1], above which the store is skipped.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public VectorRememberEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, CompiledVector source, int capacity, long unlessWithinQ16, string describe) : base(
        describe: describe,
        dimensions: (source?.Space.Identity.Dimensions ?? 0),
        key: key,
        keyFrom: keyFrom,
        rowOrdinal: rowOrdinal
    ) {
        ArgumentNullException.ThrowIfNull(argument: source);

        Capacity = capacity;
        Source = source;
        UnlessWithinQ16 = unlessWithinQ16;
    }

    /// <summary>Gets the destination table's capacity.</summary>
    public int Capacity { get; }
    /// <summary>Gets the stored vector.</summary>
    public CompiledVector Source { get; }
    /// <summary>Gets the cosine ceiling in Q48.16, in [0, 1].</summary>
    public long UnlessWithinQ16 { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        base.CollectReads(into: into);
        Source.CollectReads(into: into);
        into.Add(item: new CellAccess(
            Key: default,
            RowOrdinal: RowOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (512L + (((long)Capacity) * (Dimensions + 1)));
}

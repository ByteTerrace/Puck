namespace Puck.State;

/// <summary>One term in a vector mix effect.</summary>
/// <param name="Source">The vector operand (cell or literal).</param>
/// <param name="Weight">The integer weight in [-1000, 1000].</param>
public readonly record struct MixTermFact(CompiledVectorOperand Source, long Weight);

/// <summary>A state effect copying a vector cell or literal into a target vector cell.</summary>
public sealed class VectorCopyEffect : EffectFact, IStateAddressedEffect {
    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public StateHandle Handle { get; }
    /// <inheritdoc/>
    public CellName CellKey { get; }
    /// <summary>Gets the destination row ordinal.</summary>
    public int RowOrdinal { get; }
    /// <summary>Gets the source vector operand.</summary>
    public CompiledVectorOperand Source { get; }

    /// <summary>Initializes a vector copy effect.</summary>
    public VectorCopyEffect(
        string row,
        string key,
        CompiledCellRef? keyFrom,
        StateHandle handle,
        CellName cellKey,
        int rowOrdinal,
        CompiledVectorOperand source,
        string describe
    ) : base(describe: describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        Handle = handle;
        CellKey = cellKey;
        RowOrdinal = rowOrdinal;
        Source = source;
    }

    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) =>
        Source.CollectReads(into: into);

    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) {
        into.Add(item: new RuleAccess(
            Row: Row,
            Key: (KeyFrom is null) ? Key : null
        ));
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) =>
        2L + Source.Space.Dimensions;
}

/// <summary>A state effect computing a weighted mix of vector operands into a target vector cell.</summary>
public sealed class VectorMixEffect : EffectFact, IStateAddressedEffect {
    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public StateHandle Handle { get; }
    /// <inheritdoc/>
    public CellName CellKey { get; }
    /// <summary>Gets the destination row ordinal.</summary>
    public int RowOrdinal { get; }
    /// <summary>Gets the mix terms.</summary>
    public IReadOnlyList<MixTermFact> Terms { get; }
    /// <summary>Gets the vector dimensions.</summary>
    public int Dimensions { get; }

    /// <summary>Initializes a vector mix effect.</summary>
    public VectorMixEffect(
        string row,
        string key,
        CompiledCellRef? keyFrom,
        StateHandle handle,
        CellName cellKey,
        int rowOrdinal,
        IReadOnlyList<MixTermFact> terms,
        int dimensions,
        string describe
    ) : base(describe: describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        Handle = handle;
        CellKey = cellKey;
        RowOrdinal = rowOrdinal;
        Terms = terms;
        Dimensions = dimensions;
    }

    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        foreach (var term in Terms) {
            term.Source.CollectReads(into: into);
        }
    }

    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) {
        into.Add(item: new RuleAccess(
            Row: Row,
            Key: (KeyFrom is null) ? Key : null
        ));
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) =>
        2L + (Terms.Count * Dimensions);
}

/// <summary>A state effect computing the mean of vector cells from a table into a target vector cell.</summary>
public sealed class VectorMeanEffect : EffectFact, IStateAddressedEffect {
    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public StateHandle Handle { get; }
    /// <inheritdoc/>
    public CellName CellKey { get; }
    /// <summary>Gets the destination row ordinal.</summary>
    public int RowOrdinal { get; }
    /// <summary>Gets the source table row ordinal.</summary>
    public int FromRowOrdinal { get; }
    /// <summary>Gets the source table row name.</summary>
    public string FromRowName { get; }
    /// <summary>Gets the source table row handle.</summary>
    public StateHandle FromHandle { get; }
    /// <summary>Gets the vector dimensions.</summary>
    public int Dimensions { get; }
    /// <summary>Gets the source table capacity.</summary>
    public int FromCapacity { get; }
    /// <summary>Gets the optional where filter row ordinal.</summary>
    public int? WhereRowOrdinal { get; }
    /// <summary>Gets the optional where filter row name.</summary>
    public string? WhereRowName { get; }
    /// <summary>Gets the optional where filter row handle.</summary>
    public StateHandle WhereHandle { get; }

    /// <summary>Initializes a vector mean effect.</summary>
    public VectorMeanEffect(
        string row,
        string key,
        CompiledCellRef? keyFrom,
        StateHandle handle,
        CellName cellKey,
        int rowOrdinal,
        int fromRowOrdinal,
        string fromRowName,
        StateHandle fromHandle,
        int dimensions,
        int fromCapacity,
        int? whereRowOrdinal,
        string? whereRowName,
        StateHandle whereHandle,
        string describe
    ) : base(describe: describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        Handle = handle;
        CellKey = cellKey;
        RowOrdinal = rowOrdinal;
        FromRowOrdinal = fromRowOrdinal;
        FromRowName = fromRowName;
        FromHandle = fromHandle;
        Dimensions = dimensions;
        FromCapacity = fromCapacity;
        WhereRowOrdinal = whereRowOrdinal;
        WhereRowName = whereRowName;
        WhereHandle = whereHandle;
    }

    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        into.Add(item: new RuleAccess(Row: FromRowName, Key: null));
        if (WhereRowName is not null) {
            into.Add(item: new RuleAccess(Row: WhereRowName, Key: null));
        }
    }

    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) {
        into.Add(item: new RuleAccess(
            Row: Row,
            Key: (KeyFrom is null) ? Key : null
        ));
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) =>
        2L + (FromCapacity * Dimensions);
}

/// <summary>A state effect computing top-K nearest matches from a vector table into a target table or text slot.</summary>
public sealed class VectorNearestEffect : EffectFact {
    /// <summary>Gets the destination row ordinal.</summary>
    public int IntoRowOrdinal { get; }
    /// <summary>Gets the destination row name.</summary>
    public string IntoRowName { get; }
    /// <summary>Gets the destination row handle.</summary>
    public StateHandle IntoHandle { get; }
    /// <summary>Gets the destination row cell kind (Int, Fixed, or Text).</summary>
    public CellKind IntoKind { get; }
    /// <summary>Gets whether the destination is a slot (Text) or a keyed table (Int, Fixed).</summary>
    public bool IsIntoSlot { get; }
    /// <summary>Gets the source vector table row ordinal.</summary>
    public int FromRowOrdinal { get; }
    /// <summary>Gets the source vector table row name.</summary>
    public string FromRowName { get; }
    /// <summary>Gets the source vector table row handle.</summary>
    public StateHandle FromHandle { get; }
    /// <summary>Gets the vector dimensions.</summary>
    public int Dimensions { get; }
    /// <summary>Gets the source table capacity.</summary>
    public int FromCapacity { get; }
    /// <summary>Gets the query vector operand.</summary>
    public CompiledVectorOperand Query { get; }
    /// <summary>Gets the match count K.</summary>
    public int K { get; }
    /// <summary>Gets the optional threshold.</summary>
    public long? Threshold { get; }
    /// <summary>Gets whether to select farthest matches instead of nearest.</summary>
    public bool Farthest { get; }
    /// <summary>Gets the optional where filter row ordinal.</summary>
    public int? WhereRowOrdinal { get; }
    /// <summary>Gets the optional where filter row name.</summary>
    public string? WhereRowName { get; }
    /// <summary>Gets the optional where filter row handle.</summary>
    public StateHandle WhereHandle { get; }
    /// <summary>Gets the optional exclude key string.</summary>
    public string? ExcludeKey { get; }
    /// <summary>Gets the optional exclude key indirection.</summary>
    public CompiledCellRef? ExcludeKeyFrom { get; }
    /// <summary>Gets the pre-parsed exclude cell key or default.</summary>
    public CellName ExcludeCellKey { get; }

    /// <summary>Initializes a vector nearest effect.</summary>
    public VectorNearestEffect(
        int intoRowOrdinal,
        string intoRowName,
        StateHandle intoHandle,
        CellKind intoKind,
        bool isIntoSlot,
        int fromRowOrdinal,
        string fromRowName,
        StateHandle fromHandle,
        int dimensions,
        int fromCapacity,
        CompiledVectorOperand query,
        int k,
        long? threshold,
        bool farthest,
        int? whereRowOrdinal,
        string? whereRowName,
        StateHandle whereHandle,
        string? excludeKey,
        CompiledCellRef? excludeKeyFrom,
        CellName excludeCellKey,
        string describe
    ) : base(describe: describe) {
        IntoRowOrdinal = intoRowOrdinal;
        IntoRowName = intoRowName;
        IntoHandle = intoHandle;
        IntoKind = intoKind;
        IsIntoSlot = isIntoSlot;
        FromRowOrdinal = fromRowOrdinal;
        FromRowName = fromRowName;
        FromHandle = fromHandle;
        Dimensions = dimensions;
        FromCapacity = fromCapacity;
        Query = query;
        K = k;
        Threshold = threshold;
        Farthest = farthest;
        WhereRowOrdinal = whereRowOrdinal;
        WhereRowName = whereRowName;
        WhereHandle = whereHandle;
        ExcludeKey = excludeKey;
        ExcludeKeyFrom = excludeKeyFrom;
        ExcludeCellKey = excludeCellKey;
    }

    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        Query.CollectReads(into: into);
        into.Add(item: new RuleAccess(Row: FromRowName, Key: null));
        if (WhereRowName is not null) {
            into.Add(item: new RuleAccess(Row: WhereRowName, Key: null));
        }
        RuleAccess.CollectReference(reference: ExcludeKeyFrom, into: into);
    }

    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) =>
        into.Add(item: new RuleAccess(Row: IntoRowName, Key: null));

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) =>
        2L + (FromCapacity * Dimensions) + (K * FromCapacity);
}

/// <summary>A state effect conditionally upserting a vector into a history table unless a near vector exists.</summary>
public sealed class VectorRememberEffect : EffectFact, IStateAddressedEffect {
    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public StateHandle Handle { get; }
    /// <inheritdoc/>
    public CellName CellKey { get; }
    /// <summary>Gets the destination row ordinal.</summary>
    public int RowOrdinal { get; }
    /// <summary>Gets the vector dimensions.</summary>
    public int Dimensions { get; }
    /// <summary>Gets the destination table capacity.</summary>
    public int Capacity { get; }
    /// <summary>Gets the source vector operand.</summary>
    public CompiledVectorOperand Source { get; }
    /// <summary>Gets the unlessWithin threshold in Q48.16.</summary>
    public long UnlessWithinQ16 { get; }

    /// <summary>Initializes a vector remember effect.</summary>
    public VectorRememberEffect(
        string row,
        string key,
        CompiledCellRef? keyFrom,
        StateHandle handle,
        CellName cellKey,
        int rowOrdinal,
        int dimensions,
        int capacity,
        CompiledVectorOperand source,
        long unlessWithinQ16,
        string describe
    ) : base(describe: describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        Handle = handle;
        CellKey = cellKey;
        RowOrdinal = rowOrdinal;
        Dimensions = dimensions;
        Capacity = capacity;
        Source = source;
        UnlessWithinQ16 = unlessWithinQ16;
    }

    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        Source.CollectReads(into: into);
        into.Add(item: new RuleAccess(Row: Row, Key: null));
    }

    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) {
        into.Add(item: new RuleAccess(
            Row: Row,
            Key: (KeyFrom is null) ? Key : null
        ));
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) =>
        2L + (Capacity * Dimensions);
}

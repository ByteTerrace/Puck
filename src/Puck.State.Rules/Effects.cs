namespace Puck.State.Rules;

/// <summary>A state cell write — <c>setState</c>/<c>addState</c>, a literal, a live copy, an expression, or (for a
/// kind=Text row) a text literal.</summary>
public sealed class WriteEffect : RuleEffect, IStateWriteEffect, IValueSourcedEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default when <paramref name="keyFrom"/> applies.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="source">The compiled value source.</param>
    /// <param name="text">The text literal for a kind=Text row, or <see langword="null"/>.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    /// <param name="rowFrom">The live row selection, or <see langword="null"/> when the row is named.</param>
    public WriteEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, StateWriteKind write, in CompiledValueSource source, string? text, string describe, LiveRow? rowFrom = null) : base(describe: describe) {
        Key = key;
        KeyFrom = keyFrom;
        RowFrom = rowFrom;
        RowOrdinal = rowOrdinal;
        Source = source;
        Text = text;
        Write = write;
    }

    /// <inheritdoc/>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public LiveRow? RowFrom { get; }
    /// <inheritdoc/>
    public int RowOrdinal { get; }
    /// <inheritdoc/>
    public CompiledValueSource Source { get; }
    /// <summary>Gets the text literal for a kind=Text row, or <see langword="null"/> for a numeric write.</summary>
    public string? Text { get; }
    /// <inheritdoc/>
    public StateWriteKind Write { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        Source.CollectReads(into: into);
        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyFrom
        );
        RowFrom?.CollectIndexReads(into: into);
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        if (RowFrom is { } live) {
            live.CollectRows(
                into: into,
                isSet: (Write == StateWriteKind.Set)
            );

            return;
        }

        into.Add(item: new CellAccess(
            IsSet: (Write == StateWriteKind.Set),
            Key: ((KeyFrom is null)
            ? Key
            : default),
            RowOrdinal: RowOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (512L + Source.Cost(context: context));
}
/// <summary>Consumes a non-negative integer countdown by the simulation step's engine-tick width.</summary>
public sealed class CountdownEffect : RuleEffect, IStateWriteEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public CountdownEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, string describe) : base(describe: describe) {
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
    }

    /// <inheritdoc/>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets what firing needs: the engine-tick width of the step is read from the tick pair.</summary>
    public override EffectNeeds Needs => EffectNeeds.ReadsTick;
    /// <inheritdoc/>
    public int RowOrdinal { get; }
    /// <summary>Always <see cref="StateWriteKind.Add"/> — a countdown subtracts from the current value.</summary>
    public StateWriteKind Write => StateWriteKind.Add;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: KeyFrom
    );
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            IsSet: false,
            Key: ((KeyFrom is null)
            ? Key
            : default),
            RowOrdinal: RowOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 512L;
}
/// <summary>Fires a generator row into its draw site.</summary>
public sealed class GenerateEffect : RuleEffect, IStateAddressedEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The draw site row's catalog ordinal.</param>
    /// <param name="key">The row's slot cell key.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public GenerateEffect(int rowOrdinal, CellKey key, string describe) : base(describe: describe) {
        Key = key;
        RowOrdinal = rowOrdinal;
    }

    /// <inheritdoc/>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom => null;
    /// <inheritdoc/>
    public int RowOrdinal { get; }

    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            IsSet: true,
            Key: default,
            RowOrdinal: RowOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 4_096L;
}
/// <summary>Removes an addressed state cell.</summary>
public sealed class RemoveStateCellEffect : RuleEffect, IStateAddressedEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public RemoveStateCellEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, string describe) : base(describe: describe) {
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
    }

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
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            IsSet: true,
            Key: ((KeyFrom is null)
            ? Key
            : default),
            RowOrdinal: RowOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 512L;
}
/// <summary>Writes an absolute simulation due tick into an integer state cell.</summary>
public sealed class ScheduleStateEffect : RuleEffect, IStateWriteEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The destination row's catalog ordinal.</param>
    /// <param name="key">The destination cell key, or the invalid default.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/>.</param>
    /// <param name="delayTicks">The authored delay, in simulation ticks, added to the firing tick at runtime.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public ScheduleStateEffect(int rowOrdinal, CellKey key, CompiledCellRef? keyFrom, long delayTicks, string describe) : base(describe: describe) {
        DelayTicks = delayTicks;
        Key = key;
        KeyFrom = keyFrom;
        RowOrdinal = rowOrdinal;
    }

    /// <summary>Gets the authored delay, in simulation ticks, added to the firing tick at runtime.</summary>
    public long DelayTicks { get; }
    /// <inheritdoc/>
    public CellKey Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets what firing needs: the due tick is the firing tick plus the delay.</summary>
    public override EffectNeeds Needs => EffectNeeds.ReadsTick;
    /// <inheritdoc/>
    public int RowOrdinal { get; }
    /// <summary>Always <see cref="StateWriteKind.Set"/> — a schedule overwrites the due tick.</summary>
    public StateWriteKind Write => StateWriteKind.Set;

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => CompiledCellRef.CollectReference(
        into: into,
        reference: KeyFrom
    );
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            IsSet: true,
            Key: ((KeyFrom is null)
            ? Key
            : default),
            RowOrdinal: RowOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => 512L;
}
/// <summary>A savepoint inside the firing's scope: when a step refuses, the savepoint rewinds so earlier siblings
/// survive, <see cref="OnFailure"/> runs in the firing's own scope, and later siblings continue.</summary>
public sealed class TransactionEffect : RuleEffect {
    private readonly IRuleEffect[][] m_arms;

    /// <summary>Initializes the effect.</summary>
    /// <param name="effects">The savepoint's own steps.</param>
    /// <param name="onFailure">The refusal branch, which runs in the firing's scope.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public TransactionEffect(IRuleEffect[] effects, IRuleEffect[] onFailure, string describe) : base(describe: describe) {
        ArgumentNullException.ThrowIfNull(argument: effects);
        ArgumentNullException.ThrowIfNull(argument: onFailure);

        Effects = effects;
        OnFailure = onFailure;
        m_arms = [effects, onFailure];
    }

    /// <inheritdoc/>
    public override IReadOnlyList<IRuleEffect[]> Arms => m_arms;
    /// <summary>Gets the savepoint's own steps.</summary>
    public IRuleEffect[] Effects { get; }
    /// <summary>Gets the refusal branch.</summary>
    public IRuleEffect[] OnFailure { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        foreach (var effect in Effects) {
            effect.CollectReads(into: into);
        }
        foreach (var effect in OnFailure) {
            effect.CollectReads(into: into);
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        foreach (var effect in Effects) {
            effect.CollectWrites(into: into);
        }
        foreach (var effect in OnFailure) {
            effect.CollectWrites(into: into);
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) {
        var main = RuleWorkBudget.EffectsCost(
            context: context,
            effects: Effects
        );
        var failure = RuleWorkBudget.EffectsCost(
            context: context,
            effects: OnFailure
        );

        // A success preflights the main effects and commits them. A refusal preflights them as far as the member
        // that refuses, then preflights and commits the failure branch.
        return (1L + RuleWork.Max(
            left: (2L * main),
            right: (main + (2L * failure))
        ));
    }
}
/// <summary>Pushes one evaluated value into a history row's ring.</summary>
public sealed class PushStateEffect : RuleEffect, IValueSourcedEffect {
    /// <summary>Initializes the effect.</summary>
    /// <param name="rowOrdinal">The history row's catalog ordinal.</param>
    /// <param name="source">The compiled value source.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public PushStateEffect(int rowOrdinal, in CompiledValueSource source, string describe) : base(describe: describe) {
        RowOrdinal = rowOrdinal;
        Source = source;
    }

    /// <summary>Gets the history row's catalog ordinal.</summary>
    public int RowOrdinal { get; }
    /// <inheritdoc/>
    public CompiledValueSource Source { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) => Source.CollectReads(into: into);
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        into.Add(item: new CellAccess(
            IsSet: true,
            Key: default,
            RowOrdinal: RowOrdinal
        ));
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => (1_024L + Source.Cost(context: context));
}
/// <summary>Branches on a compiled gate: fires <see cref="Then"/> or <see cref="Else"/>, never both. Both branches
/// contribute to the read and write sets, since either may run.</summary>
public sealed class IfEffect : RuleEffect {
    private readonly IRuleEffect[][] m_arms;

    /// <summary>Initializes the effect.</summary>
    /// <param name="condition">The compiled gate.</param>
    /// <param name="then">The branch fired when <paramref name="condition"/> holds.</param>
    /// <param name="elseEffects">The branch fired when it does not; empty for none.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public IfEffect(GateToken[] condition, IRuleEffect[] then, IRuleEffect[] elseEffects, string describe) : base(describe: describe) {
        ArgumentNullException.ThrowIfNull(argument: condition);
        ArgumentNullException.ThrowIfNull(argument: elseEffects);
        ArgumentNullException.ThrowIfNull(argument: then);

        Condition = condition;
        Else = elseEffects;
        Then = then;
        m_arms = [then, elseEffects];
    }

    /// <inheritdoc/>
    public override IReadOnlyList<IRuleEffect[]> Arms => m_arms;
    /// <summary>Gets the compiled gate.</summary>
    public GateToken[] Condition { get; }
    /// <summary>Gets the branch fired when <see cref="Condition"/> does not hold; empty for none.</summary>
    public IRuleEffect[] Else { get; }
    /// <summary>Gets the branch fired when <see cref="Condition"/> holds.</summary>
    public IRuleEffect[] Then { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        RuleDataflow.CollectGate(
            gate: Condition,
            into: into
        );
        foreach (var effect in Then) {
            effect.CollectReads(into: into);
        }
        foreach (var effect in Else) {
            effect.CollectReads(into: into);
        }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        foreach (var effect in Then) {
            effect.CollectWrites(into: into);
        }
        foreach (var effect in Else) {
            effect.CollectWrites(into: into);
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => ((1L + RuleWorkBudget.GateCost(
        context: context,
        tokens: Condition
    )) + RuleWork.Max(
        left: RuleWorkBudget.EffectsCost(
            context: context,
            effects: Then
        ),
        right: RuleWorkBudget.EffectsCost(
            context: context,
            effects: Else
        )
    ));
}
/// <summary>An atomic discrete state transform. The authored declaration carries the transform's own parameters;
/// every row it addresses is resolved here to a catalog ordinal.</summary>
public sealed class TransformStateEffect : RuleEffect {
    private readonly int[] m_reads;
    private readonly int[] m_writes;

    /// <summary>Initializes the effect.</summary>
    /// <param name="transform">The authored transform.</param>
    /// <param name="arena">The resolved transform, addressed by ordinal and interned key.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    /// <param name="reads">The catalog ordinals of the rows firing reads.</param>
    /// <param name="writes">The catalog ordinals of the rows firing writes.</param>
    /// <param name="cost">The conservative work units one firing costs.</param>
    /// <param name="keyRef">The live key indirection the transform's one dynamic key resolves through.</param>
    /// <param name="fromRow">The live row a transfer's source resolves through.</param>
    /// <param name="toRow">The live row a transfer's destination resolves through.</param>
    /// <param name="value">The compiled value source a push's value resolves through.</param>
    public TransformStateEffect(StateTransform transform, ArenaTransform arena, string describe, int[] reads, int[] writes, RuleWork cost, CompiledCellRef? keyRef = null, LiveRow? fromRow = null, LiveRow? toRow = null, CompiledValueSource? value = null) : base(describe: describe) {
        ArgumentNullException.ThrowIfNull(argument: arena);
        ArgumentNullException.ThrowIfNull(argument: reads);
        ArgumentNullException.ThrowIfNull(argument: transform);
        ArgumentNullException.ThrowIfNull(argument: writes);

        Arena = arena;
        FromRow = fromRow;
        KeyRef = keyRef;
        Price = cost;
        ToRow = toRow;
        Transform = transform;
        Value = value;
        m_reads = reads;
        m_writes = writes;
    }

    /// <summary>Gets the resolved transform the arena applies.</summary>
    public ArenaTransform Arena { get; }
    /// <summary>Gets the live row a transfer's source resolves through, or <see langword="null"/>.</summary>
    public LiveRow? FromRow { get; }
    /// <summary>Gets the live key indirection the transform's one dynamic key resolves through, or
    /// <see langword="null"/>.</summary>
    public CompiledCellRef? KeyRef { get; }
    /// <summary>Gets the conservative work units one firing costs.</summary>
    public RuleWork Price { get; }
    /// <summary>Gets the live row a transfer's destination resolves through, or <see langword="null"/>.</summary>
    public LiveRow? ToRow { get; }
    /// <summary>Gets the authored transform.</summary>
    public StateTransform Transform { get; }
    /// <summary>Gets the compiled value source a push's value resolves through, or <see langword="null"/>.</summary>
    public CompiledValueSource? Value { get; }

    /// <inheritdoc/>
    public override void CollectReads(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        foreach (var ordinal in m_reads) {
            into.Add(item: new CellAccess(
                Key: default,
                RowOrdinal: ordinal
            ));
        }

        CompiledCellRef.CollectReference(
            into: into,
            reference: KeyRef
        );
        Value?.CollectReads(into: into);
        FromRow?.CollectIndexReads(into: into);
        ToRow?.CollectIndexReads(into: into);
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<CellAccess> into) {
        ArgumentNullException.ThrowIfNull(argument: into);

        // A live end writes whichever entry its index selects: every entry, conservatively.
        if (FromRow is { } fromRow) {
            fromRow.CollectReads(into: into);
        }
        if (ToRow is { } toRow) {
            toRow.CollectReads(into: into);
        }
        foreach (var ordinal in m_writes) {
            into.Add(item: new CellAccess(
                IsSet: true,
                Key: default,
                RowOrdinal: ordinal
            ));
        }
    }
    /// <inheritdoc/>
    public override RuleWork Cost(IRuleCostContext context) => Price;
}

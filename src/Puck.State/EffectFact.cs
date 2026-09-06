namespace Puck.State;

/// <summary>The operation a write asks of one state cell.</summary>
public enum StateWriteKind : byte {
    /// <summary>Replace the cell.</summary>
    Set,
    /// <summary>Add the operand to the cell.</summary>
    Add,
}

/// <summary>One compiled effect of a rule — the base every case type derives from, whether declared here or by a
/// document project's <see cref="EffectFamily"/>. Case types are classes, for the same reason
/// <see cref="OperandFact"/>'s are. <see cref="Describe"/> is set once by the case's own constructor; what firing
/// costs (<see cref="Cost"/>) and which cells it reads and writes (<see cref="CollectReads"/>,
/// <see cref="CollectWrites"/>) are the case's own answers. Firing itself stays with the evaluator that owns the
/// mutation door.</summary>
public abstract class EffectFact {
    /// <summary>Initializes the effect with its read-back spelling.</summary>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    protected EffectFact(string describe) => Describe = describe;

    /// <summary>Gets the authored spelling, for the rules read-back.</summary>
    public string Describe { get; }

    /// <summary>Gets a value indicating whether firing submits a state mutation — an effect that only emits
    /// (a cue, a pose, a save) reads back as emitted rather than as a skipped write.</summary>
    public virtual bool SubmitsMutation => true;

    /// <summary>Gets a value indicating whether the effect must sit in a transaction's final suffix because applying
    /// it rebuilds what earlier steps address.</summary>
    public virtual bool ClosesTransaction => false;

    /// <summary>Returns the conservative work units one firing costs.</summary>
    /// <param name="context">The compile context the effect was resolved against.</param>
    public abstract long Cost(RuleCompileContext context);

    /// <summary>Appends every state cell firing reads: copy sources, expression operands, and the cells its key
    /// indirections resolve through.</summary>
    /// <param name="into">The read set being collected.</param>
    public virtual void CollectReads(List<RuleAccess> into) { }

    /// <summary>Appends every state cell firing writes.</summary>
    /// <param name="into">The write set being collected.</param>
    public virtual void CollectWrites(List<RuleAccess> into) { }

    /// <summary>Gets a value indicating whether firing reads a fact only the document host answers
    /// (<see cref="OperandFact.HostOnly"/>), so a frame cannot fire it faithfully.</summary>
    public virtual bool ReadsHost => false;

    /// <summary>Returns whether a key indirection resolves through the document host.</summary>
    /// <param name="reference">The indirection, or <see langword="null"/>.</param>
    protected static bool ReferenceReadsHost(CompiledCellRef? reference) => (reference is { Custom: { HostOnly: true } });
}

/// <summary>Shared shape for the case types whose firing path addresses a state cell through a (row, key-or-indirection)
/// pair before the kind-specific work runs — so an evaluator can resolve the one destination-key indirection every
/// one of them shares without a type-pattern switch enumerating each case.</summary>
public interface IStateAddressedEffect {
    /// <summary>The destination state row name, or a document row's id for a whole-row upsert/remove.</summary>
    string Row { get; }
    /// <summary>The destination cell key (or an unused constant for a whole-row upsert/remove).</summary>
    string Key { get; }
    /// <summary>The live key indirection (<see cref="RuleFacts.CellKeyPrefix"/>), or <see langword="null"/> for
    /// a literal <see cref="Key"/>.</summary>
    CompiledCellRef? KeyFrom { get; }
}

/// <summary>Widens <see cref="IStateAddressedEffect"/> with the set/add write mode — <see cref="WriteEffect"/>,
/// <see cref="CountdownEffect"/> (always <see cref="StateWriteKind.Add"/>), and <see cref="ScheduleStateEffect"/>
/// (always <see cref="StateWriteKind.Set"/>).</summary>
public interface IStateWriteEffect : IStateAddressedEffect {
    /// <summary>Set or add.</summary>
    StateWriteKind Write { get; }
}

/// <summary>Shared shape for the case types whose numeric value is read live rather than carried as a literal —
/// <see cref="WriteEffect"/> and <see cref="PushStateEffect"/>.</summary>
public interface IValueSourcedEffect {
    /// <summary>The compiled numeric expression, or <see langword="null"/> for another source spelling.</summary>
    CompiledExpressionToken[]? Expression { get; }
    /// <summary>The live copy-source operand, or <see langword="null"/> when the value is a literal or an
    /// expression instead.</summary>
    OperandFact? From { get; }
    /// <summary>The authored constant, pre-converted to the destination row's raw encoding at compile time — read
    /// only when neither <see cref="Expression"/> nor <see cref="From"/> applies.</summary>
    long RawValue { get; }
}

/// <summary>A state cell write — <c>setState</c>/<c>addState</c>, a literal, a live copy, an expression, or (for a
/// kind=text row) a text literal.</summary>
public sealed class WriteEffect : EffectFact, IStateWriteEffect, IValueSourcedEffect {
    /// <param name="row">The destination state row name.</param>
    /// <param name="key">The destination cell key.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="write">Set or add.</param>
    /// <param name="rawValue">The authored literal, pre-converted to the destination row's raw encoding — read only
    /// when neither <paramref name="from"/> nor <paramref name="expression"/> applies.</param>
    /// <param name="from">The live copy-source operand, or <see langword="null"/> for a literal/expression/text write.</param>
    /// <param name="text">The text literal for a kind=text row, or <see langword="null"/> for a numeric write.</param>
    /// <param name="expression">The compiled numeric expression, or <see langword="null"/> for another source spelling.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public WriteEffect(string row, string key, CompiledCellRef? keyFrom, StateWriteKind write, long rawValue, OperandFact? from, string? text, CompiledExpressionToken[]? expression, string describe)
        : base(describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        Write = write;
        RawValue = rawValue;
        From = from;
        Text = text;
        Expression = expression;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <inheritdoc/>
    public StateWriteKind Write { get; }
    /// <inheritdoc/>
    public long RawValue { get; }
    /// <inheritdoc/>
    public OperandFact? From { get; }
    /// <summary>Gets the text literal for a kind=text row, or <see langword="null"/> for a numeric write.</summary>
    public string? Text { get; }
    /// <inheritdoc/>
    public CompiledExpressionToken[]? Expression { get; }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => EffectCosts.Sourced(baseCost: 512L, effect: this, context: context);
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        EffectCosts.CollectSourceReads(effect: this, into: into);
        RuleAccess.CollectReference(reference: KeyFrom, into: into);
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: ((KeyFrom is null) ? Key : null), IsSet: (Write == StateWriteKind.Set)));
    /// <inheritdoc/>
    public override bool ReadsHost => ((From is { HostOnly: true }) || ReferenceReadsHost(reference: KeyFrom) || RuleDataflow.ExpressionReadsHost(tokens: Expression));
}

/// <summary>Consumes a non-negative integer countdown by the simulation step's engine-tick width.</summary>
public sealed class CountdownEffect : EffectFact, IStateWriteEffect {
    /// <param name="row">The destination state row name.</param>
    /// <param name="key">The destination cell key.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public CountdownEffect(string row, string key, CompiledCellRef? keyFrom, string describe) : base(describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Always <see cref="StateWriteKind.Add"/> — a countdown subtracts from the current value.</summary>
    public StateWriteKind Write => StateWriteKind.Add;

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 512L;
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => RuleAccess.CollectReference(reference: KeyFrom, into: into);
    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: ((KeyFrom is null) ? Key : null), IsSet: false));
    /// <inheritdoc/>
    public override bool ReadsHost => ReferenceReadsHost(reference: KeyFrom);
}

/// <summary>Fires a generator row into its draw site.</summary>
public sealed class GenerateEffect : EffectFact, IStateAddressedEffect {
    /// <param name="row">The draw site's state row.</param>
    /// <param name="generator">The generator row name.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public GenerateEffect(string row, string generator, string describe) : base(describe) {
        Row = row;
        Generator = generator;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <summary>Gets the generator row name.</summary>
    public string Generator { get; }
    /// <summary>Always the row's own slot cell — a draw site is a scalar slot by construction.</summary>
    public string Key => StateRow.SlotKey;
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom => null;

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 4_096L;
    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: null, IsSet: true));
}

/// <summary>Removes an addressed state cell.</summary>
public sealed class RemoveStateCellEffect : EffectFact, IStateAddressedEffect {
    /// <param name="row">The destination state row name.</param>
    /// <param name="key">The destination cell key.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public RemoveStateCellEffect(string row, string key, CompiledCellRef? keyFrom, string describe) : base(describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 512L;
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => RuleAccess.CollectReference(reference: KeyFrom, into: into);
    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: ((KeyFrom is null) ? Key : null), IsSet: true));
    /// <inheritdoc/>
    public override bool ReadsHost => ReferenceReadsHost(reference: KeyFrom);
}

/// <summary>Writes an absolute simulation due tick into an integer state cell.</summary>
public sealed class ScheduleStateEffect : EffectFact, IStateWriteEffect {
    /// <param name="row">The destination state row name.</param>
    /// <param name="key">The destination cell key.</param>
    /// <param name="keyFrom">The live key indirection, or <see langword="null"/> for a literal <paramref name="key"/>.</param>
    /// <param name="delayTicks">The authored delay, in simulation ticks, added to the firing tick at runtime.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public ScheduleStateEffect(string row, string key, CompiledCellRef? keyFrom, long delayTicks, string describe) : base(describe) {
        Row = row;
        Key = key;
        KeyFrom = keyFrom;
        DelayTicks = delayTicks;
    }

    /// <inheritdoc/>
    public string Row { get; }
    /// <inheritdoc/>
    public string Key { get; }
    /// <inheritdoc/>
    public CompiledCellRef? KeyFrom { get; }
    /// <summary>Gets the authored delay, in simulation ticks, added to the firing tick at runtime.</summary>
    public long DelayTicks { get; }
    /// <summary>Always <see cref="StateWriteKind.Set"/> — a schedule overwrites the due tick.</summary>
    public StateWriteKind Write => StateWriteKind.Set;

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => 512L;
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => RuleAccess.CollectReference(reference: KeyFrom, into: into);
    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: ((KeyFrom is null) ? Key : null), IsSet: true));
    /// <inheritdoc/>
    public override bool ReadsHost => ReferenceReadsHost(reference: KeyFrom);
}

/// <summary>Applies a preflighted state-cell mutation bundle with an optional failure branch.</summary>
public sealed class TransactionEffect : EffectFact {
    /// <param name="effects">The atomic transaction's main branch.</param>
    /// <param name="onFailure">The transaction's refusal branch.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public TransactionEffect(EffectFact[] effects, EffectFact[] onFailure, string describe) : base(describe) {
        Effects = effects;
        OnFailure = onFailure;
    }

    /// <summary>Gets the atomic transaction's main branch.</summary>
    public EffectFact[] Effects { get; }
    /// <summary>Gets the transaction's refusal branch.</summary>
    public EffectFact[] OnFailure { get; }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) {
        var mainCost = EffectCosts.Sum(effects: Effects, context: context);
        var failureCost = EffectCosts.Sum(effects: OnFailure, context: context);
        // Success preflights and applies main. Refusal may inspect all of main, then preflight and apply failure.
        var success = RuleWorkBudget.SaturatingMultiply(left: 2L, right: mainCost);
        var refusal = RuleWorkBudget.SaturatingAdd(left: mainCost, right: RuleWorkBudget.SaturatingMultiply(left: 2L, right: failureCost));

        return RuleWorkBudget.SaturatingAdd(left: 1L, right: Math.Max(val1: success, val2: refusal));
    }
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        foreach (var effect in Effects) { effect.CollectReads(into: into); }
        foreach (var effect in OnFailure) { effect.CollectReads(into: into); }
    }
    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) {
        foreach (var effect in Effects) { effect.CollectWrites(into: into); }
        foreach (var effect in OnFailure) { effect.CollectWrites(into: into); }
    }
    /// <inheritdoc/>
    public override bool ReadsHost {
        get {
            foreach (var effect in Effects) { if (effect.ReadsHost) { return true; } }
            foreach (var effect in OnFailure) { if (effect.ReadsHost) { return true; } }

            return false;
        }
    }
}

/// <summary>An atomic discrete state transform.</summary>
public sealed class TransformStateEffect : EffectFact {
    /// <param name="transform">The discrete state transform.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    /// <param name="keyRef">The live key indirection a <see cref="StateTransform.ClearEnclosed"/>'s <c>from</c> or a
    /// <see cref="StateTransform.WriteSet"/>'s <c>setKey</c> or a <see cref="StateTransform.Transfer"/>'s <c>key</c>
    /// spelled, or <see langword="null"/> for a literal cell.</param>
    /// <param name="fromRef">The live indirection a <see cref="StateTransform.Transfer"/>'s <c>from</c> spelled — its
    /// integer indexes the transfer's zone table — or <see langword="null"/> for a literal zone.</param>
    /// <param name="toRef">The live indirection a <see cref="StateTransform.Transfer"/>'s <c>to</c> spelled, on the
    /// same terms as <paramref name="fromRef"/>.</param>
    public TransformStateEffect(StateTransform transform, string describe, CompiledCellRef? keyRef = null, CompiledCellRef? fromRef = null, CompiledCellRef? toRef = null) : base(describe) {
        Transform = transform;
        KeyRef = keyRef;
        FromRef = fromRef;
        ToRef = toRef;
    }

    /// <summary>Gets the discrete state transform.</summary>
    public StateTransform Transform { get; }
    /// <summary>Gets the live key indirection the transform's one dynamic key resolves through, or <see langword="null"/>.</summary>
    public CompiledCellRef? KeyRef { get; }
    /// <summary>Gets the live indirection a transfer's source zone index resolves through, or <see langword="null"/>.</summary>
    public CompiledCellRef? FromRef { get; }
    /// <summary>Gets the live indirection a transfer's destination zone index resolves through, or <see langword="null"/>.</summary>
    public CompiledCellRef? ToRef { get; }

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) {
        var storage = 0L;
        foreach (var row in context.Rows) {
            storage += row.CellCeiling;
        }
        var cost = 4096L + storage;
        switch (Transform) {
            case StateTransform.SetRay ray:
                var count = BoardCells(context: context, row: ray.Row);
                cost += (long)count * (count + 2);
                break;
            case StateTransform.Transfer transfer:
                // A live source is priced at the widest zone it can name.
                var sourceCapacity = 0L;
                foreach (var zone in SourceZones(transfer: transfer, fromRef: FromRef)) {
                    sourceCapacity = Math.Max(val1: sourceCapacity, val2: context.RowCapacity(name: zone));
                }
                cost += ((transfer.Selector == ZoneSelector.Slice) ? 2L : (long)transfer.Count) * sourceCapacity;
                break;
            case StateTransform.BoardCombine combine:
                cost += 3L * BoardCells(context: context, row: combine.Row);
                break;
            case StateTransform.Arrange arrange:
                cost += 4L * context.RowCapacity(name: arrange.Row);
                break;
            case StateTransform.SortZone sortZone:
                cost += 2L * context.RowCapacity(name: sortZone.Row) * Math.Max(1, sortZone.By.Count);
                break;
            case StateTransform.SortKeyed sortKeyed:
                cost += 2L * context.RowCapacity(name: sortKeyed.Row);
                break;
            case StateTransform.Shuffle shuffle:
                cost += 2L * context.RowCapacity(name: shuffle.Row);
                break;
            case StateTransform.WriteSet writeSet:
                var writtenCells = BoardCells(context: context, row: writeSet.Row);
                cost += (long)writtenCells * (writtenCells + 1);
                break;
            case StateTransform.Push push:
                cost += 2L * ((context.FindRow(name: push.Row)?.EffectiveDomain as StateDomain.Ring)?.Capacity ?? 1);
                break;
            case StateTransform.ClearEnclosed enclosed:
                var enclosedCells = BoardCells(context: context, row: enclosed.Row);
                var directions = ((context.FindRow(name: enclosed.Row)?.EffectiveDomain is StateDomain.CellsOf enclosedBoardRow) ? (context.FindTopology(name: enclosedBoardRow.Topology)?.DirectionCount ?? 0) : 0);
                cost += (long)enclosedCells * (directions + 2);
                break;
            case StateTransform.Observe observe:
                var cells = BoardCells(context: context, row: observe.Row);
                cost += (long)cells * (cells + 3);
                break;
        }
        return cost;
    }
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) {
        if (Transform is StateTransform.BoardCombine combine) {
            foreach (var source in new[] { combine.Left, combine.Right }) {
                if (source is not null) {
                    into.Add(item: new RuleAccess(Row: source, Key: null, IsSet: false));
                }
            }
        }
        if (Transform is StateTransform.ClearEnclosed enclosed) {
            into.Add(item: new RuleAccess(Row: enclosed.Row, Key: null, IsSet: false));
        }
        if (Transform is StateTransform.WriteSet writeSet) {
            into.Add(item: new RuleAccess(Row: writeSet.Set, Key: null, IsSet: false));
        }
        RuleAccess.CollectReference(reference: KeyRef, into: into);
        RuleAccess.CollectReference(reference: FromRef, into: into);
        RuleAccess.CollectReference(reference: ToRef, into: into);
    }
    public override void CollectWrites(List<RuleAccess> into) {
        foreach (var row in Rows(transform: Transform)) {
            into.Add(item: new RuleAccess(Row: row, Key: null, IsSet: true));
        }
    }
    // The zones a transfer's source can name: the literal source, or every table entry a live index can reach.
    private static IEnumerable<string> SourceZones(StateTransform.Transfer transfer, CompiledCellRef? fromRef) =>
        ((fromRef is null) ? [transfer.From] : (transfer.Zones ?? []).Where(predicate: static zone => zone.Length != 0));
    // The zones a transfer can write: both literal ends, plus every table entry when either end resolves live.
    private IEnumerable<string> TransferZones(StateTransform.Transfer transfer) {
        if (FromRef is null) { yield return transfer.From; }
        if (ToRef is null) { yield return transfer.To; }
        if ((FromRef is not null) || (ToRef is not null)) {
            foreach (var zone in (transfer.Zones ?? [])) {
                if (zone.Length != 0) { yield return zone; }
            }
        }
    }

    private static int BoardCells(RuleCompileContext context, string row) =>
        ((context.FindRow(name: row)?.EffectiveDomain is StateDomain.CellsOf board) && (context.FindTopology(name: board.Topology) is { } topology))
            ? topology.CellCount
            : 0;

    private IEnumerable<string> Rows(StateTransform transform) => transform switch {
        StateTransform.Transfer transfer => TransferZones(transfer: transfer),
        StateTransform.SetRay ray => [ray.Row],
        StateTransform.Shuffle shuffle => [shuffle.Row],
        StateTransform.SortZone zone => [zone.Row],
        StateTransform.SortKeyed keyed => [keyed.Row],
        StateTransform.WriteSet set => [set.Row],
        StateTransform.BoardCombine combine => [combine.Row],
        StateTransform.Arrange arrange => [arrange.Row],
        StateTransform.Push push => [push.Row],
        StateTransform.ClearEnclosed enclosed => [enclosed.Row],
        StateTransform.Observe observe => [observe.Row],
        _ => [],
    };
    /// <inheritdoc/>
    public override bool ReadsHost => (ReferenceReadsHost(reference: KeyRef) || ReferenceReadsHost(reference: FromRef) || ReferenceReadsHost(reference: ToRef));
}

/// <summary>Pushes one evaluated value into a history row's ring.</summary>
public sealed class PushStateEffect : EffectFact, IValueSourcedEffect {
    /// <param name="row">The history row.</param>
    /// <param name="rawValue">The authored literal, pre-converted to the row's raw encoding — read only when
    /// neither <paramref name="from"/> nor <paramref name="expression"/> applies.</param>
    /// <param name="from">The live copy-source operand, or <see langword="null"/> for a literal/expression push.</param>
    /// <param name="expression">The compiled numeric expression, or <see langword="null"/> for another source spelling.</param>
    /// <param name="describe">The authored spelling, for the rules read-back.</param>
    public PushStateEffect(string row, long rawValue, OperandFact? from, CompiledExpressionToken[]? expression, string describe)
        : base(describe) {
        Row = row;
        RawValue = rawValue;
        From = from;
        Expression = expression;
    }

    /// <summary>Gets the history row.</summary>
    public string Row { get; }
    /// <inheritdoc/>
    public long RawValue { get; }
    /// <inheritdoc/>
    public OperandFact? From { get; }
    /// <inheritdoc/>
    public CompiledExpressionToken[]? Expression { get; }

    /// <summary>Reshapes an already-resolved <see cref="WriteEffect"/> into the ring push over it: the value spelling
    /// (literal/copy/expression) carries over unchanged, and the row-addressing fields (Key, KeyFrom, Write, Text)
    /// fall away because a push always targets the ring's own next slot.</summary>
    /// <param name="write">The resolved write over the ring's slot cell.</param>
    /// <param name="describe">The push's own read-back spelling.</param>
    public static PushStateEffect FromWrite(WriteEffect write, string describe) =>
        new(write.Row, write.RawValue, write.From, write.Expression, describe);

    /// <inheritdoc/>
    public override long Cost(RuleCompileContext context) => EffectCosts.Sourced(baseCost: 1_024L, effect: this, context: context);
    /// <inheritdoc/>
    public override void CollectReads(List<RuleAccess> into) => EffectCosts.CollectSourceReads(effect: this, into: into);
    /// <inheritdoc/>
    public override void CollectWrites(List<RuleAccess> into) => into.Add(item: new RuleAccess(Row: Row, Key: null, IsSet: true));
    /// <inheritdoc/>
    public override bool ReadsHost => ((From is { HostOnly: true }) || RuleDataflow.ExpressionReadsHost(tokens: Expression));
}

/// <summary>The shared pricing of a live value source; the saturating arithmetic every cost sheet sums with is
/// <see cref="RuleWorkBudget.SaturatingAdd"/>/<see cref="RuleWorkBudget.SaturatingMultiply"/>.</summary>
public static class EffectCosts {
    /// <summary>Counts operations and conservative state-candidate visits for a compiled expression.</summary>
    /// <param name="tokens">The compiled postfix expression.</param>
    /// <param name="context">The compile context whose capacities bound indirect reads and reductions.</param>
    public static long Expression(CompiledExpressionToken[] tokens, RuleCompileContext context) =>
        RuleWorkBudget.ExpressionCost(tokens: tokens, context: context);
    /// <summary>Sums the cost of a list of effects.</summary>
    /// <param name="effects">The effects.</param>
    /// <param name="context">The compile context.</param>
    public static long Sum(EffectFact[] effects, RuleCompileContext context) => RuleWorkBudget.EffectsCost(effects: effects, context: context);
    /// <summary>Prices a base cost plus the effect's live source, if any.</summary>
    /// <param name="baseCost">The kind's own cost.</param>
    /// <param name="effect">The effect.</param>
    /// <param name="context">The compile context.</param>
    public static long Sourced(long baseCost, IValueSourcedEffect effect, RuleCompileContext context) {
        var cost = baseCost;
        if (effect.From is { } source) {
            cost = RuleWorkBudget.SaturatingAdd(left: cost, right: source.Cost(context: context));
        }
        if (effect.Expression is { } expression) {
            cost = RuleWorkBudget.SaturatingAdd(left: cost, right: Expression(tokens: expression, context: context));
        }
        return cost;
    }
    /// <summary>Appends the cells an effect's live source reads.</summary>
    /// <param name="effect">The effect.</param>
    /// <param name="into">The read set being collected.</param>
    public static void CollectSourceReads(IValueSourcedEffect effect, List<RuleAccess> into) {
        effect.From?.CollectReads(into: into);
        if (effect.Expression is { } expression) {
            foreach (var token in expression) {
                token.Operand?.CollectReads(into: into);
            }
        }
    }
}

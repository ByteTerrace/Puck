namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Resolves an authored transform into the form the arena applies: every row, cell key, board cell,
    /// direction, pattern and admitted value addressed by ordinal rather than by name.</summary>
    /// <param name="transform">The authored transform.</param>
    /// <param name="context">The compile context whose catalog names resolve against.</param>
    /// <param name="resolved">The resolved transform, on success.</param>
    /// <param name="reason">Why the transform was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the transform resolved.</returns>
    /// <remarks>This is the door a host's own command path and console take, where an authored transform arrives
    /// with its keys already literal. A rule compiles through the same resolution and carries the live key and
    /// live zone ends beside the result, so nothing resolves a name while a firing runs.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static bool TryResolveTransform(StateTransform transform, RuleCompileContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out ArenaTransform? resolved, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);

        try {
            var effect = ResolveStateTransform(
                context: context,
                resolved: out resolved,
                ruleName: string.Empty,
                transform: transform
            );

            if (resolved is null) {
                return ArenaTransforms.TryVectorTransform(
                    catalog: context.Catalog,
                    effect: effect,
                    reader: null,
                    reason: out reason,
                    transform: out resolved
                );
            }

            reason = string.Empty;

            return true;
        } catch (RuleException exception) {
            reason = exception.Message;
            resolved = null;

            return false;
        }
    }

    private static IRuleEffect ResolveStateTransform(StateTransform transform, string ruleName, RuleCompileContext context) => ResolveStateTransform(
        context: context,
        resolved: out _,
        ruleName: ruleName,
        transform: transform
    );
    private static IRuleEffect ResolveStateTransform(StateTransform transform, string ruleName, RuleCompileContext context, out ArenaTransform? resolved) {
        RuleException Invalid(string message) => new(
            detail: message,
            refusal: RuleRefusal.EffectKindInadmissible,
            ruleName: ruleName
        );
        StateRow Row(string name) => (context.FindRow(name: name) ?? throw Invalid(message: $"unknown state row '{name}'"));
        int Ordinal(string name) => ResolveRowOrdinal(
            context: context,
            name: name
        );
        var reads = new List<int>();
        var writes = new List<int>();
        CompiledCellRef? keyRef = null;
        CompiledValueSource? pushValue = null;
        LiveRow? fromRow = null;
        LiveRow? toRow = null;
        var sourceCost = RuleWork.Zero;

        resolved = null;

        switch (transform) {
            case StateTransform.Mix mix:
                return ResolveVectorMixTransform(
                    context: context,
                    mix: mix,
                    ruleName: ruleName
                );
            case StateTransform.Mean mean:
                return ResolveVectorMeanTransform(
                    context: context,
                    mean: mean,
                    ruleName: ruleName
                );
            case StateTransform.Nearest nearest:
                return ResolveVectorNearestTransform(
                    context: context,
                    nearest: nearest,
                    ruleName: ruleName
                );
            case StateTransform.Remember remember:
                return ResolveVectorRememberTransform(
                    context: context,
                    remember: remember,
                    ruleName: ruleName
                );
            case StateTransform.Observe observe:
                if (Row(name: observe.Row).Knowledge is not { } knowledge) {
                    throw Invalid(message: "observe requires a knowledge board");
                }

                // The kernel sweeps the declared source and mask boards as well as the knowledge board, so all three
                // are reads of the rule and the hazard picture covers a writer of either board.
                reads.Add(item: Ordinal(name: observe.Row));
                reads.Add(item: Ordinal(name: knowledge.Source));
                reads.Add(item: Ordinal(name: knowledge.Mask));
                writes.Add(item: Ordinal(name: observe.Row));
                resolved = new ArenaTransform.Observe(
                    MaskRowOrdinal: Ordinal(name: knowledge.Mask),
                    RowOrdinal: Ordinal(name: observe.Row),
                    SourceRowOrdinal: Ordinal(name: knowledge.Source)
                );

                break;
            case StateTransform.Transfer transfer: {
                    // A live end indexes the rule's zone table; a literal end must be an ordered zone over the same
                    // token domain as the other end (the table's, when that end is live).
                    _ = TryResolveLiveRow(
                        context: context,
                        name: transfer.From,
                        row: out fromRow,
                        ruleName: ruleName,
                        where: "transfer 'from'"
                    );
                    _ = TryResolveLiveRow(
                        context: context,
                        name: transfer.To,
                        row: out toRow,
                        ruleName: ruleName,
                        where: "transfer 'to'"
                    );

                    var tokenDomain = (fromRow ?? toRow)?.Table?.TokenDomain;

                    void RequireZone(string name, string label) {
                        if (Row(name: name).EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zone) {
                            throw Invalid(message: $"transfer {label} '{name}' is not an ordered token zone");
                        }
                        if (
                            (tokenDomain is { } domain) &&
                            !string.Equals(
                            a: domain,
                            b: zone.Row.Value,
                            comparisonType: StringComparison.Ordinal
                        )
                        ) {
                            throw Invalid(message: $"transfer {label} '{name}' is a zone over '{zone.Row}', not the token domain '{domain}' the transfer's other end shares");
                        }

                        tokenDomain = zone.Row.Value;
                    }

                    if (fromRow is null) {
                        RequireZone(
                            label: "'from'",
                            name: transfer.From
                        );
                        writes.Add(item: Ordinal(name: transfer.From));
                    }
                    if (toRow is null) {
                        RequireZone(
                            label: "'to'",
                            name: transfer.To
                        );
                        writes.Add(item: Ordinal(name: transfer.To));
                    }
                    if (
                        !Enum.IsDefined(value: transfer.Selector) ||
                        ((transfer.Selector is ZoneSelector.Key or ZoneSelector.Slice) != (transfer.Key is not null)) ||
                        ((transfer.Selector == ZoneSelector.Random) != (transfer.Draw is not null)) ||
                        (transfer.Count < 1) ||
                        (transfer.Count > StateTransferCapacity.MaxTransferCount) ||
                        ((transfer.Selector is ZoneSelector.Key or ZoneSelector.Slice) && (transfer.Count != 1))
                    ) {
                        throw Invalid(message: $"transfer requires selector arguments matching the selector and a count of 1..{StateTransferCapacity.MaxTransferCount} (exactly 1 by key or slice)");
                    }
                    if (transfer.Draw is { } drawName) {
                        var drawRow = Row(name: drawName);

                        if (
                            (drawRow.Draw is not { Timing: not DrawTiming.Boot } draw) ||
                            (drawRow.Kind != CellKind.Int) ||
                            !GeneratorEngine.TryResolveSource(
                            draw: draw,
                            generator: out var generator,
                            generators: context.Generators,
                            reason: out _
                        ) ||
                            (generator.Source != GeneratorSource.StreamDraw)
                        ) {
                            throw Invalid(message: "random transfer requires a redrawable integer streamDraw site");
                        }

                        writes.Add(item: Ordinal(name: drawName));
                    }
                    if (transfer.Key is { } key) {
                        if (TryResolveDynamicKey(
                            cell: out var selectedKey,
                            context: context,
                            key: key,
                            keyFieldLabel: "key",
                            ruleName: ruleName,
                            verb: "transfer"
                        )) {
                            keyRef = selectedKey;
                        } else if (!CellName.TryParse(
                            candidate: key,
                            name: out _,
                            reason: out _
                        )) {
                            throw Invalid(message: $"transfer 'key' '{key}' spells neither a token name nor a dynamic key");
                        }
                    }

                    resolved = new ArenaTransform.Transfer(
                        Count: transfer.Count,
                        DrawRowOrdinal: ((transfer.Draw is { } site)
                        ? Ordinal(name: site)
                        : -1),
                        FromRowOrdinal: ((fromRow is null)
                        ? Ordinal(name: transfer.From)
                        : -1),
                        InsertFirst: transfer.InsertFirst,
                        Key: (((transfer.Key is { } literal) && (keyRef is null))
                        ? InternKey(
                            context: context,
                            name: literal
                        )
                        : default),
                        Selector: transfer.Selector,
                        ToRowOrdinal: ((toRow is null)
                        ? Ordinal(name: transfer.To)
                        : -1)
                    );

                    break;
                }
            case StateTransform.SetRay ray: {
                    var row = Row(name: ray.Row);

                    if (
                        (row.EffectiveDomain is not StateDomain.CellsOf board) ||
                        (context.FindTopology(name: board.Topology) is not { } topology) ||
                        (topology.Direction(token: ray.Direction) < 0) ||
                        (FindPatternRow(
                        context: context,
                        name: ray.Pattern
                    ) is not { } pattern) ||
                        (pattern.Kind != CellKind.Int) ||
                        !row.TryAdmitWrite(
                        current: 0L,
                        operand: ray.Value,
                        reason: out _,
                        stored: out _,
                        write: StateWriteKind.Set
                    ) ||
                        ((row.Kind == CellKind.Bool) && (ray.Value is not (0 or 1)))
                    ) {
                        throw Invalid(message: "setRay requires valid board addressing, a declared integer-kind pattern, and an admitted replacement");
                    }

                    if (!context.TryPattern(
                        name: ray.Pattern,
                        pattern: out var compiledRay,
                        reason: out var rayReason
                    )) {
                        throw Invalid(message: rayReason);
                    }

                    var rayOrigin = -1;

                    reads.Add(item: Ordinal(name: ray.Row));
                    writes.Add(item: Ordinal(name: ray.Row));
                    if (TryResolveDynamicKey(
                        cell: out var rayKey,
                        context: context,
                        key: ray.From,
                        keyFieldLabel: "from",
                        ruleName: ruleName,
                        verb: "setRay"
                    )) {
                        keyRef = rayKey;
                    } else if (!topology.TryCell(
                        cell: out rayOrigin,
                        key: ray.From
                    )) {
                        throw Invalid(message: $"setRay 'from' names no cell of '{board.Topology}' and spells no dynamic key");
                    }

                    resolved = new ArenaTransform.SetRay(
                        Direction: topology.Direction(token: ray.Direction),
                        Origin: ((keyRef is null)
                        ? rayOrigin
                        : -1),
                        Pattern: compiledRay!,
                        RowOrdinal: Ordinal(name: ray.Row),
                        Value: ray.Value
                    );

                    break;
                }
            case StateTransform.SortZone sortZone: {
                    if (
                        (Row(name: sortZone.Row).EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zone) ||
                        (sortZone.By is not { Count: >= 1 }) ||
                        sortZone.By.Any(predicate: key => ((key is null) || (Row(name: key.Row) is not { IsKeyed: true, Kind: CellKind.Int or CellKind.Fixed } sortRow) || (sortRow.EffectiveDomain is not StateDomain.KeysOf sortKeysOf) || (sortKeysOf.Row != zone.Row))) ||
                        (sortZone.By.Select(selector: key => key!.Row).Distinct(comparer: StringComparer.Ordinal).Count() != sortZone.By.Count)
                    ) {
                        throw Invalid(message: $"sortZone requires an ordered zone with one or more distinct numeric attribute keys over the zone's token domain, each carrying its own direction");
                    }

                    var sortKeys = new ArenaSortKey[sortZone.By.Count];

                    for (var key = 0; (key < sortZone.By.Count); key++) {
                        reads.Add(item: Ordinal(name: sortZone.By[key]!.Row));
                        sortKeys[key] = new ArenaSortKey(
                            Descending: sortZone.By[key]!.Descending,
                            RowOrdinal: Ordinal(name: sortZone.By[key]!.Row)
                        );
                    }

                    writes.Add(item: Ordinal(name: sortZone.Row));
                    resolved = new ArenaTransform.SortZone(
                        By: sortKeys,
                        RowOrdinal: Ordinal(name: sortZone.Row)
                    );

                    break;
                }
            case StateTransform.SortKeyed sortKeyed:
                // A permutation has meaning only where a row's order is its own: on a board or a ring the position is
                // the address, so the arena reorders neither and an authored sort over one is refused here rather
                // than left to fire and refuse.
                if (Row(name: sortKeyed.Row) is not { Shape: RowShape.Keyed or RowShape.Ordered, Kind: CellKind.Int or CellKind.Fixed }) {
                    throw Invalid(message: "sortKeyed requires a keyed or ordered numeric row");
                }

                writes.Add(item: Ordinal(name: sortKeyed.Row));
                resolved = new ArenaTransform.SortKeyed(
                    Descending: sortKeyed.Descending,
                    RowOrdinal: Ordinal(name: sortKeyed.Row)
                );

                break;
            case StateTransform.Shuffle shuffle:
                if (
                    (Row(name: shuffle.Row) is not { Shape: RowShape.Keyed or RowShape.Ordered }) ||
                    (Row(name: shuffle.Draw).Draw is not { Timing: not DrawTiming.Boot } shuffleDraw) ||
                    (Row(name: shuffle.Draw).Kind != CellKind.Int) ||
                    !GeneratorEngine.TryResolveSource(
                    draw: shuffleDraw,
                    generator: out var shuffleSource,
                    generators: context.Generators,
                    reason: out _
                ) ||
                    (shuffleSource.Source != GeneratorSource.StreamDraw)
                ) {
                    throw Invalid(message: "shuffle requires a keyed or ordered row, and a redrawable integer streamDraw site");
                }

                writes.Add(item: Ordinal(name: shuffle.Draw));
                writes.Add(item: Ordinal(name: shuffle.Row));
                resolved = new ArenaTransform.Shuffle(
                    DrawRowOrdinal: Ordinal(name: shuffle.Draw),
                    RowOrdinal: Ordinal(name: shuffle.Row)
                );

                break;
            case StateTransform.WriteSet writeSet: {
                    var setSource = Row(name: writeSet.Set);
                    var written = Row(name: writeSet.Row);

                    if (
                        (written.EffectiveDomain is not StateDomain.CellsOf writtenBoard) ||
                        (context.FindTopology(name: writtenBoard.Topology) is not { } writtenTopology) ||
                        (writtenTopology.CellCount > BoardMask.MaxCells) ||
                        (setSource.Kind != CellKind.Int) ||
                        !written.TryAdmitWrite(
                        current: 0L,
                        operand: writeSet.Value,
                        reason: out _,
                        stored: out _,
                        write: StateWriteKind.Set
                    ) ||
                        ((written.Kind == CellKind.Bool) && (writeSet.Value is not (0 or 1)))
                    ) {
                        throw Invalid(message: $"writeSet requires a board of at most {BoardMask.MaxCells} cells, an integer set row, and an admitted value");
                    }

                    reads.Add(item: Ordinal(name: writeSet.Set));
                    writes.Add(item: Ordinal(name: writeSet.Row));
                    if (TryResolveDynamicKey(
                        cell: out var setKeyRef,
                        context: context,
                        key: writeSet.SetKey,
                        keyFieldLabel: "setKey",
                        ruleName: ruleName,
                        verb: "writeSet"
                    )) {
                        if (!setSource.IsKeyed) {
                            throw Invalid(message: "writeSet 'setKey' addresses a cell by indirection, but the set row is not keyed");
                        }

                        keyRef = setKeyRef;
                    } else if ((writeSet.SetKey is null)
                        ? !setSource.IsSlot
                        : (!setSource.IsKeyed || !CellName.TryParse(
                            candidate: writeSet.SetKey,
                            name: out _,
                            reason: out _
                        ))) {
                        throw Invalid(message: $"writeSet reads its cell set from an integer cell '{(writeSet.SetKey ?? StateRow.SlotKey.Value)}' of '{writeSet.Set}'");
                    }

                    resolved = new ArenaTransform.WriteSet(
                        RowOrdinal: Ordinal(name: writeSet.Row),
                        SetKey: ((keyRef is null)
                        ? InternKey(
                            context: context,
                            name: (writeSet.SetKey ?? StateRow.SlotKey.Value)
                        )
                        : default),
                        SetRowOrdinal: Ordinal(name: writeSet.Set),
                        Value: writeSet.Value
                    );

                    break;
                }
            case StateTransform.BoardCombine combine: {
                    var target = Row(name: combine.Row);

                    if (
                        (target.EffectiveDomain is not StateDomain.CellsOf targetBoard) ||
                        (context.FindTopology(name: targetBoard.Topology) is not { } targetTopology)
                    ) {
                        throw Invalid(message: "boardCombine writes a board row");
                    }
                    if (!BoardCombination.TryValidate(
                        combine,
                        target,
                        targetBoard.Empty,
                        targetTopology,
                        out _,
                        out _,
                        out _,
                        out var combineReason
                    )) {
                        throw Invalid(message: combineReason);
                    }

                    foreach (var sourceName in new[] { combine.Left, combine.Right }) {
                        if (sourceName is null) {
                            continue;
                        }
                        if ((Row(name: sourceName).EffectiveDomain is not StateDomain.CellsOf sourceBoard) || (sourceBoard.Topology != targetBoard.Topology)) {
                            throw Invalid(message: $"boardCombine source '{sourceName}' must be a board over '{targetBoard.Topology}'");
                        }

                        reads.Add(item: Ordinal(name: sourceName));
                    }

                    _ = BoardCombination.TryValidate(
                        combine: combine,
                        direction: out var combineDirection,
                        element: out var combineElement,
                        empty: targetBoard.Empty,
                        reason: out _,
                        row: target,
                        topology: targetTopology,
                        value: out var combineValue
                    );
                    writes.Add(item: Ordinal(name: combine.Row));
                    resolved = new ArenaTransform.BoardCombine(
                        Direction: combineDirection,
                        Element: combineElement,
                        LeftRowOrdinal: ((combine.Left is { } leftName)
                        ? Ordinal(name: leftName)
                        : -1),
                        Operation: combine.Operation,
                        RightRowOrdinal: ((combine.Right is { } rightName)
                        ? Ordinal(name: rightName)
                        : -1),
                        RowOrdinal: Ordinal(name: combine.Row),
                        Value: combineValue
                    );

                    break;
                }
            case StateTransform.Arrange arrange: {
                    var arrangedZone = Row(name: arrange.Row);
                    var rank = Row(name: arrange.From);

                    if (
                        (arrangedZone.EffectiveDomain is not StateDomain.KeysOf { Ordered: true }) ||
                        (rank.Kind != CellKind.Int) ||
                        ((arrange.FromKey is null)
                        ? !rank.IsSlot
                        : (!rank.IsKeyed || !CellName.TryParse(
                            candidate: arrange.FromKey,
                            name: out _,
                            reason: out _
                        )))
                    ) {
                        throw Invalid(message: "arrange requires an ordered zone and an integer rank cell");
                    }

                    reads.Add(item: Ordinal(name: arrange.From));
                    writes.Add(item: Ordinal(name: arrange.Row));
                    resolved = new ArenaTransform.Arrange(
                        DomainRowOrdinal: Ordinal(name: ((StateDomain.KeysOf)arrangedZone.EffectiveDomain).Row.Value),
                        FromKey: InternKey(
                            context: context,
                            name: (arrange.FromKey ?? StateRow.SlotKey.Value)
                        ),
                        FromRowOrdinal: Ordinal(name: arrange.From),
                        RowOrdinal: Ordinal(name: arrange.Row)
                    );

                    break;
                }
            case StateTransform.Push push: {
                    var ring = Row(name: push.Row);

                    if (ring.EffectiveDomain is not StateDomain.Ring) {
                        throw Invalid(message: "push requires a history row");
                    }

                    var pushed = ResolvePushValue(
                        context: context,
                        push: push,
                        ring: ring,
                        ruleName: ruleName
                    );

                    pushValue = pushed;
                    sourceCost = pushed.Cost(
                        context: context,
                        kind: ring.Kind
                    );
                    writes.Add(item: Ordinal(name: push.Row));
                    resolved = new ArenaTransform.Push(
                        Bound: !pushed.IsLiteral,
                        RowOrdinal: Ordinal(name: push.Row),
                        Value: pushed.RawValue
                    );

                    break;
                }
            case StateTransform.ClearEnclosed enclosed: {
                    var enclosedRow = Row(name: enclosed.Row);

                    if (
                        (enclosedRow.EffectiveDomain is not StateDomain.CellsOf enclosedBoard) ||
                        (context.FindTopology(name: enclosedBoard.Topology) is not { } enclosedTopology) ||
                        (enclosedRow.Kind != CellKind.Int) ||
                        (enclosed.Lower > enclosed.Upper) ||
                        ((enclosedBoard.Empty >= enclosed.Lower) && (enclosedBoard.Empty <= enclosed.Upper))
                    ) {
                        throw Invalid(message: "clearEnclosed requires an integer board and an enclosed range that excludes the board's empty value");
                    }

                    var enclosedOrigin = -1;

                    reads.Add(item: Ordinal(name: enclosed.Row));
                    writes.Add(item: Ordinal(name: enclosed.Row));
                    if (TryResolveDynamicKey(
                        cell: out var origin,
                        context: context,
                        key: enclosed.From,
                        keyFieldLabel: "from",
                        ruleName: ruleName,
                        verb: "clearEnclosed"
                    )) {
                        keyRef = origin;
                    } else if (!enclosedTopology.TryCell(
                        enclosed.From,
                        out enclosedOrigin
                    )) {
                        throw Invalid(message: $"clearEnclosed 'from' names no cell of '{enclosedBoard.Topology}' and spells no dynamic key");
                    }

                    resolved = new ArenaTransform.ClearEnclosed(
                        From: ((keyRef is null)
                        ? InternKey(
                            context: context,
                            name: enclosed.From
                        )
                        : default),
                        Lower: enclosed.Lower,
                        Origin: ((keyRef is null)
                        ? enclosedOrigin
                        : -1),
                        RowOrdinal: Ordinal(name: enclosed.Row),
                        Upper: enclosed.Upper
                    );

                    break;
                }
            default:
                throw Invalid(message: "unknown or null state transform");
        }

        return new TransformStateEffect(
            arena: resolved!,
            cost: (TransformCost(
                context: context,
                fromRow: fromRow,
                transform: transform
            ) + sourceCost),
            describe: DescribeTransform(transform: transform),
            fromRow: fromRow,
            keyRef: keyRef,
            reads: [.. reads],
            toRow: toRow,
            transform: transform,
            value: pushValue,
            writes: [.. writes]
        );
    }
    // push takes the value spellings pushState takes, so the two land the same number: the raw literal converted
    // once here, or one live source compiled through the write resolver and evaluated per firing.
    private static CompiledValueSource ResolvePushValue(StateTransform.Push push, StateRow ring, string ruleName, RuleCompileContext context) {
        RuleException Ambiguous(string message) => new(
            detail: message,
            refusal: RuleRefusal.EffectSourceAmbiguous,
            ruleName: ruleName
        );
        var hasExpression = (push.Expression is not null);
        var hasFrom = (push.FromState is not null);

        if (
            !hasExpression &&
            !hasFrom
        ) {
            if (!ring.TryAdmitWrite(
                current: 0L,
                operand: push.Value,
                reason: out _,
                stored: out _,
                write: StateWriteKind.Set
            )) {
                throw new RuleException(
                    detail: $"push writes {push.Value} into '{push.Row}', which its history row does not admit",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: ruleName
                );
            }

            return CompiledValueSource.Constant(rawValue: push.Value);
        }
        if (hasExpression && hasFrom) {
            throw Ambiguous(message: "push names both 'fromState' and 'expression' — a push spells exactly one value source");
        }
        if (push.Value != 0L) {
            throw Ambiguous(message: $"push names a live value source beside 'value' {push.Value} — a push spells exactly one value source");
        }

        // A Vector row's write resolves to a vector effect, not a WriteEffect, and a history ring never holds one.
        var resolved = ResolveWrite(
            context: context,
            expression: push.Expression,
            fromKey: push.FromKey,
            fromState: push.FromState,
            key: "0",
            rowName: push.Row,
            ruleName: ruleName,
            target: ActionTarget.Self,
            text: null,
            value: null,
            valueSeconds: null,
            verb: "push",
            write: StateWriteKind.Set
        );

        if (resolved is not WriteEffect write) {
            throw new RuleException(
                detail: $"push writes '{push.Row}', whose kind carries no single value a history ring can hold",
                refusal: RuleRefusal.EffectSourceKindMismatch,
                ruleName: ruleName
            );
        }

        return write.Source;
    }
    private static int BoardCells(RuleCompileContext context, string row) => (((context.FindRow(name: row)?.EffectiveDomain is StateDomain.CellsOf board) && (context.FindTopology(name: board.Topology) is { } topology))
        ? topology.CellCount
        : 0
    );
    private static string DescribeTransform(StateTransform transform) => (transform switch {
        StateTransform.Transfer transfer => $"transformState Transfer {transfer.From} to {transfer.To} {transfer.Selector}",
        _ => $"transformState {transform.GetType().Name}",
    });
    private static RuleWork TransformCost(StateTransform transform, RuleCompileContext context, LiveRow? fromRow) {
        var storage = 0L;

        foreach (var row in context.Rows) {
            storage += row.CellCeiling;
        }

        var cost = RuleWork.Known(units: (4_096L + storage));

        switch (transform) {
            case StateTransform.SetRay ray:
                var rayCells = BoardCells(
                    context: context,
                    row: ray.Row
                );

                cost += (((long)rayCells) * (rayCells + 2));

                break;
            case StateTransform.Transfer transfer:
                // A live source is priced at the widest row it can name.
                cost += (((transfer.Selector == ZoneSelector.Slice)
                    ? 2L
                    : ((long)transfer.Count)) * (fromRow?.SelectionCapacity ?? context.RowCapacity(name: transfer.From)));

                break;
            case StateTransform.BoardCombine combine:
                cost += (3L * BoardCells(
                    context: context,
                    row: combine.Row
                ));

                break;
            case StateTransform.Arrange arrange:
                cost += (4L * context.RowCapacity(name: arrange.Row));

                break;
            case StateTransform.SortZone sortZone:
                cost += RuleWorkBudget.InsertionSortWork(
                    count: context.RowCapacity(name: sortZone.Row),
                    keys: Math.Max(
                        val1: 1,
                        val2: sortZone.By.Count
                    )
                );

                break;
            case StateTransform.SortKeyed sortKeyed:
                cost += RuleWorkBudget.InsertionSortWork(
                    count: context.RowCapacity(name: sortKeyed.Row),
                    keys: 1
                );

                break;
            case StateTransform.Shuffle shuffle:
                cost += (2L * context.RowCapacity(name: shuffle.Row));

                break;
            case StateTransform.WriteSet writeSet:
                var writtenCells = BoardCells(
                    context: context,
                    row: writeSet.Row
                );

                cost += (((long)writtenCells) * (writtenCells + 1));

                break;
            case StateTransform.Push push:
                cost += (2L * ((context.FindRow(name: push.Row)?.EffectiveDomain as StateDomain.Ring)?.Capacity ?? 1));

                break;
            case StateTransform.ClearEnclosed enclosed:
                var directions = ((context.FindRow(name: enclosed.Row)?.EffectiveDomain is StateDomain.CellsOf enclosedBoardRow)
                    ? (context.FindTopology(name: enclosedBoardRow.Topology)?.DirectionCount ?? 0)
                    : 0
                );
                var enclosedCells = BoardCells(
                    context: context,
                    row: enclosed.Row
                );

                cost += (((long)enclosedCells) * (directions + 2));

                break;
            case StateTransform.Observe observe:
                var observedCells = BoardCells(
                    context: context,
                    row: observe.Row
                );

                cost += (((long)observedCells) * (observedCells + 3));

                break;
            default:
                break;
        }

        return cost;
    }
}

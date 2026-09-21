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
                if (Row(name: observe.Row.Spelling).Knowledge is not { } knowledge) {
                    throw Invalid(message: "observe requires a knowledge board");
                }

                // The kernel sweeps the token-keyed source and positions, the mask board, and the knowledge row.
                reads.Add(item: Ordinal(name: observe.Row.Spelling));
                reads.Add(item: Ordinal(name: knowledge.Source));
                reads.Add(item: Ordinal(name: knowledge.Mask));
                if (knowledge.Positions is { } positions) {
                    reads.Add(item: Ordinal(name: positions));
                }
                writes.Add(item: Ordinal(name: observe.Row.Spelling));
                resolved = new ArenaTransform.Observe(
                    MaskRowOrdinal: Ordinal(name: knowledge.Mask),
                    PositionsRowOrdinal: ((knowledge.Positions is { } positionsName) ? Ordinal(name: positionsName) : -1),
                    RowOrdinal: Ordinal(name: observe.Row.Spelling),
                    SourceRowOrdinal: Ordinal(name: knowledge.Source)
                );

                break;
            case StateTransform.Transfer transfer: {
                    var transferFrom = transfer.From.Spelling;
                    var transferTo = transfer.To.Spelling;

                    // A live end indexes the rule's zone table; a literal end must be an ordered zone over the same
                    // token domain as the other end (the table's, when that end is live).
                    _ = TryResolveLiveRow(
                        context: context,
                        name: transferFrom,
                        row: out fromRow,
                        ruleName: ruleName,
                        where: "transfer 'from'"
                    );
                    _ = TryResolveLiveRow(
                        context: context,
                        name: transferTo,
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
                            name: transferFrom
                        );
                        writes.Add(item: Ordinal(name: transferFrom));
                    }
                    if (toRow is null) {
                        RequireZone(
                            label: "'to'",
                            name: transferTo
                        );
                        writes.Add(item: Ordinal(name: transferTo));
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
                    if (transfer.Draw is { } drawChannel) {
                        var drawName = drawChannel.Spelling;
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
                    if (transfer.Key is { } keyChannel) {
                        var key = keyChannel.Spelling;

                        if (TryResolveDynamicKey(
                            cell: out var selectedKey,
                            context: context,
                            keyFieldLabel: "key",
                            reference: keyChannel,
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
                        ? Ordinal(name: site.Spelling)
                        : -1),
                        FromRowOrdinal: ((fromRow is null)
                        ? Ordinal(name: transferFrom)
                        : -1),
                        InsertFirst: transfer.InsertFirst,
                        Key: (((transfer.Key is { } literal) && (keyRef is null))
                        ? InternKey(
                            context: context,
                            name: literal.Spelling
                        )
                        : default),
                        Selector: transfer.Selector,
                        ToRowOrdinal: ((toRow is null)
                        ? Ordinal(name: transferTo)
                        : -1)
                    );

                    break;
                }
            case StateTransform.SetRay ray: {
                    var rayRow = ray.Row.Spelling;
                    var rayFrom = ray.From.Spelling;
                    var rayPattern = ray.Pattern.Spelling;
                    var row = Row(name: rayRow);

                    if (
                        (row.EffectiveDomain is not StateDomain.CellsOf board) ||
                        (context.FindTopology(name: board.Topology) is not { } topology) ||
                        (topology.Direction(token: ray.Direction) < 0) ||
                        (FindPatternRow(
                        context: context,
                        name: rayPattern
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
                        name: rayPattern,
                        pattern: out var compiledRay,
                        reason: out var rayReason
                    )) {
                        throw Invalid(message: rayReason);
                    }

                    var rayOrigin = -1;

                    reads.Add(item: Ordinal(name: rayRow));
                    writes.Add(item: Ordinal(name: rayRow));
                    if (TryResolveDynamicKey(
                        cell: out var rayKey,
                        context: context,
                        reference: ray.From,
                        keyFieldLabel: "from",
                        ruleName: ruleName,
                        verb: "setRay"
                    )) {
                        keyRef = rayKey;
                    } else if (!topology.TryCell(
                        cell: out rayOrigin,
                        key: rayFrom
                    )) {
                        throw Invalid(message: $"setRay 'from' names no cell of '{board.Topology}' and spells no dynamic key");
                    }

                    resolved = new ArenaTransform.SetRay(
                        Direction: topology.Direction(token: ray.Direction),
                        Origin: ((keyRef is null)
                        ? rayOrigin
                        : -1),
                        Pattern: compiledRay!,
                        RowOrdinal: Ordinal(name: rayRow),
                        Value: ray.Value
                    );

                    break;
                }
            case StateTransform.PushRay pushRay: {
                    if (!context.Catalog.TryGetPool(name: pushRay.Pool, pool: out var pool) || (pool is null) || pool.IsPair) {
                        throw Invalid(message: $"pushRay names no ordinary pool '{pushRay.Pool}'");
                    }
                    var cellField = pool.Fields.FirstOrDefault(predicate: field => (field.Name == pushRay.Cell));
                    var valueField = pool.Fields.FirstOrDefault(predicate: field => (field.Name == pushRay.Value));
                    var topology = context.FindTopology(name: pushRay.Topology.Value);
                    var direction = (topology?.Direction(token: pushRay.Direction) ?? -1);
                    var patternName = pushRay.Pattern.Spelling;
                    var pushName = pushRay.PushPattern.Spelling;
                    var stopName = pushRay.StopPattern.Spelling;

                    if ((cellField.Name != pushRay.Cell) || (valueField.Name != pushRay.Value) || (cellField.Kind != CellKind.Int) || (valueField.Kind != CellKind.Int) || (topology is null) || (direction < 0) || ((cellField.Declaration.Min is { } minimum) && (minimum > 0L)) || ((cellField.Declaration.Max is { } maximum) && (maximum < (topology.CellCount - 1L))) || (FindPatternRow(context: context, name: patternName) is not { Kind: CellKind.Int }) || (FindPatternRow(context: context, name: pushName) is not { Kind: CellKind.Int }) || (FindPatternRow(context: context, name: stopName) is not { Kind: CellKind.Int })) {
                        throw Invalid(message: "pushRay requires integer cell/value fields whose cell envelope admits the whole topology, a declared direction, and integer run, push, and stop patterns");
                    }
                    if (!TryResolveInstanceField(reference: pushRay.From, context: context, binding: out var originBinding, field: out var originField) || (originBinding.Pool.Ordinal != pool.Ordinal) || (originField.Ordinal != cellField.Ordinal)) {
                        throw Invalid(message: "pushRay 'from' must be the selected pool's live cell field");
                    }
                    if (!context.TryPattern(name: patternName, pattern: out var pattern, reason: out var patternReason)) {
                        throw Invalid(message: patternReason);
                    }
                    if (!context.TryPattern(name: pushName, pattern: out var push, reason: out var pushReason)) {
                        throw Invalid(message: pushReason);
                    }
                    if (!context.TryPattern(name: stopName, pattern: out var stop, reason: out var stopReason)) {
                        throw Invalid(message: stopReason);
                    }

                    reads.Add(item: pool.DomainRowOrdinal);
                    reads.Add(item: cellField.RowOrdinal);
                    reads.Add(item: valueField.RowOrdinal);
                    writes.Add(item: cellField.RowOrdinal);
                    resolved = new ArenaTransform.PushRay(PoolOrdinal: pool.Ordinal, CellFieldOrdinal: cellField.Ordinal, ValueFieldOrdinal: valueField.Ordinal, OriginBindingSlot: originBinding.Slot, Topology: topology, Direction: direction, Pattern: pattern!, PushPattern: push!, StopPattern: stop!, Empty: pushRay.Empty);
                    break;
                }
            case StateTransform.Sort sort: {
                    var target = sort.Row.Spelling;
                    if ((sort.By is not { Count: >= 1 }) || sort.By.Any(key => (key is null))) {
                        throw Invalid(message: "sort requires one or more numeric attribute keys, each carrying its own direction");
                    }
                    var targetOrdinal = Ordinal(name: target);
                    if ((sort.By.Count == 1) && (sort.By[0].Row.Spelling == target)) {
                        // A board position or ring slot is an address, not an order to permute.
                        if (Row(name: target) is not { Shape: RowShape.Keyed or RowShape.Ordered, Kind: CellKind.Int or CellKind.Fixed }) {
                            throw Invalid(message: "sort by own values requires a keyed or ordered numeric row");
                        }
                        reads.Add(item: targetOrdinal);
                        writes.Add(item: targetOrdinal);
                        resolved = new ArenaTransform.SortKeyed(RowOrdinal: targetOrdinal, Descending: sort.By[0].Descending);
                        break;
                    }
                    if (Row(name: target).EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zone) {
                        throw Invalid(message: "sort by attributes requires an ordered zone over a token domain");
                    }
                    var names = new HashSet<string>(comparer: StringComparer.Ordinal);
                    var keys = new ArenaSortKey[sort.By.Count];
                    for (var index = 0; (index < keys.Length); index++) {
                        var key = sort.By[index];
                        var name = key.Row.Spelling;
                        if (!names.Add(name) || (name == target) ||
                            (Row(name: name) is not { IsKeyed: true, Kind: CellKind.Int or CellKind.Fixed } attribute) ||
                            (attribute.EffectiveDomain is not StateDomain.KeysOf domain) || (domain.Row != zone.Row)) {
                            throw Invalid(message: "sort requires distinct numeric attribute keys over the zone's token domain; an own-value key must stand alone");
                        }
                        var ordinal = Ordinal(name: name);
                        reads.Add(item: ordinal);
                        keys[index] = new ArenaSortKey(RowOrdinal: ordinal, Descending: key.Descending);
                    }
                    writes.Add(item: targetOrdinal);
                    resolved = new ArenaTransform.SortZone(By: keys, RowOrdinal: targetOrdinal);
                    break;
                }
            case StateTransform.Shuffle shuffle: {
                    var shuffleRow = shuffle.Row.Spelling;
                    var shuffleDrawName = shuffle.Draw.Spelling;

                    if (
                        (Row(name: shuffleRow) is not { Shape: RowShape.Keyed or RowShape.Ordered }) ||
                        (Row(name: shuffleDrawName).Draw is not { Timing: not DrawTiming.Boot } shuffleDraw) ||
                        (Row(name: shuffleDrawName).Kind != CellKind.Int) ||
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

                    writes.Add(item: Ordinal(name: shuffleDrawName));
                    writes.Add(item: Ordinal(name: shuffleRow));
                    resolved = new ArenaTransform.Shuffle(
                        DrawRowOrdinal: Ordinal(name: shuffleDrawName),
                        RowOrdinal: Ordinal(name: shuffleRow)
                    );

                    break;
                }
            case StateTransform.WriteSet writeSet: {
                    var writeSetSet = writeSet.Set.Spelling;
                    var writeSetRow = writeSet.Row.Spelling;
                    var setSource = Row(name: writeSetSet);
                    var written = Row(name: writeSetRow);

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

                    var writeSetKey = writeSet.SetKey?.Spelling;

                    reads.Add(item: Ordinal(name: writeSetSet));
                    writes.Add(item: Ordinal(name: writeSetRow));
                    if (TryResolveDynamicKey(
                        cell: out var setKeyRef,
                        context: context,
                        reference: writeSet.SetKey,
                        keyFieldLabel: "setKey",
                        ruleName: ruleName,
                        verb: "writeSet"
                    )) {
                        if (!setSource.IsKeyed) {
                            throw Invalid(message: "writeSet 'setKey' addresses a cell by indirection, but the set row is not keyed");
                        }

                        keyRef = setKeyRef;
                    } else if ((writeSetKey is null)
                        ? !setSource.IsSlot
                        : (!setSource.IsKeyed || !CellName.TryParse(
                            candidate: writeSetKey,
                            name: out _,
                            reason: out _
                        ))) {
                        throw Invalid(message: $"writeSet reads its cell set from an integer cell '{(writeSetKey ?? StateRow.SlotKey.Value)}' of '{writeSetSet}'");
                    }

                    resolved = new ArenaTransform.WriteSet(
                        RowOrdinal: Ordinal(name: writeSetRow),
                        SetKey: ((keyRef is null)
                        ? InternKey(
                            context: context,
                            name: (writeSetKey ?? StateRow.SlotKey.Value)
                        )
                        : default),
                        SetRowOrdinal: Ordinal(name: writeSetSet),
                        Value: writeSet.Value
                    );

                    break;
                }
            case StateTransform.BoardCombine combine: {
                    var combineRow = combine.Row.Spelling;
                    var target = Row(name: combineRow);

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

                    foreach (var sourceChannel in new[] { combine.Left, combine.Right }) {
                        if (sourceChannel is not { } source) {
                            continue;
                        }

                        var sourceName = source.Spelling;

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
                    writes.Add(item: Ordinal(name: combineRow));
                    resolved = new ArenaTransform.BoardCombine(
                        Direction: combineDirection,
                        Element: combineElement,
                        LeftRowOrdinal: ((combine.Left is { } leftChannel)
                        ? Ordinal(name: leftChannel.Spelling)
                        : -1),
                        Operation: combine.Operation,
                        RightRowOrdinal: ((combine.Right is { } rightChannel)
                        ? Ordinal(name: rightChannel.Spelling)
                        : -1),
                        RowOrdinal: Ordinal(name: combineRow),
                        Value: combineValue
                    );

                    break;
                }
            case StateTransform.Arrange arrange: {
                    var arrangeRow = arrange.Row.Spelling;
                    var arrangeFrom = arrange.From.Spelling;
                    var arrangeFromKey = arrange.FromKey?.Spelling;
                    var arrangedZone = Row(name: arrangeRow);
                    var rank = Row(name: arrangeFrom);

                    if (
                        (arrangedZone.EffectiveDomain is not StateDomain.KeysOf { Ordered: true }) ||
                        (rank.Kind != CellKind.Int) ||
                        ((arrangeFromKey is null)
                        ? !rank.IsSlot
                        : (!rank.IsKeyed || !CellName.TryParse(
                            candidate: arrangeFromKey,
                            name: out _,
                            reason: out _
                        )))
                    ) {
                        throw Invalid(message: "arrange requires an ordered zone and an integer rank cell");
                    }

                    reads.Add(item: Ordinal(name: arrangeFrom));
                    writes.Add(item: Ordinal(name: arrangeRow));
                    resolved = new ArenaTransform.Arrange(
                        DomainRowOrdinal: Ordinal(name: ((StateDomain.KeysOf)arrangedZone.EffectiveDomain).Row.Value),
                        FromKey: InternKey(
                            context: context,
                            name: (arrangeFromKey ?? StateRow.SlotKey.Value)
                        ),
                        FromRowOrdinal: Ordinal(name: arrangeFrom),
                        RowOrdinal: Ordinal(name: arrangeRow)
                    );

                    break;
                }
            case StateTransform.Push push: {
                    var pushRow = push.Row.Spelling;
                    var ring = Row(name: pushRow);

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
                    writes.Add(item: Ordinal(name: pushRow));
                    resolved = new ArenaTransform.Push(
                        Bound: !pushed.IsLiteral,
                        RowOrdinal: Ordinal(name: pushRow),
                        Value: pushed.RawValue
                    );

                    break;
                }
            case StateTransform.ClearEnclosed enclosed: {
                    var enclosedRowName = enclosed.Row.Spelling;
                    var enclosedFrom = enclosed.From.Spelling;
                    var enclosedRow = Row(name: enclosedRowName);

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

                    reads.Add(item: Ordinal(name: enclosedRowName));
                    writes.Add(item: Ordinal(name: enclosedRowName));
                    if (TryResolveDynamicKey(
                        cell: out var origin,
                        context: context,
                        reference: enclosed.From,
                        keyFieldLabel: "from",
                        ruleName: ruleName,
                        verb: "clearEnclosed"
                    )) {
                        keyRef = origin;
                    } else if (!enclosedTopology.TryCell(
                        cell: out enclosedOrigin,
                        key: enclosedFrom
                    )) {
                        throw Invalid(message: $"clearEnclosed 'from' names no cell of '{enclosedBoard.Topology}' and spells no dynamic key");
                    }

                    resolved = new ArenaTransform.ClearEnclosed(
                        From: ((keyRef is null)
                        ? InternKey(
                            context: context,
                            name: enclosedFrom
                        )
                        : default),
                        Lower: enclosed.Lower,
                        Origin: ((keyRef is null)
                        ? enclosedOrigin
                        : -1),
                        RowOrdinal: Ordinal(name: enclosedRowName),
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
            key: StateChannelRef.OfName(name: "0"),
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
                    row: ray.Row.Spelling
                );

                cost += (((long)rayCells) * (rayCells + 2));

                break;
            case StateTransform.PushRay pushRay:
                var pushPool = context.Catalog.Pools.FirstOrDefault(predicate: pool => (pool.Name == pushRay.Pool));
                // Snapshot/index leases and their population cost five pool-width passes. A ray resolves each
                // occupant once, walks its linked occupants once, and writes each mover at most once; leave two
                // pool widths for the journal and pattern machinery. Visited/head/tail buffers cost three board
                // widths. The previous bound charged a complete pool scan for every board cell.
                cost += ((pushPool is null) ? 0L : (12L * pushPool.Capacity));
                cost += (3L * (context.FindTopology(name: pushRay.Topology.Value)?.CellCount ?? 0));
                break;
            case StateTransform.Transfer transfer:
                // A live source is priced at the widest row it can name.
                cost += (((transfer.Selector == ZoneSelector.Slice)
                    ? 2L
                    : ((long)transfer.Count)) * (fromRow?.SelectionCapacity ?? context.RowCapacity(name: transfer.From.Spelling)));

                break;
            case StateTransform.BoardCombine combine:
                cost += (3L * BoardCells(
                    context: context,
                    row: combine.Row.Spelling
                ));

                break;
            case StateTransform.Arrange arrange:
                cost += (4L * context.RowCapacity(name: arrange.Row.Spelling));

                break;
            case StateTransform.Sort sort:
                cost += RuleWorkBudget.InsertionSortWork(
                    count: context.RowCapacity(name: sort.Row.Spelling),
                    keys: Math.Max(
                        val1: 1,
                        val2: sort.By.Count
                    )
                );

                break;
            case StateTransform.Shuffle shuffle:
                cost += (2L * context.RowCapacity(name: shuffle.Row.Spelling));

                break;
            case StateTransform.WriteSet writeSet:
                var writtenCells = BoardCells(
                    context: context,
                    row: writeSet.Row.Spelling
                );

                cost += (((long)writtenCells) * (writtenCells + 1));

                break;
            case StateTransform.Push push:
                cost += (2L * ((context.FindRow(name: push.Row.Spelling)?.EffectiveDomain as StateDomain.Ring)?.Capacity ?? 1));

                break;
            case StateTransform.ClearEnclosed enclosed:
                var directions = ((context.FindRow(name: enclosed.Row.Spelling)?.EffectiveDomain is StateDomain.CellsOf enclosedBoardRow)
                    ? (context.FindTopology(name: enclosedBoardRow.Topology)?.DirectionCount ?? 0)
                    : 0
                );
                var enclosedCells = BoardCells(
                    context: context,
                    row: enclosed.Row.Spelling
                );

                cost += (((long)enclosedCells) * (directions + 2));

                break;
            case StateTransform.Observe observe:
                var observedCells = BoardCells(
                    context: context,
                    row: observe.Row.Spelling
                );

                cost += (((long)observedCells) * (observedCells + 3));

                break;
            default:
                break;
        }

        return cost;
    }
}

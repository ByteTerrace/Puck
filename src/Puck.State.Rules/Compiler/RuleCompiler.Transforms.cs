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

    // A transform writes what it read into rows other readers see: a sort's order spells its keys, a mean its members, an
    // arrangement its rank. So no row it writes may be seen by a reader some row it reads withholds from, judged over
    // the declared row and cell policies (StateVisibility.Encloses). A transform never declassifies; a rule that means
    // to show a hidden value writes it through its own setState. A subject the context declares no row for (a pool, a
    // live zone selection) states no policy here.
    private static void RequireEnclosedAudience(StateTransform transform, string ruleName, RuleCompileContext context) {
        var subjects = transform.Subjects();

        foreach (var written in subjects) {
            if (
                (written.Access != StateAccess.Write) ||
                (context.FindRow(name: written.Name) is not { } target)
            ) {
                continue;
            }

            foreach (var read in subjects) {
                if (
                    (read.Access != StateAccess.Read) ||
                    (context.FindRow(name: read.Name) is not { } source)
                ) {
                    continue;
                }

                var withholding = (StateVisibility.Encloses(
                    audience: target.Visibility,
                    policy: source.Visibility
                )
                    ? (source.Cells ?? []).FirstOrDefault(predicate: cell => ((cell is not null) && !StateVisibility.Encloses(
                        audience: target.Visibility,
                        policy: cell.Visibility
                    )))?.Visibility
                    : source.Visibility);

                if (withholding is not null) {
                    throw new RuleException(
                        detail: $"it writes '{target.Name}' ({DescribeAudience(visibility: target.Visibility)}) from '{source.Name}' ({DescribeAudience(visibility: withholding)}), so what it writes would show '{source.Name}' to readers it withholds from; give '{target.Name}' an audience '{source.Name}' encloses, or disclose through a rule's own write",
                        refusal: RuleRefusal.TransformWidensAudience,
                        ruleName: ruleName
                    );
                }
            }
        }
    }
    private static string DescribeAudience(StateVisibility? visibility) {
        if (
            (visibility is null) ||
            visibility.IsPublic
        ) {
            return "public";
        }

        var readers = $"readers [{string.Join(separator: ", ", values: (visibility.Readers ?? []))}]";

        return ((visibility.ReadersFrom is { } live)
            ? $"{readers} and readersFrom '{live}'"
            : readers);
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
        LiveRow? fromRow = null;
        LiveRow? toRow = null;
        var sourceCost = RuleWork.Zero;

        resolved = null;
        RequireEnclosedAudience(
            context: context,
            ruleName: ruleName,
            transform: transform
        );

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

                    if ((sort.By is not { Count: >= 1 }) || sort.By.Any(predicate: key => (key is null))) {
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

                        if (!names.Add(item: name) || (name == target) ||
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
            case StateTransform.WriteSet writeSet when (DeclaredSet(
                context: context,
                member: "set",
                reference: writeSet.Set,
                ruleName: ruleName,
                verb: "writeSet"
            ) is { } declaredSet): {
                    var writeSetRow = writeSet.Row.Spelling;
                    var written = Row(name: writeSetRow);

                    if (writeSet.SetKey is not null) {
                        throw Invalid(message: $"writeSet reads declared set '{declaredSet.Name.Value}', which has no cell for 'setKey' to address");
                    }
                    if (
                        (written.EffectiveDomain is not StateDomain.CellsOf writtenBoard) ||
                        (context.FindTopology(name: writtenBoard.Topology) is not { } writtenTopology) ||
                        !written.TryAdmitWrite(
                        current: 0L,
                        operand: writeSet.Value,
                        reason: out _,
                        stored: out _,
                        write: StateWriteKind.Set
                    ) ||
                        ((written.Kind == CellKind.Bool) && (writeSet.Value is not (0 or 1)))
                    ) {
                        throw Invalid(message: "writeSet requires a board row and an admitted value");
                    }

                    sourceCost += CellSetReads(
                        cells: writtenTopology.CellCount,
                        context: context,
                        reads: reads,
                        ruleName: ruleName,
                        set: declaredSet
                    );
                    writes.Add(item: Ordinal(name: writeSetRow));
                    resolved = new ArenaTransform.WriteSet(
                        RowOrdinal: Ordinal(name: writeSetRow),
                        Set: declaredSet,
                        SetKey: default,
                        SetRowOrdinal: -1,
                        Value: writeSet.Value
                    );

                    break;
                }
            case StateTransform.WriteSet writeSet: {
                    var writeSetSet = writeSet.Set.Spelling;
                    var writeSetRow = writeSet.Row.Spelling;
                    var setSource = (context.FindRow(name: writeSetSet) ?? throw Invalid(message: $"writeSet 'set' names '{writeSetSet}', which is neither a state row nor a declared set"));
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

                    CellSetRow? leftSet = null;
                    CellSetRow? rightSet = null;

                    foreach (var (sourceChannel, member) in new[] { (combine.Left, "left"), (combine.Right, "right") }) {
                        if (sourceChannel is not { } source) {
                            continue;
                        }
                        if (DeclaredSet(
                            context: context,
                            member: member,
                            reference: source,
                            ruleName: ruleName,
                            verb: "boardCombine"
                        ) is { } declared) {
                            sourceCost += CellSetReads(
                                cells: targetTopology.CellCount,
                                context: context,
                                reads: reads,
                                ruleName: ruleName,
                                set: declared
                            );
                            if (member == "left") {
                                leftSet = declared;
                            } else {
                                rightSet = declared;
                            }

                            continue;
                        }

                        var sourceName = source.Spelling;

                        if (
                            ((context.FindRow(name: sourceName) ?? throw Invalid(message: $"boardCombine '{member}' names '{sourceName}', which is neither a state row nor a declared set"))
                            .EffectiveDomain is not StateDomain.CellsOf sourceBoard) ||
                            (sourceBoard.Topology != targetBoard.Topology)
                        ) {
                            throw Invalid(message: $"boardCombine source '{sourceName}' must be a board over '{targetBoard.Topology}' or a declared set");
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
                        LeftRowOrdinal: (((leftSet is null) && (combine.Left is { } leftChannel))
                        ? Ordinal(name: leftChannel.Spelling)
                        : -1),
                        LeftSet: leftSet,
                        Operation: combine.Operation,
                        RightRowOrdinal: (((rightSet is null) && (combine.Right is { } rightChannel))
                        ? Ordinal(name: rightChannel.Spelling)
                        : -1),
                        RightSet: rightSet,
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
                arena: resolved!,
                context: context,
                fromRow: fromRow,
                toRow: toRow
            ) + sourceCost),
            describe: DescribeTransform(transform: transform),
            fromRow: fromRow,
            keyRef: keyRef,
            reads: [.. reads],
            toRow: toRow,
            transform: transform,
            writes: [.. writes]
        );
    }
    // A transform operand that reads a set of positions names a board row or a declared cell set, and the two never
    // share a name: a plain name that is a declared set resolves to it, and one that is also a state row refuses.
    private static CellSetRow? DeclaredSet(RuleCompileContext context, StateChannelRef reference, string verb, string member, string ruleName) {
        if (
            (reference.Name is not { } name) ||
            !context.TryCellSet(
            name: name,
            set: out var declared
        )
        ) {
            return null;
        }
        if (context.FindRow(name: name) is not null) {
            throw new RuleException(
                detail: $"{verb} '{member}' names '{name}', which is both a state row and a declared set",
                refusal: RuleRefusal.EffectKindInadmissible,
                ruleName: ruleName
            );
        }

        return declared;
    }
    // Adds every row a declared set's sources read to the transform's reads and prices one lowering at the board's
    // width: each source visits its own positions (a token family visits the domain once per member), and each
    // operator visits the board once.
    private static RuleWork CellSetReads(RuleCompileContext context, CellSetRow set, int cells, List<int> reads, string ruleName) {
        RuleException Unknown(string what) => new(
            detail: $"declared set '{set.Name.Value}' reads {what}, which the section does not declare",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        );
        int SourceRow(CellName row, string what) {
            if (context.FindRow(name: row.Value) is null) {
                throw Unknown(what: $"{what} '{row.Value}'");
            }

            var ordinal = ResolveRowOrdinal(
                context: context,
                name: row.Value
            );

            reads.Add(item: ordinal);

            return ordinal;
        }
        long Walk(CellSetExpression expression) {
            switch (expression) {
                case CellSetExpression.Board board:
                    _ = SourceRow(
                        row: board.Row,
                        what: "board"
                    );

                    return BoardCells(
                        context: context,
                        row: context.FindRow(name: board.Row.Value)
                    );
                case CellSetExpression.Zone zone:
                    return context.RowCapacity(rowOrdinal: SourceRow(
                        row: zone.Row,
                        what: "zone"
                    ));
                case CellSetExpression.Family family: {
                        if (!context.Catalog.TryGetFamily(
                            family: out var range,
                            name: family.Name
                        )) {
                            throw Unknown(what: $"family '{family.Name.Value}'");
                        }

                        var units = 0L;

                        foreach (var ordinal in range.Ordinals()) {
                            reads.Add(item: ordinal);
                            if (
                                (context.FindRowAt(rowOrdinal: ordinal)?.EffectiveDomain is StateDomain.KeysOf keys) &&
                                (context.FindRow(name: keys.Row.Value) is not null)
                            ) {
                                units += context.RowCapacity(rowOrdinal: SourceRow(
                                    row: keys.Row,
                                    what: "token domain"
                                ));
                            } else {
                                units++;
                            }
                        }

                        return units;
                    }
                case CellSetExpression.Any any:
                    return (any.Items.Sum(selector: Walk) + cells);
                case CellSetExpression.Both both:
                    return (both.Items.Sum(selector: Walk) + cells);
                case CellSetExpression.Complement complement:
                    return (Walk(expression: complement.Item) + cells);
                default:
                    return cells;
            }
        }

        return RuleWork.Known(units: Walk(expression: set.Set));
    }
    // A board row's cell count, read from its topology; zero for a row that is not a board.
    private static int BoardCells(RuleCompileContext context, StateRow? row) => (((row?.EffectiveDomain is StateDomain.CellsOf board) && (context.FindTopology(name: board.Topology) is { } topology))
        ? topology.CellCount
        : 0
    );
    private static string DescribeTransform(StateTransform transform) => (transform switch {
        StateTransform.Transfer transfer => $"transformState Transfer {transfer.From} to {transfer.To} {transfer.Selector}",
        _ => $"transformState {transform.GetType().Name}",
    });
    // One firing's price beyond its authored sources: the fixed door, then what the resolved kernel leases, visits and
    // writes, every term sized by the rows and topology the transform addresses. See TransformWork for the unit.
    private static RuleWork TransformCost(ArenaTransform arena, RuleCompileContext context, LiveRow? fromRow, LiveRow? toRow) {
        long Cells(int rowOrdinal) => BoardCells(
            context: context,
            row: context.FindRowAt(rowOrdinal: rowOrdinal)
        );
        // A live end can select any row its table or family names; a literal end is its own row.
        int[] Ends(LiveRow? live, int own) {
            if (live is null) {
                return [own];
            }

            var rows = new List<CellAccess>();

            live.CollectRows(
                into: rows,
                isSet: false
            );

            return [.. rows.Select(selector: static access => access.RowOrdinal).Distinct()];
        }

        var board = TransformWork.BoardWrite;
        var door = TransformWork.Door;
        var work = arena switch {
            // Three leases, the board read, the walk (a step and two copies a cell), the accepted prefix, and a store
            // per cell of it.
            ArenaTransform.SetRay ray => RuleWork.Known(units: (Cells(rowOrdinal: ray.RowOrdinal) * ((7L + TransformWork.PatternStep(pattern: ray.Pattern)) + board))),
            ArenaTransform.PushRay push => PushRayWork(
                context: context,
                push: push
            ),
            ArenaTransform.Observe observe => ObserveWork(
                context: context,
                observe: observe
            ),
            // The declared-set lowering is the source price the caller adds; the kernel tests every cell of the
            // lowered set and stores each member. A mask reads one cell and walks at most its 64 bits.
            ArenaTransform.WriteSet write => ((write.Set is not null)
                ? (Cells(rowOrdinal: write.RowOrdinal) * (1L + board))
                : ((door + 64L) + (Math.Min(
                    val1: Cells(rowOrdinal: write.RowOrdinal),
                    val2: 64L
                ) * board))),
            // Three leases, one pass per source (a board read or a set's fill), the target's fill and combine, and a
            // store per cell.
            ArenaTransform.BoardCombine combine => (Cells(rowOrdinal: combine.RowOrdinal) * (((5L + (BoardCombination.NeedsLeft(operation: combine.Operation)
                ? 1L
                : 0L)) + (BoardCombination.NeedsRight(operation: combine.Operation)
                ? 1L
                : 0L)) + board)),
            ArenaTransform.ClearEnclosed enclosed => ClearEnclosedWork(
                context: context,
                enclosed: enclosed
            ),
            // The rank read, five leases, the domain lookup of every token, the relative order and the unranking (each
            // quadratic in at most twenty tokens), and the reorder.
            ArenaTransform.Arrange arrange => ArrangeWork(
                arrange: arrange,
                context: context
            ),
            // The order lease and its fill, a select and swap a position, the reorder, and the site's stream.
            ArenaTransform.Shuffle shuffle => ShuffleWork(
                context: context,
                shuffle: shuffle
            ),
            // Two leases, the row's word, the insertion sort, and the reorder.
            ArenaTransform.SortKeyed sort => ((((2L + door) * context.RowCapacity(rowOrdinal: sort.RowOrdinal)) + RuleWorkBudget.InsertionSortWork(
                count: context.RowCapacity(rowOrdinal: sort.RowOrdinal),
                keys: 1
            )) + TransformWork.Reorder(
                context: context,
                members: context.RowCapacity(rowOrdinal: sort.RowOrdinal),
                rowOrdinal: sort.RowOrdinal
            )),
            // A key column lease and read per attribute, the direction and order leases, the insertion sort, and the
            // reorder.
            ArenaTransform.SortZone sort => ((((((sort.By.Count * (1L + door)) + 1L) * context.RowCapacity(rowOrdinal: sort.RowOrdinal)) + sort.By.Count) + RuleWorkBudget.InsertionSortWork(
                count: context.RowCapacity(rowOrdinal: sort.RowOrdinal),
                keys: sort.By.Count
            )) + TransformWork.Reorder(
                context: context,
                members: context.RowCapacity(rowOrdinal: sort.RowOrdinal),
                rowOrdinal: sort.RowOrdinal
            )),
            ArenaTransform.Transfer transfer => TransferWork(
                context: context,
                froms: Ends(
                    live: fromRow,
                    own: transfer.FromRowOrdinal
                ),
                tos: Ends(
                    live: toRow,
                    own: transfer.ToRowOrdinal
                ),
                transfer: transfer
            ),
            _ => RuleWork.Unmodeled(reason: $"arena transform '{arena.GetType().Name}' has no work formula"),
        };

        return (TransformWork.Call + work);
    }
    // Three board-wide and five pool-wide leases, the snapshot and its occupancy words, linking each live token to its
    // cell, the walk, both passes over every token the walk meets (its read and its stop and push patterns), the run's
    // pattern, and every mover's read, step and store.
    private static long PushRayWork(RuleCompileContext context, ArenaTransform.PushRay push) {
        var pool = context.Catalog.Pools[push.PoolOrdinal];
        var cells = ((long)push.Topology.CellCount);
        var tokens = ((long)pool.Capacity);
        var door = TransformWork.Door;
        var leases = (((3L * cells) + (5L * tokens)) + 1L);
        var snapshot = (tokens + ((tokens + 63L) / 64L));
        var link = (tokens * (door + 1L));
        var walk = cells;
        var occupants = (tokens * ((door + 4L) + (TransformWork.PatternStep(pattern: push.StopPattern) + (2L * TransformWork.PatternStep(pattern: push.PushPattern)))));
        var run = ((tokens + 1L) * TransformWork.PatternStep(pattern: push.Pattern));
        var movers = (tokens * ((door + 1L) + TransformWork.LiveWrite(
            context: context,
            rowOrdinal: pool.Fields[push.CellFieldOrdinal].RowOrdinal
        )));

        return ((((((leases + snapshot) + link) + walk) + occupants) + run) + movers);
    }
    // A board projection leases and reads the mask and the source, hides every stamp, and stores and stamps each
    // visible cell. A token projection leases and reads the mask, hides every remembered token, then reads each
    // positioned token's cell and value and stores and stamps it.
    private static long ObserveWork(RuleCompileContext context, ArenaTransform.Observe observe) {
        var door = TransformWork.Door;
        var cells = (((context.FindRowAt(rowOrdinal: observe.MaskRowOrdinal)?.EffectiveDomain is StateDomain.CellsOf mask) && (context.FindTopology(name: mask.Topology) is { } topology))
            ? topology.CellCount
            : 0L
        );
        var stamp = (door + 1L);

        if (observe.PositionsRowOrdinal < 0) {
            return (((4L * cells) + (cells * (1L + stamp))) + (cells * ((1L + TransformWork.BoardWrite) + stamp)));
        }

        return ((((2L * cells) + (context.RowCapacity(rowOrdinal: observe.RowOrdinal) * (1L + stamp))) + (context.RowCapacity(rowOrdinal: observe.PositionsRowOrdinal) * (((1L + (2L * door)) + TransformWork.LiveWrite(
            context: context,
            rowOrdinal: observe.RowOrdinal
        )) + stamp))));
    }
    // A lease and the board read; the flood clears its marks, seeds one group per direction, visits each cell once and
    // tests its neighbours, and empties each enclosed member; then every cell is read back and each cleared one stored.
    private static long ClearEnclosedWork(RuleCompileContext context, ArenaTransform.ClearEnclosed enclosed) {
        var topology = ((context.FindRowAt(rowOrdinal: enclosed.RowOrdinal)?.EffectiveDomain is StateDomain.CellsOf board)
            ? context.FindTopology(name: board.Topology)
            : null
        );
        var cells = ((long)(topology?.CellCount ?? 0));
        var directions = ((long)(topology?.DirectionCount ?? 0));
        var flood = (((cells + directions) + (cells * (2L + (3L * directions)))) + cells);

        return (((2L * cells) + flood) + (cells * (TransformWork.Door + TransformWork.BoardWrite)));
    }
    private static long ArrangeWork(RuleCompileContext context, ArenaTransform.Arrange arrange) {
        var tokens = Math.Min(
            val1: context.RowCapacity(rowOrdinal: arrange.RowOrdinal),
            val2: StateReader.MaxArrangementTokens
        );
        var leases = ((3L * StateReader.MaxArrangementTokens) + (2L * tokens));
        var lookup = (tokens * (1L + context.RowCapacity(rowOrdinal: arrange.DomainRowOrdinal)));

        return (((((TransformWork.Door + leases) + lookup) + (3L * tokens)) + (2L * (tokens * tokens))) + TransformWork.Reorder(
            context: context,
            members: tokens,
            rowOrdinal: arrange.RowOrdinal
        ));
    }
    private static long ShuffleWork(RuleCompileContext context, ArenaTransform.Shuffle shuffle) {
        var members = context.RowCapacity(rowOrdinal: shuffle.RowOrdinal);
        var samples = Math.Max(
            val1: 0L,
            val2: (members - 1L)
        );

        return ((((2L * members) + (samples * 3L)) + TransformWork.Reorder(
            context: context,
            members: members,
            rowOrdinal: shuffle.RowOrdinal
        )) + TransformWork.Draws(
            context: context,
            rowOrdinal: shuffle.DrawRowOrdinal,
            samples: samples
        ));
    }
    // Every token is selected afresh (a sample for a random transfer), then either moves between the two zones or,
    // onto its own zone, costs an order lease, its fill and a reorder; a slice moves the keyed token's whole run and
    // reorders its own zone once. A removal closes its gap by shifting every later member down a cell, and the pile
    // shrinks by one each time, so n removals shift at most n x (F - 1) - n x (n - 1) / 2 cells; taking the last
    // member, or a slice walked back from its tail, shifts none. An insertion at the head shifts every member already
    // there; one at the tail shifts none. Either end may be live, so every pair of rows it can select is priced and
    // the costliest kept.
    private static long TransferWork(RuleCompileContext context, ArenaTransform.Transfer transfer, int[] froms, int[] tos) {
        var door = TransformWork.Door;
        var slice = (transfer.Selector == ZoneSelector.Slice);
        var random = (transfer.Selector == ZoneSelector.Random);
        var literal = ((froms.Length == 1) && (tos.Length == 1));
        var widest = 0L;

        foreach (var from in froms) {
            var fromMembers = context.RowCapacity(rowOrdinal: from);
            var tokens = Math.Min(
                val1: fromMembers,
                val2: (slice
                    ? fromMembers
                    : transfer.Count)
            );
            var removalShifts = (((transfer.Selector == ZoneSelector.Last) || (slice && transfer.InsertFirst))
                ? 0L
                : Math.Max(
                    val1: 0L,
                    val2: ((tokens * (fromMembers - 1L)) - ((tokens * (tokens - 1L)) / 2L))
                ));
            var reorder = ((2L * fromMembers) + TransformWork.Reorder(
                context: context,
                members: fromMembers,
                rowOrdinal: from
            ));
            var within = (slice
                ? reorder
                : (transfer.Count * reorder));

            foreach (var to in tos) {
                var toMembers = context.RowCapacity(rowOrdinal: to);
                var across = (((tokens * (door + TransformWork.Move(
                    context: context,
                    fromMembers: fromMembers,
                    fromOrdinal: from,
                    toMembers: toMembers,
                    toOrdinal: to
                ))) + (removalShifts * TransformWork.Cell(
                    context: context,
                    rowOrdinal: from
                ))) + (transfer.InsertFirst
                    ? ((tokens * Math.Max(
                        val1: 0L,
                        val2: (toMembers - 1L)
                    )) * TransformWork.Cell(
                        context: context,
                        rowOrdinal: to
                    ))
                    : 0L));
                var pair = (literal
                    ? ((from == to)
                        ? within
                        : across)
                    : Math.Max(
                        val1: within,
                        val2: across
                    ));

                widest = Math.Max(
                    val1: widest,
                    val2: pair
                );
            }
        }

        var selections = (slice
            ? door
            : (transfer.Count * door));

        return ((((2L * door) + selections) + widest) + (random
            ? TransformWork.Draws(
                context: context,
                rowOrdinal: transfer.DrawRowOrdinal,
                samples: transfer.Count
            )
            : 0L));
    }
}

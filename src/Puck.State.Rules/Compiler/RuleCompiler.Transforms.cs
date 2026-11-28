using Puck.Maths;

namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Adds every state-row ordinal read by a declared set, including family members and their token
    /// domains, using the same source walk as transform compilation.</summary>
    /// <param name="context">The validated document's compile context.</param>
    /// <param name="set">The declared set.</param>
    /// <param name="reads">The list receiving read ordinals.</param>
    public static void CollectCellSetReads(RuleCompileContext context, CellSetRow set, List<int> reads) =>
        _ = CellSetReads(cells: 0, context: context, reads: reads, ruleName: string.Empty, set: set);
    /// <summary>Adds every state-row ordinal a resolved transform reads or writes: its subjects and, beyond them, the
    /// rows a live key, a zone's token domain, a declared set or a pool's fields address. These are the rows the
    /// arena's audience check walks before the kernel runs, from the same access walk.</summary>
    /// <param name="transform">The authored transform.</param>
    /// <param name="context">The compile context whose catalog names resolve against.</param>
    /// <param name="rows">The list receiving row ordinals; reads and writes alike, possibly repeated.</param>
    /// <param name="reason">Why the transform's shape was refused, or empty on success.</param>
    /// <returns><see langword="true"/> when the transform's shape resolved; <see langword="false"/> adds nothing.</returns>
    /// <remarks>Collects before the audience boundary (<see cref="RequireEnclosedAudience"/>) is judged, so a caller
    /// deciding whether those rows may be inspected at all asks before anything reads their cell policies.</remarks>
    public static bool TryCollectTransformRows(StateTransform transform, RuleCompileContext context, List<int> rows, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: rows);
        var start = rows.Count;
        try {
            var effect = ResolveStateTransformCore(context: context, resolved: out var resolved, ruleName: string.Empty, transform: transform);
            var reads = new List<CellAccess>();
            effect.CollectReads(into: reads);
            effect.CollectWrites(into: reads);
            foreach (var access in reads) { rows.Add(item: access.RowOrdinal); }
            if (resolved is not null) {
                ArenaTransforms.CollectAccesses(binding: ArenaTransformBinding.None, catalog: context.Catalog, reads: rows, transform: resolved, writes: rows);
            }
            reason = string.Empty;
            return true;
        } catch (RuleException exception) {
            rows.RemoveRange(index: start, count: (rows.Count - start));
            reason = exception.Message;
            return false;
        }
    }
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
    // arrangement its rank. So no row it writes may be seen by a reader some row it reads withholds from, judged here
    // over the declared row policies alone (StateVisibility.Encloses). Cell policies are values: a state command sets
    // or clears one without recompiling a rule, so a compile-time judgement over the cells seeded now would hold the
    // document's validity hostage to its current members, and a recipient's unrelated edit, which revalidates the whole
    // document, would read them. The arena judges every current cell and containing-zone policy before the kernel
    // runs (StateArena.ReadAudienceEncloses) and refuses the firing by the same name. A transform never declassifies;
    // a rule that means to show a hidden value writes it through its own setState. Resolved dependencies include live
    // keys, zone selectors, declared sets and writes that also read.
    /// <summary>Refuses a derived result whose declared output audience exceeds any input's declared row policy.
    /// Transforms and searches share this compile-time boundary; current cell policies are the arena's to judge at
    /// the firing.</summary>
    public static void RequireEnclosedAudience(IReadOnlyList<int> reads, IReadOnlyList<int> writes, string ruleName, RuleCompileContext context,
        RuleRefusal code = RuleRefusal.TransformWidensAudience) {
        foreach (var written in writes) {
            if (context.FindRowAt(rowOrdinal: written) is not { } target) {
                continue;
            }

            foreach (var read in reads) {
                if (context.FindRowAt(rowOrdinal: read) is not { } source) {
                    continue;
                }

                if (!StateVisibility.Encloses(audience: target.Visibility, policy: source.Visibility)) {
                    throw new RuleException(
                        detail: $"it writes '{target.Name}' ({DescribeAudience(visibility: target.Visibility)}) from '{source.Name}' ({DescribeAudience(visibility: source.Visibility)}), so what it writes would show '{source.Name}' to readers it withholds from; give '{target.Name}' an audience '{source.Name}' encloses, or disclose through a rule's own write",
                        refusal: code,
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
        var effect = ResolveStateTransformCore(context: context, resolved: out resolved, ruleName: ruleName, transform: transform);

        if (transform is StateTransform.Observe) { return effect; }
        var readAccesses = new List<CellAccess>();
        var writeAccesses = new List<CellAccess>();

        effect.CollectReads(into: readAccesses);
        effect.CollectWrites(into: writeAccesses);
        var reads = readAccesses.Select(selector: static access => access.RowOrdinal).Distinct().ToList();
        var writes = writeAccesses.Select(selector: static access => access.RowOrdinal).Distinct().ToList();

        if (resolved is not null) {
            // A scalar transform's audience reads leave out the rows its live ends select, and SettleDisclosure settles
            // them below; every other transform checks each row it reads.
            if (effect is not TransformStateEffect) { resolved.DisclosureReads = reads.ToArray(); }
            ArenaTransforms.CollectAccesses(transform: resolved, catalog: context.Catalog, binding: ArenaTransformBinding.None, reads: reads, writes: writes);
        }
        // A tally's cells are its engine-owned table, which only its own add and reset write.
        foreach (var ordinal in reads.Concat(writes)) {
            if (context.FindRowAt(rowOrdinal: ordinal) is { EffectiveDomain: StateDomain.Tally } tally) {
                throw new RuleException(detail: $"a transform addresses tally '{tally.Name.Value}', whose cells are the engine's table", refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName);
            }
        }
        RequireEnclosedAudience(context: context, reads: reads, ruleName: ruleName, writes: writes);
        if (effect is TransformStateEffect scalar) {
            SettleDisclosure(context: context, controls: [], scalar: scalar);
        }
        if (effect is VectorEffect vector) { vector.DisclosureCost = DisclosureCost(context: context, reads: reads, writes: writes); }
        return effect;
    }
    private static IRuleEffect ResolveStateTransformCore(StateTransform transform, string ruleName, RuleCompileContext context, out ArenaTransform? resolved) {
        RuleException Invalid(string message) => new(
            detail: message,
            refusal: RuleRefusal.EffectKindInadmissible,
            ruleName: ruleName
        );
        StateRowDeclaration Row(string name) => (context.FindRow(name: name) ?? throw Invalid(message: $"unknown state row '{name}'"));
        StateRowDeclaration RowAt(int ordinal, string name) => (context.FindRowAt(rowOrdinal: ordinal) ?? throw Invalid(message: $"unknown state row '{name}'"));
        // A row end: a family member the binding selects live (its first candidate's ordinal standing for every member's
        // shape), or the named row's ordinal.
        int LiveEnd(string name, string where, out LiveRow? live) => (TryResolveLiveRow(context: context, name: name, row: out live, ruleName: ruleName, where: where)
            ? FirstCandidate(row: live!)
            : ResolveRowOrdinal(context: context, name: Row(name: name).Name.Value));
        // Whether two topologies are one lattice in two places: members of a family of boards each over their own
        // anchored copy of one grid.
        bool SameShape(string left, string right) => ((context.FindTopology(name: left) is { } a) && (context.FindTopology(name: right) is { } b) && CompiledTopology.SameShape(left: a, right: b));
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
                    // A live row (a family member the binding selects) is substituted per firing, as a transfer's end
                    // is; every row it can select is checked for the shape here.
                    var shuffleRows = new List<string>();

                    if (TryResolveLiveRow(context: context, name: shuffleRow, row: out fromRow, ruleName: ruleName, where: "shuffle")) {
                        var candidates = new List<CellAccess>();

                        fromRow!.CollectRows(into: candidates, isSet: true);
                        shuffleRows.AddRange(collection: candidates.Select(selector: access => context.Catalog.Descriptors[access.RowOrdinal].Name).Distinct());
                    } else {
                        shuffleRows.Add(item: shuffleRow);
                    }

                    if (
                        (shuffleRows.Count == 0) ||
                        shuffleRows.Exists(match: name => (Row(name: name) is not { Shape: RowShape.Keyed or RowShape.Ordered })) ||
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
                    if (fromRow is null) {
                        writes.Add(item: Ordinal(name: shuffleRow));
                    }
                    resolved = new ArenaTransform.Shuffle(
                        DrawRowOrdinal: Ordinal(name: shuffleDrawName),
                        RowOrdinal: ((fromRow is null) ? Ordinal(name: shuffleRow) : -1)
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
                    var combineOrdinal = LiveEnd(name: combineRow, live: out toRow, where: "boardCombine 'row'");
                    var target = RowAt(ordinal: combineOrdinal, name: combineRow);

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
                        out var combineDirection,
                        out var combineElement,
                        out var combineValue,
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
                        LiveRow? sourceLive = null;
                        var sourceOrdinal = ((member == "left")
                            ? LiveEnd(name: sourceName, live: out sourceLive, where: "boardCombine 'left'")
                            : -1);

                        var sourceRow = (((sourceLive is not null) ? context.FindRowAt(rowOrdinal: sourceOrdinal) : context.FindRow(name: sourceName))
                            ?? throw Invalid(message: $"boardCombine '{member}' names '{sourceName}', which is neither a state row nor a declared set"));

                        if (
                            (sourceRow.EffectiveDomain is not StateDomain.CellsOf sourceBoard) ||
                            ((sourceBoard.Topology != targetBoard.Topology) && !SameShape(left: sourceBoard.Topology, right: targetBoard.Topology))
                        ) {
                            throw Invalid(message: $"boardCombine source '{sourceName}' must be a board over '{targetBoard.Topology}' or a declared set");
                        }

                        if (sourceLive is not null) {
                            fromRow = sourceLive;
                        } else {
                            reads.Add(item: Ordinal(name: sourceName));
                        }
                    }

                    if (toRow is null) {
                        writes.Add(item: combineOrdinal);
                    }
                    resolved = new ArenaTransform.BoardCombine(
                        Direction: combineDirection,
                        Element: combineElement,
                        LeftRowOrdinal: (((leftSet is null) && (combine.Left is { } leftChannel))
                        ? ((fromRow is not null) ? FirstCandidate(row: fromRow) : Ordinal(name: leftChannel.Spelling))
                        : -1),
                        LeftSet: leftSet,
                        Operation: combine.Operation,
                        RightRowOrdinal: (((rightSet is null) && (combine.Right is { } rightChannel))
                        ? Ordinal(name: rightChannel.Spelling)
                        : -1),
                        RightSet: rightSet,
                        RowOrdinal: combineOrdinal,
                        Value: combineValue
                    );

                    break;
                }
            case StateTransform.CopyRow copy: {
                    // A live end selects one member of a family per firing (ArenaTransformBinding), and its first
                    // candidate answers the compile-time questions every member shares.
                    var copyIntoOrdinal = LiveEnd(name: copy.Row.Spelling, live: out toRow, where: "copyRow 'row'");
                    var copyFromOrdinal = LiveEnd(name: copy.From.Spelling, live: out fromRow, where: "copyRow 'from'");
                    var copyRow = RowAt(ordinal: copyIntoOrdinal, name: copy.Row.Spelling);
                    var copyFrom = RowAt(ordinal: copyFromOrdinal, name: copy.From.Spelling);

                    if (!copyRow.IsKeyed || !copyFrom.IsKeyed || (copyRow.Kind != copyFrom.Kind) ||
                        (copyRow.Kind is not (CellKind.Int or CellKind.Fixed or CellKind.Bool))) {
                        throw Invalid(message: "copyRow requires two keyed numeric rows of one kind");
                    }

                    if (fromRow is null) {
                        reads.Add(item: copyFromOrdinal);
                    }
                    if (toRow is null) {
                        writes.Add(item: copyIntoOrdinal);
                    }
                    resolved = new ArenaTransform.CopyRow(
                        FromRowOrdinal: copyFromOrdinal,
                        RowOrdinal: copyIntoOrdinal
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
                    var boundaryLower = enclosed.BoundaryLower ?? enclosedBoard.Empty;
                    var boundaryUpper = enclosed.BoundaryUpper ?? enclosedBoard.Empty;
                    if (boundaryLower > boundaryUpper) {
                        throw Invalid(message: "clearEnclosed requires an ordered boundary range");
                    }

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
                        BoundaryLower: boundaryLower,
                        BoundaryUpper: boundaryUpper,
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
        // A board source reads its planes' words (or every cell, for a board that keeps none) into the set's words, and
        // an operator combines the sets a word at a time.
        var words = BitWords.WordsFor(members: cells);

        long Walk(CellSetExpression expression) {
            switch (expression) {
                case CellSetExpression.Board board: {
                        var ordinal = SourceRow(
                            row: board.Row,
                            what: "board"
                        );

                        return (words + TransformWork.BoardRangeRead(
                            cells: BoardCells(
                                context: context,
                                row: context.FindRow(name: board.Row.Value)
                            ),
                            context: context,
                            lower: board.Low,
                            rowOrdinal: ordinal,
                            upper: board.High
                        ));
                    }
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
                    return (any.Items.Sum(selector: Walk) + (any.Items.Count * words));
                case CellSetExpression.Both both:
                    return (both.Items.Sum(selector: Walk) + (both.Items.Count * words));
                case CellSetExpression.Complement complement:
                    return (Walk(expression: complement.Item) + words);
                default:
                    return words;
            }
        }

        return RuleWork.Known(units: Walk(expression: set.Set));
    }
    // A board row's cell count, read from its topology; zero for a row that is not a board.
    private static int BoardCells(RuleCompileContext context, StateRowDeclaration? row) => (((row?.EffectiveDomain is StateDomain.CellsOf board) && (context.FindTopology(name: board.Topology) is { } topology))
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
        long CellWords(int rowOrdinal) => BitWords.WordsFor(members: BoardCells(
            context: context,
            row: context.FindRowAt(rowOrdinal: rowOrdinal)
        ));
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
            ArenaTransform.SetRay ray => RuleWork.Known(units: (context.FindRowAt(ray.RowOrdinal)?.EffectiveDomain is StateDomain.CellsOf rayBoard
                ? (context.FindTopology(rayBoard.Topology)?.LongestRay(ray.Direction) ?? 0)
                : 0) * (7L + TransformWork.PatternStep(pattern: ray.Pattern) + board)),
            ArenaTransform.PushRay push => PushRayWork(
                context: context,
                push: push
            ),
            ArenaTransform.Observe observe => ObserveWork(
                context: context,
                observe: observe
            ),
            // The declared-set lowering is the source price the caller adds; the kernel copies the lowered set's words
            // and hands them to one set write. A mask reads one cell and writes at most its 64 bits.
            ArenaTransform.WriteSet write => ((write.Set is not null)
                ? (CellWords(rowOrdinal: write.RowOrdinal) + TransformWork.BoardCellsWrite(
                    cells: Cells(rowOrdinal: write.RowOrdinal),
                    words: CellWords(rowOrdinal: write.RowOrdinal)
                ))
                : (door + TransformWork.BoardCellsWrite(
                    cells: Math.Min(val1: Cells(rowOrdinal: write.RowOrdinal), val2: 64L),
                    words: 1L
                ))),
            ArenaTransform.BoardCombine combine => BoardCombineWork(
                combine: combine,
                context: context,
                lefts: Ends(live: fromRow, own: combine.LeftRowOrdinal)
            ),
            ArenaTransform.ClearEnclosed enclosed => ClearEnclosedWork(
                context: context,
                enclosed: enclosed
            ),
            // A read of every cell the source can hold, and a live store of each into the destination.
            ArenaTransform.CopyRow copy => (context.RowCapacity(rowOrdinal: copy.FromRowOrdinal) * (door + TransformWork.LiveWrite(
                context: context,
                rowOrdinal: copy.RowOrdinal
            ))),
            // The rank read, five leases, the domain lookup of every token, the relative order and the unranking (each
            // quadratic in at most twenty tokens), and the reorder.
            ArenaTransform.Arrange arrange => ArrangeWork(
                arrange: arrange,
                context: context
            ),
            // The order lease and its fill, a select and swap a position, the reorder, and the site's stream.
            ArenaTransform.Shuffle shuffle => ShuffleWork(
                rows: Ends(live: fromRow, own: shuffle.RowOrdinal),
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
        var snapshot = (tokens + tokens.CeilingDivide(divisor: 64L));
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
    // The flood reads its group and liberty cells from the board's planes (or every cell, for a board that keeps none)
    // and visits each cell at most once over two cell bitsets, then hands the cleared cells to one set write.
    private static long ClearEnclosedWork(RuleCompileContext context, ArenaTransform.ClearEnclosed enclosed) {
        var topology = BoardTopology(context: context, rowOrdinal: enclosed.RowOrdinal);
        var cells = (topology?.CellCount ?? 0);
        var directions = ((long)(topology?.DirectionCount ?? 0));
        var words = (long)BitWords.WordsFor(members: cells);
        var reads = (TransformWork.BoardRangeRead(cells: cells, context: context, lower: enclosed.Lower, rowOrdinal: enclosed.RowOrdinal, upper: enclosed.Upper)
            + TransformWork.BoardRangeRead(cells: cells, context: context, lower: enclosed.BoundaryLower, rowOrdinal: enclosed.RowOrdinal, upper: enclosed.BoundaryUpper));

        return (((directions + (5L * words)) + reads) + (cells * (5L + (3L * directions)))) + TransformWork.BoardCellsWrite(cells: cells, words: words);
    }
    // Three word leases, each source's member cells (a board's planes, or a declared set's words beside the lowering
    // the caller prices), the algebra on the words, and the two set writes that split the board's cells between the
    // members and the rest. A copy carries values instead: a plane read and a set write per value of its source's
    // window, then the stores of every cell once; a source that keeps no planes is read and written a cell at a time.
    // A stored value lies inside its row's declared window, since every write is admitted against it, so no cell
    // falls outside a planed source's planes.
    private static long BoardCombineWork(RuleCompileContext context, ArenaTransform.BoardCombine combine, int[] lefts) {
        if (BoardTopology(context: context, rowOrdinal: combine.RowOrdinal) is not { } topology) {
            return 0L;
        }

        var cells = topology.CellCount;
        var words = (long)topology.CellWords;

        if ((combine.Operation == BoardCombineOp.Copy) && (combine.LeftSet is null)) {
            return lefts.Max(selector: left => (TransformWork.TryPlaneWindow(context: context, lower: out var lower, rowOrdinal: left, width: out var width)
                ? (((3L * words) + (width * ((TransformWork.BoardRangeRead(cells: cells, context: context, lower: lower, rowOrdinal: left, upper: lower) + TransformWork.BoardCellsDoor(words: words)) + words))) + TransformWork.BoardCellStores(cells: cells))
                : (cells * (1L + TransformWork.BoardWrite))));
        }

        long Members(int rowOrdinal, CellSetRow? set) {
            if (set is not null) {
                return words;
            }

            var empty = ((context.FindRowAt(rowOrdinal: rowOrdinal)?.EffectiveDomain is StateDomain.CellsOf source) ? source.Empty : 0L);

            return ((2L * words) + TransformWork.BoardRangeRead(cells: cells, context: context, lower: empty, rowOrdinal: rowOrdinal, upper: empty));
        }

        var left = (BoardCombination.NeedsLeft(operation: combine.Operation)
            ? lefts.Max(selector: row => Members(rowOrdinal: row, set: combine.LeftSet))
            : 0L);
        var right = (BoardCombination.NeedsRight(operation: combine.Operation)
            ? Members(rowOrdinal: combine.RightRowOrdinal, set: combine.RightSet)
            : 0L);

        return ((((3L * words) + left) + right) + BoardCombination.MembersWork(direction: combine.Direction, operation: combine.Operation, topology: topology))
            + ((2L * TransformWork.BoardCellsDoor(words: words)) + words + TransformWork.BoardCellStores(cells: cells));
    }
    private static CompiledTopology? BoardTopology(RuleCompileContext context, int rowOrdinal) => ((context.FindRowAt(rowOrdinal: rowOrdinal)?.EffectiveDomain is StateDomain.CellsOf board)
        ? context.FindTopology(name: board.Topology)
        : null
    );
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
    // The dearest row the shuffle can permute: its own, or each a live row can select.
    private static long ShuffleWork(RuleCompileContext context, ArenaTransform.Shuffle shuffle, int[] rows) => rows.Max(selector: row => {
        var members = context.RowCapacity(rowOrdinal: row);
        var samples = Math.Max(
            val1: 0L,
            val2: (members - 1L)
        );

        return ((((2L * members) + (samples * 3L)) + TransformWork.Reorder(
            context: context,
            members: members,
            rowOrdinal: row
        )) + TransformWork.Draws(
            context: context,
            rowOrdinal: shuffle.DrawRowOrdinal,
            samples: samples
        ));
    });
    // Price stable-slot moves or an in-row permutation for the costliest admitted row pair.
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
                var across = tokens * (door + TransformWork.Move(context: context, fromMembers: fromMembers,
                    fromOrdinal: from, toMembers: toMembers, toOrdinal: to));
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

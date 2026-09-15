using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Pure candidate composition for bounded state operations. No effect, random cursor, or phase progression
/// escapes a refused candidate; the ordinary mutation pipeline validates, installs and journals the result. A refusal
/// is a returned reason, never a thrown one: a rule may retry a refused transform every tick.</summary>
public static partial class WorldStateTransforms {
    /// <summary>Lists every state row whose edit capability an operation needs.</summary>
    /// <param name="transform">The operation.</param>
    /// <returns>The addressed row names.</returns>
    public static IEnumerable<string> Subjects(StateTransform transform) => transform switch {
        StateTransform.Transfer transfer => ((transfer.Draw is null)
        ? [transfer.From, transfer.To]
        : [transfer.From, transfer.To, transfer.Draw]),
        StateTransform.SetRay ray => [ray.Row],
        StateTransform.Observe observe => [observe.Row],
        StateTransform.Shuffle shuffle => [shuffle.Row, shuffle.Draw],
        StateTransform.SortZone sortZone => [sortZone.Row, .. sortZone.By.Select(selector: key => key.Row)],
        StateTransform.SortKeyed sortKeyed => [sortKeyed.Row],
        StateTransform.WriteSet writeSet => [writeSet.Row],
        StateTransform.BoardCombine combine => [combine.Row],
        StateTransform.Arrange arrange => [arrange.Row],
        StateTransform.Push push => [push.Row],
        StateTransform.ClearEnclosed enclosed => [enclosed.Row],
        StateTransform.Mix mix => CollectMixSubjects(mix: mix),
        StateTransform.Mean mean => CollectMeanSubjects(mean: mean),
        StateTransform.Nearest nearest => CollectNearestSubjects(nearest: nearest),
        StateTransform.Remember remember => CollectRememberSubjects(remember: remember),
        _ => [],
    };

    private static IEnumerable<string> CollectMixSubjects(StateTransform.Mix mix) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        AddSpellingSubject(spelling: mix.Into, names: names);
        return names;
    }

    private static IEnumerable<string> CollectMeanSubjects(StateTransform.Mean mean) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        AddSpellingSubject(spelling: mean.Into, names: names);
        return names;
    }

    private static IEnumerable<string> CollectNearestSubjects(StateTransform.Nearest nearest) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        AddSpellingSubject(spelling: nearest.Into, names: names);
        return names;
    }

    private static IEnumerable<string> CollectRememberSubjects(StateTransform.Remember remember) {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        AddSpellingSubject(spelling: remember.Into, names: names);
        return names;
    }

    private static void AddSpellingSubject(string? spelling, ISet<string> names) {
        if (string.IsNullOrWhiteSpace(value: spelling)) {
            return;
        }
        spelling = spelling.Trim();
        if (spelling.StartsWith(value: "vector(", comparisonType: StringComparison.Ordinal)) {
            return;
        }
        var bracketIndex = spelling.IndexOf(value: '[');
        if (bracketIndex > 0 && spelling.EndsWith(value: ']')) {
            var rowName = spelling[..bracketIndex].Trim();
            if (rowName.Length > 0) {
                names.Add(item: rowName);
            }
            return;
        }
        names.Add(item: spelling);
    }
    /// <summary>Composes one operation without changing the supplied definition.</summary>
    /// <param name="definition">The validated current definition.</param>
    /// <param name="transform">The operation.</param>
    /// <param name="actor">The stamped acting principal.</param>
    /// <param name="tick">The current simulation tick.</param>
    /// <param name="instance">The authoritative instance identity used by draw streams.</param>
    /// <param name="candidate">The new definition, or the original on refusal.</param>
    /// <param name="reason">The refusal reason.</param>
    /// <param name="patterns">The document's compiled patterns, for <see cref="StateTransform.SetRay"/>; empty
    /// when the caller has none compiled (every other transform ignores it).</param>
    /// <returns>Whether the operation composed.</returns>
    public static bool TryApply(WorldDefinition definition, StateTransform transform, WorldPrincipal actor,
        ulong tick, string instance, out WorldDefinition candidate, out string reason, CompiledPatterns? patterns = null) {
        candidate = definition;
        var rows = definition.State.ToArray();
        bool composed;

        try {
            composed = transform switch {
                StateTransform.Observe observe => TryObserve(
                actor: actor,
                definition: definition,
                operation: observe,
                reason: out reason,
                rows: rows,
                tick: tick
            ),
                StateTransform.Transfer transfer => TryTransfer(
                definition: definition,
                instance: instance,
                reason: out reason,
                rows: rows,
                transfer: transfer
            ),
                StateTransform.SetRay ray => TrySetRay(
                definition,
                rows,
                ray,
                (patterns ?? CompiledPatterns.Empty),
                out reason
            ),
                StateTransform.Shuffle shuffle => TryShuffle(
                definition: definition,
                instance: instance,
                reason: out reason,
                rows: rows,
                shuffle: shuffle
            ),
                StateTransform.SortZone sortZone => TrySortZone(
                reason: out reason,
                rows: rows,
                sort: sortZone
            ),
                StateTransform.SortKeyed sortKeyed => TrySortKeyed(
                reason: out reason,
                rows: rows,
                sort: sortKeyed
            ),
                StateTransform.WriteSet writeSet => TryWriteSet(
                definition: definition,
                reason: out reason,
                rows: rows,
                writeSet: writeSet
            ),
                StateTransform.BoardCombine combine => TryBoardCombine(
                combine: combine,
                definition: definition,
                reason: out reason,
                rows: rows
            ),
                StateTransform.Arrange arrange => TryArrange(
                arrange: arrange,
                reason: out reason,
                rows: rows
            ),
                StateTransform.Push push => TryPush(
                push: push,
                reason: out reason,
                rows: rows
            ),
                StateTransform.ClearEnclosed enclosed => TryClearEnclosed(
                definition: definition,
                enclosed: enclosed,
                reason: out reason,
                rows: rows
            ),
                StateTransform.Mix mix => TryMix(
                definition: definition,
                mix: mix,
                reason: out reason,
                rows: rows
            ),
                StateTransform.Mean mean => TryMean(
                definition: definition,
                mean: mean,
                reason: out reason,
                rows: rows
            ),
                StateTransform.Nearest nearest => TryNearest(
                definition: definition,
                nearest: nearest,
                reason: out reason,
                rows: rows
            ),
                StateTransform.Remember remember => TryRemember(
                definition: definition,
                remember: remember,
                reason: out reason,
                rows: rows
            ),
                _ => Refuse(
                message: "unknown state transform",
                reason: out reason
            ),
            };
        } catch (OverflowException exception) {
            // The phase generation is checked; wrapping it silently would fork replay.
            reason = exception.Message;
            return false;
        }

        if (composed) {
            candidate = definition.WithWorldState(rows: rows);
        }

        return composed;
    }
    /// <summary>Checks a submitted guard's generation against its phase row.</summary>
    /// <param name="definition">The current definition.</param>
    /// <param name="guard">The submitted guard.</param>
    /// <param name="actor">The authenticated actor.</param>
    /// <returns>Whether the guard matches: the sole condition a mutation's guard checks.</returns>
    public static bool CanAct(WorldDefinition definition, PhaseGuard guard, WorldPrincipal actor) {
        var phase = WorldDefinitionRows.FindStateRow(
            definition.State,
            guard.Row
        )?.Phase;

        return (
            (phase is not null) &&
            (phase.Sequence == guard.Sequence) &&
            ((guard.Participant is null) || (actor == WorldPrincipal.World))
        );
    }
    /// <summary>Advances a phase row's generation by one: the completion half of a guarded submission. Called by the
    /// mutation pipeline after a mutation carrying a matching <see cref="PhaseGuard"/> succeeds.</summary>
    /// <param name="definition">The definition the guarded mutation just produced.</param>
    /// <param name="row">The phase row named by the guard.</param>
    /// <returns>The definition with that row's generation advanced.</returns>
    public static WorldDefinition Advance(WorldDefinition definition, string row) {
        var rows = definition.State.ToArray();

        for (var index = 0; (index < rows.Length); index++) {
            if (
                (rows[index].Name.Value == row) &&
                (rows[index].Phase is { } phase)
            ) {
                rows[index] = rows[index] with { Phase = phase with { Sequence = checked((phase.Sequence + 1)) } };
                break;
            }
        }

        return definition.WithWorldState(rows: rows);
    }

    private static bool Refuse(string message, out string reason) {
        reason = message;
        return false;
    }
    private static bool TryFind(WorldStateRow[] rows, string name, out int index, out string reason) {
        for (index = 0; (index < rows.Length); index++) {
            if (rows[index].Name.Value == name) {
                reason = string.Empty;
                return true;
            }
        }

        index = -1;
        reason = $"state row '{name}' does not exist";
        return false;
    }
    // The redrawable integer streamDraw site a transfer or shuffle samples from, with its seed and stream resolved.
    private static bool TryResolveDrawSite(WorldDefinition definition, WorldStateRow site, string instance, string verb,
        out StateGenerator generator, out Draw draw, out ulong seed, out ulong stream, out string reason) {
        generator = default!;
        seed = default;
        stream = 0;

        if (
            (site.Draw is not { Timing: not DrawTiming.Boot } declared) ||
            (site.Kind != CellKind.Int) ||
            !GeneratorEngine.TryResolveSource(
            generators: definition.Generators,
            draw: declared,
            generator: out generator,
            reason: out _
        ) ||
            (generator.Source != GeneratorSource.StreamDraw)
        ) {
            draw = default!;
            return Refuse(
                message: $"{verb} requires a redrawable integer streamDraw site",
                reason: out reason
            );
        }

        draw = declared;
        var descriptor = WorldDrawSites.StateRow(rowName: site.Name);

        seed = GeneratorEngine.ComputeSeedState(
            documentSeed: (definition.Generation?.WorldSeed ?? 0),
            instanceIdentity: instance,
            site: descriptor
        );
        stream = GeneratorEngine.ComputeStreamId(site: descriptor);
        reason = string.Empty;
        return true;
    }
    private static bool TryTransfer(WorldDefinition definition, WorldStateRow[] rows, StateTransform.Transfer transfer, string instance, out string reason) {
        if (
            !TryFind(
            rows,
            transfer.From,
            out var from,
            out reason
        ) ||
            !TryFind(
            rows,
            transfer.To,
            out var to,
            out reason
        )
        ) {
            return false;
        }
        var source = rows[from];
        var destination = rows[to];

        if (
            (source.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } sourceZone) ||
            (destination.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } destinationZone) ||
            (sourceZone.Row != destinationZone.Row) ||
            !Enum.IsDefined(value: transfer.Selector)
        ) {
            return Refuse(
                message: "transfer requires zones in one token domain and a defined selector",
                reason: out reason
            );
        }
        if (
            ((transfer.Selector is ZoneSelector.Key or ZoneSelector.Slice) != (transfer.Key is not null)) ||
            ((transfer.Selector == ZoneSelector.Random) != (transfer.Draw is not null))
        ) {
            return Refuse(
                message: "key and slice selection require only key; random selection requires only draw",
                reason: out reason
            );
        }
        if (
            (transfer.Count < 1) ||
            (transfer.Count > StateTransferCapacity.MaxTransferCount) ||
            ((transfer.Selector is ZoneSelector.Key or ZoneSelector.Slice) && (transfer.Count != 1))
        ) {
            return Refuse(
                message: $"transfer count must be 1..{StateTransferCapacity.MaxTransferCount}, and exactly 1 for a key or slice selection",
                reason: out reason
            );
        }
        var cells = (source.Cells ?? []).ToList();

        if (transfer.Selector == ZoneSelector.Slice) {
            return TrySlice(
                cells: cells,
                destination: destination,
                from: from,
                reason: out reason,
                rows: rows,
                source: source,
                to: to,
                transfer: transfer
            );
        }
        if (cells.Count < transfer.Count) {
            return Refuse(
                message: ((cells.Count == 0)
                ? "source zone is empty"
                : $"source zone holds {cells.Count} tokens, fewer than the {transfer.Count} to transfer"),
                reason: out reason
            );
        }
        var target = ((from == to)
            ? cells
            : (destination.Cells ?? []).ToList()
        );

        if (
            (from != to) &&
            ((target.Count + transfer.Count) > (destination.Capacity ?? destination.CellCeiling))
        ) {
            return Refuse(
                message: "destination zone is full",
                reason: out reason
            );
        }
        var drawIndex = -1;
        WorldStateRow? site = null;
        StateGenerator generator = default!;
        Draw draw = default!;
        var seed = 0UL;
        var stream = 0UL;
        var cursor = 0L;
        var lastSample = 0L;

        if (transfer.Selector == ZoneSelector.Random) {
            if (!TryFind(
                rows,
                transfer.Draw!,
                out drawIndex,
                out reason
            )) {
                return false;
            }
            site = rows[drawIndex];
            if (!TryResolveDrawSite(
                definition: definition,
                draw: out draw,
                generator: out generator,
                instance: instance,
                reason: out reason,
                seed: out seed,
                site: site,
                stream: out stream,
                verb: "random transfer"
            )) {
                return false;
            }
            cursor = site.DrawCursor;
        }
        // Each token is selected afresh from what remains, so a random transfer samples once per token and a
        // positional transfer walks the pile from its chosen end.
        for (var moved = 0; (moved < transfer.Count); moved++) {
            var selected = transfer.Selector switch {
                ZoneSelector.First => 0,
                ZoneSelector.Last => (cells.Count - 1),
                ZoneSelector.Key => cells.FindIndex(match: c => (c.Key.Value == transfer.Key)),
                _ => 0,
            };

            if (transfer.Selector == ZoneSelector.Random) {
                if (!GeneratorEngine.TryFire(
                    generator,
                    site!.Kind,
                    seed,
                    stream,
                    cursor,
                    site.DrawnMasks,
                    out var fired,
                    out reason,
                    draw.Secret,
                    draw.Skip
                )) {
                    return false;
                }
                cursor = checked((cursor + fired.Samples));
                lastSample = fired.Numeric!.Value;
                selected = ((int)((((ulong)lastSample) * ((ulong)cells.Count)) >> 32));
            }
            if (selected < 0) {
                return Refuse(
                    message: "source zone does not contain the selected token",
                    reason: out reason
                );
            }
            var token = cells[selected];

            cells.RemoveAt(index: selected);
            if (target.Any(predicate: c => (c.Key == token.Key))) {
                return Refuse(
                    message: "destination already contains the token",
                    reason: out reason
                );
            }
            target.Insert(
                index: (transfer.InsertFirst
                ? 0
                : target.Count),
                item: token
            );
        }
        if (site is not null) {
            rows[drawIndex] = site with { DrawCursor = cursor, Cells = [new(
                    WorldStateRow.SlotKey,
                    lastSample
                )] };
        }
        rows[from] = source with { Cells = cells };
        rows[to] = destination with { Cells = target };
        return true;
    }
    // The keyed token and every token after it move as one run, in order: the cascade a solitaire column hands over
    // from a card to its top. Onto the same zone, the run rotates to the other end.
    private static bool TrySlice(WorldStateRow[] rows, int from, int to, WorldStateRow source, WorldStateRow destination, List<StateCell> cells, StateTransform.Transfer transfer, out string reason) {
        var start = cells.FindIndex(match: c => (c.Key.Value == transfer.Key));

        if (start < 0) {
            return Refuse(
                message: "source zone does not contain the selected token",
                reason: out reason
            );
        }
        var run = cells.GetRange(
            start,
            (cells.Count - start)
        );

        cells.RemoveRange(
            start,
            run.Count
        );
        var target = ((from == to)
            ? cells
            : (destination.Cells ?? []).ToList()
        );

        if (
            (from != to) &&
            ((target.Count + run.Count) > (destination.Capacity ?? destination.CellCeiling))
        ) {
            return Refuse(
                message: "destination zone is full",
                reason: out reason
            );
        }
        foreach (var token in run) {
            if (target.Any(predicate: c => (c.Key == token.Key))) {
                return Refuse(
                    message: "destination already contains the token",
                    reason: out reason
                );
            }
        }
        if (transfer.InsertFirst) {
            target.InsertRange(
                collection: run,
                index: 0
            );
        } else {
            target.AddRange(collection: run);
        }
        rows[from] = source with { Cells = cells };
        rows[to] = destination with { Cells = target };
        reason = string.Empty;
        return true;
    }
    // The zone's tokens sorted into their domain's order are arrangement 0; the rank's Lehmer code picks the order.
    private static bool TryArrange(WorldStateRow[] rows, StateTransform.Arrange arrange, out string reason) {
        if (
            !TryFind(
            rows,
            arrange.Row,
            out var index,
            out reason
        ) ||
            !TryFind(
            rows,
            arrange.From,
            out var fromIndex,
            out reason
        )
        ) {
            return false;
        }
        var zone = rows[index];
        var source = rows[fromIndex];

        if (
            (source.Kind != CellKind.Int) ||
            (StateRows.FindCell(
            cells: source.Cells,
            key: CellName.Parse(candidate: (arrange.FromKey ?? WorldStateRow.SlotKey))
        ) is not { } rankCell)
        ) {
            return Refuse(
                message: "arrange reads its rank from an integer cell",
                reason: out reason
            );
        }
        Span<int> ordinals = stackalloc int[StateReader.MaxArrangementTokens];
        var count = StateReader.DomainOrdinals(
            ordinals: ordinals,
            rows: rows,
            zone: zone
        );

        if (count < 0) {
            return Refuse(
                message: $"arrange requires an ordered zone of at most {StateReader.MaxArrangementTokens} tokens, every one declared by its domain",
                reason: out reason
            );
        }
        if (
            (rankCell.Value < 0L) ||
            (((ulong)rankCell.Value) >= Puck.Maths.Combinatorics.Factorial(n: count))
        ) {
            return Refuse(
                message: $"arrange rank {rankCell.Value} is outside 0..{count}!-1",
                reason: out reason
            );
        }
        var cells = (zone.Cells ?? []).ToArray();
        Span<int> relative = stackalloc int[StateReader.MaxArrangementTokens];

        StateReader.RelativeOrder(
            ordinals: ordinals[..count],
            relative: relative[..count]
        );
        // sorted[r] is the token whose relative rank is r; the unranked permutation says which rank each position takes.
        var sorted = new StateCell[count];

        for (var position = 0; (position < count); position++) {
            sorted[relative[position]] = cells[position];
        }
        Span<int> permutation = stackalloc int[StateReader.MaxArrangementTokens];

        Puck.Maths.Combinatorics.PermutationUnrank(
            ((ulong)rankCell.Value),
            permutation[..count]
        );
        var arranged = new StateCell[count];

        for (var position = 0; (position < count); position++) {
            arranged[position] = sorted[permutation[position]];
        }
        rows[index] = zone with { Cells = arranged };
        return true;
    }
    private static bool TrySetRay(WorldDefinition definition, WorldStateRow[] rows, StateTransform.SetRay ray, CompiledPatterns patterns, out string reason) {
        if (!TryFind(
            rows,
            ray.Row,
            out var index,
            out reason
        )) {
            return false;
        }
        var row = rows[index];

        if (
            (row.EffectiveDomain is not StateDomain.CellsOf board) ||
            (WorldTopologyCompilation.Find(
            definition: definition,
            name: board.Topology
        ) is not { } topology) ||
            !topology.TryCell(
            ray.From,
            out var origin
        ) ||
            (topology.Direction(token: ray.Direction) < 0) ||
            !patterns.TryGet(
            name: ray.Pattern,
            pattern: out var pattern
        ) ||
            (pattern.Source.Kind != CellKind.Int)
        ) {
            return Refuse(
                message: "setRay requires a board origin, a valid direction, and a compiled integer-kind pattern",
                reason: out reason
            );
        }
        var direction = topology.Direction(token: ray.Direction);
        Span<long> values = stackalloc long[topology.CellCount];
        Span<long> word = stackalloc long[topology.CellCount];
        Span<int> affected = stackalloc int[topology.CellCount];

        BoardQueries.Read(
            row: row,
            topology: topology,
            values: values
        );
        var count = 0;
        var cell = origin;

        for (var visited = 1; (visited < topology.CellCount); visited++) {
            cell = topology.Neighbour(
                cell: cell,
                direction: direction
            );
            if (
                (cell < 0) ||
                (cell == origin)
            ) {
                break;
            }
            word[count] = values[cell];
            affected[count] = cell;
            count++;
        }
        var prefix = pattern.LongestAcceptedPrefix(values: word[..count]);

        if (prefix <= 0) {
            return Refuse(
                message: "setRay requires a nonempty accepted prefix",
                reason: out reason
            );
        }
        if (!row.TryAdmitWrite(
            current: 0L,
            operand: ray.Value,
            write: StateWriteKind.Set,
            stored: out var admittedValue,
            reason: out _
        )) {
            return Refuse(
                message: "setRay writes a value the board row does not admit",
                reason: out reason
            );
        }
        var cells = (row.Cells ?? []).ToList();
        var cellIndices = new Dictionary<CellName, int>(capacity: cells.Count);

        for (var cellIndex = 0; (cellIndex < cells.Count); cellIndex++) {
            // Match the first authored cell if an unvalidated row contains duplicate keys.
            cellIndices.TryAdd(
                key: cells[cellIndex].Key,
                value: cellIndex
            );
        }
        for (var affectedIndex = 0; (affectedIndex < prefix); affectedIndex++) {
            var key = CellName.Parse(candidate: topology.Key(cell: affected[affectedIndex]));

            if (cellIndices.TryGetValue(
                key: key,
                value: out var existing
            )) {
                cells[existing] = cells[existing] with { Value = admittedValue };
            } else {
                cellIndices.Add(
                    key: key,
                    value: cells.Count
                );
                cells.Add(item: new(
                    key,
                    admittedValue
                ));
            }
        }
        rows[index] = row with { Cells = cells };
        return true;
    }
    // The zone's cells are indexed by key once; each attribute row is then walked once to fill its column of the
    // key table, and the cells are ordered by the key tuple with the original position as the final tiebreak, which
    // is what makes the sort stable. Keys are in column-major order: column k occupies [k * n, (k + 1) * n).
    private static bool TrySortZone(WorldStateRow[] rows, StateTransform.SortZone sort, out string reason) {
        if (!TryFind(
            rows,
            sort.Row,
            out var index,
            out reason
        )) {
            return false;
        }
        var row = rows[index];
        var cells = (row.Cells ?? []).ToArray();
        var count = cells.Length;

        if (
            (row.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zone) ||
            (sort.By is not { Count: >= 1 and <= StateCapacity.MaxSortKeys })
        ) {
            return Refuse(
                message: $"sortZone requires an ordered zone and 1..{StateCapacity.MaxSortKeys} attribute keys, each carrying its own direction",
                reason: out reason
            );
        }
        var position = new Dictionary<CellName, int>(capacity: count);

        for (var cellIndex = 0; (cellIndex < count); cellIndex++) {
            position[cells[cellIndex].Key] = cellIndex;
        }
        var keys = new long[(sort.By.Count * count)];
        var descending = new bool[sort.By.Count];

        for (var keyIndex = 0; (keyIndex < sort.By.Count); keyIndex++) {
            var key = sort.By[keyIndex];

            if (
                (key is null) ||
                !TryFind(
                rows,
                key.Row,
                out var byIndex,
                out reason
            )
            ) {
                return Refuse(
                    message: "a sort key names no state row",
                    reason: out reason
                );
            }
            var by = rows[byIndex];

            if (
                (by.Kind is not (CellKind.Int or CellKind.Fixed)) ||
                (by.EffectiveDomain is not StateDomain.KeysOf byKeysOf) ||
                (byKeysOf.Row != zone.Row)
            ) {
                return Refuse(
                    message: $"a sort attribute must be a numeric row keyed over token domain '{zone.Row}'",
                    reason: out reason
                );
            }
            foreach (var cell in (by.Cells ?? [])) {
                if (position.TryGetValue(
                    key: cell.Key,
                    value: out var cellIndex
                )) {
                    keys[((keyIndex * count) + cellIndex)] = cell.Value;
                }
            }
            descending[keyIndex] = key.Descending;
        }
        return FinishSort(
            cells: cells,
            count: count,
            descending: descending,
            index: index,
            keys: keys,
            reason: out reason,
            row: row,
            rows: rows
        );
    }
    private static bool TrySortKeyed(WorldStateRow[] rows, StateTransform.SortKeyed sort, out string reason) {
        if (!TryFind(
            rows,
            sort.Row,
            out var index,
            out reason
        )) {
            return false;
        }
        var row = rows[index];
        var cells = (row.Cells ?? []).ToArray();
        var count = cells.Length;

        if (
            !row.IsKeyed ||
            (row.Kind is not (CellKind.Int or CellKind.Fixed))
        ) {
            return Refuse(
                message: "sortKeyed requires a keyed numeric row",
                reason: out reason
            );
        }
        var keys = new long[count];

        for (var cellIndex = 0; (cellIndex < count); cellIndex++) {
            keys[cellIndex] = cells[cellIndex].Value;
        }
        return FinishSort(
            rows,
            index,
            row,
            cells,
            count,
            keys,
            [sort.Descending],
            out reason
        );
    }
    private static bool FinishSort(WorldStateRow[] rows, int index, WorldStateRow row, StateCell[] cells, int count, long[] keys, bool[] descending, out string reason) {
        reason = string.Empty;
        var order = new int[count];

        for (var cellIndex = 0; (cellIndex < count); cellIndex++) {
            order[cellIndex] = cellIndex;
        }
        Array.Sort(
            array: order,
            comparison: (left, right) => {
            for (var keyIndex = 0; (keyIndex < descending.Length); keyIndex++) {
                var comparison = keys[((keyIndex * count) + left)].CompareTo(value: keys[((keyIndex * count) + right)]);

                if (comparison != 0) {
                    return (descending[keyIndex]
                        ? -comparison
                        : comparison
                    );
                }
            }
            return left.CompareTo(value: right);
        }
        );
        var ordered = new StateCell[cells.Length];

        for (var cellIndex = 0; (cellIndex < order.Length); cellIndex++) {
            ordered[cellIndex] = cells[order[cellIndex]];
        }
        rows[index] = row with { Cells = ordered };
        return true;
    }
    private static bool TryShuffle(WorldDefinition definition, WorldStateRow[] rows, StateTransform.Shuffle shuffle, string instance, out string reason) {
        if (!TryFind(
            rows,
            shuffle.Row,
            out var index,
            out reason
        )) {
            return false;
        }
        var row = rows[index];

        if (!row.IsKeyed) {
            return Refuse(
                message: "shuffle requires a keyed row",
                reason: out reason
            );
        }
        var cells = (row.Cells ?? []).ToArray();

        if (cells.Length < 2) {
            return true;
        }
        if (!TryFind(
            rows,
            shuffle.Draw,
            out var drawIndex,
            out reason
        )) {
            return false;
        }
        var site = rows[drawIndex];

        if (!TryResolveDrawSite(
            definition: definition,
            draw: out var draw,
            generator: out var generator,
            instance: instance,
            reason: out reason,
            seed: out var seed,
            site: site,
            stream: out var stream,
            verb: "shuffle"
        )) {
            return false;
        }
        var cursor = site.DrawCursor;
        var last = 0L;
        // Fisher-Yates from the top: position i takes a uniform pick from [0, i], the same multiply-high map a random
        // transfer selects with, one sample per position. The site records the final cursor and the last sample once.
        for (var position = (cells.Length - 1); (position > 0); position--) {
            if (!GeneratorEngine.TryFire(
                generator,
                site.Kind,
                seed,
                stream,
                cursor,
                site.DrawnMasks,
                out var fired,
                out reason,
                draw.Secret,
                draw.Skip
            )) {
                return false;
            }
            cursor = checked((cursor + fired.Samples));
            last = fired.Numeric!.Value;
            var pick = ((int)((((ulong)last) * ((ulong)(position + 1))) >> 32));

            (cells[position], cells[pick]) = (cells[pick], cells[position]);
        }
        rows[drawIndex] = site with { DrawCursor = cursor, Cells = [new(
                WorldStateRow.SlotKey,
                last
            )] };
        rows[index] = row with { Cells = cells };
        return true;
    }
}

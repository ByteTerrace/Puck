using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Pure candidate composition for bounded state operations. No effect, random cursor, or phase progression
/// escapes a refused candidate; the ordinary mutation pipeline validates, installs and journals the result. A refusal
/// is a returned reason, never a thrown one: a rule may retry a refused transform every tick.</summary>
public static partial class WorldStateTransforms {
    /// <summary>Lists every state row whose edit capability an operation needs.</summary>
    /// <param name="transform">The operation.</param>
    /// <returns>The addressed row names.</returns>
    public static IEnumerable<string> Subjects(WorldStateTransform transform) => transform switch {
        WorldStateTransform.Transfer transfer => transfer.Draw is null ? [transfer.From, transfer.To] : [transfer.From, transfer.To, transfer.Draw],
        WorldStateTransform.SetRay ray => [ray.Row],
        WorldStateTransform.Observe observe => [observe.Row],
        WorldStateTransform.Shuffle shuffle => [shuffle.Row, shuffle.Draw],
        WorldStateTransform.SortZone sortZone => [sortZone.Row, .. sortZone.By.Select(key => key.Row)],
        WorldStateTransform.SortKeyed sortKeyed => [sortKeyed.Row],
        WorldStateTransform.WriteSet writeSet => [writeSet.Row],
        WorldStateTransform.Push push => [push.Row],
        _ => [],
    };

    /// <summary>Composes one operation without changing the supplied definition.</summary>
    /// <param name="definition">The validated current definition.</param>
    /// <param name="transform">The operation.</param>
    /// <param name="actor">The stamped acting principal.</param>
    /// <param name="tick">The current simulation tick.</param>
    /// <param name="instance">The authoritative instance identity used by draw streams.</param>
    /// <param name="candidate">The new definition, or the original on refusal.</param>
    /// <param name="reason">The refusal reason.</param>
    /// <param name="patterns">The document's compiled patterns, for <see cref="WorldStateTransform.SetRay"/>; empty
    /// when the caller has none compiled (every other transform ignores it).</param>
    /// <returns>Whether the operation composed.</returns>
    public static bool TryApply(WorldDefinition definition, WorldStateTransform transform, WorldPrincipal actor,
        ulong tick, string instance, out WorldDefinition candidate, out string reason, CompiledWorldPatterns? patterns = null) {
        candidate = definition;
        var rows = definition.State.ToArray();
        bool composed;

        try {
            composed = transform switch {
                WorldStateTransform.Observe observe => TryObserve(definition, rows, observe, actor, tick, out reason),
                WorldStateTransform.Transfer transfer => TryTransfer(definition, rows, transfer, instance, out reason),
                WorldStateTransform.SetRay ray => TrySetRay(definition, rows, ray, patterns ?? CompiledWorldPatterns.Empty, out reason),
                WorldStateTransform.Shuffle shuffle => TryShuffle(definition, rows, shuffle, instance, out reason),
                WorldStateTransform.SortZone sortZone => TrySortZone(rows, sortZone, out reason),
                WorldStateTransform.SortKeyed sortKeyed => TrySortKeyed(rows, sortKeyed, out reason),
                WorldStateTransform.WriteSet writeSet => TryWriteSet(definition, rows, writeSet, out reason),
                WorldStateTransform.Push push => TryPush(rows, push, out reason),
                _ => Refuse("unknown state transform", out reason),
            };
        } catch (OverflowException exception) {
            // The phase generation is checked; wrapping it silently would fork replay.
            reason = exception.Message;
            return false;
        }

        if (composed) {
            candidate = definition.WithWorldState(rows);
        }

        return composed;
    }

    /// <summary>Checks a submitted guard's generation against its phase row.</summary>
    /// <param name="definition">The current definition.</param>
    /// <param name="guard">The submitted guard.</param>
    /// <param name="actor">The authenticated actor.</param>
    /// <returns>Whether the guard matches: the sole condition a mutation's guard checks.</returns>
    public static bool CanAct(WorldDefinition definition, WorldPhaseGuard guard, WorldPrincipal actor) {
        var phase = WorldDefinitionRows.FindStateRow(definition.State, guard.Row)?.Phase;

        return phase is not null && phase.Sequence == guard.Sequence && (guard.Participant is null || actor == WorldPrincipal.World);
    }

    /// <summary>Advances a phase row's generation by one: the completion half of a guarded submission. Called by the
    /// mutation pipeline after a mutation carrying a matching <see cref="WorldPhaseGuard"/> succeeds.</summary>
    /// <param name="definition">The definition the guarded mutation just produced.</param>
    /// <param name="row">The phase row named by the guard.</param>
    /// <returns>The definition with that row's generation advanced.</returns>
    public static WorldDefinition Advance(WorldDefinition definition, string row) {
        var rows = definition.State.ToArray();

        for (var index = 0; index < rows.Length; index++) {
            if (rows[index].Name.Value == row && rows[index].Phase is { } phase) {
                rows[index] = rows[index] with { Phase = phase with { Sequence = checked(phase.Sequence + 1) } };
                break;
            }
        }

        return definition.WithWorldState(rows);
    }

    private static bool Refuse(string message, out string reason) {
        reason = message;
        return false;
    }

    private static bool TryFind(WorldStateRow[] rows, string name, out int index, out string reason) {
        for (index = 0; index < rows.Length; index++) {
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
        out WorldGenerator generator, out WorldDraw draw, out ulong seed, out ulong stream, out string reason) {
        generator = default!;
        seed = default;
        stream = 0;

        if (site.Draw is not { Timing: not WorldDrawTiming.Boot } declared || site.Kind != CellKind.Int ||
            !WorldGeneratorEngine.TryResolveSource(generators: definition.Generators, draw: declared, generator: out generator, reason: out _) || generator.Source != WorldGeneratorSource.StreamDraw) {
            draw = default!;
            return Refuse($"{verb} requires a redrawable integer streamDraw site", out reason);
        }

        draw = declared;
        var descriptor = WorldDrawSites.StateRow(site.Name);
        seed = WorldGeneratorEngine.ComputeSeedState(worldSeed: definition.Generation?.WorldSeed ?? 0, instanceIdentity: instance, site: descriptor);
        stream = WorldGeneratorEngine.ComputeStreamId(descriptor);
        reason = string.Empty;
        return true;
    }

    private static bool TryTransfer(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.Transfer transfer, string instance, out string reason) {
        if (!TryFind(rows, transfer.From, out var from, out reason) || !TryFind(rows, transfer.To, out var to, out reason)) {
            return false;
        }
        var source = rows[from];
        var destination = rows[to];
        if (source.EffectiveDomain is not WorldStateDomain.KeysOf { Ordered: true } sourceZone || destination.EffectiveDomain is not WorldStateDomain.KeysOf { Ordered: true } destinationZone || sourceZone.Row != destinationZone.Row || !Enum.IsDefined(transfer.Selector)) {
            return Refuse("transfer requires zones in one token domain and a defined selector", out reason);
        }
        if ((transfer.Selector == WorldZoneSelector.Key) != (transfer.Key is not null) || (transfer.Selector == WorldZoneSelector.Random) != (transfer.Draw is not null)) {
            return Refuse("key selection requires only key; random selection requires only draw", out reason);
        }
        if (transfer.Count < 1 || transfer.Count > WorldStateTransferCapacity.MaxTransferCount || (transfer.Selector == WorldZoneSelector.Key && transfer.Count != 1)) {
            return Refuse($"transfer count must be 1..{WorldStateTransferCapacity.MaxTransferCount}, and exactly 1 for a key selection", out reason);
        }
        var cells = (source.Cells ?? []).ToList();
        if (cells.Count < transfer.Count) {
            return Refuse((cells.Count == 0) ? "source zone is empty" : $"source zone holds {cells.Count} tokens, fewer than the {transfer.Count} to transfer", out reason);
        }
        var target = from == to ? cells : (destination.Cells ?? []).ToList();
        if (from != to && (target.Count + transfer.Count) > (destination.Capacity ?? destination.CellCeiling)) {
            return Refuse("destination zone is full", out reason);
        }
        var drawIndex = -1;
        WorldStateRow? site = null;
        WorldGenerator generator = default!;
        WorldDraw draw = default!;
        ulong seed = 0;
        ulong stream = 0;
        var cursor = 0L;
        var lastSample = 0L;
        if (transfer.Selector == WorldZoneSelector.Random) {
            if (!TryFind(rows, transfer.Draw!, out drawIndex, out reason)) {
                return false;
            }
            site = rows[drawIndex];
            if (!TryResolveDrawSite(definition, site, instance, "random transfer", out generator, out draw, out seed, out stream, out reason)) {
                return false;
            }
            cursor = site.DrawCursor;
        }
        // Each token is selected afresh from what remains, so a random transfer samples once per token and a
        // positional transfer walks the pile from its chosen end.
        for (var moved = 0; moved < transfer.Count; moved++) {
            var selected = transfer.Selector switch {
                WorldZoneSelector.First => 0,
                WorldZoneSelector.Last => cells.Count - 1,
                WorldZoneSelector.Key => cells.FindIndex(c => c.Key.Value == transfer.Key),
                _ => 0,
            };
            if (transfer.Selector == WorldZoneSelector.Random) {
                if (!WorldGeneratorEngine.TryFire(generator, site!.Kind, seed, stream, cursor, site.DrawnMasks, out var fired, out reason, draw.Secret)) {
                    return false;
                }
                cursor = checked(cursor + fired.Samples);
                lastSample = fired.Numeric!.Value;
                selected = (int)(((ulong)lastSample * (ulong)cells.Count) >> 32);
            }
            if (selected < 0) {
                return Refuse("source zone does not contain the selected token", out reason);
            }
            var token = cells[selected];
            cells.RemoveAt(selected);
            if (target.Any(c => c.Key == token.Key)) {
                return Refuse("destination already contains the token", out reason);
            }
            target.Insert(transfer.InsertFirst ? 0 : target.Count, token);
        }
        if (site is not null) {
            rows[drawIndex] = site with { DrawCursor = cursor, Cells = [new(WorldStateRow.SlotKey, lastSample)] };
        }
        rows[from] = source with { Cells = cells };
        rows[to] = destination with { Cells = target };
        return true;
    }

    private static bool TrySetRay(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.SetRay ray, CompiledWorldPatterns patterns, out string reason) {
        if (!TryFind(rows, ray.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.EffectiveDomain is not WorldStateDomain.CellsOf board || WorldTopologyCompilation.Find(definition, board.Topology) is not { } topology ||
            !topology.TryCell(ray.From, out var origin) || topology.Direction(ray.Direction) < 0 ||
            !patterns.TryGet(ray.Pattern, out var pattern) || pattern.Source.Kind != CellKind.Int) {
            return Refuse("setRay requires a board origin, a valid direction, and a compiled integer-kind pattern", out reason);
        }
        var direction = topology.Direction(ray.Direction);
        Span<long> values = stackalloc long[topology.CellCount];
        Span<long> word = stackalloc long[topology.CellCount];
        Span<int> affected = stackalloc int[topology.CellCount];
        WorldBoardQueries.Read(row, topology, values);
        var count = 0;
        var cell = origin;
        for (var visited = 1; visited < topology.CellCount; visited++) {
            cell = topology.Neighbour(cell, direction);
            if (cell < 0 || cell == origin) {
                break;
            }
            word[count] = values[cell];
            affected[count] = cell;
            count++;
        }
        var prefix = pattern.LongestAcceptedPrefix(word[..count]);
        if (prefix <= 0) {
            return Refuse("setRay requires a nonempty accepted prefix", out reason);
        }
        var cells = (row.Cells ?? []).ToList();
        for (var affectedIndex = 0; affectedIndex < prefix; affectedIndex++) {
            var key = WorldCellName.Parse(topology.Key(affected[affectedIndex]));
            var existing = cells.FindIndex(c => c.Key == key);
            if (existing >= 0) {
                cells[existing] = cells[existing] with { Value = ray.Value };
            } else {
                cells.Add(new(key, ray.Value));
            }
        }
        rows[index] = row with { Cells = cells };
        return true;
    }

    // The zone's cells are indexed by key once; each attribute row is then walked once to fill its column of the
    // key table, and the cells are ordered by the key tuple with the original position as the final tiebreak, which
    // is what makes the sort stable. Keys are in column-major order: column k occupies [k * n, (k + 1) * n).
    private static bool TrySortZone(WorldStateRow[] rows, WorldStateTransform.SortZone sort, out string reason) {
        if (!TryFind(rows, sort.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        var cells = (row.Cells ?? []).ToArray();
        var count = cells.Length;
        if (row.EffectiveDomain is not WorldStateDomain.KeysOf { Ordered: true } zone || sort.By is not { Count: >= 1 and <= WorldStateCapacity.MaxSortKeys }) {
            return Refuse($"sortZone requires an ordered zone and 1..{WorldStateCapacity.MaxSortKeys} attribute keys, each carrying its own direction", out reason);
        }
        var position = new Dictionary<WorldCellName, int>(count);
        for (var cellIndex = 0; cellIndex < count; cellIndex++) {
            position[cells[cellIndex].Key] = cellIndex;
        }
        var keys = new long[sort.By.Count * count];
        var descending = new bool[sort.By.Count];
        for (var keyIndex = 0; keyIndex < sort.By.Count; keyIndex++) {
            var key = sort.By[keyIndex];
            if (key is null || !TryFind(rows, key.Row, out var byIndex, out reason)) {
                return Refuse("a sort key names no state row", out reason);
            }
            var by = rows[byIndex];
            if (by.Kind is not (CellKind.Int or CellKind.Fixed) || by.EffectiveDomain is not WorldStateDomain.KeysOf byKeysOf || byKeysOf.Row != zone.Row) {
                return Refuse($"a sort attribute must be a numeric row keyed over token domain '{zone.Row}'", out reason);
            }
            foreach (var cell in by.Cells ?? []) {
                if (position.TryGetValue(cell.Key, out var cellIndex)) {
                    keys[(keyIndex * count) + cellIndex] = cell.Value;
                }
            }
            descending[keyIndex] = key.Descending;
        }
        return FinishSort(rows, index, row, cells, count, keys, descending, out reason);
    }

    private static bool TrySortKeyed(WorldStateRow[] rows, WorldStateTransform.SortKeyed sort, out string reason) {
        if (!TryFind(rows, sort.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        var cells = (row.Cells ?? []).ToArray();
        var count = cells.Length;
        if (!row.IsKeyed || row.Kind is not (CellKind.Int or CellKind.Fixed)) {
            return Refuse("sortKeyed requires a keyed numeric row", out reason);
        }
        var keys = new long[count];
        for (var cellIndex = 0; cellIndex < count; cellIndex++) {
            keys[cellIndex] = cells[cellIndex].Value;
        }
        return FinishSort(rows, index, row, cells, count, keys, [sort.Descending], out reason);
    }

    private static bool FinishSort(WorldStateRow[] rows, int index, WorldStateRow row, WorldStateCell[] cells, int count, long[] keys, bool[] descending, out string reason) {
        reason = string.Empty;
        var order = new int[count];
        for (var cellIndex = 0; cellIndex < count; cellIndex++) {
            order[cellIndex] = cellIndex;
        }
        Array.Sort(order, (left, right) => {
            for (var keyIndex = 0; keyIndex < descending.Length; keyIndex++) {
                var comparison = keys[(keyIndex * count) + left].CompareTo(keys[(keyIndex * count) + right]);
                if (comparison != 0) {
                    return descending[keyIndex] ? -comparison : comparison;
                }
            }
            return left.CompareTo(right);
        });
        var ordered = new WorldStateCell[cells.Length];
        for (var cellIndex = 0; cellIndex < order.Length; cellIndex++) {
            ordered[cellIndex] = cells[order[cellIndex]];
        }
        rows[index] = row with { Cells = ordered };
        return true;
    }

    private static bool TryShuffle(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.Shuffle shuffle, string instance, out string reason) {
        if (!TryFind(rows, shuffle.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (!row.IsKeyed) {
            return Refuse("shuffle requires a keyed row", out reason);
        }
        var cells = (row.Cells ?? []).ToArray();
        if (cells.Length < 2) {
            return true;
        }
        if (!TryFind(rows, shuffle.Draw, out var drawIndex, out reason)) {
            return false;
        }
        var site = rows[drawIndex];
        if (!TryResolveDrawSite(definition, site, instance, "shuffle", out var generator, out var draw, out var seed, out var stream, out reason)) {
            return false;
        }
        var cursor = site.DrawCursor;
        var last = 0L;
        // Fisher-Yates from the top: position i takes a uniform pick from [0, i], the same multiply-high map a random
        // transfer selects with, one sample per position. The site records the final cursor and the last sample once.
        for (var position = cells.Length - 1; position > 0; position--) {
            if (!WorldGeneratorEngine.TryFire(generator, site.Kind, seed, stream, cursor, site.DrawnMasks, out var fired, out reason, draw.Secret)) {
                return false;
            }
            cursor = checked(cursor + fired.Samples);
            last = fired.Numeric!.Value;
            var pick = (int)(((ulong)last * (ulong)(position + 1)) >> 32);
            (cells[position], cells[pick]) = (cells[pick], cells[position]);
        }
        rows[drawIndex] = site with { DrawCursor = cursor, Cells = [new(WorldStateRow.SlotKey, last)] };
        rows[index] = row with { Cells = cells };
        return true;
    }
}

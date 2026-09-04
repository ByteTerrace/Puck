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
        WorldStateTransform.MoveToken move => [move.Positions, move.Allowance],
        WorldStateTransform.CompletePhase phase => [phase.Row],
        WorldStateTransform.TurnOrder order => [order.Row],
        WorldStateTransform.Shuffle shuffle => [shuffle.Row, shuffle.Draw],
        WorldStateTransform.Sort sort => sort.By is null ? [sort.Row] : [sort.Row, .. sort.By.Select(key => key.Row)],
        WorldStateTransform.SetMask setMask => [setMask.Row],
        WorldStateTransform.Combine combine => [combine.Target],
        WorldStateTransform.Push push => [push.Row],
        WorldStateTransform.MapBoard mapped => [mapped.Target],
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
    /// <returns>Whether the operation composed.</returns>
    public static bool TryApply(WorldDefinition definition, WorldStateTransform transform, WorldPrincipal actor,
        ulong tick, string instance, out WorldDefinition candidate, out string reason) {
        candidate = definition;
        var rows = definition.State.ToArray();
        bool composed;

        try {
            composed = transform switch {
                WorldStateTransform.Observe observe => TryObserve(definition, rows, observe, actor, tick, out reason),
                WorldStateTransform.Transfer transfer => TryTransfer(definition, rows, transfer, instance, out reason),
                WorldStateTransform.MoveToken move => TryMove(definition, rows, move, out reason),
                WorldStateTransform.SetRay ray => TrySetRay(definition, rows, ray, out reason),
                WorldStateTransform.CompletePhase complete => TryComplete(definition, rows, complete, actor, tick, out reason),
                WorldStateTransform.TurnOrder order => TryOrder(rows, order, actor, out reason),
                WorldStateTransform.Shuffle shuffle => TryShuffle(definition, rows, shuffle, instance, out reason),
                WorldStateTransform.Sort sort => TrySort(rows, sort, out reason),
                WorldStateTransform.SetMask setMask => TrySetMask(definition, rows, setMask, out reason),
                WorldStateTransform.Combine combine => TryCombine(definition, rows, combine, out reason),
                WorldStateTransform.Push push => TryPush(rows, push, out reason),
                WorldStateTransform.MapBoard mapped => TryMapBoard(definition, rows, mapped, out reason),
                _ => Refuse("unknown state transform", out reason),
            };
        } catch (OverflowException exception) {
            // Cursor, sequence, and round counters are checked; wrapping one silently would fork replay.
            reason = exception.Message;
            return false;
        }

        if (composed) {
            candidate = definition.WithWorldState(rows);
        }

        return composed;
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
        if (source.Zone is not { } sourceZone || destination.Zone is not { } destinationZone || sourceZone.Tokens != destinationZone.Tokens || !Enum.IsDefined(transfer.Selector)) {
            return Refuse("transfer requires zones in one token domain and a defined selector", out reason);
        }
        if ((transfer.Selector is WorldZoneSelector.First or WorldZoneSelector.Last) && !sourceZone.Ordered || transfer.InsertFirst && !destinationZone.Ordered) {
            return Refuse("positional selection and insertion require ordered zones", out reason);
        }
        if ((transfer.Selector == WorldZoneSelector.Key) != (transfer.Key is not null) || (transfer.Selector == WorldZoneSelector.Random) != (transfer.Draw is not null)) {
            return Refuse("key selection requires only key; random selection requires only draw", out reason);
        }
        if (transfer.Count < 1 || transfer.Count > WorldStateTokens.MaxCapacity || (transfer.Selector == WorldZoneSelector.Key && transfer.Count != 1)) {
            return Refuse($"transfer count must be 1..{WorldStateTokens.MaxCapacity}, and exactly 1 for a key selection", out reason);
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
        // Each token is selected afresh from what remains, so a random deal samples once per card and a
        // positional deal walks the pile from its chosen end.
        for (var moved = 0; moved < transfer.Count; moved++) {
            var selected = transfer.Selector switch {
                WorldZoneSelector.First => 0,
                WorldZoneSelector.Last => cells.Count - 1,
                WorldZoneSelector.Key => cells.FindIndex(c => c.Key.Value == transfer.Key),
                _ => 0,
            };
            if (transfer.Selector == WorldZoneSelector.Random) {
                if (!WorldGeneratorEngine.TryFire(generator, site!.Kind, seed, stream, cursor, site.DrawDecks, out var fired, out reason, draw.Secret)) {
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

    private static bool TrySetRay(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.SetRay ray, out string reason) {
        if (!TryFind(rows, ray.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.Board is not { } board || WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology ||
            !topology.TryCell(ray.From, out var origin) || topology.Direction(ray.Direction) < 0 || ray.Through == ray.Until) {
            return Refuse("setRay requires a board origin, valid direction and distinct through/until values", out reason);
        }
        var direction = topology.Direction(ray.Direction);
        Span<long> values = stackalloc long[topology.CellCount];
        Span<int> affected = stackalloc int[topology.CellCount];
        WorldBoardQueries.Read(row, topology, values);
        var count = 0;
        var cell = origin;
        var closed = false;
        for (var visited = 1; visited < topology.CellCount; visited++) {
            cell = topology.Neighbour(cell, direction);
            if (cell < 0 || cell == origin) {
                break;
            }
            if (values[cell] == ray.Until) {
                closed = count > 0;
                break;
            }
            if (values[cell] != ray.Through) {
                break;
            }
            affected[count++] = cell;
        }
        if (!closed) {
            return Refuse("setRay requires a nonempty matching run and a closing endpoint", out reason);
        }
        var cells = (row.Cells ?? []).ToList();
        for (var affectedIndex = 0; affectedIndex < count; affectedIndex++) {
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

    /// <summary>Gets the absolute phase deadline, including a newly authored initial phase.</summary>
    /// <param name="phase">The phase state.</param>
    /// <param name="rate">Simulation ticks per second.</param>
    /// <returns>The deadline, or zero for none.</returns>
    public static long Deadline(WorldStatePhase phase, int rate) => phase.Sequence == 0 && phase.DeadlineTick == 0
        ? checked((long)decimal.Ceiling(phase.Phases[phase.Current].TimeoutSeconds * rate)) : phase.DeadlineTick;

    private static bool TryComplete(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.CompletePhase complete, WorldPrincipal actor, ulong tick, out string reason) {
        if (!TryFind(rows, complete.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.Phase is not { } phase) {
            return Refuse("completion requires a phase row", out reason);
        }
        if ((complete.ExpectedSequence is { } expected && expected != phase.Sequence) || (complete.ExpectedSequence is null && actor != WorldPrincipal.World)) {
            return Refuse("phase completion requires the current sequence", out reason);
        }
        var node = phase.Phases[phase.Current];
        var deadline = Deadline(phase, definition.SimulationRateHz);
        var participant = -1;
        var actorName = actor.Describe();
        if (complete.Participant is not null) {
            if (actor != WorldPrincipal.World) {
                return Refuse("only the world program may name another participant", out reason);
            }
            actorName = complete.Participant;
        }
        for (var i = 0; i < phase.Participants.Count; i++) {
            if (phase.Participants[i] == actorName) {
                participant = i;
            }
        }
        if (complete.Timeout) {
            if (actor != WorldPrincipal.World || deadline == 0 || tick < (ulong)deadline) {
                return Refuse("timeout completion requires the world program and an expired deadline", out reason);
            }
        } else {
            if (deadline > 0 && tick >= (ulong)deadline) {
                return Refuse("phase deadline has expired", out reason);
            }
            if (node.Mode == WorldPhaseMode.Resolution ? actor != WorldPrincipal.World : participant < 0 ||
                (node.Mode == WorldPhaseMode.Sequential && participant != phase.Active) ||
                (node.Mode == WorldPhaseMode.Together && (phase.Ready & (1u << participant)) != 0)) {
                return Refuse("actor is not eligible to complete this phase", out reason);
            }
        }
        if (complete.Next is not null && actor != WorldPrincipal.World) {
            return Refuse("only the world program may branch a phase transition", out reason);
        }
        var next = phase;
        var transition = complete.Timeout || node.Mode == WorldPhaseMode.Resolution;
        if (!transition && node.Mode == WorldPhaseMode.Sequential) {
            // The turn walks in the row's direction over the participants it does not skip; passing either end of
            // the order ends the phase.
            var cursor = phase.Active;
            while (true) {
                cursor += phase.Direction;
                if ((uint)cursor >= (uint)phase.Participants.Count) {
                    transition = true;
                    break;
                }
                if (!IsSkipped(phase, cursor)) {
                    next = next with { Active = cursor };
                    break;
                }
            }
        } else if (!transition) {
            next = next with { Ready = phase.Ready | (1u << participant) };
            transition = (next.Ready | phase.Skipped) == AllParticipants(phase);
        }
        if (transition) {
            var targetName = complete.Next ?? node.Next;
            var target = -1;
            for (var i = 0; i < phase.Phases.Count; i++) {
                if (phase.Phases[i].Name == targetName) {
                    target = i;
                }
            }
            if (target < 0) {
                return Refuse("next phase is not declared", out reason);
            }
            next = next with { Current = target, Active = FirstActive(phase, phase.Direction), Ready = 0, Round = checked(phase.Round + (target == 0 ? 1 : 0)) };
        }
        if (transition || node.Mode == WorldPhaseMode.Sequential) {
            next = next with { Sequence = checked(phase.Sequence + 1) };
        }
        // Together phases keep one shared deadline; sequential activations receive a fresh interval.
        if (transition || node.Mode == WorldPhaseMode.Sequential) {
            var delay = checked((long)decimal.Ceiling(next.Phases[next.Current].TimeoutSeconds * definition.SimulationRateHz));
            next = next with { DeadlineTick = delay == 0 ? 0 : checked((long)tick + delay) };
        } else {
            next = next with { DeadlineTick = deadline };
        }
        rows[index] = row with { Phase = next };
        return true;
    }

    // The zone's cells are indexed by key once; each attribute row is then walked once to fill its column of the
    // key table, and the cells are ordered by the key tuple with the original position as the final tiebreak, which
    // is what makes the sort stable. Keys are in column-major order: column k occupies [k * n, (k + 1) * n).
    private static bool TrySort(WorldStateRow[] rows, WorldStateTransform.Sort sort, out string reason) {
        if (!TryFind(rows, sort.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        var cells = (row.Cells ?? []).ToArray();
        var count = cells.Length;
        long[] keys;
        bool[] descending;
        if (row.Zone is { } zone) {
            if (!zone.Ordered || sort.By is not { Count: >= 1 and <= WorldStateCapacity.MaxSortKeys } || sort.Descending) {
                return Refuse($"sorting a zone requires an ordered zone and 1..{WorldStateCapacity.MaxSortKeys} attribute keys, each carrying its own direction", out reason);
            }
            var position = new Dictionary<WorldCellName, int>(count);
            for (var cellIndex = 0; cellIndex < count; cellIndex++) {
                position[cells[cellIndex].Key] = cellIndex;
            }
            keys = new long[sort.By.Count * count];
            descending = new bool[sort.By.Count];
            for (var keyIndex = 0; keyIndex < sort.By.Count; keyIndex++) {
                var key = sort.By[keyIndex];
                if (key is null || !TryFind(rows, key.Row, out var byIndex, out reason)) {
                    return Refuse("a sort key names no state row", out reason);
                }
                var by = rows[byIndex];
                if (!by.IsKeyed || by.Kind is not (CellKind.Int or CellKind.Fixed) || by.KeysFrom != zone.Tokens) {
                    return Refuse($"a sort attribute must be a numeric row keyed over token domain '{zone.Tokens}'", out reason);
                }
                foreach (var cell in by.Cells ?? []) {
                    if (position.TryGetValue(cell.Key, out var cellIndex)) {
                        keys[(keyIndex * count) + cellIndex] = cell.Value;
                    }
                }
                descending[keyIndex] = key.Descending;
            }
        } else {
            if (!row.IsKeyed || sort.By is not null || row.Kind is not (CellKind.Int or CellKind.Fixed)) {
                return Refuse("sorting a keyed row orders its own numeric values and takes no attribute", out reason);
            }
            keys = new long[count];
            descending = [sort.Descending];
            for (var cellIndex = 0; cellIndex < count; cellIndex++) {
                keys[cellIndex] = cells[cellIndex].Value;
            }
        }
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
        if (row.Zone is not { Ordered: true }) {
            return Refuse("shuffle requires an ordered zone", out reason);
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
            if (!WorldGeneratorEngine.TryFire(generator, site.Kind, seed, stream, cursor, site.DrawDecks, out var fired, out reason, draw.Secret)) {
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

    private static uint AllParticipants(WorldStatePhase phase) => phase.Participants.Count == 32 ? uint.MaxValue : (1u << phase.Participants.Count) - 1;
    private static bool IsSkipped(WorldStatePhase phase, int participant) => (phase.Skipped & (1u << participant)) != 0;
    // The first participant a fresh sequential phase activates: the first unskipped one from the leading end in the
    // walk direction, or that end itself when every participant is skipped.
    private static int FirstActive(WorldStatePhase phase, int direction) {
        var count = phase.Participants.Count;
        var start = direction > 0 ? 0 : count - 1;
        for (var cursor = start; (uint)cursor < (uint)count; cursor += direction) {
            if (!IsSkipped(phase, cursor)) { return cursor; }
        }
        return start;
    }
    private static int ParticipantOrdinal(WorldStatePhase phase, string token) {
        for (var i = 0; i < phase.Participants.Count; i++) {
            if (phase.Participants[i] == token) { return i; }
        }
        return -1;
    }

    private static bool TryOrder(WorldStateRow[] rows, WorldStateTransform.TurnOrder order, WorldPrincipal actor, out string reason) {
        if (actor != WorldPrincipal.World) {
            return Refuse("only the world program may reshape turn order", out reason);
        }
        if (!TryFind(rows, order.Row, out var index, out reason)) {
            return false;
        }
        var row = rows[index];
        if (row.Phase is not { } phase) {
            return Refuse("turn order requires a phase row", out reason);
        }
        if (order.Direction is { } direction && direction is not (1 or -1)) {
            return Refuse("turn order direction must be 1 or -1", out reason);
        }
        var next = phase with { Direction = order.Direction ?? phase.Direction };
        foreach (var token in order.Skip ?? []) {
            var ordinal = ParticipantOrdinal(phase, token);
            if (ordinal < 0) { return Refuse($"'{token}' is not a declared participant", out reason); }
            next = next with { Skipped = next.Skipped | (1u << ordinal) };
        }
        foreach (var token in order.Unskip ?? []) {
            var ordinal = ParticipantOrdinal(phase, token);
            if (ordinal < 0) { return Refuse($"'{token}' is not a declared participant", out reason); }
            next = next with { Skipped = next.Skipped & ~(1u << ordinal) };
        }
        if (order.Active is { } activeToken) {
            var active = ParticipantOrdinal(phase, activeToken);
            if (active < 0) { return Refuse($"'{activeToken}' is not a declared participant", out reason); }
            if (IsSkipped(next, active)) {
                return Refuse("the activated participant is skipped", out reason);
            }
            next = next with { Active = active };
        } else if (phase.Phases[phase.Current].Mode == WorldPhaseMode.Sequential && IsSkipped(next, next.Active)) {
            // The active participant left the order: hand the turn to the next unskipped one around the ring.
            var cursor = next.Active;
            for (var step = 0; step < phase.Participants.Count; step++) {
                cursor = ((cursor + next.Direction) % phase.Participants.Count + phase.Participants.Count) % phase.Participants.Count;
                if (!IsSkipped(next, cursor)) { next = next with { Active = cursor }; break; }
            }
        }
        if (next.Active != phase.Active) {
            next = next with { Sequence = checked(phase.Sequence + 1) };
        }
        rows[index] = row with { Phase = next };
        return true;
    }
}

using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Pure candidate composition for bounded state operations. No effect, random cursor, or phase progression
/// escapes a refused candidate; the ordinary mutation pipeline validates, installs and journals the result.</summary>
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
        reason = string.Empty;
        try {
            var rows = definition.State.ToArray();
            switch (transform) {
                case WorldStateTransform.Observe observe: Observe(definition, rows, observe, actor, tick); break;
                case WorldStateTransform.Transfer transfer:
                    Transfer(definition, rows, transfer, instance);
                    break;
                case WorldStateTransform.MoveToken move:
                    Move(definition, rows, move);
                    break;
                case WorldStateTransform.SetRay ray:
                    SetRay(definition, rows, ray);
                    break;
                case WorldStateTransform.CompletePhase complete:
                    Complete(definition, rows, complete, actor, tick);
                    break;
                case WorldStateTransform.TurnOrder order:
                    Order(rows, order, actor);
                    break;
                default:
                    throw new InvalidOperationException("unknown state transform");
            }
            candidate = definition.WithWorldState(rows);
            return true;
        } catch (Exception exception) when (exception is InvalidOperationException or OverflowException or ArgumentException) {
            reason = exception.Message;
            return false;
        }
    }

    private static int Find(WorldStateRow[] rows, string name) {
        for (var index = 0; index < rows.Length; index++) {
            if (rows[index].Name.Value == name) {
                return index;
            }
        }
        throw new InvalidOperationException($"state row '{name}' does not exist");
    }

    private static void Transfer(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.Transfer transfer, string instance) {
        var from = Find(rows, transfer.From);
        var to = Find(rows, transfer.To);
        var source = rows[from];
        var destination = rows[to];
        if (source.Zone is not { } sourceZone || destination.Zone is not { } destinationZone || sourceZone.Tokens != destinationZone.Tokens || !Enum.IsDefined(transfer.Selector)) {
            throw new InvalidOperationException("transfer requires zones in one token domain and a defined selector");
        }
        if ((transfer.Selector is WorldZoneSelector.First or WorldZoneSelector.Last) && !sourceZone.Ordered || transfer.InsertFirst && !destinationZone.Ordered) {
            throw new InvalidOperationException("positional selection and insertion require ordered zones");
        }
        if ((transfer.Selector == WorldZoneSelector.Key) != (transfer.Key is not null) || (transfer.Selector == WorldZoneSelector.Random) != (transfer.Draw is not null)) {
            throw new InvalidOperationException("key selection requires only key; random selection requires only draw");
        }
        var cells = (source.Cells ?? []).ToList();
        if (cells.Count == 0) {
            throw new InvalidOperationException("source zone is empty");
        }
        if (from != to && (destination.Cells?.Count ?? 0) >= (destination.Capacity ?? destination.CellCeiling)) {
            throw new InvalidOperationException("destination zone is full");
        }
        var selected = transfer.Selector switch {
            WorldZoneSelector.First => 0,
            WorldZoneSelector.Last => cells.Count - 1,
            WorldZoneSelector.Key => cells.FindIndex(c => c.Key.Value == transfer.Key),
            _ => 0,
        };
        if (transfer.Selector == WorldZoneSelector.Random) {
            var drawIndex = Find(rows, transfer.Draw!);
            var site = rows[drawIndex];
            if (site.Draw is not { Timing: not WorldDrawTiming.Boot } draw || site.Kind != CellKind.Int ||
                !WorldGeneratorEngine.TryResolveSource(generators: definition.Generators, draw: draw, generator: out var generator, reason: out _) || generator.Source != WorldGeneratorSource.StreamDraw) {
                throw new InvalidOperationException("random transfer requires a redrawable integer streamDraw site");
            }
            var descriptor = WorldDrawSites.StateRow(site.Name);
            if (!WorldGeneratorEngine.TryFire(generator, site.Kind,
                WorldGeneratorEngine.ComputeSeedState(worldSeed: definition.Generation?.WorldSeed ?? 0, instanceIdentity: instance, site: descriptor),
                WorldGeneratorEngine.ComputeStreamId(descriptor), site.DrawCursor, site.DrawDecks, out var fired, out var reason, draw.Secret)) {
                throw new InvalidOperationException(reason);
            }
            selected = (int)(((ulong)fired.Numeric!.Value * (ulong)cells.Count) >> 32);
            rows[drawIndex] = site with { DrawCursor = checked(site.DrawCursor + fired.Samples), Cells = [new(WorldStateRow.SlotKey, fired.Numeric.Value)] };
        }
        if (selected < 0) {
            throw new InvalidOperationException("source zone does not contain the selected token");
        }
        var token = cells[selected];
        cells.RemoveAt(selected);
        var target = from == to ? cells : (destination.Cells ?? []).ToList();
        if (target.Any(c => c.Key == token.Key)) {
            throw new InvalidOperationException("destination already contains the token");
        }
        target.Insert(transfer.InsertFirst ? 0 : target.Count, token);
        rows[from] = source with { Cells = cells };
        rows[to] = destination with { Cells = target };
    }

    private static void SetRay(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.SetRay ray) {
        var index = Find(rows, ray.Row);
        var row = rows[index];
        if (row.Board is not { } board || WorldTopologyCompilation.Find(definition.StateRaw, board.Topology) is not { } topology ||
            !topology.TryCell(ray.From, out var origin) || topology.Direction(ray.Direction) < 0 || ray.Through == ray.Until) {
            throw new InvalidOperationException("setRay requires a board origin, valid direction and distinct through/until values");
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
            throw new InvalidOperationException("setRay requires a nonempty matching run and a closing endpoint");
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
    }

    /// <summary>Gets the absolute phase deadline, including a newly authored initial phase.</summary>
    /// <param name="phase">The phase state.</param>
    /// <param name="rate">Simulation ticks per second.</param>
    /// <returns>The deadline, or zero for none.</returns>
    public static long Deadline(WorldStatePhase phase, int rate) => phase.Sequence == 0 && phase.DeadlineTick == 0
        ? checked((long)decimal.Ceiling(phase.Phases[phase.Current].TimeoutSeconds * rate)) : phase.DeadlineTick;

    private static void Complete(WorldDefinition definition, WorldStateRow[] rows, WorldStateTransform.CompletePhase complete, WorldPrincipal actor, ulong tick) {
        var index = Find(rows, complete.Row);
        var row = rows[index];
        var phase = row.Phase ?? throw new InvalidOperationException("completion requires a phase row");
        if ((complete.ExpectedSequence is { } expected && expected != phase.Sequence) || (complete.ExpectedSequence is null && actor != WorldPrincipal.World)) {
            throw new InvalidOperationException("phase completion requires the current sequence");
        }
        var node = phase.Phases[phase.Current];
        var deadline = Deadline(phase, definition.SimulationRateHz);
        var participant = -1;
        var actorName = actor.Describe();
        if (complete.Participant is not null) {
            if (actor != WorldPrincipal.World) {
                throw new InvalidOperationException("only the world program may name another participant");
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
                throw new InvalidOperationException("timeout completion requires the world program and an expired deadline");
            }
        } else {
            if (deadline > 0 && tick >= (ulong)deadline) {
                throw new InvalidOperationException("phase deadline has expired");
            }
            if (node.Mode == WorldPhaseMode.Resolution ? actor != WorldPrincipal.World : participant < 0 ||
                (node.Mode == WorldPhaseMode.Sequential && participant != phase.Active) ||
                (node.Mode == WorldPhaseMode.Together && (phase.Ready & (1u << participant)) != 0)) {
                throw new InvalidOperationException("actor is not eligible to complete this phase");
            }
        }
        if (complete.Next is not null && actor != WorldPrincipal.World) {
            throw new InvalidOperationException("only the world program may branch a phase transition");
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
                throw new InvalidOperationException("next phase is not declared");
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
        throw new InvalidOperationException($"'{token}' is not a declared participant");
    }

    private static void Order(WorldStateRow[] rows, WorldStateTransform.TurnOrder order, WorldPrincipal actor) {
        if (actor != WorldPrincipal.World) {
            throw new InvalidOperationException("only the world program may reshape turn order");
        }
        var index = Find(rows, order.Row);
        var row = rows[index];
        var phase = row.Phase ?? throw new InvalidOperationException("turn order requires a phase row");
        if (order.Direction is { } direction && direction is not (1 or -1)) {
            throw new InvalidOperationException("turn order direction must be 1 or -1");
        }
        var next = phase with { Direction = order.Direction ?? phase.Direction };
        foreach (var token in order.Skip ?? []) { next = next with { Skipped = next.Skipped | (1u << ParticipantOrdinal(phase, token)) }; }
        foreach (var token in order.Unskip ?? []) { next = next with { Skipped = next.Skipped & ~(1u << ParticipantOrdinal(phase, token)) }; }
        if (order.Active is { } activeToken) {
            var active = ParticipantOrdinal(phase, activeToken);
            if (IsSkipped(next, active)) {
                throw new InvalidOperationException("the activated participant is skipped");
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
    }
}

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.World;

/// <summary>The World boundary of the arena reader. Live callers supply their arena; declaration validation reads seeds.</summary>
public static class WorldStateReader {
    private static readonly ConditionalWeakTable<WorldDefinition, StateArena> Seeds = new();
    private static readonly ConditionalWeakTable<WorldDefinition, StateArena> Live = new();

    /// <summary>Binds a runtime projection to its sole gameplay store.</summary>
    public static void Bind(WorldDefinition definition, StateArena arena) => Live.AddOrUpdate(definition, arena);
    /// <summary>Finds the arena already bound to a runtime projection.</summary>
    public static bool TryBoundArena(WorldDefinition definition, [NotNullWhen(true)] out StateArena? arena) => Live.TryGetValue(definition, out arena);
    /// <summary>Resolves the sole arena of a runtime projection, or materializes declaration seeds.</summary>
    public static StateArena Arena(WorldDefinition definition) => ReadArena(definition);

    /// <summary>Materializes values only at an explicit export or edit boundary.</summary>
    public static WorldDefinition Export(WorldDefinition definition, StateArena? arena = null) {
        arena ??= ReadArena(definition);
        return definition.WithWorldState(rows: arena.ToRows().Select(row => row.HostOwned ? WorldDefinitionRows.FindStateRow(definition.AuthoredState, row.Name)! : row as WorldStateRow ?? new WorldStateRow(row, false, null)).ToArray(),
            pools: arena.Catalog.Pools.Any(static pool => !pool.IsPair) ? arena.ToPools() : null,
            pairPools: arena.Catalog.Pools.Any(static pool => pool.IsPair) ? arena.ToPairPools() : null);
    }

    private static StateArena ReadArena(WorldDefinition definition) => Live.TryGetValue(definition, out var arena) ? arena : Seed(definition);

    /// <summary>Builds a read-only view of declaration seeds at an admission or edit boundary.</summary>
    public static StateArena Seed(WorldDefinition definition) => Seeds.GetValue(key: definition, createValueCallback: static source =>
        new StateArena(catalog: source.StateCatalog, section: source.StateRaw, time: ArenaTime.Origin));

    private static ArenaTime Time(WorldDefinition definition, ulong tick, ulong engineTick) =>
        new(Tick: tick, EngineTick: engineTick, TicksPerSecond: definition.SimulationRateHz, Dynamics: definition.Dynamics);

    /// <summary>Reads a live number or text from the supplied arena, or declaration seeds at a cold boundary.</summary>
    public static bool TryRead(WorldDefinition definition, string rowName, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out WorldStateRow? row, out long? rawValue, out string? text, StateArena? arena = null) {
        var resolved = StateReader.TryRead(arena: arena ?? ReadArena(definition), rowName: rowName, key: key, tick: tick,
            engineTick: engineTick, row: out var found, rawValue: out rawValue, text: out text);
        row = found is null ? null : found as WorldStateRow ?? WorldDefinitionRows.FindStateRow(definition.State, found.Name);
        return resolved;
    }

    /// <summary>Reads a typed cell, including a vector, without reading a seed cell.</summary>
    public static bool TryReadValue(WorldDefinition definition, string rowName, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out WorldStateRow? row, out CellValue value, StateArena? arena = null) =>
        ReadValue(definition, arena ?? ReadArena(definition), rowName, key, tick, engineTick, false, out row, out value);

    /// <summary>Reads an eased presentation value.</summary>
    public static bool TryReadEasedValue(WorldDefinition definition, string rowName, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out WorldStateRow? row, out CellValue value, StateArena? arena = null) =>
        ReadValue(definition, arena ?? ReadArena(definition), rowName, key, tick, engineTick, true, out row, out value);

    private static bool ReadValue(WorldDefinition definition, StateArena arena, string rowName, string? key, ulong tick, ulong engineTick,
        bool eased, [NotNullWhen(true)] out WorldStateRow? row, out CellValue value) {
        value = default;
        if (!arena.Catalog.TryResolve(lane: StateLane.Document, name: rowName, handle: out var handle)) { row = null; return false; }
        row = arena.Rows[handle.Ordinal] as WorldStateRow ?? WorldDefinitionRows.FindStateRow(definition.State, rowName)!;
        if (!CellName.TryParse(candidate: key ?? StateRow.SlotKey.Value, name: out var name, reason: out _) ||
            !arena.Keys.TryResolve(name: name, key: out var cell)) { return true; }
        var time = Time(definition, tick, engineTick);
        if (eased) { arena.TryReadEased(rowOrdinal: handle.Ordinal, key: cell, time: in time, value: out value); }
        else { arena.TryReadLive(rowOrdinal: handle.Ordinal, key: cell, time: in time, value: out value); }
        return true;
    }

    /// <summary>Reads an eased number or text.</summary>
    public static bool TryReadEased(WorldDefinition definition, string rowName, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out WorldStateRow? row, out long? rawValue, out string? text, StateArena? arena = null) {
        var resolved = TryReadEasedValue(definition: definition, arena: arena, rowName: rowName, key: key, tick: tick,
            engineTick: engineTick, row: out row, value: out var value);
        rawValue = value.HasValue && (value.Kind != CellKind.Vector) ? (value.Kind == CellKind.Text ? 0L : value.Raw) : null;
        text = value.HasValue && (value.Kind == CellKind.Text) ? value.AsText : null;
        return resolved;
    }

    /// <summary>Reads through a catalog-bound handle.</summary>
    public static bool TryReadHandle(WorldDefinition definition, StateCatalog catalog, StateHandle handle, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out WorldStateRow? row, out long? rawValue, out string? text, StateArena? arena = null) {
        var resolved = StateReader.TryReadHandle(arena: arena ?? ReadArena(definition), catalog: catalog, handle: handle, key: key, tick: tick,
            engineTick: engineTick, row: out var found, rawValue: out rawValue, text: out text);
        row = (WorldStateRow?)found;
        return resolved;
    }

    /// <summary>Reads a follower's value and velocity from its arena clock.</summary>
    public static bool TryEvaluateDynamics(WorldDefinition definition, StateRow row, StateCell cell, ulong tick,
        [NotNullWhen(true)] out StateDynamics? trait, out SecondOrderSample sample, StateArena? arena = null) {
        arena ??= ReadArena(definition);
        trait = null; sample = default;
        if (!arena.Catalog.TryResolve(lane: StateLane.Document, name: row.Name, handle: out var handle) ||
            !arena.Keys.TryResolve(name: cell.Key, key: out var key)) { return false; }
        var time = Time(definition, tick, 0UL);
        if (!arena.TryReadDynamics(rowOrdinal: handle.Ordinal, key: key, time: in time, sample: out sample)) { return false; }
        trait = arena.LiveBehavior(rowOrdinal: handle.Ordinal, key: key).Dynamics!;
        return true;
    }

    /// <summary>Reduces a live row.</summary>
    public static FixedQ4816 Reduce(WorldDefinition definition, string rowName, StateReduceOp op, ulong tick, ulong engineTick, StateArena? arena = null) {
        arena ??= ReadArena(definition);
        if (!arena.Catalog.TryResolve(lane: StateLane.Document, name: rowName, handle: out var handle)) { return FixedQ4816.Zero; }
        var raw = StateReader.ReduceRaw(arena: arena, rowOrdinal: handle.Ordinal, op: op, tick: tick, engineTick: engineTick);
        return (op == StateReduceOp.Count || op == StateReduceOp.ArrangementRank || arena.Layout[handle.Ordinal].Kind != CellKind.Fixed) ?
            StateReader.LiftSaturating(raw) : FixedQ4816.FromRawBits(raw);
    }

    /// <summary>Finds the winning participant key.</summary>
    public static string? ArgExtremum(WorldDefinition definition, string rowName, StateReduceOp op, ulong tick, ulong engineTick,
        Func<int, bool>? isCandidateIndex = null, StateArena? arena = null) => ArgExtremum(definition: definition, arena: arena,
            rowName: rowName, op: op, tick: tick, engineTick: engineTick, state: isCandidateIndex,
            isCandidateIndex: static (index, predicate) => predicate?.Invoke(index) ?? true);

    /// <summary>Finds the winning participant key using caller-owned filter state.</summary>
    public static string? ArgExtremum<TState>(WorldDefinition definition, string rowName, StateReduceOp op, ulong tick, ulong engineTick,
        TState state, Func<int, TState, bool> isCandidateIndex, StateArena? arena = null) {
        arena ??= ReadArena(definition);
        return arena.Catalog.TryResolve(lane: StateLane.Document, name: rowName, handle: out var handle) ?
            StateReader.ArgExtremum(arena: arena, handle: handle, op: op, tick: tick, engineTick: engineTick, state: state, isCandidateIndex: isCandidateIndex) : null;
    }

    /// <summary>Finds the winning participant key through a bound handle.</summary>
    public static string? ArgExtremum<TState>(WorldDefinition definition, StateCatalog catalog, StateHandle handle, StateReduceOp op, ulong tick,
        ulong engineTick, TState state, Func<int, TState, bool> isCandidateIndex, StateArena? arena = null) {
        arena ??= ReadArena(definition);
        if (!ReferenceEquals(arena.Catalog, catalog)) { throw new ArgumentException(message: "The catalog is stale.", paramName: nameof(catalog)); }
        return StateReader.ArgExtremum(arena: arena, handle: handle, op: op, tick: tick, engineTick: engineTick, state: state, isCandidateIndex: isCandidateIndex);
    }

    /// <summary>Converts a numeric cell to its presentation number.</summary>
    public static bool TryNumber(CellValue value, out double number) {
        number = 0d;
        if (!value.HasValue) { return false; }
        switch (value.Kind) {
            case CellKind.Int: number = value.AsInt; return true;
            case CellKind.Fixed: number = (double)FixedQ4816.FromRawBits(value.AsFixed); return true;
            case CellKind.Bool: number = value.AsBool ? 1d : 0d; return true;
            default: return false;
        }
    }
}

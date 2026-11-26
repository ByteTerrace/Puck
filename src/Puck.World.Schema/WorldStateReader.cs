using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Puck.Maths;

namespace Puck.World;

/// <summary>The World boundary of the arena reader. Live callers supply their arena; declaration validation reads seeds.</summary>
public static class WorldStateReader {
    private static readonly Puck.Abstractions.CodeDerivedTable<WorldStateSection, StateArena> Seeds = new();
    // The key every definition that authors no state section shares: they all compile the one empty catalog.
    private static readonly WorldStateSection AbsentSection = new();
    [SideTable(reason: "binds a runtime projection to the arena its host owns; the arena is state, not derived from the definition")]
    private static readonly ConditionalWeakTable<WorldDefinition, StateArena> Live = new();
    private static readonly Puck.Abstractions.CodeDerivedTable<StateRow, WorldStateRow> RowMetadata = new();

    private static WorldStateRow WorldRow(StateRow row) => row as WorldStateRow ??
        RowMetadata.GetValue(row, static declaration => new WorldStateRow(declaration, false, null));

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

    /// <summary>Gets the read-only view of a document's declaration seeds an admission or edit boundary reads.</summary>
    /// <remarks>The view is a function of the state section alone (its rows and the catalog compiled from them), so
    /// it is built once per section and every definition holding that section reads the same one: a candidate that
    /// changed no state builds none.</remarks>
    /// <param name="definition">The document whose seeds are read.</param>
    /// <returns>The seed view; the caller only reads it.</returns>
    /// <exception cref="ArgumentException">The state section does not load into an arena.</exception>
    public static StateArena Seed(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var section = (definition.StateRaw ?? AbsentSection);

        if (Seeds.TryGetValue(key: section, value: out var seed)) {
            return seed;
        }

        WorldBootWork.Count(kind: WorldBootWork.ArenaBuilds);
        WorldBootWork.Count(kind: WorldBootWork.ArenaSeedBuilds);
        seed = new StateArena(catalog: definition.StateCatalog, section: definition.StateRaw, time: ArenaTime.Origin);

        return Seeds.GetValue(key: section, derive: _ => seed);
    }

    private static ArenaTime Time(WorldDefinition definition, ulong tick, ulong engineTick) =>
        new(Tick: tick, EngineTick: engineTick, TicksPerSecond: definition.SimulationRateHz, Dynamics: definition.Dynamics);

    /// <summary>Reads a live number or text from the supplied arena, or declaration seeds at a cold boundary.</summary>
    public static bool TryRead(WorldDefinition definition, string rowName, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out WorldStateRow? row, out long? rawValue, out string? text, StateArena? arena = null) {
        var resolved = StateReader.TryRead(arena: arena ?? ReadArena(definition), rowName: rowName, key: key, tick: tick,
            engineTick: engineTick, row: out var found, rawValue: out rawValue, text: out text);
        row = found is null ? null : WorldRow(found);
        return resolved;
    }

    /// <summary>Reads a <c>Fixed</c> cell by name, for a consumer that binds a number to a row and keeps its own value
    /// where the row does not answer.</summary>
    /// <param name="definition">The document whose arena is read.</param>
    /// <param name="rowName">The state row.</param>
    /// <param name="key">The cell, or <see langword="null"/> for the row's slot cell.</param>
    /// <param name="tick">The tick to read as of.</param>
    /// <param name="engineTick">The engine tick to read as of.</param>
    /// <param name="fallback">The value a missing row, a missing cell or a row of another kind answers.</param>
    /// <param name="arena">The live store, or <see langword="null"/> for the one bound to
    /// <paramref name="definition"/>.</param>
    /// <returns>The cell's value, or <paramref name="fallback"/>.</returns>
    public static FixedQ4816 ReadFixed(WorldDefinition definition, string rowName, string? key, ulong tick, ulong engineTick, FixedQ4816 fallback, StateArena? arena = null) => (
        (
            TryRead(arena: arena, definition: definition, engineTick: engineTick, key: key, rawValue: out var raw, row: out var row, rowName: rowName, text: out _, tick: tick) &&
            (row.Kind == CellKind.Fixed) &&
            (raw is { } value)
        )
        ? FixedQ4816.FromRawBits(value: value)
        : fallback
    );

    /// <summary>Reads the turn a <c>Fixed</c> row's slot cell stands at, one whole unit a turn
    /// (<see cref="StateArena.TryReadLiveTurn"/>): exact for an advancing clock row, so an orbit it turns moves smoothly
    /// between ticks rather than in steps of a 65,536th of a turn.</summary>
    /// <param name="definition">The document whose arena is read.</param>
    /// <param name="rowName">The state row.</param>
    /// <param name="tick">The tick to read as of.</param>
    /// <param name="engineTick">The engine tick to read as of.</param>
    /// <param name="turn">The fraction of a turn scaled by 2^64 on success; otherwise zero.</param>
    /// <param name="arena">The live store, or <see langword="null"/> for the one bound to
    /// <paramref name="definition"/>.</param>
    /// <returns><see langword="true"/> when the row holds a <c>Fixed</c> slot cell.</returns>
    public static bool TryReadTurn(WorldDefinition definition, string rowName, ulong tick, ulong engineTick, out ulong turn, StateArena? arena = null) {
        turn = 0UL;
        arena ??= ReadArena(definition);
        if (!arena.Catalog.TryResolve(lane: StateLane.Document, name: rowName, handle: out var handle) ||
            !arena.Keys.TryResolve(name: StateRow.SlotKey, key: out var cell)) {
            return false;
        }
        var time = Time(definition, tick, engineTick);

        return arena.TryReadLiveTurn(rowOrdinal: handle.Ordinal, key: cell, time: in time, turn: out turn);
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
        row = WorldRow(arena.Rows[handle.Ordinal]);
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
        row = found is null ? null : WorldRow(found);
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

    /// <summary>Finds the winning participant key using caller-owned filter state.</summary>
    public static string? ArgExtremum<TState>(WorldDefinition definition, string rowName, StateReduceOp op, ulong tick, ulong engineTick,
        TState state, Func<int, TState, bool> isCandidateIndex, StateArena? arena = null) {
        arena ??= ReadArena(definition);
        return arena.Catalog.TryResolve(lane: StateLane.Document, name: rowName, handle: out var handle) ?
            StateReader.ArgExtremum(arena: arena, handle: handle, op: op, tick: tick, engineTick: engineTick, state: state, isCandidateIndex: isCandidateIndex) : null;
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

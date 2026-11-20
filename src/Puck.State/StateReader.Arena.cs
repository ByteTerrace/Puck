using System.Diagnostics.CodeAnalysis;

namespace Puck.State;

public static partial class StateReader {
    /// <summary>Resolves a name at an external read boundary, then reads the live arena.</summary>
    public static bool TryRead(StateArena arena, string rowName, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out StateRow? row, out long? rawValue, out string? text) {
        if (!arena.Catalog.TryResolve(lane: StateLane.Document, name: rowName, handle: out var handle)) {
            row = null; rawValue = null; text = null; return false;
        }
        return TryReadHandle(arena: arena, catalog: arena.Catalog, handle: handle, key: key, tick: tick,
            engineTick: engineTick, row: out row, rawValue: out rawValue, text: out text);
    }

    /// <summary>Reads a catalog-bound row; a stale handle is an error, and an absent cell has no value.</summary>
    public static bool TryReadHandle(StateArena arena, StateCatalog catalog, StateHandle handle, string? key,
        ulong tick, ulong engineTick, [NotNullWhen(true)] out StateRow? row, out long? rawValue, out string? text) {
        if (!ReferenceEquals(arena.Catalog, catalog) || !TryResolveRowHandle(arena: arena, catalog: catalog, handle: handle, rowOrdinal: out var ordinal, row: out row)) {
            throw new ArgumentException(message: "The handle does not address a current document row.", paramName: nameof(handle));
        }
        ReadCell(arena: arena, rowOrdinal: ordinal, key: key, tick: tick, engineTick: engineTick, rawValue: out rawValue, text: out text);
        return true;
    }

    /// <summary>Checks a bound address against this arena's catalog.</summary>
    public static bool TryResolveRowHandle(StateArena arena, StateCatalog catalog, StateHandle handle, out int rowOrdinal, [NotNullWhen(true)] out StateRow? row) {
        if (ReferenceEquals(arena.Catalog, catalog) && catalog.TryGetDescriptor(handle: handle, descriptor: out var descriptor) && (descriptor.Lane == StateLane.Document)) {
            rowOrdinal = handle.Ordinal; row = arena.Rows[descriptor.LaneOrdinal]; return true;
        }
        rowOrdinal = -1; row = null; return false;
    }

    /// <summary>Reads an admitted row and cell from columns.</summary>
    public static void ReadCell(StateArena arena, int rowOrdinal, string? key, ulong tick, ulong engineTick, out long? rawValue, out string? text) =>
        ReadCell(arena.ReadAccess, arena.ReadAccess.AuthorityRow(rowOrdinal), key, tick, engineTick, out rawValue, out text);

    /// <summary>Reads a named cell in the acquired row and returns its numeric or text representation.</summary>
    public static void ReadCell(StateReadAccess arena, StateReadRow rowOrdinal, string? key, ulong tick, ulong engineTick, out long? rawValue, out string? text) {
        rawValue = null; text = null;
        var time = ArenaTime.At(tick: tick, engineTick: engineTick);
        if (!arena.TryReadNamedValue(row: rowOrdinal, name: key, time: in time, value: out var value)) { return; }
        if (value.Kind == CellKind.Text) { rawValue = 0L; text = value.AsText; }
        else if (value.Kind != CellKind.Vector) { rawValue = value.Raw; }
    }

    /// <summary>Reduces live cells in member order, without materializing rows.</summary>
    public static long ReduceRaw(StateReadAccess arena, StateReadRow rowOrdinal, StateReduceOp op, ulong tick, ulong engineTick) {
        var time = ArenaTime.At(tick: tick, engineTick: engineTick);
        return ReduceRaw(arena, rowOrdinal, op, in time);
    }

    /// <summary>Reduces admitted cells at the evaluator's time with an optional key-matched filter and numeric range.</summary>
    public static long ReduceRaw(StateReadAccess arena, StateReadRow rowOrdinal, StateReduceOp op, in ArenaTime time,
        StateReadRow? filter = null, (long Lower, long Upper)? range = null) {
        if (filter is { } required) { arena.EnsureAvailable(required); }
        if (op == StateReduceOp.Count && filter is null && range is null) { return arena.CellCount(rowOrdinal); }
        if (op == StateReduceOp.ArrangementRank) {
            if ((arena.Domain(rowOrdinal) is not StateDomain.KeysOf { Ordered: true } zone) ||
                !arena.TryResolveRow(name: zone.Row, rowOrdinal: out var domain)) { return -1L; }
            Span<int> ordinals = stackalloc int[MaxArrangementTokens];
            var count = DomainOrdinals(arena, rowOrdinal, domain, ordinals);
            return count < 0 ? -1L : ArrangementRank(domainOrdinals: ordinals[..count]);
        }
        var cursor = 0;
        var any = false;
        var result = 0L;
        while (arena.TryNextCell(rowOrdinal, ref cursor, out var key)) {
            if (filter is { } gate && (!arena.TryReadLiveNumber(gate, key, in time, out var enabled) || enabled == 0)) { continue; }
            if (!arena.TryReadLiveNumber(rowOrdinal, key, in time, out var value) ||
                range is { } bounds && (value < bounds.Lower || value > bounds.Upper)) { continue; }
            result = op switch {
                StateReduceOp.Count => result + 1,
                StateReduceOp.Sum => Puck.Maths.BinaryIntegerFunctions.AddSaturating(result, value),
                StateReduceOp.Max => any ? Math.Max(result, value) : value,
                StateReduceOp.Min => any ? Math.Min(result, value) : value,
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
            any = true;
        }
        return result;
    }

    /// <summary>Reduces an authority catalog row through the shared read kernel.</summary>
    public static long ReduceRaw(StateArena arena, int rowOrdinal, StateReduceOp op, ulong tick, ulong engineTick) =>
        ReduceRaw(arena.ReadAccess, arena.ReadAccess.AuthorityRow(rowOrdinal), op, tick, engineTick);

    /// <summary>Resolves the evaluator's authority ordinals before entering the shared reduction.</summary>
    public static long ReduceRaw(StateArena arena, int rowOrdinal, StateReduceOp op, in ArenaTime time,
        int filterOrdinal, (long Lower, long Upper)? range) {
        if ((uint)rowOrdinal >= (uint)arena.Catalog.Count || filterOrdinal >= arena.Catalog.Count) { return 0L; }
        var access = arena.ReadAccess;
        return ReduceRaw(access, access.AuthorityRow(rowOrdinal), op, in time,
            filterOrdinal < 0 ? null : access.AuthorityRow(filterOrdinal), range);
    }

    /// <summary>Returns the winning live cell key, resolving equal values to the lowest participant index.</summary>
    public static string? ArgExtremum<TState>(StateArena arena, StateHandle handle, StateReduceOp op, ulong tick, ulong engineTick,
        TState state, Func<int, TState, bool> isCandidateIndex) {
        if (!arena.Catalog.TryGetDescriptor(handle: handle, descriptor: out _)) { throw new ArgumentException(message: "The handle is stale.", paramName: nameof(handle)); }
        var time = ArenaTime.At(tick: tick, engineTick: engineTick);
        var cursor = 0; var best = -1; var bestValue = 0L; string? bestKey = null;
        while (arena.TryNextCell(rowOrdinal: handle.Ordinal, cursor: ref cursor, key: out var key)) {
            if (!arena.Keys.TryCandidateIndex(key: key, index: out var index) || !isCandidateIndex(index, state) ||
                !arena.TryReadLiveNumber(rowOrdinal: handle.Ordinal, key: key, time: in time, value: out var raw)) { continue; }
            if ((best < 0) || (op == StateReduceOp.Max ? raw > bestValue : raw < bestValue) || ((raw == bestValue) && (index < best))) {
                best = index; bestValue = raw; bestKey = arena.Keys[key].Value;
            }
        }
        return bestKey;
    }
}

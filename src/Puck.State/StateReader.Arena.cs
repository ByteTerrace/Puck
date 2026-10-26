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
    public static void ReadCell(StateArena arena, int rowOrdinal, string? key, ulong tick, ulong engineTick, out long? rawValue, out string? text) {
        rawValue = null; text = null;
        if (!CellName.TryParse(candidate: key ?? StateRow.SlotKey.Value, name: out var name, reason: out _) ||
            !arena.Keys.TryResolve(name: name, key: out var cell)) { return; }
        var time = ArenaTime.At(tick: tick, engineTick: engineTick);
        if (!arena.TryReadLive(rowOrdinal: rowOrdinal, key: cell, time: in time, value: out var value)) { return; }
        if (value.Kind == CellKind.Text) { rawValue = 0L; text = value.AsText; }
        else if (value.Kind != CellKind.Vector) { rawValue = value.Raw; }
    }

    /// <summary>Reduces live cells in member order, without materializing rows.</summary>
    public static long ReduceRaw(StateArena arena, int rowOrdinal, StateReduceOp op, ulong tick, ulong engineTick) {
        if (op == StateReduceOp.Count) { return arena.CellCount(rowOrdinal: rowOrdinal); }
        if (op == StateReduceOp.ArrangementRank) {
            var row = arena.Rows[rowOrdinal];
            if ((row.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } zone) ||
                !arena.Catalog.TryResolve(lane: StateLane.Document, name: zone.Row, handle: out var domain)) { return -1L; }
            Span<int> ordinals = stackalloc int[MaxArrangementTokens];
            var count = DomainOrdinals(arena: arena, rowOrdinal: rowOrdinal, domainOrdinal: domain.Ordinal, ordinals: ordinals);
            return count < 0 ? -1L : ArrangementRank(domainOrdinals: ordinals[..count]);
        }
        var time = ArenaTime.At(tick: tick, engineTick: engineTick);
        var cursor = 0; var any = false; var result = 0L;
        while (arena.TryNextCell(rowOrdinal: rowOrdinal, cursor: ref cursor, key: out var key)) {
            if (!arena.TryReadLiveNumber(rowOrdinal: rowOrdinal, key: key, time: in time, value: out var raw)) { continue; }
            result = !any ? raw : op switch { StateReduceOp.Sum => unchecked(result + raw), StateReduceOp.Max => Math.Max(result, raw), _ => Math.Min(result, raw) };
            any = true;
        }
        return result;
    }

    /// <summary>Returns the winning live cell key, resolving equal values to the lowest participant index.</summary>
    public static string? ArgExtremum<TState>(StateArena arena, StateHandle handle, StateReduceOp op, ulong tick, ulong engineTick,
        TState state, Func<int, TState, bool> isCandidateIndex) {
        if (!arena.Catalog.TryGetDescriptor(handle: handle, descriptor: out _)) { throw new ArgumentException(message: "The handle is stale.", paramName: nameof(handle)); }
        var time = ArenaTime.At(tick: tick, engineTick: engineTick);
        var cursor = 0; var best = -1; var bestValue = 0L; string? bestKey = null;
        while (arena.TryNextCell(rowOrdinal: handle.Ordinal, cursor: ref cursor, key: out var key)) {
            var name = arena.Keys[key].Value;
            if (!TryParseCandidateIndex(key: name, index: out var index) || !isCandidateIndex(index, state) ||
                !arena.TryReadLiveNumber(rowOrdinal: handle.Ordinal, key: key, time: in time, value: out var raw)) { continue; }
            if ((best < 0) || (op == StateReduceOp.Max ? raw > bestValue : raw < bestValue) || ((raw == bestValue) && (index < best))) {
                best = index; bestValue = raw; bestKey = name;
            }
        }
        return bestKey;
    }
}

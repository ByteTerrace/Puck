using System.Diagnostics.CodeAnalysis;
using Puck.Maths;

namespace Puck.State;

/// <summary>Materializes declaration seeds at validation and edit boundaries. Gameplay reads use StateReader.</summary>
public static class StateSeedReader {
    private static StateArena Create(IReadOnlyList<StateRow>? rows, StateCatalog? catalog = null, IReadOnlyList<DynamicsRow>? dynamics = null) {
        var section = new StateSection(Rows: rows);
        if (!StateArena.TryCreate(catalog: catalog ?? StateCatalog.Compile(section), section: section,
            options: default, time: ArenaTime.At(0, 0), arena: out var arena, reason: out var reason)) { throw new InvalidOperationException(reason); }
        return arena;
    }
    /// <summary>Reads a materialized seed.</summary>
    public static bool TryRead(IReadOnlyList<StateRow>? rows, string rowName, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out StateRow? row, out long? rawValue, out string? text) =>
        StateReader.TryRead(Create(rows), rowName, key, tick, engineTick, out row, out rawValue, out text);
    /// <summary>Reads a bound materialized seed.</summary>
    public static bool TryReadHandle(IReadOnlyList<StateRow> rows, StateCatalog catalog, StateHandle handle, string? key, ulong tick, ulong engineTick,
        [NotNullWhen(true)] out StateRow? row, out long? rawValue, out string? text) =>
        StateReader.TryReadHandle(Create(rows, catalog), catalog, handle, key, tick, engineTick, out row, out rawValue, out text);
    /// <summary>Reads one declaration's seed.</summary>
    public static void ReadCell(StateRow row, string? key, ulong tick, ulong engineTick, out long? rawValue, out string? text) =>
        StateReader.ReadCell(Create([row]), 0, key, tick, engineTick, out rawValue, out text);
    /// <summary>Reduces one declaration's seed.</summary>
    public static long ReduceRaw(StateRow row, StateReduceOp op, ulong tick, ulong engineTick) => StateReader.ReduceRaw(Create([row]), 0, op, tick, engineTick);
    /// <summary>Reduces materialized seeds.</summary>
    public static FixedQ4816 Reduce(IReadOnlyList<StateRow>? rows, string rowName, StateReduceOp op, ulong tick, ulong engineTick) {
        var arena = Create(rows);
        if (!arena.Catalog.TryResolve(StateLane.Document, rowName, out var handle)) { return FixedQ4816.Zero; }
        var raw = StateReader.ReduceRaw(arena, handle.Ordinal, op, tick, engineTick);
        return op is StateReduceOp.Count or StateReduceOp.ArrangementRank || arena.Rows[handle.Ordinal].Kind != CellKind.Fixed ? StateReader.LiftSaturating(raw) : FixedQ4816.FromRawBits(raw);
    }
    /// <summary>Finds an extremum among seeds.</summary>
    public static string? ArgExtremum(IReadOnlyList<StateRow>? rows, string rowName, StateReduceOp op, ulong tick, ulong engineTick, Func<int, bool>? isCandidateIndex = null) =>
        ArgExtremum(rows, rowName, op, tick, engineTick, isCandidateIndex, static (index, predicate) => predicate?.Invoke(index) ?? true);
    /// <summary>Finds an extremum among seeds.</summary>
    public static string? ArgExtremum<TState>(IReadOnlyList<StateRow>? rows, string rowName, StateReduceOp op, ulong tick, ulong engineTick, TState state, Func<int, TState, bool> isCandidateIndex) {
        var arena = Create(rows);
        return arena.Catalog.TryResolve(StateLane.Document, rowName, out var handle) ? StateReader.ArgExtremum(arena, handle, op, tick, engineTick, state, isCandidateIndex) : null;
    }
    /// <summary>Finds an extremum through a seed handle.</summary>
    public static string? ArgExtremum<TState>(IReadOnlyList<StateRow> rows, StateCatalog catalog, StateHandle handle, StateReduceOp op, ulong tick, ulong engineTick, TState state, Func<int, TState, bool> isCandidateIndex) =>
        StateReader.ArgExtremum(Create(rows, catalog), handle, op, tick, engineTick, state, isCandidateIndex);
    /// <summary>Ranks an ordered seed zone.</summary>
    public static long ArrangementRank(IReadOnlyList<StateRow>? rows, StateRow zone) {
        Span<int> ordinals = stackalloc int[StateReader.MaxArrangementTokens];
        var count = DomainOrdinals(rows, zone, ordinals);
        return count < 0 ? -1 : StateReader.ArrangementRank(ordinals[..count]);
    }
    /// <summary>Resolves a seed zone's domain order.</summary>
    public static int DomainOrdinals(IReadOnlyList<StateRow>? rows, StateRow zone, Span<int> ordinals) {
        var arena = Create(rows);
        return zone.EffectiveDomain is StateDomain.KeysOf domain && arena.Catalog.TryResolve(StateLane.Document, zone.Name, out var handle) && arena.Catalog.TryResolve(StateLane.Document, domain.Row, out var source) ? StateReader.DomainOrdinals(arena, handle.Ordinal, source.Ordinal, ordinals) : -1;
    }
    /// <summary>Evaluates a follower while settling an edited declaration.</summary>
    public static bool TryEvaluateDynamics(IReadOnlyList<DynamicsRow>? dynamics, int ticksPerSecond, StateRow row, StateCell cell, ulong tick,
        [NotNullWhen(true)] out StateDynamics? trait, out SecondOrderSample sample) {
        var arena = Create([row]);
        trait = null; sample = default;
        var time = new ArenaTime(Dynamics: dynamics, TicksPerSecond: ticksPerSecond, Tick: tick, EngineTick: 0);
        if (!arena.Keys.TryResolve(cell.Key, out var key) || !arena.TryReadDynamics(0, key, in time, out sample)) { return false; }
        trait = arena.LiveBehavior(0, key).Dynamics!;
        return true;
    }
}

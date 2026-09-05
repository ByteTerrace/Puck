using System.Diagnostics.CodeAnalysis;
using Puck.Maths;

namespace Puck.World;

/// <summary>The document-anchored entrance to <see cref="StateReader"/>: every overload resolves the rows, the
/// catalog, the dynamics rows, and the simulation rate off one <see cref="WorldDefinition"/>, so a reader holding the
/// document never re-derives which section the engine reads. The reading itself is the engine's — see
/// <see cref="StateReader"/> for the pair rule, the computed value, and the allocation contract.</summary>
public static class WorldStateReader {
    /// <summary>Resolves one world-owned row by its compiled typed handle (see
    /// <see cref="StateReader.TryReadHandle(IReadOnlyList{StateRow}, StateCatalog, StateHandle, string?, ulong, out StateRow?, out long?, out string?)"/>).</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="catalog">The document's current state catalog.</param>
    /// <param name="handle">A world-lane handle minted by <paramref name="catalog"/>.</param>
    /// <param name="key">The cell key, or <see langword="null"/> for the slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="row">The resolved row.</param>
    /// <param name="rawValue">The addressed live raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed text payload, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row resolves; the call throws rather than answering
    /// <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="catalog"/> is not the definition's current catalog, or
    /// <paramref name="handle"/> does not address a world-owned row in it.</exception>
    public static bool TryReadHandle(
        WorldDefinition definition,
        StateCatalog catalog,
        StateHandle handle,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out WorldStateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: catalog);

        if (!ReferenceEquals(objA: definition.StateCatalog, objB: catalog)) {
            throw new ArgumentException(message: "The state catalog is not current for this definition.", paramName: nameof(catalog));
        }

        _ = StateReader.TryReadHandle(rows: definition.State, catalog: catalog, handle: handle, key: key, tick: tick, row: out var resolved, rawValue: out rawValue, text: out text);
        row = (WorldStateRow)resolved!;

        return true;
    }
    /// <summary>Finds the winning cell's key over a keyed row (see
    /// <see cref="StateReader.ArgExtremum(IReadOnlyList{StateRow}?,string,StateReduceOp,ulong,Func{int,bool}?)"/>).</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The extremum to find.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="isCandidateIndex">An optional filter over a cell key's parsed index.</param>
    public static string? ArgExtremum(WorldDefinition definition, string rowName, StateReduceOp op, ulong tick, Func<int, bool>? isCandidateIndex = null) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return StateReader.ArgExtremum(rows: definition.State, rowName: rowName, op: op, tick: tick, isCandidateIndex: isCandidateIndex);
    }
    /// <summary>Finds the winning cell's key over a keyed row with caller state (see
    /// <see cref="StateReader.ArgExtremum{TState}(IReadOnlyList{StateRow}?,string,StateReduceOp,ulong,TState,Func{int,TState,bool})"/>).</summary>
    /// <typeparam name="TState">The caller's filter-state carrier.</typeparam>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The extremum to find.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="state">State passed to <paramref name="isCandidateIndex"/>.</param>
    /// <param name="isCandidateIndex">The allocation-free candidate predicate.</param>
    public static string? ArgExtremum<TState>(WorldDefinition definition, string rowName, StateReduceOp op, ulong tick, TState state, Func<int, TState, bool> isCandidateIndex) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return StateReader.ArgExtremum(rows: definition.State, rowName: rowName, op: op, tick: tick, state: state, isCandidateIndex: isCandidateIndex);
    }
    /// <summary>Finds the winning cell's key through a compiled handle (see
    /// <see cref="StateReader.ArgExtremum{TState}(IReadOnlyList{StateRow},StateCatalog,StateHandle,StateReduceOp,ulong,TState,Func{int,TState,bool})"/>).</summary>
    /// <typeparam name="TState">The caller's filter-state carrier.</typeparam>
    /// <param name="definition">The document to read.</param>
    /// <param name="catalog">The document's current state catalog.</param>
    /// <param name="handle">The compiled handle for the row to search.</param>
    /// <param name="op">The extremum to find.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="state">State passed to <paramref name="isCandidateIndex"/>.</param>
    /// <param name="isCandidateIndex">The allocation-free candidate predicate.</param>
    public static string? ArgExtremum<TState>(WorldDefinition definition, StateCatalog catalog, StateHandle handle, StateReduceOp op, ulong tick, TState state, Func<int, TState, bool> isCandidateIndex) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return StateReader.ArgExtremum(rows: definition.State, catalog: catalog, handle: handle, op: op, tick: tick, state: state, isCandidateIndex: isCandidateIndex);
    }
    /// <summary>Reduces a keyed row's cell values (see <see cref="StateReader.Reduce"/>).</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="op">The reduction to apply.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    public static FixedQ4816 Reduce(WorldDefinition definition, string rowName, StateReduceOp op, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return StateReader.Reduce(rows: definition.State, rowName: rowName, op: op, tick: tick);
    }
    /// <summary>Resolves one (row, key) pair against the document's live <c>state</c> section (see
    /// <see cref="StateReader.TryRead(IReadOnlyList{StateRow}?, string, string?, ulong, out StateRow?, out long?, out string?)"/>).</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="key">The cell key inside the row, or <see langword="null"/> for the row's slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="row">The named row, or <see langword="null"/> when the section declares none by that name.</param>
    /// <param name="rawValue">The addressed cell's live raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed cell's text payload, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row resolved.</returns>
    public static bool TryRead(
        WorldDefinition definition,
        string rowName,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out WorldStateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var resolved = StateReader.TryRead(rows: definition.State, rowName: rowName, key: key, tick: tick, row: out var found, rawValue: out rawValue, text: out text);
        row = (WorldStateRow?)found;

        return resolved;
    }
    /// <summary>Resolves one (row, key) pair, reading an eased cell's follower rather than its stored truth (see
    /// <see cref="StateReader.TryReadEased"/>).</summary>
    /// <param name="definition">The document to read.</param>
    /// <param name="rowName">The state row's name.</param>
    /// <param name="key">The cell key inside the row, or <see langword="null"/> for the row's slot cell.</param>
    /// <param name="tick">The tick this read answers as of.</param>
    /// <param name="row">The named row, or <see langword="null"/> when the section declares none by that name.</param>
    /// <param name="rawValue">The addressed cell's live eased raw value, or <see langword="null"/> when absent.</param>
    /// <param name="text">The addressed cell's text payload, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the row resolved.</returns>
    public static bool TryReadEased(
        WorldDefinition definition,
        string rowName,
        string? key,
        ulong tick,
        [NotNullWhen(true)] out WorldStateRow? row,
        out long? rawValue,
        out string? text
    ) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        var resolved = StateReader.TryReadEased(rows: definition.State, dynamics: definition.Dynamics, ticksPerSecond: definition.SimulationRateHz, rowName: rowName, key: key, tick: tick, row: out var found, rawValue: out rawValue, text: out text);
        row = (WorldStateRow?)found;

        return resolved;
    }
    /// <summary>Evaluates a cell's easing trait against the document's dynamics rows and rate (see
    /// <see cref="StateReader.TryEvaluateDynamics"/>).</summary>
    /// <param name="definition">The document to resolve the trait's referenced <c>dynamics</c> row against.</param>
    /// <param name="row">The carrying row.</param>
    /// <param name="cell">The addressed cell.</param>
    /// <param name="tick">The tick to evaluate at.</param>
    /// <param name="trait">The resolved trait, or <see langword="null"/>.</param>
    /// <param name="sample">The evaluated value/velocity.</param>
    /// <returns><see langword="true"/> when a trait resolved and was evaluated.</returns>
    public static bool TryEvaluateDynamics(WorldDefinition definition, StateRow row, StateCell cell, ulong tick, [NotNullWhen(true)] out StateDynamics? trait, out SecondOrderSample sample) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        return StateReader.TryEvaluateDynamics(dynamics: definition.Dynamics, ticksPerSecond: definition.SimulationRateHz, row: row, cell: cell, tick: tick, trait: out trait, sample: out sample);
    }
}

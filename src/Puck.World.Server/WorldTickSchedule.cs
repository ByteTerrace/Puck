namespace Puck.World.Server;

/// <summary>Rows keyed by the simulation tick they act on, each handed out at most once.</summary>
/// <typeparam name="TRow">The scheduled row.</typeparam>
/// <remarks>An authority's timeline can rewind (a replay, a restore) and publish a tick again. A row is an event the
/// document wrote once, so a second publication of its tick hands out nothing: the row stays claimed for the life of
/// the schedule. Publishing allocates nothing.</remarks>
public sealed class WorldTickSchedule<TRow> {
    private readonly Dictionary<ulong, List<TRow>> m_entries = [];

    /// <summary>Gets the number of rows still waiting for their tick.</summary>
    public int PendingCount { get; private set; }
    /// <summary>Gets pending rows by tick, then arming order, for operator read-back.</summary>
    public IEnumerable<(ulong Tick, TRow Row)> Pending {
        get {
            foreach (var (tick, rows) in m_entries.OrderBy(keySelector: static entry => entry.Key)) {
                foreach (var row in rows) {
                    yield return (tick, row);
                }
            }
        }
    }

    /// <summary>Adds a row at a tick. Rows at one tick are handed out in the order they were added.</summary>
    /// <param name="tick">The simulation tick the row acts on.</param>
    /// <param name="row">The row.</param>
    public void Add(ulong tick, TRow row) {
        if (!m_entries.TryGetValue(
            key: tick,
            value: out var atTick
        )) {
            atTick = [];
            m_entries[tick] = atTick;
        }

        atTick.Add(item: row);
        PendingCount++;
    }
    /// <summary>Drops every pending row, returning the number dropped.</summary>
    /// <returns>The number dropped.</returns>
    public int Clear() {
        var dropped = PendingCount;

        m_entries.Clear();
        PendingCount = 0;
        return dropped;
    }
    /// <summary>Claims a tick's rows before the caller dispatches them, including a deferred host drain.</summary>
    /// <param name="tick">The completed tick.</param>
    /// <param name="rows">The due rows in arming order, or an empty list.</param>
    /// <returns>Whether any rows were due.</returns>
    public bool TryTake(ulong tick, out IReadOnlyList<TRow> rows) {
        if (!m_entries.Remove(key: tick, value: out var due)) {
            rows = [];
            return false;
        }
        PendingCount -= due.Count;
        rows = due;
        return true;
    }
    /// <summary>Hands every unclaimed row at a tick to <paramref name="fire"/>, claiming each before it fires so a
    /// row that publishes the same tick re-entrantly is not handed out twice.</summary>
    /// <typeparam name="TState">The caller's state, passed through so <paramref name="fire"/> can be a static
    /// delegate.</typeparam>
    /// <param name="tick">The just-completed simulation tick.</param>
    /// <param name="state">The caller's state.</param>
    /// <param name="fire">Receives each due row.</param>
    /// <returns>The number of rows handed out.</returns>
    public int Publish<TState>(ulong tick, TState state, Action<TState, TRow> fire) {
        ArgumentNullException.ThrowIfNull(argument: fire);

        if (!TryTake(rows: out var atTick, tick: tick)) {
            return 0;
        }

        var fired = 0;

        for (var index = 0; (index < atTick.Count); index++) {
            fired++;
            fire(
                arg1: state,
                arg2: atTick[index]
            );
        }

        return fired;
    }
}

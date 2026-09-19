namespace Puck.World.Server;

/// <summary>Rows keyed by the simulation tick they act on, each handed out at most once.</summary>
/// <typeparam name="TRow">The scheduled row.</typeparam>
/// <remarks>An authority's timeline can rewind (a replay, a restore) and publish a tick again. A row is an event the
/// document wrote once, so a second publication of its tick hands out nothing: the row stays claimed for the life of
/// the schedule. Publishing allocates nothing.</remarks>
public sealed class WorldTickSchedule<TRow> {
    private sealed class Entry(TRow row) {
        public bool Claimed;
        public TRow Row = row;
    }

    private readonly Dictionary<ulong, List<Entry>> m_entries = [];

    /// <summary>Gets the number of rows added, claimed or not.</summary>
    public int Count { get; private set; }

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

        atTick.Add(item: new Entry(row: row));
        Count++;
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

        if (!m_entries.TryGetValue(
            key: tick,
            value: out var atTick
        )) {
            return 0;
        }

        var fired = 0;

        for (var index = 0; (index < atTick.Count); index++) {
            var entry = atTick[index];

            if (entry.Claimed) {
                continue;
            }

            entry.Claimed = true;
            fired++;
            fire(
                arg1: state,
                arg2: entry.Row
            );
        }

        return fired;
    }
}

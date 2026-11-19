namespace Puck.World.Client;

/// <summary>One reading of the world's views' load (<see cref="IWorldFrameLoadSource"/>): the views' renders read back
/// since the previous reading of the same signal, summed.</summary>
/// <param name="Renders">The number of view renders the reading sums; zero for a reading with nothing new, which no
/// sample is taken from.</param>
/// <param name="Load">The renders' summed GPU time, or a present interval, in seconds.</param>
/// <param name="Grid">The render grid every summed render recorded its passes at, as a fraction of the output on each
/// axis, or zero when they recorded different grids or one recorded none.</param>
/// <param name="QueueStart">When the earliest summed render began, in seconds on its queue's timestamp clock, or zero
/// when no summed render gave its span.</param>
/// <param name="QueueEnd">When the latest summed render ended, on the same clock, or zero when no summed render gave its
/// span.</param>
public readonly record struct WorldFrameLoadReading(int Renders, double Load, double Grid, double QueueStart = 0d, double QueueEnd = 0d) {
    /// <summary>Gets whether the reading sums at least one render not read before.</summary>
    public bool IsFresh => (Renders > 0);
}
/// <summary>
/// Sums the views' newly read-back renders into one <see cref="WorldFrameLoadReading"/>. Each view hands over its
/// latest read-back render, named by its node's submission; a render is counted once, by the first reading it is
/// handed to. A view whose latest render was already counted, a standing
/// view, contributes nothing to the next reading: no load, since it cost the frame no GPU work, no freshness, and no
/// grid. So a reading is fresh exactly when some view rendered and was read back since the previous one, whichever
/// view that was, and its load is the cost of the renders that are new.
/// </summary>
public sealed class WorldFrameLoadAggregate {
    private long[] m_counted = [];
    private double m_grid;
    private double m_load;
    private int m_renders;
    private double m_queueStart;
    private double m_queueEnd;

    /// <summary>Takes a new set of views: a view that survives from the previous set keeps the render it counted, and
    /// every other view's next render is new.</summary>
    /// <param name="survivors">For each view of the new set, in order, its index in the previous set, or -1 for a view
    /// the previous set did not hold.</param>
    /// <exception cref="ArgumentOutOfRangeException">A survivor's index is not below the previous set's size.</exception>
    public void Reset(ReadOnlySpan<int> survivors) {
        var counted = new long[survivors.Length];

        for (var view = 0; (view < survivors.Length); view++) {
            var previous = survivors[view];

            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(previous, m_counted.Length, nameof(survivors));
            counted[view] = ((previous < 0) ? 0L : m_counted[previous]);
        }

        m_counted = counted;
    }
    /// <summary>Starts a reading.</summary>
    public void Begin() {
        m_grid = 0d;
        m_load = 0d;
        m_renders = 0;
        m_queueStart = double.PositiveInfinity;
        m_queueEnd = double.NegativeInfinity;
    }
    /// <summary>Hands over one view's latest read-back render. A render the view already handed over adds nothing.</summary>
    /// <param name="view">The view's index in the set the last <see cref="Reset"/> took.</param>
    /// <param name="render">The render's identity, which grows with each of the view's renders; zero or less for
    /// none.</param>
    /// <param name="load">The render's load, in the signal's unit.</param>
    /// <param name="grid">The grid the render recorded its passes at, or zero when it is unknown.</param>
    /// <param name="queueStart">When the render began, in seconds on its queue's timestamp clock.</param>
    /// <param name="queueEnd">When it ended, on the same clock; no later than <paramref name="queueStart"/> for a render
    /// whose span is unknown, which widens the reading's span not at all.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="view"/> is negative or past the views.</exception>
    public void Add(int view, long render, double load, double grid, double queueStart = 0d, double queueEnd = 0d) {
        ArgumentOutOfRangeException.ThrowIfNegative(view);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(view, m_counted.Length);

        if (render <= m_counted[view]) {
            return;
        }

        m_counted[view] = render;
        m_grid = ((m_renders == 0) ? grid : ((m_grid == grid) ? m_grid : 0d));
        m_load += load;
        m_renders++;
        if (queueEnd > queueStart) {
            m_queueStart = Math.Min(val1: m_queueStart, val2: queueStart);
            m_queueEnd = Math.Max(val1: m_queueEnd, val2: queueEnd);
        }
    }
    /// <summary>Ends the reading begun by <see cref="Begin"/>.</summary>
    /// <returns>The renders handed over since <see cref="Begin"/> that no earlier reading counted, summed, and the span
    /// on the queue's clock from the earliest's start to the latest's end.</returns>
    public WorldFrameLoadReading End() => ((m_queueEnd > m_queueStart)
        ? new(Grid: m_grid, Load: m_load, QueueEnd: m_queueEnd, QueueStart: m_queueStart, Renders: m_renders)
        : new(Grid: m_grid, Load: m_load, Renders: m_renders));
}

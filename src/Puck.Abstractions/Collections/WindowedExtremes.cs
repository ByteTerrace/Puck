namespace Puck.Abstractions.Collections;

/// <summary>
/// The least and greatest of the most recent values added, over a fixed window: the filter a delay or clock estimate
/// takes over samples a jittered link spreads upward (the least of them is the link's own delay) or downward (the greatest
/// of them is the least delayed). A window, rather than every value since the start, lets the estimate follow a drift
/// in the values within one window's length.
/// </summary>
/// <remarks>The window is a ring scanned once per <see cref="Add"/>, which for the windows a control loop takes (tens of
/// values) is cheaper than a monotonic queue's bookkeeping. Not thread-safe.</remarks>
public sealed class WindowedExtremes {
    private readonly long[] m_values;
    private int m_next;

    /// <summary>Initializes an empty window.</summary>
    /// <param name="length">The most recent values the extremes are taken over; at least one.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is below one.</exception>
    public WindowedExtremes(int length) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: length, other: 1);
        m_values = new long[length];
    }

    /// <summary>Gets the values the window holds, at most its length.</summary>
    public int Count { get; private set; }
    /// <summary>Gets the least value the window holds; zero while it holds none.</summary>
    public long Minimum { get; private set; }
    /// <summary>Gets the greatest value the window holds; zero while it holds none.</summary>
    public long Maximum { get; private set; }

    /// <summary>Adds a value, forgetting the oldest once the window is full, and takes the extremes again.</summary>
    /// <param name="value">The value.</param>
    public void Add(long value) {
        m_values[m_next] = value;
        m_next = ((m_next + 1) % m_values.Length);
        Count = Math.Min(val1: (Count + 1), val2: m_values.Length);

        var held = m_values.AsSpan(start: 0, length: Count);
        var least = held[0];
        var greatest = held[0];

        foreach (var sample in held) {
            least = Math.Min(val1: least, val2: sample);
            greatest = Math.Max(val1: greatest, val2: sample);
        }
        Minimum = least;
        Maximum = greatest;
    }
    /// <summary>Forgets every value.</summary>
    public void Clear() {
        m_next = 0;
        Count = 0;
        Minimum = 0L;
        Maximum = 0L;
    }
}

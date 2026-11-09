namespace Puck.Abstractions.Collections;

/// <summary>A count per non-negative index, over a fixed index range, that a window clears at a cost proportional to
/// the indices it counted rather than to the range: a per-tick meter indexed by body, cleared every tick.</summary>
/// <remarks>Not thread-safe. The counted indices are tracked by a <see cref="MarkedIndexSet"/>.</remarks>
public sealed class MarkedIndexCounts {
    private readonly int[] m_counts;
    private readonly MarkedIndexSet m_counted = new();

    /// <summary>Initializes a new instance of the <see cref="MarkedIndexCounts"/> class, every count zero.</summary>
    /// <param name="length">The number of indices, each in <c>[0, length)</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public MarkedIndexCounts(int length) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: length);
        m_counts = new int[length];
    }

    /// <summary>Gets the number of indices.</summary>
    public int Length => m_counts.Length;

    /// <summary>Gets or sets the count at <paramref name="index"/>.</summary>
    /// <param name="index">The index, in <c>[0, Length)</c>.</param>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the range.</exception>
    public int this[int index] {
        get => m_counts[index];
        set {
            m_counts[index] = value;
            if (value != 0) { m_counted.Add(index: index); }
        }
    }

    /// <summary>Returns every count to zero, visiting only the indices set since the last clear.</summary>
    public void Clear() {
        foreach (var index in m_counted.Members) { m_counts[index] = 0; }
        m_counted.Clear();
    }
}

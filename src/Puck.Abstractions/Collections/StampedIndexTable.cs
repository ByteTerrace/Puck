using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Collections;

/// <summary>A value per non-negative index, each held only while it carries the table's current stamp, so reading,
/// writing and removing one cost an array access and forgetting every value costs nothing. It serves a per-frame
/// judgement keyed by a dense identity (a transform slot, a draw row) that a hashed dictionary would charge a hash and
/// a probe for on every visit.</summary>
/// <typeparam name="T">The value type.</typeparam>
/// <remarks>Not thread-safe. The arrays grow to the largest index written and are kept across
/// <see cref="Clear"/>.</remarks>
public sealed class StampedIndexTable<T> {
    private T[] m_values = [];
    private int[] m_stamps = [];
    private int m_stamp = 1;

    /// <summary>Returns the value held at <paramref name="index"/>.</summary>
    /// <param name="index">The index to read; a negative index holds nothing.</param>
    /// <param name="value">The value, or the default when none is held.</param>
    /// <returns><see langword="true"/> when a value was written there since the last <see cref="Clear"/> and not
    /// removed.</returns>
    public bool TryGetValue(int index, out T value) {
        if ((((uint)index) < ((uint)m_stamps.Length)) && (m_stamps[index] == m_stamp)) {
            value = m_values[index];
            return true;
        }
        value = default!;
        return false;
    }
    /// <summary>Holds <paramref name="value"/> at <paramref name="index"/>, growing the table to reach it.</summary>
    /// <param name="index">The non-negative index.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public void Set(int index, T value) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        if (index >= m_stamps.Length) {
            var length = ArrayCapacity.Grown(current: m_stamps.Length, required: checked(index + 1));

            Array.Resize(array: ref m_stamps, newSize: length);
            Array.Resize(array: ref m_values, newSize: length);
        }
        m_values[index] = value;
        m_stamps[index] = m_stamp;
    }
    /// <summary>Removes the value at <paramref name="index"/>, if any.</summary>
    /// <param name="index">The index; a negative or unwritten index is left as it is.</param>
    public void Remove(int index) {
        if (((uint)index) < ((uint)m_stamps.Length)) { m_stamps[index] = 0; }
    }
    /// <summary>Forgets every value at once by moving the stamp; values are released to the collector only as their
    /// indices are written again.</summary>
    public void Clear() {
        if (++m_stamp == 0) {
            Array.Clear(array: m_stamps);
            m_stamp = 1;
        }
    }
    /// <summary>Returns the dense index of a key that names either a non-negative identity of one kind or the
    /// complement (<c>~</c>) of an identity of another: the two kinds interleave, so both stay dense.</summary>
    /// <param name="key">The key: an identity, or the complement of one.</param>
    /// <returns>The index.</returns>
    public static int IndexOfSigned(int key) => ((key >= 0) ? (key << 1) : (((~key) << 1) | 1));
}

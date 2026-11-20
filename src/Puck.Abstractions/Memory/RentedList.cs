namespace Puck.Abstractions.Memory;

/// <summary>
/// A growable run of elements whose storage comes from <see cref="RentedArrays"/>. Spans and references it hands out
/// are borrows that end at the next append or at disposal, which releases its storage.
/// </summary>
/// <typeparam name="T">The element.</typeparam>
/// <param name="capacity">The elements the first rented array holds at least.</param>
public sealed class RentedList<T>(int capacity = 0) : IDisposable {
    private T[] m_items = RentedArrays.Rent<T>(minimumLength: capacity);
    private int m_count;

    /// <summary>Gets the elements appended since the list was created or last cleared.</summary>
    public int Count => m_count;

    /// <summary>Gets a borrowed reference to an element.</summary>
    /// <param name="index">The element's index, below <see cref="Count"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not below
    /// <see cref="Count"/>.</exception>
    public ref T this[int index] {
        get {
            if (((uint)index) >= ((uint)m_count)) { throw new ArgumentOutOfRangeException(paramName: nameof(index)); }
            return ref m_items[index];
        }
    }

    /// <summary>Appends an element, growing the rented storage geometrically when it is full.</summary>
    /// <param name="item">The element.</param>
    public void Add(T item) {
        if (m_count == m_items.Length) { Grow(minimumLength: (m_count + 1)); }
        m_items[m_count++] = item;
    }
    /// <summary>Appends elements, growing the rented storage geometrically when they do not fit.</summary>
    /// <param name="items">The elements, borrowed through the copy.</param>
    public void AddRange(ReadOnlySpan<T> items) {
        if ((m_count + items.Length) > m_items.Length) { Grow(minimumLength: (m_count + items.Length)); }
        items.CopyTo(destination: m_items.AsSpan(start: m_count));
        m_count += items.Length;
    }
    /// <summary>Empties the list, keeping its storage.</summary>
    public void Clear() => m_count = 0;
    /// <summary>Returns a borrow of the appended elements.</summary>
    /// <returns>The elements, valid until the next append or disposal.</returns>
    public Span<T> AsSpan() => m_items.AsSpan(start: 0, length: m_count);
    /// <summary>Copies the appended elements into a new array of exactly their count.</summary>
    /// <returns>The copy; the shared empty array when the list is empty.</returns>
    public T[] ToArray() => AsSpan().ToArray();
    /// <summary>Releases the rented storage and empties the list.</summary>
    public void Dispose() {
        var items = m_items;

        m_items = [];
        m_count = 0;
        RentedArrays.Return(array: items);
    }

    private void Grow(int minimumLength) => m_items = RentedArrays.Grow(array: m_items, keep: m_count, minimumLength: Math.Max(val1: minimumLength, val2: ((int)Math.Clamp(value: (2L * m_items.Length), min: 16L, max: Array.MaxLength))));
}

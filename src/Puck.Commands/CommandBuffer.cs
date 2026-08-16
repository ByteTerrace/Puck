using System.Collections;

namespace Puck.Commands;

/// <summary>A read-only view over one tick's borrowed command storage.</summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
/// The producing <see cref="InputRouter"/> owns and reuses the backing array. A view remains valid until that router's
/// next <see cref="InputRouter.SnapshotForTick"/> call. Consumers must read it synchronously or copy the elements they
/// need to retain. This explicit lifetime lets the fixed-step path reuse storage without presenting mutable memory as
/// an immutable collection.
/// </remarks>
public readonly struct CommandBuffer<T> : IReadOnlyList<T>, IEquatable<CommandBuffer<T>> {
    private readonly T[]? m_items;

    internal CommandBuffer(T[] items, int count) {
        m_items = items;
        Count = count;
    }

    /// <inheritdoc/>
    public int Count { get; }
    /// <summary>Gets whether this view contains no elements.</summary>
    public bool IsEmpty => (Count == 0);
    /// <summary>Gets the element count.</summary>
    public int Length => Count;
    /// <summary>Gets the view as a span with the same borrowed lifetime.</summary>
    public ReadOnlySpan<T> Span => ((m_items is null)
        ? []
        : m_items.AsSpan(
            start: 0,
            length: Count
        )
    );

    /// <inheritdoc/>
    public T this[int index] {
        get {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
                index,
                Count
            );

            return m_items![index];
        }
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Equals(CommandBuffer<T> other) => Span.SequenceEqual(other: other.Span);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is CommandBuffer<T> other) && Equals(other: other));
    /// <summary>Returns a non-allocating enumerator over the view.</summary>
    public Enumerator GetEnumerator() => new(
        items: m_items,
        count: Count
    );
    /// <inheritdoc/>
    public override int GetHashCode() {
        var hash = new HashCode();

        foreach (ref readonly var item in Span) {
            hash.Add(value: item);
        }

        return hash.ToHashCode();
    }

    /// <summary>Compares two views structurally.</summary>
    public static bool operator ==(CommandBuffer<T> left, CommandBuffer<T> right) => left.Equals(other: right);
    /// <summary>Compares two views structurally.</summary>
    public static bool operator !=(CommandBuffer<T> left, CommandBuffer<T> right) => !left.Equals(other: right);

    /// <summary>A non-allocating enumerator over a <see cref="CommandBuffer{T}"/>.</summary>
    public struct Enumerator : IEnumerator<T> {
        private readonly int m_count;
        private readonly T[]? m_items;

        private int m_index;

        internal Enumerator(T[]? items, int count) {
            m_count = count;
            m_items = items;
            m_index = -1;
        }

        readonly object? IEnumerator.Current => Current;

        /// <inheritdoc/>
        public readonly T Current => m_items![m_index];

        /// <inheritdoc/>
        public readonly void Dispose() {
        }
        /// <inheritdoc/>
        public bool MoveNext() => (++m_index < m_count);
        /// <inheritdoc/>
        public void Reset() => m_index = -1;
    }
}

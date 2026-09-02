using System.Collections;

namespace Puck.Commands;

/// <summary>A read-only view over one tick's borrowed command storage.</summary>
/// <typeparam name="T">The element type.</typeparam>
/// <remarks>
/// The producing <see cref="InputRouter"/> owns and reuses the backing array. A view remains valid until that router's
/// next <see cref="InputRouter.SnapshotForTick"/> call. Consumers must read it synchronously or copy the elements they
/// need to retain. This explicit lifetime lets the fixed-step path reuse storage without presenting mutable memory as
/// an immutable collection.
/// <para>The lifetime is ENFORCED, not merely documented: every view is stamped with the router's snapshot generation
/// at construction, and reading one after that router has built another snapshot throws
/// <see cref="InvalidOperationException"/> rather than silently answering with the newer tick's contents. A view
/// borrowing nothing — <see langword="default"/>, and the lanes of <see cref="CommandSnapshot.Empty"/> — is valid
/// forever.</para>
/// </remarks>
public readonly struct CommandBuffer<T> : IReadOnlyList<T>, IEquatable<CommandBuffer<T>> {
    private readonly int m_count;
    private readonly SnapshotGeneration? m_generation;
    private readonly T[]? m_items;
    private readonly ulong m_stamp;

    internal CommandBuffer(T[] items, int count, SnapshotGeneration? generation = null) {
        m_count = count;
        m_generation = generation;
        m_items = items;
        m_stamp = (generation?.Stamp ?? 0UL);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">The producing router has built another snapshot since this view
    /// was handed out.</exception>
    /// <remarks>Checked like every other read, and for the same reason: the count belongs to the tick that produced
    /// this view, and a consumer sizes a loop, an allocation, or a "did anything happen" branch off it. Answering
    /// from a retired view would be the quietest way to act on a tick the router has already overwritten.</remarks>
    public int Count {
        get {
            EnsureLive();

            return m_count;
        }
    }
    /// <summary>Gets whether this view contains no elements.</summary>
    /// <exception cref="InvalidOperationException">The producing router has built another snapshot since this view
    /// was handed out.</exception>
    public bool IsEmpty => (Count == 0);
    /// <summary>Gets the element count.</summary>
    /// <exception cref="InvalidOperationException">The producing router has built another snapshot since this view
    /// was handed out.</exception>
    public int Length => Count;
    /// <summary>Gets the view as a span with the same borrowed lifetime.</summary>
    /// <exception cref="InvalidOperationException">The producing router has built another snapshot since this view
    /// was handed out.</exception>
    public ReadOnlySpan<T> Span {
        get {
            EnsureLive();

            return ((m_items is null)
                ? []
                : m_items.AsSpan(
                    length: m_count,
                    start: 0
                )
            );
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not less than
    /// <see cref="Count"/>.</exception>
    /// <exception cref="InvalidOperationException">The producing router has built another snapshot since this view
    /// was handed out.</exception>
    public T this[int index] {
        get {
            EnsureLive();

            // ONE unsigned comparison covers both ends: a negative index reinterprets as a huge unsigned value and
            // fails here rather than dereferencing the (possibly null) backing array below.
            if (((uint)index) >= ((uint)m_count)) {
                throw new ArgumentOutOfRangeException(paramName: nameof(index));
            }

            return m_items![index];
        }
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // The borrowed lifetime, checked. A view that borrows no storage carries no generation and never expires; one
    // that does compares the stamp it was handed out under against the router's current one, so a snapshot retained
    // across the next tick fails loudly instead of quietly reading that tick's contents under the old tick number.
    private void EnsureLive() {
        if (
            (m_generation is { } generation) &&
            (generation.Stamp != m_stamp)
        ) {
            throw new InvalidOperationException(message: "This command view was borrowed from a snapshot whose producing router has since built another one. Read a snapshot within the tick that produced it, or copy the elements to retain them.");
        }
    }

    /// <inheritdoc/>
    public bool Equals(CommandBuffer<T> other) => Span.SequenceEqual(other: other.Span);
    /// <inheritdoc/>
    public override bool Equals(object? obj) => ((obj is CommandBuffer<T> other) && Equals(other: other));
    /// <summary>Returns a non-allocating enumerator over the view.</summary>
    /// <exception cref="InvalidOperationException">The producing router has built another snapshot since this view
    /// was handed out.</exception>
    public Enumerator GetEnumerator() {
        EnsureLive();

        return new Enumerator(
            count: m_count,
            items: m_items
        );
    }
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

// The lifetime token a router stamps onto every borrowed view it hands out. One instance per router, bumped once per
// snapshot: a view compares the stamp it captured against this one, which is what turns "valid until the next
// SnapshotForTick" from a remark into a check. A class rather than a counter read off the router so the comparison
// costs one field read through a reference the view already holds.
internal sealed class SnapshotGeneration {
    internal ulong Stamp;
}

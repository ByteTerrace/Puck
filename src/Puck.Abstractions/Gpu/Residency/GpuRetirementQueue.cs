namespace Puck.Abstractions.Gpu;

/// <summary>Objects an owner replaced while GPU work may still read them, each held until a completion point the owner
/// counts on one queue has passed: an upload's sequence number, a native fence value. One queue completes its submissions
/// in order, so the points are non-decreasing in retirement order and a release stops at the first one not yet reached.
/// Single-threaded, on the owner's thread.</summary>
/// <typeparam name="T">The retired object, or the handles it is released through.</typeparam>
public sealed class GpuRetirementQueue<T> {
    private readonly Queue<(ulong After, T Item)> m_items = new();

    private ulong m_last;

    /// <summary>Gets the number of objects still held.</summary>
    public int Count => m_items.Count;

    /// <summary>Reserves room for <paramref name="count"/> more retirements, so an owner can make every allocation that
    /// can fail before it publishes a replacement and a retirement then cannot fail.</summary>
    /// <param name="count">The number of retirements to make room for; not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public void Reserve(int count = 1) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _ = m_items.EnsureCapacity(capacity: checked((m_items.Count + count)));
    }
    /// <summary>Holds <paramref name="item"/> until completion point <paramref name="after"/> has passed.</summary>
    /// <param name="after">The completion point after which no GPU work reads the object.</param>
    /// <param name="item">The retired object.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="after"/> is earlier than the last retirement's
    /// point.</exception>
    public void Retire(ulong after, T item) {
        if ((m_items.Count > 0) && (after < m_last)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(after), message: $"Completion point {after} precedes the last retirement's {m_last}.");
        }
        m_last = after;
        m_items.Enqueue(item: (after, item));
    }
    /// <summary>Releases, in retirement order, every object whose completion point is at or before
    /// <paramref name="completed"/>.</summary>
    /// <param name="completed">The latest completion point known to have passed; <see cref="ulong.MaxValue"/> releases
    /// everything.</param>
    /// <param name="release">The release of one object.</param>
    public void Release(ulong completed, Action<T> release) {
        while (m_items.TryPeek(result: out var next) && (next.After <= completed)) {
            _ = m_items.Dequeue();
            release(next.Item);
        }
    }
}

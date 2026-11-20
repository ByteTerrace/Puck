namespace Puck.Abstractions.Gpu;

/// <summary>Objects an owner replaced while GPU work may still read them, each held until a completion point the owner
/// counts on one queue has passed: an upload's sequence number, a native fence value. One queue completes its submissions
/// in order, so the points are non-decreasing in retirement order and a release stops at the first one not yet reached.
/// Single-threaded, on the owner's thread.</summary>
/// <typeparam name="T">The retired object, or the handles it is released through.</typeparam>
public sealed class GpuRetirementQueue<T> {
    private readonly Queue<(ulong After, T Item)> m_items = new();
    private readonly List<(T Item, Func<bool>? PendingUse)> m_held = [];
    private readonly List<int> m_ready = [];
    private GpuQueueTimeline? m_timeline;

    private ulong m_last;

    /// <summary>Gets the number of objects still held.</summary>
    public int Count => (m_items.Count + m_held.Count);

    /// <summary>Reserves room for <paramref name="count"/> more retirements, so an owner can make every allocation that
    /// can fail before it publishes a replacement and a retirement then cannot fail.</summary>
    /// <param name="count">The number of retirements to make room for; not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public void Reserve(int count = 1) {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var capacity = checked(Count + count);
        _ = m_items.EnsureCapacity(capacity: capacity);
        _ = m_held.EnsureCapacity(capacity: capacity);
        _ = m_ready.EnsureCapacity(capacity: capacity);
    }
    /// <summary>Holds a replaced object until its owner's pending uses have been submitted or cancelled. The next
    /// <see cref="Poll"/> queues one device checkpoint behind all eligible objects' last submissions. Pending objects
    /// do not block independent retirements. The caller must stop publishing new uses before holding the object.</summary>
    /// <param name="item">The replaced object.</param>
    /// <param name="pendingUse">Whether work not yet submitted still needs the object; null means none. Called on the
    /// submission thread, never concurrently with recording.</param>
    public void Hold(T item, Func<bool>? pendingUse = null) {
        Reserve();
        m_held.Add((item, pendingUse));
    }
    /// <summary>Queues completion proof for eligible held objects and releases the completed prefix. Call even when
    /// the owner renders nothing. A failed checkpoint retains every object. One queue cannot cross device generations.</summary>
    /// <param name="timeline">The device's timeline; the same instance on every call.</param>
    /// <param name="release">Releases an object whose last use completed.</param>
    public void Poll(GpuQueueTimeline timeline, Action<T> release) {
        if ((m_timeline is not null) && !ReferenceEquals(m_timeline, timeline)) {
            throw new InvalidOperationException("Retirement cannot cross device timelines.");
        }
        m_timeline = timeline;
        m_ready.Clear();
        for (var index = 0; index < m_held.Count; index++) {
            if (!(m_held[index].PendingUse?.Invoke() ?? false)) { m_ready.Add(index); }
        }
        if (m_ready.Count > 0) {
            var after = timeline.Checkpoint();
            foreach (var index in m_ready) { Retire(after, m_held[index].Item); }
            for (var index = m_ready.Count - 1; index >= 0; index--) { m_held.RemoveAt(m_ready[index]); }
        }
        Release(timeline.Completed, release);
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
    /// everything, held objects included, and ends the queue's timeline, so a device recreated after a loss polls it with
    /// its new one.</param>
    /// <param name="release">The release of one object.</param>
    public void Release(ulong completed, Action<T> release) {
        while (m_items.TryPeek(result: out var next) && (next.After <= completed)) {
            _ = m_items.Dequeue();
            release(next.Item);
        }
        if (completed == ulong.MaxValue) {
            foreach (var held in m_held) { release(held.Item); }
            m_held.Clear();
            m_timeline = null;
            m_last = 0;
        }
    }
}

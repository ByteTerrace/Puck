using System.Buffers;

namespace Puck.Abstractions.Memory;

/// <summary>Receives allocation deltas from an explicit storage owner, including allocate/copy overlap.</summary>
public interface IAllocationObserver {
    /// <summary>Accounts bytes acquired (positive) or released (negative).</summary>
    void Change(long bytes);
}

/// <summary>Optional physical allocator readings; these include allocator slack and are not payload counts.</summary>
public interface IAllocatorStatistics {
    /// <summary>Reads the allocator's committed and reserved address-space bytes.</summary>
    (long Committed, long Reserved) ReadStatistics();
    /// <summary>Asks the allocator to release idle storage. Live allocations are unaffected.</summary>
    void Collect();
}

/// <summary>
/// One owner's reference-free storage. An injected allocator selects native backing; offline callers may use managed
/// backing. Spans and memory slices are borrows: all must end before resize or disposal.
/// Growth allocates, copies and publishes before freeing the old block. There is no finalizer recovery.
/// </summary>
/// <typeparam name="T">The unmanaged element.</typeparam>
public sealed unsafe class OwnedBuffer<T>(IAllocator? allocator, IAllocationObserver? observer = null) : MemoryManager<T> where T : unmanaged {
    private T* m_pointer;
    private T[]? m_managed;
    private int m_capacity;
    private int m_length;
    private bool m_disposed;

    /// <summary>Gets the logical element count.</summary>
    public int Length => m_length;
    /// <summary>Gets the storage capacity.</summary>
    public int Capacity => m_capacity;
    /// <summary>Gets a borrowed reference to an element.</summary>
    public ref T this[int index] => ref GetSpan()[index];
    /// <summary>Enumerates a borrow of the logical contents.</summary>
    public Span<T>.Enumerator GetEnumerator() => GetSpan().GetEnumerator();
    /// <inheritdoc/>
    public override Span<T> GetSpan() {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        return allocator is null ? m_managed.AsSpan(0, m_length) : new Span<T>(m_pointer, m_length);
    }
    /// <summary>Changes the logical length, preserving existing entries and optionally clearing new entries.</summary>
    public void Resize(int length, bool clear = true) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > m_capacity) {
            var bytes = checked((long)length * sizeof(T));
            if (allocator is null) {
                var replacement = new T[length];
                GetSpan().CopyTo(replacement);
                observer?.Change(bytes);
                var oldManagedBytes = checked((long)m_capacity * sizeof(T));
                m_managed = replacement;
                m_capacity = m_length = length;
                observer?.Change(-oldManagedBytes);
                return;
            }
            var candidate = (T*)allocator.Allocate(checked((nuint)bytes));
            if (candidate is null) { throw new OutOfMemoryException($"Cannot allocate {bytes} bytes of {typeof(T).Name} storage."); }
            try {
                new ReadOnlySpan<T>(m_pointer, m_length).CopyTo(new Span<T>(candidate, length));
                if (clear) { new Span<T>(candidate + m_length, length - m_length).Clear(); }
                observer?.Change(bytes);
            } catch {
                allocator.Free(candidate);
                throw;
            }
            var old = m_pointer;
            var oldBytes = checked((long)m_capacity * sizeof(T));
            m_pointer = candidate;
            m_capacity = length;
            m_length = length;
            allocator.Free(old);
            observer?.Change(-oldBytes);
        } else {
            if (clear && length > m_length) {
                if (allocator is null) { m_managed.AsSpan(m_length, length - m_length).Clear(); }
                else { new Span<T>(m_pointer + m_length, length - m_length).Clear(); }
            }
            m_length = length;
        }
    }
    /// <summary>Replaces the logical contents after earlier borrows have ended.</summary>
    public void CopyFrom(ReadOnlySpan<T> source) {
        Resize(source.Length, clear: false);
        source.CopyTo(GetSpan());
    }
    /// <inheritdoc/>
    public override MemoryHandle Pin(int elementIndex = 0) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)elementIndex, (uint)m_length);
        if (allocator is null) { return m_managed.AsMemory(elementIndex, m_length - elementIndex).Pin(); }
        return new MemoryHandle(m_pointer + elementIndex);
    }
    /// <inheritdoc/>
    public override void Unpin() { }
    /// <inheritdoc/>
    protected override void Dispose(bool disposing) {
        if (m_disposed) { return; }
        m_disposed = true;
        var bytes = checked((long)m_capacity * sizeof(T));
        if (allocator is not null) { allocator.Free(m_pointer); }
        m_managed = null;
        m_pointer = null;
        m_capacity = m_length = 0;
        observer?.Change(-bytes);
    }
    /// <summary>Releases the owned storage after its last borrow ends.</summary>
    public void Dispose() => Dispose(disposing: true);
}

using System.Buffers;

namespace Puck.Abstractions.Memory;

/// <summary>Receives allocation deltas from an explicit storage owner, including allocate/copy overlap, each naming the
/// physical row its storage lies in.</summary>
public interface IAllocationObserver {
    /// <summary>Gets the storage owner reported on refusal.</summary>
    string Name => GetType().Name;
    /// <summary>Charges new host storage before allocation. A refusal leaves the owner unchanged.</summary>
    /// <param name="bytes">The size of the allocation, including all of a replacement.</param>
    /// <param name="backing">The physical row the storage will occupy.</param>
    /// <returns>Whether the charge was taken. Release it if allocation fails.</returns>
    bool TryAcquire(long bytes, MemoryBacking backing) {
        Change(bytes, backing);
        return true;
    }
    /// <summary>Charges an allocation or throws a refusal naming the owner.</summary>
    /// <param name="bytes">The requested bytes.</param>
    /// <param name="backing">The allocation's row.</param>
    void Acquire(long bytes, MemoryBacking backing) {
        if (!TryAcquire(bytes, backing)) { throw new HostMemoryRefusedException(Name, bytes); }
    }
    /// <summary>Accounts bytes acquired (positive) or released (negative).</summary>
    /// <param name="bytes">The signed allocation delta, in bytes, including growth overlap.</param>
    /// <param name="backing">The physical row the storage lies in, which the storage making the charge states; a release
    /// names the row its acquisition named.</param>
    void Change(long bytes, MemoryBacking backing);
}
/// <summary>A host owner was refused before allocating a buffer.</summary>
/// <param name="owner">The owner whose work needed the buffer.</param>
/// <param name="bytes">The requested allocation size.</param>
public sealed class HostMemoryRefusedException(string owner, long bytes)
    : Exception($"The host memory account refused {bytes} bytes for {owner}.") {
    /// <summary>Gets the owner whose work needed the buffer.</summary>
    public string Owner { get; } = owner;
    /// <summary>Gets the requested allocation size, in bytes.</summary>
    public long Bytes { get; } = bytes;

    /// <summary>Returns the refusal an exception reports: the exception itself, or the first refusal of an aggregate
    /// (parallel work) whose every inner exception is a refusal.</summary>
    /// <param name="exception">The exception a piece of work threw.</param>
    /// <returns>The refusal, or <see langword="null"/> when the exception is, or holds, anything else.</returns>
    public static HostMemoryRefusedException? Of(Exception exception) {
        ArgumentNullException.ThrowIfNull(argument: exception);
        if (exception is HostMemoryRefusedException refused) { return refused; }
        if (exception is not AggregateException aggregate) { return null; }

        HostMemoryRefusedException? first = null;

        foreach (var inner in aggregate.Flatten().InnerExceptions) {
            if (inner is not HostMemoryRefusedException each) { return null; }
            first ??= each;
        }
        return first;
    }
}
/// <summary>Optional physical allocator readings; these include allocator slack and are not payload counts.</summary>
public interface IAllocatorStatistics {
    /// <summary>Reads the allocator's committed and reserved address-space bytes.</summary>
    /// <returns>The committed and reserved byte readings, including allocator metadata and slack.</returns>
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
/// <param name="allocator">The native allocator, or null for a standalone managed owner.</param>
/// <param name="observer">The owning lifecycle's byte observer, or null for unmetered storage.</param>
public sealed unsafe class OwnedBuffer<T>(IAllocator? allocator, IAllocationObserver? observer = null) : MemoryManager<T> where T : unmanaged {
    private T* m_pointer;
    private T[]? m_managed;
    private int m_capacity;
    private int m_length;
    private bool m_disposed;

    // The row this buffer's charges name: an allocator's storage is native, a standalone owner's is managed.
    private MemoryBacking Backing => ((allocator is null) ? MemoryBacking.Managed : MemoryBacking.NativeAllocator);

    /// <summary>Gets the logical element count.</summary>
    public int Length => m_length;
    /// <summary>Gets the storage capacity.</summary>
    public int Capacity => m_capacity;

    /// <summary>Gets a borrowed reference to an element.</summary>
    /// <param name="index">The element's index, below <see cref="Length"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not below
    /// <see cref="Length"/>, which is zero once the buffer is disposed.</exception>
    public ref T this[int index] {
        get {
            if (((uint)index) >= ((uint)m_length)) { throw new ArgumentOutOfRangeException(paramName: nameof(index)); }
            if (m_pointer is not null) { return ref m_pointer[index]; }
            return ref m_managed![index];
        }
    }

    /// <summary>Enumerates a borrow of the logical contents.</summary>
    public Span<T>.Enumerator GetEnumerator() => GetSpan().GetEnumerator();
    /// <inheritdoc/>
    public override Span<T> GetSpan() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        return ((allocator is null) ? m_managed.AsSpan(length: m_length, start: 0) : new Span<T>(length: m_length, pointer: m_pointer));
    }
    /// <summary>Changes the logical length, preserving existing entries and optionally clearing new entries.</summary>
    /// <param name="length">The nonnegative logical element count.</param>
    /// <param name="clear">Whether new logical elements are cleared before publication.</param>
    /// <param name="preserve">Whether existing entries survive growth; a caller that overwrites every entry passes
    /// <see langword="false"/>, so growth copies nothing and leaves every entry unspecified.</param>
    public void Resize(int length, bool clear = true, bool preserve = true) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > m_capacity) {
            var bytes = checked((((long)length) * sizeof(T)));
            var oldBytes = ((long)m_capacity) * sizeof(T);
            observer?.Acquire(bytes, Backing);
            try {
                if (allocator is null) {
                    var replacement = new T[length];
                    if (preserve) { GetSpan().CopyTo(replacement); }
                    m_managed = replacement;
                } else {
                    var candidate = (T*)allocator.Allocate(checked((nuint)bytes));
                    if (candidate is null) { throw new OutOfMemoryException(message: $"Cannot allocate {bytes} bytes of {typeof(T).Name} storage."); }
                    var kept = preserve ? m_length : 0;
                    new ReadOnlySpan<T>(m_pointer, kept).CopyTo(new Span<T>(candidate, length));
                    if (clear) { new Span<T>(candidate + kept, length - kept).Clear(); }
                    allocator.Free(ptr: m_pointer);
                    m_pointer = candidate;
                }
            } catch {
                observer?.Change(backing: Backing, bytes: -bytes);
                throw;
            }
            m_capacity = m_length = length;
            observer?.Change(backing: Backing, bytes: -oldBytes);
        } else {
            if (!preserve) { m_length = 0; }
            if (clear && (length > m_length)) {
                if (allocator is null) { m_managed.AsSpan(length: (length - m_length), start: m_length).Clear(); } else { new Span<T>(length: (length - m_length), pointer: (m_pointer + m_length)).Clear(); }
            }
            m_length = length;
        }
    }
    /// <summary>Appends an element after the logical contents, doubling the capacity (to at least sixteen elements) when it
    /// is full, so a run of appends allocates and copies a geometric series rather than once each. The growth preserves the
    /// contents; every earlier borrow ends at the call.</summary>
    /// <param name="item">The element.</param>
    /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
    public void Append(T item) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (m_length == m_capacity) {
            var length = m_length;

            Resize(length: ((int)Math.Min(val1: Math.Max(val1: 16L, val2: (2L * m_capacity)), val2: Array.MaxLength)), clear: false);
            m_length = length;
            if (m_length == m_capacity) { throw new OutOfMemoryException(message: $"Cannot grow {typeof(T).Name} storage past {m_capacity} elements."); }
        }
        if (m_pointer is not null) { m_pointer[m_length] = item; } else { m_managed![m_length] = item; }
        m_length++;
    }
    /// <summary>Frees the storage after every borrow has ended, keeping the owner usable: its length and capacity become
    /// zero, a span of a memory view taken earlier throws <see cref="ArgumentOutOfRangeException"/> as one past the
    /// owner's length does, and a later <see cref="Resize"/> allocates again, after which those views read the new
    /// storage.</summary>
    /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
    public void Free() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        FreeStorage();
    }
    private void FreeStorage() {
        var bytes = checked((((long)m_capacity) * sizeof(T)));

        if (allocator is not null) { allocator.Free(ptr: m_pointer); }
        m_managed = null;
        m_pointer = null;
        m_capacity = m_length = 0;
        observer?.Change(backing: Backing, bytes: -bytes);
    }
    /// <summary>Replaces the logical contents after earlier borrows have ended.</summary>
    /// <param name="source">The source elements, borrowed through the copy.</param>
    public void CopyFrom(ReadOnlySpan<T> source) {
        Resize(source.Length, clear: false, preserve: false);
        source.CopyTo(destination: GetSpan());
    }
    /// <inheritdoc/>
    public override MemoryHandle Pin(int elementIndex = 0) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(((uint)elementIndex), ((uint)m_length));
        if (allocator is null) { return m_managed.AsMemory(length: (m_length - elementIndex), start: elementIndex).Pin(); }
        return new MemoryHandle((m_pointer + elementIndex));
    }
    /// <inheritdoc/>
    public override void Unpin() { }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing) {
        if (m_disposed) { return; }
        m_disposed = true;
        FreeStorage();
    }

    /// <summary>Releases the owned storage after its last borrow ends.</summary>
    public void Dispose() => Dispose(disposing: true);
}

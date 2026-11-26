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
    /// <summary>Reads the allocator's committed and reserved address-space bytes as its own statistics count them.</summary>
    /// <returns>The committed and reserved byte readings, including allocator metadata and slack. An allocator that keeps
    /// statistics per thread (mimalloc) can trail what its arenas hold; a walk of the address space reads them by their
    /// pages through <see cref="Owns"/>.</returns>
    (long Committed, long Reserved) ReadStatistics();
    /// <summary>Returns whether an address lies in memory the allocator manages (its arenas), so a walk of the process's
    /// address space can name the allocator's regions by their own pages rather than by its statistics.</summary>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> when the allocator manages the address.</returns>
    bool Owns(nint address);
    /// <summary>Asks the allocator to release idle storage. Live allocations are unaffected.</summary>
    void Collect();
}
/// <summary>
/// One owner's growable run of reference-free elements: a build's workspace list or table, or a product's storage. The
/// storage is native, which the collector never sees, so work that keeps its scratch here cannot start a collection that
/// pauses every managed thread, and a list that grows leaves no dead array on the large object heap: growth reallocates
/// the block, which the allocator may extend in place. An owner that names no allocator takes the process's
/// (<see cref="ProcessAllocator.Current"/>). Spans, references and memory slices are borrows: all end at the next growth
/// or at disposal. There is no finalizer recovery.
/// </summary>
/// <typeparam name="T">The unmanaged element.</typeparam>
public sealed unsafe class OwnedBuffer<T> : MemoryManager<T> where T : unmanaged {
    private readonly IAllocator m_allocator;
    private readonly IAllocationObserver? m_observer;
    private T* m_pointer;
    private int m_capacity;
    private int m_length;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="OwnedBuffer{T}"/> class, empty unless a capacity is
    /// reserved.</summary>
    /// <param name="allocator">The native allocator, or null for the process's (<see cref="ProcessAllocator.Current"/>,
    /// read here once: the buffer frees through the allocator it was made with).</param>
    /// <param name="observer">The owning lifecycle's byte observer, or null for unmetered storage.</param>
    /// <param name="capacity">The elements to reserve room for at once, so a list that knows its size does not grow from
    /// empty.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public OwnedBuffer(IAllocator? allocator, IAllocationObserver? observer = null, int capacity = 0) {
        m_allocator = (allocator ?? ProcessAllocator.Current);
        m_observer = observer;
        Reserve(capacity: capacity);
    }

    /// <summary>Creates a buffer of exactly <paramref name="length"/> elements, allocated once at that size.</summary>
    /// <param name="allocator">The native allocator, or null for the process's.</param>
    /// <param name="length">The buffer's length.</param>
    /// <param name="clear">Whether the elements are zeroed; otherwise they are unspecified.</param>
    /// <param name="observer">The owning lifecycle's byte observer, or null for unmetered storage.</param>
    /// <returns>The buffer, which the caller disposes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    /// <exception cref="HostMemoryRefusedException">The observer refused the storage; nothing is allocated.</exception>
    public static OwnedBuffer<T> OfLength(IAllocator? allocator, int length, bool clear, IAllocationObserver? observer = null) {
        var buffer = new OwnedBuffer<T>(allocator: allocator, capacity: length, observer: observer);

        buffer.Resize(clear: clear, length: length);
        return buffer;
    }

    // The row this buffer's charges name.
    private const MemoryBacking NativeBacking = MemoryBacking.NativeAllocator;

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
            return ref m_pointer[index];
        }
    }

    /// <summary>Enumerates a borrow of the logical contents.</summary>
    public Span<T>.Enumerator GetEnumerator() => GetSpan().GetEnumerator();
    /// <summary>Returns a borrow of the logical contents from an element on, as an array's span is taken.</summary>
    /// <param name="start">The first element, at most <see cref="Length"/>.</param>
    /// <returns>The borrow, which ends at the next growth or at disposal.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> lies outside the logical contents.</exception>
    public Span<T> AsSpan(int start) => GetSpan()[start..];
    /// <summary>Returns a borrow of a run of the logical contents, as an array's span is taken.</summary>
    /// <param name="start">The run's first element.</param>
    /// <param name="length">The run's element count.</param>
    /// <returns>The borrow, which ends at the next growth or at disposal.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The run lies outside the logical contents.</exception>
    public Span<T> AsSpan(int start, int length) => GetSpan().Slice(start: start, length: length);
    /// <summary>Borrows the logical contents as a span, so a buffer stands where a method takes an array's span.</summary>
    /// <param name="buffer">The buffer, not disposed.</param>
    public static implicit operator Span<T>(OwnedBuffer<T> buffer) => buffer.GetSpan();
    /// <summary>Borrows the logical contents as a read-only span, so a buffer stands where a method reads an array's
    /// span.</summary>
    /// <param name="buffer">The buffer, not disposed.</param>
    public static implicit operator ReadOnlySpan<T>(OwnedBuffer<T> buffer) => buffer.GetSpan();
    /// <inheritdoc/>
    public override Span<T> GetSpan() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        return new Span<T>(length: m_length, pointer: m_pointer);
    }
    /// <summary>Makes room for at least <paramref name="capacity"/> elements, preserving the contents and the length.</summary>
    /// <param name="capacity">The fewest elements the storage holds afterward.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
    public void Reserve(int capacity) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        if (capacity > m_capacity) { Grow(capacity: capacity, preserve: true); }
    }
    /// <summary>Changes the logical length, preserving existing entries and optionally clearing new entries.</summary>
    /// <param name="length">The nonnegative logical element count.</param>
    /// <param name="clear">Whether new logical elements are cleared before publication.</param>
    /// <param name="preserve">Whether existing entries survive growth; a caller that overwrites every entry passes
    /// <see langword="false"/>, so growth copies nothing and leaves every entry unspecified.</param>
    public void Resize(int length, bool clear = true, bool preserve = true) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (!preserve) { m_length = 0; }
        if (length > m_capacity) { Grow(capacity: length, preserve: preserve); }
        if (clear && (length > m_length)) { Storage(start: m_length, length: (length - m_length)).Clear(); }
        m_length = length;
    }
    /// <summary>Appends an element after the logical contents, doubling the capacity (to at least sixteen elements) when it
    /// is full, so a run of appends allocates and copies a geometric series rather than once each. The growth preserves the
    /// contents; every earlier borrow ends at the call.</summary>
    /// <param name="item">The element.</param>
    /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
    public void Add(T item) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        if (m_length == m_capacity) { Grow(capacity: Doubled(required: (m_length + 1)), preserve: true); }
        m_pointer[m_length] = item;
        m_length++;
    }
    /// <summary>Appends elements after the logical contents, growing geometrically when they do not fit.</summary>
    /// <param name="items">The elements, borrowed through the copy; never a borrow of this buffer.</param>
    /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
    public void AddRange(ReadOnlySpan<T> items) => items.CopyTo(destination: Extend(count: items.Length));
    /// <summary>Appends elements of unspecified value and returns them, growing geometrically when they do not fit.</summary>
    /// <param name="count">The number of elements to append.</param>
    /// <returns>The appended elements, a borrow that ends at the next growth or at disposal.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
    public Span<T> Extend(int count) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);
        var required = checked((m_length + count));

        if (required > m_capacity) { Grow(capacity: Doubled(required: required), preserve: true); }

        var appended = Storage(start: m_length, length: count);

        m_length = required;
        return appended;
    }
    /// <summary>Empties the logical contents, keeping the storage.</summary>
    public void Clear() => m_length = 0;
    /// <summary>Copies the logical contents into a new managed array of exactly their count.</summary>
    /// <returns>The copy; the shared empty array when the buffer is empty.</returns>
    public T[] ToArray() => GetSpan().ToArray();
    /// <summary>Frees the storage after every borrow has ended, keeping the owner usable: its length and capacity become
    /// zero, a span of a memory view taken earlier throws <see cref="ArgumentOutOfRangeException"/> as one past the
    /// owner's length does, and a later <see cref="Resize"/> allocates again, after which those views read the new
    /// storage.</summary>
    /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
    public void Free() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);
        FreeStorage();
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

    // The capacity a growth to `required` elements takes: double the current, at least sixteen, within the array limit.
    private int Doubled(int required) {
        var capacity = ArrayCapacity.Grown(current: m_capacity, required: required);

        if (capacity < required) { throw new OutOfMemoryException(message: $"Cannot grow {typeof(T).Name} storage past {m_capacity} elements."); }
        return capacity;
    }
    // Elements of the storage past the logical length are part of it; only the length bounds what callers borrow.
    private Span<T> Storage(int start, int length) => new(length: length, pointer: (m_pointer + start));
    // Replaces the storage with room for at least `capacity` elements, carrying the logical contents when `preserve`.
    private void Grow(int capacity, bool preserve) {
        var kept = (preserve ? m_length : 0);
        var bytes = checked((((long)capacity) * sizeof(T)));
        var oldBytes = (((long)m_capacity) * sizeof(T));

        m_observer?.Acquire(bytes, NativeBacking);
        // A reallocation may extend the block in place; one that carries nothing frees first, so the peak is one block.
        T* candidate;

        if (kept > 0) {
            candidate = (T*)m_allocator.Reallocate(ptr: m_pointer, newSize: checked((nuint)bytes));
        } else {
            m_allocator.Free(ptr: m_pointer);
            m_pointer = null;
            m_capacity = 0;
            m_observer?.Change(backing: NativeBacking, bytes: -oldBytes);
            oldBytes = 0;
            candidate = (T*)m_allocator.Allocate(checked((nuint)bytes));
        }
        if (candidate is null) {
            m_observer?.Change(backing: NativeBacking, bytes: -bytes);
            throw new OutOfMemoryException(message: $"Cannot allocate {bytes} bytes of {typeof(T).Name} storage.");
        }
        m_pointer = candidate;
        m_capacity = capacity;
        m_observer?.Change(backing: NativeBacking, bytes: -oldBytes);
    }
    private void FreeStorage() {
        m_allocator.Free(ptr: m_pointer);
        m_observer?.Change(backing: NativeBacking, bytes: -checked((((long)m_capacity) * sizeof(T))));
        m_pointer = null;
        m_capacity = m_length = 0;
    }
}

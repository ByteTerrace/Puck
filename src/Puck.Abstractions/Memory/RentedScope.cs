namespace Puck.Abstractions.Memory;

/// <summary>
/// The workspace of one build, charged to one owner and released together when the scope is disposed: a builder that
/// holds many same-lifetime tables (a mesh simplifier's per-place and per-triangle arrays) takes each through the scope
/// instead of allocating it, so the next build reuses the storage and the owner is charged for exactly what the build
/// holds. Arrays come from <see cref="RentedArrays"/>; buffers and lists (<see cref="Buffer{T}"/>, <see cref="RentList{T}"/>)
/// are native when the scope has an allocator, so a background build's largest scratch never reaches the collector.
/// </summary>
/// <param name="owner">The workspace owner charged for every array and buffer, or null for an unmetered scope.</param>
/// <param name="allocator">The native allocator the scope's buffers and lists take their storage from, or null to rent it
/// from <see cref="RentedArrays"/>.</param>
public sealed class RentedScope(IAllocationObserver? owner = null, IAllocator? allocator = null) : IDisposable {
    private readonly List<IDisposable> m_leases = [];

    /// <summary>Gets the owner the scope charges, or null for an unmetered scope.</summary>
    public IAllocationObserver? Owner => owner;
    /// <summary>Gets the native allocator the scope's buffers and lists use, or null when they are rented.</summary>
    public IAllocator? Allocator => allocator;

    /// <summary>Rents an array of at least <paramref name="minimumLength"/> elements that the scope returns when disposed.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="minimumLength">The fewest elements the array holds; it may hold more, and the caller reads only the
    /// length it asked for.</param>
    /// <param name="clear">Whether the first <paramref name="minimumLength"/> elements are zeroed; otherwise they are
    /// unspecified.</param>
    /// <returns>The array, valid until the scope is disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative.</exception>
    public T[] Rent<T>(int minimumLength, bool clear = false) {
        m_leases.Add(item: RentedArrays.Rent<T>(minimumLength: minimumLength, array: out var array, owner: owner));
        if (clear) { array.AsSpan(start: 0, length: minimumLength).Clear(); }

        return array;
    }
    /// <summary>Creates a buffer of exactly <paramref name="length"/> elements that the scope releases when disposed,
    /// native when the scope has an allocator.</summary>
    /// <typeparam name="T">The unmanaged element.</typeparam>
    /// <param name="length">The buffer's length.</param>
    /// <param name="clear">Whether the elements are zeroed; otherwise they are unspecified.</param>
    /// <returns>The buffer, valid until the scope is disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.</exception>
    public OwnedBuffer<T> Buffer<T>(int length, bool clear = false) where T : unmanaged {
        var buffer = OwnedBuffer<T>.OfLength(allocator: allocator, clear: clear, length: length, observer: owner);

        m_leases.Add(item: buffer);
        return buffer;
    }
    /// <summary>Creates an empty list whose storage the scope releases when disposed, charged to the scope's owner and
    /// native when the scope has an allocator.</summary>
    /// <typeparam name="T">The unmanaged element.</typeparam>
    /// <param name="capacity">The elements the first storage holds at least.</param>
    /// <returns>The list, valid until the scope is disposed.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public OwnedBuffer<T> RentList<T>(int capacity = 0) where T : unmanaged {
        var list = new OwnedBuffer<T>(allocator: allocator, capacity: capacity, observer: owner);

        m_leases.Add(item: list);

        return list;
    }
    /// <summary>Returns every array the scope rented and releases every buffer and its charges.</summary>
    public void Dispose() {
        foreach (var lease in m_leases) { lease.Dispose(); }
        m_leases.Clear();
    }
}

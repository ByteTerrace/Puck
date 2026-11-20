namespace Puck.Abstractions.Memory;

/// <summary>
/// The workspace arrays of one build, rented from <see cref="RentedArrays"/> under one owner and returned together when the
/// scope is disposed: a builder that holds many same-lifetime tables (a mesh simplifier's per-place and per-triangle
/// arrays) rents each through the scope instead of allocating it, so the next build reuses them and the owner is charged
/// for exactly what the build holds.
/// </summary>
/// <param name="owner">The workspace owner charged for every array, or null for an unmetered scope.</param>
public sealed class RentedScope(IAllocationObserver? owner = null) : IDisposable {
    private readonly List<IDisposable> m_leases = [];

    /// <summary>Gets the owner the scope charges, or null for an unmetered scope.</summary>
    public IAllocationObserver? Owner => owner;

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
    /// <summary>Creates a list whose storage the scope releases when disposed, charged to the scope's owner.</summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="capacity">The elements the first rented array holds at least.</param>
    /// <returns>The list, valid until the scope is disposed.</returns>
    public RentedList<T> RentList<T>(int capacity = 0) {
        var list = new RentedList<T>(capacity: capacity, owner: owner);

        m_leases.Add(item: list);

        return list;
    }
    /// <summary>Returns every array the scope rented and releases their charges.</summary>
    public void Dispose() {
        foreach (var lease in m_leases) { lease.Dispose(); }
        m_leases.Clear();
    }
}

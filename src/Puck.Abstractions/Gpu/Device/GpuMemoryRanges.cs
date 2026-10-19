namespace Puck.Abstractions.Gpu;

/// <summary>
/// The free ranges of one block of device memory a backend sub-allocates resources from: first fit at the alignment each
/// resource asks for, the gap an alignment leaves kept free, and a freed range merged with its free neighbours, so a
/// block that every resource left is one free range again (<see cref="IsEmpty"/>). It only does arithmetic; the backend
/// owns the native block and binds each resource at the offset it hands out. Not thread-safe: its owner locks.
/// </summary>
public sealed class GpuMemoryRanges {
    // The free ranges, ordered by offset, never touching one another.
    private readonly List<(ulong Offset, ulong Size)> m_free = [];

    /// <summary>Initializes a new instance of the <see cref="GpuMemoryRanges"/> class over a block, all of it free.</summary>
    /// <param name="capacity">The block's bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is zero.</exception>
    public GpuMemoryRanges(ulong capacity) {
        ArgumentOutOfRangeException.ThrowIfZero(value: capacity);

        Capacity = capacity;
        m_free.Add(item: (0UL, capacity));
    }

    /// <summary>Gets the block's bytes.</summary>
    public ulong Capacity { get; }
    /// <summary>Gets whether every range of the block is free.</summary>
    public bool IsEmpty => ((m_free.Count == 1) && (m_free[0].Size == Capacity));

    /// <summary>Takes the first free range that holds <paramref name="size"/> bytes at an offset aligned to
    /// <paramref name="alignment"/>.</summary>
    /// <param name="size">The bytes the resource needs.</param>
    /// <param name="alignment">The alignment its offset needs: a power of two, or zero or one for none.</param>
    /// <param name="offset">The offset taken.</param>
    /// <returns>Whether a free range held it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is zero, or <paramref name="alignment"/> is
    /// not a power of two.</exception>
    public bool TryAllocate(ulong size, ulong alignment, out ulong offset) {
        ArgumentOutOfRangeException.ThrowIfZero(value: size);
        if ((alignment > 1UL) && ((alignment & (alignment - 1UL)) != 0UL)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(alignment), actualValue: alignment, message: "An alignment is a power of two.");
        }

        var mask = ((alignment > 1UL) ? (alignment - 1UL) : 0UL);

        for (var index = 0; (index < m_free.Count); index++) {
            var (start, length) = m_free[index];
            var aligned = ((start + mask) & ~mask);
            var end = (start + length);

            if ((aligned < start) || (aligned > end) || ((end - aligned) < size)) {
                continue;
            }

            m_free.RemoveAt(index: index);
            // The tail past the resource, then the gap before it, both still free and still in offset order.
            if ((aligned + size) < end) {
                m_free.Insert(index: index, item: ((aligned + size), (end - (aligned + size))));
            }
            if (aligned > start) {
                m_free.Insert(index: index, item: (start, (aligned - start)));
            }
            offset = aligned;

            return true;
        }

        offset = 0UL;

        return false;
    }
    /// <summary>Returns a range <see cref="TryAllocate"/> took, merging it with the free ranges beside it.</summary>
    /// <param name="offset">The offset it took.</param>
    /// <param name="size">The bytes it took.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range is empty, passes the block's end, or overlaps a free
    /// range.</exception>
    public void Free(ulong offset, ulong size) {
        ArgumentOutOfRangeException.ThrowIfZero(value: size);
        if ((offset > Capacity) || ((Capacity - offset) < size)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(size), actualValue: size, message: $"The range at {offset} of {size} bytes passes the block's {Capacity}.");
        }

        var index = 0;

        while ((index < m_free.Count) && (m_free[index].Offset < offset)) {
            index++;
        }

        var start = offset;
        var end = (offset + size);

        if (((index > 0) && ((m_free[index - 1].Offset + m_free[index - 1].Size) > start)) || ((index < m_free.Count) && (m_free[index].Offset < end))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(offset), actualValue: offset, message: $"The range at {offset} of {size} bytes is already free.");
        }
        // Merge with the free range after it, then the one before it.
        if ((index < m_free.Count) && (m_free[index].Offset == end)) {
            end += m_free[index].Size;
            m_free.RemoveAt(index: index);
        }
        if ((index > 0) && ((m_free[index - 1].Offset + m_free[index - 1].Size) == start)) {
            start = m_free[index - 1].Offset;
            m_free.RemoveAt(index: (index - 1));
            index--;
        }
        m_free.Insert(index: index, item: (start, (end - start)));
    }
}

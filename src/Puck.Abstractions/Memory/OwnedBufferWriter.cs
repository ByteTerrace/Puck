using System.Buffers;

namespace Puck.Abstractions.Memory;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over one owner's native storage (<see cref="OwnedBuffer{T}"/>): a serializer that
/// writes a large document (a <see cref="System.Text.Json.Utf8JsonWriter"/> over it) grows native storage by
/// reallocation instead of a chain of managed arrays, so no step of the write reaches the large object heap. Spans and
/// memory it hands out are borrows that end at the next request or at disposal.
/// </summary>
/// <typeparam name="T">The unmanaged element.</typeparam>
/// <param name="allocator">The native allocator, or null for the process's (<see cref="ProcessAllocator.Current"/>).</param>
/// <param name="observer">The owning lifecycle's byte observer, or null for unmetered storage.</param>
public sealed class OwnedBufferWriter<T>(IAllocator? allocator = null, IAllocationObserver? observer = null) : IBufferWriter<T>, IDisposable where T : unmanaged {
    // The least room a request with no size hint is given.
    private const int MinimumRoom = 256;

    // The storage: its logical length is the room reserved, of which the first m_written elements are written.
    private readonly OwnedBuffer<T> m_storage = new(allocator: allocator, observer: observer);
    private int m_written;

    /// <summary>Gets the elements written so far.</summary>
    public int WrittenCount => m_written;
    /// <summary>Gets a borrow of the elements written so far, which ends at the next request or at disposal.</summary>
    public ReadOnlySpan<T> WrittenSpan => m_storage.GetSpan()[..m_written];

    /// <inheritdoc/>
    public void Advance(int count) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value: count, other: (m_storage.Length - m_written));
        m_written += count;
    }
    /// <inheritdoc/>
    public Memory<T> GetMemory(int sizeHint = 0) {
        Ensure(sizeHint: sizeHint);
        return m_storage.Memory[m_written..];
    }
    /// <inheritdoc/>
    public Span<T> GetSpan(int sizeHint = 0) {
        Ensure(sizeHint: sizeHint);
        return m_storage.GetSpan()[m_written..];
    }
    /// <summary>Copies the elements written into a new managed array of exactly their count.</summary>
    /// <returns>The copy; the shared empty array when nothing was written.</returns>
    public T[] ToArray() => WrittenSpan.ToArray();
    /// <summary>Empties the writer, keeping its storage for the next write.</summary>
    public void Clear() => m_written = 0;
    /// <summary>Frees the storage; the writer accepts nothing afterward.</summary>
    public void Dispose() => m_storage.Dispose();

    // Makes room for at least the hint (MinimumRoom when none is given) past the written elements, growing the room
    // geometrically and carrying what was written.
    private void Ensure(int sizeHint) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: sizeHint);

        var required = checked((m_written + Math.Max(val1: sizeHint, val2: 1)));

        if (required <= m_storage.Length) { return; }

        var room = ArrayCapacity.Grown(current: m_storage.Length, required: checked((m_written + Math.Max(val1: sizeHint, val2: MinimumRoom))));

        m_storage.Resize(clear: false, length: room);
    }
}

namespace Puck.Abstractions.Memory;

/// <summary>
/// A write-only stream that collects bytes in owned storage (<see cref="OwnedBuffer{T}"/>), for an encoder whose output
/// must be complete before its length is known: a <see cref="MemoryStream"/> grows by doubling and leaves every outgrown
/// buffer for the collector, where this stream keeps its storage native or hands it back to <see cref="RentedArrays"/>
/// for the next entry's buffer. Disposing it releases the storage, so <see cref="Written"/> is a borrow that ends there.
/// </summary>
/// <param name="capacity">The bytes the first storage holds at least.</param>
/// <param name="owner">The workspace owner charged for the storage, or null for an unmetered stream.</param>
/// <param name="allocator">The native allocator the storage comes from, or null to rent it.</param>
public sealed class RentedMemoryStream(int capacity = 0, IAllocationObserver? owner = null, IAllocator? allocator = null) : Stream {
    private readonly OwnedBuffer<byte> m_bytes = new(allocator: allocator, capacity: capacity, observer: owner);

    /// <inheritdoc/>
    public override bool CanRead => false;
    /// <inheritdoc/>
    public override bool CanSeek => false;
    /// <inheritdoc/>
    public override bool CanWrite => true;
    /// <inheritdoc/>
    public override long Length => m_bytes.Length;
    /// <inheritdoc/>
    public override long Position {
        get => m_bytes.Length;
        set => throw new NotSupportedException();
    }
    /// <summary>Gets the bytes written so far, valid until the next write or disposal.</summary>
    public ReadOnlySpan<byte> Written => m_bytes.GetSpan();

    /// <inheritdoc/>
    public override void Flush() { }
    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException();
    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => m_bytes.AddRange(items: buffer.AsSpan(start: offset, length: count));
    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer) => m_bytes.AddRange(items: buffer);
    /// <inheritdoc/>
    public override void WriteByte(byte value) => m_bytes.Add(item: value);
    /// <inheritdoc/>
    protected override void Dispose(bool disposing) {
        if (disposing) { m_bytes.Dispose(); }
        base.Dispose(disposing: disposing);
    }
}

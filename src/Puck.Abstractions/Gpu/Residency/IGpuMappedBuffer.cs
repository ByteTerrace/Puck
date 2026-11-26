namespace Puck.Abstractions.Gpu;

/// <summary>
/// A device-local buffer whose whole address range is reserved at creation and backed with device memory one chunk at a
/// time (<see cref="ChunkBytes"/>): a Direct3D 12 reserved resource whose tiles are mapped to ranges of the device's heaps.
/// A buffer that grows maps more chunks and keeps its handle, its descriptors and every word it holds, so growing never
/// holds an old and a new buffer at once; one that shrinks gives its chunks back without allocating a replacement.
/// <para>Its page authority (<see cref="IGpuPagedBuffer.Pages"/>) has one page a chunk. A chunk is writable only once its
/// mapping is queued ahead of every later submission (<see cref="Map"/>), and stops being writable the moment
/// <see cref="Unmap"/> revokes it, before its backing is released, which waits for the readers the caller retires it
/// behind. Every byte of backing passes the device's memory admission and is counted in its ledger, named to the
/// buffer's family.</para>
/// <para>Called on the thread that records and submits the device's frames, as the region that owns it is.</para>
/// </summary>
public interface IGpuMappedBuffer : IGpuPagedBuffer {
    /// <summary>Gets the bytes one mapping covers: a page of <see cref="IGpuPagedBuffer.Pages"/>.</summary>
    ulong ChunkBytes { get; }
    /// <summary>Gets the device bytes the mapped chunks hold.</summary>
    ulong MappedBytes { get; }
    /// <summary>Gets the device bytes the device holds for the chunks of its mapped buffers that no chunk is mapped to:
    /// memory already allocated, which only a <see cref="Map"/> of some mapped buffer on the device takes. Chunks given
    /// back from many of the device's blocks leave it behind them, since a block is freed only once it is empty. The
    /// same figure for every mapped buffer of one device.</summary>
    ulong SpareBytes { get; }

    /// <summary>Backs every chunk the range touches that holds no backing yet, all or none: the backing is admitted and
    /// placed for every such chunk before any mapping is queued, so a refusal leaves the buffer as it was.</summary>
    /// <param name="offset">The range's first byte.</param>
    /// <param name="bytes">The range's bytes; zero maps nothing.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range passes <see cref="IGpuBuffer.SizeBytes"/>.</exception>
    /// <exception cref="GpuMemoryRefusedException">The account or the device refused the backing.</exception>
    void Map(ulong offset, ulong bytes);
    /// <summary>Revokes every mapped chunk that lies wholly inside the range at once, and returns their release: disposing
    /// it, once every submission that may read them has completed, queues their unmapping and returns their backing to the
    /// device's heaps. A chunk the range only partly covers stays mapped.</summary>
    /// <param name="offset">The range's first byte.</param>
    /// <param name="bytes">The range's bytes.</param>
    /// <returns>The release, which the caller retires behind the chunks' readers; one that releases nothing when no chunk
    /// lies wholly inside the range.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range passes <see cref="IGpuBuffer.SizeBytes"/>.</exception>
    GpuMappedRelease Unmap(ulong offset, ulong bytes);
}
/// <summary>The chunks an <see cref="IGpuMappedBuffer.Unmap"/> revoked, released by <see cref="Dispose"/> once their
/// readers have finished.</summary>
/// <param name="bytes">The device bytes the chunks hold.</param>
/// <param name="release">What releases them: queues their unmapping and returns their backing; <see langword="null"/> for
/// none.</param>
public sealed class GpuMappedRelease(ulong bytes, Action? release) : IDisposable {
    private readonly DisposeOnce m_release = new(action: release);

    /// <summary>Gets a release of nothing.</summary>
    public static GpuMappedRelease None { get; } = new(bytes: 0UL, release: null);

    /// <summary>Gets the device bytes the release gives back.</summary>
    public ulong Bytes { get; } = bytes;

    /// <inheritdoc/>
    public void Dispose() => m_release.Dispose();
}
/// <summary>The chunk arithmetic every <see cref="IGpuMappedBuffer"/> shares: which chunks a byte range touches
/// (<see cref="IGpuMappedBuffer.Map"/> backs each of them) and which lie wholly inside it
/// (<see cref="IGpuMappedBuffer.Unmap"/> revokes only those), so the backends agree on a range's chunks to the
/// byte.</summary>
public static class GpuMappedChunks {
    /// <summary>Refuses a range that passes a buffer's size.</summary>
    /// <param name="sizeBytes">The buffer's size (<see cref="IGpuBuffer.SizeBytes"/>).</param>
    /// <param name="offset">The range's first byte.</param>
    /// <param name="bytes">The range's bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The range passes <paramref name="sizeBytes"/>.</exception>
    public static void RequireRange(ulong sizeBytes, ulong offset, ulong bytes) {
        if ((offset > sizeBytes) || (bytes > (sizeBytes - offset))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(bytes), message: $"The range of {bytes} bytes at {offset} passes the buffer's {sizeBytes}.");
        }
    }
    /// <summary>Returns the chunks a range of at least one byte touches.</summary>
    /// <param name="offset">The range's first byte.</param>
    /// <param name="bytes">The range's bytes; not zero.</param>
    /// <param name="chunkBytes">The bytes of one chunk.</param>
    /// <returns>The first and the last chunk the range touches.</returns>
    public static (int First, int Last) Touched(ulong offset, ulong bytes, ulong chunkBytes) =>
        (((int)(offset / chunkBytes)), ((int)((offset + (bytes - 1UL)) / chunkBytes)));
    /// <summary>Returns the chunks that lie wholly inside a range: those it covers to their end, or to the buffer's end
    /// for the last, which may be partial.</summary>
    /// <param name="sizeBytes">The buffer's size.</param>
    /// <param name="chunkCount">The buffer's chunks.</param>
    /// <param name="offset">The range's first byte.</param>
    /// <param name="bytes">The range's bytes.</param>
    /// <param name="chunkBytes">The bytes of one chunk.</param>
    /// <returns>The first such chunk and the chunk after the last; none when they are equal or cross.</returns>
    public static (int First, int Stop) Inside(ulong sizeBytes, int chunkCount, ulong offset, ulong bytes, ulong chunkBytes) {
        var end = (offset + bytes);

        return (((int)((offset + (chunkBytes - 1UL)) / chunkBytes)), ((end == sizeBytes) ? chunkCount : ((int)(end / chunkBytes))));
    }
}

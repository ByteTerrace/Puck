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

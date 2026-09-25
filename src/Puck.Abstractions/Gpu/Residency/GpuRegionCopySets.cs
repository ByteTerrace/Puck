namespace Puck.Abstractions.Gpu;

/// <summary>
/// One region's share of a <see cref="GpuRegionCopyPool"/>: its copy descriptor set per frame slot, which a staged
/// <see cref="GpuRegion"/> records its copy with. The pool owns the sets and releases them when it is disposed; a share
/// is valid until then, and the rewrite rule is the share's own: a region rewrites a slot's set before it records that
/// slot's copy whenever another region wrote the same set since.
/// </summary>
public sealed class GpuRegionCopySets {
    private readonly GpuRegionCopyPool m_pool;
    private readonly int m_region;

    internal GpuRegionCopySets(GpuRegionCopyPool pool, int region) {
        m_pool = pool;
        m_region = region;
    }

    /// <summary>Gets the number of frame slots, one set each.</summary>
    public int SlotCount => m_pool.SlotCount;

    /// <summary>Returns the copy set of a slot.</summary>
    /// <param name="slot">The frame slot; below <see cref="SlotCount"/>.</param>
    /// <returns>The set's handle.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is negative or not below
    /// <see cref="SlotCount"/>.</exception>
    /// <exception cref="ObjectDisposedException">The pool has been disposed.</exception>
    public nint SetOf(int slot) =>
        m_pool.SetOf(
            region: m_region,
            slot: slot
        );

    // Whether the slot's set holds the region's buffers: no other region wrote it after the region last did.
    internal bool IsWrittenBy(int slot, GpuRegion region) =>
        m_pool.IsWrittenBy(
            region: m_region,
            slot: slot,
            writer: region
        );
    // Records that the region's buffers are now what the slot's set holds.
    internal void WrittenBy(int slot, GpuRegion region) =>
        m_pool.WrittenBy(
            region: m_region,
            slot: slot,
            writer: region
        );
}

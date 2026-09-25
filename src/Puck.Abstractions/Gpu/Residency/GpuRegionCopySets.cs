namespace Puck.Abstractions.Gpu;

/// <summary>
/// One copy descriptor set per frame slot, from one pool sized by <see cref="GpuRegion.CopyPoolSizes"/>: the sets a
/// staged <see cref="GpuRegion"/> records its copy with. A region creates its own unless its owner hands it sets it
/// reserved, which an owner does when it creates its regions' pools while it is admitted, so a region it creates or
/// replaces at a later frame takes no descriptor range then and cannot be refused by a heap another owner filled in
/// between. Reserved sets outlive the regions that write them, one region at a time: a region writes its buffers into a
/// slot's set when it is created, and again before it records that slot's copy if another region wrote the set since,
/// which happens only with the slot's last submission retired. The owner disposes them after its last region.
/// </summary>
public sealed class GpuRegionCopySets : IDisposable {
    private readonly IGpuBindings m_bindings;
    private readonly nint[] m_sets;
    // The region whose buffers each slot's set holds, or null before any region wrote it.
    private readonly GpuRegion?[] m_writers;

    private nint m_pool;

    /// <summary>Initializes a new instance of the <see cref="GpuRegionCopySets"/> class, creating the pool and
    /// allocating one set per slot against the copy kernel's layout.</summary>
    /// <param name="bindings">The bindings service the pool and sets come from.</param>
    /// <param name="copyPipeline">The copy kernel's pipeline, whose set layout the sets are allocated against.</param>
    /// <param name="slotCount">The owner's frame slots; at least one.</param>
    /// <param name="name">The debug name of the region the sets serve, from its creator's identity: the pool is named
    /// bare and each slot's set at its slot's index.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> or <paramref name="copyPipeline"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The device's heap cannot hold the pool; the message carries
    /// <see cref="GpuDescriptorHeapBudget.RefusalCode"/>.</exception>
    public GpuRegionCopySets(IGpuBindings bindings, IGpuComputePipeline copyPipeline, int slotCount, in GpuObjectName name) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(copyPipeline);

        var sizes = GpuRegion.CopyPoolSizes(slotCount: slotCount);

        m_bindings = bindings;
        m_sets = new nint[slotCount];
        m_writers = new GpuRegion?[slotCount];
        m_pool = bindings.CreatePool(
            name: name,
            sizes: sizes
        );

        try {
            for (var slot = 0; (slot < slotCount); slot++) {
                m_sets[slot] = bindings.AllocateSet(
                    descriptorSetLayoutHandle: copyPipeline.DescriptorSetLayoutHandle,
                    name: name.At(index: slot),
                    poolHandle: m_pool
                );
            }
        } catch {
            Dispose();

            throw;
        }
    }

    /// <summary>Gets the number of frame slots, one set each.</summary>
    public int SlotCount => m_sets.Length;

    /// <summary>Returns the copy set of a slot.</summary>
    /// <param name="slot">The frame slot; below <see cref="SlotCount"/>.</param>
    /// <returns>The set's handle.</returns>
    /// <exception cref="ObjectDisposedException">The sets have been disposed.</exception>
    public nint SetOf(int slot) {
        ObjectDisposedException.ThrowIf(
            condition: (m_pool == 0),
            instance: this
        );

        return m_sets[slot];
    }
    /// <summary>Destroys the pool, which releases every set allocated from it. Disposing twice does nothing.</summary>
    public void Dispose() {
        if (m_pool == 0) {
            return;
        }

        m_bindings.DestroyPool(poolHandle: m_pool);
        m_pool = 0;
        Array.Clear(array: m_writers);
    }

    // Whether the slot's set holds the region's buffers: no other region wrote it after the region last did.
    internal bool IsWrittenBy(int slot, GpuRegion region) =>
        ReferenceEquals(
            objA: m_writers[slot],
            objB: region
        );
    // Records that the region's buffers are now what the slot's set holds.
    internal void WrittenBy(int slot, GpuRegion region) =>
        m_writers[slot] = region;
}

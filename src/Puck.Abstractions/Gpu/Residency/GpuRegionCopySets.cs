namespace Puck.Abstractions.Gpu;

/// <summary>
/// One copy descriptor set per frame slot, from one pool sized by <see cref="GpuRegion.CopyPoolSizes"/>, which an owner
/// creates when it is admitted and hands every staged <see cref="GpuRegion"/> it makes. A region created with them writes
/// its own buffers into these sets instead of creating a pool, so a region its owner creates or grows at a later frame
/// takes no descriptor range then and cannot be refused by a heap another owner filled in between. The sets outlive the
/// regions that write them; the owner writes them only while no submission reading them is in flight, and disposes
/// them after its last region.
/// </summary>
public sealed class GpuRegionCopySets : IDisposable {
    private readonly IGpuBindings m_bindings;
    private readonly nint[] m_sets;

    private nint m_pool;

    /// <summary>Initializes a new instance of the <see cref="GpuRegionCopySets"/> class, creating the pool and
    /// allocating one set per slot against the copy kernel's layout.</summary>
    /// <param name="bindings">The bindings service the pool and sets come from.</param>
    /// <param name="copyPipeline">The copy kernel's pipeline, whose set layout the sets are allocated against.</param>
    /// <param name="slotCount">The owner's frame slots; at least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> or <paramref name="copyPipeline"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotCount"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">The device's heap cannot hold the pool; the message carries
    /// <see cref="GpuDescriptorHeapBudget.RefusalCode"/>.</exception>
    public GpuRegionCopySets(IGpuBindings bindings, IGpuComputePipeline copyPipeline, int slotCount) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(copyPipeline);

        m_bindings = bindings;
        m_pool = bindings.CreatePool(sizes: GpuRegion.CopyPoolSizes(slotCount: slotCount));
        m_sets = new nint[slotCount];

        try {
            for (var slot = 0; (slot < slotCount); slot++) {
                m_sets[slot] = bindings.AllocateSet(
                    descriptorSetLayoutHandle: copyPipeline.DescriptorSetLayoutHandle,
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
    /// <summary>Destroys the pool, which releases every set allocated from it.</summary>
    public void Dispose() {
        m_bindings.DestroyPool(poolHandle: m_pool);
        m_pool = 0;
    }
}

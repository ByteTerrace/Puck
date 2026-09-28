namespace Puck.Abstractions.Gpu;

/// <summary>
/// One descriptor pool holding the copy sets of one or more staged <see cref="GpuRegion"/>s, one set per region and
/// frame slot, sized by <see cref="SizesOf"/>. Each region takes its share as a <see cref="GpuRegionCopySets"/>
/// (<see cref="Region"/>). A region creates a pool of its own for one region unless its owner hands it a share of a pool
/// it reserved, which an owner does when it creates its regions' sets while it is admitted, so a region it creates or
/// replaces at a later frame takes no descriptor range then and cannot be refused by a heap another owner filled in
/// between. An owner reserves every region's sets in one pool, so it holds one copy pool however many regions it has.
/// <para>
/// Reserved sets outlive the regions that write them, one region at a time per share: a region writes its buffers into
/// a slot's set when it is created, and again before it records that slot's copy if another region wrote the set since,
/// which happens only with the slot's last submission retired. The owner disposes the pool after its last region.
/// </para>
/// </summary>
public sealed class GpuRegionCopyPool : IDisposable {
    private readonly IGpuBindings m_bindings;
    private readonly GpuRegionCopySets[] m_regions;
    // Every region's sets, slot by slot: region r's set of slot s at r * SlotCount + s.
    private readonly nint[] m_sets;
    // The region whose buffers each set holds, or null before any region wrote it; indexed as m_sets.
    private readonly GpuRegion?[] m_writers;

    private nint m_pool;

    /// <summary>Initializes a new instance of the <see cref="GpuRegionCopyPool"/> class, creating the pool and
    /// allocating one set per region and slot against the copy kernel's layout.</summary>
    /// <param name="bindings">The bindings service the pool and sets come from.</param>
    /// <param name="copyPipeline">The copy kernel's pipeline, whose set layout the sets are allocated against.</param>
    /// <param name="slotCount">The owner's frame slots; at least one.</param>
    /// <param name="name">The pool's debug name, from its owner's identity; the pool is named bare.</param>
    /// <param name="regions">The debug name of each region the pool serves, from its creator's identity, in the order
    /// <see cref="Region"/> indexes them: each region's set of a slot is named at the slot's index. At least one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bindings"/> or <paramref name="copyPipeline"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slotCount"/> is not positive, or
    /// <paramref name="regions"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The device's heap cannot hold the pool; the message carries
    /// <see cref="GpuDescriptorHeapBudget.RefusalCode"/>.</exception>
    public GpuRegionCopyPool(IGpuBindings bindings, IGpuComputePipeline copyPipeline, int slotCount, in GpuObjectName name, ReadOnlySpan<GpuObjectName> regions) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(copyPipeline);

        var sizes = SizesOf(
            regionCount: regions.Length,
            slotCount: slotCount
        );

        m_bindings = bindings;
        SlotCount = slotCount;
        m_regions = new GpuRegionCopySets[regions.Length];
        m_sets = new nint[(regions.Length * slotCount)];
        m_writers = new GpuRegion?[m_sets.Length];
        m_pool = bindings.CreatePool(
            name: name,
            sizes: sizes
        );

        try {
            for (var region = 0; (region < regions.Length); region++) {
                m_regions[region] = new GpuRegionCopySets(
                    pool: this,
                    region: region
                );

                for (var slot = 0; (slot < slotCount); slot++) {
                    m_sets[((region * slotCount) + slot)] = bindings.AllocateSet(
                        descriptorSetLayoutHandle: copyPipeline.DescriptorSetLayoutHandle,
                        name: regions[region].At(index: slot),
                        poolHandle: m_pool
                    );
                }
            }
        } catch {
            Dispose();

            throw;
        }
    }

    /// <summary>Returns the descriptor pool of <paramref name="regionCount"/> staged regions' copy sets: one
    /// <see cref="GpuRegion.CopyBindings"/> set per region and frame slot. A region under any other policy than
    /// <see cref="GpuResidencyPolicy.Staged"/> writes none, and one created without reserved sets creates a pool of
    /// these sizes for itself alone.</summary>
    /// <param name="regionCount">The regions the pool serves; at least one.</param>
    /// <param name="slotCount">The frame slots each region serves; at least one.</param>
    /// <returns>The copy pool's sizes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="regionCount"/> or <paramref name="slotCount"/> is
    /// not positive.</exception>
    public static GpuDescriptorPoolSizes SizesOf(int regionCount, int slotCount) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: regionCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: slotCount);

        var sets = new IReadOnlyList<GpuComputeBinding>[checked((regionCount * slotCount))];

        Array.Fill(
            array: sets,
            value: GpuRegion.CopyBindings
        );

        return GpuDescriptorPoolSizes.ForSets(sets: sets);
    }

    /// <summary>Gets the number of regions the pool serves.</summary>
    public int RegionCount => m_regions.Length;
    /// <summary>Gets the number of frame slots, one set each per region.</summary>
    public int SlotCount { get; }

    /// <summary>Destroys the pool, which releases every region's sets. Disposing twice does nothing.</summary>
    public void Dispose() {
        if (m_pool == 0) {
            return;
        }

        m_bindings.DestroyPool(poolHandle: m_pool);
        m_pool = 0;
        Array.Clear(array: m_writers);
    }
    /// <summary>Returns a region's share of the pool, which a <see cref="GpuRegion"/> created with it writes in place of
    /// creating sets of its own.</summary>
    /// <param name="index">The region's index, in the order the constructor named the regions; below
    /// <see cref="RegionCount"/>.</param>
    /// <returns>The region's copy sets; the pool keeps owning them.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative or not below
    /// <see cref="RegionCount"/>.</exception>
    public GpuRegionCopySets Region(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: RegionCount,
            value: index
        );

        return m_regions[index];
    }

    // Region's set of slot.
    internal nint SetOf(int region, int slot) {
        ObjectDisposedException.ThrowIf(
            condition: (m_pool == 0),
            instance: this
        );

        return m_sets[IndexOf(region: region, slot: slot)];
    }
    // Whether region's set of slot holds the writer's buffers: no other region wrote it after the writer last did.
    internal bool IsWrittenBy(int region, int slot, GpuRegion writer) =>
        ReferenceEquals(
            objA: m_writers[IndexOf(region: region, slot: slot)],
            objB: writer
        );
    // Records that the writer's buffers are now what region's set of slot holds.
    internal void WrittenBy(int region, int slot, GpuRegion writer) =>
        m_writers[IndexOf(region: region, slot: slot)] = writer;

    private int IndexOf(int region, int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: SlotCount,
            value: slot
        );

        return ((region * SlotCount) + slot);
    }
}

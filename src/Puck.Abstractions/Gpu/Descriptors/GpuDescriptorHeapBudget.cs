namespace Puck.Abstractions.Gpu;

/// <summary>
/// One device's shader-visible descriptor heaps and what is admitted into them. The heaps are the device's own:
/// the view heap takes the device's reported <see cref="GpuDeviceCapabilities.ViewHeapSize"/> and the sampler heap its
/// reported <see cref="GpuDeviceCapabilities.SamplerHeapSize"/>, each the device's own limit (a Direct3D 12 runtime that
/// does not answer options 19 reports the limits every binding tier guarantees), because the device outlives every world and a heap sized by the first world would refuse a larger one later on a device
/// with room.
/// <para>What varies is admission. Each pool owner states its pools' <see cref="GpuDescriptorPoolSizes"/> from the same
/// statement its pool creation uses, and <see cref="TryAdmit"/> checks a candidate's pools against the free ranges
/// before anything is allocated: a candidate that does not fit is refused by name, whatever is installed keeps
/// presenting, and nothing grows. Each admitted pool is one range of the view heap's <see cref="GpuRangeAllocator"/>
/// for its views and one of the sampler heap's for its samplers, so at most <see cref="MaxLivePools"/> pools holding
/// views, and as many holding samplers, are live on a device.</para>
/// </summary>
public sealed class GpuDescriptorHeapBudget {
    /// <summary>The most descriptor pools live at once on one device. It bounds the range allocator's bookkeeping, not
    /// descriptors: the documented worst cases are 69 SDF engines (one main, 64 registered views and 4 sessions, one
    /// pool each), one overlay and a Direct3D 12 device's range for storage clears, which leave 953 pools for pipeline
    /// nodes and their previews. A pipeline node holds one pool for all its passes and in-flight slots and one for its
    /// float preview, which makes that 476 pipeline instances with previews, past any layout. Refused by name past it, like
    /// <c>ShaderPipelineLimits.MaxPasses</c>.</summary>
    public const int MaxLivePools = 1024;
    /// <summary>The code every refusal of a candidate that does not fit carries, whichever owner it names.</summary>
    public const string RefusalCode = "GPU_DESCRIPTOR_HEAP";

    private readonly GpuRangeAllocator m_samplers;
    private readonly GpuRangeAllocator m_views;

    /// <summary>Initializes a new instance of the <see cref="GpuDescriptorHeapBudget"/> class from a device's
    /// capabilities.</summary>
    /// <param name="capabilities">The device's capability report.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The device reports no shader-visible view or sampler heap, as a Vulkan
    /// device does, whose pools are its own.</exception>
    public GpuDescriptorHeapBudget(GpuDeviceCapabilities capabilities) {
        ArgumentNullException.ThrowIfNull(argument: capabilities);

        if (
            (capabilities.ViewHeapSize == 0) ||
            (capabilities.SamplerHeapSize == 0)
        ) {
            throw new ArgumentException(
                message: $"The {capabilities.Backend} device reports no shader-visible descriptor heap (views {capabilities.ViewHeapSize}, samplers {capabilities.SamplerHeapSize}); only a device with one shares a heap between its pools.",
                paramName: nameof(capabilities)
            );
        }

        SamplerDescriptors = capabilities.SamplerHeapSize;
        ViewDescriptors = capabilities.ViewHeapSize;
        m_samplers = new GpuRangeAllocator(
            maxRanges: MaxLivePools,
            size: SamplerDescriptors
        );
        m_views = new GpuRangeAllocator(
            maxRanges: MaxLivePools,
            size: ViewDescriptors
        );
    }

    /// <summary>Gets the pools holding views live now.</summary>
    public int LivePools => m_views.LiveRanges;
    /// <summary>Gets the sampler descriptors not admitted to any pool.</summary>
    public uint FreeSamplerDescriptors => m_samplers.FreeCount;
    /// <summary>Gets the sampler heap's size in descriptors.</summary>
    public uint SamplerDescriptors { get; }
    /// <summary>Gets the view descriptors not admitted to any pool.</summary>
    public uint FreeViewDescriptors => m_views.FreeCount;
    /// <summary>Gets the view heap's size in descriptors.</summary>
    public uint ViewDescriptors { get; }

    /// <summary>Returns the bytes a heap of <paramref name="descriptors"/> occupies at the device's descriptor
    /// increment, the figure its memory counter records.</summary>
    /// <param name="descriptors">The heap's size in descriptors.</param>
    /// <param name="incrementBytes">The device's descriptor handle increment for the heap's type.</param>
    /// <returns>The heap's bytes.</returns>
    public static ulong HeapBytes(uint descriptors, uint incrementBytes) => (((ulong)descriptors) * incrementBytes);
    /// <summary>Admits a candidate's pools, allocating one view range for each pool that holds a view and one sampler
    /// range for each pool that holds a sampler, or refuses it by name and allocates nothing.</summary>
    /// <param name="owner">The candidate's name, echoed in a refusal.</param>
    /// <param name="pools">The pools the candidate creates, each as its pool creation states it.</param>
    /// <param name="admission">The admitted ranges, which <see cref="Release"/> returns; <see langword="null"/> on
    /// refusal.</param>
    /// <param name="refusal">Why the candidate does not fit, or empty on admission.</param>
    /// <returns>Whether the candidate was admitted.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pools"/> is <see langword="null"/>.</exception>
    public bool TryAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out GpuDescriptorAdmission? admission, out string refusal) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: owner);
        ArgumentNullException.ThrowIfNull(argument: pools);

        var starts = new List<(uint Start, uint Count)>(capacity: pools.Count);
        var samplerStarts = new List<(uint Start, uint Count)>();

        try {
            foreach (var pool in pools) {
                var count = pool.HeapDescriptors;

                if (count != 0) {
                    starts.Add(item: (m_views.Allocate(count: count), count));
                }
            }
            foreach (var pool in pools) {
                var count = pool.SamplerHeapDescriptors;

                if (count != 0) {
                    samplerStarts.Add(item: (m_samplers.Allocate(count: count), count));
                }
            }
        } catch (GpuRangeExhaustedException exception) {
            Free(
                allocator: m_views,
                ranges: starts
            );
            Free(
                allocator: m_samplers,
                ranges: samplerStarts
            );
            var samplers = pools.Sum(selector: static pool => ((long)pool.SamplerHeapDescriptors));

            admission = null;
            refusal = $"[{RefusalCode}] '{owner}' needs {pools.Sum(selector: static pool => ((long)pool.HeapDescriptors))} view {((samplers == 0)
                ? string.Empty
                : $"and {samplers} sampler ")}descriptors in {pools.Count} pool(s) and is refused: {exception.Message}";

            return false;
        }

        admission = new GpuDescriptorAdmission(
            owner: owner,
            ranges: starts.AsReadOnly(),
            samplerRanges: samplerStarts.AsReadOnly()
        );
        refusal = string.Empty;

        return true;
    }
    /// <summary>Checks whether a candidate's pools fit the free ranges now, allocating nothing: an admission
    /// <see cref="TryAdmit"/> would grant is taken and at once released, so the heap is left as it was found.</summary>
    /// <param name="owner">The candidate's name, echoed in a refusal.</param>
    /// <param name="pools">The pools the candidate would create, each as its pool creation states it.</param>
    /// <param name="refusal">Why the candidate does not fit, or empty when it does.</param>
    /// <returns>Whether the candidate fits.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="pools"/> is <see langword="null"/>.</exception>
    public bool CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal) {
        if (!TryAdmit(
            admission: out var admission,
            owner: owner,
            pools: pools,
            refusal: out refusal
        )) {
            return false;
        }

        Release(admission: admission);

        return true;
    }
    /// <summary>Returns an admission's ranges to the heap.</summary>
    /// <param name="admission">The admission <see cref="TryAdmit"/> returned.</param>
    /// <exception cref="ArgumentNullException"><paramref name="admission"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="admission"/> was released already.</exception>
    public void Release(GpuDescriptorAdmission admission) {
        ArgumentNullException.ThrowIfNull(argument: admission);

        Free(
            allocator: m_views,
            ranges: admission.Ranges
        );
        Free(
            allocator: m_samplers,
            ranges: admission.SamplerRanges
        );
    }

    private static void Free(GpuRangeAllocator allocator, IReadOnlyList<(uint Start, uint Count)> ranges) {
        foreach (var (start, _) in ranges) {
            _ = allocator.Free(start: start);
        }
    }
}
/// <summary>The view and sampler ranges one owner's pools hold in a <see cref="GpuDescriptorHeapBudget"/>.</summary>
public sealed class GpuDescriptorAdmission {
    internal GpuDescriptorAdmission(string owner, IReadOnlyList<(uint Start, uint Count)> ranges, IReadOnlyList<(uint Start, uint Count)> samplerRanges) {
        Owner = owner;
        Ranges = ranges;
        SamplerRanges = samplerRanges;
    }

    /// <summary>Gets the owner's name.</summary>
    public string Owner { get; }
    /// <summary>Gets each pool's view range: its first view descriptor and its count, in pool order, for the pools that
    /// hold a view.</summary>
    public IReadOnlyList<(uint Start, uint Count)> Ranges { get; }
    /// <summary>Gets each pool's sampler range: its first sampler descriptor and its count, in pool order, for the
    /// pools that hold a sampler.</summary>
    public IReadOnlyList<(uint Start, uint Count)> SamplerRanges { get; }
}

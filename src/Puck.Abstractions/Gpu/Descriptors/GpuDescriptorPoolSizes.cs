namespace Puck.Abstractions.Gpu;

/// <summary>
/// The per-descriptor-kind capacity a pool must provide to back one or more compute descriptor sets, DERIVED from
/// their binding lists rather than hand-tallied. Pass it to <see cref="IGpuDescriptorAllocator.CreatePool"/>
/// so a pool can never silently drift out of sync with the bindings it must satisfy — a hand-counted capacity that
/// under-provisions throws at <c>AllocateSet</c> (or, on a backend that bump-allocates a shared heap, corrupts it).
/// </summary>
/// <param name="MaxSets">The number of descriptor sets allocated from the pool (one per pipeline whose bindings were summed).</param>
/// <param name="CombinedImageSamplerCount">Total combined image-sampler descriptors — graphics texture bindings and any compute <see cref="GpuComputeBindingKind.SampledImage"/> source.</param>
/// <param name="StorageBufferCount">Total storage-buffer descriptors (read + read-write).</param>
/// <param name="StorageImageCount">Total storage-image descriptors.</param>
/// <param name="AccelerationStructureCount">Total acceleration-structure descriptors (ray-query only).</param>
public readonly record struct GpuDescriptorPoolSizes(
    uint MaxSets,
    uint CombinedImageSamplerCount,
    uint StorageBufferCount,
    uint StorageImageCount,
    uint AccelerationStructureCount
) {
    /// <summary>Sums the per-kind descriptor demand across one or more descriptor sets, each a compute pipeline's
    /// binding list. <see cref="MaxSets"/> is the number of sets; an array binding (<see cref="GpuComputeBinding.Count"/>
    /// &gt; 1) contributes its full count. Backend-neutral: a Vulkan pool consumes the per-kind counts directly, while a
    /// Direct3D 12 heap sums them into its total slot capacity (every binding occupies its Count slots regardless of
    /// kind, so the sum of these counts equals the heap's packed slot total).</summary>
    /// <param name="sets">The binding list of each descriptor set the pool will back.</param>
    public static GpuDescriptorPoolSizes ForSets(params IReadOnlyList<GpuComputeBinding>[] sets) {
        ArgumentNullException.ThrowIfNull(sets);

        var combinedImageSamplerCount = 0u;
        var storageBufferCount = 0u;
        var storageImageCount = 0u;
        var accelerationStructureCount = 0u;

        foreach (var set in sets) {
            foreach (var binding in set) {
                var count = ((binding.Count > 0) ? binding.Count : 1);

                switch (binding.Kind) {
                    case GpuComputeBindingKind.StorageImage:
                        storageImageCount += count;

                        break;
                    case GpuComputeBindingKind.StorageBufferRead:
                    case GpuComputeBindingKind.StorageBufferReadWrite:
                        storageBufferCount += count;

                        break;
                    case GpuComputeBindingKind.AccelerationStructure:
                        accelerationStructureCount += count;

                        break;
                    case GpuComputeBindingKind.SampledImage:
                        // A sampled image is a combined-image-sampler descriptor on Vulkan; on Direct3D 12 it is one
                        // SRV heap slot. Either way it must be provisioned, or the pool/heap under-counts.
                        combinedImageSamplerCount += count;

                        break;
                    default:
                        break;
                }
            }
        }

        return new GpuDescriptorPoolSizes(
            MaxSets: (uint)sets.Length,
            CombinedImageSamplerCount: combinedImageSamplerCount,
            StorageBufferCount: storageBufferCount,
            StorageImageCount: storageImageCount,
            AccelerationStructureCount: accelerationStructureCount
        );
    }
}

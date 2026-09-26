namespace Puck.Abstractions.Gpu;

/// <summary>
/// The per-descriptor-kind capacity a pool must provide to back one or more descriptor sets, compute binding lists'
/// (<see cref="ForSets"/>) or groups' (<see cref="ForGroups"/>), DERIVED from their binding lists rather than
/// hand-tallied. Pass it to <see cref="IGpuBindings.CreatePool"/>
/// so a pool can never silently drift out of sync with the bindings it must satisfy — a hand-counted capacity that
/// under-provisions throws at <c>AllocateSet</c>.
/// </summary>
/// <param name="MaxSets">The number of descriptor sets allocated from the pool (one per pipeline whose bindings were summed).</param>
/// <param name="StorageBufferCount">Total storage-buffer descriptors (read + read-write).</param>
/// <param name="StorageImageCount">Total storage-image descriptors.</param>
/// <param name="ConstantBufferCount">Total constant-buffer descriptors, which only a group's sets hold.</param>
/// <param name="SampledImageCount">Total sampled-image descriptors read through a separate sampler, which only a
/// group's sets hold.</param>
/// <param name="SamplerCount">Total sampler descriptors, which only a group's sets hold: on Direct3D 12 a range of the
/// device's shader-visible sampler heap rather than of its view heap.</param>
public readonly record struct GpuDescriptorPoolSizes(
    uint MaxSets,
    uint StorageBufferCount,
    uint StorageImageCount,
    uint ConstantBufferCount = 0,
    uint SampledImageCount = 0,
    uint SamplerCount = 0
) {
    /// <summary>Gets the shader-visible view-heap slots the pool occupies on a backend that packs every view kind into
    /// one heap: the sum of the per-kind counts other than samplers.</summary>
    public uint HeapDescriptors => checked((((StorageBufferCount + StorageImageCount) + ConstantBufferCount) + SampledImageCount));
    /// <summary>Gets the shader-visible sampler-heap slots the pool occupies on a backend whose samplers live in a heap
    /// of their own: <see cref="SamplerCount"/>.</summary>
    public uint SamplerHeapDescriptors => SamplerCount;

    /// <summary>Sums two pools' demand kind by kind, sets included, as one pool backing both sets of sets.</summary>
    /// <param name="left">One pool's sizes.</param>
    /// <param name="right">The other's.</param>
    /// <returns>The sizes of a pool backing both.</returns>
    /// <exception cref="OverflowException">A count overflows.</exception>
    public static GpuDescriptorPoolSizes operator +(GpuDescriptorPoolSizes left, GpuDescriptorPoolSizes right) =>
        new(
            ConstantBufferCount: checked((left.ConstantBufferCount + right.ConstantBufferCount)),
            MaxSets: checked((left.MaxSets + right.MaxSets)),
            SampledImageCount: checked((left.SampledImageCount + right.SampledImageCount)),
            SamplerCount: checked((left.SamplerCount + right.SamplerCount)),
            StorageBufferCount: checked((left.StorageBufferCount + right.StorageBufferCount)),
            StorageImageCount: checked((left.StorageImageCount + right.StorageImageCount))
        );

    /// <summary>Sums the per-kind descriptor demand of one set of each group, as a pipeline created from a
    /// <see cref="GpuPipelineLayoutDescription"/> allocates them: <see cref="MaxSets"/> is the number of groups, and an
    /// array binding contributes its full count. A Vulkan pool consumes the per-kind counts directly; a Direct3D 12 pool
    /// is a range of the device's view heap as long as <see cref="HeapDescriptors"/> and a range of its sampler heap as
    /// long as <see cref="SamplerHeapDescriptors"/>.</summary>
    /// <param name="groups">The groups whose sets the pool will back, one set each.</param>
    /// <returns>The pool's sizes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="groups"/> or one of its groups is
    /// <see langword="null"/>.</exception>
    public static GpuDescriptorPoolSizes ForGroups(params IReadOnlyList<GpuGroupLayoutDescription> groups) {
        ArgumentNullException.ThrowIfNull(argument: groups);

        var constantBufferCount = 0u;
        var sampledImageCount = 0u;
        var samplerCount = 0u;
        var storageBufferCount = 0u;
        var storageImageCount = 0u;

        foreach (var group in groups) {
            ArgumentNullException.ThrowIfNull(argument: group);

            foreach (var binding in group.Bindings) {
                var count = binding.Count;

                switch (binding.Kind) {
                    case GpuBindingKind.ConstantBuffer:
                        constantBufferCount = checked((constantBufferCount + count));

                        break;
                    case GpuBindingKind.ReadOnlyBuffer:
                    case GpuBindingKind.ReadWriteBuffer:
                        storageBufferCount = checked((storageBufferCount + count));

                        break;
                    case GpuBindingKind.SampledImage:
                        sampledImageCount = checked((sampledImageCount + count));

                        break;
                    case GpuBindingKind.StorageImage:
                        storageImageCount = checked((storageImageCount + count));

                        break;
                    case GpuBindingKind.Sampler:
                        samplerCount = checked((samplerCount + count));

                        break;
                    default:
                        throw new InvalidOperationException(message: $"Binding kind '{binding.Kind}' has no pool-size classification.");
                }
            }
        }

        return new GpuDescriptorPoolSizes(
            ConstantBufferCount: constantBufferCount,
            MaxSets: ((uint)groups.Count),
            SampledImageCount: sampledImageCount,
            SamplerCount: samplerCount,
            StorageBufferCount: storageBufferCount,
            StorageImageCount: storageImageCount
        );
    }
    /// <summary>Sums the per-kind descriptor demand across one or more descriptor sets, each a compute pipeline's
    /// binding list. <see cref="MaxSets"/> is the number of sets; an array binding (<see cref="GpuComputeBinding.Count"/>
    /// &gt; 1) contributes its full count. Backend-neutral: a Vulkan pool consumes the per-kind counts directly, while a
    /// Direct3D 12 pool is a range of the device's view heap as long as their sum (every binding occupies its Count
    /// slots regardless of kind, so the sum of these counts equals the range's packed slot total).</summary>
    /// <param name="sets">The binding list of each descriptor set the pool will back.</param>
    public static GpuDescriptorPoolSizes ForSets(params IReadOnlyList<GpuComputeBinding>[] sets) {
        ArgumentNullException.ThrowIfNull(sets);

        var storageBufferCount = 0u;
        var storageImageCount = 0u;

        foreach (var set in sets) {
            ArgumentNullException.ThrowIfNull(set);
            GpuComputeBinding.ValidateSet(bindings: set);

            foreach (var binding in set) {
                var count = binding.Count;

                switch (binding.Kind) {
                    case GpuBindingKind.StorageImage:
                        storageImageCount = checked((storageImageCount + count));

                        break;
                    case GpuBindingKind.ReadOnlyBuffer:
                    case GpuBindingKind.ReadWriteBuffer:
                        storageBufferCount = checked((storageBufferCount + count));

                        break;
                    default:
                        throw new InvalidOperationException(message: $"Descriptor binding kind '{binding.Kind}' has no pool-size classification.");
                }
            }
        }

        return new GpuDescriptorPoolSizes(
            MaxSets: ((uint)sets.Length),
            StorageBufferCount: storageBufferCount,
            StorageImageCount: storageImageCount
        );
    }
}

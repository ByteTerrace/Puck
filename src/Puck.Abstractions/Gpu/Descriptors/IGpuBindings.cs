namespace Puck.Abstractions.Gpu;

/// <summary>
/// Creates and fills the descriptor pools, descriptor sets and samplers a pipeline binds, on the device its backend
/// implementation is bound to. Every write names a set, a binding within it, and the resource the binding reads.
/// </summary>
public interface IGpuBindings {
    /// <summary>The size in bytes a constant buffer's view is a multiple of: Direct3D 12's constant buffer view
    /// alignment, which a Vulkan uniform buffer range also satisfies.</summary>
    const ulong ConstantBufferAlignment = 256UL;

    /// <summary>The one check every backend's <see cref="WriteConstantBuffer"/> makes: a view's size is a non-zero
    /// multiple of <see cref="ConstantBufferAlignment"/>.</summary>
    /// <param name="bufferSize">The viewed size in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The size is zero or unaligned.</exception>
    static void RequireConstantBufferSize(ulong bufferSize) {
        if (
            (bufferSize == 0) ||
            ((bufferSize % ConstantBufferAlignment) != 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: bufferSize,
                message: $"A constant buffer's view is a non-zero multiple of {ConstantBufferAlignment} bytes.",
                paramName: nameof(bufferSize)
            );
        }
    }

    /// <summary>Gets the release revision of the device's shader-visible heaps
    /// (<see cref="GpuDescriptorHeapBudget.ReleaseRevision"/>), which advances whenever a pool's ranges are returned. An
    /// owner refused with <see cref="GpuDescriptorHeapRefusalException"/> tries again when it changes. Zero on Vulkan,
    /// whose pools are their own and never refused by a heap.</summary>
    long HeapReleaseRevision { get; }

    /// <summary>Allocates a single descriptor set of the given layout from a pool. A set allocated against a group's
    /// layout handle (<see cref="IGpuComputePipeline.GroupLayoutHandles"/>, <see cref="IGpuPipeline.GroupLayoutHandles"/>)
    /// belongs to that group, and <see cref="IGpuRecorder.BindDescriptorSet"/> binds it only at that group's ordinal;
    /// a set of any other layout belongs to group 0. The set is the pool's, released by
    /// <see cref="DestroyPool"/>.</summary>
    /// <param name="poolHandle">The native descriptor pool handle, from <see cref="CreatePool"/>.</param>
    /// <param name="descriptorSetLayoutHandle">The pipeline's descriptor set layout handle.</param>
    /// <returns>The native descriptor set handle, owned by the pool.</returns>
    nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle);
    /// <summary>Checks, before an owner allocates anything, whether the pools it would create fit the device's
    /// descriptor heaps now, allocating nothing. On Direct3D 12 each pool is a range of the device's one shader-visible
    /// view heap (<see cref="GpuDescriptorHeapBudget.CanAdmit"/>), and a candidate that does not fit is refused whole,
    /// its refusal carrying <see cref="GpuDescriptorHeapBudget.RefusalCode"/> and naming the owner. A Vulkan pool is a
    /// descriptor pool of its own, so every candidate fits there.</summary>
    /// <param name="owner">The candidate's name, echoed in a refusal.</param>
    /// <param name="pools">The pools the candidate would create, from the statement its pool creation reads.</param>
    /// <param name="refusal">Why the candidate does not fit, or empty when it does.</param>
    /// <returns>Whether the candidate fits.</returns>
    bool CanAdmit(string owner, IReadOnlyList<GpuDescriptorPoolSizes> pools, out string refusal);
    /// <summary>Creates a descriptor pool for the descriptor kinds <paramref name="sizes"/> counts. On Direct3D 12 the
    /// pool is a range of the device's shader-visible view heap, and a pool holding samplers also a range of its sampler
    /// heap; a pool no free range holds is refused with an <see cref="InvalidOperationException"/> carrying
    /// <see cref="GpuDescriptorHeapBudget.RefusalCode"/>; an owner checks <see cref="CanAdmit"/> first, so it is refused
    /// before it allocates.</summary>
    /// <param name="sizes">The per-descriptor-kind capacity the pool must provide, derived from the binding lists
    /// of the sets it backs (see <see cref="GpuDescriptorPoolSizes.ForSets"/> and
    /// <see cref="GpuDescriptorPoolSizes.ForGroups"/>) rather than hand-tallied.</param>
    /// <returns>The native descriptor pool handle.</returns>
    nint CreatePool(in GpuDescriptorPoolSizes sizes);
    /// <summary>Creates a sampler with the given filter and clamp-to-edge addressing. On Direct3D 12 a pipeline created
    /// without a layout description reads its samplers as static samplers in its root signature, so this returns a
    /// non-zero sentinel and the filter is applied by the compute pipeline's static sampler instead.</summary>
    /// <param name="filter">The min/mag filter.</param>
    /// <returns>The native sampler handle.</returns>
    nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear);
    /// <summary>Destroys a descriptor pool and every set allocated from it. Destroying pool zero does nothing and
    /// reaches no device on either backend, as destroying <c>VK_NULL_HANDLE</c> does on Vulkan, so an owner that
    /// never created its pool destroys it unguarded.</summary>
    /// <param name="poolHandle">The native descriptor pool handle, or zero.</param>
    void DestroyPool(nint poolHandle);
    /// <summary>Destroys a sampler.</summary>
    /// <param name="samplerHandle">The native sampler handle.</param>
    void DestroySampler(nint samplerHandle);
    /// <summary>Writes a buffer descriptor into a set. A Vulkan storage buffer carries no view-side layout, so there
    /// every buffer write is the same descriptor. On Direct3D 12 <paramref name="kind"/> chooses a shader resource
    /// view or an unordered access view, and <paramref name="elementStride"/> the view's shape: zero is a raw view over
    /// 4-byte words (<c>ByteAddressBuffer</c>), any other value a structured view over elements of that size
    /// (<c>StructuredBuffer&lt;T&gt;</c>), whose element count is the size divided by the stride.</summary>
    /// <param name="descriptorSetHandle">The descriptor set to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="bufferHandle">The native buffer handle.</param>
    /// <param name="bufferSize">The viewed size in bytes, a multiple of four and of <paramref name="elementStride"/>.</param>
    /// <param name="kind">The binding's kind, <see cref="GpuBindingKind.ReadOnlyBuffer"/> or
    /// <see cref="GpuBindingKind.ReadWriteBuffer"/>, as its shader declaration reads or writes the buffer.</param>
    /// <param name="elementStride">The size in bytes of the element the shader declares, or zero for a raw view.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not a storage-buffer kind.</exception>
    void WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBindingKind kind, uint elementStride);
    /// <summary>Writes a combined image sampler descriptor into a set.</summary>
    /// <param name="descriptorSetHandle">The descriptor set to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The element of an arrayed binding.</param>
    /// <param name="imageViewHandle">The native image view handle.</param>
    /// <param name="samplerHandle">The native sampler handle, from <see cref="CreateSampler"/>.</param>
    void WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle);
    /// <summary>Writes a constant buffer descriptor into a group's set: the buffer a <c>ConstantBuffer&lt;T&gt;</c>
    /// declaration of a <see cref="GpuBindingKind.ConstantBuffer"/> binding reads, from its first byte.</summary>
    /// <param name="descriptorSetHandle">The group's set to write into.</param>
    /// <param name="binding">The binding index within the group.</param>
    /// <param name="arrayElement">The element of an arrayed binding.</param>
    /// <param name="bufferHandle">The native buffer handle.</param>
    /// <param name="bufferSize">The viewed size in bytes, a multiple of <see cref="ConstantBufferAlignment"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferSize"/> is zero or not a multiple of
    /// <see cref="ConstantBufferAlignment"/>.</exception>
    void WriteConstantBuffer(nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize);
    /// <summary>Writes a sampled image descriptor into a group's set: an image a <see cref="GpuBindingKind.SampledImage"/>
    /// binding reads through a separate sampler, in its shader-read-only layout.</summary>
    /// <param name="descriptorSetHandle">The group's set to write into.</param>
    /// <param name="binding">The binding index within the group.</param>
    /// <param name="arrayElement">The element of an arrayed binding.</param>
    /// <param name="imageViewHandle">The native image view handle.</param>
    void WriteSampledImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle);
    /// <summary>Writes a sampler descriptor into a group's set: a <see cref="GpuBindingKind.Sampler"/> binding. On
    /// Direct3D 12 the descriptor lands in the set's sampler table, a range of the device's sampler heap.</summary>
    /// <param name="descriptorSetHandle">The group's set to write into.</param>
    /// <param name="binding">The binding index within the group.</param>
    /// <param name="arrayElement">The element of an arrayed binding.</param>
    /// <param name="samplerHandle">The native sampler handle, from <see cref="CreateSampler"/>.</param>
    void WriteSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle);
    /// <summary>Writes a storage image descriptor into a set: the image bound for compute writes.</summary>
    /// <param name="descriptorSetHandle">The descriptor set to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The element of an arrayed binding.</param>
    /// <param name="imageViewHandle">The native image view handle.</param>
    void WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle);
}

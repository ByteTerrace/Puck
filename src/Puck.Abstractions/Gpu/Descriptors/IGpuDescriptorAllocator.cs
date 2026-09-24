namespace Puck.Abstractions.Gpu;

/// <summary>
/// Manages descriptor pools, descriptor sets, and samplers in a backend-neutral way.
/// </summary>
public interface IGpuDescriptorAllocator {
    /// <summary>Allocates a single descriptor set of the given layout from a pool.</summary>
    nint AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle);
    /// <summary>Creates a descriptor pool for combined image samplers, storage buffers, and/or storage
    /// images.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="sizes">The per-descriptor-kind capacity the pool must provide, DERIVED from the binding lists
    /// of the sets it will back (see <see cref="GpuDescriptorPoolSizes.ForSets"/>) rather than hand-tallied.</param>
    /// <returns>The native descriptor pool handle.</returns>
    nint CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes);
    /// <summary>Creates a sampler with the given filter (see <see cref="GpuSamplerFilter"/>) and clamp-to-edge
    /// addressing. On Direct3D 12 samplers are static in the root signature, so this returns a non-zero sentinel and
    /// the filter is applied by the compute pipeline's static sampler instead.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="filter">The min/mag filter (<see cref="GpuSamplerFilter.Linear"/> or <see cref="GpuSamplerFilter.Nearest"/>).</param>
    /// <returns>The native sampler handle.</returns>
    nint CreateSampler(nint deviceHandle, GpuSamplerFilter filter = GpuSamplerFilter.Linear);
    /// <summary>Destroys a descriptor pool.</summary>
    void DestroyPool(nint deviceHandle, nint poolHandle);
    /// <summary>Destroys a sampler.</summary>
    void DestroySampler(nint deviceHandle, nint samplerHandle);
    /// <summary>Writes a combined image sampler descriptor into a set.</summary>
    void WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle);
    /// <summary>Writes a read-only storage buffer descriptor into a set (a Vulkan storage buffer, or a Direct3D 12 SRV).</summary>
    void WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize);
    /// <summary>Writes a read-only storage buffer descriptor over <see langword="uint"/>-stride (4-byte) elements into a
    /// set. Unlike <see cref="WriteStorageBuffer"/> — whose Direct3D 12 SRV uses the 16-byte (<c>uint4</c>) program-word
    /// stride — this matches a <c>StructuredBuffer&lt;uint&gt;</c>/<c>&lt;float&gt;</c> so the element count is correct
    /// for small buffers (a Direct3D 12 stride-16 SRV over an 8-byte buffer yields a zero-element, page-faulting view).</summary>
    void WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize);
    /// <summary>Writes a read-write storage buffer descriptor into a set (a Vulkan storage buffer, or a Direct3D 12 UAV).</summary>
    void WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize);
    /// <summary>Writes a raw (byte-address) buffer descriptor into a set: the view a <c>ByteAddressBuffer</c> or
    /// <c>RWByteAddressBuffer</c> declaration reads. On Direct3D 12 it is a raw SRV, or a raw UAV when
    /// <paramref name="writable"/>, over 4-byte words; a Vulkan storage buffer carries no view-side layout, so there it
    /// is the ordinary storage-buffer write.</summary>
    /// <param name="deviceHandle">The native device handle.</param>
    /// <param name="descriptorSetHandle">The descriptor set to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="bufferHandle">The native buffer handle.</param>
    /// <param name="bufferSize">The viewed size in bytes, a multiple of four.</param>
    /// <param name="writable">Whether the binding is read-write (a UAV on Direct3D 12) rather than read-only (an SRV).</param>
    void WriteRawBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, bool writable);
    /// <summary>Writes a storage image descriptor into a set (the image bound for compute write and later sampling).</summary>
    void WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle);
}

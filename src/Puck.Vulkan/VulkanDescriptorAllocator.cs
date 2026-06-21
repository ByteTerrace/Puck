using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// An ergonomic wrapper over <see cref="IVulkanDescriptorApi"/> for the descriptor pool, set, sampler, and
/// write operations a renderer drives. It owns no policy: callers decide pool capacity, set count, and sampler
/// parameters, so it is reusable by any Vulkan consumer rather than tied to a particular pipeline's layout.
/// </summary>
public sealed class VulkanDescriptorAllocator(IVulkanDescriptorApi descriptorApi) {
    /// <summary>Allocates a single descriptor set of the given layout from a pool.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="poolHandle">The native <c>VkDescriptorPool</c> handle to allocate from.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle of the set.</param>
    /// <returns>The native <c>VkDescriptorSet</c> handle.</returns>
    public nint AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) {
        return descriptorApi.AllocateSet(request: new VulkanDescriptorSetAllocateRequest(
            DescriptorSetLayoutHandle: descriptorSetLayoutHandle,
            DeviceHandle: deviceHandle,
            PoolHandle: poolHandle
        ));
    }
    /// <summary>Creates a descriptor pool sized for the given sets and per-type descriptor capacity.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="maxSets">The maximum number of descriptor sets the pool can allocate.</param>
    /// <param name="poolSizes">The per-type descriptor capacity the pool reserves.</param>
    /// <param name="flags">A bitmask of <c>VkDescriptorPoolCreateFlagBits</c> for the pool.</param>
    /// <returns>The native <c>VkDescriptorPool</c> handle.</returns>
    public nint CreatePool(nint deviceHandle, uint maxSets, ReadOnlyMemory<VulkanDescriptorPoolSize> poolSizes, uint flags = 0) {
        return descriptorApi.CreatePool(request: new VulkanDescriptorPoolCreateRequest(
            DeviceHandle: deviceHandle,
            Flags: flags,
            MaxSets: maxSets,
            PoolSizes: poolSizes
        ));
    }
    /// <summary>Creates a sampler from the given parameters.</summary>
    /// <param name="request">The sampler parameters to create from.</param>
    /// <returns>The native <c>VkSampler</c> handle.</returns>
    public nint CreateSampler(VulkanSamplerCreateRequest request) {
        return descriptorApi.CreateSampler(request: request);
    }
    /// <summary>Destroys a descriptor pool.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="poolHandle">The native <c>VkDescriptorPool</c> handle to destroy.</param>
    public void DestroyPool(nint deviceHandle, nint poolHandle) {
        descriptorApi.DestroyPool(
            deviceHandle: deviceHandle,
            poolHandle: poolHandle
        );
    }
    /// <summary>Destroys a sampler.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="samplerHandle">The native <c>VkSampler</c> handle to destroy.</param>
    public void DestroySampler(nint deviceHandle, nint samplerHandle) {
        descriptorApi.DestroySampler(
            deviceHandle: deviceHandle,
            samplerHandle: samplerHandle
        );
    }
    /// <summary>Writes a top-level acceleration structure (TLAS) descriptor into a set. Vulkan-only — there is
    /// no neutral-seam equivalent, because ray-query has no Direct3D 12 / DXIL counterpart.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="accelerationStructureHandle">The native <c>VkAccelerationStructureKHR</c> handle to bind.</param>
    public void WriteAccelerationStructure(nint deviceHandle, nint descriptorSetHandle, uint binding, nint accelerationStructureHandle) {
        descriptorApi.WriteAccelerationStructure(request: new VulkanDescriptorAccelerationStructureWriteRequest(
            AccelerationStructureHandle: accelerationStructureHandle,
            Binding: binding,
            DescriptorSetHandle: descriptorSetHandle,
            DeviceHandle: deviceHandle
        ));
    }
    /// <summary>Writes a combined image sampler descriptor into a set, in shader-read-only layout.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The array element within the binding.</param>
    /// <param name="imageViewHandle">The native <c>VkImageView</c> handle to sample.</param>
    /// <param name="samplerHandle">The native <c>VkSampler</c> handle to sample with.</param>
    public void WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) {
        descriptorApi.WriteImage(request: new VulkanDescriptorImageWriteRequest(
            ArrayElement: arrayElement,
            Binding: binding,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.CombinedImageSampler,
            DeviceHandle: deviceHandle,
            ImageLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            ImageViewHandle: imageViewHandle,
            SamplerHandle: samplerHandle
        ));
    }
    /// <summary>Writes a storage buffer descriptor into a set.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to bind.</param>
    /// <param name="bufferSize">The size, in bytes, of the bound buffer range.</param>
    public void WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) {
        descriptorApi.WriteBuffer(request: new VulkanDescriptorBufferWriteRequest(
            ArrayElement: 0,
            Binding: binding,
            BufferHandle: bufferHandle,
            BufferOffset: 0,
            BufferRange: bufferSize,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.StorageBuffer,
            DeviceHandle: deviceHandle
        ));
    }
    /// <summary>Writes a storage image descriptor into a set, in general layout (no sampler).</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The array element within the binding.</param>
    /// <param name="imageViewHandle">The native <c>VkImageView</c> handle to bind.</param>
    public void WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
        descriptorApi.WriteImage(request: new VulkanDescriptorImageWriteRequest(
            ArrayElement: arrayElement,
            Binding: binding,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.StorageImage,
            DeviceHandle: deviceHandle,
            ImageLayout: VulkanImageLayout.General,
            ImageViewHandle: imageViewHandle,
            SamplerHandle: 0
        ));
    }
}

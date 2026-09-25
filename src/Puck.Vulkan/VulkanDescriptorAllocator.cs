using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// An ergonomic wrapper over <see cref="IVulkanDescriptorApi"/> for the descriptor pool, set, sampler, and
/// write operations a renderer drives. It owns no policy: callers decide pool capacity, set count, and sampler
/// parameters, so it is reusable by any Vulkan consumer rather than tied to a particular pipeline's layout.
/// </summary>
public sealed class VulkanDescriptorAllocator(IVulkanDescriptorApi descriptorApi) {
    /// <summary>Allocates a single descriptor set of the given layout from a pool.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="poolHandle">The native <c>VkDescriptorPool</c> handle to allocate from.</param>
    /// <param name="descriptorSetLayoutHandle">The native <c>VkDescriptorSetLayout</c> handle of the set.</param>
    /// <returns>The native <c>VkDescriptorSet</c> handle.</returns>
    public nint AllocateSet(VulkanDeviceCommands device, nint poolHandle, nint descriptorSetLayoutHandle) {
        return descriptorApi.AllocateSet(request: new VulkanDescriptorSetAllocateRequest(
            DescriptorSetLayoutHandle: descriptorSetLayoutHandle,
            Device: device,
            PoolHandle: poolHandle
        ));
    }
    /// <summary>Creates a descriptor pool sized for the given sets and per-type descriptor capacity.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="maxSets">The maximum number of descriptor sets the pool can allocate.</param>
    /// <param name="poolSizes">The per-type descriptor capacity the pool reserves.</param>
    /// <param name="flags">A bitmask of <c>VkDescriptorPoolCreateFlagBits</c> for the pool.</param>
    /// <returns>The native <c>VkDescriptorPool</c> handle.</returns>
    public nint CreatePool(VulkanDeviceCommands device, uint maxSets, ReadOnlyMemory<VulkanDescriptorPoolSize> poolSizes, uint flags = 0) {
        return descriptorApi.CreatePool(request: new VulkanDescriptorPoolCreateRequest(
            Device: device,
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
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="poolHandle">The native <c>VkDescriptorPool</c> handle to destroy.</param>
    public void DestroyPool(VulkanDeviceCommands device, nint poolHandle) {
        descriptorApi.DestroyPool(
            device: device,
            poolHandle: poolHandle
        );
    }
    /// <summary>Destroys a sampler.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="samplerHandle">The native <c>VkSampler</c> handle to destroy.</param>
    public void DestroySampler(VulkanDeviceCommands device, nint samplerHandle) {
        descriptorApi.DestroySampler(
            device: device,
            samplerHandle: samplerHandle
        );
    }
    /// <summary>Writes a combined image sampler descriptor into a set, in shader-read-only layout.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The array element within the binding.</param>
    /// <param name="imageViewHandle">The native <c>VkImageView</c> handle to sample.</param>
    /// <param name="samplerHandle">The native <c>VkSampler</c> handle to sample with.</param>
    public void WriteCombinedImageSampler(VulkanDeviceCommands device, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) {
        descriptorApi.WriteImage(request: new VulkanDescriptorImageWriteRequest(
            ArrayElement: arrayElement,
            Binding: binding,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.CombinedImageSampler,
            Device: device,
            ImageLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            ImageViewHandle: imageViewHandle,
            SamplerHandle: samplerHandle
        ));
    }
    /// <summary>Writes a sampled image descriptor into a set, in shader-read-only layout, read through a separate
    /// sampler.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The array element within the binding.</param>
    /// <param name="imageViewHandle">The native <c>VkImageView</c> handle to sample.</param>
    public void WriteSampledImage(VulkanDeviceCommands device, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
        descriptorApi.WriteImage(request: new VulkanDescriptorImageWriteRequest(
            ArrayElement: arrayElement,
            Binding: binding,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.SampledImage,
            Device: device,
            ImageLayout: VulkanImageLayout.ShaderReadOnlyOptimal,
            ImageViewHandle: imageViewHandle,
            SamplerHandle: 0
        ));
    }
    /// <summary>Writes a sampler descriptor into a set.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The array element within the binding.</param>
    /// <param name="samplerHandle">The native <c>VkSampler</c> handle.</param>
    public void WriteSampler(VulkanDeviceCommands device, nint descriptorSetHandle, uint binding, uint arrayElement, nint samplerHandle) {
        descriptorApi.WriteImage(request: new VulkanDescriptorImageWriteRequest(
            ArrayElement: arrayElement,
            Binding: binding,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.Sampler,
            Device: device,
            ImageLayout: 0,
            ImageViewHandle: 0,
            SamplerHandle: samplerHandle
        ));
    }
    /// <summary>Writes a uniform buffer descriptor into a set, over the buffer from its first byte.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The array element within the binding.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to bind.</param>
    /// <param name="bufferSize">The size, in bytes, of the bound buffer range.</param>
    public void WriteUniformBuffer(VulkanDeviceCommands device, nint descriptorSetHandle, uint binding, uint arrayElement, nint bufferHandle, ulong bufferSize) {
        descriptorApi.WriteBuffer(request: new VulkanDescriptorBufferWriteRequest(
            ArrayElement: arrayElement,
            Binding: binding,
            BufferHandle: bufferHandle,
            BufferOffset: 0,
            BufferRange: bufferSize,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.UniformBuffer,
            Device: device
        ));
    }
    /// <summary>Writes a storage buffer descriptor into a set.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="bufferHandle">The native <c>VkBuffer</c> handle to bind.</param>
    /// <param name="bufferSize">The size, in bytes, of the bound buffer range.</param>
    public void WriteStorageBuffer(VulkanDeviceCommands device, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) {
        descriptorApi.WriteBuffer(request: new VulkanDescriptorBufferWriteRequest(
            ArrayElement: 0,
            Binding: binding,
            BufferHandle: bufferHandle,
            BufferOffset: 0,
            BufferRange: bufferSize,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.StorageBuffer,
            Device: device
        ));
    }
    /// <summary>Writes a storage image descriptor into a set, in general layout (no sampler).</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <param name="descriptorSetHandle">The native <c>VkDescriptorSet</c> handle to write into.</param>
    /// <param name="binding">The binding index within the set.</param>
    /// <param name="arrayElement">The array element within the binding.</param>
    /// <param name="imageViewHandle">The native <c>VkImageView</c> handle to bind.</param>
    public void WriteStorageImage(VulkanDeviceCommands device, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) {
        descriptorApi.WriteImage(request: new VulkanDescriptorImageWriteRequest(
            ArrayElement: arrayElement,
            Binding: binding,
            DescriptorSetHandle: descriptorSetHandle,
            DescriptorType: VulkanDescriptorType.StorageImage,
            Device: device,
            ImageLayout: VulkanImageLayout.General,
            ImageViewHandle: imageViewHandle,
            SamplerHandle: 0
        ));
    }
}

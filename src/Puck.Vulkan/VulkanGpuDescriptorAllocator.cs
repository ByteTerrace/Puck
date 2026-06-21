using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuDescriptorAllocator"/> by forwarding to <see cref="VulkanDescriptorAllocator"/>,
/// adapting the pool-size and sampler parameters to their Vulkan-specific forms.
/// </summary>
public sealed class VulkanGpuDescriptorAllocator(VulkanDescriptorAllocator allocator) : IGpuDescriptorAllocator {
    /// <inheritdoc/>
    public nint AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) =>
        allocator.AllocateSet(
            deviceHandle: deviceHandle,
            descriptorSetLayoutHandle: descriptorSetLayoutHandle,
            poolHandle: poolHandle
        );
    /// <inheritdoc/>
    public nint CreatePool(nint deviceHandle, uint maxSets, uint combinedImageSamplerCount, uint storageBufferCount, uint storageImageCount, uint accelerationStructureCount = 0) {
        var poolSizes = BuildPoolSizes(
            accelerationStructureCount: accelerationStructureCount,
            combinedImageSamplerCount: combinedImageSamplerCount,
            storageBufferCount: storageBufferCount,
            storageImageCount: storageImageCount
        );

        return allocator.CreatePool(
            deviceHandle: deviceHandle,
            maxSets: maxSets,
            poolSizes: poolSizes
        );
    }
    /// <inheritdoc/>
    public nint CreateSampler(nint deviceHandle, uint filter = GpuSamplerFilter.Linear) {
        var vulkanFilter = ((filter == GpuSamplerFilter.Nearest) ? VulkanFilter.Nearest : VulkanFilter.Linear);

        return allocator.CreateSampler(request: new VulkanSamplerCreateRequest(
            AddressModeU: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeV: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeW: VulkanSamplerAddressMode.ClampToEdge,
            AnisotropyEnable: 0,
            BorderColor: 0,
            CompareEnable: 0,
            CompareOp: 0,
            DeviceHandle: deviceHandle,
            Flags: 0,
            MagFilter: vulkanFilter,
            MaxAnisotropy: 1f,
            MaxLod: 0f,
            MinFilter: vulkanFilter,
            MinLod: 0f,
            MipLodBias: 0f,
            MipmapMode: VulkanSamplerMipmapMode.Nearest,
            UnnormalizedCoordinates: 0
        ));
    }
    /// <inheritdoc/>
    public void DestroyPool(nint deviceHandle, nint poolHandle) =>
        allocator.DestroyPool(
            deviceHandle: deviceHandle,
            poolHandle: poolHandle
        );
    /// <inheritdoc/>
    public void DestroySampler(nint deviceHandle, nint samplerHandle) =>
        allocator.DestroySampler(
            deviceHandle: deviceHandle,
            samplerHandle: samplerHandle
        );
    /// <inheritdoc/>
    public void WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) =>
        allocator.WriteCombinedImageSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle,
            imageViewHandle: imageViewHandle,
            samplerHandle: samplerHandle
        );
    /// <inheritdoc/>
    public void WriteStorageBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) =>
        allocator.WriteStorageBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );
    /// <inheritdoc/>
    public void WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) =>
        // A Vulkan storage buffer carries no descriptor-side stride (the shader's declared type defines the layout),
        // so a read-only structured buffer is the same descriptor write as any other storage buffer.
        allocator.WriteStorageBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );
    /// <inheritdoc/>
    public void WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) =>
        // A Vulkan storage buffer is read-write regardless; this is the same descriptor as the read-only write.
        allocator.WriteStorageBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );
    /// <inheritdoc/>
    public void WriteStorageImage(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) =>
        allocator.WriteStorageImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle,
            imageViewHandle: imageViewHandle
        );
    /// <inheritdoc/>
    public void WriteAccelerationStructure(nint deviceHandle, nint descriptorSetHandle, uint binding, nint accelerationStructureReference) =>
        // The reference is the Vulkan VkAccelerationStructureKHR handle directly.
        allocator.WriteAccelerationStructure(
            accelerationStructureHandle: accelerationStructureReference,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );

    private static ReadOnlyMemory<VulkanDescriptorPoolSize> BuildPoolSizes(uint combinedImageSamplerCount, uint storageBufferCount, uint storageImageCount, uint accelerationStructureCount) {
        var sizes = new List<VulkanDescriptorPoolSize>(capacity: 4);

        if (combinedImageSamplerCount > 0) {
            sizes.Add(item: new VulkanDescriptorPoolSize(DescriptorCount: combinedImageSamplerCount, DescriptorType: VulkanDescriptorType.CombinedImageSampler));
        }

        if (storageBufferCount > 0) {
            sizes.Add(item: new VulkanDescriptorPoolSize(DescriptorCount: storageBufferCount, DescriptorType: VulkanDescriptorType.StorageBuffer));
        }

        if (storageImageCount > 0) {
            sizes.Add(item: new VulkanDescriptorPoolSize(DescriptorCount: storageImageCount, DescriptorType: VulkanDescriptorType.StorageImage));
        }

        if (accelerationStructureCount > 0) {
            sizes.Add(item: new VulkanDescriptorPoolSize(DescriptorCount: accelerationStructureCount, DescriptorType: VulkanDescriptorType.AccelerationStructure));
        }

        return sizes.ToArray();
    }
}

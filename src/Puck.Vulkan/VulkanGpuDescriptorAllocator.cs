using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuDescriptorAllocator"/> by forwarding to <see cref="VulkanDescriptorAllocator"/>,
/// adapting the pool-size and sampler parameters to their Vulkan-specific forms.
/// </summary>
public sealed class VulkanGpuDescriptorAllocator(VulkanDescriptorAllocator allocator) : IGpuDescriptorAllocator {
    private static ReadOnlyMemory<VulkanDescriptorPoolSize> BuildPoolSizes(uint combinedImageSamplerCount, uint storageBufferCount, uint storageImageCount) {
        var sizes = new List<VulkanDescriptorPoolSize>(capacity: 3);

        if (combinedImageSamplerCount > 0) {
            sizes.Add(item: new VulkanDescriptorPoolSize(
                DescriptorCount: combinedImageSamplerCount,
                DescriptorType: VulkanDescriptorType.CombinedImageSampler
            ));
        }

        if (storageBufferCount > 0) {
            sizes.Add(item: new VulkanDescriptorPoolSize(
                DescriptorCount: storageBufferCount,
                DescriptorType: VulkanDescriptorType.StorageBuffer
            ));
        }

        if (storageImageCount > 0) {
            sizes.Add(item: new VulkanDescriptorPoolSize(
                DescriptorCount: storageImageCount,
                DescriptorType: VulkanDescriptorType.StorageImage
            ));
        }

        return sizes.ToArray();
    }

    /// <inheritdoc/>
    public nint AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) =>
        allocator.AllocateSet(
            descriptorSetLayoutHandle: descriptorSetLayoutHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            poolHandle: poolHandle
        );
    /// <inheritdoc/>
    public nint CreatePool(nint deviceHandle, in GpuDescriptorPoolSizes sizes) {
        var poolSizes = BuildPoolSizes(
            combinedImageSamplerCount: sizes.CombinedImageSamplerCount,
            storageBufferCount: sizes.StorageBufferCount,
            storageImageCount: sizes.StorageImageCount
        );

        return allocator.CreatePool(
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            maxSets: sizes.MaxSets,
            poolSizes: poolSizes
        );
    }
    /// <inheritdoc/>
    public nint CreateSampler(nint deviceHandle, GpuSamplerFilter filter = GpuSamplerFilter.Linear) {
        var vulkanFilter = ((filter == GpuSamplerFilter.Nearest)
            ? VulkanFilter.Nearest
            : VulkanFilter.Linear
        );

        return allocator.CreateSampler(request: new VulkanSamplerCreateRequest(
            AddressModeU: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeV: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeW: VulkanSamplerAddressMode.ClampToEdge,
            AnisotropyEnable: 0,
            BorderColor: 0,
            CompareEnable: 0,
            CompareOp: 0,
            Device: VulkanDeviceCommands.FromToken(token: deviceHandle),
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
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            poolHandle: poolHandle
        );
    /// <inheritdoc/>
    public void DestroySampler(nint deviceHandle, nint samplerHandle) =>
        allocator.DestroySampler(
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            samplerHandle: samplerHandle
        );
    /// <inheritdoc/>
    public void WriteCombinedImageSampler(nint deviceHandle, nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) =>
        allocator.WriteCombinedImageSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
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
            device: VulkanDeviceCommands.FromToken(token: deviceHandle)
        );
    /// <inheritdoc/>
    public void WriteStorageBufferReadOnly(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) =>
        // A Vulkan storage buffer carries no descriptor-side stride (the shader's declared type defines the layout),
        // so a read-only structured buffer is the same descriptor write as any other storage buffer.
        WriteStorageBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );
    /// <inheritdoc/>
    public void WriteStorageBufferReadWrite(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize) =>
        // A Vulkan storage buffer is read-write regardless; this is the same descriptor as the read-only write.
        WriteStorageBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            deviceHandle: deviceHandle
        );
    /// <inheritdoc/>
    public void WriteRawBuffer(nint deviceHandle, nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, bool writable) =>
        // A byte-address buffer compiles to a storage buffer of 32-bit words; the descriptor is the same either way.
        WriteStorageBuffer(
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
            device: VulkanDeviceCommands.FromToken(token: deviceHandle),
            imageViewHandle: imageViewHandle
        );
}

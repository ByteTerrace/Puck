using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// Implements <see cref="IGpuBindings"/> by forwarding to <see cref="VulkanDescriptorAllocator"/> against the current
/// logical device of its device context, adapting the pool-size and sampler parameters to their Vulkan forms.
/// </summary>
/// <param name="deviceContext">The device context whose current logical device every call reaches; a device recreated
/// after a loss is picked up by the next call.</param>
/// <param name="allocator">The Vulkan descriptor allocator.</param>
public sealed class VulkanGpuBindings(IVulkanDeviceContext deviceContext, VulkanDescriptorAllocator allocator) : IGpuBindings {
    private VulkanDeviceCommands Device => deviceContext.LogicalDevice.Commands;

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
    public nint AllocateSet(nint poolHandle, nint descriptorSetLayoutHandle) =>
        allocator.AllocateSet(
            descriptorSetLayoutHandle: descriptorSetLayoutHandle,
            device: Device,
            poolHandle: poolHandle
        );
    /// <inheritdoc/>
    public nint CreatePool(in GpuDescriptorPoolSizes sizes) {
        var poolSizes = BuildPoolSizes(
            combinedImageSamplerCount: sizes.CombinedImageSamplerCount,
            storageBufferCount: sizes.StorageBufferCount,
            storageImageCount: sizes.StorageImageCount
        );

        return allocator.CreatePool(
            device: Device,
            maxSets: sizes.MaxSets,
            poolSizes: poolSizes
        );
    }
    /// <inheritdoc/>
    public nint CreateSampler(GpuSamplerFilter filter = GpuSamplerFilter.Linear) {
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
            Device: Device,
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
    public void DestroyPool(nint poolHandle) =>
        allocator.DestroyPool(
            device: Device,
            poolHandle: poolHandle
        );
    /// <inheritdoc/>
    public void DestroySampler(nint samplerHandle) =>
        allocator.DestroySampler(
            device: Device,
            samplerHandle: samplerHandle
        );
    /// <inheritdoc/>
    /// <remarks>The access and element stride describe only a Direct3D 12 view; every Vulkan buffer write is the
    /// same storage-buffer descriptor.</remarks>
    public void WriteBuffer(nint descriptorSetHandle, uint binding, nint bufferHandle, ulong bufferSize, GpuBufferAccess access, uint elementStride) =>
        allocator.WriteStorageBuffer(
            binding: binding,
            bufferHandle: bufferHandle,
            bufferSize: bufferSize,
            descriptorSetHandle: descriptorSetHandle,
            device: Device
        );
    /// <inheritdoc/>
    public void WriteCombinedImageSampler(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle, nint samplerHandle) =>
        allocator.WriteCombinedImageSampler(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            device: Device,
            imageViewHandle: imageViewHandle,
            samplerHandle: samplerHandle
        );
    /// <inheritdoc/>
    public void WriteStorageImage(nint descriptorSetHandle, uint binding, uint arrayElement, nint imageViewHandle) =>
        allocator.WriteStorageImage(
            arrayElement: arrayElement,
            binding: binding,
            descriptorSetHandle: descriptorSetHandle,
            device: Device,
            imageViewHandle: imageViewHandle
        );
}

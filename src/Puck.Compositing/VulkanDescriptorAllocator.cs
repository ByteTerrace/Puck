using Puck.Vulkan;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Messages;

namespace Puck.Compositing;

public sealed class VulkanDescriptorAllocator(IVulkanDescriptorApi descriptorApi) {
    public nint AllocateSet(nint deviceHandle, nint poolHandle, nint descriptorSetLayoutHandle) {
        return descriptorApi.AllocateSet(request: new VulkanDescriptorSetAllocateRequest(
            DescriptorSetLayoutHandle: descriptorSetLayoutHandle,
            DeviceHandle: deviceHandle,
            PoolHandle: poolHandle
        ));
    }
    public nint CreatePool(nint deviceHandle, uint maxCombinedImageSamplers, uint maxStorageBuffers) {
        var sizes = new VulkanDescriptorPoolSize[(((maxCombinedImageSamplers > 0)
            ? 1
            : 0) + ((maxStorageBuffers > 0)
            ? 1
            : 0))];
        var sizeCount = 0;

        if (maxCombinedImageSamplers > 0) {
            sizes[sizeCount++] = new VulkanDescriptorPoolSize(
                DescriptorCount: maxCombinedImageSamplers,
                DescriptorType: VulkanDescriptorType.CombinedImageSampler
            );
        }

        if (maxStorageBuffers > 0) {
            sizes[sizeCount++] = new VulkanDescriptorPoolSize(
                DescriptorCount: maxStorageBuffers,
                DescriptorType: VulkanDescriptorType.StorageBuffer
            );
        }

        return descriptorApi.CreatePool(request: new VulkanDescriptorPoolCreateRequest(
            DeviceHandle: deviceHandle,
            Flags: 0,
            MaxSets: 1,
            PoolSizes: sizes
        ));
    }
    public nint CreateSampler(nint deviceHandle) {
        return descriptorApi.CreateSampler(request: new VulkanSamplerCreateRequest(
            AddressModeU: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeV: VulkanSamplerAddressMode.ClampToEdge,
            AddressModeW: VulkanSamplerAddressMode.ClampToEdge,
            AnisotropyEnable: 0,
            BorderColor: 0,
            CompareEnable: 0,
            CompareOp: 0,
            DeviceHandle: deviceHandle,
            Flags: 0,
            MagFilter: VulkanFilter.Linear,
            MaxAnisotropy: 0f,
            MaxLod: 0f,
            MinFilter: VulkanFilter.Linear,
            MinLod: 0f,
            MipLodBias: 0f,
            MipmapMode: VulkanSamplerMipmapMode.Linear,
            UnnormalizedCoordinates: 0
        ));
    }
    public void DestroyPool(nint deviceHandle, nint poolHandle) {
        descriptorApi.DestroyPool(
            deviceHandle: deviceHandle,
            poolHandle: poolHandle
        );
    }
    public void DestroySampler(nint deviceHandle, nint samplerHandle) {
        descriptorApi.DestroySampler(
            deviceHandle: deviceHandle,
            samplerHandle: samplerHandle
        );
    }
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
}
